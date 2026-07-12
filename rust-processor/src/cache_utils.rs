use std::collections::{BTreeSet, HashSet};
use std::io;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

#[allow(dead_code)]
pub const DEFAULT_SLICE_SIZE: u64 = 1_048_576;

// Detect + evict probe the 1 MiB slices of each (service, url). The chunk count is now sized
// from the URL's observed byte size (`MAX(LogEntries.BytesServed)`) via `probe_chunks_for_bytes`,
// clamped to [`DEFAULT_MAX_CHUNKS`, `MAX_PROBE_CHUNKS`]. `DEFAULT_MAX_CHUNKS` is the unconditional
// FLOOR — sizing can only ever probe MORE slices than the old flat behavior, never fewer — and
// `MAX_PROBE_CHUNKS` is the perf CEILING that keeps the all-miss cost bounded (see below).
#[allow(dead_code)]
pub const DEFAULT_MAX_CHUNKS: usize = 100;

// Upper bound on probe chunk count when sizing from a URL's byte size.
//
// WHY A CAP IS REQUIRED: the eviction scan runs over ALL inactive downloads every 6h, and the
// all-absent (true-miss) case CANNOT early-exit — `has_cache_file` / the path probe must check
// every candidate before concluding "gone". Sizing chunk count uncapped from per-URL bytes would
// let a single large URL (e.g. a 100 GB session sum mis-attributed to one URL) explode to ~100k
// md5/stat candidates, multiplied across every unique (service, url) that genuinely misses.
//
// WHY 4096: DEFAULT_SLICE_SIZE is 1 MiB, so 4096 chunks == 4 GiB worth of slices. A single cached
// lancache OBJECT (one Steam/Epic/Blizzard chunk file, one WSUS .psf range) is realistically far
// smaller than 4 GiB — they are typically 1 MiB..256 MiB — so 4 GiB covers any realistic single
// sliced object with large margin while never undercounting. Worst-case all-miss cost per unique
// key is bounded at 4097 candidates (vs today's 101), each unique key is probed exactly once
// scan-wide (memoized), and the present case still short-circuits on the first hit.
#[allow(dead_code)]
pub const MAX_PROBE_CHUNKS: usize = 4096;

/// The byte-range shape observed in an access log. Only a complete, single inclusive HTTP
/// range is accepted for sliced cache inference; unsupported range forms fail closed.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum ObservedByteRange {
    NoRange,
    Inclusive { start: u64, end: u64 },
}

/// Exact nginx cache-key form for a physical cache object.
#[derive(Debug, Clone, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum CacheSliceKind {
    NoRange,
    Noslice,
    Ranged { start: u64, end: u64 },
}

/// One exact cache-key/path alternative produced from an observed request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhysicalCacheSlice {
    pub kind: CacheSliceKind,
    pub exact_path: PathBuf,
}

/// Canonical request-to-cache mapping shared by detection and evidence-driven removal.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RequestSliceMapping {
    pub normalized_uri: String,
    pub observed_range: ObservedByteRange,
    pub slices: Vec<PhysicalCacheSlice>,
}

/// Parse a single inclusive HTTP byte range. Empty/`-` means the request had no Range header.
/// Suffix, open-ended, reversed, multi-range, non-decimal, and overflowing values are rejected.
pub fn parse_http_byte_range(raw: &str) -> Option<ObservedByteRange> {
    let raw = raw.trim();
    if raw.is_empty() || raw == "-" {
        return Some(ObservedByteRange::NoRange);
    }

    let value = raw.strip_prefix("bytes=")?;
    if value.contains(',') {
        return None;
    }
    let (start, end) = value.split_once('-')?;
    if start.is_empty()
        || end.is_empty()
        || !start.bytes().all(|byte| byte.is_ascii_digit())
        || !end.bytes().all(|byte| byte.is_ascii_digit())
    {
        return None;
    }

    let start = start.parse::<u64>().ok()?;
    let end = end.parse::<u64>().ok()?;
    (start <= end).then_some(ObservedByteRange::Inclusive { start, end })
}

/// Align an inclusive observed range to every containing 1 MiB nginx slice, including both
/// endpoint slices. Arithmetic and unreasonably large mappings fail closed.
pub fn aligned_slice_ranges(range: &ObservedByteRange) -> Option<Vec<(u64, u64)>> {
    let ObservedByteRange::Inclusive { start, end } = range else {
        return Some(Vec::new());
    };

    let first_start = (start / DEFAULT_SLICE_SIZE) * DEFAULT_SLICE_SIZE;
    let last_start = (end / DEFAULT_SLICE_SIZE) * DEFAULT_SLICE_SIZE;
    let count = last_start
        .checked_sub(first_start)?
        .checked_div(DEFAULT_SLICE_SIZE)?
        .checked_add(1)?;
    let count = usize::try_from(count).ok()?;
    if count > MAX_PROBE_CHUNKS {
        return None;
    }

    let mut ranges = Vec::with_capacity(count);
    for index in 0..count {
        let offset = (index as u64).checked_mul(DEFAULT_SLICE_SIZE)?;
        let slice_start = first_start.checked_add(offset)?;
        let slice_end = slice_start.checked_add(DEFAULT_SLICE_SIZE - 1)?;
        ranges.push((slice_start, slice_end));
    }
    Some(ranges)
}

/// Resolve an observed request to its exact nginx cache-key path(s). A range maps to each
/// containing physical slice. A range-less request retains the no-range and `::noslice`
/// alternatives because the access log alone cannot distinguish those locations.
pub fn physical_slices_for_request(
    cache_dir: &Path,
    service: &str,
    raw_url: &str,
    raw_http_range: &str,
) -> Option<RequestSliceMapping> {
    let observed_range = parse_http_byte_range(raw_http_range)?;
    let normalized_uri = nginx_cache_uri(raw_url).into_owned();
    let service = service_name_lowercase(service);

    let slices = match &observed_range {
        ObservedByteRange::NoRange => vec![
            PhysicalCacheSlice {
                kind: CacheSliceKind::NoRange,
                exact_path: calculate_cache_path_no_range(cache_dir, &service, &normalized_uri),
            },
            PhysicalCacheSlice {
                kind: CacheSliceKind::Noslice,
                exact_path: calculate_cache_path_noslice(cache_dir, &service, &normalized_uri),
            },
        ],
        ObservedByteRange::Inclusive { .. } => aligned_slice_ranges(&observed_range)?
            .into_iter()
            .map(|(start, end)| PhysicalCacheSlice {
                kind: CacheSliceKind::Ranged { start, end },
                exact_path: calculate_cache_path(cache_dir, &service, &normalized_uri, start, end),
            })
            .collect(),
    };

    Some(RequestSliceMapping {
        normalized_uri,
        observed_range,
        slices,
    })
}

/// Derives the probe chunk count for a URL from its observed byte size, clamped to
/// `[DEFAULT_MAX_CHUNKS, MAX_PROBE_CHUNKS]`.
///
/// - `bytes <= 0` (unknown/0 size) → `DEFAULT_MAX_CHUNKS` (the old floor; never less coverage).
/// - small object → `DEFAULT_MAX_CHUNKS` (floor keeps the old 100-slice safety net).
/// - large object → `ceil(bytes / DEFAULT_SLICE_SIZE)` capped at `MAX_PROBE_CHUNKS`.
///
/// Sizing this way lets detect/evict find cached slices that fall past the first 100 MiB of a
/// large object, fixing the Phase-3 false-miss, while the cap bounds the all-miss scan cost.
#[allow(dead_code)]
pub fn probe_chunks_for_bytes(bytes: i64) -> usize {
    if bytes <= 0 {
        return DEFAULT_MAX_CHUNKS;
    }
    // ceil(bytes / slice) — number of 1 MiB slices needed to cover `bytes`.
    let needed = ((bytes as u64).div_ceil(DEFAULT_SLICE_SIZE)) as usize;
    needed.clamp(DEFAULT_MAX_CHUNKS, MAX_PROBE_CHUNKS)
}

/// Returns the canonical form of `candidate`, but only if it resides under `root` and is not a symlink.
/// Errors out for symlinks (refuses to follow), paths outside the root, or non-existent paths.
///
/// This is the canonical guard used before any `fs::remove_file`, `fs::remove_dir`, or
/// `fs::remove_dir_all` on URL-derived or admin-controlled paths.
#[allow(dead_code)]
pub fn safe_path_under_root(root: &Path, candidate: &Path) -> io::Result<PathBuf> {
    let meta = candidate.symlink_metadata()?;
    if meta.file_type().is_symlink() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("symlink not allowed: {}", candidate.display()),
        ));
    }
    let canonical = candidate.canonicalize()?;
    let canonical_root = root.canonicalize()?;
    if !canonical.starts_with(&canonical_root) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("path escapes root: {}", candidate.display()),
        ));
    }
    Ok(canonical)
}

