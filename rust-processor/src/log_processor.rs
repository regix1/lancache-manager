use anyhow::{Context, Result};
use chrono::{TimeZone, Utc};
use chrono_tz::Tz;
use clap::Parser;
use serde::Serialize;
use sqlx::PgPool;
use sqlx::Row;
use std::collections::{HashMap, HashSet};
use std::env;
use std::future::Future;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use lancache_processor::cache_utils;
use lancache_processor::cancel;
use lancache_processor::db;
use lancache_processor::log_discovery;
use lancache_processor::log_layout;
use lancache_processor::log_purge;
use lancache_processor::log_reader;
use lancache_processor::log_resume;
use lancache_processor::models;
use lancache_processor::parser;
use lancache_processor::parser_http_detailed;
use lancache_processor::progress_events;
use lancache_processor::progress_utils;
use lancache_processor::riot_hosts;
use lancache_processor::service_utils;
use lancache_processor::tact_products;
use progress_events::ProgressReporter;

/// Log processor utility - parses lancache access logs and stores in database
#[derive(clap::Parser, Debug)]
#[command(name = "log_processor")]
#[command(about = "Parses lancache access logs and stores entries in the database")]
struct Args {
    /// Directory containing log files (e.g., H:/logs)
    log_dir: String,

    /// Path to progress JSON file
    progress_path: String,

    /// Map depot IDs to games during processing (1=yes, 0=no)
    auto_map_depots: u8,

    /// Datasource name the stored entries are tagged with
    datasource_name: String,

    /// Path to the per-source positions JSON: where each log source resumes
    positions_path: String,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,
}

use log_discovery::LogFile;
use log_layout::{discover_log_sources, IgnoredReason, LogSource, ParseOutcome, SourceKind};
use log_reader::LogFileReader;
use models::*;
use parser::LogParser;
use parser_http_detailed::HttpDetailedParser;
use std::collections::BTreeMap;

/// Version of the positions-file and progress-file contract.
const CHECKPOINT_SCHEMA_VERSION: u32 = 1;

/// Per-source-stem series offsets, written by the C# host. Each value is one offset into
/// that stem's oldest -> newest rotation series (the same aggregate line-count semantics
/// the monolithic path has always used, per stem — never per physical file name).
#[derive(serde::Deserialize, Debug)]
struct PositionsFile {
    schema_version: u32,
    sources: HashMap<String, u64>,
}

/// Classify one raw record. `complete` is false only for a final record with no trailing
/// newline. Classification is structural and never depends on a DB outcome; probe and
/// heartbeat checks run BEFORE parsing so synthetic traffic never counts as unparsed.
fn classify_record(
    parser: &LogParser,
    detailed_parser: &HttpDetailedParser,
    raw: &[u8],
    complete: bool,
    kind: &SourceKind,
) -> ParseOutcome {
    if !complete {
        return ParseOutcome::Incomplete;
    }

    let had_invalid_utf8 = std::str::from_utf8(raw).is_err();
    let lossy = String::from_utf8_lossy(raw);
    let line = lossy.trim();

    if line.is_empty() {
        return ParseOutcome::RecognizedIgnored(IgnoredReason::Blank);
    }
    if matches!(kind, SourceKind::Fallback) {
        return ParseOutcome::RecognizedIgnored(IgnoredReason::Fallback);
    }
    if service_utils::is_manager_probe(line) {
        return ParseOutcome::RecognizedIgnored(IgnoredReason::Probe);
    }

    // The cachelog parser runs first everywhere: an explicit `[service]` tag always
    // wins over a filename hint. (An http-detailed record can never match its regex.)
    if let Some(entry) = parser.parse_line(line) {
        if service_utils::should_skip_url(&entry.url) {
            return ParseOutcome::RecognizedIgnored(IgnoredReason::Heartbeat);
        }
        return ParseOutcome::Parsed(entry);
    }

    match kind {
        SourceKind::Service(service) => {
            if let Some(entry) = detailed_parser.parse_line(line, service) {
                if service_utils::should_skip_url(&entry.url) {
                    return ParseOutcome::RecognizedIgnored(IgnoredReason::Heartbeat);
                }
                return ParseOutcome::Parsed(entry);
            }
        }
        SourceKind::Monolithic => {
            // The reporting-user case: http-detailed content in access.log. The
            // format is recognized but there is no service attribution.
            if detailed_parser.recognizes(line) {
                return ParseOutcome::RecognizedIgnored(IgnoredReason::Hintless);
            }
        }
        SourceKind::Fallback => {}
    }

    if had_invalid_utf8 {
        ParseOutcome::InvalidEncoding
    } else {
        ParseOutcome::Unrecognized
    }
}

/// Load and validate a supplied positions file. A supplied-but-invalid file is a hard
/// failure BEFORE any database work: silently defaulting every source to offset 0 would
/// re-ingest the full history.
fn load_positions(path: &str) -> Result<HashMap<String, u64>> {
    let raw = std::fs::read_to_string(path)
        .map_err(|e| anyhow::anyhow!("positions file {} unreadable: {}", path, e))?;
    if raw.trim().is_empty() {
        return Err(anyhow::anyhow!("positions file {} is empty", path));
    }
    let parsed: PositionsFile = serde_json::from_str(&raw)
        .map_err(|e| anyhow::anyhow!("positions file {} malformed: {}", path, e))?;
    if parsed.schema_version != CHECKPOINT_SCHEMA_VERSION {
        return Err(anyhow::anyhow!(
            "positions file {} has unsupported schema_version {} (expected {})",
            path,
            parsed.schema_version,
            CHECKPOINT_SCHEMA_VERSION
        ));
    }
    Ok(parsed.sources)
}

const BULK_BATCH_SIZE: usize = 5_000;
const SESSION_GAP_MINUTES: i64 = 5;
const LINE_BUFFER_CAPACITY: usize = 1024;
const LOG_ENTRY_INSERT_SQL: &str = r#"INSERT INTO "LogEntries" ("Timestamp", "ClientIp", "Service", "Method", "HttpRange", "Url", "StatusCode", "BytesServed", "CacheStatus", "DepotId", "DownloadId", "CreatedAt", "Datasource")
       SELECT * FROM UNNEST($1::timestamptz[], $2::text[], $3::text[], $4::text[], $5::text[], $6::text[], $7::int[], $8::bigint[], $9::text[], $10::bigint[], $11::bigint[], $12::timestamptz[], $13::text[])"#;

/// Character limits for bounded string columns on "LogEntries". Every value is clamped
/// to its varchar width before insert so one oversized value from any service cannot
/// abort the whole batch transaction.
const LOG_ENTRY_CLIENT_IP_MAX_CHARS: usize = 50;
const LOG_ENTRY_SERVICE_MAX_CHARS: usize = 50;
const LOG_ENTRY_VARCHAR_MAX_CHARS: usize = 16;
const LOG_ENTRY_HTTP_RANGE_MAX_CHARS: usize = 2000;
const LOG_ENTRY_URL_MAX_CHARS: usize = 2000;
const LOG_ENTRY_DATASOURCE_MAX_CHARS: usize = 100;

/// Clamp a value to a column's character limit without splitting a char boundary.
fn clamp_chars(value: &str, max_chars: usize) -> String {
    match value.char_indices().nth(max_chars) {
        Some((byte_index, _)) => value[..byte_index].to_string(),
        None => value.to_string(),
    }
}

/// Throttle interval for reloading the Xbox CDN fragment patterns from the DB during a run.
const XBOX_PATTERN_RELOAD: Duration = Duration::from_secs(60);

/// Buffered log entry ready for bulk INSERT - owns its data to avoid lifetime issues across session groups
struct PendingLogEntry {
    timestamp: chrono::DateTime<Utc>,
    client_ip: String,
    service: String,
    method: String,
    http_range: String,
    url: String,
    status_code: i32,
    bytes_served: i64,
    cache_status: String,
    depot_id: Option<i64>,
    download_id: i64,
    created_at: chrono::DateTime<Utc>,
    datasource: String,
}

/// Splits a session's served bytes into the cache-hit and cache-miss totals a download row carries.
///
/// Together the two totals ARE the download's size on every page that reads it. This used to match
/// `== "HIT"` and `== "MISS"` exactly, so a line whose status was anything else - a different case, or
/// one of the EXPIRED and STALE values the parser preserves verbatim - counted as neither, and a
/// session made of those recorded zero bytes while its log entries kept the real figures. The pages
/// then hid it as an empty session.
///
/// HIT is matched the way the speed tracker matches it, ignoring case; everything else that served
/// bytes is a miss, BYPASS aside. nginx never consulted the cache on a BYPASS line, so no file was
/// written, and the eviction scan reads these two totals as proof that one was: counting bypassed
/// bytes as a miss makes such a download claim content that can never be found on disk, which flags
/// it evicted forever. A session of nothing but BYPASS keeps zero bytes and stays hidden.
///
/// The cache-status vocabulary still belongs to whoever reads `LogEntries."CacheStatus"`, which keeps
/// the literal value: corruption detection reads it there and needs the distinctions this sum
/// deliberately flattens.
fn split_hit_miss_bytes<'a>(entries: impl Iterator<Item = (&'a str, i64)>) -> (i64, i64) {
    let mut hit = 0i64;
    let mut miss = 0i64;
    for (status, bytes) in entries {
        if status.eq_ignore_ascii_case("HIT") {
            hit += bytes;
        } else if !status.eq_ignore_ascii_case("BYPASS") {
            miss += bytes;
        }
    }
    (hit, miss)
}

#[derive(Serialize)]
struct Progress {
    /// Total line count is only known once processing finishes (the expensive
    /// line-counting pre-pass was removed). 0 while running; set to the real
    /// count on the final "completed" write so the host can persist it.
    total_lines: u64,
    lines_parsed: u64,
    entries_saved: u64,
    /// Raw (compressed) bytes consumed so far across all files.
    bytes_processed: u64,
    /// Sum of on-disk file sizes for every discovered log file.
    total_bytes: u64,
    percent_complete: f64,
    status: String,
    message: String,
    timestamp: String,
    // NOTE: warnings/errors removed - they're only used for C# logging (stderr capture)
    // and are NOT displayed in UI. Keeping them caused unbounded memory growth.
    /// Contract version of this file. The C# host rejects checkpoints it does not know.
    schema_version: u32,
    /// Unique id of this processor run; lets the host match a terminal checkpoint to the
    /// process it actually launched.
    run_id: String,
    /// Empty while running. On the final write: completed | completed_with_warnings |
    /// partial | failed | cancelled. This polled file is the single authority — process
    /// exit 0 without a valid terminal checkpoint is treated as failure by the host.
    terminal_status: String,
    /// Presentation-only source layout: monolithic | bare_metal | mixed ("" until known).
    layout: String,
    /// Per-source-stem series line counts as consumed by this run. Only authoritative on
    /// a completed / completed_with_warnings terminal write.
    source_positions: BTreeMap<String, u64>,
    /// Complete records no recognizer accepted.
    unparsed_lines: u64,
    /// http-detailed records found in a hint-less file (e.g. a renamed access.log):
    /// recognized but unattributable, so they cannot ingest.
    hintless_http_detailed_lines: u64,
    /// Records in fallback-access.log: position advances, never ingested.
    skipped_fallback_lines: u64,
    /// Records with invalid UTF-8 that no recognizer accepted even after lossy decoding.
    invalid_encoding_lines: u64,
    /// Manager probes, heartbeats and blank records — recognized synthetic traffic,
    /// never a warning signal.
    recognized_ignored_lines: u64,
    /// Unterminated final records (writer mid-line at EOF); never counted toward positions.
    incomplete_final_records: u64,
    /// Unique Riot CDN hosts observed and successfully resolved during this processor run.
    riot_hosts_processed: u64,
    riot_hosts_mapped: u64,
    /// "path: error" for every file that failed mid-run. Non-empty ⇒ terminal is at best
    /// `partial`, never plain `completed`.
    files_with_errors: Vec<String>,
}

fn seed_progress(run_id: &str, status: &str, terminal_status: &str, message: &str) -> Progress {
    Progress {
        total_lines: 0,
        lines_parsed: 0,
        entries_saved: 0,
        bytes_processed: 0,
        total_bytes: 0,
        percent_complete: 0.0,
        status: status.to_string(),
        message: message.to_string(),
        timestamp: progress_utils::current_timestamp(),
        schema_version: CHECKPOINT_SCHEMA_VERSION,
        run_id: run_id.to_string(),
        terminal_status: terminal_status.to_string(),
        layout: String::new(),
        source_positions: BTreeMap::new(),
        unparsed_lines: 0,
        hintless_http_detailed_lines: 0,
        skipped_fallback_lines: 0,
        invalid_encoding_lines: 0,
        recognized_ignored_lines: 0,
        incomplete_final_records: 0,
        riot_hosts_processed: 0,
        riot_hosts_mapped: 0,
        files_with_errors: Vec::new(),
    }
}

fn write_seed_failure_terminal(progress_path: &Path, run_id: &str, message: &str) -> Result<()> {
    let failed = seed_progress(run_id, "failed", "failed", message);
    progress_utils::write_progress_with_retry(progress_path, &failed, 5)
}

async fn create_pool_or_write_terminal<F, Fut>(
    progress_path: &Path,
    run_id: &str,
    create_pool: F,
) -> Result<PgPool>
where
    F: FnOnce() -> Fut,
    Fut: Future<Output = Result<PgPool>>,
{
    match create_pool().await {
        Ok(pool) => Ok(pool),
        Err(error) => {
            let message = format!("Failed to create database pool: {error:#}");
            if let Err(write_error) = write_seed_failure_terminal(progress_path, run_id, &message) {
                eprintln!("Warning: failed to write failure checkpoint: {write_error:#}");
            }
            Err(anyhow::anyhow!(message))
        }
    }
}

struct Processor {
    pool: PgPool,
    log_dir: PathBuf,
    progress_path: PathBuf,
    /// Per-stem start offsets from the positions file; a stem not listed starts at 0.
    positions: HashMap<String, u64>,
    resume_path: Option<PathBuf>,
    run_id: String,
    /// Presentation-only layout of the discovered sources ("" until discovery runs).
    layout: String,
    /// Live per-stem series line counts (skipped + processed complete records).
    source_positions: BTreeMap<String, u64>,
    unparsed_lines: u64,
    hintless_http_detailed_lines: u64,
    skipped_fallback_lines: u64,
    invalid_encoding_lines: u64,
    recognized_ignored_lines: u64,
    incomplete_final_records: u64,
    files_with_errors: Vec<String>,
    rotated_member_errors: usize,
    parser: LogParser,
    detailed_parser: HttpDetailedParser,
    total_lines: AtomicU64,
    lines_parsed: AtomicU64,
    entries_saved: AtomicU64,
    /// Sum of on-disk file sizes for all discovered log files (the progress denominator).
    total_bytes: u64,
    /// Raw bytes of fully-processed (or skipped-with-error) files.
    bytes_completed: AtomicU64,
    /// Raw bytes consumed from the file currently being read (shared with LogFileReader).
    current_file_bytes: Arc<AtomicU64>,
    /// On-disk size of the file currently being read (clamps read-ahead overshoot).
    current_file_size: AtomicU64,
    auto_map_depots: bool,
    last_logged_percent: AtomicU64, // Store as integer (0-100) for atomic operations
    logged_depots: HashSet<u32>,    // Track depots that have already been logged
    logged_tact_products: HashSet<String>, // Track Blizzard TACT products already logged
    riot_mapping: riot_hosts::RiotMappingCounters,
    datasource_name: String,
    /// Depot -> (AppId, AppName) memo, filled lazily per depot actually seen in a batch.
    /// The old shape preloaded the WHOLE owner-mapping table here on every spawn - and this
    /// binary is respawned every second by LiveLogMonitorService, so an idle box paid a
    /// full-table SELECT plus a tens-of-MB HashMap rebuild per second to resolve at most a
    /// handful of new depots.
    depot_map: HashMap<u32, (u32, Option<String>)>,
    /// Depots confirmed to have no owner mapping this run (negative memo, so an unmapped
    /// depot is queried at most once per run - mirroring the old start-of-run snapshot).
    depots_unmapped: HashSet<u32>,
    skip_dedup: bool, // True when table is empty - skip duplicate checks for max speed
    /// Xbox CDN fragment -> (title, product_id), longest fragment first. Xbox content arrives as
    /// lancache-tagged `wsus` traffic over opaque /filestreamingservice/files/<GUID> URLs;
    /// a stored XboxCdnPattern.UrlFragment match canonicalizes the Download to Service='xbox'
    /// + GameName=title + XboxProductId=id at ingest. Reloaded periodically (the tables fill as
    ///   daemons contribute fragments). Empty until the first wsus line triggers a load.
    xbox_patterns: Vec<(String, String, String)>,
    /// Last time `xbox_patterns` was loaded; throttles reloads to once per `XBOX_PATTERN_RELOAD`.
    last_xbox_pattern_load: Option<Instant>,
    /// Per-URL Xbox NEGATIVE resolution cache, keyed by the URL's md5 digest (16 bytes; a
    /// collision would need two crafted URLs in one run - not a realistic log shape). A full
    /// reprocess sees every unique wsus URL, and the old String-keyed map holding each URL
    /// (mostly for None results) grew to hundreds of MB.
    xbox_url_negative: HashSet<u128>,
    /// Per-URL Xbox POSITIVE resolutions (rare - only matched game URLs), same digest key.
    xbox_url_positive: HashMap<u128, (String, String)>,
    #[cfg(test)]
    before_resume_open: Option<Box<dyn FnMut() + Send>>,
    #[cfg(test)]
    before_content_open: Option<Box<dyn FnMut(&Path) + Send>>,
    #[cfg(test)]
    read_failure: Option<PathBuf>,
    #[cfg(test)]
    open_failure: Option<PathBuf>,
    #[cfg(test)]
    open_attempts: HashMap<PathBuf, u64>,
    #[cfg(test)]
    parsed_records: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ProcessingOutcome {
    Completed,
    Cancelled,
}

/// A file result is deliberately typed so cooperative cancellation can never be folded
/// into the same branch as a fully consumed file by a future caller.
#[derive(Debug, Clone, PartialEq, Eq)]
enum FileProcessingOutcome {
    Completed,
    SourceBlockedByIncompleteRecord,
    RotatedMemberUnreadable(RotatedMemberProblem),
    ResumeTargetChanged,
    Cancelled,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct RotatedMemberProblem {
    kind: RotatedMemberProblemKind,
    message: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum RotatedMemberProblemKind {
    Open,
    Read,
    UnterminatedTail,
}

#[derive(Clone, Default)]
struct FileResume {
    offset: u64,
    expected: Option<(log_purge::FileIdentity, u32)>,
    opened: Option<log_purge::FileIdentity>,
    opened_len: Option<u64>,
    observed_len: Option<u64>,
    mark: Option<log_resume::FileMark>,
    held_skip: u64,
    #[cfg(test)]
    fail_read: bool,
}

impl FileResume {
    fn read_until_newline(
        &mut self,
        reader: &mut LogFileReader,
        buffer: &mut Vec<u8>,
    ) -> Result<usize> {
        #[cfg(test)]
        if self.fail_read {
            self.fail_read = false;
            return Err(anyhow::anyhow!("injected read failure"));
        }
        reader.read_until_newline(buffer)
    }
}

impl Processor {
    fn new(
        pool: PgPool,
        log_dir: PathBuf,
        progress_path: PathBuf,
        auto_map_depots: bool,
        datasource_name: String,
        positions: HashMap<String, u64>,
        run_id: String,
    ) -> Self {
        // Get timezone from environment variable (same as C# uses)
        let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
        let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);
        eprintln!("Using timezone: {} (from TZ env var)", local_tz);
        eprintln!("Auto-map depots: {}", auto_map_depots);
        eprintln!("Datasource: {}", datasource_name);

        Self {
            pool,
            log_dir,
            progress_path,
            positions,
            resume_path: None,
            run_id,
            layout: String::new(),
            source_positions: BTreeMap::new(),
            unparsed_lines: 0,
            hintless_http_detailed_lines: 0,
            skipped_fallback_lines: 0,
            invalid_encoding_lines: 0,
            recognized_ignored_lines: 0,
            incomplete_final_records: 0,
            files_with_errors: Vec::new(),
            rotated_member_errors: 0,
            parser: LogParser::new(local_tz),
            detailed_parser: HttpDetailedParser::new(local_tz),
            total_lines: AtomicU64::new(0),
            lines_parsed: AtomicU64::new(0),
            entries_saved: AtomicU64::new(0),
            total_bytes: 0,
            bytes_completed: AtomicU64::new(0),
            current_file_bytes: Arc::new(AtomicU64::new(0)),
            current_file_size: AtomicU64::new(0),
            auto_map_depots,
            last_logged_percent: AtomicU64::new(0),
            logged_depots: HashSet::new(),
            logged_tact_products: HashSet::new(),
            riot_mapping: riot_hosts::RiotMappingCounters::default(),
            datasource_name,
            depot_map: HashMap::new(),
            depots_unmapped: HashSet::new(),
            skip_dedup: false,
            xbox_patterns: Vec::new(),
            last_xbox_pattern_load: None,
            xbox_url_negative: HashSet::new(),
            xbox_url_positive: HashMap::new(),
            #[cfg(test)]
            before_resume_open: None,
            #[cfg(test)]
            before_content_open: None,
            #[cfg(test)]
            read_failure: None,
            #[cfg(test)]
            open_failure: None,
            #[cfg(test)]
            open_attempts: HashMap::new(),
            #[cfg(test)]
            parsed_records: 0,
        }
    }

    /// Convert UTC NaiveDateTime to local timezone NaiveDateTime
    /// Returns a naive datetime representing the same instant in the target timezone
    /// Raw (compressed) bytes consumed so far: completed files contribute their
    /// full on-disk size; the in-flight file contributes its underlying stream
    /// position, clamped to its size to absorb BufReader read-ahead overshoot.
    fn bytes_processed(&self) -> u64 {
        let current = self
            .current_file_bytes
            .load(Ordering::Relaxed)
            .min(self.current_file_size.load(Ordering::Relaxed));
        self.bytes_completed.load(Ordering::Relaxed) + current
    }

    /// Byte-based progress percent (replaces the deleted line-count pre-pass).
    fn percent_complete(&self) -> f64 {
        if self.total_bytes > 0 {
            ((self.bytes_processed() as f64 / self.total_bytes as f64) * 100.0).min(100.0)
        } else {
            0.0
        }
    }

    fn make_progress(&self, status: &str, terminal_status: &str, message: &str) -> Progress {
        Progress {
            total_lines: self.total_lines.load(Ordering::Relaxed),
            lines_parsed: self.lines_parsed.load(Ordering::Relaxed),
            entries_saved: self.entries_saved.load(Ordering::Relaxed),
            bytes_processed: self.bytes_processed(),
            total_bytes: self.total_bytes,
            percent_complete: self.percent_complete(),
            status: status.to_string(),
            message: message.to_string(),
            timestamp: progress_utils::current_timestamp(),
            schema_version: CHECKPOINT_SCHEMA_VERSION,
            run_id: self.run_id.clone(),
            terminal_status: terminal_status.to_string(),
            layout: self.layout.clone(),
            source_positions: self.source_positions.clone(),
            unparsed_lines: self.unparsed_lines,
            hintless_http_detailed_lines: self.hintless_http_detailed_lines,
            skipped_fallback_lines: self.skipped_fallback_lines,
            invalid_encoding_lines: self.invalid_encoding_lines,
            recognized_ignored_lines: self.recognized_ignored_lines,
            incomplete_final_records: self.incomplete_final_records,
            riot_hosts_processed: self.riot_mapping.processed(),
            riot_hosts_mapped: self.riot_mapping.mapped(),
            files_with_errors: self.files_with_errors.clone(),
        }
    }

    fn write_progress(&self, status: &str, message: &str) -> Result<()> {
        let progress = self.make_progress(status, "", message);
        // Use shared progress writing utility with retry logic
        progress_utils::write_progress_with_retry(&self.progress_path, &progress, 5)
    }

    /// The initial informational tick must not prevent the processor from reaching an
    /// authoritative terminal checkpoint. Later terminal writes remain mandatory.
    fn write_starting_progress_best_effort(&self, message: &str) {
        if let Err(error) = self.write_progress("starting", message) {
            eprintln!("Warning: failed to write starting progress: {error:#}");
        }
    }

    /// Final authoritative checkpoint. `terminal_status` carries the typed outcome; the
    /// legacy `status` field keeps its historical vocabulary for anything still reading it.
    fn write_terminal(&self, terminal_status: &str, status: &str, message: &str) -> Result<()> {
        let progress = self.make_progress(status, terminal_status, message);
        progress_utils::write_progress_with_retry(&self.progress_path, &progress, 5)
    }

    fn write_cancelled_terminal(&self) -> Result<()> {
        let parsed = self.lines_parsed.load(Ordering::Relaxed);
        let saved = self.entries_saved.load(Ordering::Relaxed);
        self.write_terminal(
            "cancelled",
            "cancelled",
            &format!(
                "Cancelled: {} lines parsed, {} entries saved",
                parsed, saved
            ),
        )
    }

    fn classify_record(&self, raw: &[u8], complete: bool, kind: &SourceKind) -> ParseOutcome {
        classify_record(&self.parser, &self.detailed_parser, raw, complete, kind)
    }

    async fn process(&mut self) -> Result<ProcessingOutcome> {
        eprintln!("Starting log processing...");
        eprintln!("Log directory: {}", self.log_dir.display());

        // Discover every source: the access.log stem AND every *-access.log stem
        // (with the logs/ -> logs/http descent for the bare-metal parent-dir shape).
        let source_set = match discover_log_sources(&self.log_dir) {
            Ok(source_set) => source_set,
            Err(error) => {
                let message = format!("Failed to discover log sources: {error:#}");
                if let Err(write_error) = self.write_terminal("failed", "failed", &message) {
                    eprintln!("Warning: failed to write failure checkpoint: {write_error:#}");
                }
                return Err(anyhow::anyhow!(message));
            }
        };
        self.layout = source_set.layout().to_string();

        let sources: Vec<LogSource> = source_set.sources.clone();

        if sources.is_empty() {
            eprintln!("No log files found in {}", source_set.dir.display());
            self.write_terminal("completed_with_warnings", "completed", "No log files found")?;
            return Ok(ProcessingOutcome::Completed);
        }

        eprintln!("Layout: {} — {} source(s):", self.layout, sources.len());
        let mut file_sizes: Vec<Vec<u64>> = Vec::with_capacity(sources.len());
        for source in &sources {
            eprintln!("  - {} ({} file(s))", source.stem, source.files.len());
            // Progress denominator: sum of on-disk file sizes (instant). This replaces
            // the old line-counting pre-pass that read and decompressed every file twice.
            let sizes: Vec<u64> = source
                .files
                .iter()
                .map(|log_file| {
                    std::fs::metadata(&log_file.path)
                        .map(|m| m.len())
                        .unwrap_or_else(|e| {
                            eprintln!(
                                "WARNING: Failed to read size of {}: {} (treating as 0 bytes)",
                                log_file.path.display(),
                                e
                            );
                            0
                        })
                })
                .collect();
            file_sizes.push(sizes);
        }
        self.total_bytes = file_sizes.iter().flatten().sum();
        eprintln!("Total size across all files: {} bytes", self.total_bytes);

        self.write_starting_progress_best_effort(&format!(
            "Processing {} source(s), {} bytes total",
            sources.len(),
            self.total_bytes
        ));

        // Check if this is a fresh database - skip dedup for maximum speed
        let starts_at_zero = sources
            .iter()
            .all(|s| self.positions.get(&s.stem).copied().unwrap_or(0) == 0);
        if starts_at_zero {
            let is_empty: bool =
                sqlx::query_scalar(r#"SELECT NOT EXISTS(SELECT 1 FROM "LogEntries" LIMIT 1)"#)
                    .fetch_one(&self.pool)
                    .await
                    .unwrap_or_else(|e| {
                        eprintln!(
                            "[log_processor] Warning: failed to check if database is empty: {}",
                            e
                        );
                        false
                    });
            if is_empty {
                eprintln!("Fresh database detected - skipping duplicate checks for maximum speed");
                self.skip_dedup = true;
            }
        }

        // LogEntries table already exists from C# migrations
        // Index IX_LogEntries_DuplicateCheck covers ClientIp, Service, Timestamp, Url,
        // BytesServed, Datasource, and md5(COALESCE(HttpRange, '')).

        // Public positions stay line counts. The sidecar proves a byte offset within one plain
        // member; any identity, length, prefix, or CRC doubt falls back to the corrected line skip.
        let mut resume_file = self
            .resume_path
            .as_deref()
            .map(log_resume::load)
            .unwrap_or_default();

        'sources: for (source_index, source) in sources.iter().enumerate() {
            let start_offset = self.positions.get(&source.stem).copied().unwrap_or(0);
            let mut stem_files = source.files.clone();
            let mut stem_sizes = file_sizes[source_index].clone();
            let mut paths: Vec<PathBuf> = stem_files.iter().map(|file| file.path.clone()).collect();
            let stem_entry = resume_file
                .stems
                .get(&source.stem)
                .filter(|entry| entry.position == start_offset)
                .cloned();
            let saved_point = stem_entry
                .as_ref()
                .and_then(|entry| log_resume::resume_point(entry, start_offset, &paths));
            let mut use_saved_point = saved_point.is_some();
            let mut stop_sources = false;

            'stem_attempt: loop {
                let point = use_saved_point.then(|| saved_point.as_ref()).flatten();
                let mut lines_to_skip = if point.is_some() {
                    0
                } else {
                    start_offset.saturating_sub(
                        stem_entry
                            .as_ref()
                            .map(|entry| log_resume::deleted_prefix_records(entry, &paths))
                            .unwrap_or(0),
                    )
                };
                let mut records_consumed = point.map(|value| value.records_before).unwrap_or(0);
                let first_file_index = point.map(|value| value.file_index).unwrap_or(0);
                let mut marks = point
                    .map(|value| value.older_files.clone())
                    .unwrap_or_default();
                let mut resumed_counts_pending = point.is_some();
                let mut frozen_source_position: Option<u64> = None;
                let mut last_reached: Option<(usize, FileResume, u64)> = None;
                let mut restart_with_line_skip = false;

                #[cfg(test)]
                if point.is_some() {
                    if let Some(before_open) = self.before_resume_open.as_mut() {
                        before_open();
                    }
                }

                for file_index in first_file_index..stem_files.len() {
                    let log_file = &stem_files[file_index];
                    eprintln!(
                        "\nProcessing {} file {}/{}: {}",
                        source.stem,
                        file_index + 1,
                        stem_files.len(),
                        log_file.path.display()
                    );

                    let file_size = stem_sizes[file_index];
                    let position_before_file = records_consumed;
                    let resumed_file_records = if point
                        .map(|value| value.file_index == file_index)
                        .unwrap_or(false)
                    {
                        stem_entry
                            .as_ref()
                            .map(|entry| entry.file_records)
                            .unwrap_or(0)
                    } else {
                        0
                    };
                    let mut file_resume =
                        if let Some(point) = point.filter(|value| value.file_index == file_index) {
                            FileResume {
                                offset: point.offset,
                                expected: stem_entry
                                    .as_ref()
                                    .map(|entry| (entry.file_identity.clone(), entry.tail_crc)),
                                ..FileResume::default()
                            }
                        } else {
                            FileResume::default()
                        };
                    if point.is_none()
                        && lines_to_skip > 0
                        && !matches!(
                            log_file.path.extension().and_then(|value| value.to_str()),
                            Some("gz" | "zst")
                        )
                    {
                        file_resume.mark = stem_entry.as_ref().and_then(|entry| {
                            let identity = log_purge::file_identity(&log_file.path).ok()?;
                            let len = std::fs::metadata(&log_file.path).ok()?.len();
                            entry
                                .older_files
                                .iter()
                                .find(|mark| mark.identity == identity && len > mark.len)
                                .cloned()
                        });
                    }
                    #[cfg(test)]
                    {
                        file_resume.fail_read = self.read_failure.as_ref() == Some(&log_file.path);
                    }
                    let file_result = self
                        .process_single_file(
                            log_file,
                            file_size,
                            &mut lines_to_skip,
                            &source.kind,
                            &mut records_consumed,
                            &mut file_resume,
                        )
                        .await;
                    lines_to_skip = lines_to_skip.saturating_add(file_resume.held_skip);
                    file_resume.held_skip = 0;

                    if matches!(&file_result, Ok(FileProcessingOutcome::ResumeTargetChanged)) {
                        restart_with_line_skip = true;
                        break;
                    }

                    if resumed_counts_pending {
                        if let Some(point) = point {
                            self.lines_parsed
                                .fetch_add(point.records_before, Ordering::Relaxed);
                            let skipped_bytes: u64 = stem_sizes[..point.file_index].iter().sum();
                            self.bytes_completed
                                .fetch_add(skipped_bytes, Ordering::Relaxed);
                        }
                        resumed_counts_pending = false;
                    }

                    if file_result.is_err() && frozen_source_position.is_none() {
                        frozen_source_position = Some(position_before_file);
                    }

                    let published = frozen_source_position
                        .map(|position| position.max(start_offset))
                        .unwrap_or(records_consumed);
                    self.source_positions.insert(source.stem.clone(), published);

                    if matches!(&file_result, Ok(FileProcessingOutcome::Cancelled))
                        || cancel::is_cancelled()
                    {
                        self.write_cancelled_terminal()?;
                        self.current_file_bytes.store(0, Ordering::Relaxed);
                        self.current_file_size.store(0, Ordering::Relaxed);
                        return Ok(ProcessingOutcome::Cancelled);
                    }

                    let file_records = resumed_file_records
                        + records_consumed.saturating_sub(position_before_file);
                    let mark_identity = file_resume
                        .opened
                        .clone()
                        .or_else(|| log_purge::file_identity(&log_file.path).ok());
                    if let Some(identity) = mark_identity {
                        let len = file_resume.observed_len.unwrap_or(file_size);
                        let proof = file_resume.opened.as_ref().and_then(|opened| {
                            file_resume.observed_len?;
                            if matches!(
                                log_file.path.extension().and_then(|value| value.to_str()),
                                Some("gz" | "zst")
                            ) || log_purge::file_identity(&log_file.path).ok().as_ref()
                                != Some(opened)
                                || std::fs::metadata(&log_file.path).ok()?.len()
                                    < file_resume.offset
                            {
                                return None;
                            }
                            Some((
                                file_resume.offset,
                                log_resume::tail_crc(&log_file.path, file_resume.offset).ok()?,
                            ))
                        });
                        marks.push(log_resume::FileMark {
                            identity,
                            len,
                            records: file_records,
                            offset: proof.map(|value| value.0),
                            tail_crc: proof.map(|value| value.1),
                        });
                    }
                    last_reached = Some((file_index, file_resume.clone(), file_records));

                    self.bytes_completed.fetch_add(file_size, Ordering::Relaxed);
                    self.current_file_bytes.store(0, Ordering::Relaxed);
                    self.current_file_size.store(0, Ordering::Relaxed);

                    match file_result {
                        Ok(FileProcessingOutcome::Completed) => {}
                        Ok(FileProcessingOutcome::SourceBlockedByIncompleteRecord) => break,
                        Ok(FileProcessingOutcome::RotatedMemberUnreadable(problem)) => {
                            let read = records_consumed.saturating_sub(position_before_file);
                            if lines_to_skip > 0 {
                                let marked = stem_entry.as_ref().and_then(|entry| {
                                    let identity = file_resume.opened.clone().or_else(|| {
                                        log_purge::file_identity(&log_file.path).ok()
                                    })?;
                                    let len = file_resume
                                        .opened_len
                                        .or_else(|| {
                                            std::fs::metadata(&log_file.path)
                                                .ok()
                                                .map(|attributes| attributes.len())
                                        })
                                        .unwrap_or(file_size);
                                    let prefix_matched = file_resume.mark.is_some()
                                        || log_resume::target_matches(
                                            entry,
                                            &log_file.path,
                                            &identity,
                                            len,
                                        );
                                    log_resume::mark_records(entry, &identity, len, prefix_matched)
                                });
                                if let Some(records) = marked {
                                    lines_to_skip =
                                        lines_to_skip.saturating_sub(records.saturating_sub(read));
                                } else if problem.kind != RotatedMemberProblemKind::UnterminatedTail
                                {
                                    lines_to_skip = 0;
                                }
                            }
                            self.rotated_member_errors += 1;
                            self.files_with_errors.push(format!(
                                "{}: {}",
                                log_file.path.display(),
                                problem.message
                            ));
                            eprintln!(
                                "Warning: rotated member {} ended early: {}",
                                log_file.path.display(),
                                problem.message
                            );
                            // Per-member marks preserve the skip that belongs to later files when a
                            // rotation grows or ends early. An unknown member keeps the established
                            // fallback correction so newer records remain reachable.
                        }
                        Ok(FileProcessingOutcome::ResumeTargetChanged) => {
                            unreachable!("resume replacement restarts before state is published")
                        }
                        Ok(FileProcessingOutcome::Cancelled) => {
                            unreachable!("cancellation is handled before completed-byte folding")
                        }
                        Err(error) => {
                            let message = format!("{error}");
                            let database_error = message.contains("error returned from database")
                                || message.contains("pool timed out")
                                || message.contains("connection refused")
                                || message.contains("operator does not exist");
                            self.files_with_errors.push(format!(
                                "{}: {}",
                                log_file.path.display(),
                                message
                            ));
                            if database_error {
                                eprintln!(
                                    "ERROR: Database error processing {}: {}",
                                    log_file.path.display(),
                                    error
                                );
                                stop_sources = true;
                                break;
                            }
                            eprintln!(
                                "Warning: file {} could not be processed: {}",
                                log_file.path.display(),
                                error
                            );
                        }
                    }
                }

                if restart_with_line_skip {
                    let refreshed = discover_log_sources(&self.log_dir)
                        .context("Failed to rediscover a rotated resume target")?;
                    if let Some(refreshed_source) = refreshed
                        .sources
                        .into_iter()
                        .find(|candidate| candidate.stem == source.stem)
                    {
                        let old_size: u64 = stem_sizes.iter().sum();
                        stem_files = refreshed_source.files;
                        stem_sizes = stem_files
                            .iter()
                            .map(|file| {
                                std::fs::metadata(&file.path)
                                    .map(|attributes| attributes.len())
                                    .unwrap_or(0)
                            })
                            .collect();
                        let new_size: u64 = stem_sizes.iter().sum();
                        self.total_bytes = self
                            .total_bytes
                            .saturating_sub(old_size)
                            .saturating_add(new_size);
                        paths = stem_files.iter().map(|file| file.path.clone()).collect();
                    }
                    use_saved_point = false;
                    continue 'stem_attempt;
                }

                let published = self
                    .source_positions
                    .get(&source.stem)
                    .copied()
                    .unwrap_or(start_offset);
                let fresh = if frozen_source_position.is_none() {
                    last_reached
                        .as_ref()
                        .and_then(|(file_index, file_resume, file_records)| {
                            let log_file = &stem_files[*file_index];
                            let identity = file_resume.opened.as_ref()?;
                            if matches!(
                                log_file.path.extension().and_then(|value| value.to_str()),
                                Some("gz" | "zst")
                            ) || log_purge::file_identity(&log_file.path).ok().as_ref()
                                != Some(identity)
                            {
                                return None;
                            }
                            let tail_crc =
                                log_resume::tail_crc(&log_file.path, file_resume.offset).ok()?;
                            Some(log_resume::StemResume {
                                position: published,
                                older_files: marks[..marks.len().saturating_sub(1)].to_vec(),
                                file_identity: identity.clone(),
                                offset: file_resume.offset,
                                file_records: *file_records,
                                tail_crc,
                            })
                        })
                } else {
                    None
                };
                if let Some(entry) = log_resume::entry_after_run(
                    resume_file.stems.get(&source.stem),
                    fresh,
                    frozen_source_position.is_some(),
                    published,
                    start_offset,
                ) {
                    resume_file.stems.insert(source.stem.clone(), entry);
                } else {
                    resume_file.stems.remove(&source.stem);
                }
                break 'stem_attempt;
            }

            if stop_sources {
                break 'sources;
            }
        }

        // Close the final-file race: a CANCEL arriving after the last file result but before
        // terminal resolution must still produce an authoritative cancelled checkpoint.
        if cancel::is_cancelled() {
            self.write_cancelled_terminal()?;
            return Ok(ProcessingOutcome::Cancelled);
        }

        let entries_saved = self.entries_saved.load(Ordering::Relaxed);

        // The full line count is only known now that every file has been read;
        // publish it so the final progress write (and the C# host, which persists
        // it via SetLogTotalLines) sees the real total.
        let final_line_count = self.lines_parsed.load(Ordering::Relaxed);
        self.total_lines.store(final_line_count, Ordering::Relaxed);

        // Rotated-member problems still publish a partial position and sidecar even when every
        // complete record was ignored. Other zero-entry file failures remain failed runs.
        if self.files_with_errors.len() > self.rotated_member_errors
            && entries_saved == 0
            && self.total_bytes > 0
        {
            let msg = format!(
                "Log processing failed - 0 entries processed from {} parsed lines. Errors: {}",
                final_line_count,
                self.files_with_errors.join("; ")
            );
            eprintln!("{}", msg);
            self.write_terminal("failed", "failed", &msg)?;
            return Err(anyhow::anyhow!(msg));
        }

        let (terminal, message) = Self::resolve_terminal_outcome(
            &self.files_with_errors,
            self.unparsed_lines,
            self.hintless_http_detailed_lines,
            self.invalid_encoding_lines,
        );
        match terminal {
            "partial" => eprintln!(
                "\nProcessing completed with {} file error(s), {} entries saved",
                self.files_with_errors.len(),
                entries_saved
            ),
            "completed_with_warnings" => eprintln!("\n{}", message),
            _ => eprintln!("\nAll files processed successfully!"),
        }
        if let Some(path) = self.resume_path.as_deref() {
            if let Err(error) = log_resume::save(path, &resume_file) {
                eprintln!(
                    "Warning: failed to save resume file {}: {error:#}",
                    path.display()
                );
            }
        }
        self.write_terminal(terminal, "completed", &message)?;

        Ok(ProcessingOutcome::Completed)
    }

