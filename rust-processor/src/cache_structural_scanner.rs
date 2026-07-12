use crate::cache_corruption_detector::{
    CorruptionCandidate, CorruptionEvidence, CorruptionReport, CorruptionSettings, DetectionMethod,
    FileFingerprint, StructuralCoverage, StructuralEvidence, StructuralIssue,
    CORRUPTION_CONTRACT_VERSION,
};
use crate::{cache_utils, cancel, progress_utils};
use anyhow::{bail, Context, Result};
use chrono::{DateTime, SecondsFormat, Utc};
use jwalk::WalkDir;
use serde::Serialize;
use std::collections::BTreeMap;
use std::fs::{File, Metadata, OpenOptions};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Component, Path, PathBuf};
use std::time::{Duration, SystemTime};

pub const MAX_PREFIX_BYTES: u64 = u16::MAX as u64;
pub const MIN_STABLE_AGE_SECONDS: u64 = 600;
const KEY_MARKER: &[u8] = b"\nKEY: ";

#[allow(dead_code)]
#[repr(C)]
struct NginxCacheHeaderV5Layout {
    version: usize,
    valid_sec: libc::time_t,
    updating_sec: libc::time_t,
    error_sec: libc::time_t,
    last_modified: libc::time_t,
    date: libc::time_t,
    crc32: u32,
    valid_msec: u16,
    header_start: u16,
    body_start: u16,
    etag_len: u8,
    etag: [u8; 128],
    vary_len: u8,
    vary: [u8; 128],
    variant: [u8; 16],
}

#[derive(Clone, Copy)]
struct Layout {
    size: usize,
    version: usize,
    crc32: usize,
    header_start: usize,
    body_start: usize,
    etag_len: usize,
    vary_len: usize,
    variant: usize,
}

impl Layout {
    fn native() -> Option<Self> {
        #[cfg(unix)]
        {
            Some(Self {
                size: std::mem::size_of::<NginxCacheHeaderV5Layout>(),
                version: std::mem::offset_of!(NginxCacheHeaderV5Layout, version),
                crc32: std::mem::offset_of!(NginxCacheHeaderV5Layout, crc32),
                header_start: std::mem::offset_of!(NginxCacheHeaderV5Layout, header_start),
                body_start: std::mem::offset_of!(NginxCacheHeaderV5Layout, body_start),
                etag_len: std::mem::offset_of!(NginxCacheHeaderV5Layout, etag_len),
                vary_len: std::mem::offset_of!(NginxCacheHeaderV5Layout, vary_len),
                variant: std::mem::offset_of!(NginxCacheHeaderV5Layout, variant),
            })
        }
        #[cfg(not(unix))]
        {
            None
        }
    }