// Filesystem type magic numbers from statfs (Unix only)
#[cfg(unix)]
#[allow(dead_code)]
const NFS_SUPER_MAGIC: i64 = 0x6969;
#[cfg(unix)]
#[allow(dead_code)]
const NFS_V4_MAGIC: i64 = 0x6E667364; // "nfsd" in hex
#[cfg(unix)]
#[allow(dead_code)]
const CIFS_MAGIC_NUMBER: i64 = 0xFF534D42;
#[cfg(unix)]
#[allow(dead_code)]
const SMB_SUPER_MAGIC: i64 = 0x517B;
#[cfg(unix)]
#[allow(dead_code)]
const SMB2_MAGIC_NUMBER: i64 = 0xFE534D42;

/// Filesystem type enumeration for optimizing operations
/// Used on Linux to detect NFS/SMB and adjust parallelism accordingly
#[derive(Debug, Clone, Copy, PartialEq)]
#[allow(dead_code)]
pub enum FilesystemType {
    /// Local filesystem (ext4, xfs, btrfs, etc.)
    Local,
    /// NFS network mount
    Nfs,
    /// SMB/CIFS network mount
    Smb,
    /// Unknown filesystem type
    Unknown,
}

#[allow(dead_code)]
impl FilesystemType {
    /// Check if this is a network filesystem (NFS, SMB, CIFS)
    pub fn is_network(&self) -> bool {
        matches!(self, FilesystemType::Nfs | FilesystemType::Smb)
    }

    /// Get recommended parallelism level for this filesystem type
    /// Network filesystems perform poorly with high parallelism
    pub fn recommended_parallelism(&self) -> usize {
        match self {
            FilesystemType::Local => {
                // Local SSDs can handle high parallelism
                let cpus = std::thread::available_parallelism()
                    .map(|p| p.get())
                    .unwrap_or(4);
                std::cmp::min(cpus * 2, 16)
            }
            FilesystemType::Nfs | FilesystemType::Smb => {
                // Network filesystems are latency-bound, not bandwidth-bound: each metadata
                // lookup and header read costs a round-trip, so the only way to keep the link
                // busy is to have several requests in flight. Too low a value leaves the
                // scanner idle waiting on the wire; too high buries the server in IOPS.
                8
            }
            FilesystemType::Unknown => 4,
        }
    }
}

/// Detect the filesystem type for a given path
/// Uses statfs on Unix systems to check the filesystem magic number
#[cfg(unix)]
#[allow(dead_code)]
pub fn detect_filesystem_type(path: &Path) -> FilesystemType {
    use std::ffi::CString;
    use std::mem::MaybeUninit;
    use std::os::unix::ffi::OsStrExt;

    let c_path = match CString::new(path.as_os_str().as_bytes()) {
        Ok(p) => p,
        Err(_) => return FilesystemType::Unknown,
    };

    let mut stat: MaybeUninit<libc::statfs> = MaybeUninit::uninit();
    let res = unsafe { libc::statfs(c_path.as_ptr(), stat.as_mut_ptr()) };

    if res != 0 {
        return FilesystemType::Unknown;
    }

    let stat = unsafe { stat.assume_init() };
    let fs_type = stat.f_type as i64;

    match fs_type {
        x if x == NFS_SUPER_MAGIC || x == NFS_V4_MAGIC => FilesystemType::Nfs,
        x if x == CIFS_MAGIC_NUMBER || x == SMB_SUPER_MAGIC || x == SMB2_MAGIC_NUMBER => {
            FilesystemType::Smb
        }
        _ => FilesystemType::Local,
    }
}

/// Fallback for non-Unix systems - always returns Local
#[cfg(not(unix))]
#[allow(dead_code)]
pub fn detect_filesystem_type(_path: &Path) -> FilesystemType {
    // On Windows, network paths are typically detected via path prefix
    // For now, assume local
    FilesystemType::Local
}

/// Calculate MD5 hash for a cache key
/// Used for deriving cache file paths from service + URL combinations
pub fn calculate_md5(cache_key: &str) -> String {
    format!("{:x}", md5::compute(cache_key.as_bytes()))
}

/// 128-bit md5 digest of a cache key - the numeric form of the 32-hex on-disk file name
/// (`calculate_md5` prints exactly these bytes as lowercase hex). Probe indexes key on this
/// instead of the hex String: 16 inline bytes, no heap allocation per candidate.
#[allow(dead_code)]
pub fn calculate_md5_digest(cache_key: &str) -> u128 {
    u128::from_be_bytes(md5::compute(cache_key.as_bytes()).0)
}

/// Parses an on-disk cache file name (32 hex chars) into its u128 digest. Any other name
/// returns None - it cannot be an md5 cache key, so no probe candidate can ever match it.
/// Case-insensitive, mirroring the filesystem-casing normalization the String-keyed index
/// applied on Windows; nginx itself only writes lowercase names.
#[allow(dead_code)]
pub fn parse_cache_file_digest(name: &str) -> Option<u128> {
    if name.len() != 32 {
        return None;
    }
    let mut value: u128 = 0;
    for byte in name.bytes() {
        value = (value << 4) | (byte as char).to_digit(16)? as u128;
    }
    Some(value)
}

/// Validates the exact nginx `levels=2:2` relative path used by structural cache scans.
/// Unlike [`parse_cache_file_digest`], this deliberately rejects uppercase hex and verifies
/// both hash-directory suffixes. It is a lexical shape check; callers must separately enforce
/// canonical-root containment and no-symlink filesystem policy before reading or deleting.
pub fn strict_cache_file_digest(cache_root: &Path, candidate: &Path) -> Option<u128> {
    let relative = candidate.strip_prefix(cache_root).ok()?;
    let components = relative.components().collect::<Vec<_>>();
    if components.len() != 3 {
        return None;
    }
    let [std::path::Component::Normal(last_2), std::path::Component::Normal(previous_2), std::path::Component::Normal(file_name)] =
        components.as_slice()
    else {
        return None;
    };
    let last_2 = last_2.to_str()?;
    let previous_2 = previous_2.to_str()?;
    let file_name = file_name.to_str()?;
    let lowercase_hex = |value: &str| {
        value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || matches!(byte, b'a'..=b'f'))
    };
    if last_2.len() != 2
        || previous_2.len() != 2
        || file_name.len() != 32
        || !lowercase_hex(last_2)
        || !lowercase_hex(previous_2)
        || !lowercase_hex(file_name)
        || last_2 != &file_name[30..32]
        || previous_2 != &file_name[28..30]
    {
        return None;
    }
    parse_cache_file_digest(file_name)
}

/// Canonical on-disk path for a cache-key digest under `cache_dir`, mirroring
/// `calculate_cache_path`'s `{last_2}/{middle_2}/{full_hash}` lancache layout (nginx
/// `levels=2:2`, the layout every fs-probing path in this module already assumes).
#[allow(dead_code)]
pub fn cache_path_for_digest(cache_dir: &Path, digest: u128) -> PathBuf {
    let hash = format!("{:032x}", digest);
    let last_2 = &hash[30..];
    let middle_2 = &hash[28..30];
    cache_dir.join(last_2).join(middle_2).join(&hash)
}

/// Reproduce nginx's `$uri` from a stored `LogEntries.Url`.
///
/// The lancache monolithic image keys the cache on `$cacheidentifier$uri$slice_range`
/// (`cache.conf.d/root/30_cache_key.conf:1`), where `$uri` is nginx's NORMALIZED, URL-DECODED
/// request path — NOT `$request_uri`. The access log records the raw `$request` line, so the
/// parser stores the URL with the query string retained and percent-escapes as-logged. To match
/// the on-disk md5 filename we must transform the stored URL into nginx's `$uri` before hashing:
///
///   (a) DROP everything from the first `?` (query string — nginx keys on `$uri`, not `$request_uri`).
///   (b) PERCENT-DECODE `%XX` escapes (nginx decodes `$uri`).
///   (c) COLLAPSE consecutive `//` to `/` (nginx default `merge_slashes on`).
///   (d) RESOLVE `.` / `..` path segments.
///
/// This is the shared key-derivation chokepoint, applied identically by detect, evict, all three
/// removers and both corruption binaries. A wrong transform here breaks all of them together, so
/// the transforms are deliberately minimal and exactly mirror nginx ground truth.
///
/// Borrowed fast path: if the URL has no `?`, no `%`, no `//`, and no `.`-only / `..` segment, the
/// stored URL already equals `$uri`, so we return `Cow::Borrowed` with zero allocation. This bounds
/// the blast radius to only previously-divergent URLs and keeps the hot rayon'd probe loop cheap.
pub fn nginx_cache_uri(url: &str) -> std::borrow::Cow<'_, str> {
    // (a) Strip the query string first; only the path participates in `$uri`.
    let path = match url.find('?') {
        Some(idx) => &url[..idx],
        None => url,
    };

    // Fast path: a clean, already-`$uri`-shaped path. No query was present (path == url), no
    // percent-escape, no `//` run, and no `.`/`..` segment that would resolve away. Return the
    // original borrowed slice unchanged — zero allocation in the hot loop.
    if std::ptr::eq(path, url) && !needs_uri_normalization(path) {
        return std::borrow::Cow::Borrowed(url);
    }

    // (b) Percent-decode, then (c) collapse `//`, then (d) resolve dot-segments.
    let decoded = percent_decode(path);
    let collapsed = collapse_double_slashes(&decoded);
    let resolved = resolve_dot_segments(&collapsed);
    std::borrow::Cow::Owned(resolved)
}