    /// Typed terminal outcome for a run that reached the end of its file loop. Any file
    /// error caps the outcome at `partial` — never plain `completed` — and parse-level
    /// anomalies surface as `completed_with_warnings` so a zero-ingest run can never
    /// masquerade as healthy success.
    fn resolve_terminal_outcome(
        files_with_errors: &[String],
        unparsed: u64,
        hintless: u64,
        invalid_encoding: u64,
    ) -> (&'static str, String) {
        if !files_with_errors.is_empty() {
            return (
                "partial",
                format!(
                    "Log processing finished with {} file error(s)",
                    files_with_errors.len()
                ),
            );
        }
        if unparsed > 0 || hintless > 0 || invalid_encoding > 0 {
            return (
                "completed_with_warnings",
                format!(
                    "Log processing finished: {} unrecognized line(s), {} http-detailed line(s) without a service hint, {} line(s) with invalid encoding",
                    unparsed, hintless, invalid_encoding
                ),
            );
        }
        ("completed", "Log processing finished".to_string())
    }

    /// Process a single log file belonging to one source's rotation series.
    async fn process_single_file(
        &mut self,
        log_file: &LogFile,
        file_size: u64,
        lines_to_skip: &mut u64,
        kind: &SourceKind,
        records_consumed: &mut u64,
        resume: &mut FileResume,
    ) -> Result<FileProcessingOutcome> {
        self.process_single_file_with_cancel(
            log_file,
            file_size,
            lines_to_skip,
            kind,
            records_consumed,
            resume,
            cancel::is_cancelled,
        )
        .await
    }

    async fn process_single_file_with_cancel<F>(
        &mut self,
        log_file: &LogFile,
        file_size: u64,
        lines_to_skip: &mut u64,
        kind: &SourceKind,
        records_consumed: &mut u64,
        resume: &mut FileResume,
        is_cancelled: F,
    ) -> Result<FileProcessingOutcome>
    where
        F: Fn() -> bool,
    {
        // Track raw (compressed) bytes consumed from this file for byte-based progress.
        let byte_counter = Arc::new(AtomicU64::new(0));
        self.current_file_bytes = byte_counter.clone();
        self.current_file_size.store(file_size, Ordering::Relaxed);
        let plain = !matches!(
            log_file.path.extension().and_then(|value| value.to_str()),
            Some("gz" | "zst")
        );
        let mark = resume.mark.take();

        #[cfg(test)]
        if let Some(before_open) = self.before_content_open.as_mut() {
            before_open(&log_file.path);
        }

        let open_error: Option<anyhow::Error> = {
            #[cfg(test)]
            {
                *self.open_attempts.entry(log_file.path.clone()).or_insert(0) += 1;
                (self.open_failure.as_ref() == Some(&log_file.path))
                    .then(|| anyhow::anyhow!("injected open failure"))
            }
            #[cfg(not(test))]
            {
                None
            }
        };

        let opened = if let Some(error) = open_error {
            Err(error)
        } else if let Some((expected, tail_crc)) = resume.expected.as_ref() {
            match LogFileReader::open_at_offset(
                &log_file.path,
                resume.offset,
                expected,
                *tail_crc,
                byte_counter.clone(),
            ) {
                Ok(Some(reader)) => {
                    resume.opened = Some(expected.clone());
                    Ok(reader)
                }
                Ok(None) => return Ok(FileProcessingOutcome::ResumeTargetChanged),
                Err(error) => Err(error),
            }
        } else {
            LogFileReader::open_with_byte_counter_identity(
                &log_file.path,
                byte_counter.clone(),
                mark.as_ref(),
            )
            .map(|(reader, identity, len, prefix_matched)| {
                resume.opened_len = Some(len);
                if prefix_matched {
                    if let Some(mark) = mark {
                        let local_skip = (*lines_to_skip).min(mark.records);
                        resume.held_skip = lines_to_skip.saturating_sub(local_skip);
                        *lines_to_skip = local_skip;
                        resume.mark = Some(mark);
                    }
                }
                resume.opened = Some(identity);
                reader
            })
        };
        let mut reader = match opened {
            Ok(reader) => reader,
            Err(error) if log_file.rotation_number.is_some() => {
                return Ok(FileProcessingOutcome::RotatedMemberUnreadable(
                    RotatedMemberProblem {
                        kind: RotatedMemberProblemKind::Open,
                        message: format!("{error:#}"),
                    },
                ));
            }
            Err(error) => return Err(error),
        };

        // Records are read as raw bytes: one invalid byte must never abort a file, and
        // UTF-8 lossiness is confined to the classifier's text handling.
        let mut record_buf: Vec<u8> = Vec::with_capacity(LINE_BUFFER_CAPACITY);

        // Skip records if we haven't reached the start position for this stem yet
        if *lines_to_skip > 0 {
            eprintln!(
                "Skipping {} lines in this file to reach start position",
                lines_to_skip
            );
            while *lines_to_skip > 0 {
                if is_cancelled() {
                    return Ok(FileProcessingOutcome::Cancelled);
                }
                record_buf.clear();
                let bytes_read = match resume.read_until_newline(&mut reader, &mut record_buf) {
                    Ok(bytes_read) => bytes_read,
                    Err(error) if log_file.rotation_number.is_some() => {
                        return Ok(FileProcessingOutcome::RotatedMemberUnreadable(
                            RotatedMemberProblem {
                                kind: RotatedMemberProblemKind::Read,
                                message: format!("{error:#}"),
                            },
                        ));
                    }
                    Err(error) => return Err(error),
                };
                if bytes_read == 0 {
                    // Reached EOF before skipping all lines - this file is exhausted
                    if plain {
                        resume.observed_len = Some(byte_counter.load(Ordering::Relaxed));
                    }
                    return Ok(FileProcessingOutcome::Completed);
                }
                if !record_buf.ends_with(b"\n") {
                    // Unterminated final record: the writer is mid-line. It was never
                    // counted toward the position, so it is not skippable either — and
                    // the source stops here so the position stays a clean prefix.
                    self.incomplete_final_records += 1;
                    if plain {
                        resume.observed_len = Some(byte_counter.load(Ordering::Relaxed));
                    }
                    if log_file.rotation_number.is_some() {
                        return Ok(FileProcessingOutcome::RotatedMemberUnreadable(
                            RotatedMemberProblem {
                                kind: RotatedMemberProblemKind::UnterminatedTail,
                                message: "ends with an unterminated record".to_string(),
                            },
                        ));
                    }
                    return Ok(FileProcessingOutcome::SourceBlockedByIncompleteRecord);
                }
                *lines_to_skip -= 1;
                *records_consumed += 1;
                resume.offset += bytes_read as u64;
                self.lines_parsed.fetch_add(1, Ordering::Relaxed);
            }
        }

        let mut batch = Vec::with_capacity(BULK_BATCH_SIZE);

        self.write_progress(
            "processing",
            &format!("Reading {}...", log_file.path.display()),
        )?;

        loop {
            // Poll independently of parse outcomes. Bare-metal fallback files and stretches
            // of ignored/unrecognized records may never fill a DB batch, but must remain
            // cooperatively cancellable. Flush parsed work before publishing cancellation.
            if is_cancelled() {
                if !batch.is_empty() {
                    self.process_batch(&batch).await?;
                    batch.clear();
                }
                return Ok(FileProcessingOutcome::Cancelled);
            }

            record_buf.clear();
            let bytes_read = match resume.read_until_newline(&mut reader, &mut record_buf) {
                Ok(bytes_read) => bytes_read,
                Err(error) if log_file.rotation_number.is_some() => {
                    if !batch.is_empty() {
                        self.process_batch(&batch).await?;
                        batch.clear();
                    }
                    return Ok(FileProcessingOutcome::RotatedMemberUnreadable(
                        RotatedMemberProblem {
                            kind: RotatedMemberProblemKind::Read,
                            message: format!("{error:#}"),
                        },
                    ));
                }
                Err(error) => return Err(error),
            };

            if bytes_read == 0 {
                // EOF - process remaining batch
                if !batch.is_empty() {
                    self.process_batch(&batch).await?;
                    batch.clear();
                    batch.shrink_to_fit(); // Release memory since we're done
                }
                if plain {
                    resume.observed_len = Some(byte_counter.load(Ordering::Relaxed));
                }
                break;
            }

            let complete = record_buf.ends_with(b"\n");
            let outcome = self.classify_record(&record_buf, complete, kind);

            if matches!(outcome, ParseOutcome::Incomplete) {
                // Never counted toward the position; a later run ingests the completed
                // line exactly once. Flush what we have and stop the SOURCE here so the
                // stem's position describes one clean prefix of its series.
                self.incomplete_final_records += 1;
                if !batch.is_empty() {
                    self.process_batch(&batch).await?;
                    batch.clear();
                    batch.shrink_to_fit();
                }
                if plain {
                    resume.observed_len = Some(byte_counter.load(Ordering::Relaxed));
                }
                if log_file.rotation_number.is_some() {
                    return Ok(FileProcessingOutcome::RotatedMemberUnreadable(
                        RotatedMemberProblem {
                            kind: RotatedMemberProblemKind::UnterminatedTail,
                            message: "ends with an unterminated record".to_string(),
                        },
                    ));
                }
                return Ok(FileProcessingOutcome::SourceBlockedByIncompleteRecord);
            }

            *records_consumed += 1;
            resume.offset += bytes_read as u64;
            self.lines_parsed.fetch_add(1, Ordering::Relaxed);

            match outcome {
                ParseOutcome::Parsed(entry) => {
                    #[cfg(test)]
                    {
                        self.parsed_records += 1;
                    }
                    batch.push(entry);

                    // Process batch when it reaches BULK_BATCH_SIZE
                    if batch.len() >= BULK_BATCH_SIZE {
                        self.process_batch(&batch).await?;
                        batch.clear();
                        // Don't shrink here - we'll reuse the capacity for the next batch

                        let parsed = self.lines_parsed.load(Ordering::Relaxed);
                        let saved = self.entries_saved.load(Ordering::Relaxed);
                        let percent = self.percent_complete();
                        let current_percent_bucket = (percent / 5.0).floor() as u64 * 5; // Round down to nearest 5%
                        let last_logged = self.last_logged_percent.load(Ordering::Relaxed);

                        // Only log when we cross a 5% boundary
                        if current_percent_bucket > last_logged {
                            self.last_logged_percent
                                .store(current_percent_bucket, Ordering::Relaxed);
                            eprintln!(
                                "Progress: {} lines ({:.1}%), {} entries saved",
                                parsed, percent, saved
                            );
                        }

                        self.write_progress(
                            "processing",
                            &format!("{} lines parsed, {} entries saved", parsed, saved),
                        )?;

                        // Cooperative cancel: check after each flushed batch (clean DB-transaction boundary)
                        if is_cancelled() {
                            eprintln!("Cancel requested — stopping after batch flush ({} lines, {} entries saved)", parsed, saved);
                            return Ok(FileProcessingOutcome::Cancelled);
                        }
                    }
                }
                ParseOutcome::RecognizedIgnored(IgnoredReason::Fallback) => {
                    self.skipped_fallback_lines += 1;
                }
                ParseOutcome::RecognizedIgnored(IgnoredReason::Hintless) => {
                    self.hintless_http_detailed_lines += 1;
                }
                ParseOutcome::RecognizedIgnored(_) => {
                    self.recognized_ignored_lines += 1;
                }
                ParseOutcome::InvalidEncoding => {
                    self.invalid_encoding_lines += 1;
                }
                ParseOutcome::Unrecognized => {
                    self.unparsed_lines += 1;
                }
                ParseOutcome::Incomplete => unreachable!("handled above"),
            }
        }

        Ok(FileProcessingOutcome::Completed)
    }

    /// Extract a path prefix from an Epic CDN URL to use as a session discriminator.
    /// Epic CDN URLs follow the pattern: /Builds/Org/o-<orgHash>/<buildHash>/default/<chunkFile>
    /// We extract the first 5 segments (/Builds/Org/o-xxx/hash/default) which uniquely identify a game.
    /// Returns None if the URL doesn't have enough segments, falling back to `_nodepot` behavior.
    fn extract_epic_path_prefix(url: &str) -> Option<String> {
        // Split the URL path into segments, skipping empty segments from leading slash
        let segments: Vec<&str> = url.split('/').filter(|s| !s.is_empty()).collect();
        // Need at least 5 segments for a meaningful Epic CDN path prefix
        if segments.len() >= 5 {
            // Rejoin the first 5 segments as the prefix key
            Some(format!("/{}", segments[..5].join("/")))
        } else {
            None
        }
    }

    async fn process_batch(&mut self, entries: &[LogEntry]) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }

        // Begin a transaction
        let mut tx = self.pool.begin().await?;
        // The upgrade merge takes the stronger table lock. Taking the lock used by this batch
        // before its lookups makes an already-started pass wait and then see the merged rows.
        sqlx::query("LOCK TABLE \"Downloads\" IN ROW EXCLUSIVE MODE")
            .execute(&mut *tx)
            .await?;

        // Pre-resolve the Xbox title for each entry (aligned by index) so the grouping loop below
        // can key matched Xbox traffic per-title without holding a mutable borrow of `self`.
        // wsus/xboxlive entries that match no Xbox fragment resolve to None and stay generic.
        let mut entry_xbox_titles: Vec<Option<String>> = Vec::with_capacity(entries.len());
        for entry in entries {
            if Self::is_xbox_cache_service(&entry.service) {
                entry_xbox_titles.push(
                    self.lookup_xbox_game(&entry.url)
                        .await
                        .map(|(title, _pid)| title),
                );
            } else {
                entry_xbox_titles.push(None);
            }
        }

        // Group entries by client_ip + service + depot_id to prevent different games from being merged
        // For Epic services without a depot_id, use the URL path prefix as a discriminator
        // so different Epic games get separate sessions instead of being merged into one
        let mut grouped: HashMap<String, Vec<&LogEntry>> = HashMap::new();
        for (entry, xbox_title) in entries.iter().zip(entry_xbox_titles.iter()) {
            let depot_suffix = if let Some(id) = entry.depot_id {
                format!("_{}", id)
            } else if let Some(title) = xbox_title {
                // Xbox content is tagged `wsus` (DO-client) or `xboxlive` (prefill, assets1) over
                // opaque CDN URLs; key the session on the resolved title so distinct Xbox games (and
                // distinct from generic Windows Update / Xbox Live, which keep `_nodepot`) get
                // distinct sessions.
                format!("_xboxgame:{}", title)
            } else if entry.service.to_lowercase().contains("epic") {
                // For Epic entries, use the CDN path prefix (e.g., /Builds/Org/o-xxx/hash/default)
                // as the session discriminator to keep different games in separate sessions
                Self::extract_epic_path_prefix(&entry.url)
                    .map(|prefix| format!("_epic:{}", prefix))
                    .unwrap_or_else(|| "_nodepot".to_string())
            } else if let Some(product) = entry.tact_product.as_deref() {
                // For Blizzard entries, key the session on the RESOLVED game so a
                // title's multiple CDN paths (e.g. configs + data) canonicalize into
                // ONE session instead of splitting into several `_tact:<raw-seg>`
                // sessions. Shared product-agnostic paths collapse together under the
                // shared label; genuinely unknown segments keep the raw segment so
                // distinct unknown games still get distinct sessions.
                match tact_products::resolve_tact_segment(product) {
                    tact_products::TactResolution::Game(name) => format!("_tactgame:{}", name),
                    tact_products::TactResolution::Shared(label) => format!("_tactgame:{}", label),
                    tact_products::TactResolution::Unknown => format!("_tact:{}", product),
                }
            } else if let Some(host) = entry.cdn_host.as_deref() {
                // For Riot entries, key the session on the CDN host because every
                // game's bundle URL shares the identical path
                // (/channels/public/bundles/<hash>.bundle) — only the host subdomain
                // (lol/valorant/bacon) distinguishes the games. Keying on the resolved
                // game name keeps a title's traffic in ONE session; unknown hosts keep
                // the raw host so distinct unknown Riot products still get distinct
                // sessions instead of collapsing together.
                match riot_hosts::resolve_riot_host(host) {
                    Some(name) => format!("_riotgame:{}", name),
                    None => format!("_riot:{}", host),
                }
            } else {
                "_nodepot".to_string()
            };
            let key = format!("{}_{}{}", entry.client_ip, entry.service, depot_suffix);
            grouped.entry(key).or_default().push(entry);
        }

        // A map's iteration order changes between processes. Xbox groups run first so generic
        // Windows Update lines cannot join a game row in the batch where its pattern first applies.
        // Collect entries to insert into a shared buffer for ONE bulk INSERT
        let mut pending_inserts: Vec<PendingLogEntry> = Vec::with_capacity(entries.len());
        let mut keys: Vec<&String> = grouped.keys().collect();
        keys.sort_by(|a, b| {
            let a_entry = grouped[*a][0];
            let b_entry = grouped[*b][0];
            let a_xbox =
                a[a_entry.client_ip.len() + 1 + a_entry.service.len()..].starts_with("_xboxgame:");
            let b_xbox =
                b[b_entry.client_ip.len() + 1 + b_entry.service.len()..].starts_with("_xboxgame:");
            (!a_xbox, a.as_str()).cmp(&(!b_xbox, b.as_str()))
        });
        for key in keys {
            let mut ordered = grouped[key].clone();
            ordered.sort_by_key(|entry| entry.timestamp);
            let mut part_start = 0;
            let mut part_max = ordered[0].timestamp;
            for index in 1..ordered.len() {
                if ordered[index].timestamp
                    > part_max + chrono::Duration::minutes(SESSION_GAP_MINUTES)
                {
                    // One run groups like many live passes: a long in-batch silence starts the
                    // same new row that the stored-row window starts between separate passes.
                    self.process_session_group(
                        &mut tx,
                        &ordered[part_start..index],
                        &mut pending_inserts,
                    )
                    .await?;
                    part_start = index;
                    part_max = ordered[index].timestamp;
                } else {
                    part_max = part_max.max(ordered[index].timestamp);
                }
            }
            self.process_session_group(&mut tx, &ordered[part_start..], &mut pending_inserts)
                .await?;
        }

        // ONE bulk INSERT for ALL entries across ALL session groups
        if !pending_inserts.is_empty() {
            Self::bulk_insert_log_entries(&mut tx, &pending_inserts).await?;
        }

        tx.commit().await?;

        self.entries_saved
            .fetch_add(pending_inserts.len() as u64, Ordering::Relaxed);