    #[cfg(test)]
    fn linux_x86_64() -> Self {
        Self {
            size: 336,
            version: 0,
            crc32: 48,
            header_start: 54,
            body_start: 56,
            etag_len: 58,
            vary_len: 187,
            variant: 316,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum SkipReason {
    UnsupportedPlatform,
    UnsupportedCacheVersion,
    UnsupportedLayout,
    UnsupportedStatus,
    UnsupportedRangeShape,
    NoAuthoritativeLength,
    AmbiguousHttpHeaders,
    Recent,
    Changed,
    Replaced,
    Symlink,
    SpecialFile,
    ForeignPath,
    IoError,
    Cancelled,
}

impl SkipReason {
    fn as_str(self) -> &'static str {
        match self {
            Self::UnsupportedPlatform => "unsupported_platform",
            Self::UnsupportedCacheVersion => "unsupported_cache_version",
            Self::UnsupportedLayout => "unsupported_layout",
            Self::UnsupportedStatus => "unsupported_status",
            Self::UnsupportedRangeShape => "unsupported_range_shape",
            Self::NoAuthoritativeLength => "no_authoritative_length",
            Self::AmbiguousHttpHeaders => "ambiguous_http_headers",
            Self::Recent => "recent",
            Self::Changed => "changed",
            Self::Replaced => "replaced",
            Self::Symlink => "symlink",
            Self::SpecialFile => "special_file",
            Self::ForeignPath => "foreign_path",
            Self::IoError => "io_error",
            Self::Cancelled => "cancelled",
        }
    }
}

#[derive(Debug, Clone)]
struct ParsedStructural {
    issues: Vec<StructuralIssue>,
    cache_key: Vec<u8>,
    http_status: Option<u16>,
    header_start: Option<u16>,
    body_start: Option<u16>,
    actual_payload_length: Option<u64>,
    expected_payload_length: Option<u64>,
    content_length: Option<u64>,
    content_range: Option<String>,
}

#[derive(Debug, Clone)]
enum ParseOutcome {
    Consistent,
    Proven(ParsedStructural),
    Skip(SkipReason),
}

#[derive(Debug, Clone)]
pub struct Inspection {
    pub outcome: InspectionOutcome,
    pub bytes_read: u64,
    pub sparse: bool,
}

#[derive(Debug, Clone)]
pub enum InspectionOutcome {
    Consistent,
    Proven(StructuralEvidence),
    Skip(&'static str),
}

#[derive(Debug)]
pub struct StructuralScanResult {
    pub report: CorruptionReport,
    pub cancelled: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ScanProgress {
    status: String,
    stage_key: String,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
    timestamp: String,
}

fn read_usize(bytes: &[u8], offset: usize) -> Option<usize> {
    let raw: [u8; std::mem::size_of::<usize>()] = bytes
        .get(offset..offset.checked_add(std::mem::size_of::<usize>())?)?
        .try_into()
        .ok()?;
    Some(usize::from_ne_bytes(raw))
}

fn read_u32(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_ne_bytes(
        bytes.get(offset..offset.checked_add(4)?)?.try_into().ok()?,
    ))
}

fn read_u16(bytes: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_ne_bytes(
        bytes.get(offset..offset.checked_add(2)?)?.try_into().ok()?,
    ))
}

fn hex_bytes(bytes: &[u8]) -> String {
    use std::fmt::Write;
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(result, "{byte:02x}");
    }
    result
}

#[derive(Debug)]
struct HttpMetadata {
    status: u16,
    content_length: Option<u64>,
    content_range: Option<(u64, String)>,
    chunked: bool,
    multipart: bool,
}

fn parse_decimal(value: &[u8]) -> Option<u64> {
    if value.is_empty() || !value.iter().all(u8::is_ascii_digit) {
        return None;
    }
    std::str::from_utf8(value).ok()?.parse().ok()
}

fn trim_ascii(mut value: &[u8]) -> &[u8] {
    while value.first().is_some_and(u8::is_ascii_whitespace) {
        value = &value[1..];
    }
    while value.last().is_some_and(u8::is_ascii_whitespace) {
        value = &value[..value.len() - 1];
    }
    value
}

fn parse_content_range(value: &[u8]) -> Option<(u64, String)> {
    let text = std::str::from_utf8(trim_ascii(value)).ok()?;
    let rest = text.strip_prefix("bytes ")?;
    if rest.contains(',') {
        return None;
    }
    let (range, total) = rest.split_once('/')?;
    let (start, end) = range.split_once('-')?;
    let start = start.parse::<u64>().ok()?;
    let end = end.parse::<u64>().ok()?;
    if start > end {
        return None;
    }
    if total != "*" {
        let total = total.parse::<u64>().ok()?;
        if end >= total {
            return None;
        }
    }
    let span = end.checked_sub(start)?.checked_add(1)?;
    Some((span, text.to_string()))
}

fn parse_http_headers(bytes: &[u8]) -> std::result::Result<HttpMetadata, SkipReason> {
    if !bytes.is_ascii() {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    let normalized = bytes
        .split(|byte| *byte == b'\n')
        .map(|line| line.strip_suffix(b"\r").unwrap_or(line))
        .collect::<Vec<_>>();
    let trailing_empty = normalized
        .iter()
        .rev()
        .take_while(|line| line.is_empty())
        .count();
    if trailing_empty < 2 {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    let mut lines = normalized.as_slice();
    while lines.last().is_some_and(|line| line.is_empty()) {
        lines = &lines[..lines.len() - 1];
    }
    let Some(status_line) = lines.first().copied() else {
        return Err(SkipReason::AmbiguousHttpHeaders);
    };
    if lines.iter().any(|line| line.is_empty()) {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    let status_text =
        std::str::from_utf8(status_line).map_err(|_| SkipReason::AmbiguousHttpHeaders)?;
    let mut status_parts = status_text.split_ascii_whitespace();
    if !matches!(status_parts.next(), Some("HTTP/1.0" | "HTTP/1.1")) {
        return Err(SkipReason::UnsupportedStatus);
    }
    let status = status_parts
        .next()
        .and_then(|value| value.parse::<u16>().ok())
        .ok_or(SkipReason::AmbiguousHttpHeaders)?;
    if !matches!(status, 200 | 206) {
        return Err(SkipReason::UnsupportedStatus);
    }

    let mut content_length = None;
    let mut content_range = None;
    let mut transfer_encoding = None::<Vec<u8>>;
    let mut multipart = false;
    for line in &lines[1..] {
        if line.is_empty() {
            continue;
        }
        if line
            .first()
            .is_some_and(|byte| matches!(byte, b' ' | b'\t'))
        {
            return Err(SkipReason::AmbiguousHttpHeaders);
        }
        let Some(separator) = line.iter().position(|byte| *byte == b':') else {
            return Err(SkipReason::AmbiguousHttpHeaders);
        };
        let (name, with_separator) = line.split_at(separator);
        let raw_value = &with_separator[1..];
        if name.is_empty()
            || !name
                .iter()
                .all(|byte| byte.is_ascii_alphanumeric() || *byte == b'-')
        {
            return Err(SkipReason::AmbiguousHttpHeaders);
        }
        let name = name.iter().map(u8::to_ascii_lowercase).collect::<Vec<_>>();
        let value = trim_ascii(raw_value);
        match name.as_slice() {
            b"content-length" => {
                if content_length.is_some() {
                    return Err(SkipReason::AmbiguousHttpHeaders);
                }
                content_length =
                    Some(parse_decimal(value).ok_or(SkipReason::AmbiguousHttpHeaders)?);
            }
            b"content-range" => {
                if content_range.is_some() {
                    return Err(SkipReason::AmbiguousHttpHeaders);
                }
                content_range =
                    Some(parse_content_range(value).ok_or(SkipReason::AmbiguousHttpHeaders)?);
            }
            b"transfer-encoding" => {
                if transfer_encoding.is_some() {
                    return Err(SkipReason::AmbiguousHttpHeaders);
                }
                transfer_encoding = Some(value.iter().map(u8::to_ascii_lowercase).collect());
            }
            b"content-type" => {
                let lower = value.iter().map(u8::to_ascii_lowercase).collect::<Vec<_>>();
                multipart = lower.starts_with(b"multipart/byteranges");
            }
            _ => {}
        }
    }
    let chunked = transfer_encoding.as_deref().is_some_and(|value| {
        value
            .split(|byte| *byte == b',')
            .last()
            .is_some_and(|coding| trim_ascii(coding) == b"chunked")
    });
    if transfer_encoding.is_some() && !chunked {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    Ok(HttpMetadata {
        status,
        content_length,
        content_range,
        chunked,
        multipart,
    })
}

fn parse_prefix(prefix: &[u8], file_len: u64, path_digest: u128, layout: Layout) -> ParseOutcome {
    if file_len == 0 {
        return ParseOutcome::Proven(ParsedStructural {
            issues: vec![StructuralIssue::EmptyCacheFile],
            cache_key: Vec::new(),
            http_status: None,
            header_start: None,
            body_start: None,
            actual_payload_length: None,
            expected_payload_length: None,
            content_length: None,
            content_range: None,
        });
    }
    let Some(version) = read_usize(prefix, layout.version) else {
        return ParseOutcome::Skip(SkipReason::UnsupportedLayout);
    };
    if version != 5 {
        return ParseOutcome::Skip(SkipReason::UnsupportedCacheVersion);
    }
    let fixed_end = match layout.size.checked_add(KEY_MARKER.len()) {
        Some(value) => value,
        None => return ParseOutcome::Skip(SkipReason::UnsupportedLayout),
    };
    if file_len < fixed_end as u64 || prefix.len() < fixed_end {
        return ParseOutcome::Proven(ParsedStructural {
            issues: vec![StructuralIssue::TruncatedCacheHeader],
            cache_key: Vec::new(),
            http_status: None,
            header_start: None,
            body_start: None,
            actual_payload_length: None,
            expected_payload_length: None,
            content_length: None,
            content_range: None,
        });
    }
    let Some(header_start) = read_u16(prefix, layout.header_start) else {
        return ParseOutcome::Skip(SkipReason::UnsupportedLayout);
    };
    let Some(body_start) = read_u16(prefix, layout.body_start) else {
        return ParseOutcome::Skip(SkipReason::UnsupportedLayout);
    };
    if prefix.get(layout.size..fixed_end) != Some(KEY_MARKER) {
        return proven_envelope(
            StructuralIssue::MalformedCacheHeader,
            header_start,
            body_start,
        );
    }
    if header_start as usize <= fixed_end {
        return proven_envelope(
            StructuralIssue::MalformedCacheHeader,
            header_start,
            body_start,
        );
    }
    if header_start > body_start {
        return proven_envelope(
            StructuralIssue::InvalidPayloadOffset,
            header_start,
            body_start,
        );
    }
    if u64::from(header_start) > file_len {
        return proven_envelope(
            StructuralIssue::TruncatedCacheHeader,
            header_start,
            body_start,
        );
    }
    if u64::from(body_start) > file_len {
        return proven_envelope(
            StructuralIssue::TruncatedBeforePayload,
            header_start,
            body_start,
        );
    }
    let header_start_usize = usize::from(header_start);
    let body_start_usize = usize::from(body_start);
    if prefix.len() < body_start_usize || prefix.get(header_start_usize - 1) != Some(&b'\n') {
        return proven_envelope(
            StructuralIssue::MalformedCacheHeader,
            header_start,
            body_start,
        );
    }
    let cache_key = prefix[fixed_end..header_start_usize - 1].to_vec();
    let stored_crc = read_u32(prefix, layout.crc32).unwrap_or_default();
    let etag_len = prefix.get(layout.etag_len).copied().unwrap_or(u8::MAX);
    let vary_len = prefix.get(layout.vary_len).copied().unwrap_or(u8::MAX);
    if cache_key.is_empty()
        || stored_crc != crc32fast::hash(&cache_key)
        || etag_len > 128
        || vary_len > 128
    {
        return proven_with_key(
            StructuralIssue::MalformedCacheHeader,
            cache_key,
            header_start,
            body_start,
        );
    }

    let main_digest = u128::from_be_bytes(md5::compute(&cache_key).0);
    let variant_digest = prefix
        .get(layout.variant..layout.variant + 16)
        .and_then(|bytes| <[u8; 16]>::try_from(bytes).ok())
        .map(u128::from_be_bytes);
    if path_digest != main_digest && !(vary_len > 0 && variant_digest == Some(path_digest)) {
        return proven_with_key(
            StructuralIssue::CacheKeyPathMismatch,
            cache_key,
            header_start,
            body_start,
        );
    }

    let actual = file_len - u64::from(body_start);
    let http = match parse_http_headers(&prefix[header_start_usize..body_start_usize]) {
        Ok(value) => value,
        Err(reason) => return ParseOutcome::Skip(reason),
    };
    let mut issues = Vec::new();
    let expected = match http.status {
        200 if http.chunked => return ParseOutcome::Skip(SkipReason::NoAuthoritativeLength),
        200 => {
            let Some(length) = http.content_length else {
                return ParseOutcome::Skip(SkipReason::NoAuthoritativeLength);
            };
            if actual != length {
                issues.push(StructuralIssue::PayloadLengthMismatch);
            }
            Some(length)
        }
        206 => {
            if http.multipart {
                return ParseOutcome::Skip(SkipReason::UnsupportedRangeShape);
            }
            let Some((span, _)) = http.content_range.as_ref() else {
                return ParseOutcome::Skip(SkipReason::UnsupportedRangeShape);
            };
            if !http.chunked && http.content_length.is_some_and(|length| length != *span) {
                issues.push(StructuralIssue::ContentLengthRangeConflict);
            }
            if actual != *span {
                issues.push(StructuralIssue::ContentRangeLengthMismatch);
            }
            Some(*span)
        }
        _ => return ParseOutcome::Skip(SkipReason::UnsupportedStatus),
    };
    if issues.is_empty() {
        ParseOutcome::Consistent
    } else {
        ParseOutcome::Proven(ParsedStructural {
            issues,
            cache_key,
            http_status: Some(http.status),
            header_start: Some(header_start),
            body_start: Some(body_start),
            actual_payload_length: Some(actual),
            expected_payload_length: expected,
            content_length: http.content_length,
            content_range: http.content_range.map(|(_, raw)| raw),
        })
    }
}

fn proven_envelope(issue: StructuralIssue, header_start: u16, body_start: u16) -> ParseOutcome {
    proven_with_key(issue, Vec::new(), header_start, body_start)
}

fn proven_with_key(
    issue: StructuralIssue,
    cache_key: Vec<u8>,
    header_start: u16,
    body_start: u16,
) -> ParseOutcome {
    ParseOutcome::Proven(ParsedStructural {
        issues: vec![issue],
        cache_key,
        http_status: None,
        header_start: Some(header_start),
        body_start: Some(body_start),
        actual_payload_length: None,
        expected_payload_length: None,
        content_length: None,
        content_range: None,
    })
}

#[cfg(unix)]
fn fingerprint(metadata: &Metadata) -> FileFingerprint {
    use std::os::unix::fs::MetadataExt;
    FileFingerprint {
        dev: metadata.dev(),
        ino: metadata.ino(),
        len: metadata.len(),
        mtime_ns: metadata
            .mtime()
            .saturating_mul(1_000_000_000)
            .saturating_add(metadata.mtime_nsec()),
        ctime_ns: metadata
            .ctime()
            .saturating_mul(1_000_000_000)
            .saturating_add(metadata.ctime_nsec()),
    }
}

pub fn path_fingerprint_matches(path: &Path, expected: &FileFingerprint) -> Result<bool> {
    let metadata = match std::fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(error) => {
            return Err(error).with_context(|| format!("failed to restat {}", path.display()))
        }
    };
    Ok(metadata.file_type().is_file()
        && !metadata.file_type().is_symlink()
        && fingerprint(&metadata) == *expected)
}

#[cfg(not(unix))]
fn fingerprint(metadata: &Metadata) -> FileFingerprint {
    let modified = metadata
        .modified()
        .ok()
        .and_then(|value| value.duration_since(SystemTime::UNIX_EPOCH).ok())
        .map(|value| i64::try_from(value.as_nanos()).unwrap_or(i64::MAX))
        .unwrap_or_default();
    FileFingerprint {
        dev: 0,
        ino: 0,
        len: metadata.len(),
        mtime_ns: modified,
        ctime_ns: modified,
    }
}

#[cfg(unix)]
fn sparse(metadata: &Metadata) -> bool {
    use std::os::unix::fs::MetadataExt;
    metadata.blocks().saturating_mul(512) < metadata.len()
}

#[cfg(not(unix))]
fn sparse(_metadata: &Metadata) -> bool {
    false
}

fn has_symlink_component(root: &Path, path: &Path) -> Result<bool> {
    let relative = path
        .strip_prefix(root)
        .with_context(|| format!("path is outside cache root: {}", path.display()))?;
    let mut current = root.to_path_buf();
    for component in relative.components() {
        let Component::Normal(name) = component else {
            return Ok(true);
        };
        current.push(name);
        match std::fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() => return Ok(true),
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
            Err(error) => {
                return Err(error).with_context(|| format!("failed to lstat {}", current.display()))
            }
        }
    }
    Ok(false)
}

fn open_nofollow(path: &Path) -> Result<File> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NOFOLLOW | libc::O_CLOEXEC);
    }
    options
        .open(path)
        .with_context(|| format!("failed to open cache file {}", path.display()))
}

