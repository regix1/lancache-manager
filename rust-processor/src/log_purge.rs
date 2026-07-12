// Shared log-purge helper used by `cache_steam_remove`, `cache_purge_log_entries`
// and `cache_corruption`.
//
// This module contains `remove_log_entries_for_game`, which walks all nginx
// access.log files under a log directory (plain + gzip + zstd), rewrites each
// file to exclude lines matching the given URL set or depot-ID set, and
// atomically replaces the originals.
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
use std::collections::HashSet;
use std::fs::File;
use std::io::{BufWriter, Write as IoWrite};
use std::path::Path;

use aho_corasick::AhoCorasick;
use flate2::write::GzEncoder;
use flate2::Compression;
use tempfile::NamedTempFile;

use crate::cache_utils;
use crate::log_reader::LogFileReader;
use crate::models::LogEntry;
use crate::parser::LogParser;
use crate::{log_discovery, service_utils};

/// Byte-level prefilter for removal candidates. A line that fails
/// `is_candidate` can never match the removal predicate and is written
/// through without UTF-8 validation or regex parsing.
pub(crate) struct RemovalPrefilter {
    automaton: AhoCorasick,
}

/// One exact stored corruption observation. Matching every field keeps log cleanup inside the
/// immutable evidence window; a URL match alone is intentionally insufficient.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub(crate) struct ExactLogObservation {
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
pub(crate) struct ExactLogMatcher {
    observations: HashSet<ExactLogObservation>,
}

impl ExactLogMatcher {
    pub(crate) fn new<I>(observations: I) -> Self
    where
        I: IntoIterator<Item = ExactLogObservation>,
    {
        Self {
            observations: observations.into_iter().collect(),
        }
    }

    pub(crate) fn prefilter(&self) -> Result<RemovalPrefilter> {
        RemovalPrefilter::new(
            self.observations
                .iter()
                .map(|observation| observation.raw_url.as_bytes()),
        )
    }