        Ok(())
    }

    /// Depot -> (AppId, AppName) via the lazy per-depot memo. At most ONE indexed SELECT per
    /// new depot per run (a batch resolves a single primary depot); already-seen depots and
    /// confirmed-unmapped depots never touch the DB again this run. A transient query error
    /// is NOT negative-memoized, so the depot retries on the next batch.
    async fn lookup_depot_mapping(&mut self, depot_id: u32) -> Option<(u32, Option<String>)> {
        if let Some(mapped) = self.depot_map.get(&depot_id) {
            return Some(mapped.clone());
        }
        if self.depots_unmapped.contains(&depot_id) {
            return None;
        }

        // ORDER BY makes the pick deterministic for shared depots with multiple owner rows,
        // and lowest-AppId matches the representative SteamService.GetAppIdFromDepot uses.
        let row = match sqlx::query(
            r#"SELECT "AppId", "AppName" FROM "SteamDepotMappings" WHERE "DepotId" = $1 AND "IsOwner" = true ORDER BY "AppId" LIMIT 1"#,
        )
        .bind(depot_id as i64)
        .fetch_optional(&self.pool)
        .await
        {
            Ok(row) => row,
            Err(e) => {
                eprintln!("Warning: Failed to look up depot mapping for {}: {}", depot_id, e);
                return None;
            }
        };

        match row {
            Some(row) => {
                let app_id: i64 = row.get("AppId");
                let app_name: Option<String> = row.get("AppName");
                let mapped = (app_id as u32, app_name);
                self.depot_map.insert(depot_id, mapped.clone());
                Some(mapped)
            }
            None => {
                self.depots_unmapped.insert(depot_id);
                None
            }
        }
    }

    /// True for lancache service tags that carry Xbox / Microsoft Store delivery traffic. Two shapes
    /// reach the cache: Delivery-Optimization CLIENT traffic tagged `wsus` (shared with generic
    /// Windows Update, over `/filestreamingservice/files/<GUID>`), and prefill-daemon traffic pulled
    /// direct from assets1.xboxlive.com tagged `xboxlive` (over `/<d>/<guid>/<guid>/<ver>.<guid>/<pkg>`).
    /// Both must be considered, but we only canonicalize the rows whose URL also matches a stored
    /// Xbox fragment, so generic OS updates and generic Xbox Live traffic are untouched. Mirrors the
    /// C# `ResolveDownloadsAsync` candidate filter (`%wsus%` OR `%xboxlive%`).
    fn is_xbox_cache_service(service: &str) -> bool {
        let s = service.to_lowercase();
        s.contains("wsus") || s.contains("xboxlive")
    }

    /// Reload the Xbox CDN fragment -> title patterns from the DB, throttled to
    /// `XBOX_PATTERN_RELOAD`. The tables fill over time as daemons contribute fragments, so we
    /// reload periodically during a long run. Errors (e.g. tables not created yet) are ignored.
    async fn load_xbox_patterns(&mut self) {
        if let Some(last) = self.last_xbox_pattern_load {
            if last.elapsed() < XBOX_PATTERN_RELOAD {
                return;
            }
        }

        let result = sqlx::query(
            "SELECT p.\"UrlFragment\", COALESCE(m.\"Title\", p.\"Title\") AS \"Title\", p.\"ProductId\" \
             FROM \"XboxCdnPatterns\" p \
             LEFT JOIN \"XboxGameMappings\" m ON p.\"ProductId\" = m.\"ProductId\" \
             ORDER BY LENGTH(p.\"UrlFragment\") DESC"
        )
        .fetch_all(&self.pool)
        .await;

        match result {
            Ok(rows) => {
                self.xbox_patterns = rows
                    .iter()
                    .filter_map(|row| {
                        Self::xbox_pattern_row(
                            row.get("UrlFragment"),
                            row.get("Title"),
                            row.get("ProductId"),
                        )
                    })
                    .collect();
                self.last_xbox_pattern_load = Some(Instant::now());
                // Preserve negative decisions across a reload so one run classifies each URL
                // consistently. A later process can adopt the stored unnamed row through its owned
                // LogEntries when a newly available pattern identifies the download.
                self.xbox_url_positive.clear();
            }
            Err(_) => {
                // Silently ignore (tables may not exist yet); wsus stays generic.
            }
        }
    }

    /// Keep ONLY well-formed `/filestreamingservice/files/<GUID>` fragments that carry a title.
    /// Empty / "/" / non-GUID fragments would `contains()`-match generic wsus URLs and relabel
    /// Windows Update traffic as a game — the same shape guard the C# resolver
    /// (XboxMappingService.IsValidFragment) applies. This Rust path is the PRIMARY canonicalizer,
    /// so the guard MUST live here too.
    /// A blank title is dropped exactly like an absent one. This loader is the only source of Xbox
    /// names, and a match canonicalizes the download to `Service='xbox'` with `GameName` = the
    /// title. An empty `GameName` splits the codebase — the identity key rule buckets it as
    /// `steam:0` while the detection queries test the column against NULL and call it a named game
    /// — and the C# re-resolver only ever revisits `wsus`/`xboxlive` rows, so an `xbox` row stamped
    /// with `""` can never be given its real title. Dropping the pattern keeps the URL generic
    /// `wsus`, which stays re-resolvable once a real title arrives.
    fn xbox_pattern_row(
        fragment: Option<String>,
        title: Option<String>,
        product_id: Option<String>,
    ) -> Option<(String, String, String)> {
        match (fragment, title, product_id) {
            (Some(frag), Some(name), Some(pid))
                if cache_utils::is_valid_xbox_fragment(&frag) && !name.trim().is_empty() =>
            {
                Some((frag, name, pid))
            }
            _ => None,
        }
    }

    /// Resolve a `wsus`/`xboxlive` URL to its Xbox `(title, product_id)` via a stored fragment match
    /// (longest-first), or None if it is generic Windows Update / Xbox Live. Per-URL cached (None
    /// caches too, so a confirmed non-match is never re-walked). Loads the patterns on first use.
    async fn lookup_xbox_game(&mut self, url: &str) -> Option<(String, String)> {
        let key = cache_utils::calculate_md5_digest(url);
        if self.xbox_url_negative.contains(&key) {
            return None;
        }
        if let Some(cached) = self.xbox_url_positive.get(&key) {
            return Some(cached.clone());
        }

        self.load_xbox_patterns().await;

        let result = Self::match_xbox_fragment(&self.xbox_patterns, url)
            .map(|(_, name, pid)| (name.clone(), pid.clone()));

        match &result {
            Some(game) => {
                self.xbox_url_positive.insert(key, game.clone());
            }
            None => {
                self.xbox_url_negative.insert(key);
            }
        }
        result
    }

    /// ASCII-case-insensitively find the (longest-first) Xbox pattern whose fragment is contained in
    /// `url`. Xbox CDN fragments are `/filestreamingservice/files/<GUID>` paths; the GUID hex casing
    /// the daemon stores (from the manifest URI) can differ from the casing in the nginx access-log
    /// URL, so a case-sensitive `contains` would miss a real match and leave the row generic `wsus`.
    /// The C# resolver (XboxMappingService.cs) already compares with `StringComparison.OrdinalIgnoreCase`;
    /// this keeps the primary Rust canonicalizer consistent. ASCII lowercasing is exact for these
    /// paths, and `lookup_xbox_game`'s per-URL cache means each unique URL is lowercased at most once.
    fn match_xbox_fragment<'a>(
        patterns: &'a [(String, String, String)],
        url: &str,
    ) -> Option<&'a (String, String, String)> {
        let url_lower = url.to_ascii_lowercase();
        patterns
            .iter()
            .find(|(fragment, _, _)| url_lower.contains(&fragment.to_ascii_lowercase()))
    }

    /// For a batch of `wsus`/`xboxlive` entries, pick the dominant resolved Xbox game (most frequent
    /// match). Returns `("xbox", Some(title), Some(product_id))` when the batch is a recognized Xbox
    /// download, else `(service, None, None)` so unmatched traffic stays generic Windows Update /
    /// Xbox Live. Splitting the IDENTITY service (`xbox`, on Downloads) from the cache-hash service
    /// (the original `wsus`/`xboxlive` tag, on LogEntries) is load-bearing — see the identity model.
    async fn resolve_xbox_canonicalization(
        &mut self,
        service: &str,
        new_entries: &[&LogEntry],
    ) -> (String, Option<String>, Option<String>) {
        if !Self::is_xbox_cache_service(service) {
            return (service.to_string(), None, None);
        }

        // Count matches by (title, product_id); pick the most frequent (ties broken by title) so a
        // batch interleaving a game's files with a stray Office/Defender chunk names the game.
        let mut game_counts: HashMap<(String, String), usize> = HashMap::new();
        for entry in new_entries {
            if let Some(game) = self.lookup_xbox_game(&entry.url).await {
                *game_counts.entry(game).or_insert(0) += 1;
            }
        }

        match game_counts
            .into_iter()
            .max_by(|a, b| a.1.cmp(&b.1).then_with(|| a.0.cmp(&b.0)))
            .map(|((title, product_id), _)| (title, product_id))
        {
            Some((title, product_id)) => ("xbox".to_string(), Some(title), Some(product_id)),
            None => (service.to_string(), None, None),
        }
    }

    async fn process_session_group(
        &mut self,
        tx: &mut sqlx::Transaction<'_, sqlx::Postgres>,
        entries: &[&LogEntry],
        pending_inserts: &mut Vec<PendingLogEntry>,
    ) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }

        // Duplicate detection - skip on fresh database for maximum speed
        let (new_entries, skipped): (Vec<&LogEntry>, usize) = if self.skip_dedup {
            // Fresh database - all entries are new, no dedup needed
            (entries.to_vec(), 0)
        } else {
            // Bulk duplicate detection - single query for the whole group
            let mut check_client_ips: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_services: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_timestamps: Vec<chrono::DateTime<Utc>> =
                Vec::with_capacity(entries.len());
            let mut check_urls: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_bytes: Vec<i64> = Vec::with_capacity(entries.len());
            let mut check_http_ranges: Vec<String> = Vec::with_capacity(entries.len());

            for entry in entries {
                check_client_ips.push(&entry.client_ip);
                check_services.push(&entry.service);
                check_timestamps.push(Utc.from_utc_datetime(&entry.timestamp));
                check_urls.push(&entry.url);
                check_bytes.push(entry.bytes_served);
                check_http_ranges.push(clamp_chars(
                    &entry.http_range,
                    LOG_ENTRY_HTTP_RANGE_MAX_CHARS,
                ));
            }

            let existing_rows = sqlx::query(
                r#"SELECT "ClientIp", "Service", "Timestamp", "Url", "BytesServed", "HttpRange"
                   FROM "LogEntries"
                   WHERE "Datasource" = $6
                   AND ("ClientIp", "Service", "Timestamp", "Url", "BytesServed", md5(COALESCE("HttpRange", '')))
                   IN (SELECT c.ip, c.svc, c.ts, c.url, c.bytes, md5(c.rng) FROM UNNEST($1::text[], $2::text[], $3::timestamptz[], $4::text[], $5::bigint[], $7::text[]) AS c(ip, svc, ts, url, bytes, rng)
                       UNION ALL
                       SELECT c.ip, c.svc, c.ts, c.url, c.bytes, md5('') FROM UNNEST($1::text[], $2::text[], $3::timestamptz[], $4::text[], $5::bigint[]) AS c(ip, svc, ts, url, bytes))"#
            )
            .bind(&check_client_ips)
            .bind(&check_services)
            .bind(&check_timestamps)
            .bind(&check_urls)
            .bind(&check_bytes)
            .bind(&self.datasource_name)
            .bind(&check_http_ranges)
            .fetch_all(&mut **tx)
            .await?;

            let mut existing_keys: HashSet<(String, String, i64, String, i64, String)> =
                HashSet::new();
            let mut legacy: HashSet<(String, String, i64, String, i64)> = HashSet::new();
            for row in &existing_rows {
                let client_ip: String = row.get("ClientIp");
                let service: String = row.get("Service");
                let ts: chrono::DateTime<Utc> = row.get("Timestamp");
                let url: String = row.get("Url");
                let bytes: i64 = row.get("BytesServed");
                let http_range: Option<String> = row.get("HttpRange");
                let key = (
                    client_ip,
                    service,
                    ts.timestamp_nanos_opt().unwrap_or(0),
                    url,
                    bytes,
                );
                if let Some(range) = http_range {
                    existing_keys.insert((key.0, key.1, key.2, key.3, key.4, range));
                } else {
                    // A null range predates the range column and stands for every range of the
                    // same request. The digest keeps the index key bounded for long headers.
                    legacy.insert(key);
                }
            }

            let mut new_vec: Vec<&LogEntry> = Vec::with_capacity(entries.len());
            let mut skip_count = 0usize;

            for entry in entries {
                let ts_nanos = Utc
                    .from_utc_datetime(&entry.timestamp)
                    .timestamp_nanos_opt()
                    .unwrap_or(0);
                let key = (
                    entry.client_ip.clone(),
                    entry.service.clone(),
                    ts_nanos,
                    entry.url.clone(),
                    entry.bytes_served,
                );
                let exact_key = (
                    key.0.clone(),
                    key.1.clone(),
                    key.2,
                    key.3.clone(),
                    key.4,
                    clamp_chars(&entry.http_range, LOG_ENTRY_HTTP_RANGE_MAX_CHARS),
                );
                if legacy.contains(&key) || existing_keys.contains(&exact_key) {
                    skip_count += 1;
                } else {
                    new_vec.push(*entry);
                }
            }
            (new_vec, skip_count)
        };

        // If all entries were duplicates, skip all processing
        if new_entries.is_empty() {
            return Ok(());
        }

        // Now process only the new (non-duplicate) entries
        let first_entry = new_entries[0];
        let client_ip = &first_entry.client_ip;
        let service = &first_entry.service;

        // Calculate timestamps and aggregations ONLY for new entries
        let first_timestamp = new_entries.iter().map(|e| e.timestamp).min().unwrap();
        let last_timestamp = new_entries.iter().map(|e| e.timestamp).max().unwrap();

        let (total_hit_bytes, total_miss_bytes) = split_hit_miss_bytes(
            new_entries
                .iter()
                .map(|e| (e.cache_status.as_str(), e.bytes_served)),
        );

        // Extract primary depot ID (most common) - use new_entries, not all entries
        let primary_depot_id = new_entries
            .iter()
            .filter_map(|e| e.depot_id)
            .fold(HashMap::new(), |mut map, depot| {
                *map.entry(depot).or_insert(0) += 1;
                map
            })
            .into_iter()
            .max_by_key(|(_, count)| *count)
            .map(|(depot, _)| depot);

        // Extract primary Blizzard TACT product (most common) for game naming/grouping
        let primary_tact_product: Option<String> = new_entries
            .iter()
            .filter_map(|e| e.tact_product.clone())
            .fold(HashMap::new(), |mut map, product| {
                *map.entry(product).or_insert(0) += 1;
                map
            })
            .into_iter()
            .max_by_key(|(_, count)| *count)
            .map(|(product, _)| product);

        // Extract primary Riot CDN host (most common) for game naming/grouping.
        // Riot bundle URLs carry no product slug; the host subdomain is the discriminator.
        let primary_cdn_host: Option<String> = new_entries
            .iter()
            .filter_map(|e| e.cdn_host.clone())
            .fold(HashMap::new(), |mut map, host| {
                *map.entry(host).or_insert(0) += 1;
                map
            })
            .into_iter()
            .max_by_key(|(_, count)| *count)
            .map(|(host, _)| host);

        let last_url = new_entries.last().map(|e| e.url.as_str());

        // Xbox canonicalization (INGEST-PRIMARY, active-session-safe). When this batch of `wsus`
        // traffic matches a stored Xbox fragment, the Downloads-side IDENTITY service becomes `xbox`
        // and GameName becomes the resolved title — while LogEntries.Service stays
        // `wsus` (the cache-hash service). Every Downloads lookup and write below keys on
        // `download_service`. Unmatched `wsus` stays generic Windows Update. When a later process
        // learns the pattern, the adoption lookup rejoins the row from its owned LogEntries.
        let (download_service_owned, xbox_game_name, xbox_product_id) = self
            .resolve_xbox_canonicalization(service, &new_entries)
            .await;
        let download_service: &str = &download_service_owned;

        // Lookup depot mappings during log processing (auto_map_depots = true)
        // This ensures Downloads have GameAppId/GameName set immediately, avoiding "Unknown Game" in UI
        let (game_app_id, game_name) = if let Some(ref title) = xbox_game_name {
            // Matched Xbox content: GameAppId stays None (named-style identity, like Blizzard/Riot),
            // GameName = the resolved title. The Download was already canonicalized to Service='xbox'
            // via `download_service`. Unmatched wsus never reaches here (xbox_game_name is None).
            (None, Some(title.clone()))
        } else if self.auto_map_depots && service.to_lowercase() == "steam" {
            if let Some(depot_id) = primary_depot_id {
                match self.lookup_depot_mapping(depot_id).await {
                    Some((app_id, app_name)) => {
                        // Only log each depot mapping once to avoid log spam
                        if !self.logged_depots.contains(&depot_id) {
                            let game_display = app_name.as_deref().unwrap_or("Unknown");
                            eprintln!(
                                "Mapped depot {} -> App {} ({})",
                                depot_id, app_id, game_display
                            );
                            self.logged_depots.insert(depot_id);
                        }
                        (Some(app_id), app_name)
                    }
                    None => (None, None),
                }
            } else {
                (None, None)
            }
        } else if self.auto_map_depots && service.to_lowercase() == "blizzard" {
            // Blizzard has no integer app id; resolve the TACT CDN-path / product
            // segment -> game name (products/aliases) or the shared label (shared
            // product-agnostic paths like configs/agent/catalogs). GameAppId stays
            // None (only GameName is set). Genuinely unknown segments leave GameName
            // NULL (mirroring an unmapped depot) and are LOGGED once per run so a
            // 1-line aliases/shared entry can close the gap later.
            if let Some(product) = primary_tact_product.as_deref() {
                match tact_products::resolve_tact_segment(product) {
                    tact_products::TactResolution::Game(name) => {
                        if !self.logged_tact_products.contains(product) {
                            eprintln!("Mapped Blizzard product {} -> {}", product, name);
                            self.logged_tact_products.insert(product.to_string());
                        }
                        (None, Some(name))
                    }
                    tact_products::TactResolution::Shared(label) => {
                        if !self.logged_tact_products.contains(product) {
                            eprintln!("Mapped Blizzard shared path {} -> {}", product, label);
                            self.logged_tact_products.insert(product.to_string());
                        }
                        (None, Some(label))
                    }
                    tact_products::TactResolution::Unknown => {
                        if !self.logged_tact_products.contains(product) {
                            let req_count = new_entries
                                .iter()
                                .filter(|e| e.tact_product.as_deref() == Some(product))
                                .count();
                            eprintln!(
                                "Unmapped Blizzard CDN path: {} ({} req)",
                                product, req_count
                            );
                            self.logged_tact_products.insert(product.to_string());
                        }
                        (None, None)
                    }
                }
            } else {
                (None, None)
            }
        } else if self.auto_map_depots && service.to_lowercase() == "riot" {
            // Riot has no integer app id and no product slug in the URL path; resolve
            // the CDN host subdomain (lol/valorant/bacon) -> game name. GameAppId stays
            // None (only GameName is set). Unknown hosts leave GameName NULL (mirroring
            // an unmapped depot) and are LOGGED once per run so a 1-line host entry can
            // close the gap later.
            if let Some(host) = primary_cdn_host.as_deref() {
                let observation = self.riot_mapping.observe(host);
                match observation.game_name {
                    Some(name) => {
                        if observation.first_observation {
                            eprintln!("Mapped Riot host {} -> {}", host, name);
                        }
                        (None, Some(name.to_string()))
                    }
                    None => {
                        if observation.first_observation {
                            let req_count = new_entries
                                .iter()
                                .filter(|e| e.cdn_host.as_deref() == Some(host))
                                .count();
                            eprintln!("Unmapped Riot CDN host: {} ({} req)", host, req_count);
                        }
                        (None, None)
                    }
                }
            } else {
                (None, None)
            }
        } else {
            (None, None)
        };

        let gap = chrono::Duration::minutes(SESSION_GAP_MINUTES);
        let window_start = Utc.from_utc_datetime(&(first_timestamp - gap));
        let window_end = Utc.from_utc_datetime(&(first_timestamp + gap));
        let first_utc_dt = Utc.from_utc_datetime(&first_timestamp);
        let last_utc_dt = Utc.from_utc_datetime(&last_timestamp);

        // A process has no in-memory session history from earlier passes. The symmetric log-time
        // window rejoins a partial replay without joining old traffic to a current download. The
        // update only widens the row, and evicted rows never receive new traffic.
        let download_id_opt: Option<i64> = if let Some(ref xbox_title) = xbox_game_name {
            let by_title = sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND (\"GameName\" = $3 OR \"GameName\" IS NULL) AND \"StartTimeUtc\" <= $4 AND \"EndTimeUtc\" >= $5 AND \"IsEvicted\" = false AND \"Datasource\" = $6 ORDER BY (\"GameName\" = $3) DESC, \"StartTimeUtc\" DESC LIMIT 1")
                .persistent(false)
                .bind(client_ip)
                .bind(download_service)
                .bind(xbox_title)
                .bind(window_end)
                .bind(window_start)
                .bind(&self.datasource_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|row| row.get::<i64, _>("Id"));
            if by_title.is_some() {
                by_title
            } else {
                let fragments: Vec<String> = self
                    .xbox_patterns
                    .iter()
                    .filter(|(_, title, _)| title == xbox_title)
                    .map(|(fragment, _, _)| fragment.to_ascii_lowercase())
                    .collect();
                if fragments.is_empty() {
                    None
                } else {
                    // The URLs owned by the row identify the game even when generic Windows Update
                    // traffic replaced LastUrl. The DownloadId index limits the subquery to one row.
                    sqlx::query("SELECT \"Id\" FROM \"Downloads\" d WHERE d.\"ClientIp\" = $1 AND d.\"Service\" = $2 AND d.\"DepotId\" IS NULL AND d.\"GameName\" IS NULL AND d.\"StartTimeUtc\" <= $3 AND d.\"EndTimeUtc\" >= $4 AND d.\"IsEvicted\" = false AND d.\"Datasource\" = $5 AND EXISTS (SELECT 1 FROM \"LogEntries\" e, UNNEST($6::text[]) AS f(frag) WHERE e.\"DownloadId\" = d.\"Id\" AND strpos(lower(e.\"Url\"), f.frag) > 0) ORDER BY d.\"StartTimeUtc\" DESC LIMIT 1")
                        .persistent(false)
                        .bind(client_ip)
                        .bind(service)
                        .bind(window_end)
                        .bind(window_start)
                        .bind(&self.datasource_name)
                        .bind(&fragments)
                        .fetch_optional(&mut **tx)
                        .await?
                        .map(|row| row.get::<i64, _>("Id"))
                }
            }
        } else if let Some(depot_id) = primary_depot_id {
            sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" = $3 AND \"StartTimeUtc\" <= $4 AND \"EndTimeUtc\" >= $5 AND \"IsEvicted\" = false AND \"Datasource\" = $6 ORDER BY \"StartTimeUtc\" DESC LIMIT 1")
                .persistent(false)
                .bind(client_ip)
                .bind(service)
                .bind(depot_id as i64)
                .bind(window_end)
                .bind(window_start)
                .bind(&self.datasource_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|row| row.get::<i64, _>("Id"))
        } else if service.to_lowercase().contains("epic") {
            if let Some(path_prefix) = last_url.and_then(Self::extract_epic_path_prefix) {
                let like_pattern = format!("{}%", path_prefix);
                sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"LastUrl\" LIKE $3 AND \"StartTimeUtc\" <= $4 AND \"EndTimeUtc\" >= $5 AND \"IsEvicted\" = false AND \"Datasource\" = $6 ORDER BY \"StartTimeUtc\" DESC LIMIT 1")
                    .persistent(false)
                    .bind(client_ip)
                    .bind(service)
                    .bind(&like_pattern)
                    .bind(window_end)
                    .bind(window_start)
                    .bind(&self.datasource_name)
                    .fetch_optional(&mut **tx)
                    .await?
                    .map(|row| row.get::<i64, _>("Id"))
            } else {
                sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"StartTimeUtc\" <= $3 AND \"EndTimeUtc\" >= $4 AND \"IsEvicted\" = false AND \"Datasource\" = $5 ORDER BY \"StartTimeUtc\" DESC LIMIT 1")
                    .persistent(false)
                    .bind(client_ip)
                    .bind(service)
                    .bind(window_end)
                    .bind(window_start)
                    .bind(&self.datasource_name)
                    .fetch_optional(&mut **tx)
                    .await?
                    .map(|row| row.get::<i64, _>("Id"))
            }
        } else if service.to_lowercase() == "riot" && game_name.is_some() {
            let resolved_name = game_name
                .as_deref()
                .ok_or_else(|| anyhow::anyhow!("riot game_name expected but was None"))?;
            sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND (\"GameName\" = $3 OR \"GameName\" IS NULL) AND \"StartTimeUtc\" <= $4 AND \"EndTimeUtc\" >= $5 AND \"IsEvicted\" = false AND \"Datasource\" = $6 ORDER BY (\"GameName\" = $3) DESC, \"StartTimeUtc\" DESC LIMIT 1")
                .persistent(false)
                .bind(client_ip)
                .bind(service)
                .bind(resolved_name)
                .bind(window_end)
                .bind(window_start)
                .bind(&self.datasource_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|row| row.get::<i64, _>("Id"))
        } else if let Some(resolved_name) = game_name.as_deref() {
            sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"GameName\" = $3 AND \"StartTimeUtc\" <= $4 AND \"EndTimeUtc\" >= $5 AND \"IsEvicted\" = false AND \"Datasource\" = $6 ORDER BY \"StartTimeUtc\" DESC LIMIT 1")
                .persistent(false)
                .bind(client_ip)
                .bind(service)
                .bind(resolved_name)
                .bind(window_end)
                .bind(window_start)
                .bind(&self.datasource_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|row| row.get::<i64, _>("Id"))
        } else if let Some(product) = primary_tact_product.as_deref() {
            let like_pattern = format!("%/tpr/{}/%", product);
            sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND LOWER(\"LastUrl\") LIKE $3 AND \"StartTimeUtc\" <= $4 AND \"EndTimeUtc\" >= $5 AND \"IsEvicted\" = false AND \"Datasource\" = $6 ORDER BY \"StartTimeUtc\" DESC LIMIT 1")
                .persistent(false)
                .bind(client_ip)
                .bind(service)
                .bind(&like_pattern)
                .bind(window_end)
                .bind(window_start)
                .bind(&self.datasource_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|row| row.get::<i64, _>("Id"))
        } else if service.to_lowercase() == "riot" && primary_cdn_host.is_some() {
            sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"GameName\" IS NULL AND \"StartTimeUtc\" <= $3 AND \"EndTimeUtc\" >= $4 AND \"IsEvicted\" = false AND \"Datasource\" = $5 ORDER BY \"StartTimeUtc\" DESC LIMIT 1")
                .persistent(false)
                .bind(client_ip)
                .bind(service)
                .bind(window_end)
                .bind(window_start)
                .bind(&self.datasource_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|row| row.get::<i64, _>("Id"))
        } else {
            sqlx::query("SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"StartTimeUtc\" <= $3 AND \"EndTimeUtc\" >= $4 AND \"IsEvicted\" = false AND \"Datasource\" = $5 ORDER BY \"StartTimeUtc\" DESC LIMIT 1")
                .persistent(false)
                .bind(client_ip)
                .bind(service)
                .bind(window_end)
                .bind(window_start)
                .bind(&self.datasource_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|row| row.get::<i64, _>("Id"))
        };

        let game_image_url: Option<String> = None;
        let download_id = if let Some(download_id) = download_id_opt {
            // Only a newer line changes the last URL or reactivates the row. A concurrent resolver
            // that already named the row also keeps its chosen identity service. The row can be
            // marked evicted between the select above and this update; an evicted row must not
            // absorb new bytes, so a missed update takes the insert below.
            let updated = sqlx::query("UPDATE \"Downloads\" SET \"StartTimeUtc\" = LEAST(\"StartTimeUtc\", $12), \"EndTimeUtc\" = GREATEST(\"EndTimeUtc\", $1), \"CacheHitBytes\" = \"CacheHitBytes\" + $2, \"CacheMissBytes\" = \"CacheMissBytes\" + $3, \"LastUrl\" = CASE WHEN $1 > \"EndTimeUtc\" THEN $4 ELSE \"LastUrl\" END, \"IsActive\" = (\"IsActive\" OR $1 > \"EndTimeUtc\"), \"Service\" = CASE WHEN \"GameName\" IS NULL THEN $13 ELSE \"Service\" END, \"DepotId\" = COALESCE($5, \"DepotId\"), \"GameAppId\" = COALESCE($6, \"GameAppId\"), \"GameName\" = COALESCE($7, \"GameName\"), \"GameImageUrl\" = COALESCE($8, \"GameImageUrl\"), \"XboxProductId\" = COALESCE($9, \"XboxProductId\") WHERE \"Id\" = $10 AND \"Datasource\" = $11 AND \"IsEvicted\" = false")
                .bind(last_utc_dt)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(last_url)
                .bind(primary_depot_id.map(|depot| depot as i64))
                .bind(game_app_id.map(|id| id as i64))
                .bind(&game_name)
                .bind(&game_image_url)
                .bind(&xbox_product_id)
                .bind(download_id)
                .bind(&self.datasource_name)
                .bind(first_utc_dt)
                .bind(download_service)
                .execute(&mut **tx)
                .await?;
            (updated.rows_affected() > 0).then_some(download_id)
        } else {
            None
        };
        let download_id = if let Some(download_id) = download_id {
            download_id
        } else {
            let row = sqlx::query("INSERT INTO \"Downloads\" (\"ClientIp\", \"Service\", \"StartTimeUtc\", \"EndTimeUtc\", \"CacheHitBytes\", \"CacheMissBytes\", \"IsActive\", \"GameAppId\", \"GameName\", \"GameImageUrl\", \"LastUrl\", \"DepotId\", \"Datasource\", \"XboxProductId\") VALUES ($1, $2, $3, $4, $5, $6, true, $7, $8, $9, $10, $11, $12, $13) RETURNING \"Id\"")
                .bind(client_ip)
                .bind(download_service)
                .bind(first_utc_dt)
                .bind(last_utc_dt)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(game_app_id.map(|id| id as i64))
                .bind(&game_name)
                .bind(&game_image_url)
                .bind(last_url)
                .bind(primary_depot_id.map(|depot| depot as i64))
                .bind(&self.datasource_name)
                .bind(&xbox_product_id)
                .fetch_one(&mut **tx)
                .await?;
            row.get::<i64, _>("Id")
        };

        // Push entries to pending buffer - will be bulk-inserted by process_batch
        let now = Utc::now();
        for entry in &new_entries {
            pending_inserts.push(PendingLogEntry {
                timestamp: Utc.from_utc_datetime(&entry.timestamp),
                client_ip: clamp_chars(&entry.client_ip, LOG_ENTRY_CLIENT_IP_MAX_CHARS),
                service: clamp_chars(&entry.service, LOG_ENTRY_SERVICE_MAX_CHARS),
                method: clamp_chars(&entry.method, LOG_ENTRY_VARCHAR_MAX_CHARS),
                http_range: clamp_chars(&entry.http_range, LOG_ENTRY_HTTP_RANGE_MAX_CHARS),
                url: clamp_chars(&entry.url, LOG_ENTRY_URL_MAX_CHARS),
                status_code: entry.status_code,
                bytes_served: entry.bytes_served,
                cache_status: clamp_chars(&entry.cache_status, LOG_ENTRY_VARCHAR_MAX_CHARS),
                depot_id: entry.depot_id.map(|d| d as i64),
                download_id,
                created_at: now,
                datasource: clamp_chars(&self.datasource_name, LOG_ENTRY_DATASOURCE_MAX_CHARS),
            });
        }

        if skipped > 0 {
            eprintln!(
                "Skipped {} duplicate entries ({} new/{})",
                skipped,
                new_entries.len(),
                entries.len()
            );
        }

        Ok(())
    }

    /// Bulk INSERT all pending log entries in ONE UNNEST query per chunk.
    /// Called once per batch instead of once per session group.
    async fn bulk_insert_log_entries(
        tx: &mut sqlx::Transaction<'_, sqlx::Postgres>,
        entries: &[PendingLogEntry],
    ) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }

        let mut ts_vec: Vec<&chrono::DateTime<Utc>> = Vec::with_capacity(entries.len());
        let mut client_ip_vec: Vec<&str> = Vec::with_capacity(entries.len());
        let mut service_vec: Vec<&str> = Vec::with_capacity(entries.len());
        let mut method_vec: Vec<&str> = Vec::with_capacity(entries.len());
        let mut http_range_vec: Vec<&str> = Vec::with_capacity(entries.len());
        let mut url_vec: Vec<&str> = Vec::with_capacity(entries.len());
        let mut status_code_vec: Vec<i32> = Vec::with_capacity(entries.len());
        let mut bytes_served_vec: Vec<i64> = Vec::with_capacity(entries.len());
        let mut cache_status_vec: Vec<&str> = Vec::with_capacity(entries.len());
        let mut depot_id_vec: Vec<Option<i64>> = Vec::with_capacity(entries.len());
        let mut download_id_vec: Vec<i64> = Vec::with_capacity(entries.len());
        let mut created_at_vec: Vec<&chrono::DateTime<Utc>> = Vec::with_capacity(entries.len());
        let mut datasource_vec: Vec<&str> = Vec::with_capacity(entries.len());

        for entry in entries {
            ts_vec.push(&entry.timestamp);
            client_ip_vec.push(&entry.client_ip);
            service_vec.push(&entry.service);
            method_vec.push(&entry.method);
            http_range_vec.push(&entry.http_range);
            url_vec.push(&entry.url);
            status_code_vec.push(entry.status_code);
            bytes_served_vec.push(entry.bytes_served);
            cache_status_vec.push(&entry.cache_status);
            depot_id_vec.push(entry.depot_id);
            download_id_vec.push(entry.download_id);
            created_at_vec.push(&entry.created_at);
            datasource_vec.push(&entry.datasource);
        }

        // UNNEST uses one array bind per column, so row count does not multiply bind parameters.
        const MAX_ROWS: usize = 5000;
        let n = entries.len();
        let mut offset = 0usize;
        while offset < n {
            let end = std::cmp::min(offset + MAX_ROWS, n);
            sqlx::query(LOG_ENTRY_INSERT_SQL)
                .bind(&ts_vec[offset..end])
                .bind(&client_ip_vec[offset..end])
                .bind(&service_vec[offset..end])
                .bind(&method_vec[offset..end])
                .bind(&http_range_vec[offset..end])
                .bind(&url_vec[offset..end])
                .bind(&status_code_vec[offset..end])
                .bind(&bytes_served_vec[offset..end])
                .bind(&cache_status_vec[offset..end])
                .bind(&depot_id_vec[offset..end])
                .bind(&download_id_vec[offset..end])
                .bind(&created_at_vec[offset..end])
                .bind(&datasource_vec[offset..end])
                .execute(&mut **tx)
                .await?;
            offset = end;
        }

        Ok(())
    }
}