fn stable_age(metadata: &Metadata, now: SystemTime) -> bool {
    metadata
        .modified()
        .ok()
        .and_then(|modified| now.duration_since(modified).ok())
        .is_some_and(|age| age >= Duration::from_secs(MIN_STABLE_AGE_SECONDS))
}

fn read_prefix(file: &mut File, file_len: u64, layout: Layout) -> Result<(Vec<u8>, u64)> {
    // Read only far enough to discover the serialized body offset first. A corrupt body_start
    // must not make the scanner eagerly consume the rest of the fixed header (or payload bytes).
    let offset_probe_end = layout
        .body_start
        .checked_add(std::mem::size_of::<u16>())
        .context("nginx body_start offset overflowed")?;
    let first_len = usize::try_from(file_len.min(offset_probe_end as u64))?;
    let mut prefix = vec![0; first_len];
    file.read_exact(&mut prefix)
        .with_context(|| format!("failed to read {first_len}-byte nginx cache prefix"))?;
    let mut bytes_read = first_len as u64;
    if file_len >= layout.size as u64 {
        if let Some(body_start) = read_u16(&prefix, layout.body_start) {
            let target = u64::from(body_start).min(file_len).min(MAX_PREFIX_BYTES);
            if target > bytes_read {
                let target = usize::try_from(target)?;
                prefix.resize(target, 0);
                file.seek(SeekFrom::Start(bytes_read))?;
                file.read_exact(&mut prefix[bytes_read as usize..])
                    .context("failed to read through nginx payload offset")?;
                bytes_read = target as u64;
            }
        }
    }
    Ok((prefix, bytes_read))
}

// Retained for the structural-scanner test harness (see cache_corruption.rs); the scan hot
// path now calls inspect_path_with_layout directly with a pre-computed digest.
#[allow(dead_code)]
pub fn inspect_path(
    root: &Path,
    path: &Path,
    now: SystemTime,
    detected_at: DateTime<Utc>,
) -> Result<Inspection> {
    let Some(path_digest) = cache_utils::strict_cache_file_digest(root, path) else {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::ForeignPath.as_str()),
            bytes_read: 0,
            sparse: false,
        });
    };
    let Some(layout) = Layout::native() else {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::UnsupportedPlatform.as_str()),
            bytes_read: 0,
            sparse: false,
        });
    };
    inspect_path_with_layout(root, path, path_digest, now, detected_at, layout)
}