/// True if `path` contains anything that `nginx_cache_uri` would rewrite: a `%` escape, a `//`
/// run, or a `.`/`..` path segment. Used to decide whether the borrowed fast path applies.
fn needs_uri_normalization(path: &str) -> bool {
    let bytes = path.as_bytes();
    if bytes.contains(&b'%') {
        return true;
    }
    if bytes.windows(2).any(|pair| pair == b"//") {
        return true;
    }
    // A `.`/`..` segment is one bounded by `/` (or string edges) consisting only of dots.
    path.split('/').any(|seg| seg == "." || seg == "..")
}

/// Decode `%XX` percent-escapes the way nginx decodes `$uri`. Lone or malformed `%` sequences are
/// left verbatim (nginx is lenient here and so are we). Self-contained: no extra crate dependency.
fn percent_decode(input: &str) -> String {
    let bytes = input.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            let hi = (bytes[i + 1] as char).to_digit(16);
            let lo = (bytes[i + 2] as char).to_digit(16);
            if let (Some(hi), Some(lo)) = (hi, lo) {
                out.push((hi * 16 + lo) as u8);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    // The decoded bytes form a path; cache keys are hashed as bytes so lossy UTF-8 is harmless,
    // and lancache paths are ASCII in practice.
    String::from_utf8_lossy(&out).into_owned()
}

/// Collapse runs of consecutive `/` to a single `/` (nginx `merge_slashes on`). Idempotent: an
/// already-collapsed path is returned with the same content.
fn collapse_double_slashes(input: &str) -> String {
    if !input.as_bytes().windows(2).any(|pair| pair == b"//") {
        return input.to_string();
    }
    let mut out = String::with_capacity(input.len());
    let mut prev_slash = false;
    for ch in input.chars() {
        if ch == '/' {
            if !prev_slash {
                out.push(ch);
            }
            prev_slash = true;
        } else {
            out.push(ch);
            prev_slash = false;
        }
    }
    out
}

/// Resolve `.` and `..` path segments the way nginx normalizes `$uri`. Operates on a
/// `/`-collapsed path. A leading `/` is preserved; a `..` cannot escape above the root.
fn resolve_dot_segments(input: &str) -> String {
    if !input.split('/').any(|seg| seg == "." || seg == "..") {
        return input.to_string();
    }

    let leading_slash = input.starts_with('/');
    let trailing_slash = input.len() > 1 && input.ends_with('/');

    let mut stack: Vec<&str> = Vec::new();
    for seg in input.split('/') {
        match seg {
            "" | "." => {} // empty (from leading/trailing/collapsed) and `.` are dropped
            ".." => {
                stack.pop();
            }
            other => stack.push(other),
        }
    }

    let mut out = String::with_capacity(input.len());
    if leading_slash {
        out.push('/');
    }
    out.push_str(&stack.join("/"));
    if trailing_slash && !out.ends_with('/') {
        out.push('/');
    }
    out
}

/// Calculate cache file path with byte range using lancache's MD5 structure:
/// /cache/{last_2_chars}/{2_chars_before_that}/{full_hash}
///
/// Cache key format: "{service}{url}bytes={start}-{end}"
///
/// Note: This function is used by multiple binaries but not all, hence the allow(dead_code)
#[allow(dead_code)]
pub fn calculate_cache_path(
    cache_dir: &Path,
    service: &str,
    url: &str,
    start: u64,
    end: u64,
) -> PathBuf {
    // Transform the stored URL into nginx's `$uri` so the md5 matches the on-disk filename.
    let url = nginx_cache_uri(url);
    let cache_key = format!("{}{}bytes={}-{}", service, url, start, end);
    let hash = calculate_md5(&cache_key);

    let len = hash.len();
    if len < 4 {
        return cache_dir.join(&hash);
    }

    let last_2 = &hash[len - 2..];
    let middle_2 = &hash[len - 4..len - 2];

    cache_dir.join(last_2).join(middle_2).join(&hash)
}

/// Calculate cache file path without byte range
/// Lancache nginx cache key format: $cacheidentifier$uri (NO slice_range!)
///
/// Cache key format: "{service}{url}"
///
/// Note: This function is used by multiple binaries but not all, hence the allow(dead_code)
#[allow(dead_code)]
pub fn calculate_cache_path_no_range(cache_dir: &Path, service: &str, url: &str) -> PathBuf {
    // Transform the stored URL into nginx's `$uri` so the md5 matches the on-disk filename.
    let url = nginx_cache_uri(url);
    let cache_key = format!("{}{}", service, url);
    let hash = calculate_md5(&cache_key);

    let len = hash.len();
    if len < 4 {
        return cache_dir.join(&hash);
    }

    let last_2 = &hash[len - 2..];
    let middle_2 = &hash[len - 4..len - 2];

    cache_dir.join(last_2).join(middle_2).join(&hash)
}

/// Calculate cache file path for the `@noslice` location.
/// Lancache nginx cache key format: `$cacheidentifier$uri::noslice` (literal `::noslice` suffix,
/// NO byte range — `15_noslice.conf:51`). Mirrors `calculate_cache_path_no_range` exactly except
/// for the trailing `::noslice`, so the produced filename matches the no-range/ranged forms in shape.
///
/// Note: This function is used by multiple binaries but not all, hence the allow(dead_code)
#[allow(dead_code)]
pub fn calculate_cache_path_noslice(cache_dir: &Path, service: &str, url: &str) -> PathBuf {
    // Transform the stored URL into nginx's `$uri` so the md5 matches the on-disk filename.
    let url = nginx_cache_uri(url);
    let cache_key = format!("{}{}::noslice", service, url);
    let hash = calculate_md5(&cache_key);

    let len = hash.len();
    if len < 4 {
        return cache_dir.join(&hash);
    }

    let last_2 = &hash[len - 2..];
    let middle_2 = &hash[len - 4..len - 2];

    cache_dir.join(last_2).join(middle_2).join(&hash)
}

/// Lowercase service name for cache-path MD5 hashing.
///
/// Feeds MD5 cache-path hashing and must NOT canonicalize IPs/localhost.
/// This is deliberately different from `service_utils::normalize_service_name`,
/// which canonicalizes IPs and localhost for log parsing. Do not merge these.
#[allow(dead_code)]
pub fn service_name_lowercase(service: &str) -> String {
    service.to_lowercase()
}

#[allow(dead_code)]
pub fn sorted_sample_urls<I, S>(urls: I, limit: usize) -> Vec<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut sample_urls = BTreeSet::new();

    for url in urls {
        let url = url.as_ref();
        if sample_urls.contains(url) {
            continue;
        }

        if sample_urls.len() < limit {
            sample_urls.insert(url.to_owned());
            continue;
        }

        if sample_urls
            .last()
            .map(|largest| url < largest.as_str())
            .unwrap_or(false)
        {
            sample_urls.pop_last();
            sample_urls.insert(url.to_owned());
        }
    }

    sample_urls.into_iter().collect()
}

/// Lazily yields the md5-hex cache-key hashes probed for (service, url) — exactly the
/// file-name components of `cache_path_candidates_for_probe`, in the same order, without
/// constructing any paths. Lazy so callers doing set-membership checks can early-exit
/// on the first hit instead of always hashing all `max_chunks + 2` candidates.
///
/// Order: `[no_range, noslice, chunk0, chunk1, ...]`. The `::noslice` candidate matches the
/// lancache `@noslice` location's `$cacheidentifier$uri::noslice` key (literal suffix, no byte
/// range) so the probe also finds noslice-cached content.
#[allow(dead_code)]
pub fn cache_hash_candidates_iter(
    service: &str,
    url: &str,
    max_chunks: usize,
) -> impl Iterator<Item = String> {
    let service = service_name_lowercase(service);
    // Transform the stored URL into nginx's `$uri` once so every candidate md5 (no-range +
    // noslice + ranged) matches the on-disk filename. Owned because the ranged closure outlives `url`.
    let url = nginx_cache_uri(url).into_owned();
    let no_range_hash = calculate_md5(&format!("{}{}", service, url));
    let noslice_hash = calculate_md5(&format!("{}{}::noslice", service, url));

    std::iter::once(no_range_hash)
        .chain(std::iter::once(noslice_hash))
        .chain(
            chunk_ranges_for_probe(max_chunks)
                .into_iter()
                .map(move |(start, end)| {
                    calculate_md5(&format!("{}{}bytes={}-{}", service, url, start, end))
                }),
        )
}

