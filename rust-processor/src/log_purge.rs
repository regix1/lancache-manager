// Shared log-purge helper used by `cache_steam_remove`, `cache_purge_log_entries`
// and `cache_corruption`.
//
// This module contains `remove_log_entries_for_game`, which walks all nginx
// access-log sources under a log directory (plain + gzip + zstd), rewrites each
// file to exclude lines matching the given URL set or depot-ID set, and atomically
// replaces the originals.
//
// It was extracted from `cache_steam_remove.rs` so that both the single-game
// remover and the bulk evicted purge binary can share the exact same logic
// without duplication.
//
// Performance shape (P2 of the rust-perf plan):
// 1. An Aho-Corasick prefilter over the raw line BYTES decides whether a line
//    can possibly match before the expensive 9-capture regex parse runs. The
//    prefilter may only ever produce false POSITIVES (the full parse remains
//    the source of truth) — lines containing "//" bypass the prefilter and are
//    always full-parsed, because `LogParser::normalize_url` collapses slashes
//    and the normalized target URL may not be a literal substring of the raw line.
// 2. Each file gets a read-only scan pass first; files with zero confirmed
//    matches are left completely untouched (no temp file, no recompression).
// 3. Hot loops read raw bytes (`read_until`) instead of validated Strings.
// 4. Gzip rewrite output uses `Compression::fast()` (still valid gzip; the
//    rotated logs are archival, slightly larger output is accepted).

use anyhow::{Context, Result};
use chrono::{DateTime, Utc};
use std::borrow::Cow;
use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io::{BufWriter, Write as IoWrite};
use std::path::{Path, PathBuf};

use aho_corasick::AhoCorasick;
use flate2::write::GzEncoder;
use flate2::Compression;
use serde::{Deserialize, Serialize};
use tempfile::NamedTempFile;

use crate::cache_utils;
use crate::log_layout::{discover_log_sources, kind_for_stem, logical_stem, SourceKind};
use crate::log_reader::LogFileReader;
use crate::models::LogEntry;
use crate::parser::{parse_log_line, LogParser};
use crate::parser_http_detailed::HttpDetailedParser;
use crate::service_utils;