    pub(crate) fn matches(&self, entry: &LogEntry) -> bool {
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct LogRewriteOutcome {
    pub lines_removed: u64,
    pub permission_errors: usize,
    pub other_errors: usize,
    pub cancelled: bool,
}

enum FileRewriteOutcome {
    Removed(u64),
    Cancelled,
}

/// Owns the concrete output encoder so compressed streams can be finalized before the
/// temporary file is persisted. A trait-object `flush()` is not sufficient for zstd because
/// it does not write the end-of-frame marker.
enum LogRewriteWriter {
    Plain(BufWriter<File>),
    Gzip(GzEncoder<BufWriter<File>>),
    Zstd(zstd::Encoder<'static, BufWriter<File>>),
}

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
    pub(crate) fn new<I, P>(patterns: I) -> Result<Self>
    where
        I: IntoIterator<Item = P>,
        P: AsRef<[u8]>,
    {
        let automaton =
            AhoCorasick::new(patterns).context("Failed to build Aho-Corasick removal prefilter")?;
        Ok(Self { automaton })
    }

    /// Returns true when the line might match the removal predicate and must
    /// take the full-parse path. Lines containing "//" always return true:
    /// `normalize_url` collapses consecutive slashes, so the stored target URL
    /// may not appear literally in the raw line (false-negative hazard).
    pub(crate) fn is_candidate(&self, raw_line: &[u8]) -> bool {
        line_contains_double_slash(raw_line) || self.automaton.is_match(raw_line)
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
    parser: &LogParser,
    should_remove_entry: &F,
) -> bool
where
    F: Fn(&LogEntry) -> bool,
{
    if !prefilter.is_candidate(raw_line) {
        return false;
    }

    let Ok(text) = std::str::from_utf8(raw_line) else {
        // Not valid UTF-8 -> the regex parser could never have matched it.
        return false;
    };

    match parser.parse_line(text.trim()) {
        Some(entry) => !service_utils::should_skip_url(&entry.url) && should_remove_entry(&entry),
        None => false,
    }
}

/// Read-only scan pass: counts total lines and confirmed-match lines without
/// creating a temp file or recompressing anything.
fn scan_file_for_matches<F>(
    path: &Path,
    prefilter: &RemovalPrefilter,
    parser: &LogParser,
    should_remove_entry: &F,
    is_cancelled: &(dyn Fn() -> bool + Send + Sync),
) -> Result<Option<(u64, u64)>>
where
    F: Fn(&LogEntry) -> bool,
{
    let mut reader = LogFileReader::open(path)?;
    let mut line: Vec<u8> = Vec::with_capacity(1024);
    let mut lines_total: u64 = 0;
    let mut lines_matched: u64 = 0;

    loop {
        if is_cancelled() {
            return Ok(None);
        }
        line.clear();
        let bytes_read = reader.read_until_newline(&mut line)?;
        if bytes_read == 0 {
            break;
        }

        lines_total += 1;
        if line_should_be_removed(&line, prefilter, parser, should_remove_entry) {
            lines_matched += 1;
        }
    }

    Ok(Some((lines_total, lines_matched)))
}

fn rewrite_matching_log_entries_outcome<F>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_entry: F,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
    is_cancelled: &(dyn Fn() -> bool + Send + Sync),
) -> Result<LogRewriteOutcome>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
{
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Filtering log files to remove {} entries...", description);

    let parser = LogParser::new(chrono_tz::UTC);
    let log_files = log_discovery::discover_log_files(log_dir, "access.log")?;
    let total_files = log_files.len();

    let total_lines_removed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let other_errors = AtomicUsize::new(0);
    let cancellation_observed = std::sync::atomic::AtomicBool::new(false);
    let files_done = AtomicUsize::new(0);

    log_files
        .par_iter()
        .enumerate()
        .for_each(|(file_index, log_file)| {
            eprintln!(
                "  Processing file {}/{}: {}",
                file_index + 1,
                log_files.len(),
                log_file.path.display()
            );

            let file_result = (|| -> Result<FileRewriteOutcome> {
                if is_cancelled() {
                    return Ok(FileRewriteOutcome::Cancelled);
                }
                // Pass 1: read-only scan. Files with zero confirmed matches are left
                // completely untouched (no temp file, no recompression).
                let Some((lines_total, lines_matched)) = scan_file_for_matches(
                    &log_file.path,
                    prefilter,
                    &parser,
                    &should_remove_entry,
                    is_cancelled,
                )?
                else {
                    return Ok(FileRewriteOutcome::Cancelled);
                };

                if lines_matched == 0 {
                    return Ok(FileRewriteOutcome::Removed(0));
                }

                if lines_matched == lines_total {
                    // Every line matched: delete the file entirely (same semantics as
                    // the previous single-pass implementation, minus the wasted temp write).
                    eprintln!(
                        "  INFO: All {} lines from this file matched, deleting file entirely",
                        lines_total
                    );
                    if is_cancelled() {
                        return Ok(FileRewriteOutcome::Cancelled);
                    }
                    match cache_utils::safe_path_under_root(log_dir, &log_file.path) {
                        Ok(_) => {
                            std::fs::remove_file(&log_file.path)?;
                        }
                        Err(e) => {
                            return Err(e).with_context(|| {
                                format!("unsafe log path {}", log_file.path.display())
                            });
                        }
                    }
                    return Ok(FileRewriteOutcome::Removed(lines_matched));
                }

                // Pass 2: rewrite the file without the matching lines.
                let file_dir = log_file
                    .path
                    .parent()
                    .context("Failed to get file directory")?;
                let temp_file = NamedTempFile::new_in(file_dir)?;

                let mut lines_removed: u64 = 0;

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
                        if is_cancelled() {
                            return Ok(FileRewriteOutcome::Cancelled);
                        }
                        line.clear();
                        let bytes_read = log_reader.read_until_newline(&mut line)?;
                        if bytes_read == 0 {
                            break;
                        }

                        if line_should_be_removed(&line, prefilter, &parser, &should_remove_entry) {
                            lines_removed += 1;
                        } else {
                            writer.write_all(&line)?;
                        }
                    }

                    writer.finish()?;
                }

                let temp_path = temp_file.into_temp_path();

                if is_cancelled() {
                    return Ok(FileRewriteOutcome::Cancelled);
                }

                if let Err(persist_err) = temp_path.persist(&log_file.path) {
                    eprintln!(
                        "    persist() failed ({}), using copy fallback...",
                        persist_err
                    );
                    std::fs::copy(&persist_err.path, &log_file.path)?;
                    match cache_utils::safe_path_under_root(log_dir, &persist_err.path) {
                        Ok(_) => {
                            std::fs::remove_file(&persist_err.path).ok();
                        }
                        Err(e) => {
                            eprintln!("skipping unsafe path {}: {}", persist_err.path.display(), e);
                        }
                    }
                }

                Ok(FileRewriteOutcome::Removed(lines_removed))
            })();

            match file_result {
                Ok(FileRewriteOutcome::Removed(lines_removed)) => {
                    eprintln!("    Removed {} log lines from this file", lines_removed);
                    total_lines_removed.fetch_add(lines_removed, Ordering::Relaxed);
                }
                Ok(FileRewriteOutcome::Cancelled) => {
                    cancellation_observed.store(true, Ordering::Relaxed);
                    eprintln!(
                        "  Cancellation observed before replacing {}",
                        log_file.path.display()
                    );
                }
                Err(e) => {
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
        });

    let final_removed = total_lines_removed.load(Ordering::Relaxed);
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);
    let final_other_errors = other_errors.load(Ordering::Relaxed);
    eprintln!(
        "Total log entries removed: {}, permission errors: {}",
        final_removed, final_permission_errors
    );
    Ok(LogRewriteOutcome {
        lines_removed: final_removed,
        permission_errors: final_permission_errors,
        other_errors: final_other_errors,
        cancelled: cancellation_observed.load(Ordering::Relaxed),
    })
}

pub(crate) fn rewrite_matching_log_entries<F>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_entry: F,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
) -> Result<(u64, usize)>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
{
    let outcome = rewrite_matching_log_entries_outcome(
        log_dir,
        description,
        prefilter,
        should_remove_entry,
        on_file_processed,
        &|| false,
    )?;
    Ok((outcome.lines_removed, outcome.permission_errors))
}