fn inspect_path_with_layout(
    root: &Path,
    path: &Path,
    path_digest: u128,
    now: SystemTime,
    detected_at: DateTime<Utc>,
    layout: Layout,
) -> Result<Inspection> {
    inspect_path_with_layout_and_hook(root, path, path_digest, now, detected_at, layout, || {})
}

fn inspect_path_with_layout_and_hook<F: FnOnce()>(
    root: &Path,
    path: &Path,
    path_digest: u128,
    now: SystemTime,
    detected_at: DateTime<Utc>,
    layout: Layout,
    after_read: F,
) -> Result<Inspection> {
    if has_symlink_component(root, path)? {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Symlink.as_str()),
            bytes_read: 0,
            sparse: false,
        });
    }
    let path_before = match std::fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(Inspection {
                outcome: InspectionOutcome::Skip(SkipReason::Changed.as_str()),
                bytes_read: 0,
                sparse: false,
            })
        }
        Err(error) => {
            return Err(error).with_context(|| format!("failed to inspect {}", path.display()))
        }
    };
    if !path_before.file_type().is_file() {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::SpecialFile.as_str()),
            bytes_read: 0,
            sparse: false,
        });
    }
    if !stable_age(&path_before, now) {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Recent.as_str()),
            bytes_read: 0,
            sparse: sparse(&path_before),
        });
    }
    cache_utils::safe_path_under_root(root, path)
        .with_context(|| format!("unsafe structural cache path {}", path.display()))?;
    let mut file = open_nofollow(path)?;
    let before = file.metadata()?;
    if fingerprint(&before) != fingerprint(&path_before) {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Replaced.as_str()),
            bytes_read: 0,
            sparse: sparse(&before),
        });
    }
    let (prefix, bytes_read) = match read_prefix(&mut file, before.len(), layout) {
        Ok(value) => value,
        Err(error) => {
            let after = file.metadata()?;
            if fingerprint(&before) != fingerprint(&after) {
                return Ok(Inspection {
                    outcome: InspectionOutcome::Skip(SkipReason::Changed.as_str()),
                    bytes_read: 0,
                    sparse: sparse(&after),
                });
            }
            return Err(error)
                .with_context(|| format!("failed to inspect prefix for {}", path.display()));
        }
    };
    after_read();
    if cancel::is_cancelled() {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Cancelled.as_str()),
            bytes_read,
            sparse: sparse(&before),
        });
    }
    let after = file.metadata()?;
    let path_after = std::fs::symlink_metadata(path)?;
    if fingerprint(&before) != fingerprint(&after)
        || fingerprint(&after) != fingerprint(&path_after)
        || path_after.file_type().is_symlink()
    {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Changed.as_str()),
            bytes_read,
            sparse: sparse(&after),
        });
    }
    let outcome = match parse_prefix(&prefix, before.len(), path_digest, layout) {
        ParseOutcome::Consistent => InspectionOutcome::Consistent,
        ParseOutcome::Skip(reason) => InspectionOutcome::Skip(reason.as_str()),
        ParseOutcome::Proven(parsed) => {
            let key_md5 = format!(
                "{:032x}",
                u128::from_be_bytes(md5::compute(&parsed.cache_key).0)
            );
            InspectionOutcome::Proven(StructuralEvidence {
                issues: parsed.issues,
                cache_key_encoding: "hex".to_string(),
                cache_key: hex_bytes(&parsed.cache_key),
                cache_key_md5: key_md5,
                cache_version: 5,
                http_status: parsed.http_status,
                header_start: parsed.header_start,
                body_start: parsed.body_start,
                file_length: before.len(),
                actual_payload_length: parsed.actual_payload_length,
                expected_payload_length: parsed.expected_payload_length,
                content_length: parsed.content_length,
                content_range: parsed.content_range,
                fingerprint: fingerprint(&before),
                detected_at_utc: detected_at.to_rfc3339_opts(SecondsFormat::AutoSi, true),
            })
        }
    };
    Ok(Inspection {
        outcome,
        bytes_read,
        sparse: sparse(&before),
    })
}

fn service_from_key_hex(evidence: &StructuralEvidence) -> String {
    let bytes = evidence
        .cache_key
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            let text = std::str::from_utf8(pair).ok()?;
            u8::from_str_radix(text, 16).ok()
        })
        .collect::<Option<Vec<_>>>();
    let Some(bytes) = bytes else {
        return "unknown".to_string();
    };
    let prefix = bytes.split(|byte| *byte == b'/').next().unwrap_or_default();
    if prefix.is_empty()
        || !prefix
            .iter()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
    {
        return "unknown".to_string();
    }
    String::from_utf8(prefix.to_ascii_lowercase()).unwrap_or_else(|_| "unknown".to_string())
}

fn structural_candidate(path: &Path, evidence: StructuralEvidence) -> CorruptionCandidate {
    let service = service_from_key_hex(&evidence);
    let identity = format!(
        "{}|{}|{:?}|{}|{}|{}",
        path.display(),
        evidence.cache_key_md5,
        evidence.issues,
        evidence.fingerprint.dev,
        evidence.fingerprint.ino,
        evidence.fingerprint.ctime_ns
    );
    CorruptionCandidate {
        candidate_id: cache_utils::calculate_md5(&identity),
        service,
        exact_paths: vec![path.display().to_string()],
        evidence: CorruptionEvidence::Structural {
            structural: evidence,
        },
    }
}

fn update_progress(
    progress_path: Option<&Path>,
    status: &str,
    pass: &str,
    processed: usize,
    total: usize,
    candidates: usize,
    coverage: &StructuralCoverage,
) -> Result<()> {
    let Some(path) = progress_path else {
        return Ok(());
    };
    let counting = pass == "counting";
    // The counting pass does not yet know the total, so a percentage would be a misleading
    // frozen 0%. Emit a distinct, count-driven stage instead and leave the percent
    // indeterminate (zeroed) so the frontend can render the running eligible-file count.
    let stage_key = if counting {
        "signalr.corruptionDetect.enumerating"
    } else {
        "signalr.corruptionDetect.scanningHeaders"
    };
    let percent = if counting {
        0.0
    } else if total == 0 {
        100.0
    } else {
        (processed as f64 / total as f64 * 100.0).min(99.9)
    };
    let mut context = serde_json::json!({
        "detectionMethod": "structural",
        "pass": pass,
        "filesProcessed": processed,
        "totalFiles": total,
        "totalCorrupted": candidates,
        "coverage": coverage,
    });
    if counting {
        // `processed` carries the running eligible-file count during the counting pass.
        context["count"] = serde_json::json!(processed);
    }
    progress_utils::write_progress_json(
        path,
        &ScanProgress {
            status: status.to_string(),
            stage_key: stage_key.to_string(),
            context,
            percent_complete: percent,
            files_processed: processed,
            total_files: total,
            timestamp: progress_utils::current_timestamp(),
        },
    )
}

fn walk(root: &Path) -> impl Iterator<Item = jwalk::Result<jwalk::DirEntry<((), ())>>> {
    let parallelism = cache_utils::detect_filesystem_type(root).recommended_parallelism();
    WalkDir::new(root)
        .follow_links(false)
        .parallelism(jwalk::Parallelism::RayonNewPool(parallelism))
        .into_iter()
}