fn write_processor_failure_terminal(processor: &Processor, error: &anyhow::Error) -> Result<()> {
    processor.write_terminal(
        "failed",
        "failed",
        &format!("Log processing failed: {error:#}"),
    )
}

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();

    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let log_dir = PathBuf::from(&args.log_dir);
    let progress_path = PathBuf::from(&args.progress_path);
    let auto_map_depots = args.auto_map_depots == 1;
    let datasource_name = args.datasource_name;
    let resume_path = Path::new(&args.positions_path)
        .with_file_name(format!("rust_resume_{datasource_name}.json"));

    let run_id = uuid::Uuid::new_v4().to_string();

    // File-write-before-stdout-emit invariant: seed the progress file before the "started"
    // event so an event-triggered C# read never sees an empty/stale file. Real counters are
    // unknown yet; the processor's own ticks overwrite this as soon as processing begins.
    let starting = seed_progress(&run_id, "starting", "", "Starting log processing");
    if let Err(e) = progress_utils::write_progress_with_retry(&progress_path, &starting, 5) {
        eprintln!("Warning: failed to seed progress file: {:#}", e);
    }

    // A supplied positions file must validate BEFORE any database work: silently treating
    // a missing/malformed file as "all sources at 0" would re-ingest the entire history.
    let positions = match load_positions(&args.positions_path) {
        Ok(map) => map,
        Err(e) => {
            let msg = format!("Invalid positions file: {e:#}");
            eprintln!("{msg}");
            if let Err(write_err) = write_seed_failure_terminal(&progress_path, &run_id, &msg) {
                eprintln!("Warning: failed to write failure checkpoint: {write_err:#}");
            }
            reporter.emit_failed(
                "signalr.logProcessor.error.fatal",
                serde_json::json!({}),
                Some(msg.clone()),
            );
            return Err(anyhow::anyhow!(msg));
        }
    };

    // Emit started event
    reporter.emit_started("signalr.logProcessor.starting", serde_json::json!({}));
    reporter.emit_progress(0.0, "signalr.logProcessor.starting", serde_json::json!({}));

    let pool = match create_pool_or_write_terminal(&progress_path, &run_id, db::create_pool).await {
        Ok(pool) => pool,
        Err(error) => {
            reporter.emit_failed(
                "signalr.logProcessor.error.fatal",
                serde_json::json!({}),
                Some(format!("{error:#}")),
            );
            return Err(error);
        }
    };

    // Depot mappings are resolved lazily per depot inside the processor (one indexed SELECT
    // per new depot). The old whole-table preload ran on every spawn - once per second on a
    // live box - just to resolve at most a handful of new depots.
    let mut processor = Processor::new(
        pool,
        log_dir,
        progress_path,
        auto_map_depots,
        datasource_name,
        positions,
        run_id,
    );
    processor.resume_path = Some(resume_path);

    match processor.process().await {
        Ok(ProcessingOutcome::Completed) => {
            reporter.emit_complete("signalr.logProcessor.complete", serde_json::json!({}));
            Ok(())
        }
        Ok(ProcessingOutcome::Cancelled) => {
            reporter.emit_cancelled("signalr.logProcessor.cancelled", serde_json::json!({}));
            Ok(())
        }
        Err(e) => {
            if let Err(write_error) = write_processor_failure_terminal(&processor, &e) {
                eprintln!("Warning: failed to write failure checkpoint: {write_error:#}");
            }
            reporter.emit_failed(
                "signalr.logProcessor.error.fatal",
                serde_json::json!({}),
                Some(format!("{e:#}")),
            );
            Err(e)
        }
    }
}

#[cfg(test)]
mod classification_tests {
    use super::*;
    use flate2::write::GzEncoder;
    use flate2::Compression;
    use std::io::Write;

    fn test_pool() -> PgPool {
        sqlx::postgres::PgPoolOptions::new()
            .connect_lazy("postgres://postgres:password@127.0.0.1/lancache_test")
            .expect("create lazy test pool")
    }

    fn test_processor(
        log_dir: PathBuf,
        progress_path: PathBuf,
        positions: HashMap<String, u64>,
    ) -> Processor {
        Processor::new(
            test_pool(),
            log_dir,
            progress_path,
            false,
            "test".to_string(),
            positions,
            "test-run".to_string(),
        )
    }

    fn read_progress(path: &Path) -> serde_json::Value {
        let contents = std::fs::read_to_string(path).expect("read progress checkpoint");
        serde_json::from_str(&contents).expect("parse progress checkpoint")
    }

    fn resume_processor(
        directory: &Path,
        progress_name: &str,
        resume_path: &Path,
        position: u64,
    ) -> Processor {
        let positions = HashMap::from([("fallback-access.log".to_string(), position)]);
        let mut processor = test_processor(
            directory.to_path_buf(),
            directory.join(progress_name),
            positions,
        );
        processor.resume_path = Some(resume_path.to_path_buf());
        processor
    }

    pub(super) fn write_gzip(path: &Path, contents: &[u8], compression: Compression) {
        let file = std::fs::File::create(path).expect("create gzip fixture");
        let mut encoder = GzEncoder::new(file, compression);
        encoder.write_all(contents).expect("write gzip fixture");
        encoder.finish().expect("finish gzip fixture");
    }

    fn position(progress: &serde_json::Value) -> u64 {
        progress["source_positions"]["fallback-access.log"]
            .as_u64()
            .expect("read fallback source position")
    }

    fn parsers() -> (LogParser, HttpDetailedParser) {
        (
            LogParser::new(chrono_tz::UTC),
            HttpDetailedParser::new(chrono_tz::UTC),
        )
    }

    fn classify(raw: &[u8], complete: bool, kind: &SourceKind) -> ParseOutcome {
        let (p, d) = parsers();
        classify_record(&p, &d, raw, complete, kind)
    }

    const CACHELOG_LINE: &[u8] = b"[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /depot/123/chunk/ab HTTP/1.1\" 200 1024 \"-\" \"Valve/Steam\" \"HIT\" \"-\" \"-\"";
    const CACHELOG_JSON: &[u8] = br#"{"cache_identifier":"xboxlive","remote_addr":"192.0.2.40","time_local":"12/Sep/2026:18:22:34 +1000","method":"GET","path":"/content/file","status":"206","bytes_sent":2048,"user_agent":"Fixture/1.0","upstream_cache_status":"MISS","host":"cdn.example.test","http_range":"bytes=0-2047","forwarded_for":"198.51.100.9","remote_user":"fixture-user","referer":"-","proto":"HTTP/1.1","scheme":"https"}"#;
    const DETAILED_LINE: &[u8] = b"[01/Jan/2024:00:00:00 +0000] 192.168.1.50 GET \"/depot/123/chunk/ab\" - HTTP/1.1 200 \"-\" 512 1040 1024 0.005 1024 HIT lancache.steamcontent.com 200 0.004 \"Valve/Steam\"";

    #[test]
    fn garbage_is_unrecognized_never_silently_dropped() {
        let out = classify(
            b"complete garbage that is not a log line\n",
            true,
            &SourceKind::Monolithic,
        );
        assert!(matches!(out, ParseOutcome::Unrecognized));
    }

    #[test]
    fn probe_lines_classify_recognized_ignored_never_unparsed() {
        let probe = b"[steam] 172.20.0.5 / - - - [01/Jan/2024:00:00:00 +0000] \"GET / HTTP/1.1\" 301 162 \"-\" \"lancache-manager-status-check/1.0\" \"MISS\" \"h\" \"-\"\n";
        assert!(matches!(
            classify(probe, true, &SourceKind::Monolithic),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Probe)
        ));
        // Same probe UA inside an http-detailed record in a per-service file.
        let probe_detailed = b"[01/Jan/2024:00:00:00 +0000] 172.20.0.5 GET \"/\" - HTTP/1.1 301 \"-\" 512 162 162 0.001 162 MISS h 301 0.001 \"lancache-manager-status-check/1.0\"\n";
        assert!(matches!(
            classify(probe_detailed, true, &SourceKind::Service("steam".into())),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Probe)
        ));
    }

    #[test]
    fn heartbeat_classifies_recognized_ignored() {
        let hb = b"[steam] 10.0.0.1 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /lancache-heartbeat HTTP/1.1\" 204 0 \"-\" \"ua\" \"-\" \"-\" \"-\"\n";
        assert!(matches!(
            classify(hb, true, &SourceKind::Monolithic),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Heartbeat)
        ));
    }

    #[test]
    fn hintless_http_detailed_in_monolithic_file() {
        // The reporting-user case: bare-metal http-detailed content renamed to access.log.
        let out = classify(DETAILED_LINE, true, &SourceKind::Monolithic);
        assert!(matches!(
            out,
            ParseOutcome::RecognizedIgnored(IgnoredReason::Hintless)
        ));
    }

    #[test]
    fn detailed_line_in_service_file_parses_with_hint() {
        match classify(
            DETAILED_LINE,
            true,
            &SourceKind::Service("steam".to_string()),
        ) {
            ParseOutcome::Parsed(entry) => {
                assert_eq!(entry.service, "steam");
                assert_eq!(entry.bytes_served, 1024);
                assert_eq!(entry.depot_id, Some(123));
            }
            other => panic!("expected Parsed, got {other:?}"),
        }
    }

    #[test]
    fn cachelog_tag_wins_inside_a_service_file() {
        // A cachelog record inside blizzard-access.log keeps its own [steam] tag.
        match classify(
            CACHELOG_LINE,
            true,
            &SourceKind::Service("blizzard".to_string()),
        ) {
            ParseOutcome::Parsed(entry) => assert_eq!(entry.service, "steam"),
            other => panic!("expected Parsed, got {other:?}"),
        }
    }

    #[test]
    fn supported_cachelog_records_keep_explicit_identity() {
        let populated_text = b"[xboxlive] 192.0.2.40 / 198.51.100.9, 2001:db8::9 - fixture-user [12/Sep/2026:18:22:34 +1000] \"GET /content/text HTTP/1.1\" 200 1024 \"-\" \"Fixture/1.0\" \"HIT\" \"cdn.example.test\" \"-\"\n";
        for record in [populated_text.as_slice(), CACHELOG_JSON] {
            for kind in [
                SourceKind::Monolithic,
                SourceKind::Service("steam".to_string()),
            ] {
                match classify(record, true, &kind) {
                    ParseOutcome::Parsed(entry) => {
                        assert_eq!(entry.service, "xboxlive");
                        assert_eq!(entry.client_ip, "192.0.2.40");
                    }
                    other => panic!("expected Parsed, got {other:?}"),
                }
            }
        }
    }

    #[test]
    fn json_classification_preserves_existing_ignore_and_error_rules() {
        assert!(matches!(
            classify(CACHELOG_JSON, true, &SourceKind::Fallback),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Fallback)
        ));
        assert!(matches!(
            classify(CACHELOG_JSON, false, &SourceKind::Monolithic),
            ParseOutcome::Incomplete
        ));
        assert!(matches!(
            classify(
                br#"{"cache_identifier":"xboxlive"}"#,
                true,
                &SourceKind::Monolithic
            ),
            ParseOutcome::Unrecognized
        ));

        let probe = br#"{"cache_identifier":"steam","remote_addr":"192.0.2.40","time_local":"12/Sep/2026:18:22:34 +1000","method":"GET","path":"/","status":"200","bytes_sent":1,"user_agent":"lancache-manager-status-check/1.0","upstream_cache_status":"MISS","host":"cdn.example.test","http_range":"-"}"#;
        assert!(matches!(
            classify(probe, true, &SourceKind::Monolithic),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Probe)
        ));
    }

    fn live_processor(
        pool: &PgPool,
        directory: &Path,
        positions: HashMap<String, u64>,
        run_id: &str,
    ) -> Processor {
        Processor::new(
            pool.clone(),
            directory.to_path_buf(),
            directory.join(format!("{run_id}.json")),
            false,
            "format-fixture".to_string(),
            positions,
            run_id.to_string(),
        )
    }

    async fn stored_counts(pool: &PgPool) -> (i64, i64, i64, i64) {
        let row = sqlx::query(
            r#"SELECT
                   (SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = $1)::bigint AS "LogRows",
                   COUNT(*)::bigint AS "DownloadRows",
                   COALESCE(SUM("CacheHitBytes"), 0)::bigint AS "Hit",
                   COALESCE(SUM("CacheMissBytes"), 0)::bigint AS "Miss"
               FROM "Downloads" WHERE "Datasource" = $1"#,
        )
        .bind("format-fixture")
        .fetch_one(pool)
        .await
        .expect("read persisted counts");
        (
            row.get("LogRows"),
            row.get("DownloadRows"),
            row.get("Hit"),
            row.get("Miss"),
        )
    }

    pub(super) async fn create_test_schema(pool: &PgPool) {
        sqlx::raw_sql(
            r#"
            CREATE TABLE "Downloads" (
                "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Service" text NOT NULL,
                "ClientIp" text NOT NULL,
                "StartTimeUtc" timestamptz NOT NULL,
                "EndTimeUtc" timestamptz NOT NULL,
                "CacheHitBytes" bigint NOT NULL,
                "CacheMissBytes" bigint NOT NULL,
                "IsActive" boolean NOT NULL,
                "LastUrl" text,
                "DepotId" bigint,
                "GameAppId" bigint,
                "GameName" text,
                "GameImageUrl" text,
                "XboxProductId" text,
                "EpicAppId" text,
                "Datasource" text NOT NULL,
                "IsEvicted" boolean NOT NULL DEFAULT false
            );
            CREATE TABLE "LogEntries" (
                "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Timestamp" timestamptz NOT NULL,
                "ClientIp" text NOT NULL,
                "Service" text NOT NULL,
                "Method" text NOT NULL,
                "HttpRange" text,
                "Url" text NOT NULL,
                "StatusCode" integer NOT NULL,
                "BytesServed" bigint NOT NULL,
                "CacheStatus" text NOT NULL,
                "DepotId" bigint,
                "DownloadId" bigint NOT NULL REFERENCES "Downloads"("Id"),
                "CreatedAt" timestamptz NOT NULL,
                "Datasource" text NOT NULL
            );
            CREATE INDEX "IX_LogEntries_DownloadId" ON "LogEntries" ("DownloadId");
            CREATE INDEX "IX_LogEntries_DuplicateCheck" ON "LogEntries" ("ClientIp", "Service", "Timestamp", "Url", "BytesServed", "Datasource", md5(COALESCE("HttpRange", '')));
            CREATE TABLE "XboxCdnPatterns" (
                "UrlFragment" text NOT NULL,
                "Title" text,
                "ProductId" text NOT NULL
            );
            CREATE TABLE "XboxGameMappings" (
                "ProductId" text PRIMARY KEY,
                "Title" text
            );
            "#,
        )
        .execute(pool)
        .await
        .expect("create fixture schema");
    }

    #[sqlx::test(migrations = false)]
    async fn cachelog_formats_persist_resume_remove_and_do_not_replay(pool: PgPool) {
        create_test_schema(&pool).await;

        let directory = tempfile::tempdir().expect("create fixture directory");
        let text_control = "[xboxlive] 192.0.2.40 / - - - [11/Sep/2026:18:22:34 +1000] \"GET /xbox/control HTTP/1.1\" 200 1024 \"-\" \"Fixture/1.0\" \"HIT\" \"assets1.xboxlive.com\" \"-\"";
        let populated_text = "[xboxlive] 192.0.2.40 / 198.51.100.9, 2001:db8::9 - fixture-user [12/Sep/2026:18:22:34 +1000] \"GET /xbox/populated HTTP/1.1\" 206 2048 \"-\" \"Fixture/1.0\" \"MISS\" \"assets1.xboxlive.com\" \"bytes=0-2047\"";
        let xbox_json = serde_json::json!({
            "cache_identifier": "xboxlive",
            "remote_addr": "192.0.2.40",
            "time_local": "13/Sep/2026:18:22:34 +1000",
            "method": "GET",
            "path": "/xbox/json",
            "status": "200",
            "bytes_sent": 4096,
            "user_agent": "Fixture/1.0",
            "upstream_cache_status": "HIT",
            "host": "assets1.xboxlive.com",
            "http_range": "-",
            "timestamp": "2026-09-13T18:22:34+10:00",
            "forwarded_for": "198.51.100.10",
            "remote_user": "fixture-user",
            "referer": "-",
            "proto": "HTTP/1.1",
            "scheme": "https"
        })
        .to_string();
        let wsus_json = serde_json::json!({
            "cache_identifier": "wsus",
            "remote_addr": "2001:db8::44",
            "time_local": "13/Sep/2026:18:23:34 +1000",
            "method": "GET",
            "path": "/wsus/json",
            "status": "206",
            "bytes_sent": 8192,
            "user_agent": "Fixture/1.0",
            "upstream_cache_status": "MISS",
            "host": "download.windowsupdate.com",
            "http_range": "bytes=0-8191",
            "timestamp": "2026-09-13T18:23:34+10:00",
            "forwarded_for": "invalid forwarded text",
            "remote_user": "fixture-user",
            "referer": "-",
            "proto": "HTTP/1.1",
            "scheme": "https"
        })
        .to_string();
        let log_contents = format!("{text_control}\n{populated_text}\n{xbox_json}\n{wsus_json}\n");
        std::fs::write(directory.path().join("access.log"), &log_contents)
            .expect("write access log");

        let mut first = live_processor(&pool, directory.path(), HashMap::new(), "first");
        assert_eq!(
            first.process().await.expect("process initial import"),
            ProcessingOutcome::Completed
        );
        assert_eq!(first.entries_saved.load(Ordering::Relaxed), 4);
        let first_progress = read_progress(&directory.path().join("first.json"));
        assert_eq!(first_progress["terminal_status"], "completed");
        assert_eq!(first_progress["source_positions"]["access.log"], 4);
        assert_eq!(first_progress["entries_saved"], 4);
        assert_eq!(first_progress["unparsed_lines"], 0);
        assert_eq!(first_progress["hintless_http_detailed_lines"], 0);
        assert_eq!(first_progress["invalid_encoding_lines"], 0);
        assert_eq!(
            first_progress["files_with_errors"]
                .as_array()
                .unwrap()
                .len(),
            0
        );

        let entries = sqlx::query(
            r#"SELECT "ClientIp", "Service", "Timestamp", "CacheStatus", "BytesServed"
               FROM "LogEntries" ORDER BY "Timestamp", "Service""#,
        )
        .fetch_all(&pool)
        .await
        .expect("read persisted entries");
        assert_eq!(entries.len(), 4);
        let expected_times = [
            Utc.with_ymd_and_hms(2026, 9, 11, 8, 22, 34)
                .single()
                .unwrap(),
            Utc.with_ymd_and_hms(2026, 9, 12, 8, 22, 34)
                .single()
                .unwrap(),
            Utc.with_ymd_and_hms(2026, 9, 13, 8, 22, 34)
                .single()
                .unwrap(),
            Utc.with_ymd_and_hms(2026, 9, 13, 8, 23, 34)
                .single()
                .unwrap(),
        ];
        for (row, expected) in entries.iter().zip(expected_times) {
            assert_eq!(row.get::<chrono::DateTime<Utc>, _>("Timestamp"), expected);
        }
        assert_eq!(entries[0].get::<String, _>("ClientIp"), "192.0.2.40");
        assert_eq!(entries[1].get::<String, _>("ClientIp"), "192.0.2.40");
        assert_eq!(entries[2].get::<String, _>("ClientIp"), "192.0.2.40");
        assert_eq!(entries[3].get::<String, _>("ClientIp"), "2001:db8::44");

        let totals = sqlx::query(
            r#"SELECT COUNT(*)::bigint AS "Rows",
                      COALESCE(SUM("CacheHitBytes"), 0)::bigint AS "Hit",
                      COALESCE(SUM("CacheMissBytes"), 0)::bigint AS "Miss"
               FROM "Downloads" WHERE "Datasource" = $1"#,
        )
        .bind("format-fixture")
        .fetch_one(&pool)
        .await
        .expect("read download totals");
        assert_eq!(totals.get::<i64, _>("Rows"), 4);
        assert_eq!(totals.get::<i64, _>("Hit"), 5120);
        assert_eq!(totals.get::<i64, _>("Miss"), 10240);
        let initial_counts = stored_counts(&pool).await;
        assert_eq!(initial_counts, (4, 4, 5120, 10240));

        let xbox_totals = sqlx::query(
            r#"SELECT COALESCE(SUM("CacheHitBytes"), 0)::bigint AS "CacheHitBytes",
                      COALESCE(SUM("CacheMissBytes"), 0)::bigint AS "CacheMissBytes" FROM "Downloads"
               WHERE "Datasource" = $1 AND "Service" = $2 AND "ClientIp" = $3"#,
        )
        .bind("format-fixture")
        .bind("xboxlive")
        .bind("192.0.2.40")
        .fetch_one(&pool)
        .await
        .expect("read xboxlive totals");
        assert_eq!(xbox_totals.get::<i64, _>("CacheHitBytes"), 5120);
        assert_eq!(xbox_totals.get::<i64, _>("CacheMissBytes"), 2048);

        let wsus_totals = sqlx::query(
            r#"SELECT COALESCE(SUM("CacheHitBytes"), 0)::bigint AS "CacheHitBytes",
                      COALESCE(SUM("CacheMissBytes"), 0)::bigint AS "CacheMissBytes" FROM "Downloads"
               WHERE "Datasource" = $1 AND "Service" = $2 AND "ClientIp" = $3"#,
        )
        .bind("format-fixture")
        .bind("wsus")
        .bind("2001:db8::44")
        .fetch_one(&pool)
        .await
        .expect("read wsus totals");
        assert_eq!(wsus_totals.get::<i64, _>("CacheHitBytes"), 0);
        assert_eq!(wsus_totals.get::<i64, _>("CacheMissBytes"), 8192);

        let resumed_positions = HashMap::from([("access.log".to_string(), 4)]);
        let mut resumed = live_processor(&pool, directory.path(), resumed_positions, "resumed");
        assert_eq!(
            resumed.process().await.expect("process resumed import"),
            ProcessingOutcome::Completed
        );
        assert_eq!(resumed.entries_saved.load(Ordering::Relaxed), 0);
        assert_eq!(stored_counts(&pool).await, initial_counts);

        let mut reset = live_processor(&pool, directory.path(), HashMap::new(), "reset");
        assert_eq!(
            reset.process().await.expect("process reset import"),
            ProcessingOutcome::Completed
        );
        assert_eq!(reset.entries_saved.load(Ordering::Relaxed), 0);
        let reset_progress = read_progress(&directory.path().join("reset.json"));
        assert_eq!(reset_progress["source_positions"]["access.log"], 4);
        assert_eq!(stored_counts(&pool).await, initial_counts);

        let saved_positions = HashMap::from([("access.log".to_string(), 4)]);
        let removal = lancache_processor::log_purge::remove_all_log_entries_for_service(
            directory.path(),
            "xboxlive",
            Some(&saved_positions),
        )
        .expect("remove xboxlive log records");
        assert_eq!(removal.lines_removed, 3);
        assert_eq!(
            removal.lines_removed_before_position_by_stem,
            HashMap::from([("access.log".to_string(), 3)])
        );
        assert_eq!(
            std::fs::read_to_string(directory.path().join("access.log"))
                .expect("read rewritten access log"),
            format!("{wsus_json}\n")
        );

        sqlx::query(r#"DELETE FROM "LogEntries" WHERE "Datasource" = $1 AND "Service" = $2"#)
            .bind("format-fixture")
            .bind("xboxlive")
            .execute(&pool)
            .await
            .expect("delete xboxlive entries");
        sqlx::query(r#"DELETE FROM "Downloads" WHERE "Datasource" = $1 AND "Service" = $2"#)
            .bind("format-fixture")
            .bind("xboxlive")
            .execute(&pool)
            .await
            .expect("delete xboxlive downloads");

        let adjusted_positions = HashMap::from([("access.log".to_string(), 1)]);
        let mut adjusted = live_processor(&pool, directory.path(), adjusted_positions, "adjusted");
        assert_eq!(
            adjusted.process().await.expect("process adjusted resume"),
            ProcessingOutcome::Completed
        );
        assert_eq!(adjusted.entries_saved.load(Ordering::Relaxed), 0);

        let mut replay = live_processor(&pool, directory.path(), HashMap::new(), "replay");
        assert_eq!(
            replay.process().await.expect("process zero replay"),
            ProcessingOutcome::Completed
        );
        assert_eq!(replay.entries_saved.load(Ordering::Relaxed), 0);

        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = $1 AND "Service" = 'xboxlive'"#,
            )
            .bind("format-fixture")
            .fetch_one(&pool)
            .await
            .expect("count remaining xboxlive entries"),
            0
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = $1 AND "Service" = 'wsus'"#,
            )
            .bind("format-fixture")
            .fetch_one(&pool)
            .await
            .expect("count remaining wsus entries"),
            1
        );
        let final_totals = sqlx::query(
            r#"SELECT "CacheHitBytes", "CacheMissBytes" FROM "Downloads"
               WHERE "Datasource" = $1 AND "Service" = 'wsus'"#,
        )
        .bind("format-fixture")
        .fetch_one(&pool)
        .await
        .expect("read final wsus totals");
        assert_eq!(final_totals.get::<i64, _>("CacheHitBytes"), 0);
        assert_eq!(final_totals.get::<i64, _>("CacheMissBytes"), 8192);
    }

    #[test]
    fn fallback_lines_always_skip_even_when_parseable() {
        assert!(matches!(
            classify(DETAILED_LINE, true, &SourceKind::Fallback),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Fallback)
        ));
        assert!(matches!(
            classify(b"garbage\n", true, &SourceKind::Fallback),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Fallback)
        ));
    }

    #[test]
    fn blank_records_are_recognized_ignored() {
        assert!(matches!(
            classify(b"\n", true, &SourceKind::Monolithic),
            ParseOutcome::RecognizedIgnored(IgnoredReason::Blank)
        ));
    }

    #[test]
    fn invalid_utf8_that_parses_no_recognizer_counts_invalid_encoding() {
        let raw = b"\xff\xfe garbage \xff\n";
        assert!(matches!(
            classify(raw, true, &SourceKind::Monolithic),
            ParseOutcome::InvalidEncoding
        ));
    }

    #[test]
    fn invalid_utf8_confined_to_text_fields_still_parses() {
        // A cachelog line with a bad byte inside the user-agent field: lossy decoding
        // confines the damage and the record still ingests.
        let mut raw = Vec::new();
        raw.extend_from_slice(b"[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /depot/1/chunk/a HTTP/1.1\" 200 10 \"-\" \"Va\xfflve\" \"HIT\" \"-\" \"-\"\n");
        assert!(matches!(
            classify(&raw, true, &SourceKind::Monolithic),
            ParseOutcome::Parsed(_)
        ));
    }

    #[test]
    fn unterminated_final_record_is_incomplete() {
        assert!(matches!(
            classify(CACHELOG_LINE, false, &SourceKind::Monolithic),
            ParseOutcome::Incomplete
        ));
    }

    #[test]
    fn terminal_outcome_rules() {
        // Clean run.
        let (t, _) = Processor::resolve_terminal_outcome(&[], 0, 0, 0);
        assert_eq!(t, "completed");
        // Parse anomalies are warnings, never silent success.
        let (t, msg) = Processor::resolve_terminal_outcome(&[], 5, 0, 0);
        assert_eq!(t, "completed_with_warnings");
        assert!(msg.contains("5 unrecognized"));
        let (t, _) = Processor::resolve_terminal_outcome(&[], 0, 3, 0);
        assert_eq!(t, "completed_with_warnings");
        let (t, _) = Processor::resolve_terminal_outcome(&[], 0, 0, 2);
        assert_eq!(t, "completed_with_warnings");
        // Any file error caps at partial, even with zero parse anomalies.
        let (t, _) = Processor::resolve_terminal_outcome(&["a.log: boom".to_string()], 0, 0, 0);
        assert_eq!(t, "partial");
    }

    #[tokio::test]
    async fn corrupt_member_freezes_position_but_newer_file_is_still_processed() {
        let tmp = tempfile::tempdir().expect("create fixture directory");
        std::fs::write(
            tmp.path().join("fallback-access.log.1.gz"),
            b"not a gzip stream",
        )
        .expect("write corrupt rotation");
        std::fs::write(tmp.path().join("fallback-access.log"), b"one\ntwo\n")
            .expect("write live log");
        let progress_path = tmp.path().join("progress.json");
        let mut positions = HashMap::new();
        positions.insert("fallback-access.log".to_string(), 1);
        let mut processor =
            test_processor(tmp.path().to_path_buf(), progress_path.clone(), positions);
        // Keep the fixture in the normal partial-terminal path without requiring a DB insert.
        processor.entries_saved.store(1, Ordering::Relaxed);

        let outcome = processor.process().await.expect("finish partial run");

        assert_eq!(outcome, ProcessingOutcome::Completed);
        let progress = read_progress(&progress_path);
        assert_eq!(progress["terminal_status"], "partial");
        // A rotated read failure no longer freezes the stem. With no saved mark, the pending
        // skip is cleared so both complete records in the current file remain reachable.
        assert_eq!(progress["source_positions"]["fallback-access.log"], 2);
        assert_eq!(progress["lines_parsed"], 2);
        assert_eq!(progress["files_with_errors"].as_array().unwrap().len(), 1);
    }

    #[tokio::test]
    async fn byte_resume_skips_unchanged_rotations_and_reads_only_appended_records() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let resume_path = directory.path().join("resume.json");
        let oldest = directory.path().join("fallback-access.log.2.gz");
        let current = directory.path().join("fallback-access.log");
        write_gzip(&oldest, b"a\nb\nc\n", Compression::default());
        std::fs::write(directory.path().join("fallback-access.log.1"), b"d\ne\nf\n")
            .expect("write rotation fixture");
        std::fs::write(&current, b"g\nh\n").expect("write current fixture");

        let mut first = resume_processor(directory.path(), "first.json", &resume_path, 0);
        first.process().await.expect("process initial series");
        assert_eq!(
            position(&read_progress(&directory.path().join("first.json"))),
            8
        );

        let compressed_len = std::fs::metadata(&oldest)
            .expect("read compressed fixture length")
            .len() as usize;
        std::fs::write(&oldest, vec![b'x'; compressed_len])
            .expect("replace compressed bytes in place");
        std::fs::OpenOptions::new()
            .append(true)
            .open(&current)
            .expect("open current fixture")
            .write_all(b"i\nj\n")
            .expect("append current records");

        let mut resumed = resume_processor(directory.path(), "resumed.json", &resume_path, 8);
        resumed.process().await.expect("resume from byte offset");
        let resumed_progress = read_progress(&directory.path().join("resumed.json"));
        assert_eq!(resumed_progress["terminal_status"], "completed");
        assert_eq!(position(&resumed_progress), 10);
        assert_eq!(resumed_progress["lines_parsed"], 10);

        let positions = HashMap::from([("fallback-access.log".to_string(), 8)]);
        let mut control = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("control.json"),
            positions,
        );
        control.process().await.expect("run line-skip control");
        let control_progress = read_progress(&directory.path().join("control.json"));
        assert_eq!(control_progress["terminal_status"], "partial");
        assert_eq!(
            control_progress["files_with_errors"]
                .as_array()
                .unwrap()
                .len(),
            1
        );
    }

    #[tokio::test]
    async fn mismatched_position_uses_line_skip() {
        let directory = tempfile::tempdir().expect("create position fixture");
        let resume_path = directory.path().join("resume.json");
        let current = directory.path().join("fallback-access.log");
        std::fs::write(&current, b"a\nb\nc\n").expect("write current fixture");
        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process initial fixture");
        std::fs::OpenOptions::new()
            .append(true)
            .open(&current)
            .expect("open current fixture")
            .write_all(b"d\ne\n")
            .expect("append current records");

        let mut second = resume_processor(directory.path(), "second.json", &resume_path, 2);
        second.before_resume_open = Some(Box::new(|| {
            panic!("a sidecar for another public position must not be opened")
        }));
        second.process().await.expect("fall back to line skip");

        let progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(position(&progress), 5);
        assert_eq!(progress["lines_parsed"], 5);
    }

    #[tokio::test]
    async fn rotation_moves_the_saved_plain_file_without_replaying_older_members() {
        let directory = tempfile::tempdir().expect("create rotation fixture");
        let resume_path = directory.path().join("resume.json");
        let oldest = directory.path().join("fallback-access.log.2.gz");
        let middle = directory.path().join("fallback-access.log.1");
        let current = directory.path().join("fallback-access.log");
        write_gzip(&oldest, b"a\nb\nc\n", Compression::default());
        std::fs::write(&middle, b"d\ne\nf\n").expect("write middle fixture");
        std::fs::write(&current, b"g\nh\n").expect("write current fixture");
        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process initial series");

        let compressed_len = std::fs::metadata(&oldest)
            .expect("read compressed fixture length")
            .len() as usize;
        std::fs::write(&oldest, vec![b'x'; compressed_len])
            .expect("replace compressed bytes in place");
        std::fs::rename(&oldest, directory.path().join("fallback-access.log.3.gz"))
            .expect("shift oldest rotation");
        std::fs::rename(&middle, directory.path().join("fallback-access.log.2"))
            .expect("shift middle rotation");
        std::fs::rename(&current, directory.path().join("fallback-access.log.1"))
            .expect("rotate current fixture");
        std::fs::write(&current, b"i\n").expect("write new current fixture");

        let mut second = resume_processor(directory.path(), "second.json", &resume_path, 8);
        second.process().await.expect("resume moved plain file");
        let progress = read_progress(&directory.path().join("second.json"));

        assert_eq!(progress["terminal_status"], "completed");
        assert_eq!(position(&progress), 9);
        assert_eq!(progress["lines_parsed"], 9);
    }

    #[tokio::test]
    async fn truncated_current_file_refuses_the_saved_offset() {
        let directory = tempfile::tempdir().expect("create truncation fixture");
        let resume_path = directory.path().join("resume.json");
        let current = directory.path().join("fallback-access.log");
        std::fs::write(&current, b"a\nb\nc\n").expect("write current fixture");
        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process initial fixture");
        std::fs::write(&current, b"z\n").expect("truncate current fixture");

        let mut second = resume_processor(directory.path(), "second.json", &resume_path, 3);
        second.before_resume_open = Some(Box::new(|| {
            panic!("a saved offset beyond the current size must not be opened")
        }));
        second.process().await.expect("fall back after truncation");

        let progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(position(&progress), 1);
        assert_eq!(progress["lines_parsed"], 1);
    }

    #[tokio::test]
    async fn resume_adjusts_for_a_deleted_oldest_rotation() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let resume_path = directory.path().join("resume.json");
        let oldest = directory.path().join("fallback-access.log.2.gz");
        let current = directory.path().join("fallback-access.log");
        write_gzip(&oldest, b"a\nb\nc\n", Compression::default());
        std::fs::write(directory.path().join("fallback-access.log.1"), b"d\ne\nf\n")
            .expect("write rotation fixture");
        std::fs::write(&current, b"g\nh\n").expect("write current fixture");

        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process initial series");
        std::fs::remove_file(&oldest).expect("delete oldest rotation");
        std::fs::OpenOptions::new()
            .append(true)
            .open(&current)
            .expect("open current fixture")
            .write_all(b"i\n")
            .expect("append current record");

        let mut resumed = resume_processor(directory.path(), "resumed.json", &resume_path, 8);
        resumed.process().await.expect("resume shortened series");

        assert_eq!(
            position(&read_progress(&directory.path().join("resumed.json"))),
            6
        );
        assert_eq!(resumed.skipped_fallback_lines, 1);
    }

    #[tokio::test]
    async fn changed_tail_refuses_resume_and_uses_saved_member_marks() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let resume_path = directory.path().join("resume.json");
        let oldest = directory.path().join("access.log.2.gz");
        let current = directory.path().join("access.log");
        write_gzip(&oldest, b"a\nb\nc\n", Compression::default());
        std::fs::write(directory.path().join("access.log.1"), b"d\ne\nf\n")
            .expect("write rotation fixture");
        std::fs::write(&current, b"g\nh\n").expect("write current fixture");

        let first_positions = HashMap::from([("access.log".to_string(), 0)]);
        let mut first = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("first.json"),
            first_positions,
        );
        first.resume_path = Some(resume_path.clone());
        first.process().await.expect("process initial series");
        let compressed_len = std::fs::metadata(&oldest)
            .expect("read compressed fixture length")
            .len() as usize;
        std::fs::write(&oldest, vec![b'x'; compressed_len])
            .expect("replace compressed bytes in place");
        std::fs::write(&current, b"G\nh\ni\nj\n").expect("rewrite current tail and append");

        let second_positions = HashMap::from([("access.log".to_string(), 8)]);
        let mut resumed = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("resumed.json"),
            second_positions,
        );
        resumed.resume_path = Some(resume_path);
        resumed.process().await.expect("fall back to line skip");
        let progress = read_progress(&directory.path().join("resumed.json"));

        assert_eq!(progress["terminal_status"], "partial");
        assert_eq!(progress["source_positions"]["access.log"], 7);
        assert_eq!(progress["lines_parsed"], 7);
        assert_eq!(progress["unparsed_lines"], 2);
    }

    #[tokio::test]
    async fn changed_prefix_keeps_the_full_skip_budget() {
        let directory = tempfile::tempdir().expect("create prefix fixture");
        let rotated = directory.path().join("fallback-access.log.1");
        std::fs::write(&rotated, b"a\nb\n").expect("write saved prefix");
        let identity = log_purge::file_identity(&rotated).expect("read saved identity");
        let mark = log_resume::FileMark {
            identity: identity.clone(),
            len: 4,
            records: 2,
            offset: Some(4),
            tail_crc: Some(log_resume::tail_crc(&rotated, 4).expect("hash saved prefix")),
        };
        std::fs::write(&rotated, b"a\nb\nc\n").expect("grow prefix fixture");
        let changed_path = rotated.clone();
        let mut processor = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("progress.json"),
            HashMap::new(),
        );
        processor.before_content_open = Some(Box::new(move |path| {
            if path == changed_path {
                std::fs::write(path, b"x\ny\nz\n").expect("change offered prefix");
                assert_eq!(
                    log_purge::file_identity(path).expect("read changed identity"),
                    identity
                );
            }
        }));
        let mut lines_to_skip = 3;
        let mut records_consumed = 0;
        let mut resume = FileResume {
            mark: Some(mark),
            ..FileResume::default()
        };

        let outcome = processor
            .process_single_file(
                &LogFile::from_path(rotated),
                6,
                &mut lines_to_skip,
                &SourceKind::Fallback,
                &mut records_consumed,
                &mut resume,
            )
            .await
            .expect("read changed prefix from its start");

        assert_eq!(outcome, FileProcessingOutcome::Completed);
        assert_eq!(lines_to_skip, 0);
        assert_eq!(records_consumed, 3);
        assert_eq!(resume.held_skip, 0);
        assert!(resume.mark.is_none());
        assert_eq!(processor.skipped_fallback_lines, 0);
    }

    #[tokio::test]
    async fn malformed_sidecar_falls_back_and_is_replaced() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let resume_path = directory.path().join("resume.json");
        let current = directory.path().join("fallback-access.log");
        std::fs::write(&current, b"a\nb\n").expect("write current fixture");
        std::fs::write(&resume_path, "not json").expect("write malformed sidecar");

        let mut processor = resume_processor(directory.path(), "progress.json", &resume_path, 0);
        processor
            .process()
            .await
            .expect("use line-position fallback");

        assert_eq!(
            position(&read_progress(&directory.path().join("progress.json"))),
            2
        );
        assert_eq!(log_resume::load(&resume_path).schema_version, 1);
    }

    #[tokio::test]
    async fn rotated_read_failure_keeps_newer_records_reachable() {
        let directory = tempfile::tempdir().expect("create read-error fixture");
        let oldest = directory.path().join("fallback-access.log.2");
        std::fs::write(&oldest, b"a\nb\nc\nd\n").expect("write oldest fixture");
        std::fs::write(directory.path().join("fallback-access.log.1"), b"e\nf\ng\n")
            .expect("write rotation fixture");
        std::fs::write(directory.path().join("fallback-access.log"), b"h\ni\n")
            .expect("write current fixture");

        let mut first = resume_processor(
            directory.path(),
            "first.json",
            &directory.path().join("resume.json"),
            0,
        );
        first.process().await.expect("process initial series");
        std::fs::remove_file(&oldest).expect("remove oldest fixture");
        std::fs::write(&oldest, b"replacement\n").expect("replace oldest identity");
        std::fs::OpenOptions::new()
            .append(true)
            .open(directory.path().join("fallback-access.log"))
            .expect("open current fixture")
            .write_all(b"j\nk\n")
            .expect("append current records");

        let positions = HashMap::from([("fallback-access.log".to_string(), 9)]);
        let mut second = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("second.json"),
            positions,
        );
        second.read_failure = Some(oldest.clone());
        second
            .process()
            .await
            .expect("continue after rotated read failure");
        let progress = read_progress(&directory.path().join("second.json"));

        assert_eq!(progress["terminal_status"], "partial");
        assert_eq!(position(&progress), 7);
        assert_eq!(progress["lines_parsed"], 7);
        assert!(progress["files_with_errors"][0]
            .as_str()
            .unwrap()
            .contains("fallback-access.log.2"));
    }

    #[tokio::test]
    async fn heartbeat_only_followup_recovers_after_a_truncated_rotation() {
        let directory = tempfile::tempdir().expect("create heartbeat fixture");
        let resume_path = directory.path().join("resume.json");
        let rotation = directory.path().join("fallback-access.log.1.gz");
        let current = directory.path().join("fallback-access.log");
        let rotated = (0..200)
            .map(|index| format!("rotation-{index}\n"))
            .collect::<String>();
        write_gzip(&rotation, rotated.as_bytes(), Compression::none());
        let compressed = std::fs::read(&rotation).expect("read gzip fixture");
        std::fs::write(&rotation, &compressed[..compressed.len() * 3 / 5])
            .expect("truncate gzip fixture");
        std::fs::write(&current, b"current-a\ncurrent-b\n").expect("write current fixture");

        let mut reader = LogFileReader::open(&rotation).expect("open truncated fixture");
        let mut buffer = Vec::new();
        let mut complete = 0u64;
        loop {
            buffer.clear();
            match reader.read_until_newline(&mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(_) if buffer.ends_with(b"\n") => complete += 1,
                Ok(_) => break,
            }
        }
        assert!(complete > 0);

        let mut first = resume_processor(directory.path(), "first.json", &resume_path, 0);
        first.process().await.expect("process truncated series");
        let first_progress = read_progress(&directory.path().join("first.json"));
        assert_eq!(first_progress["terminal_status"], "partial");
        assert_eq!(position(&first_progress), complete + 2);

        std::fs::remove_file(&resume_path).expect("remove resume sidecar");
        let heartbeat = b"[steam] 10.0.0.1 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /lancache-heartbeat HTTP/1.1\" 204 0 \"-\" \"ua\" \"-\" \"-\" \"-\"\n";
        let mut file = std::fs::OpenOptions::new()
            .append(true)
            .open(&current)
            .expect("open current fixture");
        file.write_all(heartbeat).expect("append first heartbeat");
        file.write_all(heartbeat).expect("append second heartbeat");
        drop(file);

        let mut second =
            resume_processor(directory.path(), "second.json", &resume_path, complete + 2);
        second.process().await.expect("recover through line skip");
        let second_progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(second_progress["terminal_status"], "partial");
        assert_eq!(position(&second_progress), complete + 4);
        assert!(resume_path.is_file());

        std::fs::OpenOptions::new()
            .append(true)
            .open(&current)
            .expect("reopen current fixture")
            .write_all(heartbeat)
            .expect("append third heartbeat");
        let mut third =
            resume_processor(directory.path(), "third.json", &resume_path, complete + 4);
        third.process().await.expect("resume after heartbeat pass");
        let third_progress = read_progress(&directory.path().join("third.json"));
        assert_eq!(third_progress["terminal_status"], "completed");
        assert_eq!(position(&third_progress), complete + 5);
        assert!(third_progress["files_with_errors"]
            .as_array()
            .expect("read file errors")
            .is_empty());
    }

    #[tokio::test]
    async fn truncated_recompression_reduces_the_pending_skip() {
        let directory = tempfile::tempdir().expect("create recompression fixture");
        let plain_rotation = directory.path().join("access.log.1");
        let gzip_rotation = directory.path().join("access.log.1.gz");
        let current = directory.path().join("access.log");
        std::fs::write(&plain_rotation, b"a\nb\nc\nd\ne\n").expect("write plain rotation");
        std::fs::write(&current, b"f\ng\n").expect("write current fixture");
        let first_positions = HashMap::from([("access.log".to_string(), 0)]);
        test_processor(
            directory.path().to_path_buf(),
            directory.path().join("first.json"),
            first_positions,
        )
        .process()
        .await
        .expect("process initial series");

        write_gzip(&gzip_rotation, b"a\nb\n", Compression::none());
        let compressed = std::fs::read(&gzip_rotation).expect("read gzip fixture");
        std::fs::write(&gzip_rotation, &compressed[..compressed.len() - 4])
            .expect("truncate gzip trailer");
        std::fs::remove_file(&plain_rotation).expect("remove plain rotation");
        std::fs::OpenOptions::new()
            .append(true)
            .open(&current)
            .expect("open current fixture")
            .write_all(b"h\ni\nj\n")
            .expect("append current records");

        let positions = HashMap::from([("access.log".to_string(), 7)]);
        let mut second = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("second.json"),
            positions,
        );
        second
            .process()
            .await
            .expect("continue after truncated recompression");
        let progress = read_progress(&directory.path().join("second.json"));

        assert_eq!(progress["terminal_status"], "partial");
        assert_eq!(progress["source_positions"]["access.log"], 7);
        assert_eq!(progress["lines_parsed"], 7);
        assert_eq!(progress["unparsed_lines"], 5);
    }

    #[tokio::test]
    async fn rotated_open_failure_is_typed_but_current_open_failure_propagates() {
        let directory = tempfile::tempdir().expect("create open-error fixture");
        let absent = directory.path().join("access.log.3.gz");
        let mut processor = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("progress.json"),
            HashMap::new(),
        );
        let mut skip = 0;
        let mut consumed = 0;
        let mut resume = FileResume::default();

        let rotated = processor
            .process_single_file_with_cancel(
                &LogFile {
                    path: absent.clone(),
                    rotation_number: Some(3),
                    is_compressed: true,
                },
                0,
                &mut skip,
                &SourceKind::Monolithic,
                &mut consumed,
                &mut resume,
                || false,
            )
            .await
            .expect("classify rotated open failure");
        assert!(matches!(
            rotated,
            FileProcessingOutcome::RotatedMemberUnreadable(RotatedMemberProblem {
                kind: RotatedMemberProblemKind::Open,
                ..
            })
        ));

        let mut resume = FileResume::default();
        let current = processor
            .process_single_file_with_cancel(
                &LogFile {
                    path: absent,
                    rotation_number: None,
                    is_compressed: true,
                },
                0,
                &mut skip,
                &SourceKind::Monolithic,
                &mut consumed,
                &mut resume,
                || false,
            )
            .await;
        assert!(current.is_err());
    }

    #[tokio::test]
    async fn resume_target_rotation_restarts_with_the_refreshed_series() {
        let directory = tempfile::tempdir().expect("create rotation-race fixture");
        let resume_path = directory.path().join("resume.json");
        let current = directory.path().join("fallback-access.log");
        std::fs::write(&current, b"a\nb\nc\n").expect("write current fixture");
        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process initial series");

        let rotated = directory.path().join("fallback-access.log.1");
        let hook_current = current.clone();
        let hook_rotated = rotated.clone();
        let mut second = resume_processor(directory.path(), "second.json", &resume_path, 3);
        second.before_resume_open = Some(Box::new(move || {
            std::fs::rename(&hook_current, &hook_rotated).expect("rotate current fixture");
            std::fs::write(&hook_current, b"d\ne\nf\ng\nh\n").expect("write new current fixture");
        }));

        second
            .process()
            .await
            .expect("restart after identity change");
        let progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(position(&progress), 8);
        assert_eq!(progress["lines_parsed"], 8);
    }

    #[tokio::test]
    async fn rewritten_resume_target_restarts_with_line_skip() {
        let directory = tempfile::tempdir().expect("create rewrite fixture");
        let resume_path = directory.path().join("resume.json");
        let current = directory.path().join("fallback-access.log");
        std::fs::write(&current, b"a\nb\nc\n").expect("write current fixture");
        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process initial series");

        let identity = log_purge::file_identity(&current).expect("read current identity");
        let hook_current = current.clone();
        let mut second = resume_processor(directory.path(), "second.json", &resume_path, 3);
        second.before_resume_open = Some(Box::new(move || {
            std::fs::write(
                &hook_current,
                b"long-first\nlong-second\nlong-third\ng\nh\n",
            )
            .expect("rewrite current fixture");
            assert_eq!(
                log_purge::file_identity(&hook_current).expect("read rewritten identity"),
                identity
            );
        }));

        second.process().await.expect("restart after tail change");
        let progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(position(&progress), 5);
        assert_eq!(progress["lines_parsed"], 5);
    }

    #[tokio::test]
    async fn zero_offset_rotation_restarts_with_the_refreshed_series() {
        let directory = tempfile::tempdir().expect("create empty rotation fixture");
        let resume_path = directory.path().join("resume.json");
        let current = directory.path().join("fallback-access.log");
        std::fs::write(&current, b"").expect("write empty current fixture");
        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process empty series");

        std::fs::write(&current, b"lost\n").expect("grow current fixture");
        let rotated = directory.path().join("fallback-access.log.1");
        let hook_current = current.clone();
        let hook_rotated = rotated.clone();
        let mut second = resume_processor(directory.path(), "second.json", &resume_path, 0);
        second.before_resume_open = Some(Box::new(move || {
            std::fs::rename(&hook_current, &hook_rotated).expect("rotate current fixture");
            std::fs::write(&hook_current, b"new\n").expect("write replacement fixture");
        }));

        second
            .process()
            .await
            .expect("restart after zero-offset rotation");
        let progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(position(&progress), 2);
        assert_eq!(progress["lines_parsed"], 2);
    }

    #[tokio::test]
    async fn rotated_unterminated_tail_keeps_complete_records_and_sidecar() {
        let directory = tempfile::tempdir().expect("create unterminated fixture");
        let resume_path = directory.path().join("resume.json");
        std::fs::write(
            directory.path().join("access.log.1"),
            b"a1\na2\na3-cut-mid-line",
        )
        .expect("write incomplete rotation");
        std::fs::write(directory.path().join("access.log"), b"b1\nb2\n")
            .expect("write current fixture");
        let positions = HashMap::from([("access.log".to_string(), 0)]);
        let mut first = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("first.json"),
            positions,
        );
        first.resume_path = Some(resume_path.clone());
        first
            .process()
            .await
            .expect("continue after incomplete rotation");
        let first_progress = read_progress(&directory.path().join("first.json"));
        assert_eq!(first_progress["terminal_status"], "partial");
        assert_eq!(first_progress["source_positions"]["access.log"], 4);
        assert_eq!(first_progress["unparsed_lines"], 4);

        std::fs::OpenOptions::new()
            .append(true)
            .open(directory.path().join("access.log"))
            .expect("open current fixture")
            .write_all(b"b3\n")
            .expect("append current record");
        let positions = HashMap::from([("access.log".to_string(), 4)]);
        let mut second = test_processor(
            directory.path().to_path_buf(),
            directory.path().join("second.json"),
            positions,
        );
        second.resume_path = Some(resume_path);
        second
            .process()
            .await
            .expect("resume after incomplete rotation");
        let second_progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(
            second_progress["terminal_status"],
            "completed_with_warnings"
        );
        assert_eq!(second_progress["source_positions"]["access.log"], 5);
        assert_eq!(second_progress["unparsed_lines"], 1);
    }

    #[tokio::test]
    async fn logrotate_recompression_reads_only_new_records() {
        let directory = tempfile::tempdir().expect("create logrotate fixture");
        let resume_path = directory.path().join("resume.json");
        let third = directory.path().join("fallback-access.log.3.gz");
        let second = directory.path().join("fallback-access.log.2.gz");
        let first_rotation = directory.path().join("fallback-access.log.1");
        let current = directory.path().join("fallback-access.log");
        write_gzip(&third, b"1\n2\n3\n4\n5\n", Compression::default());
        write_gzip(&second, b"6\n7\n8\n", Compression::default());
        std::fs::write(&first_rotation, b"9\n10\n11\n").expect("write plain rotation");
        std::fs::write(&current, b"12\n13\n").expect("write current fixture");
        resume_processor(directory.path(), "first.json", &resume_path, 0)
            .process()
            .await
            .expect("process initial logrotate series");

        std::fs::OpenOptions::new()
            .append(true)
            .open(&current)
            .expect("open current fixture")
            .write_all(b"14\n")
            .expect("append current record");
        std::fs::remove_file(&third).expect("remove oldest rotation");
        std::fs::rename(&second, &third).expect("shift compressed rotation");
        let plain_second = directory.path().join("fallback-access.log.2");
        std::fs::rename(&first_rotation, &plain_second).expect("shift plain rotation");
        let plain_contents = std::fs::read(&plain_second).expect("read plain rotation");
        write_gzip(&second, &plain_contents, Compression::default());
        std::fs::remove_file(&plain_second).expect("remove recompressed source");
        std::fs::rename(&current, &first_rotation).expect("rotate current fixture");
        std::fs::write(&current, b"15\n16\n").expect("write new current fixture");

        let mut resumed = resume_processor(directory.path(), "second.json", &resume_path, 13);
        resumed
            .process()
            .await
            .expect("process recompressed series");
        let progress = read_progress(&directory.path().join("second.json"));
        assert_eq!(position(&progress), 11);
        assert_eq!(progress["unparsed_lines"], 0);
        assert_eq!(progress["lines_parsed"], 11);
        assert_eq!(resumed.skipped_fallback_lines, 3);
    }

    #[tokio::test]
    async fn discovery_failure_writes_failed_terminal() {
        let tmp = tempfile::tempdir().expect("create fixture directory");
        let not_a_directory = tmp.path().join("access.log");
        std::fs::write(&not_a_directory, b"line\n").expect("write file path fixture");
        let progress_path = tmp.path().join("progress.json");
        let mut processor = test_processor(not_a_directory, progress_path.clone(), HashMap::new());

        let error = processor
            .process()
            .await
            .expect_err("file path discovery must fail");

        assert!(format!("{error:#}").contains("Failed to discover log sources"));
        let progress = read_progress(&progress_path);
        assert_eq!(progress["status"], "failed");
        assert_eq!(progress["terminal_status"], "failed");
    }

    #[tokio::test]
    async fn starting_progress_failure_is_best_effort() {
        let tmp = tempfile::tempdir().expect("create fixture directory");
        let processor = test_processor(
            tmp.path().to_path_buf(),
            tmp.path().to_path_buf(),
            HashMap::new(),
        );

        processor.write_starting_progress_best_effort("starting fixture");

        assert!(tmp.path().is_dir());
    }

    #[tokio::test]
    async fn pool_creation_failure_writes_failed_terminal() {
        let tmp = tempfile::tempdir().expect("create fixture directory");
        let progress_path = tmp.path().join("progress.json");

        let error = create_pool_or_write_terminal(&progress_path, "pool-run", || async {
            Err::<PgPool, _>(anyhow::anyhow!("pool unavailable"))
        })
        .await
        .expect_err("pool creation must fail");

        assert!(format!("{error:#}").contains("pool unavailable"));
        let progress = read_progress(&progress_path);
        assert_eq!(progress["run_id"], "pool-run");
        assert_eq!(progress["terminal_status"], "failed");
        assert!(progress["message"]
            .as_str()
            .unwrap()
            .contains("Failed to create database pool"));
    }

    #[tokio::test]
    async fn generic_processor_error_writes_failed_terminal() {
        let tmp = tempfile::tempdir().expect("create fixture directory");
        let progress_path = tmp.path().join("progress.json");
        let processor = test_processor(
            tmp.path().to_path_buf(),
            progress_path.clone(),
            HashMap::new(),
        );

        write_processor_failure_terminal(&processor, &anyhow::anyhow!("generic failure"))
            .expect("write failed terminal");

        let progress = read_progress(&progress_path);
        assert_eq!(progress["terminal_status"], "failed");
        assert!(progress["message"]
            .as_str()
            .unwrap()
            .contains("generic failure"));
    }

    #[tokio::test]
    async fn single_file_cancellation_has_typed_outcome() {
        let tmp = tempfile::tempdir().expect("create fixture directory");
        let log_path = tmp.path().join("fallback-access.log");
        std::fs::write(&log_path, b"one\n").expect("write log fixture");
        let progress_path = tmp.path().join("progress.json");
        let mut processor = test_processor(tmp.path().to_path_buf(), progress_path, HashMap::new());
        let log_file = LogFile::from_path(log_path);
        let mut lines_to_skip = 0;
        let mut records_consumed = 0;
        let mut resume = FileResume::default();

        let outcome = processor
            .process_single_file_with_cancel(
                &log_file,
                4,
                &mut lines_to_skip,
                &SourceKind::Fallback,
                &mut records_consumed,
                &mut resume,
                || true,
            )
            .await
            .expect("return cancellation outcome");

        assert_eq!(outcome, FileProcessingOutcome::Cancelled);
        assert_eq!(records_consumed, 0);
    }

    #[test]
    fn positions_file_validation() {
        let tmp = tempfile::tempdir().unwrap();
        let path = tmp.path().join("positions.json");
        let path_str = path.to_str().unwrap();

        // Missing file
        assert!(load_positions(path_str).is_err());
        // Empty file
        std::fs::write(&path, "").unwrap();
        assert!(load_positions(path_str).is_err());
        // Malformed JSON
        std::fs::write(&path, "{not json").unwrap();
        assert!(load_positions(path_str).is_err());
        // Wrong schema version
        std::fs::write(&path, r#"{"schema_version": 99, "sources": {}}"#).unwrap();
        assert!(load_positions(path_str).is_err());
        // Valid
        std::fs::write(
            &path,
            r#"{"schema_version": 1, "sources": {"access.log": 42, "steam-access.log": 7}}"#,
        )
        .unwrap();
        let map = load_positions(path_str).unwrap();
        assert_eq!(map.get("access.log"), Some(&42));
        assert_eq!(map.get("steam-access.log"), Some(&7));
    }
}