/// Strict exact-evidence variant: reports both permission and non-permission file failures so the
/// caller can retain database/persisted evidence after any partial log rewrite.
pub(crate) fn rewrite_matching_log_entries_strict<F>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_entry: F,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
) -> Result<LogRewriteOutcome>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
{
    rewrite_matching_log_entries_outcome(
        log_dir,
        description,
        prefilter,
        should_remove_entry,
        on_file_processed,
        &|| false,
    )
}

/// Strict exact-evidence rewrite with cooperative cancellation. Cancellation never persists the
/// temporary rewrite for the in-flight file, reports `cancelled`, and leaves database cleanup to
/// the caller so file-before-database ordering remains enforceable.
pub(crate) fn rewrite_matching_log_entries_strict_cancellable<F, C>(
    log_dir: &Path,
    description: &str,
    prefilter: &RemovalPrefilter,
    should_remove_entry: F,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
    is_cancelled: C,
) -> Result<LogRewriteOutcome>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
    C: Fn() -> bool + Send + Sync,
{
    rewrite_matching_log_entries_outcome(
        log_dir,
        description,
        prefilter,
        should_remove_entry,
        on_file_processed,
        &is_cancelled,
    )
}

/// Rewrite every nginx access.log file under `log_dir` to drop entries whose
/// URL is in `urls_to_remove` OR whose parsed depot_id is in `valid_depot_ids`.
///
/// Returns `(lines_removed, permission_errors)`.
///
/// An optional `on_file_processed` callback is invoked after each file completes
/// (including scan-only files that needed no rewrite), receiving
/// `(files_processed_so_far, total_files)`. The callback must be
/// `Send + Sync` because files are processed in parallel via rayon.
///
/// Safe to run against a live cache host: each target file is rewritten via a
/// temp file in the same directory and then atomically persisted (or copy +
/// delete fallback) so partially-written files are never observed. Files that
/// contain no matching lines are left completely untouched.
#[allow(dead_code)]
pub(crate) fn remove_log_entries_for_game(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    valid_depot_ids: &HashSet<u32>,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
) -> Result<(u64, usize)> {
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
    )
}