pub fn scan(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
) -> Result<StructuralScanResult> {
    scan_with_cancellation(root, scan_started_utc, progress_path, cancel::is_cancelled)
}

fn scan_with_cancellation<F: FnMut() -> bool>(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
    mut cancellation_requested: F,
) -> Result<StructuralScanResult> {
    let root_link_metadata = std::fs::symlink_metadata(root)
        .with_context(|| format!("failed to inspect structural cache root {}", root.display()))?;
    if root_link_metadata.file_type().is_symlink() || !root_link_metadata.is_dir() {
        bail!(
            "structural cache root must be a non-symlink directory: {}",
            root.display()
        );
    }
    let canonical_root = std::fs::canonicalize(root).with_context(|| {
        format!(
            "failed to canonicalize structural cache root {}",
            root.display()
        )
    })?;
    if canonical_root != root.canonicalize()? {
        bail!("structural cache root changed during setup");
    }

    let scan_started_at = std::time::Instant::now();
    eprintln!(
        "[structural] scan starting under {}",
        canonical_root.display()
    );

    let mut total = 0usize;
    let mut cancelled = false;
    let mut coverage = StructuralCoverage::default();
    // Emit the enumerating stage immediately so the UI leaves any prior scanningHeaders/0%
    // state instead of sitting on a frozen 0% for the whole (potentially multi-minute) count.
    update_progress(progress_path, "scanning", "counting", 0, 0, 0, &coverage)?;
    eprintln!("[structural] enumerating cache files (this pass computes the total)...");
    for entry in walk(root) {
        if cancellation_requested() {
            cancelled = true;
            break;
        }
        let entry = match entry {
            Ok(entry) => entry,
            Err(error) => {
                if error.depth() == 0 {
                    return Err(error).context("failed to enumerate structural cache root");
                }
                coverage.io_errors = coverage.io_errors.saturating_add(1);
                *coverage
                    .skipped_by_reason
                    .entry("count_enumeration_io_error".to_string())
                    .or_default() += 1;
                continue;
            }
        };
        if cache_utils::strict_cache_file_digest(root, &entry.path()).is_some() {
            total = total.saturating_add(1);
            if total.is_multiple_of(2_000) {
                update_progress(
                    progress_path,
                    "scanning",
                    "counting",
                    total,
                    0,
                    0,
                    &coverage,
                )?;
            }
            if total.is_multiple_of(50_000) {
                eprintln!("[structural] enumerated {total} eligible cache files so far...");
            }
        }
    }
    eprintln!(
        "[structural] enumeration complete: {total} eligible cache files in {:.1}s (cancelled={cancelled})",
        scan_started_at.elapsed().as_secs_f64()
    );

    let mut candidates = Vec::new();
    // Final counting emit: report the full eligible-file count now that enumeration finished.
    update_progress(
        progress_path,
        "scanning",
        "counting",
        total,
        total,
        0,
        &coverage,
    )?;
    if !cancelled {
        let now = SystemTime::from(scan_started_utc);
        // Resolve the native cache-header layout once; it is compile-time constant.
        let layout = Layout::native();
        eprintln!("[structural] inspecting {total} cache files...");
        for entry in walk(root) {
            if cancellation_requested() {
                cancelled = true;
                break;
            }
            let entry = match entry {
                Ok(entry) => entry,
                Err(error) => {
                    if error.depth() == 0 {
                        return Err(error).context("failed to enumerate structural cache root");
                    }
                    coverage.io_errors = coverage.io_errors.saturating_add(1);
                    *coverage
                        .skipped_by_reason
                        .entry(SkipReason::IoError.as_str().to_string())
                        .or_default() += 1;
                    continue;
                }
            };
            let path = entry.path();
            // Compute the strict cache digest ONCE here and thread it into the inspector so it
            // is not recomputed inside inspect_path_with_layout_and_hook.
            let Some(path_digest) = cache_utils::strict_cache_file_digest(root, &path) else {
                continue;
            };
            coverage.files_seen += 1;
            let inspection = match layout {
                Some(layout) => {
                    inspect_path_with_layout(root, &path, path_digest, now, scan_started_utc, layout)
                }
                None => Ok(Inspection {
                    outcome: InspectionOutcome::Skip(SkipReason::UnsupportedPlatform.as_str()),
                    bytes_read: 0,
                    sparse: false,
                }),
            };
            match inspection {
                Ok(inspection) => {
                    coverage.bytes_read = coverage.bytes_read.saturating_add(inspection.bytes_read);
                    coverage.sparse_files += usize::from(inspection.sparse);
                    match inspection.outcome {
                        InspectionOutcome::Consistent => {
                            coverage.files_checked += 1;
                            coverage.consistent += 1;
                        }
                        InspectionOutcome::Proven(evidence) => {
                            coverage.files_checked += 1;
                            candidates.push(structural_candidate(&path, evidence));
                        }
                        InspectionOutcome::Skip(reason) => {
                            *coverage
                                .skipped_by_reason
                                .entry(reason.to_string())
                                .or_default() += 1;
                        }
                    }
                }
                Err(error) => {
                    eprintln!(
                        "WARNING: structural scan skipped {}: {error:#}",
                        path.display()
                    );
                    coverage.io_errors += 1;
                    *coverage
                        .skipped_by_reason
                        .entry(SkipReason::IoError.as_str().to_string())
                        .or_default() += 1;
                }
            }
            let processed = coverage.files_seen;
            if processed.is_multiple_of(2_000) {
                update_progress(
                    progress_path,
                    "scanning",
                    "scanning",
                    processed,
                    total,
                    candidates.len(),
                    &coverage,
                )?;
            }
            if processed.is_multiple_of(50_000) {
                eprintln!(
                    "[structural] inspected {processed}/{total} files, {} suspects so far...",
                    candidates.len()
                );
            }
        }
    }
    eprintln!(
        "[structural] scan finished in {:.1}s: files_seen={} files_checked={} consistent={} suspects={} sparse={} io_errors={} bytes_read={} skipped_by_reason={:?} (cancelled={cancelled})",
        scan_started_at.elapsed().as_secs_f64(),
        coverage.files_seen,
        coverage.files_checked,
        coverage.consistent,
        candidates.len(),
        coverage.sparse_files,
        coverage.io_errors,
        coverage.bytes_read,
        coverage.skipped_by_reason
    );
    candidates.sort_by(|left, right| left.exact_paths.cmp(&right.exact_paths));
    let mut service_counts = BTreeMap::new();
    for candidate in &candidates {
        *service_counts.entry(candidate.service.clone()).or_default() += 1;
    }
    let report = CorruptionReport {
        contract_version: CORRUPTION_CONTRACT_VERSION,
        detection_method: DetectionMethod::Structural,
        scan_started_utc: scan_started_utc.to_rfc3339_opts(SecondsFormat::AutoSi, true),
        settings: CorruptionSettings {
            threshold: None,
            lookback_days: None,
            min_stable_age_seconds: Some(MIN_STABLE_AGE_SECONDS),
            max_prefix_bytes: Some(MAX_PREFIX_BYTES),
        },
        service_counts,
        detection_counts: BTreeMap::from([("structural".to_string(), candidates.len())]),
        total: candidates.len(),
        coverage: Some(coverage.clone()),
        candidates,
    };
    update_progress(
        progress_path,
        if cancelled { "cancelled" } else { "completed" },
        "scanning",
        coverage.files_seen,
        total,
        report.total,
        &coverage,
    )?;
    Ok(StructuralScanResult { report, cancelled })
}