#[cfg(test)]
mod session_continuity_tests {
    use super::*;
    use flate2::Compression;
    use sqlx::postgres::{PgConnectOptions, PgPoolOptions};
    use std::str::FromStr;

    const DATABASE_ENV: &str = "LANCACHE_TEST_DATABASE_URL";

    async fn isolated_pool(test_name: &str) -> Option<(PgPool, String, PgConnectOptions)> {
        let Ok(url) = std::env::var(DATABASE_ENV) else {
            println!("SKIP (LANCACHE_TEST_DATABASE_URL unset): {test_name}");
            return None;
        };
        let schema = format!("lp_test_{}", uuid::Uuid::new_v4().simple());
        let options = PgConnectOptions::from_str(&url).expect("parse isolated database URL");
        let admin = PgPoolOptions::new()
            .max_connections(1)
            .connect_with(options.clone())
            .await
            .expect("connect to isolated database");
        sqlx::query(&format!(r#"CREATE SCHEMA "{schema}""#))
            .execute(&admin)
            .await
            .expect("create isolated schema");
        admin.close().await;
        let scoped = options.clone().options([("search_path", schema.as_str())]);
        let pool = PgPoolOptions::new()
            .max_connections(5)
            .connect_with(scoped)
            .await
            .expect("connect to isolated schema");
        super::classification_tests::create_test_schema(&pool).await;
        Some((pool, schema, options))
    }

    async fn drop_schema(pool: PgPool, schema: &str, options: PgConnectOptions) {
        pool.close().await;
        let admin = PgPoolOptions::new()
            .max_connections(1)
            .connect_with(options)
            .await
            .expect("reconnect for schema cleanup");
        sqlx::query(&format!(r#"DROP SCHEMA "{schema}" CASCADE"#))
            .execute(&admin)
            .await
            .expect("drop isolated schema");
        admin.close().await;
    }

    fn log_line(
        service: &str,
        client: &str,
        time: &str,
        url: &str,
        bytes: i64,
        range: &str,
    ) -> String {
        format!(
            "[{service}] {client} / - - - [{time} +0000] \"GET {url} HTTP/1.1\" 206 {bytes} \"-\" \"Fixture/1.0\" \"HIT\" \"cdn.example.test\" \"{range}\"\n"
        )
    }

    async fn run(
        pool: &PgPool,
        directory: &Path,
        position: u64,
        run_id: &str,
        datasource: &str,
    ) -> serde_json::Value {
        run_counted(pool, directory, position, run_id, datasource)
            .await
            .0
    }

    async fn run_counted(
        pool: &PgPool,
        directory: &Path,
        position: u64,
        run_id: &str,
        datasource: &str,
    ) -> (serde_json::Value, u64) {
        let positions = HashMap::from([("access.log".to_string(), position)]);
        let progress_path = directory.join(format!("{run_id}.json"));
        let mut processor = Processor::new(
            pool.clone(),
            directory.to_path_buf(),
            progress_path.clone(),
            false,
            datasource.to_string(),
            positions,
            run_id.to_string(),
        );
        processor.resume_path = Some(directory.join(format!("{datasource}-resume.json")));
        assert_eq!(
            processor.process().await.expect("process database fixture"),
            ProcessingOutcome::Completed
        );
        let contents = std::fs::read_to_string(progress_path).expect("read database progress");
        (
            serde_json::from_str(&contents).expect("parse database progress"),
            processor.parsed_records,
        )
    }

    fn append(path: &Path, contents: &str) {
        use std::io::Write;
        std::fs::OpenOptions::new()
            .append(true)
            .open(path)
            .expect("open database log fixture")
            .write_all(contents.as_bytes())
            .expect("append database log fixture");
    }

    async fn stored_rows(pool: &PgPool, datasource: &str) -> Vec<(String, i64)> {
        sqlx::query_as::<_, (String, i64)>(
            r#"SELECT "Url", "BytesServed" FROM "LogEntries" WHERE "Datasource" = $1 ORDER BY "Url""#,
        )
        .bind(datasource)
        .fetch_all(pool)
        .await
        .expect("read stored log rows")
    }

    async fn stored_totals(pool: &PgPool, datasource: &str) -> (i64, i64, i64) {
        let row = sqlx::query(
            r#"SELECT
                   COUNT(*)::bigint AS "Rows",
                   COALESCE(SUM(CASE WHEN "CacheStatus" = 'HIT' THEN "BytesServed" ELSE 0 END), 0)::bigint AS "Hit",
                   COALESCE(SUM(CASE WHEN "CacheStatus" = 'MISS' THEN "BytesServed" ELSE 0 END), 0)::bigint AS "Miss"
               FROM "LogEntries" WHERE "Datasource" = $1"#,
        )
        .bind(datasource)
        .fetch_one(pool)
        .await
        .expect("read stored log totals");
        (row.get("Rows"), row.get("Hit"), row.get("Miss"))
    }

    async fn verify_resume_boundaries(pool: &PgPool) {
        let remount = tempfile::tempdir().expect("create changed-identity fixture");
        let remount_oldest = remount.path().join("access.log.2");
        let remount_older = remount.path().join("access.log.1");
        let remount_current = remount.path().join("access.log");
        std::fs::write(
            &remount_oldest,
            log_line(
                "steam",
                "10.0.6.1",
                "07/Jan/2026:12:00:00",
                "/resume/a2/01-oldest",
                10,
                "a2-1",
            ),
        )
        .expect("write changed-identity oldest member");
        std::fs::write(
            &remount_older,
            log_line(
                "steam",
                "10.0.6.1",
                "07/Jan/2026:12:00:01",
                "/resume/a2/02-older",
                20,
                "a2-2",
            ),
        )
        .expect("write changed-identity older member");
        std::fs::write(
            &remount_current,
            log_line(
                "steam",
                "10.0.6.1",
                "07/Jan/2026:12:00:02",
                "/resume/a2/03-current",
                30,
                "a2-3",
            ),
        )
        .expect("write changed-identity current member");
        let (_, first_parsed) = run_counted(pool, remount.path(), 0, "a2-first", "resume-a2").await;
        assert_eq!(first_parsed, 3);

        let remount_resume = remount.path().join("resume-a2-resume.json");
        let mut remount_file = log_resume::load(&remount_resume);
        let remount_entry = remount_file
            .stems
            .get_mut("access.log")
            .expect("read changed-identity resume entry");
        for (index, mark) in remount_entry.older_files.iter_mut().enumerate() {
            mark.identity = log_purge::FileIdentity {
                first: u64::MAX,
                second: index as u64 + 1,
            };
        }
        remount_entry.file_identity = log_purge::FileIdentity {
            first: u64::MAX,
            second: 99,
        };
        let old_deleted: u64 = remount_entry
            .older_files
            .iter()
            .map(|mark| mark.records)
            .sum();
        assert_eq!(old_deleted, 2);
        log_resume::save(&remount_resume, &remount_file)
            .expect("save changed identities in resume entry");
        append(
            &remount_current,
            &log_line(
                "steam",
                "10.0.6.1",
                "07/Jan/2026:12:00:03",
                "/resume/a2/04-new",
                40,
                "a2-4",
            ),
        );
        let remount_paths = vec![
            remount_oldest.clone(),
            remount_older.clone(),
            remount_current.clone(),
        ];
        let remount_saved = log_resume::load(&remount_resume);
        let remount_entry = remount_saved
            .stems
            .get("access.log")
            .expect("reload changed-identity resume entry");
        assert!(log_resume::resume_point(remount_entry, 3, &remount_paths).is_none());
        assert_eq!(
            log_resume::deleted_prefix_records(remount_entry, &remount_paths),
            0
        );

        let (remount_second, remount_parsed) =
            run_counted(pool, remount.path(), 3, "a2-second", "resume-a2").await;
        assert_eq!(remount_second["source_positions"]["access.log"], 4);
        assert_eq!(remount_parsed, 1);
        assert_eq!(
            stored_rows(pool, "resume-a2").await,
            vec![
                ("/resume/a2/01-oldest".to_string(), 10),
                ("/resume/a2/02-older".to_string(), 20),
                ("/resume/a2/03-current".to_string(), 30),
                ("/resume/a2/04-new".to_string(), 40),
            ]
        );
        assert_eq!(stored_totals(pool, "resume-a2").await, (4, 100, 0));
        let (remount_third, remount_third_parsed) =
            run_counted(pool, remount.path(), 4, "a2-third", "resume-a2").await;
        assert_eq!(remount_third["source_positions"]["access.log"], 4);
        assert_eq!(remount_third_parsed, 0);
        assert_eq!(stored_totals(pool, "resume-a2").await, (4, 100, 0));

        let remount_control = tempfile::tempdir().expect("create changed-identity comparison");
        std::fs::copy(&remount_oldest, remount_control.path().join("access.log.2"))
            .expect("copy changed-identity oldest member");
        std::fs::copy(&remount_older, remount_control.path().join("access.log.1"))
            .expect("copy changed-identity older member");
        std::fs::copy(&remount_current, remount_control.path().join("access.log"))
            .expect("copy changed-identity current member");
        let (_, remount_control_parsed) = run_counted(
            pool,
            remount_control.path(),
            3 - old_deleted,
            "a2-comparison",
            "resume-a2-comparison",
        )
        .await;
        assert_eq!(remount_control_parsed, 3);
        assert_eq!(
            stored_rows(pool, "resume-a2-comparison").await,
            vec![
                ("/resume/a2/02-older".to_string(), 20),
                ("/resume/a2/03-current".to_string(), 30),
                ("/resume/a2/04-new".to_string(), 40),
            ]
        );

        let deleted = tempfile::tempdir().expect("create deleted-oldest database fixture");
        let deleted_oldest = deleted.path().join("access.log.2");
        let deleted_older = deleted.path().join("access.log.1");
        let deleted_current = deleted.path().join("access.log");
        std::fs::write(
            &deleted_oldest,
            log_line(
                "steam",
                "10.0.6.2",
                "07/Jan/2026:13:00:00",
                "/resume/a3/01-oldest",
                10,
                "a3-1",
            ),
        )
        .expect("write deleted-oldest member");
        std::fs::write(
            &deleted_older,
            log_line(
                "steam",
                "10.0.6.2",
                "07/Jan/2026:13:00:01",
                "/resume/a3/02-older",
                20,
                "a3-2",
            ),
        )
        .expect("write surviving older member");
        std::fs::write(
            &deleted_current,
            log_line(
                "steam",
                "10.0.6.2",
                "07/Jan/2026:13:00:02",
                "/resume/a3/03-current",
                30,
                "a3-3",
            ),
        )
        .expect("write deleted-oldest current member");
        run(pool, deleted.path(), 0, "a3-first", "resume-a3").await;
        std::fs::remove_file(&deleted_oldest).expect("delete oldest database member");
        append(
            &deleted_current,
            &log_line(
                "steam",
                "10.0.6.2",
                "07/Jan/2026:13:00:03",
                "/resume/a3/04-new",
                40,
                "a3-4",
            ),
        );
        let (deleted_second, deleted_parsed) =
            run_counted(pool, deleted.path(), 3, "a3-second", "resume-a3").await;
        assert_eq!(deleted_second["source_positions"]["access.log"], 3);
        assert_eq!(deleted_parsed, 1);
        assert_eq!(
            stored_rows(pool, "resume-a3").await,
            vec![
                ("/resume/a3/01-oldest".to_string(), 10),
                ("/resume/a3/02-older".to_string(), 20),
                ("/resume/a3/03-current".to_string(), 30),
                ("/resume/a3/04-new".to_string(), 40),
            ]
        );
        assert_eq!(stored_totals(pool, "resume-a3").await, (4, 100, 0));

        let collision = tempfile::tempdir().expect("create identity-collision fixture");
        let collision_oldest = collision.path().join("access.log.2.gz");
        let collision_older = collision.path().join("access.log.1");
        let collision_current = collision.path().join("access.log");
        let collision_oldest_line = log_line(
            "steam",
            "10.0.6.3",
            "07/Jan/2026:14:00:00",
            "/resume/a5/01-oldest",
            10,
            "a5-1",
        );
        super::classification_tests::write_gzip(
            &collision_oldest,
            collision_oldest_line.as_bytes(),
            Compression::default(),
        );
        std::fs::write(
            &collision_older,
            [
                log_line(
                    "steam",
                    "10.0.6.3",
                    "07/Jan/2026:14:00:01",
                    "/resume/a5/02-older-a",
                    20,
                    "a5-2",
                ),
                log_line(
                    "steam",
                    "10.0.6.3",
                    "07/Jan/2026:14:00:02",
                    "/resume/a5/03-older-b",
                    30,
                    "a5-3",
                ),
            ]
            .concat(),
        )
        .expect("write identity-collision older member");
        std::fs::write(
            &collision_current,
            log_line(
                "steam",
                "10.0.6.3",
                "07/Jan/2026:14:00:03",
                "/resume/a5/04-current",
                40,
                "a5-4",
            ),
        )
        .expect("write identity-collision current member");
        run(pool, collision.path(), 0, "a5-first", "resume-a5").await;

        std::fs::remove_file(&collision_oldest).expect("remove identity-collision oldest member");
        let collision_source = collision.path().join("access.log.2");
        std::fs::rename(&collision_older, &collision_source)
            .expect("move member before recompression");
        let collision_contents =
            std::fs::read(&collision_source).expect("read member for recompression");
        super::classification_tests::write_gzip(
            &collision_oldest,
            &collision_contents,
            Compression::none(),
        );
        std::fs::remove_file(&collision_source).expect("remove plain recompression source");
        std::fs::rename(&collision_current, &collision_older)
            .expect("rotate identity-collision current member");
        std::fs::write(
            &collision_current,
            [
                log_line(
                    "steam",
                    "10.0.6.3",
                    "07/Jan/2026:14:00:04",
                    "/resume/a5/05-new-a",
                    50,
                    "a5-5",
                ),
                log_line(
                    "steam",
                    "10.0.6.3",
                    "07/Jan/2026:14:00:05",
                    "/resume/a5/06-new-b",
                    60,
                    "a5-6",
                ),
            ]
            .concat(),
        )
        .expect("write identity-collision new current member");

        let collision_resume = collision.path().join("resume-a5-resume.json");
        let mut collision_file = log_resume::load(&collision_resume);
        let collision_entry = collision_file
            .stems
            .get_mut("access.log")
            .expect("read identity-collision resume entry");
        let collision_identity =
            log_purge::file_identity(&collision_oldest).expect("read recompressed member identity");
        let collision_len = std::fs::metadata(&collision_oldest)
            .expect("read recompressed member length")
            .len();
        assert_ne!(collision_entry.older_files[0].len, collision_len);
        collision_entry.older_files[0].identity = collision_identity.clone();
        assert_eq!(
            log_resume::mark_records(collision_entry, &collision_identity, collision_len, false,),
            None
        );
        log_resume::save(&collision_resume, &collision_file)
            .expect("save identity-collision resume entry");
        let collision_paths = vec![
            collision_oldest.clone(),
            collision_older.clone(),
            collision_current.clone(),
        ];
        let collision_saved = log_resume::load(&collision_resume);
        let collision_entry = collision_saved
            .stems
            .get("access.log")
            .expect("reload identity-collision resume entry");
        assert!(log_resume::resume_point(collision_entry, 4, &collision_paths).is_none());
        assert_eq!(
            log_resume::deleted_prefix_records(collision_entry, &collision_paths),
            3
        );

        let (collision_second, collision_parsed) =
            run_counted(pool, collision.path(), 4, "a5-second", "resume-a5").await;
        assert_eq!(collision_second["source_positions"]["access.log"], 5);
        assert_eq!(collision_parsed, 4);
        assert_eq!(
            stored_rows(pool, "resume-a5").await,
            vec![
                ("/resume/a5/01-oldest".to_string(), 10),
                ("/resume/a5/02-older-a".to_string(), 20),
                ("/resume/a5/03-older-b".to_string(), 30),
                ("/resume/a5/04-current".to_string(), 40),
                ("/resume/a5/05-new-a".to_string(), 50),
                ("/resume/a5/06-new-b".to_string(), 60),
            ]
        );
        assert_eq!(stored_totals(pool, "resume-a5").await, (6, 210, 0));

        let collision_control = tempfile::tempdir().expect("create identity-only comparison");
        std::fs::copy(
            &collision_oldest,
            collision_control.path().join("access.log.2.gz"),
        )
        .expect("copy recompressed comparison member");
        std::fs::copy(
            &collision_older,
            collision_control.path().join("access.log.1"),
        )
        .expect("copy rotated comparison member");
        std::fs::copy(
            &collision_current,
            collision_control.path().join("access.log"),
        )
        .expect("copy current comparison member");
        let (_, collision_control_parsed) = run_counted(
            pool,
            collision_control.path(),
            4,
            "a5-comparison",
            "resume-a5-comparison",
        )
        .await;
        assert_eq!(collision_control_parsed, 1);
        assert_eq!(
            stored_rows(pool, "resume-a5-comparison").await,
            vec![("/resume/a5/06-new-b".to_string(), 60)]
        );

        let late_tail = tempfile::tempdir().expect("create late-tail fixture");
        let late_current = late_tail.path().join("access.log");
        let late_rotated = late_tail.path().join("access.log.1");
        std::fs::write(
            &late_current,
            [
                log_line(
                    "steam",
                    "10.0.6.4",
                    "07/Jan/2026:15:00:00",
                    "/resume/a6/01-a1",
                    10,
                    "a6-1",
                ),
                log_line(
                    "steam",
                    "10.0.6.4",
                    "07/Jan/2026:15:00:01",
                    "/resume/a6/02-a2",
                    20,
                    "a6-2",
                ),
            ]
            .concat(),
        )
        .expect("write late-tail initial current member");
        run(pool, late_tail.path(), 0, "a6-first", "resume-a6").await;
        std::fs::rename(&late_current, &late_rotated).expect("rotate late-tail member");
        let late_line = log_line(
            "steam",
            "10.0.6.4",
            "07/Jan/2026:15:00:02",
            "/resume/a6/03-a3",
            30,
            "a6-3",
        );
        append(
            &late_rotated,
            late_line
                .strip_suffix('\n')
                .expect("remove late-tail terminator"),
        );
        std::fs::write(
            &late_current,
            [
                log_line(
                    "steam",
                    "10.0.6.4",
                    "07/Jan/2026:15:00:03",
                    "/resume/a6/04-b1",
                    40,
                    "a6-4",
                ),
                log_line(
                    "steam",
                    "10.0.6.4",
                    "07/Jan/2026:15:00:04",
                    "/resume/a6/05-b2",
                    50,
                    "a6-5",
                ),
            ]
            .concat(),
        )
        .expect("write late-tail new current member");
        let (late_partial, late_partial_parsed) =
            run_counted(pool, late_tail.path(), 2, "a6-partial", "resume-a6").await;
        assert_eq!(late_partial["terminal_status"], "partial");
        assert_eq!(late_partial["source_positions"]["access.log"], 4);
        assert_eq!(late_partial_parsed, 2);
        assert_eq!(
            stored_rows(pool, "resume-a6").await,
            vec![
                ("/resume/a6/01-a1".to_string(), 10),
                ("/resume/a6/02-a2".to_string(), 20),
                ("/resume/a6/04-b1".to_string(), 40),
                ("/resume/a6/05-b2".to_string(), 50),
            ]
        );
        let partial_resume = log_resume::load(&late_tail.path().join("resume-a6-resume.json"));
        let partial_entry = partial_resume
            .stems
            .get("access.log")
            .expect("read late-tail resume entry");
        assert_eq!(partial_entry.older_files.len(), 1);
        assert_eq!(partial_entry.older_files[0].records, 2);
        assert_eq!(
            partial_entry.older_files[0].len,
            std::fs::metadata(&late_rotated)
                .expect("read observed late-tail length")
                .len()
        );
        assert!(partial_entry.older_files[0].offset.is_some());
        assert!(partial_entry.older_files[0].tail_crc.is_some());

        append(&late_rotated, "\n");
        append(
            &late_current,
            &log_line(
                "steam",
                "10.0.6.4",
                "07/Jan/2026:15:00:05",
                "/resume/a6/06-b3",
                60,
                "a6-6",
            ),
        );
        let (late_complete, late_complete_parsed) =
            run_counted(pool, late_tail.path(), 4, "a6-complete", "resume-a6").await;
        assert_eq!(late_complete["terminal_status"], "completed");
        assert_eq!(late_complete["source_positions"]["access.log"], 6);
        assert_eq!(late_complete_parsed, 2);
        assert_eq!(
            stored_rows(pool, "resume-a6").await,
            vec![
                ("/resume/a6/01-a1".to_string(), 10),
                ("/resume/a6/02-a2".to_string(), 20),
                ("/resume/a6/03-a3".to_string(), 30),
                ("/resume/a6/04-b1".to_string(), 40),
                ("/resume/a6/05-b2".to_string(), 50),
                ("/resume/a6/06-b3".to_string(), 60),
            ]
        );
        assert_eq!(stored_totals(pool, "resume-a6").await, (6, 210, 0));
        let (late_unchanged, late_unchanged_parsed) =
            run_counted(pool, late_tail.path(), 6, "a6-unchanged", "resume-a6").await;
        assert_eq!(late_unchanged["source_positions"]["access.log"], 6);
        assert_eq!(late_unchanged_parsed, 0);
        assert_eq!(stored_totals(pool, "resume-a6").await, (6, 210, 0));

        let late_complete_member = tempfile::tempdir().expect("create appended-rotation fixture");
        let appended_current = late_complete_member.path().join("access.log");
        let appended_rotated = late_complete_member.path().join("access.log.1");
        std::fs::write(
            &appended_current,
            [
                log_line(
                    "steam",
                    "10.0.6.5",
                    "07/Jan/2026:16:00:00",
                    "/resume/a6-complete/01-c1",
                    11,
                    "a6c-1",
                ),
                log_line(
                    "steam",
                    "10.0.6.5",
                    "07/Jan/2026:16:00:01",
                    "/resume/a6-complete/02-c2",
                    22,
                    "a6c-2",
                ),
            ]
            .concat(),
        )
        .expect("write appended-rotation initial member");
        run(
            pool,
            late_complete_member.path(),
            0,
            "a6c-first",
            "resume-a6-complete",
        )
        .await;
        std::fs::rename(&appended_current, &appended_rotated).expect("rotate complete member");
        std::fs::write(
            &appended_current,
            log_line(
                "steam",
                "10.0.6.5",
                "07/Jan/2026:16:00:03",
                "/resume/a6-complete/04-d1",
                44,
                "a6c-4",
            ),
        )
        .expect("write appended-rotation current member");
        let (_, appended_rotation_parsed) = run_counted(
            pool,
            late_complete_member.path(),
            2,
            "a6c-rotated",
            "resume-a6-complete",
        )
        .await;
        assert_eq!(appended_rotation_parsed, 1);
        append(
            &appended_rotated,
            &log_line(
                "steam",
                "10.0.6.5",
                "07/Jan/2026:16:00:02",
                "/resume/a6-complete/03-c3",
                33,
                "a6c-3",
            ),
        );
        append(
            &appended_current,
            &log_line(
                "steam",
                "10.0.6.5",
                "07/Jan/2026:16:00:04",
                "/resume/a6-complete/05-d2",
                55,
                "a6c-5",
            ),
        );
        let (appended_second, appended_second_parsed) = run_counted(
            pool,
            late_complete_member.path(),
            3,
            "a6c-second",
            "resume-a6-complete",
        )
        .await;
        assert_eq!(appended_second["source_positions"]["access.log"], 5);
        assert_eq!(appended_second_parsed, 2);
        assert_eq!(
            stored_rows(pool, "resume-a6-complete").await,
            vec![
                ("/resume/a6-complete/01-c1".to_string(), 11),
                ("/resume/a6-complete/02-c2".to_string(), 22),
                ("/resume/a6-complete/03-c3".to_string(), 33),
                ("/resume/a6-complete/04-d1".to_string(), 44),
                ("/resume/a6-complete/05-d2".to_string(), 55),
            ]
        );
        assert_eq!(stored_totals(pool, "resume-a6-complete").await, (5, 165, 0));
        let (_, appended_third_parsed) = run_counted(
            pool,
            late_complete_member.path(),
            5,
            "a6c-third",
            "resume-a6-complete",
        )
        .await;
        assert_eq!(appended_third_parsed, 0);

        let permanent = tempfile::tempdir().expect("create permanent-tail fixture");
        let permanent_current = permanent.path().join("access.log");
        let permanent_rotated = permanent.path().join("access.log.1");
        std::fs::write(
            &permanent_current,
            [
                log_line(
                    "steam",
                    "10.0.6.6",
                    "07/Jan/2026:17:00:00",
                    "/resume/a6-permanent/01-e1",
                    12,
                    "a6p-1",
                ),
                log_line(
                    "steam",
                    "10.0.6.6",
                    "07/Jan/2026:17:00:01",
                    "/resume/a6-permanent/02-e2",
                    24,
                    "a6p-2",
                ),
            ]
            .concat(),
        )
        .expect("write permanent-tail initial member");
        run(
            pool,
            permanent.path(),
            0,
            "a6p-first",
            "resume-a6-permanent",
        )
        .await;
        std::fs::rename(&permanent_current, &permanent_rotated)
            .expect("rotate permanent-tail member");
        let permanent_line = log_line(
            "steam",
            "10.0.6.6",
            "07/Jan/2026:17:00:02",
            "/resume/a6-permanent/03-never-complete",
            30,
            "a6p-3",
        );
        append(
            &permanent_rotated,
            permanent_line
                .strip_suffix('\n')
                .expect("remove permanent-tail terminator"),
        );
        std::fs::write(
            &permanent_current,
            log_line(
                "steam",
                "10.0.6.6",
                "07/Jan/2026:17:00:03",
                "/resume/a6-permanent/04-f1",
                36,
                "a6p-4",
            ),
        )
        .expect("write permanent-tail current member");
        let (permanent_partial, permanent_partial_parsed) = run_counted(
            pool,
            permanent.path(),
            2,
            "a6p-partial",
            "resume-a6-permanent",
        )
        .await;
        assert_eq!(permanent_partial["terminal_status"], "partial");
        assert_eq!(permanent_partial["source_positions"]["access.log"], 3);
        assert_eq!(permanent_partial_parsed, 1);
        append(
            &permanent_current,
            &log_line(
                "steam",
                "10.0.6.6",
                "07/Jan/2026:17:00:04",
                "/resume/a6-permanent/05-f2",
                48,
                "a6p-5",
            ),
        );
        let (permanent_second, permanent_second_parsed) = run_counted(
            pool,
            permanent.path(),
            3,
            "a6p-second",
            "resume-a6-permanent",
        )
        .await;
        assert_eq!(permanent_second["terminal_status"], "completed");
        assert_eq!(permanent_second["source_positions"]["access.log"], 4);
        assert_eq!(permanent_second_parsed, 1);
        assert_eq!(
            stored_rows(pool, "resume-a6-permanent").await,
            vec![
                ("/resume/a6-permanent/01-e1".to_string(), 12),
                ("/resume/a6-permanent/02-e2".to_string(), 24),
                ("/resume/a6-permanent/04-f1".to_string(), 36),
                ("/resume/a6-permanent/05-f2".to_string(), 48),
            ]
        );
        assert_eq!(
            stored_totals(pool, "resume-a6-permanent").await,
            (4, 120, 0)
        );
        let (_, permanent_third_parsed) = run_counted(
            pool,
            permanent.path(),
            4,
            "a6p-third",
            "resume-a6-permanent",
        )
        .await;
        assert_eq!(permanent_third_parsed, 0);

        let unreadable = tempfile::tempdir().expect("create unreadable-rotation fixture");
        let unreadable_rotated = unreadable.path().join("access.log.1");
        let unreadable_current = unreadable.path().join("access.log");
        std::fs::write(
            &unreadable_rotated,
            log_line(
                "steam",
                "10.0.6.7",
                "07/Jan/2026:18:00:00",
                "/resume/unreadable/00-old",
                5,
                "unreadable-old",
            ),
        )
        .expect("write unreadable rotated member");
        std::fs::write(
            &unreadable_current,
            [
                log_line(
                    "steam",
                    "10.0.6.7",
                    "07/Jan/2026:18:00:01",
                    "/resume/unreadable/01-current",
                    70,
                    "unreadable-1",
                ),
                log_line(
                    "steam",
                    "10.0.6.7",
                    "07/Jan/2026:18:00:02",
                    "/resume/unreadable/02-current",
                    80,
                    "unreadable-2",
                ),
            ]
            .concat(),
        )
        .expect("write unreadable current member");
        let unreadable_resume = unreadable.path().join("resume-unreadable-resume.json");
        let unreadable_first_path = unreadable.path().join("unreadable-first.json");
        let mut unreadable_first = Processor::new(
            pool.clone(),
            unreadable.path().to_path_buf(),
            unreadable_first_path.clone(),
            false,
            "resume-unreadable".to_string(),
            HashMap::from([("access.log".to_string(), 0)]),
            "unreadable-first".to_string(),
        );
        unreadable_first.resume_path = Some(unreadable_resume.clone());
        unreadable_first.open_failure = Some(unreadable_rotated.clone());
        assert_eq!(
            unreadable_first
                .process()
                .await
                .expect("continue after injected rotated open failure"),
            ProcessingOutcome::Completed
        );
        assert_eq!(
            unreadable_first
                .open_attempts
                .get(&unreadable_rotated)
                .copied()
                .unwrap_or_default(),
            1
        );
        assert_eq!(unreadable_first.parsed_records, 2);
        let unreadable_first_progress: serde_json::Value = serde_json::from_str(
            &std::fs::read_to_string(unreadable_first_path)
                .expect("read unreadable first progress"),
        )
        .expect("parse unreadable first progress");
        assert_eq!(unreadable_first_progress["terminal_status"], "partial");
        assert_eq!(
            unreadable_first_progress["source_positions"]["access.log"],
            2
        );
        let unreadable_file = log_resume::load(&unreadable_resume);
        let unreadable_entry = unreadable_file
            .stems
            .get("access.log")
            .expect("read unreadable resume entry");
        assert_eq!(unreadable_entry.older_files.len(), 1);
        assert_eq!(unreadable_entry.older_files[0].records, 0);
        assert_eq!(unreadable_entry.older_files[0].offset, None);
        assert_eq!(unreadable_entry.older_files[0].tail_crc, None);
        assert_eq!(
            unreadable_entry.older_files[0].identity,
            log_purge::file_identity(&unreadable_rotated)
                .expect("read unreadable rotated identity")
        );
        assert_eq!(
            unreadable_entry.older_files[0].len,
            std::fs::metadata(&unreadable_rotated)
                .expect("read unreadable rotated length")
                .len()
        );

        append(
            &unreadable_current,
            &log_line(
                "steam",
                "10.0.6.7",
                "07/Jan/2026:18:00:03",
                "/resume/unreadable/03-new",
                90,
                "unreadable-3",
            ),
        );
        let unreadable_second_path = unreadable.path().join("unreadable-second.json");
        let mut unreadable_second = Processor::new(
            pool.clone(),
            unreadable.path().to_path_buf(),
            unreadable_second_path.clone(),
            false,
            "resume-unreadable".to_string(),
            HashMap::from([("access.log".to_string(), 2)]),
            "unreadable-second".to_string(),
        );
        unreadable_second.resume_path = Some(unreadable_resume.clone());
        unreadable_second.open_failure = Some(unreadable_rotated.clone());
        unreadable_second
            .process()
            .await
            .expect("resume after unreadable rotated member");
        assert_eq!(
            unreadable_second
                .open_attempts
                .get(&unreadable_rotated)
                .copied()
                .unwrap_or_default(),
            0
        );
        assert_eq!(unreadable_second.parsed_records, 1);
        let unreadable_second_progress: serde_json::Value = serde_json::from_str(
            &std::fs::read_to_string(unreadable_second_path)
                .expect("read unreadable second progress"),
        )
        .expect("parse unreadable second progress");
        assert_eq!(unreadable_second_progress["terminal_status"], "completed");
        assert_eq!(
            unreadable_second_progress["source_positions"]["access.log"],
            3
        );
        assert_eq!(
            stored_rows(pool, "resume-unreadable").await,
            vec![
                ("/resume/unreadable/01-current".to_string(), 70),
                ("/resume/unreadable/02-current".to_string(), 80),
                ("/resume/unreadable/03-new".to_string(), 90),
            ]
        );
        assert_eq!(stored_totals(pool, "resume-unreadable").await, (3, 240, 0));

        let unreadable_third_path = unreadable.path().join("unreadable-third.json");
        let mut unreadable_third = Processor::new(
            pool.clone(),
            unreadable.path().to_path_buf(),
            unreadable_third_path,
            false,
            "resume-unreadable".to_string(),
            HashMap::from([("access.log".to_string(), 3)]),
            "unreadable-third".to_string(),
        );
        unreadable_third.resume_path = Some(unreadable_resume);
        unreadable_third.open_failure = Some(unreadable_rotated.clone());
        unreadable_third
            .process()
            .await
            .expect("repeat unchanged unreadable resume");
        assert_eq!(
            unreadable_third
                .open_attempts
                .get(&unreadable_rotated)
                .copied()
                .unwrap_or_default(),
            0
        );
        assert_eq!(unreadable_third.parsed_records, 0);
        assert_eq!(stored_totals(pool, "resume-unreadable").await, (3, 240, 0));
    }

    #[cfg(unix)]
    async fn verify_unix_open_denial(pool: &PgPool) {
        let Some(attempt_path) = std::env::var_os("LANCACHE_OPEN_ATTEMPT_LOG") else {
            return;
        };
        let legacy = std::env::var_os("LANCACHE_EXPECT_LEGACY_IDENTITY").is_some();
        std::fs::write(&attempt_path, b"").expect("reset denied-open attempt log");

        let directory = tempfile::tempdir().expect("create Unix open-denial fixture");
        let rotated = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        std::fs::write(
            &rotated,
            log_line(
                "steam",
                "10.0.6.8",
                "07/Jan/2026:19:00:00",
                "/resume/unix-open/00-old",
                5,
                "unix-open-old",
            ),
        )
        .expect("write Unix denied member");
        std::fs::write(
            &current,
            [
                log_line(
                    "steam",
                    "10.0.6.8",
                    "07/Jan/2026:19:00:01",
                    "/resume/unix-open/01-current",
                    70,
                    "unix-open-1",
                ),
                log_line(
                    "steam",
                    "10.0.6.8",
                    "07/Jan/2026:19:00:02",
                    "/resume/unix-open/02-current",
                    80,
                    "unix-open-2",
                ),
            ]
            .concat(),
        )
        .expect("write Unix open-denial current member");
        let denied = rotated
            .canonicalize()
            .expect("canonicalize Unix denied member");
        std::env::set_var("LANCACHE_DENY_OPEN_PATH", &denied);
        let resume_path = directory.path().join("resume-unix-open-resume.json");

        let mut first = Processor::new(
            pool.clone(),
            directory.path().to_path_buf(),
            directory.path().join("unix-open-first.json"),
            false,
            "resume-unix-open".to_string(),
            HashMap::from([("access.log".to_string(), 0)]),
            "unix-open-first".to_string(),
        );
        first.resume_path = Some(resume_path.clone());
        first
            .process()
            .await
            .expect("continue after Unix denied open");
        let first_attempts = std::fs::read_to_string(&attempt_path)
            .expect("read first Unix open attempts")
            .lines()
            .count();
        let first_marks = log_resume::load(&resume_path)
            .stems
            .get("access.log")
            .expect("read first Unix resume entry")
            .older_files
            .len();

        append(
            &current,
            &log_line(
                "steam",
                "10.0.6.8",
                "07/Jan/2026:19:00:03",
                "/resume/unix-open/03-new",
                90,
                "unix-open-3",
            ),
        );
        let mut second = Processor::new(
            pool.clone(),
            directory.path().to_path_buf(),
            directory.path().join("unix-open-second.json"),
            false,
            "resume-unix-open".to_string(),
            HashMap::from([("access.log".to_string(), 2)]),
            "unix-open-second".to_string(),
        );
        second.resume_path = Some(resume_path.clone());
        second
            .process()
            .await
            .expect("run second Unix open-denial pass");
        let second_attempts = std::fs::read_to_string(&attempt_path)
            .expect("read second Unix open attempts")
            .lines()
            .count();

        let mut third = Processor::new(
            pool.clone(),
            directory.path().to_path_buf(),
            directory.path().join("unix-open-third.json"),
            false,
            "resume-unix-open".to_string(),
            HashMap::from([("access.log".to_string(), 3)]),
            "unix-open-third".to_string(),
        );
        third.resume_path = Some(resume_path);
        third
            .process()
            .await
            .expect("run unchanged Unix open-denial pass");
        let third_attempts = std::fs::read_to_string(&attempt_path)
            .expect("read third Unix open attempts")
            .lines()
            .count();
        std::env::remove_var("LANCACHE_DENY_OPEN_PATH");

        let rows = stored_rows(pool, "resume-unix-open").await;
        let totals = stored_totals(pool, "resume-unix-open").await;
        println!(
            "UNIX_OPEN_DENIAL legacy={legacy} attempts={first_attempts},{second_attempts},{third_attempts} marks={first_marks} parsed={},{},{} rows={} hit={} miss={}",
            first.parsed_records,
            second.parsed_records,
            third.parsed_records,
            totals.0,
            totals.1,
            totals.2
        );

        assert_eq!(
            rows,
            vec![
                ("/resume/unix-open/01-current".to_string(), 70),
                ("/resume/unix-open/02-current".to_string(), 80),
                ("/resume/unix-open/03-new".to_string(), 90),
            ]
        );
        assert_eq!(totals, (3, 240, 0));
        assert_eq!(first.parsed_records, 2);
        if legacy {
            assert_eq!(first_marks, 0);
            assert!(first_attempts >= 2);
            assert!(second_attempts > first_attempts);
            assert!(third_attempts > second_attempts);
            assert_eq!(second.parsed_records, 3);
            assert_eq!(third.parsed_records, 3);
        } else {
            assert_eq!(first_marks, 1);
            assert_eq!(first_attempts, 1);
            assert_eq!(second_attempts, first_attempts);
            assert_eq!(third_attempts, first_attempts);
            assert_eq!(second.parsed_records, 1);
            assert_eq!(third.parsed_records, 0);
        }
    }

    #[tokio::test]
    async fn continuous_sessions_follow_log_time_gaps() {
        let name = "continuous_sessions_follow_log_time_gaps";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };
        let directory = tempfile::tempdir().expect("create database fixture");
        let path = directory.path().join("access.log");
        let first = (0..3)
            .map(|second| {
                log_line(
                    "steam",
                    "10.0.0.1",
                    &format!("01/Jan/2026:12:00:0{second}"),
                    "/depot/101/chunk/a",
                    10,
                    "bytes=0-9",
                )
            })
            .collect::<String>();
        std::fs::write(&path, first).expect("write first database pass");
        run(&pool, directory.path(), 0, "continuous-first", "continuity").await;

        let second = (5..8)
            .map(|second| {
                log_line(
                    "steam",
                    "10.0.0.1",
                    &format!("01/Jan/2026:12:00:0{second}"),
                    "/depot/101/chunk/a",
                    10,
                    &format!("bytes={}-{}", second * 10, second * 10 + 9),
                )
            })
            .collect::<String>();
        append(&path, &second);
        run(
            &pool,
            directory.path(),
            3,
            "continuous-second",
            "continuity",
        )
        .await;

        let row = sqlx::query(
            r#"SELECT COUNT(*)::bigint AS "Rows", COALESCE(SUM("CacheHitBytes"), 0)::bigint AS "Bytes" FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
        )
        .bind("continuity")
        .bind("10.0.0.1")
        .fetch_one(&pool)
        .await
        .expect("read continuous session");
        assert_eq!(row.get::<i64, _>("Rows"), 1);
        assert_eq!(row.get::<i64, _>("Bytes"), 60);
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
            )
            .bind("continuity")
            .bind("10.0.0.1")
            .fetch_one(&pool)
            .await
            .expect("count continuous log entries"),
            6
        );

        let within_batch = [
            log_line(
                "steam",
                "10.0.0.2",
                "01/Jan/2026:13:00:00",
                "/depot/202/chunk/a",
                1,
                "a",
            ),
            log_line(
                "steam",
                "10.0.0.2",
                "01/Jan/2026:13:00:05",
                "/depot/202/chunk/b",
                1,
                "b",
            ),
            log_line(
                "steam",
                "10.0.0.2",
                "01/Jan/2026:13:06:10",
                "/depot/202/chunk/c",
                1,
                "c",
            ),
        ]
        .concat();
        append(&path, &within_batch);
        run(&pool, directory.path(), 6, "within-batch", "continuity").await;
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
            )
            .bind("continuity")
            .bind("10.0.0.2")
            .fetch_one(&pool)
            .await
            .expect("count gap-separated rows"),
            2
        );

