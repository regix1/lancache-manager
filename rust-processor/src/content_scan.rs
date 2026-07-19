//! Read-only Status Check content scan.
//!
//! The manager's Status Check asks "did recent downloads actually traverse the cache?" by
//! sampling the tail of each log source and re-probing a few observed content paths. This module
//! is the READ side of that: it reuses the shared log-source discovery (`log_layout`) and BOTH
//! canonical line parsers (the cachelog `LogParser` and the per-service `HttpDetailedParser`) to
//! turn the most recent access-log lines into positive-cache candidate records.
//!
//! It deliberately does NOT reimplement any log grammar or source discovery, and it never touches
//! the live monitor's positions (each source's newest file is tail-read, offset-independent). The
//! security-sensitive filtering (path/SSRF safety, host DNS normalization, not-future timestamp)
//! and final sample selection stay in the C# host; here we only apply the cheap positive-cache
//! gate (GET, 2xx-with-body, HIT/MISS) so the emitted payload is bounded.

use crate::log_layout::{discover_log_sources, SourceKind};
use crate::parser::LogParser;
use crate::parser_http_detailed::HttpDetailedParser;
use anyhow::{Context, Result};
use chrono::{NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use serde::Serialize;
use std::cmp::{Ordering, Reverse};
use std::collections::BinaryHeap;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

/// Bounded tail budget per source's live file, matching the retired C# scanner's 32 MiB default.
pub const DEFAULT_MAX_TAIL_BYTES: u64 = 32 * 1024 * 1024;

/// Hard ceiling for the per-source tail budget regardless of the CLI argument, so an oversized value
/// can never request a multi-gigabyte allocation. Backstop only; the default sits far below it.
pub const MAX_TAIL_BYTES_LIMIT: u64 = 512 * 1024 * 1024;

/// Upper bound on returned candidates so an enormous log cannot produce an unbounded JSON payload.
/// The host selects a small final set from these most-recent-first records anyway.
pub const DEFAULT_MAX_SAMPLES: usize = 5000;

/// Hard ceiling for returned candidates regardless of the CLI argument, so an oversized value can
/// never demand unbounded retention. Backstop only; the default sits far below it.
pub const MAX_SAMPLES_LIMIT: usize = 100_000;

/// A live source file was read (its lines were sampled), so the check can produce a verdict.
const AVAILABILITY_AVAILABLE: &str = "available";
/// A source file exists but could not be read (locked / permissions); the check is fail-soft.
const AVAILABILITY_UNREADABLE: &str = "unreadable";
/// No log sources were discovered in the directory at all.
const AVAILABILITY_LOG_MISSING: &str = "logMissing";

/// One positive-cache candidate the host may re-probe. `target` is the request URL exactly as
/// logged (unmodified): the C# side runs its path/SSRF safety over the raw form, and normalizing
/// here would let a `//host`-style target slip past that guard.
#[derive(Debug, Serialize)]
pub struct ContentScanRecord {
    pub service: String,
    pub host: String,
    pub target: String,
    pub method: String,
    pub status_code: i32,
    pub bytes: i64,
    pub cache_status: String,
    /// RFC3339 UTC instant of the log record.
    pub timestamp: String,
    pub user_agent: String,
}

/// The scan outcome for one datasource log directory.
#[derive(Debug, Serialize)]
pub struct ContentScanOutput {
    /// `available` (a live source file was read), `unreadable` (a source file existed but could
    /// not be read), or `logMissing` (no sources discovered). The host aggregates these across
    /// datasources and downgrades `available` with zero samples to `noSamples`.
    pub availability: String,
    pub scanned_bytes: u64,
    pub truncated: bool,
    pub records: Vec<ContentScanRecord>,
}

/// Read the local timezone the same way the record processor does, so a cachelog/http-detailed
/// timestamp without an explicit offset lands on the same instant here as during ingestion.
fn local_tz_from_env() -> Tz {
    std::env::var("TZ")
        .ok()
        .and_then(|value| value.parse().ok())
        .unwrap_or(chrono_tz::UTC)
}

struct TailRead {
    lines: Vec<String>,
    bytes_read: u64,
    /// The read started past the beginning of the file, so the oldest sampled line was dropped as
    /// a partial and older history is not represented.
    truncated: bool,
}

/// One positive-cache candidate ordered solely by its log instant. Retained in a min-heap so the
/// scan holds at most `max_samples` of them at once (the newest), instead of buffering every
/// positive row and sorting the whole set at the end.
struct ScoredCandidate {
    observed_at: NaiveDateTime,
    record: ContentScanRecord,
}

impl PartialEq for ScoredCandidate {
    fn eq(&self, other: &Self) -> bool {
        self.observed_at == other.observed_at
    }
}

impl Eq for ScoredCandidate {}

impl PartialOrd for ScoredCandidate {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for ScoredCandidate {
    fn cmp(&self, other: &Self) -> Ordering {
        self.observed_at.cmp(&other.observed_at)
    }
}

/// Insert a candidate while keeping only the newest `max_samples`. The heap is a min-heap on the
/// log instant (via `Reverse`), so its peek is the oldest retained candidate: once full, a newer
/// candidate evicts that oldest and an older one is dropped. Storage stays O(`max_samples`).
fn retain_newest(
    heap: &mut BinaryHeap<Reverse<ScoredCandidate>>,
    max_samples: usize,
    candidate: ScoredCandidate,
) {
    if max_samples == 0 {
        return;
    }
    if heap.len() < max_samples {
        heap.push(Reverse(candidate));
        return;
    }
    if let Some(Reverse(oldest)) = heap.peek() {
        if candidate.observed_at > oldest.observed_at {
            heap.pop();
            heap.push(Reverse(candidate));
        }
    }
}

/// Read the last `max_tail_bytes` of `path` as complete lines, read-only. A partial first line
/// (when the read started mid-file) and a partial final line (writer mid-record) are dropped so a
/// parser never sees a truncated record. Mirrors the retired C# `ContentPathLogScanner.ReadTail`.
fn read_tail(path: &Path, max_tail_bytes: u64) -> Result<TailRead> {
    let mut file = File::open(path)
        .with_context(|| format!("Failed to open log source: {}", path.display()))?;
    let length = file
        .metadata()
        .with_context(|| format!("Failed to stat log source: {}", path.display()))?
        .len();

    let start = length.saturating_sub(max_tail_bytes);
    let capacity = usize::try_from(length - start).unwrap_or(usize::MAX);
    // Fallible allocation: an oversized tail budget returns an error instead of aborting the whole
    // process the way an infallible `vec![0u8; capacity]` would on allocation failure.
    let mut buffer: Vec<u8> = Vec::new();
    buffer.try_reserve_exact(capacity).with_context(|| {
        format!(
            "Failed to allocate {capacity} bytes to tail log source: {}",
            path.display()
        )
    })?;
    buffer.resize(capacity, 0);
    file.seek(SeekFrom::Start(start))
        .with_context(|| format!("Failed to seek log source: {}", path.display()))?;

    let mut bytes_read = 0usize;
    while bytes_read < buffer.len() {
        let read = file
            .read(&mut buffer[bytes_read..])
            .with_context(|| format!("Failed to read log source: {}", path.display()))?;
        if read == 0 {
            break;
        }
        bytes_read += read;
    }
    buffer.truncate(bytes_read);

    let mut slice: &[u8] = &buffer;
    if start > 0 {
        // Drop the partial first line so a truncated record cannot reach a parser.
        slice = match slice.iter().position(|&b| b == b'\n') {
            Some(index) => &slice[index + 1..],
            None => &[],
        };
    }
    if slice.last() != Some(&b'\n') {
        // Drop a trailing partial line (the writer may be mid-record).
        slice = match slice.iter().rposition(|&b| b == b'\n') {
            Some(index) => &slice[..index + 1],
            None => &[],
        };
    }

    let text = String::from_utf8_lossy(slice);
    // Trim each complete line before parsing, matching the ingestion path's
    // `String::from_utf8_lossy(raw).trim()` (`log_processor::classify_record`) so a
    // whitespace-decorated record the processor accepts is not rejected here: the http-detailed
    // parser is anchored on the final User-Agent quote and trailing spaces would break the match.
    let lines = text
        .split('\n')
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(str::to_string)
        .collect();

    Ok(TailRead {
        lines,
        bytes_read: bytes_read as u64,
        truncated: start > 0,
    })
}

/// Parse one line into a positive-cache candidate, or None. Canonical order (identical to the
/// record processor): the cachelog parser runs first everywhere so an explicit `[service]` tag
/// always wins; otherwise, for a per-service source, the http-detailed parser runs with the
/// stem's service hint. Manager probe lines are already rejected inside both `parse_line` calls.
fn parse_candidate(
    cachelog: &LogParser,
    detailed: &HttpDetailedParser,
    line: &str,
    kind: &SourceKind,
) -> Option<(NaiveDateTime, ContentScanRecord)> {
    let (entry, host, user_agent) = if let Some(entry) = cachelog.parse_line(line) {
        let (host, user_agent) = cachelog.extract_host_and_user_agent(line)?;
        (entry, host, user_agent)
    } else if let SourceKind::Service(service) = kind {
        let entry = detailed.parse_line(line, service)?;
        let (host, user_agent) = detailed.extract_host_and_user_agent(line)?;
        (entry, host, user_agent)
    } else {
        // Monolithic http-detailed content is hint-less (no service attribution); the fallback
        // series is never ingested. Neither yields a candidate.
        return None;
    };

    // Cheap positive-cache gate only. Path/SSRF safety and host DNS validation stay in the host.
    if entry.method != "GET"
        || !is_positive_cache_evidence(entry.status_code, entry.bytes_served, &entry.cache_status)
    {
        return None;
    }

    let observed_at = entry.timestamp;
    let timestamp = Utc.from_utc_datetime(&observed_at).to_rfc3339();
    let record = ContentScanRecord {
        service: entry.service,
        host,
        target: entry.raw_url,
        method: entry.method,
        status_code: entry.status_code,
        bytes: entry.bytes_served,
        cache_status: entry.cache_status,
        timestamp,
        user_agent,
    };
    Some((observed_at, record))
}

/// Positive cache traversal evidence: a completed request (200/206) that actually served bytes and
/// resolved to a real cache HIT or MISS. Non-2xx, zero-byte, and BYPASS/EXPIRED/UNKNOWN rows are
/// not evidence a path is cacheable.
fn is_positive_cache_evidence(status_code: i32, bytes: i64, cache_status: &str) -> bool {
    matches!(status_code, 200 | 206)
        && bytes > 0
        && (cache_status.eq_ignore_ascii_case("HIT") || cache_status.eq_ignore_ascii_case("MISS"))
}

/// Scan one datasource log directory: discover every source, tail-read each source's live file,
/// and return the most-recent-first, capped set of positive-cache candidates.
pub fn scan_directory(
    directory: &Path,
    max_tail_bytes: u64,
    max_samples: usize,
) -> Result<ContentScanOutput> {
    let local_tz = local_tz_from_env();
    let cachelog = LogParser::new(local_tz);
    let detailed = HttpDetailedParser::new(local_tz);

    let sources = discover_log_sources(directory)
        .with_context(|| format!("Failed to discover log sources in {}", directory.display()))?
        .sources;

    let mut heap: BinaryHeap<Reverse<ScoredCandidate>> = BinaryHeap::new();
    let mut scanned_bytes = 0u64;
    let mut truncated = false;
    let mut any_readable = false;
    let mut any_unreadable = false;

    for source in &sources {
        // The fallback series advances positions but its lines are never ingested; skip it here
        // too so a bare-metal fallback file can never become a probed content path.
        if matches!(source.kind, SourceKind::Fallback) {
            continue;
        }
        // A live-file tail scan reads only the CURRENT unrotated member (rotation_number == None and
        // uncompressed). A source whose only surviving member is a rotated/compressed archive (e.g.
        // just `access.log.1.gz` after rotation) has no current file to tail, so it contributes
        // nothing rather than being raw-read as compressed bytes and falsely counted as readable.
        let Some(current) = source
            .files
            .iter()
            .find(|file| file.rotation_number.is_none() && !file.is_compressed)
        else {
            continue;
        };

        match read_tail(&current.path, max_tail_bytes) {
            Ok(tail) => {
                any_readable = true;
                scanned_bytes = scanned_bytes.saturating_add(tail.bytes_read);
                truncated |= tail.truncated;
                for line in &tail.lines {
                    if let Some((observed_at, record)) =
                        parse_candidate(&cachelog, &detailed, line, &source.kind)
                    {
                        retain_newest(
                            &mut heap,
                            max_samples,
                            ScoredCandidate {
                                observed_at,
                                record,
                            },
                        );
                    }
                }
            }
            Err(_) => {
                // A source file that exists but cannot be read is a fail-soft unreadable state,
                // not a hard error: other sources are still sampled.
                any_unreadable = true;
            }
        }
    }

    // Drain the bounded newest-set and order it newest-first for the host.
    let mut scored: Vec<ScoredCandidate> = heap
        .into_iter()
        .map(|Reverse(candidate)| candidate)
        .collect();
    scored.sort_by(|a, b| b.observed_at.cmp(&a.observed_at));
    let records = scored
        .into_iter()
        .map(|candidate| candidate.record)
        .collect();

    let availability = if any_readable {
        AVAILABILITY_AVAILABLE
    } else if any_unreadable {
        AVAILABILITY_UNREADABLE
    } else {
        AVAILABILITY_LOG_MISSING
    };

    Ok(ContentScanOutput {
        availability: availability.to_string(),
        scanned_bytes,
        truncated,
        records,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write as _;

    const CACHELOG_TS: &str = "01/Jan/2024:00:00:00 +0000";
    const DETAILED_TS: &str = "01/Jan/2024:00:01:00 +0000";

    fn write_gzip(path: &Path, contents: &[u8]) {
        let file = fs::File::create(path).expect("create gzip fixture");
        let mut encoder = flate2::write::GzEncoder::new(file, flate2::Compression::default());
        encoder.write_all(contents).expect("gzip write");
        encoder.finish().expect("gzip finish");
    }

    fn write_zstd(path: &Path, contents: &[u8]) {
        let encoded = zstd::encode_all(contents, 0).expect("zstd encode");
        fs::write(path, encoded).expect("write zstd fixture");
    }

    fn cachelog_line(
        service: &str,
        target: &str,
        status: u32,
        bytes: u64,
        cache: &str,
        host: &str,
        ua: &str,
    ) -> String {
        format!(
            "[{service}] 192.168.1.50 / - - - [{CACHELOG_TS}] \"GET {target} HTTP/1.1\" {status} {bytes} \"-\" \"{ua}\" \"{cache}\" \"{host}\" \"-\""
        )
    }

    fn detailed_line(
        method: &str,
        target: &str,
        status: u32,
        body: u64,
        cache: &str,
        host: &str,
        ua: &str,
    ) -> String {
        format!(
            "[{DETAILED_TS}] 10.0.0.9 {method} \"{target}\" - HTTP/1.1 {status} \"-\" 512 {sent} {body} 0.005 {body} {cache} {host} {status} 0.004 \"{ua}\"",
            sent = body + 16,
        )
    }

    #[test]
    fn scan_directory_pulls_candidates_from_both_cachelog_and_http_detailed_sources() {
        let dir = tempfile::tempdir().expect("temp dir");

        // Monolithic cachelog source: one positive-cache steam line plus non-evidence noise.
        let access = format!(
            "{}\n{}\n{}\n{}\n",
            cachelog_line(
                "steam",
                "/depot/1/chunk/a",
                200,
                1024,
                "HIT",
                "cache.steamcontent.com",
                "Valve/Steam"
            ),
            // Non-GET request: excluded.
            cachelog_line(
                "steam",
                "/depot/1/chunk/b",
                200,
                1024,
                "HIT",
                "cache.steamcontent.com",
                "Valve/Steam"
            )
            .replace("GET ", "HEAD "),
            // Manager probe UA: excluded by the parser.
            cachelog_line(
                "steam",
                "/depot/1/chunk/c",
                200,
                1024,
                "HIT",
                "cache.steamcontent.com",
                "lancache-manager-status-check/1.0"
            ),
            // BYPASS is not positive cache evidence: excluded.
            cachelog_line(
                "steam",
                "/depot/1/chunk/d",
                200,
                1024,
                "BYPASS",
                "cache.steamcontent.com",
                "Valve/Steam"
            ),
        );
        fs::write(dir.path().join("access.log"), access).expect("write access.log");

        // Per-service (bare-metal) http-detailed source: one positive-cache riot line plus a
        // zero-byte line that is not evidence.
        let riot = format!(
            "{}\n{}\n",
            detailed_line(
                "GET",
                "/channels/public/bundles/x.bundle",
                200,
                4096,
                "MISS",
                "lol.dyn.riotcdn.net",
                "riot-client"
            ),
            detailed_line(
                "GET",
                "/channels/public/bundles/y.bundle",
                200,
                0,
                "MISS",
                "lol.dyn.riotcdn.net",
                "riot-client"
            ),
        );
        fs::write(dir.path().join("riot-access.log"), riot).expect("write riot-access.log");

        // An empty per-service file must contribute readability but zero records.
        fs::write(dir.path().join("steam-access.log"), b"").expect("write empty steam file");

        let output =
            scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, DEFAULT_MAX_SAMPLES).expect("scan");

        assert_eq!(output.availability, AVAILABILITY_AVAILABLE);
        assert!(!output.truncated);
        assert_eq!(
            output.records.len(),
            2,
            "exactly the two positive-cache lines survive"
        );

        let steam = output
            .records
            .iter()
            .find(|r| r.service == "steam")
            .expect("steam candidate from the monolithic cachelog source");
        assert_eq!(steam.host, "cache.steamcontent.com");
        assert_eq!(steam.target, "/depot/1/chunk/a");
        assert_eq!(steam.cache_status, "HIT");
        assert_eq!(steam.bytes, 1024);

        let riot = output
            .records
            .iter()
            .find(|r| r.service == "riot")
            .expect("riot candidate from the per-service http-detailed source");
        // Filename hint attributes the service; the http-detailed $host is the request host.
        assert_eq!(riot.host, "lol.dyn.riotcdn.net");
        assert_eq!(riot.target, "/channels/public/bundles/x.bundle");
        assert_eq!(riot.cache_status, "MISS");

        // Most-recent-first ordering: the http-detailed record has the later timestamp.
        assert_eq!(output.records[0].service, "riot");
    }

    #[test]
    fn scan_directory_reports_available_with_no_records_for_empty_per_service_files() {
        let dir = tempfile::tempdir().expect("temp dir");
        fs::write(dir.path().join("steam-access.log"), b"").expect("write empty steam file");
        fs::write(dir.path().join("blizzard-access.log"), b"").expect("write empty blizzard file");

        let output =
            scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, DEFAULT_MAX_SAMPLES).expect("scan");

        assert_eq!(output.availability, AVAILABILITY_AVAILABLE);
        assert!(output.records.is_empty());
        assert_eq!(output.scanned_bytes, 0);
    }

    #[test]
    fn scan_directory_reports_log_missing_for_empty_directory() {
        let dir = tempfile::tempdir().expect("temp dir");
        let output =
            scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, DEFAULT_MAX_SAMPLES).expect("scan");
        assert_eq!(output.availability, AVAILABILITY_LOG_MISSING);
        assert!(output.records.is_empty());
    }

    #[test]
    fn scan_directory_caps_records_most_recent_first() {
        let dir = tempfile::tempdir().expect("temp dir");
        // Three distinct-second timestamps; the cap must keep the newest two.
        let mut lines = String::new();
        for (minute, target) in [("00", "/a"), ("01", "/b"), ("02", "/c")] {
            lines.push_str(&format!(
                "[steam] 192.168.1.50 / - - - [01/Jan/2024:00:{minute}:00 +0000] \"GET {target} HTTP/1.1\" 200 1024 \"-\" \"Valve/Steam\" \"HIT\" \"cache.steamcontent.com\" \"-\"\n"
            ));
        }
        fs::write(dir.path().join("access.log"), lines).expect("write access.log");

        let output = scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, 2).expect("scan");

        assert_eq!(output.records.len(), 2);
        assert_eq!(output.records[0].target, "/c");
        assert_eq!(output.records[1].target, "/b");
    }

    #[test]
    fn scan_directory_ignores_rotation_only_gz_source() {
        // Only a compressed, rotated member survives (no current unrotated file). Its DECOMPRESSED
        // bytes would parse as a positive-cache line, but a live-file tail scan must never raw-read
        // a rotated archive: the source contributes nothing, not a false readable/record. If the
        // scan read the gzip bytes it would report `available`/nonzero scanned_bytes instead.
        let dir = tempfile::tempdir().expect("temp dir");
        let line = format!(
            "{}\n",
            cachelog_line(
                "steam",
                "/depot/1/chunk/a",
                200,
                1024,
                "HIT",
                "cache.steamcontent.com",
                "Valve/Steam"
            )
        );
        write_gzip(&dir.path().join("access.log.1.gz"), line.as_bytes());

        let output =
            scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, DEFAULT_MAX_SAMPLES).expect("scan");

        assert_eq!(output.availability, AVAILABILITY_LOG_MISSING);
        assert!(output.records.is_empty());
        assert_eq!(
            output.scanned_bytes, 0,
            "a rotated archive is never raw-read"
        );
    }

    #[test]
    fn scan_directory_ignores_rotation_only_zst_source() {
        // Same rule for a per-service zstd rotation: no current file means the source is skipped,
        // never decoded as lossy text off the raw compressed bytes.
        let dir = tempfile::tempdir().expect("temp dir");
        let line = format!(
            "{}\n",
            detailed_line(
                "GET",
                "/channels/public/bundles/x.bundle",
                200,
                4096,
                "MISS",
                "lol.dyn.riotcdn.net",
                "riot-client"
            )
        );
        write_zstd(&dir.path().join("riot-access.log.2.zst"), line.as_bytes());

        let output =
            scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, DEFAULT_MAX_SAMPLES).expect("scan");

        assert_eq!(output.availability, AVAILABILITY_LOG_MISSING);
        assert!(output.records.is_empty());
        assert_eq!(
            output.scanned_bytes, 0,
            "a rotated archive is never raw-read"
        );
    }

    #[test]
    fn scan_directory_reads_current_file_beside_a_compressed_rotation() {
        // The current unrotated file is tailed even when an older compressed rotation sits beside
        // it; the rotation is ignored, the live file's positive-cache line survives.
        let dir = tempfile::tempdir().expect("temp dir");
        write_gzip(
            &dir.path().join("access.log.1.gz"),
            format!(
                "{}\n",
                cachelog_line(
                    "steam",
                    "/depot/1/chunk/old",
                    200,
                    1024,
                    "HIT",
                    "cache.steamcontent.com",
                    "Valve/Steam"
                )
            )
            .as_bytes(),
        );
        fs::write(
            dir.path().join("access.log"),
            format!(
                "{}\n",
                cachelog_line(
                    "steam",
                    "/depot/1/chunk/current",
                    200,
                    1024,
                    "HIT",
                    "cache.steamcontent.com",
                    "Valve/Steam"
                )
            ),
        )
        .expect("write access.log");

        let output =
            scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, DEFAULT_MAX_SAMPLES).expect("scan");

        assert_eq!(output.availability, AVAILABILITY_AVAILABLE);
        assert_eq!(output.records.len(), 1);
        assert_eq!(output.records[0].target, "/depot/1/chunk/current");
    }

    #[test]
    fn scan_directory_trims_whitespace_decorated_http_detailed_lines() {
        // A per-service http-detailed line with a leading space and trailing spaces after the final
        // quoted User-Agent is accepted by canonical ingestion (which trims) and must be accepted
        // here too, or a readable log would falsely produce zero samples.
        let dir = tempfile::tempdir().expect("temp dir");
        let decorated = format!(
            "   {}     \n",
            detailed_line(
                "GET",
                "/channels/public/bundles/x.bundle",
                200,
                4096,
                "MISS",
                "lol.dyn.riotcdn.net",
                "riot-client"
            )
        );
        fs::write(dir.path().join("riot-access.log"), decorated).expect("write riot-access.log");

        let output =
            scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, DEFAULT_MAX_SAMPLES).expect("scan");

        assert_eq!(output.availability, AVAILABILITY_AVAILABLE);
        let record = output
            .records
            .iter()
            .find(|r| r.service == "riot")
            .expect("trimmed riot candidate survives");
        assert_eq!(record.target, "/channels/public/bundles/x.bundle");
        assert_eq!(record.host, "lol.dyn.riotcdn.net");
    }

    #[test]
    fn scan_directory_retains_only_max_samples_newest_across_high_volume_source() {
        // Far more positive rows than the cap: retention stays bounded to max_samples (a min-heap,
        // never the whole positive set) and keeps exactly the newest ones.
        let dir = tempfile::tempdir().expect("temp dir");
        let total = 2000usize;
        let max_samples = 50usize;
        let mut lines = String::new();
        for index in 0..total {
            let minute = index / 60;
            let second = index % 60;
            lines.push_str(&format!(
                "[steam] 192.168.1.50 / - - - [01/Jan/2024:00:{minute:02}:{second:02} +0000] \"GET /chunk/{index} HTTP/1.1\" 200 1024 \"-\" \"Valve/Steam\" \"HIT\" \"cache.steamcontent.com\" \"-\"\n"
            ));
        }
        fs::write(dir.path().join("access.log"), lines).expect("write access.log");

        let output = scan_directory(dir.path(), DEFAULT_MAX_TAIL_BYTES, max_samples).expect("scan");

        assert_eq!(
            output.records.len(),
            max_samples,
            "retention capped at max_samples"
        );
        // Newest first, and the retained window is exactly the newest max_samples rows.
        assert_eq!(output.records[0].target, format!("/chunk/{}", total - 1));
        assert_eq!(
            output.records[max_samples - 1].target,
            format!("/chunk/{}", total - max_samples)
        );
    }
}