#[allow(dead_code)]
pub fn cache_hash_candidates_for_probe(service: &str, url: &str, max_chunks: usize) -> Vec<String> {
    cache_hash_candidates_iter(service, url, max_chunks).collect()
}

/// Digest twin of [`cache_hash_candidates_iter`]: yields the same candidates in the same order
/// as u128 digests. Reuses one key buffer across the ranged candidates, so probing allocates
/// nothing per candidate (the String form allocated a format! input plus a 32-byte hex String
/// for each of up to `max_chunks + 2` candidates - billions of allocations across a full scan).
#[allow(dead_code)]
pub fn cache_digest_candidates_iter(
    service: &str,
    url: &str,
    max_chunks: usize,
) -> impl Iterator<Item = u128> {
    use std::fmt::Write;

    let service = service_name_lowercase(service);
    let url = nginx_cache_uri(url).into_owned();
    let no_range = calculate_md5_digest(&format!("{}{}", service, url));
    let noslice = calculate_md5_digest(&format!("{}{}::noslice", service, url));

    let mut key_buf = String::with_capacity(service.len() + url.len() + 40);
    std::iter::once(no_range)
        .chain(std::iter::once(noslice))
        .chain(
            chunk_ranges_for_probe(max_chunks)
                .into_iter()
                .map(move |(start, end)| {
                    key_buf.clear();
                    let _ = write!(key_buf, "{}{}bytes={}-{}", service, url, start, end);
                    calculate_md5_digest(&key_buf)
                }),
        )
}

/// Digest twin of [`existing_cache_hashes_for_url`]: collects every EXISTING slice digest for
/// (service, url) with the same consecutive-miss-bounded walk, allocating no per-candidate
/// strings. Same coverage and safety guarantees.
#[allow(dead_code)]
pub fn existing_cache_digests_for_url<F>(service: &str, url: &str, mut exists: F) -> Vec<u128>
where
    F: FnMut(u128) -> bool,
{
    use std::fmt::Write;

    let service = service_name_lowercase(service);
    let url = nginx_cache_uri(url).into_owned();
    let mut hits: Vec<u128> = Vec::new();

    let no_range = calculate_md5_digest(&format!("{}{}", service, url));
    if exists(no_range) {
        hits.push(no_range);
    }
    let noslice = calculate_md5_digest(&format!("{}{}::noslice", service, url));
    if exists(noslice) {
        hits.push(noslice);
    }

    let mut key_buf = String::with_capacity(service.len() + url.len() + 40);
    let mut consecutive_misses = 0usize;
    let mut chunk = 0usize;
    while chunk < MAX_PROBE_CHUNKS {
        let start = chunk as u64 * DEFAULT_SLICE_SIZE;
        key_buf.clear();
        let _ = write!(
            key_buf,
            "{}{}bytes={}-{}",
            service,
            url,
            start,
            chunk_end(start)
        );
        let digest = calculate_md5_digest(&key_buf);
        if exists(digest) {
            hits.push(digest);
            consecutive_misses = 0;
        } else {
            consecutive_misses += 1;
            if consecutive_misses >= CONSECUTIVE_MISS_LIMIT {
                break;
            }
        }
        chunk += 1;
    }

    hits
}

/// Maximum run of CONSECUTIVE absent slices tolerated by the collect-all detection walk before it
/// concludes the object is fully enumerated and stops. A range-served object (Blizzard `/tpr/` TACT
/// archive, Riot bundle) occupies MANY contiguous 1 MiB slices, but partial eviction can punch
/// small holes; this gap tolerance lets the walk bridge those holes and keep counting later slices
/// instead of stopping at the first miss (which is what the old single-slice `.find_map` did).
///
/// PERFORMANCE: this only bounds the PRESENT case (a real multi-slice object). For a genuine
/// all-miss URL the walk stops after exactly this many index probes (no `no_range`/`noslice` hit and
/// chunk 0 absent ⇒ at most `CONSECUTIVE_MISS_LIMIT` index lookups, each O(1) on the index / one
/// `stat` on the fs path), so the incremental/eviction all-miss cost stays tightly bounded and never
/// walks to `MAX_PROBE_CHUNKS`.
#[allow(dead_code)]
pub const CONSECUTIVE_MISS_LIMIT: usize = 8;

/// Collect EVERY md5-hex cache-key hash that EXISTS in `index` for (service, url), covering the
/// WHOLE on-disk object regardless of the URL's logged `bytes_served`.
///
/// Detection's `bytes_served` is `MAX(LogEntries.BytesServed)` per (service, url); for a range-served
/// object each log row is ONE ~1 MiB range, so MAX can be a single slice and a size-derived chunk
/// count would undercount. Instead we walk chunk indices 0,1,2,… and collect every present slice,
/// stopping only after `CONSECUTIVE_MISS_LIMIT` consecutive absences (tolerating partial-eviction
/// holes) and never past `MAX_PROBE_CHUNKS` (the perf ceiling). Over-enumeration is SAFE because we
/// only retain candidates that ACTUALLY EXIST — non-existent slices are skipped, not counted.
///
/// Always probes the `no_range` + `::noslice` keys first (they have no byte range), then the ranged
/// slices. Returns the file_name-hash strings that hit (callers map them back to paths via `index`).
#[allow(dead_code)]
pub fn existing_cache_hashes_for_url<F>(service: &str, url: &str, mut exists: F) -> Vec<String>
where
    F: FnMut(&str) -> bool,
{
    let service = service_name_lowercase(service);
    let url = nginx_cache_uri(url).into_owned();
    let mut hits: Vec<String> = Vec::new();

    // Range-less keys first: `$cacheidentifier$uri` and `$cacheidentifier$uri::noslice`.
    let no_range = calculate_md5(&format!("{}{}", service, url));
    if exists(&no_range) {
        hits.push(no_range);
    }
    let noslice = calculate_md5(&format!("{}{}::noslice", service, url));
    if exists(&noslice) {
        hits.push(noslice);
    }

    // Ranged slices: walk 0,1,2,… collecting every present 1 MiB slice. Stop after a run of
    // CONSECUTIVE_MISS_LIMIT absences (bridges partial-eviction holes) or at MAX_PROBE_CHUNKS.
    let mut consecutive_misses = 0usize;
    let mut chunk = 0usize;
    while chunk < MAX_PROBE_CHUNKS {
        let start = chunk as u64 * DEFAULT_SLICE_SIZE;
        let hash = calculate_md5(&format!(
            "{}{}bytes={}-{}",
            service,
            url,
            start,
            chunk_end(start)
        ));
        if exists(&hash) {
            hits.push(hash);
            consecutive_misses = 0;
        } else {
            consecutive_misses += 1;
            if consecutive_misses >= CONSECUTIVE_MISS_LIMIT {
                break;
            }
        }
        chunk += 1;
    }

    hits
}

/// Filesystem twin of [`existing_cache_hashes_for_url`]: collect EVERY cache-file PATH that EXISTS
/// on disk for (service, url), covering the whole object via the same consecutive-miss-bounded walk.
/// Used by the incremental (no-index) detection path. Same coverage and safety guarantees.
#[allow(dead_code)]
pub fn existing_cache_paths_for_url(cache_dir: &Path, service: &str, url: &str) -> Vec<PathBuf> {
    let service = service_name_lowercase(service);
    let mut hits: Vec<PathBuf> = Vec::new();

    let no_range = calculate_cache_path_no_range(cache_dir, &service, url);
    if no_range.exists() {
        hits.push(no_range);
    }
    let noslice = calculate_cache_path_noslice(cache_dir, &service, url);
    if noslice.exists() {
        hits.push(noslice);
    }

    let mut consecutive_misses = 0usize;
    let mut chunk = 0usize;
    while chunk < MAX_PROBE_CHUNKS {
        let start = chunk as u64 * DEFAULT_SLICE_SIZE;
        let path = calculate_cache_path(cache_dir, &service, url, start, chunk_end(start));
        if path.exists() {
            hits.push(path);
            consecutive_misses = 0;
        } else {
            consecutive_misses += 1;
            if consecutive_misses >= CONSECUTIVE_MISS_LIMIT {
                break;
            }
        }
        chunk += 1;
    }

    hits
}