        append(
            &path,
            &log_line(
                "steam",
                "10.0.0.1",
                "01/Jan/2026:12:10:00",
                "/depot/101/chunk/later",
                10,
                "later",
            ),
        );
        run(&pool, directory.path(), 9, "ten-minute-gap", "continuity").await;
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
            )
            .bind("continuity")
            .bind("10.0.0.1")
            .fetch_one(&pool)
            .await
            .expect("count separated pass rows"),
            2
        );

        drop_schema(pool, &schema, options).await;
    }

    #[tokio::test]
    async fn identities_eviction_and_replayed_time_preserve_row_boundaries() {
        let name = "identities_eviction_and_replayed_time_preserve_row_boundaries";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };
        let directory = tempfile::tempdir().expect("create database fixture");
        let path = directory.path().join("access.log");
        let first = [
            log_line(
                "steam",
                "10.0.1.1",
                "02/Jan/2026:12:00:00",
                "/depot/301/chunk/a",
                10,
                "a",
            ),
            log_line(
                "steam",
                "10.0.1.1",
                "02/Jan/2026:12:00:01",
                "/depot/302/chunk/a",
                20,
                "a",
            ),
            log_line(
                "steam",
                "10.0.1.2",
                "02/Jan/2026:12:00:00",
                "/depot/303/chunk/a",
                30,
                "a",
            ),
            log_line(
                "steam",
                "10.0.1.3",
                "05/Jan/2026:12:00:00",
                "/depot/304/chunk/today",
                40,
                "a",
            ),
        ]
        .concat();
        std::fs::write(&path, first).expect("write identity fixture");
        run(&pool, directory.path(), 0, "identity-first", "identity").await;

        sqlx::query(
            r#"UPDATE "Downloads" SET "IsActive" = false, "IsEvicted" = true WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
        )
        .bind("identity")
        .bind("10.0.1.2")
        .execute(&pool)
        .await
        .expect("evict fixture row");
        let second = [
            log_line(
                "steam",
                "10.0.1.1",
                "02/Jan/2026:12:00:03",
                "/depot/301/chunk/b",
                10,
                "b",
            ),
            log_line(
                "steam",
                "10.0.1.1",
                "02/Jan/2026:12:00:04",
                "/depot/302/chunk/b",
                20,
                "b",
            ),
            log_line(
                "steam",
                "10.0.1.2",
                "02/Jan/2026:12:02:00",
                "/depot/303/chunk/b",
                30,
                "b",
            ),
            log_line(
                "steam",
                "10.0.1.3",
                "02/Jan/2026:12:00:00",
                "/depot/304/chunk/old",
                50,
                "old",
            ),
        ]
        .concat();
        append(&path, &second);
        run(&pool, directory.path(), 4, "identity-second", "identity").await;

        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
            )
            .bind("identity")
            .bind("10.0.1.1")
            .fetch_one(&pool)
            .await
            .expect("count concurrent depot rows"),
            2
        );
        let evicted = sqlx::query(
            r#"SELECT COUNT(*)::bigint AS "Rows", COUNT(*) FILTER (WHERE "IsEvicted")::bigint AS "Evicted" FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
        )
        .bind("identity")
        .bind("10.0.1.2")
        .fetch_one(&pool)
        .await
        .expect("read evicted row split");
        assert_eq!(evicted.get::<i64, _>("Rows"), 2);
        assert_eq!(evicted.get::<i64, _>("Evicted"), 1);
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
            )
            .bind("identity")
            .bind("10.0.1.3")
            .fetch_one(&pool)
            .await
            .expect("count replay-separated rows"),
            2
        );

        sqlx::query(
            r#"INSERT INTO "Downloads" ("Service", "ClientIp", "StartTimeUtc", "EndTimeUtc", "CacheHitBytes", "CacheMissBytes", "IsActive", "LastUrl", "DepotId", "Datasource") VALUES ('steam', '10.0.1.4', '2026-01-02T10:00:00Z', '2026-01-02T11:00:00Z', 10, 0, false, '/depot/305/chunk/latest', 305, 'identity')"#,
        )
        .execute(&pool)
        .await
        .expect("insert stored session span");
        append(
            &path,
            &[
                log_line(
                    "steam",
                    "10.0.1.4",
                    "02/Jan/2026:10:57:00",
                    "/depot/305/chunk/inside",
                    5,
                    "inside",
                ),
                log_line(
                    "steam",
                    "10.0.1.4",
                    "02/Jan/2026:11:00:00",
                    "/depot/305/chunk/equal",
                    5,
                    "equal",
                ),
            ]
            .concat(),
        );
        run(&pool, directory.path(), 8, "identity-replay", "identity").await;
        let stored = sqlx::query(
            r#"SELECT "StartTimeUtc", "EndTimeUtc", "LastUrl", "IsActive", "CacheHitBytes" FROM "Downloads" WHERE "Datasource" = 'identity' AND "ClientIp" = '10.0.1.4'"#,
        )
        .fetch_one(&pool)
        .await
        .expect("read stored session span");
        assert_eq!(
            stored.get::<chrono::DateTime<Utc>, _>("StartTimeUtc"),
            Utc.with_ymd_and_hms(2026, 1, 2, 10, 0, 0).single().unwrap()
        );
        assert_eq!(
            stored.get::<chrono::DateTime<Utc>, _>("EndTimeUtc"),
            Utc.with_ymd_and_hms(2026, 1, 2, 11, 0, 0).single().unwrap()
        );
        assert_eq!(
            stored.get::<String, _>("LastUrl"),
            "/depot/305/chunk/latest"
        );
        assert!(!stored.get::<bool, _>("IsActive"));
        assert_eq!(stored.get::<i64, _>("CacheHitBytes"), 20);

        drop_schema(pool, &schema, options).await;
    }

    #[tokio::test]
    async fn row_evicted_between_lookup_and_update_takes_no_new_bytes() {
        let name = "row_evicted_between_lookup_and_update_takes_no_new_bytes";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };
        let directory = tempfile::tempdir().expect("create database fixture");
        let path = directory.path().join("access.log");
        std::fs::write(
            &path,
            log_line(
                "steam",
                "10.0.2.1",
                "02/Jan/2026:12:00:00",
                "/depot/401/chunk/a",
                10,
                "a",
            ),
        )
        .expect("write eviction race fixture");
        run(&pool, directory.path(), 0, "race-first", "race").await;
        let evicted_id =
            sqlx::query_scalar::<_, i64>(r#"SELECT "Id" FROM "Downloads" WHERE "Datasource" = $1"#)
                .bind("race")
                .fetch_one(&pool)
                .await
                .expect("read the row to evict");

        // The uncommitted eviction holds the row lock, so the processor selects the row while it
        // is still current and then waits on the lock in its update.
        let mut eviction = pool.begin().await.expect("begin eviction");
        let eviction_pid = sqlx::query_scalar::<_, i32>("SELECT pg_backend_pid()")
            .fetch_one(&mut *eviction)
            .await
            .expect("read eviction backend");
        sqlx::query(
            r#"UPDATE "Downloads" SET "IsActive" = false, "IsEvicted" = true WHERE "Id" = $1"#,
        )
        .bind(evicted_id)
        .execute(&mut *eviction)
        .await
        .expect("evict the row");
        append(
            &path,
            &log_line(
                "steam",
                "10.0.2.1",
                "02/Jan/2026:12:00:05",
                "/depot/401/chunk/b",
                20,
                "b",
            ),
        );
        let release = async {
            let mut waiting = false;
            for _ in 0..1500 {
                waiting = sqlx::query_scalar::<_, bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE $1 = ANY(pg_blocking_pids(pid)))",
                )
                .bind(eviction_pid)
                .fetch_one(&pool)
                .await
                .expect("read lock waiters");
                if waiting {
                    break;
                }
                tokio::time::sleep(std::time::Duration::from_millis(20)).await;
            }
            assert!(waiting, "the processor never waited on the evicted row");
            eviction.commit().await.expect("commit eviction");
        };

        tokio::join!(
            run(&pool, directory.path(), 1, "race-second", "race"),
            release
        );

        let rows = sqlx::query_as::<_, (i64, i64, bool)>(
            r#"SELECT "Id", "CacheHitBytes", "IsEvicted" FROM "Downloads" WHERE "Datasource" = $1 ORDER BY "Id""#,
        )
        .bind("race")
        .fetch_all(&pool)
        .await
        .expect("read rows after the race");
        assert_eq!(rows.len(), 2);
        assert_eq!(rows[0], (evicted_id, 10, true));
        assert_eq!((rows[1].1, rows[1].2), (20, false));
        let new_line_owner = sqlx::query_scalar::<_, i64>(
            r#"SELECT "DownloadId" FROM "LogEntries" WHERE "Datasource" = $1 AND "Url" = $2"#,
        )
        .bind("race")
        .bind("/depot/401/chunk/b")
        .fetch_one(&pool)
        .await
        .expect("read the new line's download");
        assert_eq!(new_line_owner, rows[1].0);

        drop_schema(pool, &schema, options).await;
    }

    #[tokio::test]
    async fn xbox_adoption_and_group_priority_keep_game_and_generic_rows_separate() {
        let name = "xbox_adoption_and_group_priority_keep_game_and_generic_rows_separate";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };
        let directory = tempfile::tempdir().expect("create database fixture");
        let path = directory.path().join("access.log");
        let fragment = "/filestreamingservice/files/12345678-90ab-cdef-1234-567890abcdef";
        let game_url = format!("{fragment}/game.bin");
        let generic_url = "/c/msdownload/update/generic.bin";
        std::fs::write(
            &path,
            [
                log_line(
                    "wsus",
                    "10.0.2.9",
                    "03/Jan/2026:12:00:00",
                    &game_url,
                    10,
                    "game-1",
                ),
                log_line(
                    "wsus",
                    "10.0.2.9",
                    "03/Jan/2026:12:00:01",
                    generic_url,
                    20,
                    "generic-1",
                ),
            ]
            .concat(),
        )
        .expect("write Xbox adoption fixture");
        run(&pool, directory.path(), 0, "xbox-first", "xbox").await;
        sqlx::query(
            r#"INSERT INTO "XboxCdnPatterns" ("UrlFragment", "Title", "ProductId") VALUES ($1, $2, $3)"#,
        )
        .bind(fragment)
        .bind("Game X")
        .bind("GAME-X")
        .execute(&pool)
        .await
        .expect("insert Xbox pattern");
        append(
            &path,
            &[
                log_line(
                    "wsus",
                    "10.0.2.9",
                    "03/Jan/2026:12:01:00",
                    &game_url,
                    30,
                    "game-2",
                ),
                log_line(
                    "wsus",
                    "10.0.2.9",
                    "03/Jan/2026:12:01:01",
                    generic_url,
                    40,
                    "generic-2",
                ),
                log_line(
                    "wsus",
                    "10.0.2.9",
                    "03/Jan/2026:12:01:02",
                    generic_url,
                    50,
                    "generic-3",
                ),
            ]
            .concat(),
        );
        run(&pool, directory.path(), 2, "xbox-second", "xbox").await;
        let named = sqlx::query(
            r#"SELECT COUNT(*)::bigint AS "Rows", COALESCE(SUM("CacheHitBytes"), 0)::bigint AS "Bytes" FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2 AND "Service" = 'xbox' AND "GameName" = 'Game X'"#,
        )
        .bind("xbox")
        .bind("10.0.2.9")
        .fetch_one(&pool)
        .await
        .expect("read adopted Xbox row");
        assert_eq!(named.get::<i64, _>("Rows"), 1);
        assert_eq!(named.get::<i64, _>("Bytes"), 60);
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "Downloads" WHERE "Datasource" = $1 AND "ClientIp" = $2 AND "Service" = 'wsus'"#,
            )
            .bind("xbox")
            .bind("10.0.2.9")
            .fetch_one(&pool)
            .await
            .expect("count generic Xbox-client rows"),
            1
        );

        let empty_download = sqlx::query_scalar::<_, i64>(
            r#"INSERT INTO "Downloads" ("Service", "ClientIp", "StartTimeUtc", "EndTimeUtc", "CacheHitBytes", "CacheMissBytes", "IsActive", "Datasource") VALUES ('wsus', '10.0.3.3', '2026-01-04T13:30:00Z', '2026-01-04T13:30:00Z', 100, 0, true, 'ranges') RETURNING "Id""#,
        )
        .fetch_one(&pool)
        .await
        .expect("insert empty-range download");
        sqlx::query(
            r#"INSERT INTO "LogEntries" ("Timestamp", "ClientIp", "Service", "Method", "HttpRange", "Url", "StatusCode", "BytesServed", "CacheStatus", "DownloadId", "CreatedAt", "Datasource") VALUES ('2026-01-04T13:30:00Z', '10.0.3.3', 'wsus', 'GET', '', '/empty/file.bin', 206, 100, 'HIT', $1, now(), 'ranges')"#,
        )
        .bind(empty_download)
        .execute(&pool)
        .await
        .expect("insert empty-range log row");
        append(
            &path,
            &log_line(
                "wsus",
                "10.0.3.3",
                "04/Jan/2026:13:30:00",
                "/empty/file.bin",
                100,
                "bytes=0-99",
            ),
        );
        run(&pool, directory.path(), 3, "empty-range", "ranges").await;
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = 'ranges' AND "ClientIp" = '10.0.3.3'"#,
            )
            .fetch_one(&pool)
            .await
            .expect("count empty-range requests"),
            2
        );

        append(
            &path,
            &[
                log_line(
                    "epicgames",
                    "10.0.0.1",
                    "03/Jan/2026:13:00:00",
                    "/Builds/Org/_xboxgame:decoy/hash/default/file",
                    1,
                    "epic",
                ),
                log_line(
                    "wsus",
                    "10.0.0.9",
                    "03/Jan/2026:13:00:00",
                    &game_url,
                    1,
                    "xbox",
                ),
            ]
            .concat(),
        );
        run(&pool, directory.path(), 5, "group-order", "xbox").await;
        let order = sqlx::query(
            r#"SELECT MIN("Id") FILTER (WHERE "ClientIp" = '10.0.0.9') AS "Xbox", MIN("Id") FILTER (WHERE "ClientIp" = '10.0.0.1') AS "Epic" FROM "Downloads" WHERE "Datasource" = $1"#,
        )
        .bind("xbox")
        .fetch_one(&pool)
        .await
        .expect("read group insertion order");
        assert!(order.get::<i64, _>("Xbox") < order.get::<i64, _>("Epic"));

        drop_schema(pool, &schema, options).await;
    }

    #[tokio::test]
    async fn request_ranges_and_custom_plans_preserve_duplicate_identity() {
        let name = "request_ranges_and_custom_plans_preserve_duplicate_identity";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };
        let directory = tempfile::tempdir().expect("create database fixture");
        let path = directory.path().join("access.log");
        let first = log_line(
            "wsus",
            "10.0.3.1",
            "04/Jan/2026:12:00:00",
            "/same/file.bin",
            100,
            "bytes=0-99",
        );
        std::fs::write(&path, &first).expect("write range fixture");
        run(&pool, directory.path(), 0, "range-first", "ranges").await;
        append(
            &path,
            &log_line(
                "wsus",
                "10.0.3.1",
                "04/Jan/2026:12:00:00",
                "/same/file.bin",
                100,
                "bytes=100-199",
            ),
        );
        run(&pool, directory.path(), 1, "range-second", "ranges").await;
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = $1 AND "ClientIp" = $2"#,
            )
            .bind("ranges")
            .bind("10.0.3.1")
            .fetch_one(&pool)
            .await
            .expect("count ranged requests"),
            2
        );

        let legacy_download = sqlx::query_scalar::<_, i64>(
            r#"INSERT INTO "Downloads" ("Service", "ClientIp", "StartTimeUtc", "EndTimeUtc", "CacheHitBytes", "CacheMissBytes", "IsActive", "Datasource") VALUES ('wsus', '10.0.3.2', '2026-01-04T13:00:00Z', '2026-01-04T13:00:00Z', 100, 0, true, 'ranges') RETURNING "Id""#,
        )
        .fetch_one(&pool)
        .await
        .expect("insert legacy download");
        sqlx::query(
            r#"INSERT INTO "LogEntries" ("Timestamp", "ClientIp", "Service", "Method", "HttpRange", "Url", "StatusCode", "BytesServed", "CacheStatus", "DownloadId", "CreatedAt", "Datasource") VALUES ('2026-01-04T13:00:00Z', '10.0.3.2', 'wsus', 'GET', NULL, '/legacy/file.bin', 206, 100, 'HIT', $1, now(), 'ranges')"#,
        )
        .bind(legacy_download)
        .execute(&pool)
        .await
        .expect("insert legacy log row");
        append(
            &path,
            &log_line(
                "wsus",
                "10.0.3.2",
                "04/Jan/2026:13:00:00",
                "/legacy/file.bin",
                100,
                "bytes=0-99",
            ),
        );
        run(&pool, directory.path(), 2, "legacy-range", "ranges").await;
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = 'ranges' AND "ClientIp" = '10.0.3.2'"#,
            )
            .fetch_one(&pool)
            .await
            .expect("count legacy request"),
            1
        );

        sqlx::query(
            r#"INSERT INTO "Downloads" ("Service", "ClientIp", "StartTimeUtc", "EndTimeUtc", "CacheHitBytes", "CacheMissBytes", "IsActive", "Datasource") SELECT 'steam', 'dense-' || value::text, now(), now(), 0, 0, false, 'ranges' FROM generate_series(1, 3000) value"#,
        )
        .execute(&pool)
        .await
        .expect("seed dense download window");
        let plan_lines = (0..10)
            .map(|index| {
                log_line(
                    "steam",
                    &format!("192.0.2.{index}"),
                    "04/Jan/2026:14:00:00",
                    "/nodepot/file.bin",
                    1,
                    &format!("plan-{index}"),
                )
            })
            .collect::<String>();
        append(&path, &plan_lines);
        let plan_options = options.clone().options([("search_path", schema.as_str())]);
        let plan_pool = PgPoolOptions::new()
            .max_connections(1)
            .connect_with(plan_options)
            .await
            .expect("connect one-connection plan pool");
        run(&plan_pool, directory.path(), 4, "custom-plans", "ranges").await;
        let prepared = sqlx::query_scalar::<_, i64>(
            r#"SELECT COUNT(*) FROM pg_prepared_statements WHERE statement LIKE '%"EndTimeUtc" >= $%' AND statement LIKE '%FROM "Downloads"%'"#,
        )
        .persistent(false)
        .fetch_one(&plan_pool)
        .await
        .expect("count prepared window statements");
        assert_eq!(prepared, 0);
        plan_pool.close().await;

        drop_schema(pool, &schema, options).await;
    }

    #[tokio::test]
    async fn truncated_rotation_flushes_complete_records_and_saves_newer_position() {
        let name = "truncated_rotation_flushes_complete_records_and_saves_newer_position";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };
        let directory = tempfile::tempdir().expect("create truncated fixture");
        let rotation = directory.path().join("access.log.1.gz");
        let current = directory.path().join("access.log");
        let rotated_lines = (0..200)
            .map(|index| {
                log_line(
                    "steam",
                    "10.0.4.1",
                    "05/Jan/2026:12:00:00",
                    &format!("/depot/401/chunk/{index}"),
                    1,
                    &format!("range-{index}"),
                )
            })
            .collect::<String>();
        super::classification_tests::write_gzip(
            &rotation,
            rotated_lines.as_bytes(),
            Compression::none(),
        );
        let bytes = std::fs::read(&rotation).expect("read gzip fixture");
        std::fs::write(&rotation, &bytes[..bytes.len() * 3 / 5]).expect("truncate gzip fixture");
        std::fs::write(
            &current,
            [
                log_line(
                    "steam",
                    "10.0.4.1",
                    "05/Jan/2026:12:00:01",
                    "/depot/401/chunk/live-a",
                    1,
                    "live-a",
                ),
                log_line(
                    "steam",
                    "10.0.4.1",
                    "05/Jan/2026:12:00:02",
                    "/depot/401/chunk/live-b",
                    1,
                    "live-b",
                ),
            ]
            .concat(),
        )
        .expect("write current fixture");

        let mut reader = LogFileReader::open(&rotation).expect("open truncated fixture");
        let mut buffer = Vec::new();
        let mut complete = 0u64;
        loop {
            buffer.clear();
            match reader.read_until_newline(&mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(_) if buffer.ends_with(b"\n") => complete += 1,
                Ok(_) => break,
            }
        }
        assert!(complete > 0);

        let progress = run(&pool, directory.path(), 0, "truncated", "truncated").await;
        assert_eq!(progress["terminal_status"], "partial");
        assert_eq!(progress["source_positions"]["access.log"], complete + 2);
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = 'truncated'"#,
            )
            .fetch_one(&pool)
            .await
            .expect("count flushed truncated records"),
            (complete + 2) as i64
        );
        assert!(directory.path().join("truncated-resume.json").is_file());

        let appended = log_line(
            "steam",
            "10.0.4.1",
            "05/Jan/2026:12:00:03",
            "/depot/401/chunk/live-c",
            1,
            "live-c",
        );
        append(&current, &appended);
        let resumed = run(
            &pool,
            directory.path(),
            complete + 2,
            "truncated-second",
            "truncated",
        )
        .await;
        assert_eq!(resumed["terminal_status"], "completed");
        assert_eq!(resumed["source_positions"]["access.log"], complete + 3);
        assert!(resumed["files_with_errors"]
            .as_array()
            .expect("read resumed file errors")
            .is_empty());

        let without_resume = tempfile::tempdir().expect("create line-skip fixture");
        std::fs::copy(&rotation, without_resume.path().join("access.log.1.gz"))
            .expect("copy truncated rotation");
        std::fs::copy(&current, without_resume.path().join("access.log"))
            .expect("copy current fixture");
        let positions = HashMap::from([("access.log".to_string(), complete + 2)]);
        let progress_path = without_resume.path().join("progress.json");
        let mut processor = Processor::new(
            pool.clone(),
            without_resume.path().to_path_buf(),
            progress_path.clone(),
            false,
            "truncated-without-resume".to_string(),
            positions,
            "truncated-without-resume".to_string(),
        );
        processor.process().await.expect("run corrected line skip");
        let line_skip: serde_json::Value = serde_json::from_str(
            &std::fs::read_to_string(progress_path).expect("read line-skip progress"),
        )
        .expect("parse line-skip progress");
        assert_eq!(line_skip["terminal_status"], "partial");
        assert_eq!(line_skip["source_positions"]["access.log"], complete + 3);
        assert_eq!(line_skip["lines_parsed"], complete + 3);

        let rewrite_directory = tempfile::tempdir().expect("create rewrite fixture");
        let rewrite_current = rewrite_directory.path().join("access.log");
        let old_lines = [
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:00:00",
                "/depot/402/chunk/old-a",
                1,
                "old-a",
            ),
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:00:01",
                "/depot/402/chunk/old-b",
                1,
                "old-b",
            ),
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:00:02",
                "/depot/402/chunk/old-c",
                1,
                "old-c",
            ),
        ]
        .concat();
        std::fs::write(&rewrite_current, old_lines).expect("write original resume fixture");
        run(
            &pool,
            rewrite_directory.path(),
            0,
            "rewrite-first",
            "resume-rewrite",
        )
        .await;

        let rewritten_lines = [
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:01:00",
                &format!("/depot/402/chunk/rewrite-d-{}", "x".repeat(800)),
                1,
                "rewrite-d",
            ),
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:01:01",
                "/depot/402/chunk/rewrite-e",
                1,
                "rewrite-e",
            ),
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:01:02",
                "/depot/402/chunk/rewrite-f",
                1,
                "rewrite-f",
            ),
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:01:03",
                "/depot/402/chunk/rewrite-g",
                1,
                "rewrite-g",
            ),
            log_line(
                "steam",
                "10.0.4.2",
                "05/Jan/2026:13:01:04",
                "/depot/402/chunk/rewrite-h",
                1,
                "rewrite-h",
            ),
        ]
        .concat();
        let rewrite_identity = log_purge::file_identity(&rewrite_current)
            .expect("read original resume target identity");
        let hook_current = rewrite_current.clone();
        let rewrite_progress_path = rewrite_directory.path().join("rewrite-second.json");
        let mut rewrite_processor = Processor::new(
            pool.clone(),
            rewrite_directory.path().to_path_buf(),
            rewrite_progress_path.clone(),
            false,
            "resume-rewrite".to_string(),
            HashMap::from([("access.log".to_string(), 3)]),
            "rewrite-second".to_string(),
        );
        rewrite_processor.resume_path =
            Some(rewrite_directory.path().join("resume-rewrite-resume.json"));
        rewrite_processor.before_resume_open = Some(Box::new(move || {
            std::fs::write(&hook_current, &rewritten_lines).expect("rewrite resume target");
            assert_eq!(
                log_purge::file_identity(&hook_current)
                    .expect("read rewritten resume target identity"),
                rewrite_identity
            );
        }));
        rewrite_processor
            .process()
            .await
            .expect("restart rewritten resume target");
        let rewrite_progress: serde_json::Value = serde_json::from_str(
            &std::fs::read_to_string(rewrite_progress_path)
                .expect("read rewrite progress checkpoint"),
        )
        .expect("parse rewrite progress checkpoint");
        assert_eq!(rewrite_progress["source_positions"]["access.log"], 5);
        assert_eq!(rewrite_progress["unparsed_lines"], 0);
        let rewrite_urls = sqlx::query_scalar::<_, String>(
            r#"SELECT "Url" FROM "LogEntries" WHERE "Datasource" = 'resume-rewrite' ORDER BY "Url""#,
        )
        .fetch_all(&pool)
        .await
        .expect("read rewrite URLs");
        assert_eq!(
            rewrite_urls,
            vec![
                "/depot/402/chunk/old-a".to_string(),
                "/depot/402/chunk/old-b".to_string(),
                "/depot/402/chunk/old-c".to_string(),
                "/depot/402/chunk/rewrite-g".to_string(),
                "/depot/402/chunk/rewrite-h".to_string(),
            ]
        );

        let zero_directory = tempfile::tempdir().expect("create zero-offset fixture");
        let zero_current = zero_directory.path().join("access.log");
        std::fs::write(&zero_current, b"").expect("write empty resume target");
        run(&pool, zero_directory.path(), 0, "zero-first", "resume-zero").await;
        std::fs::write(
            &zero_current,
            log_line(
                "steam",
                "10.0.4.3",
                "05/Jan/2026:14:00:00",
                "/depot/403/chunk/lost",
                1,
                "lost",
            ),
        )
        .expect("grow zero-offset resume target");
        let zero_rotated = zero_directory.path().join("access.log.1");
        let hook_current = zero_current.clone();
        let hook_rotated = zero_rotated.clone();
        let zero_progress_path = zero_directory.path().join("zero-second.json");
        let mut zero_processor = Processor::new(
            pool.clone(),
            zero_directory.path().to_path_buf(),
            zero_progress_path.clone(),
            false,
            "resume-zero".to_string(),
            HashMap::from([("access.log".to_string(), 0)]),
            "zero-second".to_string(),
        );
        zero_processor.resume_path = Some(zero_directory.path().join("resume-zero-resume.json"));
        zero_processor.before_resume_open = Some(Box::new(move || {
            std::fs::rename(&hook_current, &hook_rotated).expect("rotate zero-offset target");
            std::fs::write(
                &hook_current,
                log_line(
                    "steam",
                    "10.0.4.3",
                    "05/Jan/2026:14:00:01",
                    "/depot/403/chunk/new",
                    1,
                    "new",
                ),
            )
            .expect("write zero-offset replacement");
        }));
        zero_processor
            .process()
            .await
            .expect("restart zero-offset resume target");
        let zero_progress: serde_json::Value = serde_json::from_str(
            &std::fs::read_to_string(zero_progress_path)
                .expect("read zero-offset progress checkpoint"),
        )
        .expect("parse zero-offset progress checkpoint");
        assert_eq!(zero_progress["source_positions"]["access.log"], 2);
        assert_eq!(zero_progress["unparsed_lines"], 0);
        let zero_urls = sqlx::query_scalar::<_, String>(
            r#"SELECT "Url" FROM "LogEntries" WHERE "Datasource" = 'resume-zero' ORDER BY "Url""#,
        )
        .fetch_all(&pool)
        .await
        .expect("read zero-offset URLs");
        assert_eq!(
            zero_urls,
            vec![
                "/depot/403/chunk/lost".to_string(),
                "/depot/403/chunk/new".to_string(),
            ]
        );

        verify_resume_boundaries(&pool).await;
        #[cfg(unix)]
        verify_unix_open_denial(&pool).await;

        drop_schema(pool, &schema, options).await;
    }

    #[tokio::test]
    async fn count_seeded_unreadable_rotation_preserves_new_records() {
        let name = "count_seeded_unreadable_rotation_preserves_new_records";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };

        for (datasource, populated) in [("count-seed-empty", false), ("count-seed-populated", true)]
        {
            let directory = tempfile::tempdir().expect("create count-seeded fixture");
            let rotated = directory.path().join("access.log.1");
            let current = directory.path().join("access.log");
            let first_two = [
                log_line(
                    "steam",
                    "10.0.6.9",
                    "07/Jan/2026:20:00:01",
                    "/resume/count-seed/01-current",
                    70,
                    "count-seed-1",
                ),
                log_line(
                    "steam",
                    "10.0.6.9",
                    "07/Jan/2026:20:00:02",
                    "/resume/count-seed/02-current",
                    80,
                    "count-seed-2",
                ),
            ]
            .concat();
            std::fs::write(
                &rotated,
                log_line(
                    "steam",
                    "10.0.6.9",
                    "07/Jan/2026:20:00:00",
                    "/resume/count-seed/00-unreadable",
                    5,
                    "count-seed-old",
                ),
            )
            .expect("write count-seeded rotated member");
            std::fs::write(&current, &first_two).expect("write counted current records");

            if populated {
                let preload = tempfile::tempdir().expect("create populated target fixture");
                std::fs::write(preload.path().join("access.log"), &first_two)
                    .expect("write populated target records");
                let (_, parsed) =
                    run_counted(&pool, preload.path(), 0, "count-seed-preload", datasource).await;
                assert_eq!(parsed, 2);
                assert_eq!(stored_totals(&pool, datasource).await, (2, 150, 0));
            }

            append(
                &current,
                &log_line(
                    "steam",
                    "10.0.6.9",
                    "07/Jan/2026:20:00:03",
                    "/resume/count-seed/03-new",
                    90,
                    "count-seed-3",
                ),
            );
            let resume_path = directory.path().join(format!("{datasource}-resume.json"));
            assert!(!resume_path.exists());

            let first_path = directory.path().join("count-seed-first.json");
            let mut first = Processor::new(
                pool.clone(),
                directory.path().to_path_buf(),
                first_path.clone(),
                false,
                datasource.to_string(),
                HashMap::from([("access.log".to_string(), 2)]),
                "count-seed-first".to_string(),
            );
            first.resume_path = Some(resume_path.clone());
            first.open_failure = Some(rotated.clone());
            assert_eq!(
                first
                    .process()
                    .await
                    .expect("process count-seeded fallback"),
                ProcessingOutcome::Completed
            );
            assert_eq!(
                first
                    .open_attempts
                    .get(&rotated)
                    .copied()
                    .unwrap_or_default(),
                1
            );
            assert_eq!(first.parsed_records, 3);
            assert_eq!(
                first.entries_saved.load(Ordering::Relaxed),
                if populated { 1 } else { 3 }
            );
            let first_progress: serde_json::Value = serde_json::from_str(
                &std::fs::read_to_string(first_path).expect("read count-seeded first progress"),
            )
            .expect("parse count-seeded first progress");
            assert_eq!(first_progress["terminal_status"], "partial");
            assert_eq!(first_progress["source_positions"]["access.log"], 3);
            assert_eq!(
                first_progress["files_with_errors"]
                    .as_array()
                    .expect("read count-seeded problems")
                    .len(),
                1
            );
            assert!(resume_path.is_file());
            assert_eq!(
                stored_rows(&pool, datasource).await,
                vec![
                    ("/resume/count-seed/01-current".to_string(), 70),
                    ("/resume/count-seed/02-current".to_string(), 80),
                    ("/resume/count-seed/03-new".to_string(), 90),
                ]
            );
            assert_eq!(stored_totals(&pool, datasource).await, (3, 240, 0));

            append(
                &current,
                &log_line(
                    "steam",
                    "10.0.6.9",
                    "07/Jan/2026:20:00:04",
                    "/resume/count-seed/04-next",
                    100,
                    "count-seed-4",
                ),
            );
            let second_path = directory.path().join("count-seed-second.json");
            let mut second = Processor::new(
                pool.clone(),
                directory.path().to_path_buf(),
                second_path.clone(),
                false,
                datasource.to_string(),
                HashMap::from([("access.log".to_string(), 3)]),
                "count-seed-second".to_string(),
            );
            second.resume_path = Some(resume_path.clone());
            second.open_failure = Some(rotated.clone());
            second
                .process()
                .await
                .expect("resume count-seeded fallback");
            assert_eq!(
                second
                    .open_attempts
                    .get(&rotated)
                    .copied()
                    .unwrap_or_default(),
                0
            );
            assert_eq!(second.parsed_records, 1);
            assert_eq!(second.entries_saved.load(Ordering::Relaxed), 1);
            let second_progress: serde_json::Value = serde_json::from_str(
                &std::fs::read_to_string(second_path).expect("read count-seeded second progress"),
            )
            .expect("parse count-seeded second progress");
            assert_eq!(second_progress["terminal_status"], "completed");
            assert_eq!(second_progress["source_positions"]["access.log"], 4);
            assert_eq!(stored_totals(&pool, datasource).await, (4, 340, 0));

            let third_path = directory.path().join("count-seed-third.json");
            let mut third = Processor::new(
                pool.clone(),
                directory.path().to_path_buf(),
                third_path.clone(),
                false,
                datasource.to_string(),
                HashMap::from([("access.log".to_string(), 4)]),
                "count-seed-third".to_string(),
            );
            third.resume_path = Some(resume_path.clone());
            third.open_failure = Some(rotated.clone());
            third
                .process()
                .await
                .expect("repeat unchanged count-seeded fallback");
            assert_eq!(
                third
                    .open_attempts
                    .get(&rotated)
                    .copied()
                    .unwrap_or_default(),
                0
            );
            assert_eq!(third.parsed_records, 0);
            assert_eq!(third.entries_saved.load(Ordering::Relaxed), 0);
            let third_progress: serde_json::Value = serde_json::from_str(
                &std::fs::read_to_string(third_path).expect("read count-seeded third progress"),
            )
            .expect("parse count-seeded third progress");
            assert_eq!(third_progress["terminal_status"], "completed");
            assert_eq!(third_progress["source_positions"]["access.log"], 4);
            assert_eq!(stored_totals(&pool, datasource).await, (4, 340, 0));
        }

        drop_schema(pool, &schema, options).await;
    }

    #[tokio::test]
    async fn frozen_database_batch_keeps_the_previous_resume_entry() {
        let name = "frozen_database_batch_keeps_the_previous_resume_entry";
        let Some((pool, schema, options)) = isolated_pool(name).await else {
            return;
        };
        let directory = tempfile::tempdir().expect("create frozen fixture");
        let oldest = directory.path().join("access.log.2");
        let current = directory.path().join("access.log");
        let first = (0..5010)
            .map(|index| {
                log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:00",
                    &format!("/depot/501/chunk/old-{index}"),
                    1,
                    &format!("old-{index}"),
                )
            })
            .collect::<String>();
        std::fs::write(&oldest, first).expect("write oldest batch fixture");
        std::fs::write(
            directory.path().join("access.log.1"),
            [
                log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:01",
                    "/depot/501/chunk/rotation-a",
                    1,
                    "rotation-a",
                ),
                log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:02",
                    "/depot/501/chunk/rotation-b",
                    1,
                    "rotation-b",
                ),
                log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:03",
                    "/depot/501/chunk/rotation-c",
                    1,
                    "rotation-c",
                ),
            ]
            .concat(),
        )
        .expect("write second rotation fixture");
        std::fs::write(
            &current,
            [
                log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:04",
                    "/depot/501/chunk/current-a",
                    1,
                    "current-a",
                ),
                log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:05",
                    "/depot/501/chunk/current-b",
                    1,
                    "current-b",
                ),
            ]
            .concat(),
        )
        .expect("write current batch fixture");
        run(&pool, directory.path(), 0, "frozen-first", "frozen").await;

        let committed = (0..5000)
            .map(|index| {
                log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:06",
                    &format!("/depot/501/chunk/committed-{index}"),
                    1,
                    &format!("committed-{index}"),
                )
            })
            .collect::<String>();
        append(&current, &committed);
        append(
            &current,
            &log_line(
                "steam",
                "10.0.5.1",
                "06/Jan/2026:12:00:07",
                "/depot/501/chunk/tc17-fail",
                1,
                "fail",
            ),
        );
        sqlx::query(
            r#"ALTER TABLE "LogEntries" ADD CONSTRAINT tc17 CHECK ("Url" NOT LIKE '%/tc17-fail%')"#,
        )
        .execute(&pool)
        .await
        .expect("add deterministic batch constraint");
        let second = run(&pool, directory.path(), 5015, "frozen-second", "frozen").await;
        assert_eq!(second["terminal_status"], "partial");
        assert_eq!(second["source_positions"]["access.log"], 5015);

        sqlx::query(r#"ALTER TABLE "LogEntries" DROP CONSTRAINT tc17"#)
            .execute(&pool)
            .await
            .expect("drop deterministic batch constraint");
        std::fs::remove_file(&oldest).expect("delete oldest frozen fixture");
        for index in 0..3 {
            append(
                &current,
                &log_line(
                    "steam",
                    "10.0.5.1",
                    "06/Jan/2026:12:00:08",
                    &format!("/depot/501/chunk/new-{index}"),
                    1,
                    &format!("new-{index}"),
                ),
            );
        }
        let third = run(&pool, directory.path(), 5015, "frozen-third", "frozen").await;
        assert_eq!(third["source_positions"]["access.log"], 5009);
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                r#"SELECT COUNT(*) FROM "LogEntries" WHERE "Datasource" = 'frozen' AND ("Url" LIKE '%/tc17-fail' OR "Url" LIKE '%/new-%')"#,
            )
            .fetch_one(&pool)
            .await
            .expect("count recovered frozen records"),
            4
        );

        drop_schema(pool, &schema, options).await;
    }

    async fn stored_view(pool: &PgPool) -> (Vec<String>, Vec<String>, Vec<String>) {
        let log_rows = sqlx::query(
            r#"SELECT "Timestamp", "ClientIp", "Service", "Method", "HttpRange", "Url", "StatusCode", "BytesServed", "CacheStatus", "DepotId", "Datasource" FROM "LogEntries" ORDER BY "Timestamp", "ClientIp", "Service", "Url", COALESCE("HttpRange", '')"#,
        )
        .fetch_all(pool)
        .await
        .expect("read equivalence log rows")
        .into_iter()
        .map(|row| {
            format!(
                "{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}",
                row.get::<chrono::DateTime<Utc>, _>("Timestamp"),
                row.get::<String, _>("ClientIp"),
                row.get::<String, _>("Service"),
                row.get::<String, _>("Method"),
                row.get::<Option<String>, _>("HttpRange"),
                row.get::<String, _>("Url"),
                row.get::<i32, _>("StatusCode"),
                row.get::<i64, _>("BytesServed"),
                row.get::<String, _>("CacheStatus"),
                row.get::<Option<i64>, _>("DepotId"),
                row.get::<String, _>("Datasource")
            )
        })
        .collect();
        let downloads = sqlx::query(
            r#"SELECT "Service", "ClientIp", "StartTimeUtc", "EndTimeUtc", "CacheHitBytes", "CacheMissBytes", "IsActive", "LastUrl", "DepotId", "GameAppId", "GameName", "GameImageUrl", "Datasource", "XboxProductId", "IsEvicted" FROM "Downloads" ORDER BY "StartTimeUtc", "ClientIp", "Service", COALESCE("DepotId", 0)"#,
        )
        .fetch_all(pool)
        .await
        .expect("read equivalence downloads")
        .into_iter()
        .map(|row| {
            format!(
                "{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}",
                row.get::<String, _>("Service"),
                row.get::<String, _>("ClientIp"),
                row.get::<chrono::DateTime<Utc>, _>("StartTimeUtc"),
                row.get::<chrono::DateTime<Utc>, _>("EndTimeUtc"),
                row.get::<i64, _>("CacheHitBytes"),
                row.get::<i64, _>("CacheMissBytes"),
                row.get::<bool, _>("IsActive"),
                row.get::<Option<String>, _>("LastUrl"),
                row.get::<Option<i64>, _>("DepotId"),
                row.get::<Option<i64>, _>("GameAppId"),
                row.get::<Option<String>, _>("GameName"),
                row.get::<Option<String>, _>("GameImageUrl"),
                row.get::<String, _>("Datasource"),
                row.get::<Option<String>, _>("XboxProductId"),
                row.get::<bool, _>("IsEvicted")
            )
        })
        .collect();
        let groups = sqlx::query_scalar::<_, String>(
            r#"SELECT string_agg(concat_ws('|', "Timestamp"::text, "Url", COALESCE("HttpRange", '')), E'\x1e' ORDER BY "Timestamp", "Url", COALESCE("HttpRange", '')) FROM "LogEntries" GROUP BY "DownloadId" ORDER BY MIN("Timestamp"), MIN("Url")"#,
        )
        .fetch_all(pool)
        .await
        .expect("read equivalence groups");
        (log_rows, downloads, groups)
    }

    #[tokio::test]
    async fn one_run_and_incremental_runs_store_the_same_result() {
        let name = "one_run_and_incremental_runs_store_the_same_result";
        let Some((one_pool, one_schema, one_options)) = isolated_pool(name).await else {
            return;
        };
        let Some((many_pool, many_schema, many_options)) = isolated_pool(name).await else {
            return;
        };
        let lines = vec![
            log_line(
                "steam",
                "10.0.6.1",
                "07/Jan/2026:12:00:00",
                "/depot/601/chunk/a",
                10,
                "a",
            ),
            log_line(
                "steam",
                "10.0.6.2",
                "07/Jan/2026:12:00:01",
                "/depot/602/chunk/a",
                20,
                "a",
            ),
            log_line(
                "wsus",
                "10.0.6.3",
                "07/Jan/2026:12:00:02",
                "/ranged/file.bin",
                30,
                "bytes=0-29",
            ),
            log_line(
                "steam",
                "10.0.6.4",
                "07/Jan/2026:12:00:10",
                "/depot/604/chunk/a",
                40,
                "a",
            ),
            log_line(
                "steam",
                "10.0.6.4",
                "07/Jan/2026:12:06:20",
                "/depot/604/chunk/b",
                50,
                "b",
            ),
            log_line(
                "wsus",
                "10.0.6.3",
                "07/Jan/2026:12:00:02",
                "/ranged/file.bin",
                30,
                "bytes=30-59",
            ),
            log_line(
                "steam",
                "10.0.6.1",
                "07/Jan/2026:12:00:03",
                "/depot/601/chunk/b",
                60,
                "b",
            ),
            log_line(
                "steam",
                "10.0.6.2",
                "07/Jan/2026:12:00:04",
                "/depot/602/chunk/b",
                70,
                "b",
            ),
        ];

        let one_directory = tempfile::tempdir().expect("create one-run fixture");
        super::classification_tests::write_gzip(
            &one_directory.path().join("access.log.2.gz"),
            lines[..4].concat().as_bytes(),
            Compression::default(),
        );
        std::fs::write(
            one_directory.path().join("access.log.1"),
            lines[4..7].concat(),
        )
        .expect("write one-run rotation");
        std::fs::write(one_directory.path().join("access.log"), &lines[7])
            .expect("write one-run current file");
        let one_progress = run(&one_pool, one_directory.path(), 0, "one", "equivalence").await;

        let many_directory = tempfile::tempdir().expect("create incremental fixture");
        let many_current = many_directory.path().join("access.log");
        std::fs::write(&many_current, lines[..2].concat()).expect("write incremental step one");
        let first = run(
            &many_pool,
            many_directory.path(),
            0,
            "many-1",
            "equivalence",
        )
        .await;
        append(&many_current, &lines[2..4].concat());
        let second = run(
            &many_pool,
            many_directory.path(),
            first["source_positions"]["access.log"].as_u64().unwrap(),
            "many-2",
            "equivalence",
        )
        .await;
        std::fs::rename(&many_current, many_directory.path().join("access.log.1"))
            .expect("rotate incremental current");
        std::fs::write(&many_current, &lines[4]).expect("write incremental step three");
        let third = run(
            &many_pool,
            many_directory.path(),
            second["source_positions"]["access.log"].as_u64().unwrap(),
            "many-3",
            "equivalence",
        )
        .await;
        append(&many_current, &lines[5]);
        let fourth = run(
            &many_pool,
            many_directory.path(),
            third["source_positions"]["access.log"].as_u64().unwrap(),
            "many-4",
            "equivalence",
        )
        .await;
        append(&many_current, &lines[6]);
        let fifth = run(
            &many_pool,
            many_directory.path(),
            fourth["source_positions"]["access.log"].as_u64().unwrap(),
            "many-5",
            "equivalence",
        )
        .await;
        let plain_second = many_directory.path().join("access.log.2");
        std::fs::rename(many_directory.path().join("access.log.1"), &plain_second)
            .expect("shift incremental rotation");
        let oldest = std::fs::read(&plain_second).expect("read incremental rotation");
        super::classification_tests::write_gzip(
            &many_directory.path().join("access.log.2.gz"),
            &oldest,
            Compression::default(),
        );
        std::fs::remove_file(&plain_second).expect("remove recompressed rotation");
        std::fs::rename(&many_current, many_directory.path().join("access.log.1"))
            .expect("rotate incremental live file");
        std::fs::write(&many_current, &lines[7]).expect("write incremental final current");
        let many_progress = run(
            &many_pool,
            many_directory.path(),
            fifth["source_positions"]["access.log"].as_u64().unwrap(),
            "many-6",
            "equivalence",
        )
        .await;

        let one = stored_view(&one_pool).await;
        let many = stored_view(&many_pool).await;
        println!(
            "equivalence rows: one={} incremental={} downloads={}",
            one.0.len(),
            many.0.len(),
            one.1.len()
        );
        assert_eq!(one, many);
        assert_eq!(
            one_progress["source_positions"]["access.log"],
            many_progress["source_positions"]["access.log"]
        );

        drop_schema(one_pool, &one_schema, one_options).await;
        drop_schema(many_pool, &many_schema, many_options).await;
    }
}