pub fn revalidate_for_removal(
    root: &Path,
    candidate: &CorruptionCandidate,
    now: SystemTime,
) -> Result<RemovalDisposition> {
    let layout = Layout::native().context("structural removal is unsupported on this platform")?;
    revalidate_for_removal_with_layout(root, candidate, now, layout)
}

fn revalidate_for_removal_with_layout(
    root: &Path,
    candidate: &CorruptionCandidate,
    now: SystemTime,
    layout: Layout,
) -> Result<RemovalDisposition> {
    if candidate.exact_paths.len() != 1 {
        bail!("structural candidate must contain exactly one exact path");
    }
    let CorruptionEvidence::Structural {
        structural: persisted,
    } = &candidate.evidence
    else {
        bail!("remove-structural received non-structural evidence");
    };
    if persisted.issues.is_empty() {
        bail!("structural candidate has no issues");
    }
    let path = PathBuf::from(&candidate.exact_paths[0]);
    let Some(path_digest) = cache_utils::strict_cache_file_digest(root, &path) else {
        bail!("structural candidate path has an invalid cache layout");
    };
    match std::fs::symlink_metadata(&path) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(RemovalDisposition::Missing)
        }
        Err(error) => {
            return Err(error).with_context(|| format!("failed to lstat {}", path.display()))
        }
        Ok(_) => {}
    }
    let inspection = inspect_path_with_layout(root, &path, path_digest, now, Utc::now(), layout)?;
    match inspection.outcome {
        InspectionOutcome::Consistent => Ok(RemovalDisposition::Healed),
        InspectionOutcome::Skip("cancelled") => Ok(RemovalDisposition::Cancelled),
        InspectionOutcome::Skip(reason) => bail!(
            "structural candidate {} is no longer safely actionable: {reason}",
            path.display()
        ),
        InspectionOutcome::Proven(current) => {
            if current.fingerprint != persisted.fingerprint {
                bail!(
                    "structural candidate changed since scan: {}",
                    path.display()
                );
            }
            if current.issues != persisted.issues
                || current.cache_key != persisted.cache_key
                || current.cache_key_md5 != persisted.cache_key_md5
            {
                bail!(
                    "structural candidate finding changed since scan: {}",
                    path.display()
                );
            }
            Ok(RemovalDisposition::Ready {
                path,
                evidence: persisted.clone(),
            })
        }
    }
}

#[derive(Debug, Clone)]
pub enum RemovalDisposition {
    Missing,
    Healed,
    Cancelled,
    Ready {
        path: PathBuf,
        evidence: StructuralEvidence,
    },
}

#[cfg(all(test, unix, target_pointer_width = "64"))]
pub(crate) fn write_linux_v5_test_fixture(
    root: &Path,
    key: &[u8],
    headers: &[u8],
    payload: &[u8],
) -> PathBuf {
    let layout = Layout::linux_x86_64();
    let header_start = layout.size + KEY_MARKER.len() + key.len() + 1;
    let body_start = header_start + headers.len();
    let mut bytes = vec![0; body_start];
    bytes[layout.version..layout.version + std::mem::size_of::<usize>()]
        .copy_from_slice(&5usize.to_ne_bytes());
    bytes[layout.crc32..layout.crc32 + 4].copy_from_slice(&crc32fast::hash(key).to_ne_bytes());
    bytes[layout.header_start..layout.header_start + 2]
        .copy_from_slice(&(header_start as u16).to_ne_bytes());
    bytes[layout.body_start..layout.body_start + 2]
        .copy_from_slice(&(body_start as u16).to_ne_bytes());
    bytes[layout.size..layout.size + KEY_MARKER.len()].copy_from_slice(KEY_MARKER);
    bytes[layout.size + KEY_MARKER.len()..header_start - 1].copy_from_slice(key);
    bytes[header_start - 1] = b'\n';
    bytes[header_start..body_start].copy_from_slice(headers);
    bytes.extend_from_slice(payload);
    let digest = u128::from_be_bytes(md5::compute(key).0);
    let path = cache_utils::cache_path_for_digest(root, digest);
    std::fs::create_dir_all(path.parent().expect("fixture cache path has parent"))
        .expect("create fixture parents");
    std::fs::write(&path, bytes).expect("write fixture");
    path
}

#[cfg(test)]
mod tests {
    use super::*;

    fn put_usize(buffer: &mut [u8], offset: usize, value: usize) {
        buffer[offset..offset + std::mem::size_of::<usize>()].copy_from_slice(&value.to_ne_bytes());
    }

    fn put_u32(buffer: &mut [u8], offset: usize, value: u32) {
        buffer[offset..offset + 4].copy_from_slice(&value.to_ne_bytes());
    }

    fn put_u16(buffer: &mut [u8], offset: usize, value: u16) {
        buffer[offset..offset + 2].copy_from_slice(&value.to_ne_bytes());
    }

    fn fixture(key: &[u8], headers: &[u8], payload: &[u8]) -> (Vec<u8>, u128) {
        let layout = Layout::linux_x86_64();
        let header_start = layout.size + KEY_MARKER.len() + key.len() + 1;
        let body_start = header_start + headers.len();
        let mut bytes = vec![0; body_start];
        put_usize(&mut bytes, layout.version, 5);
        put_u32(&mut bytes, layout.crc32, crc32fast::hash(key));
        put_u16(&mut bytes, layout.header_start, header_start as u16);
        put_u16(&mut bytes, layout.body_start, body_start as u16);
        bytes[layout.size..layout.size + KEY_MARKER.len()].copy_from_slice(KEY_MARKER);
        bytes[layout.size + KEY_MARKER.len()..header_start - 1].copy_from_slice(key);
        bytes[header_start - 1] = b'\n';
        bytes[header_start..body_start].copy_from_slice(headers);
        bytes.extend_from_slice(payload);
        let digest = u128::from_be_bytes(md5::compute(key).0);
        (bytes, digest)
    }

    fn issues(outcome: ParseOutcome) -> Vec<StructuralIssue> {
        match outcome {
            ParseOutcome::Proven(parsed) => parsed.issues,
            other => panic!("expected proven corruption, got {other:?}"),
        }
    }