const LOG_CHECK_ENV: &str = "LANCACHE_LOG_CHECK";
const LOG_RESULT_ENV: &str = "LANCACHE_LOG_RESULT";

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FileIdentity {
    pub first: u64,
    pub second: u64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PublicationExpectation {
    target_path: PathBuf,
    original_identity: FileIdentity,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PublicationCheck {
    valid: bool,
    files: Vec<PublicationExpectation>,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct PublicationRecord {
    target_path: PathBuf,
    original_identity: FileIdentity,
    temporary_identity: Option<FileIdentity>,
    published_identity: Option<FileIdentity>,
    changed: bool,
    deleted: bool,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct PublicationResult {
    success: bool,
    files: Vec<PublicationRecord>,
}

#[cfg(windows)]
pub fn file_identity(path: &Path) -> Result<FileIdentity> {
    let file = File::open(path)
        .with_context(|| format!("failed to open identity handle for {}", path.display()))?;
    file_identity_of(&file)
        .with_context(|| format!("failed to read identity for {}", path.display()))
}

#[cfg(windows)]
pub fn file_identity_of(file: &File) -> Result<FileIdentity> {
    use std::os::windows::io::AsRawHandle;

    #[repr(C)]
    struct ByHandleFileInformation {
        file_attributes: u32,
        creation_time: [u32; 2],
        last_access_time: [u32; 2],
        last_write_time: [u32; 2],
        volume_serial_number: u32,
        file_size_high: u32,
        file_size_low: u32,
        number_of_links: u32,
        file_index_high: u32,
        file_index_low: u32,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetFileInformationByHandle(
            file: *mut std::ffi::c_void,
            information: *mut ByHandleFileInformation,
        ) -> i32;
    }

    let mut information = std::mem::MaybeUninit::<ByHandleFileInformation>::uninit();
    let succeeded = unsafe {
        GetFileInformationByHandle(file.as_raw_handle().cast(), information.as_mut_ptr())
    };
    if succeeded == 0 {
        return Err(std::io::Error::last_os_error()).context("failed to read file identity");
    }
    let information = unsafe { information.assume_init() };
    Ok(FileIdentity {
        first: information.volume_serial_number as u64,
        second: ((information.file_index_high as u64) << 32) | information.file_index_low as u64,
    })
}

#[cfg(unix)]
pub fn file_identity(path: &Path) -> Result<FileIdentity> {
    use std::os::unix::fs::MetadataExt;

    let attributes = std::fs::metadata(path)
        .with_context(|| format!("failed to read identity for {}", path.display()))?;
    Ok(FileIdentity {
        first: attributes.dev(),
        second: attributes.ino(),
    })
}

#[cfg(unix)]
pub fn file_identity_of(file: &File) -> Result<FileIdentity> {
    use std::os::unix::fs::MetadataExt;

    let attributes = file.metadata().context("failed to read file identity")?;
    Ok(FileIdentity {
        first: attributes.dev(),
        second: attributes.ino(),
    })
}

#[cfg(not(any(windows, unix)))]
pub fn file_identity(_path: &Path) -> Result<FileIdentity> {
    anyhow::bail!("file identity is unsupported on this platform")
}

#[cfg(not(any(windows, unix)))]
pub fn file_identity_of(_file: &File) -> Result<FileIdentity> {
    anyhow::bail!("file identity is unsupported on this platform")
}

fn read_publication_check() -> Result<Option<PublicationCheck>> {
    let Some(check_path) = std::env::var_os(LOG_CHECK_ENV) else {
        return Ok(None);
    };
    let bytes = std::fs::read(&check_path).with_context(|| {
        format!(
            "failed to read log publication check {}",
            Path::new(&check_path).display()
        )
    })?;
    let check: PublicationCheck =
        serde_json::from_slice(&bytes).context("failed to parse log publication check")?;
    if !check.valid {
        anyhow::bail!("log publication check was invalidated")
    }
    Ok(Some(check))
}

pub fn publication_expectation(path: &Path) -> Result<Option<PublicationExpectation>> {
    let Some(check) = read_publication_check()? else {
        return Ok(None);
    };

    let canonical = path
        .canonicalize()
        .with_context(|| format!("failed to canonicalize log target {}", path.display()))?;
    let expected = check
        .files
        .into_iter()
        .find(|file| {
            file.target_path
                .canonicalize()
                .map(|candidate| candidate == canonical)
                .unwrap_or(false)
        })
        .with_context(|| {
            format!(
                "log publication check did not include {}",
                canonical.display()
            )
        })?;
    let actual = file_identity(path)?;
    if actual != expected.original_identity {
        anyhow::bail!(
            "log target identity changed before publication: {}",
            path.display()
        )
    }
    Ok(Some(expected))
}

/// Checks that the host's check bound every one of `paths` with its current identity. The check is read and its
/// entries canonicalized once; a per-file `publication_expectation` re-reads the whole check for each file, which
/// grows with the square of a series' rotation count. With no paths nothing is read, as with no per-file call.
pub fn check_publication_targets(paths: &[&Path]) -> Result<()> {
    if paths.is_empty() {
        return Ok(());
    }
    let Some(check) = read_publication_check()? else {
        return Ok(());
    };
    // The first entry for a target wins, as the per-file lookup's `find` does.
    let mut bound: HashMap<PathBuf, FileIdentity> = HashMap::new();
    for file in check.files {
        if let Ok(target) = file.target_path.canonicalize() {
            bound.entry(target).or_insert(file.original_identity);
        }
    }
    for path in paths {
        let canonical = path
            .canonicalize()
            .with_context(|| format!("failed to canonicalize log target {}", path.display()))?;
        let expected = bound.get(&canonical).with_context(|| {
            format!(
                "log publication check did not include {}",
                canonical.display()
            )
        })?;
        if file_identity(path)? != *expected {
            anyhow::bail!(
                "log target identity changed before publication: {}",
                path.display()
            )
        }
    }
    Ok(())
}

fn write_publication_result(records: Vec<PublicationRecord>, success: bool) -> Result<()> {
    let Some(result_path) = std::env::var_os(LOG_RESULT_ENV) else {
        return Ok(());
    };
    let result_path = PathBuf::from(result_path);
    let parent = result_path
        .parent()
        .context("log publication result has no parent directory")?;
    let mut temporary = NamedTempFile::new_in(parent)?;
    serde_json::to_writer(
        &mut temporary,
        &PublicationResult {
            success,
            files: records,
        },
    )?;
    temporary.as_file_mut().flush()?;
    temporary
        .persist(&result_path)
        .map_err(|error| error.error)
        .with_context(|| format!("failed to publish log result {}", result_path.display()))?;
    Ok(())
}

/// Completes the publication of a removal that deleted whole files itself (one service's
/// per-service series) before any rewrite ran. The host's check expects one record per file it
/// bound: the records a rewrite already published stay, each deleted file is added as deleted,
/// and every other checked file as unchanged, which holds only while its identity still matches.
/// A checked file of `service` that is already gone counts as deleted once no file of its series
/// is left beside it. A checked file of another series that is gone stays recorded unchanged and
/// does not fail the publication; the host reads that series again.
pub fn publish_deleted_files(
    deleted: &[FileIdentity],
    deletes_succeeded: bool,
    service: &str,
) -> Result<()> {
    let (Some(check_path), Some(result_path)) = (
        std::env::var_os(LOG_CHECK_ENV),
        std::env::var_os(LOG_RESULT_ENV),
    ) else {
        return Ok(());
    };
    let check: PublicationCheck =
        serde_json::from_slice(&std::fs::read(&check_path).with_context(|| {
            format!(
                "failed to read log publication check {}",
                Path::new(&check_path).display()
            )
        })?)
        .context("failed to parse log publication check")?;
    let mut success = deletes_succeeded && check.valid;
    let mut records = Vec::new();
    let result_path = PathBuf::from(result_path);
    if result_path.exists() {
        let published: PublicationResult = serde_json::from_slice(
            &std::fs::read(&result_path).context("failed to read log publication result")?,
        )
        .context("failed to parse log publication result")?;
        success &= published.success;
        records = published.files;
    }
    let removed_service = SourceKind::Service(service.to_string());
    for expected in check.files {
        if records
            .iter()
            .any(|record| record.original_identity == expected.original_identity)
        {
            continue;
        }
        let gone = matches!(
            std::fs::symlink_metadata(&expected.target_path),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound
        );
        let of_removed_service = expected
            .target_path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(logical_stem)
            .is_some_and(|stem| kind_for_stem(&stem) == removed_service);
        // A file of the removed service that something outside the app deleted first: with no file of
        // that series left beside it, its lines are gone too. A rotation that renamed or compressed it
        // leaves a series member that still holds its lines, so that run keeps failing.
        let was_deleted = deleted.contains(&expected.original_identity)
            || (gone
                && of_removed_service
                && !discover_log_sources(
                    expected
                        .target_path
                        .parent()
                        .context("log publication target has no parent directory")?,
                )?
                .sources
                .iter()
                .any(|source| source.kind == removed_service));
        // A file of another series that something outside the app deleted was never this removal's to
        // delete: it stays recorded unchanged without failing the removal, and the host reads that
        // series again from its first line.
        if !was_deleted && (!gone || of_removed_service) {
            success &= file_identity(&expected.target_path).ok().as_ref()
                == Some(&expected.original_identity);
        }
        records.push(PublicationRecord {
            published_identity: if was_deleted {
                None
            } else {
                Some(expected.original_identity.clone())
            },
            target_path: expected.target_path,
            original_identity: expected.original_identity,
            temporary_identity: None,
            changed: was_deleted,
            deleted: was_deleted,
        });
    }
    write_publication_result(records, success)
}

/// Byte-level prefilter for removal candidates. A line that fails
/// `is_candidate` can never match the removal predicate and is written
/// through without UTF-8 validation or regex parsing.
pub struct RemovalPrefilter {
    /// None when the pattern set is too large to index: every line is then a candidate and the
    /// full parse decides alone.
    automaton: Option<AhoCorasick>,
}

/// One exact stored corruption observation. Matching every field keeps log cleanup inside the
/// immutable evidence window; a URL match alone is intentionally insufficient.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct ExactLogObservation {
    pub service: String,
    pub raw_url: String,
    pub timestamp: DateTime<Utc>,
    pub client_ip: String,
    pub method: String,
    pub http_status: i32,
    pub bytes_served: i64,
    pub cache_status: String,
    pub raw_range: Option<String>,
}

/// Hash-set matcher and safe raw-line prefilter derived from exact observations.
pub struct ExactLogMatcher {
    observations: HashSet<ExactLogObservation>,
}

impl ExactLogMatcher {
    pub fn new<I>(observations: I) -> Self
    where
        I: IntoIterator<Item = ExactLogObservation>,
    {
        Self {
            observations: observations.into_iter().collect(),
        }
    }

    pub fn prefilter(&self) -> Result<RemovalPrefilter> {
        RemovalPrefilter::new(
            self.observations
                .iter()
                .map(|observation| observation.raw_url.as_bytes()),
        )
    }

    pub fn matches(&self, entry: &LogEntry) -> bool {
        let raw_range = (!entry.http_range.is_empty()).then_some(entry.http_range.clone());
        self.observations.contains(&ExactLogObservation {
            service: entry.service.clone(),
            raw_url: entry.raw_url.clone(),
            timestamp: DateTime::<Utc>::from_naive_utc_and_offset(entry.timestamp, Utc),
            client_ip: entry.client_ip.clone(),
            method: entry.method.clone(),
            http_status: entry.status_code,
            bytes_served: entry.bytes_served,
            cache_status: entry.cache_status.clone(),
            raw_range,
        })
    }

    #[cfg(test)]
    fn len(&self) -> usize {
        self.observations.len()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LogRewriteOutcome {
    pub lines_removed: u64,
    pub permission_errors: usize,
    pub other_errors: usize,
    /// Removed-line count per source stem (`access.log`, `steam-access.log`, ...), summed
    /// across the stem's whole rotation series - the same series the stored ingestion
    /// positions count. The caller must subtract these from the saved positions: a purge
    /// deletes lines the position has already counted past, so an unadjusted position
    /// points that many lines into not-yet-ingested content and the next incremental run
    /// silently skips it.
    pub lines_removed_by_stem: HashMap<String, u64>,
    /// The subset of `lines_removed_by_stem` whose series index (position within the stem's
    /// rotation series, oldest file first) sits BELOW the stem's saved ingestion position -
    /// the lines the reader had already consumed. Only these move the position back; a line
    /// removed ahead of the position was never read, so the position must not move for it.
    /// When no positions were supplied to the rewrite, this equals `lines_removed_by_stem`
    /// (treat everything as read; the resulting over-subtraction only replays lines, which
    /// ingestion dedupe discards, while under-subtraction skips unread lines for good).
    pub lines_removed_before_position_by_stem: HashMap<String, u64>,
}

/// Owns the concrete output encoder so compressed streams can be finalized before the
/// temporary file is persisted. A trait-object `flush()` is not sufficient for zstd because
/// it does not write the end-of-frame marker.
enum LogRewriteWriter {
    Plain(BufWriter<File>),
    Gzip(GzEncoder<BufWriter<File>>),
    Zstd(zstd::Encoder<'static, BufWriter<File>>),
}

type LogLinePredicate<'a> = dyn Fn(&[u8], &SourceKind) -> bool + Send + Sync + 'a;

impl IoWrite for LogRewriteWriter {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        match self {
            Self::Plain(writer) => writer.write(buf),
            Self::Gzip(writer) => writer.write(buf),
            Self::Zstd(writer) => writer.write(buf),
        }
    }

    fn flush(&mut self) -> std::io::Result<()> {
        match self {
            Self::Plain(writer) => writer.flush(),
            Self::Gzip(writer) => writer.flush(),
            Self::Zstd(writer) => writer.flush(),
        }
    }
}

impl LogRewriteWriter {
    fn finish(self) -> Result<()> {
        match self {
            Self::Plain(mut writer) => writer.flush()?,
            Self::Gzip(writer) => writer.finish()?.flush()?,
            Self::Zstd(writer) => writer.finish()?.flush()?,
        }
        Ok(())
    }
}

impl RemovalPrefilter {
    /// Builds the prefilter from literal byte patterns (exact URL strings and
    /// `/depot/{id}/` fragments). An empty pattern set is valid and matches
    /// nothing, which mirrors a removal predicate that can never fire.
    pub fn new<I, P>(patterns: I) -> Result<Self>
    where
        I: IntoIterator<Item = P>,
        P: AsRef<[u8]>,
    {
        // The automaton costs a multiple of the total pattern bytes in memory and walks a table
        // of that size for every line, so past this budget it is slower than the parse it exists
        // to skip and it is what the kernel kills first: a bulk purge handed 3.7 M URLs exited 137
        // while building it. Above the budget every line takes the full-parse path instead.
        const MAX_INDEXED_PATTERN_BYTES: usize = 8 * 1024 * 1024;
        let patterns: Vec<P> = patterns.into_iter().collect();
        let pattern_bytes: usize = patterns.iter().map(|pattern| pattern.as_ref().len()).sum();
        if pattern_bytes > MAX_INDEXED_PATTERN_BYTES {
            return Ok(Self { automaton: None });
        }
        let automaton =
            AhoCorasick::new(patterns).context("Failed to build Aho-Corasick removal prefilter")?;
        Ok(Self {
            automaton: Some(automaton),
        })
    }

    /// Returns true when the line might match the removal predicate and must
    /// take the full-parse path. Lines containing "//" always return true:
    /// `normalize_url` collapses consecutive slashes, so the stored target URL
    /// may not appear literally in the raw line (false-negative hazard).
    pub(crate) fn is_candidate(&self, raw_line: &[u8]) -> bool {
        let json_record = raw_line
            .iter()
            .find(|byte| !byte.is_ascii_whitespace())
            .is_some_and(|byte| *byte == b'{');
        match &self.automaton {
            Some(automaton) => {
                json_record || line_contains_double_slash(raw_line) || automaton.is_match(raw_line)
            }
            None => true,
        }
    }
}

fn line_contains_double_slash(line: &[u8]) -> bool {
    line.windows(2).any(|pair| pair == b"//")
}

/// Full decision for one raw line: prefilter first, then (for candidates only)
/// UTF-8 conversion + regex parse + exact predicate confirmation.
fn line_should_be_removed<F>(
    raw_line: &[u8],
    prefilter: &RemovalPrefilter,
    cachelog: &LogParser,
    detailed: &HttpDetailedParser,
    source_kind: &SourceKind,
    should_remove_entry: &F,
    should_remove_line: Option<&LogLinePredicate<'_>>,
) -> bool
where
    F: Fn(&LogEntry) -> bool,
{
    if !prefilter.is_candidate(raw_line) {
        return false;
    }

    if let Some(should_remove_line) = should_remove_line {
        return should_remove_line(raw_line, source_kind);
    }

    let Ok(text) = std::str::from_utf8(raw_line) else {
        // Not valid UTF-8 -> the regex parser could never have matched it.
        return false;
    };

    match parse_log_line(cachelog, detailed, text.trim(), source_kind) {
        Some(entry) => !service_utils::should_skip_url(&entry.url) && should_remove_entry(&entry),
        None => false,
    }
}

/// Read-only scan pass: counts total lines and confirmed-match lines without
/// creating a temp file or recompressing anything.
fn scan_file_for_matches<F>(
    path: &Path,
    prefilter: &RemovalPrefilter,
    cachelog: &LogParser,
    detailed: &HttpDetailedParser,
    source_kind: &SourceKind,
    should_remove_entry: &F,
    should_remove_line: Option<&LogLinePredicate<'_>>,
) -> Result<(u64, u64)>
where
    F: Fn(&LogEntry) -> bool,
{
    let mut reader = LogFileReader::open(path)?;
    let mut line: Vec<u8> = Vec::with_capacity(1024);
    let mut lines_total: u64 = 0;
    let mut lines_matched: u64 = 0;

    loop {
        line.clear();
        let bytes_read = reader.read_until_newline(&mut line)?;
        if bytes_read == 0 {
            break;
        }

        lines_total += 1;
        if line_should_be_removed(
            &line,
            prefilter,
            cachelog,
            detailed,
            source_kind,
            should_remove_entry,
            should_remove_line,
        ) {
            lines_matched += 1;
        }
    }

    Ok((lines_total, lines_matched))
}

fn rewrite_matching_log_entries_outcome<F>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_entry: F,
    should_remove_line: Option<&LogLinePredicate<'_>>,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
{
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Filtering log files to remove {} entries...", description);

    let cachelog = LogParser::new(chrono_tz::UTC);
    let detailed = HttpDetailedParser::new(chrono_tz::UTC);
    let source_set = discover_log_sources(log_dir)?;
    // Each stem's rotation series is processed OLDEST -> NEWEST sequentially, so a removed
    // line's series index (running offset + in-file index) is known the moment it is removed.
    // Comparing that index against the stem's saved ingestion position splits the removals
    // into already-read (the position must come back by these) and not-yet-read (it must
    // not). Parallelism runs across stems; a stem's members are ordered by definition.
    let total_files: usize = source_set.sources.iter().map(|s| s.files.len()).sum();

    let total_lines_removed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let other_errors = AtomicUsize::new(0);
    let files_done = AtomicUsize::new(0);
    let lines_removed_by_stem: std::sync::Mutex<HashMap<String, u64>> =
        std::sync::Mutex::new(HashMap::new());
    let lines_removed_before_position_by_stem: std::sync::Mutex<HashMap<String, u64>> =
        std::sync::Mutex::new(HashMap::new());
    let publication_records: std::sync::Mutex<Vec<PublicationRecord>> =
        std::sync::Mutex::new(Vec::new());

    source_set.sources.into_par_iter().for_each(|source| {
        // None = no positions supplied at all -> every removed line counts as already read.
        // Some(0) = positions supplied but this stem has never been ingested -> nothing was
        // read, so nothing is subtracted from the (zero) position.
        let stem_position: Option<u64> =
            stem_positions.map(|m| m.get(&source.stem).copied().unwrap_or(0));
        let mut series_offset: u64 = 0;
        let mut stem_removed: u64 = 0;
        let mut stem_removed_before: u64 = 0;

        for log_file in &source.files {
            eprintln!("  Processing file: {}", log_file.path.display());

            let file_result = (|| -> Result<(u64, u64, u64)> {
                let expected = publication_expectation(&log_file.path)?;
                let original_identity = match &expected {
                    Some(expectation) => expectation.original_identity.clone(),
                    None => file_identity(&log_file.path)?,
                };

                // Pass 1: read-only scan. Files with zero confirmed matches are left
                // completely untouched (no temp file, no recompression).
                let (lines_total, lines_matched) = scan_file_for_matches(
                    &log_file.path,
                    prefilter,
                    &cachelog,
                    &detailed,
                    &source.kind,
                    &should_remove_entry,
                    should_remove_line,
                )?;

                if lines_matched == 0 {
                    publication_records
                        .lock()
                        .expect("publication_records mutex poisoned")
                        .push(PublicationRecord {
                            target_path: log_file.path.clone(),
                            original_identity: original_identity.clone(),
                            temporary_identity: None,
                            published_identity: Some(original_identity),
                            changed: false,
                            deleted: false,
                        });
                    return Ok((lines_total, 0, 0));
                }

                if lines_matched == lines_total && source.kind == SourceKind::Monolithic {
                    // Every line matched: delete the file entirely (same semantics as
                    // the previous monolithic implementation, minus the wasted temp write).
                    eprintln!(
                        "  INFO: All {} lines from this file matched, deleting file entirely",
                        lines_total
                    );
                    match cache_utils::safe_path_under_root(log_dir, &log_file.path) {
                        Ok(_) => {
                            publication_expectation(&log_file.path)?;
                            std::fs::remove_file(&log_file.path)?;
                            publication_records
                                .lock()
                                .expect("publication_records mutex poisoned")
                                .push(PublicationRecord {
                                    target_path: log_file.path.clone(),
                                    original_identity,
                                    temporary_identity: None,
                                    published_identity: None,
                                    changed: true,
                                    deleted: true,
                                });
                        }
                        Err(e) => {
                            return Err(e).with_context(|| {
                                format!("unsafe log path {}", log_file.path.display())
                            });
                        }
                    }
                    // The file occupied series indices offset..offset+lines_total; the
                    // already-read share is whatever of that range lies below the position.
                    let removed_before = match stem_position {
                        Some(p) => p.saturating_sub(series_offset).min(lines_total),
                        None => lines_matched,
                    };
                    return Ok((lines_total, lines_matched, removed_before));
                }

                // Pass 2: rewrite the file without the matching lines.
                let file_dir = log_file
                    .path
                    .parent()
                    .context("Failed to get file directory")?;
                let temp_file = NamedTempFile::new_in(file_dir)?;

                let mut lines_total_rewrite: u64 = 0;
                let mut lines_removed: u64 = 0;
                let mut removed_before: u64 = 0;

                {
                    let mut log_reader = LogFileReader::open(&log_file.path)?;

                    let mut writer = if log_file.is_compressed {
                        let path_str = log_file.path.to_string_lossy();
                        if path_str.ends_with(".gz") {
                            LogRewriteWriter::Gzip(GzEncoder::new(
                                BufWriter::with_capacity(
                                    1024 * 1024,
                                    temp_file.as_file().try_clone()?,
                                ),
                                Compression::fast(),
                            ))
                        } else if path_str.ends_with(".zst") {
                            LogRewriteWriter::Zstd(zstd::Encoder::new(
                                BufWriter::with_capacity(
                                    1024 * 1024,
                                    temp_file.as_file().try_clone()?,
                                ),
                                3,
                            )?)
                        } else {
                            LogRewriteWriter::Plain(BufWriter::with_capacity(
                                1024 * 1024,
                                temp_file.as_file().try_clone()?,
                            ))
                        }
                    } else {
                        LogRewriteWriter::Plain(BufWriter::with_capacity(
                            1024 * 1024,
                            temp_file.as_file().try_clone()?,
                        ))
                    };

                    let mut line: Vec<u8> = Vec::with_capacity(1024);

                    loop {
                        line.clear();
                        let bytes_read = log_reader.read_until_newline(&mut line)?;
                        if bytes_read == 0 {
                            break;
                        }

                        if line_should_be_removed(
                            &line,
                            prefilter,
                            &cachelog,
                            &detailed,
                            &source.kind,
                            &should_remove_entry,
                            should_remove_line,
                        ) {
                            lines_removed += 1;
                            // An unterminated final record naturally lands in the not-read
                            // bucket: the saved position only ever counts complete records,
                            // so that line's series index is always at or past it.
                            match stem_position {
                                Some(p) if series_offset + lines_total_rewrite >= p => {}
                                _ => removed_before += 1,
                            }
                        } else {
                            writer.write_all(&line)?;
                        }
                        lines_total_rewrite += 1;
                    }

                    writer.finish()?;
                }

                let temp_path = temp_file.into_temp_path();
                let temporary_identity = file_identity(&temp_path)?;
                let kept_path = temp_path
                    .keep()
                    .map_err(|error| error.error)
                    .context("failed to keep rewritten log temporary file")?;

                if let Err(error) = (|| -> Result<()> {
                    publication_expectation(&log_file.path)?;
                    if file_identity(&kept_path)? != temporary_identity {
                        anyhow::bail!(
                            "rewritten log temporary identity changed: {}",
                            kept_path.display()
                        )
                    }
                    std::fs::rename(&kept_path, &log_file.path).with_context(|| {
                        format!(
                            "failed to atomically publish rewritten log {}",
                            log_file.path.display()
                        )
                    })?;
                    let published_identity = file_identity(&log_file.path)?;
                    if published_identity != temporary_identity {
                        anyhow::bail!(
                            "published log identity did not match kept temporary file: {}",
                            log_file.path.display()
                        )
                    }
                    publication_records
                        .lock()
                        .expect("publication_records mutex poisoned")
                        .push(PublicationRecord {
                            target_path: log_file.path.clone(),
                            original_identity,
                            temporary_identity: Some(temporary_identity.clone()),
                            published_identity: Some(published_identity),
                            changed: true,
                            deleted: false,
                        });
                    Ok(())
                })() {
                    if kept_path.exists() {
                        let _ = std::fs::remove_file(&kept_path);
                    }
                    return Err(error);
                }

                Ok((lines_total_rewrite, lines_removed, removed_before))
            })();

            match file_result {
                Ok((lines_total, lines_removed, removed_before)) => {
                    eprintln!("    Removed {} log lines from this file", lines_removed);
                    series_offset += lines_total;
                    stem_removed += lines_removed;
                    stem_removed_before += removed_before;
                    total_lines_removed.fetch_add(lines_removed, Ordering::Relaxed);
                }
                Err(e) => {
                    // The file's length is unknown, so the series offset stays put. Later
                    // removed lines in this stem then compare against a too-low offset and
                    // over-count as already-read, which only pulls the position further
                    // back - the replay-safe direction.
                    let error_str = e.to_string();
                    if error_str.contains("Permission denied") || error_str.contains("os error 13")
                    {
                        permission_errors.fetch_add(1, Ordering::Relaxed);
                        eprintln!(
                            "  ERROR: Permission denied for file {}: {}",
                            log_file.path.display(),
                            e
                        );
                    } else {
                        other_errors.fetch_add(1, Ordering::Relaxed);
                        eprintln!(
                            "  WARNING: Skipping file {}: {}",
                            log_file.path.display(),
                            e
                        );
                    }
                }
            }

            if let Some(cb) = on_file_processed {
                let done = files_done.fetch_add(1, Ordering::Relaxed) + 1;
                cb(done, total_files);
            }
        }

        if stem_removed > 0 {
            let mut by_stem = lines_removed_by_stem
                .lock()
                .expect("lines_removed_by_stem mutex poisoned");
            *by_stem.entry(source.stem.clone()).or_insert(0) += stem_removed;
            drop(by_stem);
            let mut before_by_stem = lines_removed_before_position_by_stem
                .lock()
                .expect("lines_removed_before_position_by_stem mutex poisoned");
            *before_by_stem.entry(source.stem.clone()).or_insert(0) += stem_removed_before;
        }
    });

    let final_removed = total_lines_removed.load(Ordering::Relaxed);
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);
    let final_other_errors = other_errors.load(Ordering::Relaxed);
    eprintln!(
        "Total log entries removed: {}, permission errors: {}",
        final_removed, final_permission_errors
    );
    let records = publication_records
        .into_inner()
        .expect("publication_records mutex poisoned");
    write_publication_result(
        records,
        final_permission_errors == 0 && final_other_errors == 0,
    )?;
    Ok(LogRewriteOutcome {
        lines_removed: final_removed,
        permission_errors: final_permission_errors,
        other_errors: final_other_errors,
        lines_removed_by_stem: lines_removed_by_stem
            .into_inner()
            .expect("lines_removed_by_stem mutex poisoned"),
        lines_removed_before_position_by_stem: lines_removed_before_position_by_stem
            .into_inner()
            .expect("lines_removed_before_position_by_stem mutex poisoned"),
    })
}

pub(crate) fn rewrite_matching_log_entries<F>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_entry: F,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
{
    rewrite_matching_log_entries_outcome(
        log_dir,
        description,
        prefilter,
        should_remove_entry,
        None,
        on_file_processed,
        stem_positions,
    )
}

/// Strict exact-evidence variant: reports both permission and non-permission file failures so the
/// caller can retain database/persisted evidence after any partial log rewrite.
pub fn rewrite_matching_log_entries_strict<F>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_entry: F,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
{
    rewrite_matching_log_entries_outcome(
        log_dir,
        description,
        prefilter,
        should_remove_entry,
        None,
        on_file_processed,
        stem_positions,
    )
}

pub fn rewrite_matching_log_lines_strict<F>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_line: F,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome>
where
    F: Fn(&[u8], &SourceKind) -> bool + Send + Sync,
{
    rewrite_matching_log_entries_outcome(
        log_dir,
        description,
        prefilter,
        |_| false,
        Some(&should_remove_line),
        None,
        stem_positions,
    )
}

/// Rewrite every discovered nginx access-log file under `log_dir` to drop entries whose
/// URL is in `urls_to_remove` OR whose parsed depot_id is in `valid_depot_ids`.
///
/// Returns the full rewrite outcome, including per-stem removed-line counts the caller
/// must report so saved ingestion positions can be reduced.
///
/// An optional `on_file_processed` callback is invoked after each file completes
/// (including scan-only files that needed no rewrite), receiving
/// `(files_processed_so_far, total_files)`. The callback must be
/// `Send + Sync` because files are processed in parallel via rayon.
///
/// Safe to run against a live cache host: each target file is rewritten via a
/// temp file in the same directory and then atomically renamed so partially-written
/// files are never observed. Files that
/// contain no matching lines are left completely untouched.
#[allow(dead_code)]
pub fn remove_log_entries_for_game(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    valid_depot_ids: &HashSet<u32>,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome> {
    let patterns = urls_to_remove
        .iter()
        .map(|url| Cow::<[u8]>::Borrowed(url.as_bytes()))
        .chain(
            valid_depot_ids
                .iter()
                .map(|depot_id| Cow::<[u8]>::Owned(format!("/depot/{depot_id}/").into_bytes())),
        );
    let prefilter = RemovalPrefilter::new(patterns)?;
    rewrite_matching_log_entries(
        log_dir,
        "game",
        &prefilter,
        |entry| {
            if urls_to_remove.contains(&entry.url) {
                return true;
            }

            entry
                .depot_id
                .map(|depot_id| valid_depot_ids.contains(&depot_id))
                .unwrap_or(false)
        },
        on_file_processed,
        stem_positions,
    )
}

#[allow(dead_code)]
pub(crate) fn remove_log_entries_for_urls(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome> {
    let prefilter = RemovalPrefilter::new(urls_to_remove.iter().map(|url| url.as_bytes()))?;
    rewrite_matching_log_entries(
        log_dir,
        "URL-matched",
        &prefilter,
        |entry| urls_to_remove.contains(&entry.url),
        None,
        stem_positions,
    )
}

#[allow(dead_code)]
pub fn remove_log_entries_for_service(
    log_dir: &Path,
    service: &str,
    urls_to_remove: &HashSet<String>,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome> {
    let normalized_service = service_utils::normalize_service_name(service);
    let description = format!("service '{}'", service);
    let prefilter = RemovalPrefilter::new(urls_to_remove.iter().map(|url| url.as_bytes()))?;

    rewrite_matching_log_entries(
        log_dir,
        &description,
        &prefilter,
        |entry| entry.service == normalized_service && urls_to_remove.contains(&entry.url),
        None,
        stem_positions,
    )
}

pub fn remove_all_log_entries_for_service(
    log_dir: &Path,
    service: &str,
    stem_positions: Option<&HashMap<String, u64>>,
) -> Result<LogRewriteOutcome> {
    let normalized_service = service_utils::normalize_service_name(service);
    let pattern = format!("[{normalized_service}]");
    let prefilter = RemovalPrefilter::new([pattern.as_bytes()])?;
    rewrite_matching_log_lines_strict(
        log_dir,
        service,
        &prefilter,
        |raw_line, _| service_utils::line_matches_service(raw_line, &normalized_service),
        stem_positions,
    )
}

/// Read a per-stem ingestion-positions file (a JSON object of stem name to line index) for
/// the exact-subtraction split. `None` on any failure: the rewrite then treats every removed
/// line as already read, the direction whose worst case is a replay the dedupe discards.
pub fn read_stem_positions(path: &str) -> Option<HashMap<String, u64>> {
    match std::fs::read_to_string(path) {
        Ok(text) => match serde_json::from_str::<HashMap<String, u64>>(&text) {
            Ok(map) => Some(map),
            Err(e) => {
                eprintln!("Warning: stem-positions file {} unparseable ({}); treating all removed lines as read", path, e);
                None
            }
        },
        Err(e) => {
            eprintln!("Warning: stem-positions file {} unreadable ({}); treating all removed lines as read", path, e);
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::DateTime;
    use std::fs;
    use std::io::Write;

    #[cfg(unix)]
    #[test]
    fn path_and_handle_identity_follow_the_same_unix_file() {
        let directory = tempfile::tempdir().expect("create identity fixture");
        let path = directory.path().join("access.log");
        let moved = directory.path().join("access.log.1");
        fs::write(&path, b"old\n").expect("write identity fixture");
        let opened = File::open(&path).expect("open identity fixture");
        let original = file_identity_of(&opened).expect("read handle identity");

        assert_eq!(file_identity(&path).expect("read path identity"), original);
        fs::rename(&path, &moved).expect("rename identity fixture");
        assert_eq!(
            file_identity(&moved).expect("read renamed path identity"),
            original
        );
        fs::write(&path, b"replacement\n").expect("write replacement fixture");
        assert_ne!(
            file_identity(&path).expect("read replacement identity"),
            original
        );
        assert_eq!(
            file_identity_of(&opened).expect("reread handle identity"),
            original
        );
    }

    #[test]
    fn purge_counts_split_per_stem_and_sum_across_a_rotation_series() {
        let dir = tempfile::tempdir().unwrap();
        // One rotation series for the monolithic stem: 1 match in the live file,
        // 3 matches in the rotated member. Plus a separate per-service stem with 2 matches.
        fs::write(
            dir.path().join("access.log.1"),
            format!(
                "{}
{}
{}
{}
",
                log_line("/depot/9/chunk/x1", "HIT"),
                log_line("/depot/9/chunk/x2", "HIT"),
                log_line("/depot/9/chunk/x3", "HIT"),
                log_line("/depot/7/chunk/keep", "HIT"),
            ),
        )
        .unwrap();
        fs::write(
            dir.path().join("access.log"),
            format!(
                "{}
{}
",
                log_line("/depot/7/chunk/keep2", "HIT"),
                log_line("/depot/9/chunk/x4", "MISS"),
            ),
        )
        .unwrap();
        fs::write(
            dir.path().join("steam-access.log"),
            format!(
                "{}
{}
",
                log_line("/depot/9/chunk/x5", "HIT"),
                log_line("/depot/9/chunk/x6", "HIT"),
            ),
        )
        .unwrap();

        let urls: HashSet<String> = [
            "/depot/9/chunk/x1",
            "/depot/9/chunk/x2",
            "/depot/9/chunk/x3",
            "/depot/9/chunk/x4",
            "/depot/9/chunk/x5",
            "/depot/9/chunk/x6",
        ]
        .into_iter()
        .map(str::to_string)
        .collect();
        let outcome = remove_log_entries_for_urls(dir.path(), &urls, None).unwrap();

        assert_eq!(outcome.lines_removed, 6);
        // The rotation series sums under ONE stem key; the per-service stem keeps its own.
        assert_eq!(
            outcome.lines_removed_by_stem,
            HashMap::from([
                ("access.log".to_string(), 4u64),
                ("steam-access.log".to_string(), 2u64)
            ])
        );
        // No positions supplied: everything counts as already read.
        assert_eq!(
            outcome.lines_removed_before_position_by_stem,
            outcome.lines_removed_by_stem
        );
    }

    #[test]
    fn removals_ahead_of_the_saved_position_do_not_count_as_already_read() {
        let dir = tempfile::tempdir().unwrap();
        // Series: access.log.1 holds indices 0..4, access.log holds 5..9. Position 6 means
        // indices 0..5 were read. Removals at series indices 2 (read) and 7 (not read).
        let mut rotated = String::new();
        for i in 0..5 {
            let url = if i == 2 {
                "/depot/9/chunk/gone-a".to_string()
            } else {
                format!("/depot/7/chunk/keep{i}")
            };
            rotated.push_str(&log_line(&url, "HIT"));
            rotated.push('\n');
        }
        fs::write(dir.path().join("access.log.1"), rotated).unwrap();
        let mut live = String::new();
        for i in 5..10 {
            let url = if i == 7 {
                "/depot/9/chunk/gone-b".to_string()
            } else {
                format!("/depot/7/chunk/keep{i}")
            };
            live.push_str(&log_line(&url, "HIT"));
            live.push('\n');
        }
        fs::write(dir.path().join("access.log"), live).unwrap();

        let urls: HashSet<String> = ["/depot/9/chunk/gone-a", "/depot/9/chunk/gone-b"]
            .into_iter()
            .map(str::to_string)
            .collect();
        let positions = HashMap::from([("access.log".to_string(), 6u64)]);
        let outcome = remove_log_entries_for_urls(dir.path(), &urls, Some(&positions)).unwrap();

        assert_eq!(outcome.lines_removed, 2);
        assert_eq!(
            outcome.lines_removed_by_stem,
            HashMap::from([("access.log".to_string(), 2u64)])
        );
        // Only the removal at series index 2 sat below position 6.
        assert_eq!(
            outcome.lines_removed_before_position_by_stem,
            HashMap::from([("access.log".to_string(), 1u64)])
        );
    }

    fn log_line(url: &str, cache_status: &str) -> String {
        format!(
            "[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET {} HTTP/1.1\" 200 1024 \"-\" \"Valve/Steam\" \"{}\" \"-\" \"-\"",
            url, cache_status
        )
    }

    fn detailed_log_line(url: &str, cache_status: &str) -> String {
        format!(
            "[01/Jan/2024:00:00:00 +0000] 192.168.1.50 GET \"{url}\" - HTTP/1.1 200 \"-\" 512 1040 1024 0.005 1024 {cache_status} cdn.test 200 0.004 \"Valve/Steam\""
        )
    }

    fn exact_log_line(
        client: &str,
        method: &str,
        status: i32,
        url: &str,
        cache_status: &str,
        range: &str,
    ) -> String {
        exact_log_line_with_bytes(client, method, status, url, 1024, cache_status, range)
    }

    fn exact_log_line_with_bytes(
        client: &str,
        method: &str,
        status: i32,
        url: &str,
        bytes_served: i64,
        cache_status: &str,
        range: &str,
    ) -> String {
        format!(
            "[steam] {client} / - - - [01/Jan/2024:00:00:00 +0000] \"{method} {url} HTTP/1.1\" {status} {bytes_served} \"-\" \"Valve/Steam\" \"{cache_status}\" \"cdn.test\" \"{range}\""
        )
    }

    fn target_observation() -> ExactLogObservation {
        ExactLogObservation {
            service: "steam".to_string(),
            raw_url: "/same.bin".to_string(),
            timestamp: DateTime::parse_from_rfc3339("2024-01-01T00:00:00Z")
                .unwrap()
                .with_timezone(&Utc),
            client_ip: "192.168.1.50".to_string(),
            method: "GET".to_string(),
            http_status: 206,
            bytes_served: 1024,
            cache_status: "MISS".to_string(),
            raw_range: Some("bytes=1048576-2097151".to_string()),
        }
    }

    fn read_log_file(path: &Path) -> String {
        let mut reader = LogFileReader::open(path).unwrap();
        let mut output = String::new();
        let mut line = String::new();
        loop {
            line.clear();
            if reader.read_line(&mut line).unwrap() == 0 {
                break;
            }
            output.push_str(&line);
        }
        output
    }

    fn write_gzip(path: &Path, contents: &[u8]) {
        let file = fs::File::create(path).expect("create gzip fixture");
        let mut encoder = flate2::write::GzEncoder::new(file, flate2::Compression::fast());
        encoder.write_all(contents).expect("write gzip fixture");
        encoder.finish().expect("finish gzip fixture");
    }

    fn write_zstd(path: &Path, contents: &[u8]) {
        let file = fs::File::create(path).expect("create zstd fixture");
        let mut encoder = zstd::Encoder::new(file, 1).expect("create zstd fixture");
        encoder.write_all(contents).expect("write zstd fixture");
        encoder.finish().expect("finish zstd fixture");
    }

    fn json_log_line(service: &str, path: &str, bytes_sent: i64) -> String {
        serde_json::json!({
            "cache_identifier": service,
            "remote_addr": "192.0.2.40",
            "time_local": "01/Jan/2024:00:00:00 +0000",
            "method": "GET",
            "path": path,
            "status": "206",
            "bytes_sent": bytes_sent,
            "user_agent": "Fixture/1.0",
            "upstream_cache_status": "MISS",
            "host": "cdn.example.test",
            "http_range": "bytes=1048576-2097151"
        })
        .to_string()
    }

    #[test]
    fn service_removal_handles_json_across_plain_gzip_and_zstd() {
        let dir = tempfile::tempdir().expect("create fixture directory");
        let text_target = log_line("/xbox/text", "HIT").replacen("[steam]", "[xboxlive]", 1);
        let text_neighbor = log_line("/steam/keep", "HIT");
        let json_target = json_log_line("xboxlive", "/xbox/json", 2048);
        let escaped_service = json_target.replace("xboxlive", "xbox\\u006cive");
        let json_neighbor = json_log_line("wsus", "/wsus/keep", 4096);
        let malformed = r#"{"cache_identifier":"xboxlive"}"#;

        fs::write(
            dir.path().join("access.log"),
            format!("{text_target}\n{text_neighbor}\n"),
        )
        .expect("write plain fixture");
        write_gzip(
            &dir.path().join("access.log.1.gz"),
            format!("{escaped_service}\n{malformed}\n").as_bytes(),
        );
        write_zstd(
            &dir.path().join("access.log.2.zst"),
            format!("{json_target}\n{json_neighbor}\n").as_bytes(),
        );

        let positions = HashMap::from([("access.log".to_string(), 6)]);
        let outcome = remove_all_log_entries_for_service(dir.path(), "xboxlive", Some(&positions))
            .expect("remove xboxlive records");

        assert_eq!(outcome.lines_removed, 3);
        assert_eq!(
            outcome.lines_removed_by_stem,
            HashMap::from([("access.log".to_string(), 3)])
        );
        assert_eq!(
            outcome.lines_removed_before_position_by_stem,
            HashMap::from([("access.log".to_string(), 3)])
        );
        assert_eq!(
            read_log_file(&dir.path().join("access.log")),
            format!("{text_neighbor}\n")
        );
        assert_eq!(
            read_log_file(&dir.path().join("access.log.1.gz")),
            format!("{malformed}\n")
        );
        assert_eq!(
            read_log_file(&dir.path().join("access.log.2.zst")),
            format!("{json_neighbor}\n")
        );
    }

    #[test]
    fn exact_removal_decodes_escaped_json_without_deleting_neighbor() {
        let dir = tempfile::tempdir().expect("create fixture directory");
        let path = dir.path().join("access.log");
        let target = r#"{"cache_identifier":"steam","remote_addr":"192.168.1.50","time_local":"01/Jan/2024:00:00:00 +0000","method":"GET","path":"\u002fsame.bin","status":"206","bytes_sent":1024,"user_agent":"Fixture/1.0","upstream_cache_status":"MISS","host":"cdn.example.test","http_range":"bytes=1048576-2097151"}"#;
        let neighbor = r#"{"cache_identifier":"steam","remote_addr":"192.168.1.50","time_local":"01/Jan/2024:00:00:00 +0000","method":"GET","path":"\u002fsame.bin","status":"206","bytes_sent":2048,"user_agent":"Fixture/1.0","upstream_cache_status":"MISS","host":"cdn.example.test","http_range":"bytes=1048576-2097151"}"#;
        fs::write(&path, format!("{target}\n{neighbor}\n")).expect("write JSON fixture");

        let matcher = ExactLogMatcher::new([target_observation()]);
        let prefilter = matcher.prefilter().expect("create exact prefilter");
        let outcome = rewrite_matching_log_entries_strict(
            dir.path(),
            "exact JSON evidence",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
            None,
        )
        .expect("rewrite exact JSON evidence");

        assert_eq!(outcome.lines_removed, 1);
        assert_eq!(
            fs::read_to_string(path).expect("read fixture"),
            format!("{neighbor}\n")
        );
    }

    #[test]
    fn exact_matcher_keeps_same_url_with_different_observation_identity() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("access.log");
        let target = exact_log_line(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            "MISS",
            "bytes=1048576-2097151",
        );
        let keep_range = exact_log_line(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            "MISS",
            "bytes=2097152-3145727",
        );
        let keep_status = exact_log_line(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            "HIT",
            "bytes=1048576-2097151",
        );
        let keep_client = exact_log_line(
            "192.168.1.51",
            "GET",
            206,
            "/same.bin",
            "MISS",
            "bytes=1048576-2097151",
        );
        let keep_bytes = exact_log_line_with_bytes(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            2048,
            "MISS",
            "bytes=1048576-2097151",
        );
        fs::write(
            &log_path,
            format!("{target}\n{keep_range}\n{keep_status}\n{keep_client}\n{keep_bytes}\n"),
        )
        .unwrap();

        let matcher = ExactLogMatcher::new([target_observation()]);
        assert_eq!(matcher.len(), 1);
        let prefilter = matcher.prefilter().unwrap();
        let outcome = rewrite_matching_log_entries_strict(
            dir.path(),
            "exact corruption evidence",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
            None,
        )
        .unwrap();

        assert_eq!(outcome.lines_removed, 1);
        assert_eq!(outcome.permission_errors, 0);
        assert_eq!(outcome.other_errors, 0);
        let remaining = fs::read_to_string(log_path).unwrap();
        assert!(!remaining.contains(&target));
        assert!(remaining.contains(&keep_range));
        assert!(remaining.contains(&keep_status));
        assert!(remaining.contains(&keep_client));
        assert!(remaining.contains(&keep_bytes));
    }

    #[test]
    fn exact_matcher_preserves_scope_in_gzip_and_zstd_logs() {
        let dir = tempfile::tempdir().unwrap();
        let target = exact_log_line(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            "MISS",
            "bytes=1048576-2097151",
        );
        let keep = exact_log_line(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            "MISS",
            "bytes=2097152-3145727",
        );
        let keep_bytes = exact_log_line_with_bytes(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            2048,
            "MISS",
            "bytes=1048576-2097151",
        );
        let contents = format!("{target}\n{keep}\n{keep_bytes}\n");
        let gzip_path = dir.path().join("access.log.1.gz");
        let zstd_path = dir.path().join("access.log.2.zst");

        write_gzip(&gzip_path, contents.as_bytes());
        write_zstd(&zstd_path, contents.as_bytes());

        let matcher = ExactLogMatcher::new([target_observation()]);
        let prefilter = matcher.prefilter().unwrap();
        let outcome = rewrite_matching_log_entries_strict(
            dir.path(),
            "compressed exact corruption evidence",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
            None,
        )
        .unwrap();

        assert_eq!(outcome.lines_removed, 2);
        assert_eq!(read_log_file(&gzip_path), format!("{keep}\n{keep_bytes}\n"));
        assert_eq!(read_log_file(&zstd_path), format!("{keep}\n{keep_bytes}\n"));
    }

    #[test]
    fn exact_rewrite_is_idempotent_and_preserves_neighboring_bytes() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("access.log");
        let target = exact_log_line(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            "MISS",
            "bytes=1048576-2097151",
        );
        let neighbor = exact_log_line_with_bytes(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            1025,
            "MISS",
            "bytes=1048576-2097151",
        );
        fs::write(&log_path, format!("{target}\n{neighbor}\n")).unwrap();
        let matcher = ExactLogMatcher::new([target_observation()]);
        let prefilter = matcher.prefilter().unwrap();

        let first = rewrite_matching_log_entries_strict(
            dir.path(),
            "idempotent exact evidence",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
            None,
        )
        .unwrap();
        let second = rewrite_matching_log_entries_strict(
            dir.path(),
            "idempotent exact evidence retry",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
            None,
        )
        .unwrap();

        assert_eq!(first.lines_removed, 1);
        assert_eq!(second.lines_removed, 0);
        assert_eq!(
            fs::read_to_string(log_path).unwrap(),
            format!("{neighbor}\n")
        );
    }
    #[test]
    fn strict_rewrite_reports_compressed_file_failure_after_other_exact_work() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("access.log");
        let target = exact_log_line(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            "MISS",
            "bytes=1048576-2097151",
        );
        let neighbor = exact_log_line_with_bytes(
            "192.168.1.50",
            "GET",
            206,
            "/same.bin",
            2048,
            "MISS",
            "bytes=1048576-2097151",
        );
        fs::write(&log_path, format!("{target}\n{neighbor}\n")).unwrap();
        fs::write(dir.path().join("access.log.1.zst"), b"not a zstd frame").unwrap();
        let matcher = ExactLogMatcher::new([target_observation()]);
        let prefilter = matcher.prefilter().unwrap();

        let outcome = rewrite_matching_log_entries_strict(
            dir.path(),
            "partial exact evidence",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
            None,
        )
        .unwrap();

        assert_eq!(outcome.lines_removed, 1);
        assert_eq!(outcome.permission_errors, 0);
        assert_eq!(outcome.other_errors, 1);
        assert_eq!(
            fs::read_to_string(log_path).unwrap(),
            format!("{neighbor}\n")
        );
    }

    #[test]
    fn prefilter_skips_non_matching_lines_and_flags_candidates() {
        let patterns = ["/depot/123456/chunk/abc".to_string()];
        let prefilter =
            RemovalPrefilter::new(patterns.iter().map(|pattern| pattern.as_bytes())).unwrap();
        drop(patterns);

        // Direct literal hit -> candidate
        assert!(prefilter.is_candidate(log_line("/depot/123456/chunk/abc", "HIT").as_bytes()));
        // No hit, no double slash -> definitively not a candidate
        assert!(!prefilter.is_candidate(log_line("/depot/999/chunk/zzz", "HIT").as_bytes()));
        // Doubled slash forces the full-parse path even though the literal
        // pattern is not a substring of the raw line
        assert!(prefilter.is_candidate(log_line("/depot/123456//chunk/abc", "HIT").as_bytes()));
    }

    #[test]
    fn prefilter_past_the_pattern_budget_sends_every_line_to_the_full_parse() {
        // One pattern larger than the budget: no automaton, so a line that matches nothing is
        // still a candidate and the exact predicate stays the only thing that can drop it.
        let oversized = vec![b'x'; 8 * 1024 * 1024 + 1];
        let prefilter = RemovalPrefilter::new([oversized.as_slice()]).unwrap();
        assert!(prefilter.is_candidate(log_line("/depot/999/chunk/zzz", "HIT").as_bytes()));

        // Under the budget the automaton is built and non-matching lines are still skipped.
        let prefilter = RemovalPrefilter::new([b"/depot/123456/chunk/abc".as_slice()]).unwrap();
        assert!(!prefilter.is_candidate(log_line("/depot/999/chunk/zzz", "HIT").as_bytes()));
    }

    #[test]
    fn doubled_slash_line_whose_normalized_url_is_a_target_is_removed() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("access.log");

        let target_url = "/depot/123456/chunk/abc";
        let keep_url = "/depot/777777/chunk/keepme";
        let contents = format!(
            "{}\n{}\n{}\n",
            log_line(target_url, "HIT"),
            // Raw line contains "//", normalized URL equals the target -> must be removed
            log_line("/depot/123456//chunk/abc", "MISS"),
            log_line(keep_url, "HIT"),
        );
        fs::write(&log_path, &contents).unwrap();

        let urls: HashSet<String> = [target_url.to_string()].into_iter().collect();
        let outcome = remove_log_entries_for_urls(dir.path(), &urls, None).unwrap();

        assert_eq!(outcome.lines_removed, 2);
        assert_eq!(outcome.permission_errors, 0);
        // The per-stem map carries the position adjustment: 2 lines out of access.log's series.
        assert_eq!(
            outcome.lines_removed_by_stem,
            HashMap::from([("access.log".to_string(), 2u64)])
        );

        let remaining = fs::read_to_string(&log_path).unwrap();
        assert!(remaining.contains(keep_url));
        assert!(!remaining.contains("/depot/123456"));
    }

    #[test]
    fn per_service_http_detailed_purge_preserves_kept_bytes() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("steam-access.log");
        let target_one = detailed_log_line("/depot/424242/chunk/drop-a", "MISS");
        let target_two = detailed_log_line("/depot/424242/chunk/drop-b", "HIT");
        let kept = format!(
            " \t{}  \r\n",
            detailed_log_line("/depot/555555/chunk/keep", "HIT")
        );
        let unrecognized = "unrecognized /depot/424242/chunk/drop-a bytes \t\r\n";
        let contents = format!("{target_one}\r\n{kept}{target_two}\n{unrecognized}");
        fs::write(&log_path, contents.as_bytes()).unwrap();

        let urls: HashSet<String> = HashSet::new();
        let depot_ids: HashSet<u32> = [424242].into_iter().collect();
        let outcome =
            remove_log_entries_for_game(dir.path(), &urls, &depot_ids, None, None).unwrap();

        assert_eq!(outcome.lines_removed, 2);
        assert_eq!(outcome.permission_errors, 0);
        assert!(log_path.exists());
        assert_eq!(
            fs::read(&log_path).unwrap(),
            format!("{kept}{unrecognized}").as_bytes()
        );
    }

    #[test]
    fn file_with_no_matches_is_left_completely_untouched() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("access.log");

        let contents = format!(
            "{}\n{}\n",
            log_line("/depot/111111/chunk/aaa", "HIT"),
            log_line("/depot/222222/chunk/bbb", "MISS"),
        );
        fs::write(&log_path, &contents).unwrap();
        let mtime_before = fs::metadata(&log_path).unwrap().modified().unwrap();

        let urls: HashSet<String> = ["/depot/999999/chunk/zzz".to_string()]
            .into_iter()
            .collect();
        let depot_ids: HashSet<u32> = HashSet::new();
        let calls = std::sync::atomic::AtomicUsize::new(0);
        let cb = |done: usize, total: usize| {
            assert_eq!(total, 1);
            assert_eq!(done, 1);
            calls.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        };
        let outcome =
            remove_log_entries_for_game(dir.path(), &urls, &depot_ids, Some(&cb), None).unwrap();
        assert_eq!(outcome.lines_removed, 0);
        assert_eq!(
            calls.load(std::sync::atomic::Ordering::Relaxed),
            1,
            "on_file_processed must fire for scan-only files"
        );

        let mtime_after = fs::metadata(&log_path).unwrap().modified().unwrap();
        assert_eq!(
            mtime_before, mtime_after,
            "untouched file must not be rewritten"
        );
        assert_eq!(fs::read_to_string(&log_path).unwrap(), contents);
    }

    #[test]
    fn file_where_all_lines_match_is_deleted_entirely() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("access.log");

        let target_url = "/depot/123456/chunk/abc";
        fs::write(
            &log_path,
            format!(
                "{}\n{}\n",
                log_line(target_url, "HIT"),
                log_line(target_url, "MISS")
            ),
        )
        .unwrap();

        let urls: HashSet<String> = [target_url.to_string()].into_iter().collect();
        let outcome = remove_log_entries_for_urls(dir.path(), &urls, None).unwrap();

        assert_eq!(outcome.lines_removed, 2);
        assert!(!log_path.exists(), "fully-matched file must be deleted");
    }

    #[test]
    fn depot_id_pattern_prefilters_and_removes_depot_lines() {
        let dir = tempfile::tempdir().unwrap();
        let log_path = dir.path().join("access.log");

        fs::write(
            &log_path,
            format!(
                "{}\n{}\n",
                log_line("/depot/424242/chunk/abc", "HIT"),
                log_line("/depot/555555/chunk/def", "HIT"),
            ),
        )
        .unwrap();

        let urls: HashSet<String> = HashSet::new();
        let depot_ids: HashSet<u32> = [424242].into_iter().collect();
        let outcome =
            remove_log_entries_for_game(dir.path(), &urls, &depot_ids, None, None).unwrap();

        assert_eq!(outcome.lines_removed, 1);
        let remaining = fs::read_to_string(&log_path).unwrap();
        assert!(remaining.contains("/depot/555555/"));
        assert!(!remaining.contains("/depot/424242/"));
    }
}