#[cfg(test)]
mod log_entry_clamp_tests {
    use super::{
        clamp_chars, split_hit_miss_bytes, LOG_ENTRY_CLIENT_IP_MAX_CHARS,
        LOG_ENTRY_DATASOURCE_MAX_CHARS, LOG_ENTRY_HTTP_RANGE_MAX_CHARS,
        LOG_ENTRY_SERVICE_MAX_CHARS, LOG_ENTRY_URL_MAX_CHARS, LOG_ENTRY_VARCHAR_MAX_CHARS,
    };

    #[test]
    fn values_within_the_column_limit_pass_through_unchanged() {
        assert_eq!(clamp_chars("GET", LOG_ENTRY_VARCHAR_MAX_CHARS), "GET");
        // The longest legitimate nginx cache status must survive intact.
        assert_eq!(
            clamp_chars("REVALIDATED", LOG_ENTRY_VARCHAR_MAX_CHARS),
            "REVALIDATED"
        );
    }

    #[test]
    fn oversized_values_clamp_to_the_column_limit() {
        let oversized = "X".repeat(LOG_ENTRY_VARCHAR_MAX_CHARS + 5);
        let clamped = clamp_chars(&oversized, LOG_ENTRY_VARCHAR_MAX_CHARS);
        assert_eq!(clamped.chars().count(), LOG_ENTRY_VARCHAR_MAX_CHARS);
    }