#[allow(dead_code)]
pub fn cache_path_candidates_for_probe(
    cache_dir: &Path,
    service: &str,
    url: &str,
    max_chunks: usize,
) -> Vec<PathBuf> {
    let service = service_name_lowercase(service);
    let mut paths = Vec::with_capacity(max_chunks + 2);
    paths.push(calculate_cache_path_no_range(cache_dir, &service, url));
    paths.push(calculate_cache_path_noslice(cache_dir, &service, url));

    for (start, end) in chunk_ranges_for_probe(max_chunks) {
        paths.push(calculate_cache_path(cache_dir, &service, url, start, end));
    }

    paths
}

#[allow(dead_code)]
pub fn cache_path_candidates_for_bytes(
    cache_dir: &Path,
    service: &str,
    url: &str,
    total_bytes: i64,
) -> Vec<PathBuf> {
    let service = service_name_lowercase(service);
    let chunk_ranges = chunk_ranges_for_total_bytes(total_bytes);
    let mut paths = Vec::with_capacity(chunk_ranges.len() + 2);
    paths.push(calculate_cache_path_no_range(cache_dir, &service, url));
    paths.push(calculate_cache_path_noslice(cache_dir, &service, url));

    for (start, end) in chunk_ranges {
        paths.push(calculate_cache_path(cache_dir, &service, url, start, end));
    }

    paths
}

fn chunk_ranges_for_probe(max_chunks: usize) -> Vec<(u64, u64)> {
    (0..max_chunks)
        .map(|chunk| {
            let start = chunk as u64 * DEFAULT_SLICE_SIZE;
            (start, chunk_end(start))
        })
        .collect()
}

fn chunk_ranges_for_total_bytes(total_bytes: i64) -> Vec<(u64, u64)> {
    if total_bytes <= 0 {
        return vec![(0, chunk_end(0))];
    }

    let mut ranges = Vec::new();
    let mut start = 0u64;
    let total_bytes = total_bytes as u64;
    while start < total_bytes {
        ranges.push((start, chunk_end(start)));
        start += DEFAULT_SLICE_SIZE;
    }

    ranges
}

fn chunk_end(start: u64) -> u64 {
    start + DEFAULT_SLICE_SIZE - 1
}