#[allow(dead_code)]
pub(crate) fn remove_log_entries_for_urls(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
) -> Result<(u64, usize)> {
    let prefilter = RemovalPrefilter::new(urls_to_remove.iter().map(|url| url.as_bytes()))?;
    rewrite_matching_log_entries(
        log_dir,
        "URL-matched",
        &prefilter,
        |entry| urls_to_remove.contains(&entry.url),
        None,
    )
}

#[allow(dead_code)]
pub(crate) fn remove_log_entries_for_service(
    log_dir: &Path,
    service: &str,
    urls_to_remove: &HashSet<String>,
) -> Result<(u64, usize)> {
    let normalized_service = service_utils::normalize_service_name(service);
    let description = format!("service '{}'", service);
    let prefilter = RemovalPrefilter::new(urls_to_remove.iter().map(|url| url.as_bytes()))?;

    rewrite_matching_log_entries(
        log_dir,
        &description,
        &prefilter,
        |entry| entry.service == normalized_service && urls_to_remove.contains(&entry.url),
        None,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::DateTime;
    use std::fs;
    use std::io::Write;

    fn log_line(url: &str, cache_status: &str) -> String {
        format!(
            "[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET {} HTTP/1.1\" 200 1024 \"-\" \"Valve/Steam\" \"{}\" \"-\" \"-\"",
            url, cache_status
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

        {
            let file = fs::File::create(&gzip_path).unwrap();
            let mut encoder = flate2::write::GzEncoder::new(file, flate2::Compression::fast());
            encoder.write_all(contents.as_bytes()).unwrap();
            encoder.finish().unwrap();
        }
        {
            let file = fs::File::create(&zstd_path).unwrap();
            let mut encoder = zstd::Encoder::new(file, 1).unwrap();
            encoder.write_all(contents.as_bytes()).unwrap();
            encoder.finish().unwrap();
        }

        let matcher = ExactLogMatcher::new([target_observation()]);
        let prefilter = matcher.prefilter().unwrap();
        let outcome = rewrite_matching_log_entries_strict(
            dir.path(),
            "compressed exact corruption evidence",
            &prefilter,
            |entry| matcher.matches(entry),
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
        )
        .unwrap();
        let second = rewrite_matching_log_entries_strict(
            dir.path(),
            "idempotent exact evidence retry",
            &prefilter,
            |entry| matcher.matches(entry),
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
    fn cancellable_strict_rewrite_does_not_replace_in_flight_file() {
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
        let contents = format!("{target}\n");
        fs::write(&log_path, &contents).unwrap();
        let matcher = ExactLogMatcher::new([target_observation()]);
        let prefilter = matcher.prefilter().unwrap();

        let outcome = rewrite_matching_log_entries_strict_cancellable(
            dir.path(),
            "cancelled exact evidence",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
            || true,
        )
        .unwrap();

        assert!(outcome.cancelled);
        assert_eq!(outcome.lines_removed, 0);
        assert_eq!(fs::read_to_string(log_path).unwrap(), contents);
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
        let (lines_removed, permission_errors) =
            remove_log_entries_for_urls(dir.path(), &urls).unwrap();

        assert_eq!(lines_removed, 2);
        assert_eq!(permission_errors, 0);

        let remaining = fs::read_to_string(&log_path).unwrap();
        assert!(remaining.contains(keep_url));
        assert!(!remaining.contains("/depot/123456"));
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
        let (lines_removed, _) =
            remove_log_entries_for_game(dir.path(), &urls, &depot_ids, Some(&cb)).unwrap();
        assert_eq!(lines_removed, 0);
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
        let (lines_removed, _) = remove_log_entries_for_urls(dir.path(), &urls).unwrap();

        assert_eq!(lines_removed, 2);
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
        let (lines_removed, _) =
            remove_log_entries_for_game(dir.path(), &urls, &depot_ids, None).unwrap();

        assert_eq!(lines_removed, 1);
        let remaining = fs::read_to_string(&log_path).unwrap();
        assert!(remaining.contains("/depot/555555/"));
        assert!(!remaining.contains("/depot/424242/"));
    }
}