    fn materialize(root: &Path, key: &[u8], headers: &[u8], payload: &[u8]) -> (PathBuf, usize) {
        let (bytes, digest) = fixture(key, headers, payload);
        let body_start = read_u16(&bytes, Layout::linux_x86_64().body_start).unwrap() as usize;
        let path = cache_utils::cache_path_for_digest(root, digest);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, bytes).unwrap();
        (path, body_start)
    }

    fn old_now() -> SystemTime {
        SystemTime::now() + Duration::from_secs(MIN_STABLE_AGE_SECONDS + 5)
    }

    #[test]
    fn authentic_v5_200_and_valid_empty_are_consistent() {
        for payload in [b"hello".as_slice(), b"".as_slice()] {
            let headers = format!(
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n",
                payload.len()
            );
            let (bytes, digest) =
                fixture(b"steam/depot/filebytes=0-4", headers.as_bytes(), payload);
            assert!(matches!(
                parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()),
                ParseOutcome::Consistent
            ));
        }
    }

    #[cfg(all(target_os = "linux", target_arch = "x86_64"))]
    #[test]
    fn native_linux_x86_64_layout_matches_nginx_v5_abi() {
        let native = Layout::native().unwrap();
        let expected = Layout::linux_x86_64();
        assert_eq!(native.size, 336);
        assert_eq!(native.version, expected.version);
        assert_eq!(native.crc32, expected.crc32);
        assert_eq!(native.header_start, expected.header_start);
        assert_eq!(native.body_start, expected.body_start);
        assert_eq!(native.etag_len, expected.etag_len);
        assert_eq!(native.vary_len, expected.vary_len);
        assert_eq!(native.variant, expected.variant);
    }

    #[test]
    fn payload_short_overlong_and_offsets_are_proven() {
        for payload in [b"1234".as_slice(), b"123456".as_slice()] {
            let (bytes, digest) = fixture(
                b"steam/file",
                b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n",
                payload,
            );
            assert_eq!(
                issues(parse_prefix(
                    &bytes,
                    bytes.len() as u64,
                    digest,
                    Layout::linux_x86_64()
                )),
                [StructuralIssue::PayloadLengthMismatch]
            );
        }
        let (mut bytes, digest) = fixture(
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let layout = Layout::linux_x86_64();
        put_u16(&mut bytes, layout.body_start, 1);
        assert_eq!(
            issues(parse_prefix(&bytes, bytes.len() as u64, digest, layout)),
            [StructuralIssue::InvalidPayloadOffset]
        );
        put_u16(&mut bytes, layout.body_start, u16::MAX);
        assert_eq!(
            issues(parse_prefix(&bytes, bytes.len() as u64, digest, layout)),
            [StructuralIssue::TruncatedBeforePayload]
        );
    }

    #[test]
    fn envelope_corruptions_and_path_mismatch_are_proven() {
        let (base, digest) = fixture(
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let layout = Layout::linux_x86_64();
        let mut bad = base.clone();
        bad[layout.size + 1] ^= 1;
        assert_eq!(
            issues(parse_prefix(&bad, bad.len() as u64, digest, layout)),
            [StructuralIssue::MalformedCacheHeader]
        );
        let mut bad = base.clone();
        put_u32(&mut bad, layout.crc32, 0);
        assert_eq!(
            issues(parse_prefix(&bad, bad.len() as u64, digest, layout)),
            [StructuralIssue::MalformedCacheHeader]
        );
        for length_offset in [layout.etag_len, layout.vary_len] {
            let mut bad = base.clone();
            bad[length_offset] = 129;
            assert_eq!(
                issues(parse_prefix(&bad, bad.len() as u64, digest, layout)),
                [StructuralIssue::MalformedCacheHeader]
            );
        }
        assert_eq!(
            issues(parse_prefix(&base, base.len() as u64, digest ^ 1, layout)),
            [StructuralIssue::CacheKeyPathMismatch]
        );
        assert!(matches!(
            parse_prefix(&[], 0, digest, layout),
            ParseOutcome::Proven(_)
        ));
        let short = &base[..layout.size + 2];
        assert_eq!(
            issues(parse_prefix(short, short.len() as u64, digest, layout)),
            [StructuralIssue::TruncatedCacheHeader]
        );
    }

    #[test]
    fn range_chunked_encoding_and_skip_shapes_follow_closed_table() {
        let cases = [
            (
                b"HTTP/1.1 206 Partial\r\nContent-Range: bytes 0-4/10\r\nContent-Length: 5\r\n\r\n"
                    .as_slice(),
                b"12345".as_slice(),
                None,
            ),
            (
                b"HTTP/1.1 206 Partial\r\nContent-Range: bytes 5-9/10\r\nContent-Length: 5\r\n\r\n",
                b"1234",
                Some(StructuralIssue::ContentRangeLengthMismatch),
            ),
            (
                b"HTTP/1.1 206 Partial\r\nContent-Range: bytes 0-4/10\r\nContent-Length: 4\r\n\r\n",
                b"12345",
                Some(StructuralIssue::ContentLengthRangeConflict),
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 5\r\n\r\n",
                b"12345",
                None,
            ),
        ];
        for (headers, payload, expected_issue) in cases {
            let (bytes, digest) = fixture(b"steam/file", headers, payload);
            let outcome = parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64());
            match expected_issue {
                Some(issue) => assert!(issues(outcome).contains(&issue)),
                None => assert!(matches!(outcome, ParseOutcome::Consistent)),
            }
        }
        for headers in [
            b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n".as_slice(),
            b"HTTP/1.1 200 OK\r\n\r\n",
        ] {
            let (bytes, digest) = fixture(b"steam/file", headers, b"data");
            assert!(matches!(
                parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()),
                ParseOutcome::Skip(SkipReason::NoAuthoritativeLength)
            ));
        }
    }

    #[test]
    fn ambiguous_and_unsupported_http_never_become_candidates() {
        for (headers, reason) in [
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\n".as_slice(),
                SkipReason::AmbiguousHttpHeaders,
            ),
            (
                b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n",
                SkipReason::UnsupportedStatus,
            ),
            (
                b"HTTP/1.1 206 Partial\r\nContent-Type: multipart/byteranges\r\n\r\n",
                SkipReason::UnsupportedRangeShape,
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 18446744073709551616\r\n\r\n",
                SkipReason::AmbiguousHttpHeaders,
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n",
                SkipReason::AmbiguousHttpHeaders,
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\nX-Late: value\r\n\r\n",
                SkipReason::AmbiguousHttpHeaders,
            ),
            (b"NOT HTTP\r\n\r\n", SkipReason::UnsupportedStatus),
        ] {
            let (bytes, digest) = fixture(b"steam/file", headers, b"");
            assert!(
                matches!(parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()), ParseOutcome::Skip(actual) if actual == reason)
            );
        }
    }

    #[test]
    fn valid_vary_variant_path_is_consistent() {
        let (mut bytes, _) = fixture(
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let layout = Layout::linux_x86_64();
        bytes[layout.vary_len] = 4;
        let variant = [7u8; 16];
        bytes[layout.variant..layout.variant + 16].copy_from_slice(&variant);
        assert!(matches!(
            parse_prefix(
                &bytes,
                bytes.len() as u64,
                u128::from_be_bytes(variant),
                layout
            ),
            ParseOutcome::Consistent
        ));
    }

    #[test]
    fn chunked_206_uses_content_range_and_ignores_content_length() {
        let (bytes, digest) = fixture(
            b"steam/file",
            b"HTTP/1.1 206 Partial\r\nTransfer-Encoding: chunked\r\nContent-Range: bytes 0-4/10\r\nContent-Length: 999\r\n\r\n",
            b"12345",
        );
        assert!(matches!(
            parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()),
            ParseOutcome::Consistent
        ));
    }

    #[test]
    fn filesystem_inspection_reads_only_through_payload_offset() {
        let temp = tempfile::tempdir().unwrap();
        let payload = vec![7u8; 1024 * 1024];
        let headers = format!(
            "HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n",
            payload.len()
        );
        let (path, body_start) =
            materialize(temp.path(), b"steam/file", headers.as_bytes(), &payload);
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(inspection.outcome, InspectionOutcome::Consistent));
        assert_eq!(inspection.bytes_read, body_start as u64);
        assert!(inspection.bytes_read <= MAX_PREFIX_BYTES);
        assert!(inspection.bytes_read < std::fs::metadata(path).unwrap().len());
    }

    #[test]
    fn malformed_small_payload_offset_reads_only_the_offset_probe() {
        let temp = tempfile::tempdir().unwrap();
        let (path, _) = materialize(
            temp.path(),
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 1024\r\n\r\n",
            &[7u8; 1024],
        );
        let layout = Layout::linux_x86_64();
        let mut bytes = std::fs::read(&path).unwrap();
        put_u16(&mut bytes, layout.body_start, 1);
        std::fs::write(&path, bytes).unwrap();

        let mut file = open_nofollow(&path).unwrap();
        let file_len = file.metadata().unwrap().len();
        let (prefix, bytes_read) = read_prefix(&mut file, file_len, layout).unwrap();
        let expected_probe = layout.body_start + std::mem::size_of::<u16>();
        assert_eq!(bytes_read, expected_probe as u64);
        assert_eq!(prefix.len(), expected_probe);
        assert!(bytes_read < (layout.size + KEY_MARKER.len()) as u64);
    }

    #[test]
    fn root_failure_is_fatal_and_count_pass_is_cancellable() {
        let temp = tempfile::tempdir().unwrap();
        assert!(
            scan_with_cancellation(&temp.path().join("missing"), Utc::now(), None, || false)
                .is_err()
        );

        let root = temp.path().join("cache");
        for digest in [1u128, 2, 3] {
            let path = cache_utils::cache_path_for_digest(&root, digest);
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, b"").unwrap();
        }
        let checks = std::cell::Cell::new(0usize);
        let result = scan_with_cancellation(&root, Utc::now(), None, || {
            let next = checks.get() + 1;
            checks.set(next);
            next >= 2
        })
        .unwrap();
        assert!(result.cancelled);
        assert!(checks.get() >= 2);
    }

    #[test]
    fn recent_and_mid_read_mutation_are_typed_skips() {
        let temp = tempfile::tempdir().unwrap();
        let (path, _) = materialize(
            temp.path(),
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"1234",
        );
        let recent = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            SystemTime::now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(recent.outcome, InspectionOutcome::Skip("recent")));

        let replacement = std::fs::read(&path).unwrap();
        let changed = inspect_path_with_layout_and_hook(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
            || {
                let mut replacement = replacement.clone();
                replacement.push(0);
                std::fs::write(&path, replacement).unwrap();
            },
        )
        .unwrap();
        assert!(matches!(
            changed.outcome,
            InspectionOutcome::Skip("changed")
        ));
    }

    #[test]
    fn structural_revalidation_resolves_missing_healed_and_rejects_changed() {
        let temp = tempfile::tempdir().unwrap();
        let headers = b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n";
        let (path, _) = materialize(temp.path(), b"steam/file", headers, b"1234");
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        let InspectionOutcome::Proven(evidence) = inspection.outcome else {
            panic!("expected structural evidence");
        };
        let candidate = structural_candidate(&path, evidence);
        assert!(matches!(
            revalidate_for_removal_with_layout(
                temp.path(),
                &candidate,
                old_now(),
                Layout::linux_x86_64()
            )
            .unwrap(),
            RemovalDisposition::Ready { .. }
        ));

        std::fs::remove_file(&path).unwrap();
        assert!(matches!(
            revalidate_for_removal_with_layout(
                temp.path(),
                &candidate,
                old_now(),
                Layout::linux_x86_64()
            )
            .unwrap(),
            RemovalDisposition::Missing
        ));

        materialize(
            temp.path(),
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"1234",
        );
        assert!(matches!(
            revalidate_for_removal_with_layout(
                temp.path(),
                &candidate,
                old_now(),
                Layout::linux_x86_64()
            )
            .unwrap(),
            RemovalDisposition::Healed
        ));

        materialize(temp.path(), b"steam/file", headers, b"12");
        assert!(revalidate_for_removal_with_layout(
            temp.path(),
            &candidate,
            old_now(),
            Layout::linux_x86_64()
        )
        .is_err());
    }

    #[test]
    fn structural_wire_evidence_is_flat_tagged_and_has_no_observations() {
        let evidence = StructuralEvidence {
            issues: vec![StructuralIssue::EmptyCacheFile],
            cache_key_encoding: "hex".into(),
            cache_key: String::new(),
            cache_key_md5: "d41d8cd98f00b204e9800998ecf8427e".into(),
            cache_version: 5,
            http_status: None,
            header_start: None,
            body_start: None,
            file_length: 0,
            actual_payload_length: None,
            expected_payload_length: None,
            content_length: None,
            content_range: None,
            fingerprint: FileFingerprint {
                dev: 1,
                ino: 2,
                len: 0,
                mtime_ns: 3,
                ctime_ns: 4,
            },
            detected_at_utc: "2024-01-01T00:00:00Z".into(),
        };
        let value = serde_json::to_value(CorruptionEvidence::Structural {
            structural: evidence,
        })
        .unwrap();
        assert_eq!(value["kind"], "structural");
        assert_eq!(value["issues"][0], "empty_cache_file");
        assert!(value.get("structural").is_none());
        assert!(value.get("observations").is_none());
    }

    #[cfg(unix)]
    #[test]
    fn symlink_leaf_is_never_followed() {
        use std::os::unix::fs::symlink;
        let temp = tempfile::tempdir().unwrap();
        let target = temp.path().join("target");
        std::fs::write(&target, b"target").unwrap();
        let digest = u128::from_be_bytes(md5::compute(b"steam/file").0);
        let path = cache_utils::cache_path_for_digest(temp.path(), digest);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        symlink(&target, &path).unwrap();
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(
            inspection.outcome,
            InspectionOutcome::Skip("symlink")
        ));
        assert_eq!(std::fs::read(target).unwrap(), b"target");
    }

    #[cfg(unix)]
    #[test]
    fn symlinked_parent_and_special_leaf_are_typed_skips() {
        use std::ffi::CString;
        use std::os::unix::ffi::OsStrExt;
        use std::os::unix::fs::symlink;
        let temp = tempfile::tempdir().unwrap();
        let digest = u128::from_be_bytes(md5::compute(b"steam/file").0);
        let path = cache_utils::cache_path_for_digest(temp.path(), digest);
        let outside = temp.path().join("outside");
        std::fs::create_dir_all(outside.join(path.parent().unwrap().file_name().unwrap())).unwrap();
        symlink(
            &outside,
            temp.path()
                .join(path.components().nth_back(2).unwrap().as_os_str()),
        )
        .unwrap();
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(
            inspection.outcome,
            InspectionOutcome::Skip("symlink")
        ));

        let fifo_root = temp.path().join("fifo-root");
        let fifo = cache_utils::cache_path_for_digest(&fifo_root, digest);
        std::fs::create_dir_all(fifo.parent().unwrap()).unwrap();
        let fifo_c = CString::new(fifo.as_os_str().as_bytes()).unwrap();
        assert_eq!(unsafe { libc::mkfifo(fifo_c.as_ptr(), 0o600) }, 0);
        let inspection = inspect_path_with_layout(
            &fifo_root,
            &fifo,
            cache_utils::strict_cache_file_digest(&fifo_root, &fifo).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(
            inspection.outcome,
            InspectionOutcome::Skip("special_file")
        ));
    }

    #[cfg(unix)]
    #[test]
    fn sparse_allocation_is_not_itself_corruption() {
        let temp = tempfile::tempdir().unwrap();
        let payload_len = 1024 * 1024u64;
        let headers = format!("HTTP/1.1 200 OK\r\nContent-Length: {payload_len}\r\n\r\n");
        let (path, body_start) = materialize(temp.path(), b"steam/file", headers.as_bytes(), b"");
        let file = OpenOptions::new().write(true).open(&path).unwrap();
        file.set_len(body_start as u64 + payload_len).unwrap();
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(inspection.outcome, InspectionOutcome::Consistent));
        assert!(inspection.sparse);
    }
}