/// Removes empty directories from the given set, deepest-first, also pruning empty parents
/// one level up (stopping at `cache_dir`). Uses `safe_path_under_root` as a guard before
/// any removal. Returns the count of directories successfully removed.
#[allow(dead_code)]
pub(crate) fn cleanup_empty_directories(
    cache_dir: &Path,
    dirs_to_check: HashSet<PathBuf>,
) -> usize {
    let mut removed_count = 0;

    let mut sorted_dirs: Vec<PathBuf> = dirs_to_check.into_iter().collect();
    sorted_dirs.sort_by(|a, b| b.components().count().cmp(&a.components().count()));

    for dir in sorted_dirs {
        // Canonical-under-root guard: refuses symlinks, paths outside root.
        if let Err(e) = safe_path_under_root(cache_dir, &dir) {
            eprintln!("  skipping unsafe dir {}: {}", dir.display(), e);
            continue;
        }

        if let Ok(entries) = std::fs::read_dir(&dir) {
            if entries.count() == 0 {
                if std::fs::remove_dir(&dir).is_ok() {
                    removed_count += 1;

                    if let Some(parent) = dir.parent() {
                        if parent != cache_dir {
                            match safe_path_under_root(cache_dir, parent) {
                                Ok(_) => {
                                    if let Ok(parent_entries) = std::fs::read_dir(parent) {
                                        if parent_entries.count() == 0 {
                                            std::fs::remove_dir(parent).ok();
                                            removed_count += 1;
                                        }
                                    }
                                }
                                Err(e) => {
                                    eprintln!(
                                        "  skipping unsafe parent {}: {}",
                                        parent.display(),
                                        e
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    removed_count
}

/// Shape guard for a stored Xbox CDN fragment, mirroring the C# `XboxMappingService.IsValidFragment`.
/// Two distinct Xbox content-URL shapes must validate:
///   1. Delivery-Optimization CLIENT traffic (dl.delivery.mp, tagged `wsus`):
///      `/filestreamingservice/files/<GUID>` — the marker immediately followed by ONE canonical
///      8-4-4-4-12 hex GUID.
///   2. Prefill-daemon traffic pulled direct from assets1.xboxlive.com (tagged `xboxlive`):
///      `/<digit>/<guid>/<guid>/<version>.<guid>/<packageName>` — no marker, but >=2 GUIDs.
/// A fragment is usable if it matches EITHER shape. Everything else (empty / `/` / generic-wsus /
/// single-GUID paths) is rejected so a malformed DB row can never `contains()`-match unrelated
/// Windows Update / Xbox Live traffic and relabel it as a game. Pure byte scan (no regex) to keep
/// the ingest/speed hot paths allocation-free.
///
/// Shared by both `log_processor` (the primary canonicalizer) and `speed_tracker` so the two Xbox
/// pattern loaders apply ONE identical shape check — there is exactly one implementation. Kept
/// behaviorally byte-for-byte equivalent to the C# `XboxMappingService.IsValidFragment`.
#[allow(dead_code)]
pub fn is_valid_xbox_fragment(fragment: &str) -> bool {
    has_filestreaming_guid(fragment) || count_guids(fragment) >= 2
}

/// True if the fragment contains a `/filestreamingservice/files/` marker immediately followed by a
/// canonical 8-4-4-4-12 hex GUID (the Delivery-Optimization client object path).
///
/// The marker scan is ASCII-case-INSENSITIVE to mirror the C# `_filestreamingFragmentRegex`
/// (`RegexOptions.IgnoreCase`), so an uppercase `/FILESTREAMINGSERVICE/FILES/<GUID>` validates on
/// both sides. Still a pure byte scan (no allocation) on the ingest/speed hot paths.
#[allow(dead_code)]
fn has_filestreaming_guid(fragment: &str) -> bool {
    const MARKER: &[u8] = b"/filestreamingservice/files/";
    let bytes = fragment.as_bytes();
    if bytes.len() < MARKER.len() {
        return false;
    }

    // Scan every offset for a case-insensitive marker match; accept if any is followed by a
    // well-formed GUID. The marker bytes are ASCII so `eq_ignore_ascii_case` compares the letters
    // case-insensitively while the literal `/` separators compare exactly.
    let last = bytes.len() - MARKER.len();
    for i in 0..=last {
        let matches_marker = bytes[i..i + MARKER.len()]
            .iter()
            .zip(MARKER.iter())
            .all(|(b, m)| b.eq_ignore_ascii_case(m));
        if matches_marker && is_guid_at(bytes, i + MARKER.len()) {
            return true;
        }
    }
    false
}

/// Count the non-overlapping canonical 8-4-4-4-12 hex GUIDs in the fragment. REUSES `is_guid_at`
/// so the GUID shape is defined in exactly one place (no duplicated 8-4-4-4-12 definition). Matches
/// the C# `_guidRegex.Matches(fragment).Count` semantics (non-overlapping left-to-right scan).
#[allow(dead_code)]
fn count_guids(fragment: &str) -> usize {
    let bytes = fragment.as_bytes();
    let mut count = 0;
    let mut i = 0;
    while i < bytes.len() {
        if is_guid_at(bytes, i) {
            count += 1;
            i += 36; // advance past the 36-char GUID (non-overlapping)
        } else {
            i += 1;
        }
    }
    count
}

/// True if `bytes[at..]` begins with a canonical 8-4-4-4-12 lowercase/uppercase hex GUID.
#[allow(dead_code)]
fn is_guid_at(bytes: &[u8], at: usize) -> bool {
    // 8-4-4-4-12 hex with hyphens = 36 chars.
    const GUID_LEN: usize = 36;
    if at + GUID_LEN > bytes.len() {
        return false;
    }
    for (i, &b) in bytes[at..at + GUID_LEN].iter().enumerate() {
        let is_hyphen_pos = i == 8 || i == 13 || i == 18 || i == 23;
        if is_hyphen_pos {
            if b != b'-' {
                return false;
            }
        } else if !b.is_ascii_hexdigit() {
            return false;
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    #[test]
    fn http_range_parser_accepts_only_single_closed_inclusive_ranges() {
        assert_eq!(parse_http_byte_range(""), Some(ObservedByteRange::NoRange));
        assert_eq!(parse_http_byte_range("-"), Some(ObservedByteRange::NoRange));
        assert_eq!(
            parse_http_byte_range("bytes=0-0"),
            Some(ObservedByteRange::Inclusive { start: 0, end: 0 })
        );
        assert_eq!(
            parse_http_byte_range("bytes=1048575-1048576"),
            Some(ObservedByteRange::Inclusive {
                start: 1_048_575,
                end: 1_048_576,
            })
        );

        for malformed in [
            "bytes=",
            "bytes=-10",
            "bytes=10-",
            "bytes=10-9",
            "bytes=0-1,2-3",
            "bytes=+0-1",
            "bytes=0x0-1",
            "items=0-1",
            "bytes=18446744073709551616-18446744073709551617",
        ] {
            assert_eq!(parse_http_byte_range(malformed), None, "{malformed}");
        }
    }

    #[test]
    fn inclusive_alignment_handles_single_bytes_and_exact_boundaries() {
        assert_eq!(
            aligned_slice_ranges(&ObservedByteRange::Inclusive { start: 0, end: 0 }),
            Some(vec![(0, 1_048_575)])
        );
        assert_eq!(
            aligned_slice_ranges(&ObservedByteRange::Inclusive {
                start: 1_048_575,
                end: 1_048_576,
            }),
            Some(vec![(0, 1_048_575), (1_048_576, 2_097_151)])
        );
        assert_eq!(
            aligned_slice_ranges(&ObservedByteRange::Inclusive {
                start: u64::MAX,
                end: u64::MAX,
            }),
            Some(vec![(u64::MAX - (DEFAULT_SLICE_SIZE - 1), u64::MAX)]),
            "the final representable slice ends exactly at u64::MAX"
        );
    }

    #[test]
    fn blizzard_reference_vectors_map_to_exact_levels_2_2_paths() {
        let cache_dir = Path::new("/cache");
        let first = physical_slices_for_request(
            cache_dir,
            "blizzard",
            "/tpr/sc1live/data/c9/7e/c97e6071294fb69f542c57874d8433c5",
            "bytes=0-99699",
        )
        .expect("first vector");
        assert_eq!(first.slices.len(), 1);
        assert_eq!(
            first.slices[0]
                .exact_path
                .strip_prefix(cache_dir)
                .expect("relative path")
                .to_string_lossy()
                .replace('\\', "/"),
            "5f/6a/32fc9098be7edf9ae705aeb659836a5f"
        );

        let middle = physical_slices_for_request(
            cache_dir,
            "blizzard",
            "/tpr/zeus/data/c1/94/c1942d6badb10a911d3e617bac1e7be0",
            "bytes=26380013-32837297",
        )
        .expect("mid-object vector");
        let actual: Vec<String> = middle
            .slices
            .iter()
            .map(|slice| {
                slice
                    .exact_path
                    .strip_prefix(cache_dir)
                    .expect("relative path")
                    .to_string_lossy()
                    .replace('\\', "/")
            })
            .collect();
        assert_eq!(
            actual,
            [
                "0d/0e/6ace2ce71c04d335965ee2dc70550e0d",
                "69/c2/4b91e8e526be6e04d040aa199df9c269",
                "e0/4d/a9b3583eee3d4111af9d7c54fbf74de0",
                "5f/0b/d095893c2cdbb4ba37d00d9b12520b5f",
                "3c/c6/2cfb5b84a9fad3e5c0f62fbaee57c63c",
                "21/b7/90dd1966e01fa49a9d49f9200551b721",
                "90/a7/da189ae006772318633d6ebe04f1a790",
            ]
        );
        assert!(middle.slices.iter().all(|slice| {
            !matches!(
                slice.kind,
                CacheSliceKind::Ranged {
                    start: 0,
                    end: 1_048_575
                }
            )
        }));
    }

    #[test]
    fn no_range_mapping_keeps_no_range_and_noslice_alternatives() {
        let cache_dir = Path::new("/cache");
        let mapping =
            physical_slices_for_request(cache_dir, "Steam", "/depot/1/chunk/a?token=ignored", "-")
                .expect("no-range mapping");
        assert_eq!(mapping.normalized_uri, "/depot/1/chunk/a");
        assert_eq!(mapping.observed_range, ObservedByteRange::NoRange);
        assert_eq!(mapping.slices.len(), 2);
        assert_eq!(mapping.slices[0].kind, CacheSliceKind::NoRange);
        assert_eq!(mapping.slices[1].kind, CacheSliceKind::Noslice);
        assert_ne!(mapping.slices[0].exact_path, mapping.slices[1].exact_path);
    }

    #[test]
    fn sorted_sample_urls_matches_full_sort_and_dedup() {
        let urls = [
            "z", "beta", "alpha", "beta", "m", "aa", "delta", "alpha", "é", "c",
        ];

        for limit in 0..=(urls.len() + 1) {
            let mut expected: Vec<String> = urls.iter().map(|url| (*url).to_string()).collect();
            expected.sort();
            expected.dedup();
            expected.truncate(limit);

            let actual = sorted_sample_urls(urls.iter().copied(), limit);
            assert_eq!(actual, expected, "limit {limit}");
            assert!(actual.len() <= limit);
        }
    }

    /// The shared Xbox fragment shape guard used by BOTH `log_processor::load_xbox_patterns` and
    /// `speed_tracker::load_xbox_patterns`. It must reject the malformed / short / non-GUID
    /// fragments that the old `frag.len() > 1` check in speed_tracker would have wrongly accepted
    /// (and then `contains()`-matched generic Windows Update traffic), while still accepting a real
    /// `/filestreamingservice/files/<36-char-GUID>` fragment, case-insensitively on the hex.
    #[test]
    fn xbox_fragment_guard_rejects_malformed_accepts_real_guid() {
        const GUID: &str = "12345678-90ab-cdef-1234-567890abcdef";

        // Rejected: things the weak len>1 guard let through.
        assert!(!is_valid_xbox_fragment(""), "empty must be rejected");
        assert!(!is_valid_xbox_fragment("/"), "root must be rejected");
        assert!(
            !is_valid_xbox_fragment("/files/"),
            "short generic path must be rejected"
        );
        assert!(
            !is_valid_xbox_fragment("/c/msdownload/update/abc"),
            "generic wsus path must be rejected"
        );
        assert!(
            !is_valid_xbox_fragment("/filestreamingservice/files/not-a-guid"),
            "marker without a valid GUID must be rejected"
        );
        assert!(
            !is_valid_xbox_fragment("/filestreamingservice/files/12345678-90ab"),
            "truncated GUID must be rejected"
        );

        // Accepted: a well-formed fragment and one embedded in a full access-log URL, both
        // lower- and upper-case hex (speed_tracker / log_processor lowercase before matching).
        assert!(is_valid_xbox_fragment(&format!(
            "/filestreamingservice/files/{GUID}"
        )));
        assert!(is_valid_xbox_fragment(&format!(
            "http://assets1.xboxlive.com/filestreamingservice/files/{GUID}?P1=1"
        )));
        assert!(is_valid_xbox_fragment(
            "/filestreamingservice/files/ABCDEF12-3456-7890-ABCD-EF1234567890"
        ));
        // Uppercase MARKER (not just hex) with exactly one GUID — only the case-insensitive marker
        // branch can accept this, mirroring C#'s RegexOptions.IgnoreCase. SHARED with the C# test.
        assert!(is_valid_xbox_fragment(
            "/FILESTREAMINGSERVICE/FILES/12345678-90AB-CDEF-1234-567890ABCDEF"
        ));
    }

    /// Equivalence guarantee for the filename-keyed eviction probe: the hash candidates
    /// produced by `cache_hash_candidates_for_probe` must equal, element-for-element, the
    /// file_name components of the path candidates from `cache_path_candidates_for_probe`.
    #[test]
    fn hash_candidates_match_probe_path_file_names() {
        let cases: [(&str, &str); 4] = [
            (
                "steam",
                "/depot/881100/chunk/9b5af6c1d3e8a2f4b7c0d9e8f1a2b3c4d5e6f708",
            ),
            (
                "Epic",
                "/Builds/Org/CloudDir/ChunksV4/12/ABCD_0123456789abcdef.chunk",
            ),
            (
                "blizzard",
                "/tpr/bnt001/data/05/d6/05d6a7b8c9d0e1f2a3b4c5d6e7f8a9b0",
            ),
            // The `?range=1` query is now stripped by nginx_cache_uri (nginx keys on `$uri`,
            // not `$request_uri`). Both the hash form and the path form apply the SAME transform,
            // so this test still proves internal consistency — only the absolute md5 changed (to
            // the nginx-correct, query-stripped value).
            (
                "wsus",
                "/c/upgr/2021/01/windows10-kb5000802-x64_abc123.psf?range=1",
            ),
        ];
        let cache_dir = Path::new("/cache");

        for (service, url) in cases {
            let paths =
                cache_path_candidates_for_probe(cache_dir, service, url, DEFAULT_MAX_CHUNKS);
            let hashes = cache_hash_candidates_for_probe(service, url, DEFAULT_MAX_CHUNKS);

            assert_eq!(
                paths.len(),
                hashes.len(),
                "candidate count diverged for ({}, {})",
                service,
                url
            );

            for (index, (path, hash)) in paths.iter().zip(hashes.iter()).enumerate() {
                let file_name = path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .expect("probe path candidate must end in a UTF-8 file name");
                assert_eq!(
                    file_name, hash,
                    "candidate {} diverged for ({}, {})",
                    index, service, url
                );
            }
        }
    }

    /// The on-disk filename index relies on `calculate_md5` emitting 32-char lowercase hex.
    #[test]
    fn calculate_md5_is_lowercase_hex() {
        let hash = calculate_md5("steam/depot/881100/chunk/ABCDEF");
        assert_eq!(hash.len(), 32);
        assert!(
            hash.chars()
                .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()),
            "md5 hex must be lowercase: {}",
            hash
        );
    }

    /// The digest pipeline must be a bit-for-bit re-encoding of the hex pipeline: same
    /// candidates in the same order, where each u128 is exactly the parsed 32-hex name.
    #[test]
    fn digest_candidates_match_hex_candidates() {
        let service = "steam";
        let url = "/depot/1/chunk/aa?ver=2";
        let hex: Vec<String> =
            cache_hash_candidates_iter(service, url, DEFAULT_MAX_CHUNKS).collect();
        let digests: Vec<u128> =
            cache_digest_candidates_iter(service, url, DEFAULT_MAX_CHUNKS).collect();
        assert_eq!(hex.len(), digests.len());
        for (h, d) in hex.iter().zip(&digests) {
            assert_eq!(parse_cache_file_digest(h), Some(*d));
            assert_eq!(format!("{:032x}", d), *h);
        }
    }

    #[test]
    fn parse_cache_file_digest_rejects_non_hex_names() {
        assert_eq!(parse_cache_file_digest("tmp1234"), None);
        assert_eq!(parse_cache_file_digest(""), None);
        assert_eq!(
            parse_cache_file_digest("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"),
            None
        );
        // 31 and 33 chars must both fail even if all-hex.
        assert_eq!(
            parse_cache_file_digest("0123456789abcdef0123456789abcde"),
            None
        );
        assert_eq!(
            parse_cache_file_digest("0123456789abcdef0123456789abcdef0"),
            None
        );
        // Case-insensitive accept, mirroring the Windows casing normalization.
        assert_eq!(
            parse_cache_file_digest("00000000000000000000000000000ABC"),
            Some(0xabc)
        );
    }

    #[test]
    fn strict_cache_file_digest_requires_lowercase_exact_levels_2_2_shape() {
        let root = Path::new("/cache");
        let hash = "0123456789abcdef0123456789abcdef";
        let exact = root.join("ef").join("cd").join(hash);
        assert_eq!(
            strict_cache_file_digest(root, &exact),
            parse_cache_file_digest(hash)
        );
        for invalid in [
            root.join("EF").join("cd").join(hash),
            root.join("ef").join("CD").join(hash),
            root.join("ef").join("cd").join(hash.to_uppercase()),
            root.join("00").join("cd").join(hash),
            root.join("ef").join("00").join(hash),
            root.join("extra").join("ef").join("cd").join(hash),
            root.join(hash),
        ] {
            assert_eq!(
                strict_cache_file_digest(root, &invalid),
                None,
                "{}",
                invalid.display()
            );
        }
    }

    /// cache_path_for_digest must reproduce calculate_cache_path's layout exactly for the
    /// same key: same subdirectories, same file name.
    #[test]
    fn cache_path_for_digest_matches_calculate_cache_path() {
        let cache_dir = Path::new("/cache/cache");
        let service = "steam";
        let url = "/depot/1/chunk/aa";
        let by_key = calculate_cache_path(cache_dir, service, url, 0, DEFAULT_SLICE_SIZE - 1);
        let digest = calculate_md5_digest(&format!(
            "{}{}bytes={}-{}",
            service,
            nginx_cache_uri(url),
            0,
            DEFAULT_SLICE_SIZE - 1
        ));
        assert_eq!(cache_path_for_digest(cache_dir, digest), by_key);
    }

    /// The digest walk must agree with the hex walk over an arbitrary membership set (same
    /// hits, same order), proving the eviction/detect index conversion changes nothing.
    #[test]
    fn existing_digests_walk_matches_hex_walk() {
        let service = "blizzard";
        let url = "/tpr/x//archive%41?q=1";
        let member: std::collections::HashSet<String> =
            cache_hash_candidates_for_probe(service, url, 12)
                .into_iter()
                .skip(1)
                .step_by(3)
                .collect();
        let hex_hits = existing_cache_hashes_for_url(service, url, |h| member.contains(h));
        let digest_hits = existing_cache_digests_for_url(service, url, |d| {
            member.contains(&format!("{:032x}", d))
        });
        let hex_as_digests: Vec<u128> = hex_hits
            .iter()
            .map(|h| parse_cache_file_digest(h).expect("hex hit must parse"))
            .collect();
        assert_eq!(hex_as_digests, digest_hits);
        assert!(
            !digest_hits.is_empty(),
            "membership set must produce hits for the gate to mean anything"
        );
    }

    /// The probe-list iterator and the eager Vec form must agree (the scan probes via the
    /// lazy iterator while the equivalence test above exercises the Vec form).
    #[test]
    fn hash_candidates_iter_matches_vec_form() {
        let collected: Vec<String> =
            cache_hash_candidates_iter("steam", "/depot/1/chunk/aa", DEFAULT_MAX_CHUNKS).collect();
        let eager =
            cache_hash_candidates_for_probe("steam", "/depot/1/chunk/aa", DEFAULT_MAX_CHUNKS);
        assert_eq!(collected, eager);
        // no_range + noslice + MAX_CHUNKS ranged slices.
        assert_eq!(collected.len(), DEFAULT_MAX_CHUNKS + 2);
    }

    /// The probe must include the `@noslice` location's key:
    /// `md5(lower(service) + nginx_cache_uri(url) + "::noslice")` — with NO byte range — so
    /// noslice-cached content is matched by detect/evict/remove. Pinned at the second position
    /// (immediately after the no-range candidate, before the chunk slices).
    #[test]
    fn hash_candidates_contains_noslice_key() {
        let service = "epicgames";
        let url = "/Builds/Org/CloudDir/ChunksV4/12/ABCD_0123456789abcdef.chunk";

        let expected_noslice = calculate_md5(&format!(
            "{}{}::noslice",
            service_name_lowercase(service),
            nginx_cache_uri(url)
        ));

        let hashes = cache_hash_candidates_for_probe(service, url, DEFAULT_MAX_CHUNKS);
        assert!(
            hashes.contains(&expected_noslice),
            "probe candidates must contain the ::noslice key {} for ({}, {})",
            expected_noslice,
            service,
            url
        );
        // Order contract: [no_range, noslice, chunk0, ...] — noslice is the second candidate.
        let expected_no_range = calculate_md5(&format!(
            "{}{}",
            service_name_lowercase(service),
            nginx_cache_uri(url)
        ));
        assert_eq!(
            hashes[0], expected_no_range,
            "first candidate must be no_range"
        );
        assert_eq!(
            hashes[1], expected_noslice,
            "second candidate must be ::noslice"
        );
    }

    /// `probe_chunks_for_bytes` sizes the probe chunk count from the URL's byte size, never
    /// below the `DEFAULT_MAX_CHUNKS` floor and never above the `MAX_PROBE_CHUNKS` ceiling.
    #[test]
    fn probe_chunks_for_bytes_clamps_to_floor_and_ceiling() {
        // 0 / unknown / negative size → the old flat floor (never less coverage than before).
        assert_eq!(probe_chunks_for_bytes(0), DEFAULT_MAX_CHUNKS);
        assert_eq!(probe_chunks_for_bytes(-1), DEFAULT_MAX_CHUNKS);

        // A small object (well under the floor's coverage) still probes the full floor.
        assert_eq!(probe_chunks_for_bytes(1), DEFAULT_MAX_CHUNKS);
        assert_eq!(
            probe_chunks_for_bytes(DEFAULT_SLICE_SIZE as i64),
            DEFAULT_MAX_CHUNKS
        );
        // Exactly at the floor boundary: 100 MiB → 100 slices == floor.
        assert_eq!(
            probe_chunks_for_bytes(DEFAULT_MAX_CHUNKS as i64 * DEFAULT_SLICE_SIZE as i64),
            DEFAULT_MAX_CHUNKS
        );

        // A large object (between floor and ceiling) → ceil(bytes / slice), exceeding the
        // old flat 100. 1 GiB = 1024 MiB → 1024 slices.
        let one_gib = 1024i64 * DEFAULT_SLICE_SIZE as i64;
        assert_eq!(probe_chunks_for_bytes(one_gib), 1024);
        assert!(probe_chunks_for_bytes(one_gib) > DEFAULT_MAX_CHUNKS);

        // ceil rounding: floor + half a slice rounds UP to floor + 1.
        let just_over_floor =
            DEFAULT_MAX_CHUNKS as i64 * DEFAULT_SLICE_SIZE as i64 + DEFAULT_SLICE_SIZE as i64 / 2;
        assert_eq!(
            probe_chunks_for_bytes(just_over_floor),
            DEFAULT_MAX_CHUNKS + 1
        );

        // A huge (e.g. mis-attributed 100 GB) size is capped at MAX_PROBE_CHUNKS so the
        // all-miss eviction scan stays bounded.
        let one_hundred_gb = 100i64 * 1024 * DEFAULT_SLICE_SIZE as i64;
        assert_eq!(probe_chunks_for_bytes(one_hundred_gb), MAX_PROBE_CHUNKS);
        // Exactly at the ceiling boundary stays at the ceiling.
        assert_eq!(
            probe_chunks_for_bytes(MAX_PROBE_CHUNKS as i64 * DEFAULT_SLICE_SIZE as i64),
            MAX_PROBE_CHUNKS
        );
    }

    use std::borrow::Cow;

    #[test]
    fn nginx_cache_uri_strips_query_string() {
        assert_eq!(nginx_cache_uri("/a/b?x=1"), "/a/b");
        assert_eq!(nginx_cache_uri("/a/b?x=1&y=2"), "/a/b");
        // A bare trailing `?` yields the empty query → path only.
        assert_eq!(nginx_cache_uri("/a/b?"), "/a/b");
    }

    #[test]
    fn nginx_cache_uri_percent_decodes() {
        assert_eq!(nginx_cache_uri("/a%20b"), "/a b"); // %20 -> space
        assert_eq!(nginx_cache_uri("/a%2Fb"), "/a/b"); // %2F -> '/'
        assert_eq!(nginx_cache_uri("/a%2fb"), "/a/b"); // lowercase hex
                                                       // Query stripped THEN decoded: the decode operates on the path only.
        assert_eq!(nginx_cache_uri("/a%20b?token=%2F"), "/a b");
    }

    #[test]
    fn nginx_cache_uri_collapses_double_slashes_idempotent() {
        assert_eq!(nginx_cache_uri("/a//b"), "/a/b");
        assert_eq!(nginx_cache_uri("/a///b//c"), "/a/b/c");
        // Already-clean path is unchanged (idempotent collapse).
        assert_eq!(nginx_cache_uri("/a/b/c"), "/a/b/c");
    }

    #[test]
    fn nginx_cache_uri_resolves_dot_segments() {
        assert_eq!(nginx_cache_uri("/a/./b"), "/a/b");
        assert_eq!(nginx_cache_uri("/a/b/../c"), "/a/c");
        assert_eq!(nginx_cache_uri("/a/b/../../c"), "/c");
        // `..` cannot escape above the root.
        assert_eq!(nginx_cache_uri("/../a"), "/a");
    }

    #[test]
    fn nginx_cache_uri_borrowed_fast_path_for_clean_url() {
        // A clean depot path needs no transform → returned BORROWED (zero allocation in the hot
        // rayon'd probe loop). This is the safety mechanism that bounds the fix's blast radius.
        let clean = "/depot/881100/chunk/9b5af6c1d3e8a2f4b7c0d9e8f1a2b3c4d5e6f708";
        assert!(matches!(nginx_cache_uri(clean), Cow::Borrowed(_)));
        // A URL that needs ANY transform is Owned.
        assert!(matches!(nginx_cache_uri("/a/b?x=1"), Cow::Owned(_)));
        assert!(matches!(nginx_cache_uri("/a//b"), Cow::Owned(_)));
        assert!(matches!(nginx_cache_uri("/a%20b"), Cow::Owned(_)));
        assert!(matches!(nginx_cache_uri("/a/./b"), Cow::Owned(_)));
    }

    /// REAL-SAMPLE REGRESSION GATE — pins the nginx-correct md5 key against a known on-disk file.
    ///
    /// This is `#[ignore]`'d so CI stays green until a real sample is pasted in. To enable it:
    ///
    ///   1. On the server, run `rust-processor/scripts/verify_cache_key.py --sample-evicted 25`
    ///      (or pass an explicit `--service`/`--url`). It prints, per (service, url), which URL
    ///      variant produced an md5 whose file EXISTS under /data/cache/cache. The WINNING variant
    ///      is the confirmed nginx derivation, and the printed `md5` is the on-disk filename.
    ///   2. Replace the three placeholders below with that real (service, url, expected_md5).
    ///   3. Drop the `#[ignore]` (or run `cargo test -- --ignored`) to assert the SHIPPED
    ///      derivation reproduces the real on-disk filename.
    ///
    /// Do NOT fabricate a hash — a fabricated value would make this a tautology and defeat the
    /// purpose of pinning against ground truth.
    #[test]
    #[ignore = "fill in a real (service, url, md5) captured by scripts/verify_cache_key.py first"]
    fn nginx_key_matches_real_on_disk_file() {
        // ---- PASTE REAL SAMPLE HERE (from verify_cache_key.py) ----
        let service: &str = "REPLACE_ME_service"; // e.g. "steam"
        let url: &str = "REPLACE_ME_url"; // e.g. "/depot/123/chunk/abc?token=xyz"
        let expected_md5: &str = "REPLACE_ME_32_char_md5"; // the on-disk filename the script found
                                                           // -----------------------------------------------------------

        // Reproduce nginx's key the SHIPPED way: lower(service) + $uri + slice range, then md5.
        let uri = nginx_cache_uri(url);
        let svc = service_name_lowercase(service);

        // Try the no-range key and the first-slice key; one of them must match the on-disk file.
        let no_range = calculate_md5(&format!("{}{}", svc, uri));
        let first_slice = calculate_md5(&format!("{}{}bytes=0-1048575", svc, uri));

        assert!(
            no_range == expected_md5 || first_slice == expected_md5,
            "nginx-correct key did not reproduce the on-disk filename {} (got no_range={}, first_slice={}) for service={} url={}",
            expected_md5,
            no_range,
            first_slice,
            service,
            url
        );
    }

    /// §12 Q1 REMOVAL all-slice gate. A range-served object (Xbox /filestreamingservice/files/<GUID>,
    /// Blizzard /tpr/ archive, Riot bundle) is logged as many ~1 MiB ranges under ONE url, so
    /// `MAX(BytesServed)` ≈ a single slice. The removal bins used to size candidates from that max
    /// (`cache_path_candidates_for_bytes`) and so generated ~1 ranged candidate → deleted only slice
    /// 0 and orphaned slices 1..N. The fix swaps removal to `existing_cache_paths_for_url`, which
    /// stat-walks EVERY on-disk slice. This writes real slice files for an Xbox wsus object (cache
    /// hash uses LogEntries.Service = `wsus`, per the identity split) and asserts the all-slice walk
    /// returns them all, while the old size-derived list would have under-covered.
    #[test]
    fn removal_walk_enumerates_all_range_slices_xbox_wsus() {
        let dir = tempfile::tempdir().unwrap();
        let cache_dir = dir.path();
        // Xbox content is cache-hashed under the `wsus` service (the cache-service split).
        let service = "wsus";
        let url = "/filestreamingservice/files/2c1f8b3a-0d4e-4a5b-9c6d-7e8f9a0b1c2d";
        let n_slices = 64usize; // a multi-slice range object

        // Materialize the no_range location + the first `n_slices` ranged slices on disk.
        let mut written: Vec<PathBuf> = Vec::new();
        written.push(calculate_cache_path_no_range(cache_dir, service, url));
        for chunk in 0..n_slices {
            let start = chunk as u64 * DEFAULT_SLICE_SIZE;
            written.push(calculate_cache_path(
                cache_dir,
                service,
                url,
                start,
                chunk_end(start),
            ));
        }
        for path in &written {
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, b"x").unwrap();
        }

        // The all-slice existence walk (what removal now uses) must find EVERY written slice.
        let found = existing_cache_paths_for_url(cache_dir, service, url);
        assert_eq!(
            found.len(),
            written.len(),
            "all-slice walk must enumerate every on-disk slice for removal (no orphans)"
        );

        // Control: the OLD size-derived candidate list, sized from a single ~1 MiB range
        // (MAX(BytesServed) for a range object), would have produced far fewer ranged candidates —
        // demonstrating the §12 Q1 under-delete the walk fixes.
        let one_range = DEFAULT_SLICE_SIZE as i64;
        let old_candidates = cache_path_candidates_for_bytes(cache_dir, service, url, one_range);
        assert!(
            old_candidates.len() < written.len(),
            "size-derived candidates ({}) must be fewer than the {} real slices — the under-delete §12 Q1 fixes",
            old_candidates.len(),
            written.len()
        );
    }
}