    #[test]
    fn clamp_respects_char_boundaries() {
        let multibyte = "é".repeat(20);
        let clamped = clamp_chars(&multibyte, LOG_ENTRY_VARCHAR_MAX_CHARS);
        assert_eq!(clamped.chars().count(), LOG_ENTRY_VARCHAR_MAX_CHARS);
        assert!(multibyte.starts_with(&clamped));
    }

    #[test]
    fn oversized_http_range_clamps_to_its_column_limit() {
        let oversized = format!("bytes={}", "0-100,".repeat(400));
        let clamped = clamp_chars(&oversized, LOG_ENTRY_HTTP_RANGE_MAX_CHARS);
        assert_eq!(clamped.chars().count(), LOG_ENTRY_HTTP_RANGE_MAX_CHARS);
    }

    #[test]
    fn oversized_url_clamps_to_its_column_limit() {
        let oversized = "u".repeat(LOG_ENTRY_URL_MAX_CHARS + 1);
        let clamped = clamp_chars(&oversized, LOG_ENTRY_URL_MAX_CHARS);
        assert_eq!(clamped.chars().count(), LOG_ENTRY_URL_MAX_CHARS);
    }

    #[test]
    fn oversized_client_ip_clamps_to_its_column_limit() {
        let oversized = "1".repeat(LOG_ENTRY_CLIENT_IP_MAX_CHARS + 1);
        let clamped = clamp_chars(&oversized, LOG_ENTRY_CLIENT_IP_MAX_CHARS);
        assert_eq!(clamped.chars().count(), LOG_ENTRY_CLIENT_IP_MAX_CHARS);
    }

    #[test]
    fn oversized_service_clamps_to_its_column_limit() {
        let oversized = "s".repeat(LOG_ENTRY_SERVICE_MAX_CHARS + 1);
        let clamped = clamp_chars(&oversized, LOG_ENTRY_SERVICE_MAX_CHARS);
        assert_eq!(clamped.chars().count(), LOG_ENTRY_SERVICE_MAX_CHARS);
    }

    /// The bug this pins: a download whose lines all carried a status outside the exact literals
    /// `HIT` and `MISS` recorded zero bytes, so every page that reads a download's size showed
    /// nothing while the log entries held the real figures, and the views that hide empty sessions
    /// dropped the download entirely. BYPASS is the one exception, because nginx never consulted
    /// the cache on such a line and nothing was written to disk.
    #[test]
    fn only_bypassed_bytes_stay_out_of_both_totals() {
        let entries = [
            ("HIT", 100),
            ("hit", 10),
            ("MISS", 1000),
            ("miss", 200),
            ("BYPASS", 30),
            ("bypass", 4),
            ("EXPIRED", 7),
            ("UNKNOWN", 3),
            ("-", 1),
        ];
        let (hit, miss) = split_hit_miss_bytes(entries.iter().copied());

        assert_eq!(hit, 110, "HIT is matched however the log cased it");
        assert_eq!(
            miss, 1211,
            "everything else that served bytes counts as a miss, BYPASS aside"
        );
        assert_eq!(
            hit + miss + 34,
            entries.iter().map(|(_, bytes)| bytes).sum::<i64>(),
            "every served byte is still accounted for: the 34 bypassed ones are the only bytes \
             either total leaves out"
        );
    }

    #[test]
    fn a_session_of_bypassed_lines_records_no_bytes() {
        // nginx answered these from upstream without consulting the cache, so no file was written
        // and the download has no cached size to report. The eviction scan reads these totals as
        // proof content reached disk, and such a session would otherwise be flagged evicted forever.
        let (hit, miss) = split_hit_miss_bytes([("BYPASS", 5_000), ("BYPASS", 1_500)].into_iter());

        assert_eq!(hit, 0);
        assert_eq!(miss, 0, "bypassed bytes are not the session's cached size");
    }

    #[test]
    fn oversized_datasource_clamps_to_its_column_limit() {
        let oversized = "d".repeat(LOG_ENTRY_DATASOURCE_MAX_CHARS + 1);
        let clamped = clamp_chars(&oversized, LOG_ENTRY_DATASOURCE_MAX_CHARS);
        assert_eq!(clamped.chars().count(), LOG_ENTRY_DATASOURCE_MAX_CHARS);
    }

    #[test]
    fn normal_http_range_passes_through_unchanged() {
        let normal = "bytes=0-1048575";
        assert_eq!(clamp_chars(normal, LOG_ENTRY_HTTP_RANGE_MAX_CHARS), normal);
    }
}

#[cfg(test)]
mod xbox_fragment_guard_tests {
    use super::{Processor, LOG_ENTRY_INSERT_SQL};
    // The shape guard now lives in the shared `cache_utils` module so `log_processor` and
    // `speed_tracker` apply ONE identical check. These tests exercise it through the same path
    // the log_processor pattern loader uses.
    use crate::cache_utils::is_valid_xbox_fragment;

    const GUID: &str = "12345678-90ab-cdef-1234-567890abcdef";

    #[test]
    fn log_entry_insert_persists_real_method_and_http_range() {
        assert!(LOG_ENTRY_INSERT_SQL.contains("\"Method\", \"HttpRange\""));
        assert!(LOG_ENTRY_INSERT_SQL.contains("$4::text[]"));
        assert!(LOG_ENTRY_INSERT_SQL.contains("$5::text[]"));
        assert!(LOG_ENTRY_INSERT_SQL.contains("$13::text[]"));
        assert!(!LOG_ENTRY_INSERT_SQL.contains("'GET'"));
    }

    #[test]
    fn accepts_filestreaming_files_guid() {
        let frag = format!("/filestreamingservice/files/{}", GUID);
        assert!(is_valid_xbox_fragment(&frag));
    }

    #[test]
    fn accepts_full_url_containing_the_fragment() {
        let url = format!(
            "http://assets1.xboxlive.com/filestreamingservice/files/{}?P1=123",
            GUID
        );
        assert!(is_valid_xbox_fragment(&url));
    }

    #[test]
    fn rejects_empty_and_root() {
        assert!(!is_valid_xbox_fragment(""));
        assert!(!is_valid_xbox_fragment("/"));
    }

    #[test]
    fn pattern_row_keeps_a_fragment_that_has_a_title() {
        let frag = format!("/filestreamingservice/files/{}", GUID);
        let kept = Processor::xbox_pattern_row(
            Some(frag.clone()),
            Some("Halo Infinite".to_string()),
            Some("9NBLGGH537DL".to_string()),
        );
        assert_eq!(
            kept,
            Some((
                frag,
                "Halo Infinite".to_string(),
                "9NBLGGH537DL".to_string()
            ))
        );
    }

    #[test]
    fn pattern_row_drops_a_blank_title_like_a_missing_one() {
        // An empty GameName is bucketed as steam:0 by the identity key rule and as a named game by
        // the detection queries, and the C# re-resolver only revisits wsus/xboxlive rows, so a row
        // stamped with "" can never be corrected. The URL must stay generic wsus instead.
        let frag = format!("/filestreamingservice/files/{}", GUID);
        let pid = Some("9NBLGGH537DL".to_string());

        assert_eq!(
            Processor::xbox_pattern_row(Some(frag.clone()), None, pid.clone()),
            None
        );
        assert_eq!(
            Processor::xbox_pattern_row(Some(frag.clone()), Some(String::new()), pid.clone()),
            None
        );
        assert_eq!(
            Processor::xbox_pattern_row(Some(frag), Some("   ".to_string()), pid),
            None
        );
    }

    #[test]
    fn rejects_non_filestreaming_fragments() {
        // A short generic wsus path that the old `len > 1` guard would have ACCEPTED, which would
        // then `contains()`-match unrelated Windows Update traffic.
        assert!(!is_valid_xbox_fragment("/files/"));
        assert!(!is_valid_xbox_fragment("/c/msdownload/update/abc"));
        assert!(!is_valid_xbox_fragment("microsoft"));
    }

    #[test]
    fn rejects_filestreaming_without_a_valid_guid() {
        assert!(!is_valid_xbox_fragment(
            "/filestreamingservice/files/not-a-guid"
        ));
        // Truncated GUID (too short).
        assert!(!is_valid_xbox_fragment(
            "/filestreamingservice/files/12345678-90ab"
        ));
        // Hyphens in the wrong positions.
        assert!(!is_valid_xbox_fragment(
            "/filestreamingservice/files/1234567890-ab-cdef-1234-567890abcdef"
        ));
    }

    #[test]
    fn accepts_uppercase_hex_guid() {
        let frag = "/filestreamingservice/files/ABCDEF12-3456-7890-ABCD-EF1234567890";
        assert!(is_valid_xbox_fragment(frag));
    }

    // SHARED FIXTURE (mirrored in C# XboxFragmentValidationTests). The marker itself is uppercased and
    // there is exactly ONE GUID, so this can ONLY validate via the case-insensitive marker branch (the
    // >=2-GUID branch does not apply). This locks the C#<->Rust equivalence: C#'s
    // _filestreamingFragmentRegex is RegexOptions.IgnoreCase, so the Rust marker scan must be too.
    #[test]
    fn accepts_uppercase_filestreamingservice_marker() {
        let frag = "/FILESTREAMINGSERVICE/FILES/12345678-90AB-CDEF-1234-567890ABCDEF";
        assert!(
            is_valid_xbox_fragment(frag),
            "uppercase filestreamingservice marker (1 GUID) must validate, matching C# IgnoreCase"
        );
    }

    // --- case-insensitive fragment matching (mirrors the C# OrdinalIgnoreCase resolver) ---

    fn pattern(frag: &str, title: &str) -> (String, String, String) {
        (frag.to_string(), title.to_string(), "9PXBOX".to_string())
    }

    #[test]
    fn match_is_case_insensitive_on_guid_hex() {
        // Stored fragment has lowercase GUID hex; access-log URL has UPPERCASE hex. A case-sensitive
        // contains would miss this; the resolver must still name the game.
        let patterns = vec![pattern(
            "/filestreamingservice/files/abcdef12-3456-7890-abcd-ef1234567890",
            "Halo",
        )];
        let url = "http://assets1.xboxlive.com/filestreamingservice/files/ABCDEF12-3456-7890-ABCD-EF1234567890?P1=1";
        let m = Processor::match_xbox_fragment(&patterns, url);
        assert!(
            m.is_some(),
            "uppercase-URL vs lowercase-fragment must match"
        );
        assert_eq!(m.unwrap().1, "Halo");
    }

    #[test]
    fn match_is_case_insensitive_when_fragment_is_uppercase() {
        let patterns = vec![pattern(
            "/FileStreamingService/Files/ABCDEF12-3456-7890-ABCD-EF1234567890",
            "Forza",
        )];
        let url = "http://cdn/filestreamingservice/files/abcdef12-3456-7890-abcd-ef1234567890";
        assert_eq!(
            Processor::match_xbox_fragment(&patterns, url).unwrap().1,
            "Forza"
        );
    }

    #[test]
    fn match_picks_longest_first_when_multiple_match() {
        // Patterns are stored longest-first (ORDER BY LENGTH DESC); the more specific one wins.
        let patterns = vec![
            pattern(
                "/filestreamingservice/files/abcdef12-3456-7890-abcd-ef1234567890/extra",
                "Specific",
            ),
            pattern(
                "/filestreamingservice/files/abcdef12-3456-7890-abcd-ef1234567890",
                "Generic",
            ),
        ];
        let url = "/FILESTREAMINGSERVICE/FILES/ABCDEF12-3456-7890-ABCD-EF1234567890/EXTRA";
        assert_eq!(
            Processor::match_xbox_fragment(&patterns, url).unwrap().1,
            "Specific"
        );
    }

    #[test]
    fn no_match_returns_none() {
        let patterns = vec![pattern(
            "/filestreamingservice/files/abcdef12-3456-7890-abcd-ef1234567890",
            "Halo",
        )];
        let url = "http://cdn/c/msdownload/update/software/secu/2024/01/something.cab";
        assert!(Processor::match_xbox_fragment(&patterns, url).is_none());
    }

    // --- assets1.xboxlive.com prefill fragment shape (the naming-bug fix) ---
    // The prefill daemon pulls direct from assets1.xboxlive.com over
    // /<digit>/<guid>/<guid>/<version>.<guid>/<packageName> (NOT the /filestreamingservice/files/<GUID>
    // DO-client shape). The validator must accept it via the ">=2 GUIDs" rule. This is the SAME fixture
    // string used by the C# `XboxFragmentValidationTests` so the two mirrors stay in sync.
    const BO4_FRAGMENT: &str = "/4/e4393384-8ff0-4d92-aac1-bad1fb53178a/cdaa6a83-240e-4888-b462-5a0d2c5aa90e/1.0.23.1.04470f65-eb47-428d-89de-d70e05f73369/bo4-ww-en-fr_1.0.23.1_x64__ht1qfjb0gaftw";
    const BO4_TITLE: &str = "Call of Duty\u{00ae}: Black Ops 4";

    #[test]
    fn accepts_real_assets1_prefill_fragment() {
        // 3 GUIDs (content + version + version-segment) → accepted via the >=2-GUID branch.
        assert!(
            is_valid_xbox_fragment(BO4_FRAGMENT),
            "real BO4 assets1.xboxlive.com fragment (>=2 GUIDs) must be accepted"
        );
    }

    #[test]
    fn rejects_paths_with_too_few_guids() {
        // No marker and <2 GUIDs → rejected, so generic Xbox Live / WSUS traffic is never relabeled.
        assert!(!is_valid_xbox_fragment("/4/foo/bar"));
        assert!(!is_valid_xbox_fragment("/4/foo/bar/baz"));
        assert!(!is_valid_xbox_fragment("/c/msdownload/update/abc"));
        // Exactly ONE GUID and no filestreamingservice marker is still not enough.
        assert!(!is_valid_xbox_fragment(
            "/4/e4393384-8ff0-4d92-aac1-bad1fb53178a/pkg"
        ));
    }

    #[test]
    fn match_round_trips_the_bo4_fragment() {
        // Store the daemon-emitted fragment; the access-log URL (same path, host prefix + query the
        // consumer strips) must resolve back to the title.
        let patterns = vec![pattern(BO4_FRAGMENT, BO4_TITLE)];
        let url = format!("http://assets1.xboxlive.com{}?P1=1", BO4_FRAGMENT);
        let m = Processor::match_xbox_fragment(&patterns, &url);
        assert!(
            m.is_some(),
            "stored BO4 fragment must match the same access-log URL"
        );
        assert_eq!(m.unwrap().1, BO4_TITLE);
    }

    #[test]
    fn xbox_cache_service_guard_includes_xboxlive_and_wsus_not_steam() {
        // The consumer guard must fire for BOTH tags Xbox traffic lands under, and never for an
        // unrelated service (so a steam row is never relabeled).
        assert!(Processor::is_xbox_cache_service("xboxlive"));
        assert!(Processor::is_xbox_cache_service("XBOXLIVE"));
        assert!(Processor::is_xbox_cache_service("wsus"));
        assert!(!Processor::is_xbox_cache_service("steam"));
        assert!(!Processor::is_xbox_cache_service("epicgames"));
    }
}
