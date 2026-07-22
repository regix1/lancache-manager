use anyhow::Result;
use chrono::{NaiveDateTime, TimeZone, Utc};
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

mod cache_utils;
mod cancel;
mod db;
mod log_discovery;
mod log_layout;
mod log_reader;
mod models;
mod parser;
mod parser_http_detailed;
mod progress_events;
mod progress_utils;
mod riot_hosts;
mod service_utils;
mod session;
mod tact_products;

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

    /// Line number to start from (0 for beginning)
    start_position: u64,

    /// Map depot IDs to games during processing (1=yes, 0=no)
    auto_map_depots: u8,

    /// Optional name for multi-datasource support (default: 'default')
    #[arg(default_value = "default")]
    datasource_name: Option<String>,

    /// Path to the per-source positions JSON ("" = legacy monolithic mode: only the
    /// access.log series is processed and start_position applies to it exactly as before)
    #[arg(default_value = "")]
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
use session::SessionTracker;
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
    start_position: u64,
    /// Per-stem start offsets from the positions file. None = legacy monolithic mode
    /// (only the access.log series, start_position applies to it exactly as before).
    positions: Option<HashMap<String, u64>>,
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
    parser: LogParser,
    detailed_parser: HttpDetailedParser,
    session_tracker: SessionTracker,
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
    local_tz: Tz,
    auto_map_depots: bool,
    last_logged_percent: AtomicU64, // Store as integer (0-100) for atomic operations
    logged_depots: HashSet<u32>,    // Track depots that have already been logged
    logged_tact_products: HashSet<String>, // Track Blizzard TACT products already logged
    logged_riot_hosts: HashSet<String>, // Track Riot CDN hosts already logged
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
    /// daemons contribute fragments). Empty until the first wsus line triggers a load.
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
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ProcessingOutcome {
    Completed,
    Cancelled,
}

/// A file result is deliberately typed so cooperative cancellation can never be folded
/// into the same branch as a fully consumed file by a future caller.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum FileProcessingOutcome {
    Completed,
    SourceBlockedByIncompleteRecord,
    Cancelled,
}

impl Processor {
    fn new(
        pool: PgPool,
        log_dir: PathBuf,
        progress_path: PathBuf,
        start_position: u64,
        auto_map_depots: bool,
        datasource_name: String,
        positions: Option<HashMap<String, u64>>,
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
            start_position,
            positions,
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
            parser: LogParser::new(local_tz),
            detailed_parser: HttpDetailedParser::new(local_tz),
            session_tracker: SessionTracker::new(Duration::from_secs(
                SESSION_GAP_MINUTES as u64 * 60,
            )),
            total_lines: AtomicU64::new(0),
            lines_parsed: AtomicU64::new(0),
            entries_saved: AtomicU64::new(0),
            total_bytes: 0,
            bytes_completed: AtomicU64::new(0),
            current_file_bytes: Arc::new(AtomicU64::new(0)),
            current_file_size: AtomicU64::new(0),
            local_tz,
            auto_map_depots,
            last_logged_percent: AtomicU64::new(0),
            logged_depots: HashSet::new(),
            logged_tact_products: HashSet::new(),
            logged_riot_hosts: HashSet::new(),
            datasource_name,
            depot_map: HashMap::new(),
            depots_unmapped: HashSet::new(),
            skip_dedup: false,
            xbox_patterns: Vec::new(),
            last_xbox_pattern_load: None,
            xbox_url_negative: HashSet::new(),
            xbox_url_positive: HashMap::new(),
        }
    }

    /// Convert UTC NaiveDateTime to local timezone NaiveDateTime
    /// Returns a naive datetime representing the same instant in the target timezone
    fn utc_to_local(&self, utc_dt: NaiveDateTime) -> NaiveDateTime {
        // Create a UTC datetime from the naive UTC time
        let utc_datetime = Utc.from_utc_datetime(&utc_dt);

        // Convert to the target timezone
        let local_datetime = utc_datetime.with_timezone(&self.local_tz);

        // Return the local time components (this discards the timezone info but keeps the adjusted time)
        // e.g., if UTC is 22:28:34 and TZ is America/Chicago (UTC-6), this returns 16:28:34
        NaiveDateTime::new(local_datetime.date_naive(), local_datetime.time())
    }

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

        // Legacy mode (no positions file) processes ONLY the monolithic access.log
        // series, with start_position applying to it exactly as it always has.
        // A positions file activates every discovered source; the CLI start_position
        // is ignored entirely (it cannot be meaningful across parallel streams).
        let sources: Vec<LogSource> = if self.positions.is_none() {
            source_set
                .sources
                .iter()
                .filter(|s| s.kind == SourceKind::Monolithic)
                .cloned()
                .collect()
        } else {
            source_set.sources.clone()
        };

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
        let starts_at_zero = match &self.positions {
            None => self.start_position == 0,
            Some(map) => sources
                .iter()
                .all(|s| map.get(&s.stem).copied().unwrap_or(0) == 0),
        };
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
        // Index IX_LogEntries_DuplicateCheck on (ClientIp, Service, Timestamp, Url, BytesServed) exists

        // Process each source's file series in order (oldest to newest).
        'sources: for (source_index, source) in sources.iter().enumerate() {
            let start_offset = match &self.positions {
                None => self.start_position,
                Some(map) => map.get(&source.stem).copied().unwrap_or(0),
            };
            let mut lines_to_skip = start_offset;
            // Complete records consumed across this stem's series (skipped + processed).
            // This IS the stem's position: an offset into the ordered rotation series.
            let mut records_consumed: u64 = 0;
            // Once a physical member errors, keep ingesting newer members but freeze the
            // published position at the prefix before that member. `count-lines` stops at
            // the same boundary. Partial terminals are already never persisted by the host,
            // but reporting a clean prefix keeps the checkpoint truthful and future-proof.
            let mut frozen_source_position: Option<u64> = None;

            for (file_index, log_file) in source.files.iter().enumerate() {
                eprintln!(
                    "\nProcessing {} file {}/{}: {}",
                    source.stem,
                    file_index + 1,
                    source.files.len(),
                    log_file.path.display()
                );

                let file_size = file_sizes[source_index][file_index];
                let position_before_file = records_consumed;
                let file_result = self
                    .process_single_file(
                        log_file,
                        file_size,
                        &mut lines_to_skip,
                        &source.kind,
                        &mut records_consumed,
                    )
                    .await;

                if file_result.is_err() && frozen_source_position.is_none() {
                    frozen_source_position = Some(position_before_file);
                }

                self.source_positions.insert(
                    source.stem.clone(),
                    frozen_source_position.unwrap_or(records_consumed),
                );

                // Check cancellation before folding the whole file into bytes_completed: an
                // interrupted file must retain its real in-flight byte count in the terminal
                // checkpoint rather than being reported as fully consumed.
                if matches!(&file_result, Ok(FileProcessingOutcome::Cancelled))
                    || cancel::is_cancelled()
                {
                    // Cancellation can arrive after the file's final read/batch flush but
                    // before this caller-side check, so the caller owns the terminal write.
                    self.write_cancelled_terminal()?;
                    self.current_file_bytes.store(0, Ordering::Relaxed);
                    self.current_file_size.store(0, Ordering::Relaxed);
                    return Ok(ProcessingOutcome::Cancelled);
                }

                // Whether the file completed or was skipped with an error, its bytes are
                // consumed work: fold them into the completed total so percent stays monotone.
                self.bytes_completed.fetch_add(file_size, Ordering::Relaxed);
                self.current_file_bytes.store(0, Ordering::Relaxed);
                self.current_file_size.store(0, Ordering::Relaxed);

                match file_result {
                    Ok(FileProcessingOutcome::Completed) => {}
                    Ok(FileProcessingOutcome::SourceBlockedByIncompleteRecord) => {
                        // Unterminated record: the rest of this source's series stays
                        // unread this run so the persisted position is a clean prefix.
                        // Only the live current file normally ends mid-line, so this
                        // costs nothing in the common case.
                        break;
                    }
                    Ok(FileProcessingOutcome::Cancelled) => {
                        unreachable!("cancellation is handled before completed-byte folding")
                    }
                    Err(e) => {
                        let error_str = format!("{}", e);
                        // Classify the error: IO/decompression errors are "corrupted file",
                        // database errors are infrastructure failures that should not be silenced
                        let is_db_error = error_str.contains("error returned from database")
                            || error_str.contains("pool timed out")
                            || error_str.contains("connection refused")
                            || error_str.contains("operator does not exist");

                        self.files_with_errors.push(format!(
                            "{}: {}",
                            log_file.path.display(),
                            error_str
                        ));

                        if is_db_error {
                            eprintln!(
                                "ERROR: Database error processing {}: {}",
                                log_file.path.display(),
                                e
                            );
                            // Database errors affect ALL files and sources, no point continuing
                            break 'sources;
                        } else {
                            eprintln!(
                                "⚠ Warning: Skipping corrupted file {}: {}",
                                log_file.path.display(),
                                e
                            );
                            eprintln!("  Continuing with remaining files...");
                            // Any file error caps the terminal at `partial`. Keep ingesting
                            // fresh files so a permanent corrupt rotation cannot starve live
                            // traffic; dedup absorbs their re-read while the published source
                            // position remains frozen at the clean prefix above.
                            continue;
                        }
                    }
                }
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

        // If we had errors and processed zero entries, this is a failure
        if !self.files_with_errors.is_empty() && entries_saved == 0 && self.total_bytes > 0 {
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
    ) -> Result<FileProcessingOutcome> {
        self.process_single_file_with_cancel(
            log_file,
            file_size,
            lines_to_skip,
            kind,
            records_consumed,
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
        is_cancelled: F,
    ) -> Result<FileProcessingOutcome>
    where
        F: Fn() -> bool,
    {
        // Track raw (compressed) bytes consumed from this file for byte-based progress.
        let byte_counter = Arc::new(AtomicU64::new(0));
        self.current_file_bytes = byte_counter.clone();
        self.current_file_size.store(file_size, Ordering::Relaxed);

        // Open log file with automatic compression detection
        let mut reader = LogFileReader::open_with_byte_counter(&log_file.path, byte_counter)?;

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
                let bytes_read = reader.read_until_newline(&mut record_buf)?;
                if bytes_read == 0 {
                    // Reached EOF before skipping all lines - this file is exhausted
                    return Ok(FileProcessingOutcome::Completed);
                }
                if !record_buf.ends_with(b"\n") {
                    // Unterminated final record: the writer is mid-line. It was never
                    // counted toward the position, so it is not skippable either — and
                    // the source stops here so the position stays a clean prefix.
                    self.incomplete_final_records += 1;
                    return Ok(FileProcessingOutcome::SourceBlockedByIncompleteRecord);
                }
                *lines_to_skip -= 1;
                *records_consumed += 1;
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
            let bytes_read = reader.read_until_newline(&mut record_buf)?;

            if bytes_read == 0 {
                // EOF - process remaining batch
                if !batch.is_empty() {
                    self.process_batch(&batch).await?;
                    batch.clear();
                    batch.shrink_to_fit(); // Release memory since we're done
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
                return Ok(FileProcessingOutcome::SourceBlockedByIncompleteRecord);
            }

            *records_consumed += 1;
            self.lines_parsed.fetch_add(1, Ordering::Relaxed);

            match outcome {
                ParseOutcome::Parsed(entry) => {
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
            grouped.entry(key).or_insert_with(Vec::new).push(entry);
        }

        // Process each group (Downloads, stats, session tracking)
        // Collect entries to insert into a shared buffer for ONE bulk INSERT
        let mut pending_inserts: Vec<PendingLogEntry> = Vec::with_capacity(entries.len());
        for (session_key, group_entries) in &grouped {
            self.process_session_group(&mut tx, session_key, group_entries, &mut pending_inserts)
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
                        let fragment: Option<String> = row.get("UrlFragment");
                        let title: Option<String> = row.get("Title");
                        let product_id: Option<String> = row.get("ProductId");
                        match (fragment, title, product_id) {
                            // Keep ONLY well-formed /filestreamingservice/files/<GUID> fragments.
                            // Empty / "/" / non-GUID fragments would `contains()`-match generic wsus
                            // URLs and relabel Windows Update traffic as a game — same shape guard the
                            // C# resolver (XboxMappingService.IsValidFragment) applies. This Rust path
                            // is the PRIMARY canonicalizer, so the guard MUST live here too.
                            (Some(frag), Some(name), Some(pid))
                                if cache_utils::is_valid_xbox_fragment(&frag) =>
                            {
                                Some((frag, name, pid))
                            }
                            _ => None,
                        }
                    })
                    .collect();
                self.last_xbox_pattern_load = Some(Instant::now());
                // Active-session safety: PRESERVE negative (None) decisions across a reload. A URL
                // that already resolved to generic Windows Update must STAY wsus for the rest of this
                // run, even if a daemon contributes its fragment mid-download — otherwise a still
                // in-flight `wsus` download would flip to `xbox` on the next batch, the active lookup
                // (keyed on Service='xbox') would miss the live `wsus` row, and the download would
                // SPLIT into two rows. New URLs first seen AFTER the pattern exists resolve normally;
                // the next process run (a natural session gap) re-evaluates everything against the
                // now-populated table. Positive entries are cleared so a renamed title can refresh.
                self.xbox_url_positive.clear();
            }
            Err(_) => {
                // Silently ignore (tables may not exist yet); wsus stays generic.
            }
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
        session_key: &str,
        entries: &[&LogEntry],
        pending_inserts: &mut Vec<PendingLogEntry>,
    ) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }

        // Duplicate detection - skip on fresh database for maximum speed
        let (new_entries, skipped): (Vec<&LogEntry>, usize) = if self.skip_dedup {
            // Fresh database - all entries are new, no dedup needed
            (entries.iter().map(|e| *e).collect(), 0)
        } else {
            // Bulk duplicate detection - single query for the whole group
            let mut check_client_ips: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_services: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_timestamps: Vec<chrono::DateTime<Utc>> =
                Vec::with_capacity(entries.len());
            let mut check_urls: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_bytes: Vec<i64> = Vec::with_capacity(entries.len());

            for entry in entries {
                check_client_ips.push(&entry.client_ip);
                check_services.push(&entry.service);
                check_timestamps.push(Utc.from_utc_datetime(&entry.timestamp));
                check_urls.push(&entry.url);
                check_bytes.push(entry.bytes_served);
            }

            let existing_rows = sqlx::query(
                r#"SELECT "ClientIp", "Service", "Timestamp", "Url", "BytesServed"
                   FROM "LogEntries"
                   WHERE ("ClientIp", "Service", "Timestamp", "Url", "BytesServed")
                   IN (SELECT * FROM UNNEST($1::text[], $2::text[], $3::timestamptz[], $4::text[], $5::bigint[]))"#
            )
            .bind(&check_client_ips)
            .bind(&check_services)
            .bind(&check_timestamps)
            .bind(&check_urls)
            .bind(&check_bytes)
            .fetch_all(&mut **tx)
            .await?;

            let existing_keys: HashSet<(String, String, i64, String, i64)> = existing_rows
                .iter()
                .map(|row| {
                    let client_ip: String = row.get("ClientIp");
                    let service: String = row.get("Service");
                    let ts: chrono::DateTime<Utc> = row.get("Timestamp");
                    let url: String = row.get("Url");
                    let bytes: i64 = row.get("BytesServed");
                    (
                        client_ip,
                        service,
                        ts.timestamp_nanos_opt().unwrap_or(0),
                        url,
                        bytes,
                    )
                })
                .collect();

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
                if existing_keys.contains(&key) {
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

        let total_hit_bytes: i64 = new_entries
            .iter()
            .filter(|e| e.cache_status == "HIT")
            .map(|e| e.bytes_served)
            .sum();

        let total_miss_bytes: i64 = new_entries
            .iter()
            .filter(|e| e.cache_status == "MISS")
            .map(|e| e.bytes_served)
            .sum();

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
        // and GameName becomes the resolved title — while LogEntries.Service / ServiceStats stay
        // `wsus` (the cache-hash service). Every Downloads lookup/insert/deactivate below keys on
        // `download_service`, so an Xbox download is consistently looked up under `xbox` across
        // batches (deterministic per-URL resolution → no mid-session split). Unmatched `wsus` keeps
        // `service` and is generic Windows Update. Backfilling already-ingested still-`wsus` rows is
        // the C# post-pass's job, not ours.
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
                            let game_display =
                                app_name.as_ref().map(|n| n.as_str()).unwrap_or("Unknown");
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
                match riot_hosts::resolve_riot_host(host) {
                    Some(name) => {
                        if !self.logged_riot_hosts.contains(host) {
                            eprintln!("Mapped Riot host {} -> {}", host, name);
                            self.logged_riot_hosts.insert(host.to_string());
                        }
                        (None, Some(name.to_string()))
                    }
                    None => {
                        if !self.logged_riot_hosts.contains(host) {
                            let req_count = new_entries
                                .iter()
                                .filter(|e| e.cdn_host.as_deref() == Some(host))
                                .count();
                            eprintln!("Unmapped Riot CDN host: {} ({} req)", host, req_count);
                            self.logged_riot_hosts.insert(host.to_string());
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

        // Check if we should create a new download session
        let should_create_new = self
            .session_tracker
            .should_create_new_session(session_key, first_timestamp);

        // Find or create download session
        let download_id = if should_create_new {
            // Mark ALL old active sessions as inactive for this client/service. Uses the
            // download-side identity service so an Xbox session deactivates prior `xbox` sessions
            // (not unrelated generic `wsus` Windows Update sessions for the same client).
            sqlx::query(
                "UPDATE \"Downloads\" SET \"IsActive\" = false WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"IsActive\" = true"
            )
            .bind(client_ip)
            .bind(download_service)
            .execute(&mut **tx)
            .await?;

            // Create new download session with depot mapping
            let game_image_url: Option<String> = None;

            // Convert NaiveDateTime to proper UTC DateTime for PostgreSQL timestamptz columns
            let first_utc_dt = Utc.from_utc_datetime(&first_timestamp);
            let last_utc_dt = Utc.from_utc_datetime(&last_timestamp);
            let first_local_dt = Utc.from_utc_datetime(&self.utc_to_local(first_timestamp));
            let last_local_dt = Utc.from_utc_datetime(&self.utc_to_local(last_timestamp));

            let row = sqlx::query(
                "INSERT INTO \"Downloads\" (\"Service\", \"ClientIp\", \"StartTimeUtc\", \"EndTimeUtc\", \"StartTimeLocal\", \"EndTimeLocal\", \"CacheHitBytes\", \"CacheMissBytes\", \"IsActive\", \"LastUrl\", \"DepotId\", \"GameAppId\", \"GameName\", \"GameImageUrl\", \"Datasource\", \"XboxProductId\")
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, true, $9, $10, $11, $12, $13, $14, $15)
                 RETURNING \"Id\""
            )
            .bind(download_service)
            .bind(client_ip)
            .bind(first_utc_dt)
            .bind(last_utc_dt)
            .bind(first_local_dt)
            .bind(last_local_dt)
            .bind(total_hit_bytes)
            .bind(total_miss_bytes)
            .bind(last_url)
            .bind(primary_depot_id.map(|d| d as i64))
            .bind(game_app_id.map(|id| id as i64))
            .bind(&game_name)
            .bind(&game_image_url)
            .bind(&self.datasource_name)
            .bind(&xbox_product_id)
            .fetch_one(&mut **tx)
            .await?;

            let download_id: i64 = row.get("Id");

            // Upsert client stats - no pre-check SELECT needed
            sqlx::query(
                r#"INSERT INTO "ClientStats" ("ClientIp", "TotalCacheHitBytes", "TotalCacheMissBytes", "LastActivityUtc", "LastActivityLocal", "TotalDownloads", "TotalDurationSeconds")
                   VALUES ($1, $2, $3, $4, $5, 1, 0.0)
                   ON CONFLICT ("ClientIp") DO UPDATE SET
                       "TotalCacheHitBytes" = "ClientStats"."TotalCacheHitBytes" + EXCLUDED."TotalCacheHitBytes",
                       "TotalCacheMissBytes" = "ClientStats"."TotalCacheMissBytes" + EXCLUDED."TotalCacheMissBytes",
                       "LastActivityUtc" = EXCLUDED."LastActivityUtc",
                       "LastActivityLocal" = EXCLUDED."LastActivityLocal",
                       "TotalDownloads" = "ClientStats"."TotalDownloads" + 1"#
            )
            .bind(client_ip)
            .bind(total_hit_bytes)
            .bind(total_miss_bytes)
            .bind(last_utc_dt)
            .bind(last_local_dt)
            .execute(&mut **tx)
            .await?;

            // Upsert service stats - no pre-check SELECT needed
            sqlx::query(
                r#"INSERT INTO "ServiceStats" ("Service", "TotalCacheHitBytes", "TotalCacheMissBytes", "LastActivityUtc", "LastActivityLocal", "TotalDownloads")
                   VALUES ($1, $2, $3, $4, $5, 1)
                   ON CONFLICT ("Service") DO UPDATE SET
                       "TotalCacheHitBytes" = "ServiceStats"."TotalCacheHitBytes" + EXCLUDED."TotalCacheHitBytes",
                       "TotalCacheMissBytes" = "ServiceStats"."TotalCacheMissBytes" + EXCLUDED."TotalCacheMissBytes",
                       "LastActivityUtc" = EXCLUDED."LastActivityUtc",
                       "LastActivityLocal" = EXCLUDED."LastActivityLocal",
                       "TotalDownloads" = "ServiceStats"."TotalDownloads" + 1"#
            )
            .bind(service)
            .bind(total_hit_bytes)
            .bind(total_miss_bytes)
            .bind(last_utc_dt)
            .bind(last_local_dt)
            .execute(&mut **tx)
            .await?;

            download_id
        } else {
            // Try to find existing active download for this specific depot/game
            let download_id_opt: Option<i64> = if let Some(ref xbox_title) = xbox_game_name {
                // Matched Xbox content. Match the existing active session under the IDENTITY service
                // (`xbox`, via download_service) by the resolved title OR a still-NULL GameName. The
                // `_xboxgame:<title>` session grouping already guarantees this batch belongs to this
                // one title, so adopting a previously-NULL xbox row is safe and lets the COALESCE
                // UPDATE name it in this batch. Keying on download_service (not the raw wsus service)
                // is what keeps Xbox sessions from colliding with generic Windows Update rows.
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true AND (\"GameName\" = $3 OR \"GameName\" IS NULL) ORDER BY (\"GameName\" = $3) DESC, \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(download_service)
                .bind(xbox_title)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i64, _>("Id"))
            } else if let Some(depot_id) = primary_depot_id {
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" = $3 AND \"IsActive\" = true ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .bind(depot_id as i64)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i64, _>("Id"))
            } else if service.to_lowercase().contains("epic") {
                // For Epic services, match by URL path prefix to find the correct game session
                if let Some(path_prefix) = last_url.and_then(|u| Self::extract_epic_path_prefix(u))
                {
                    let like_pattern = format!("{}%", path_prefix);
                    sqlx::query(
                        "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true AND \"LastUrl\" LIKE $3 ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                    )
                    .bind(client_ip)
                    .bind(service)
                    .bind(&like_pattern)
                    .fetch_optional(&mut **tx)
                    .await?
                    .map(|r| r.get::<i64, _>("Id"))
                } else {
                    sqlx::query(
                        "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                    )
                    .bind(client_ip)
                    .bind(service)
                    .fetch_optional(&mut **tx)
                    .await?
                    .map(|r| r.get::<i64, _>("Id"))
                }
            } else if service.to_lowercase() == "riot" && game_name.is_some() {
                // Riot host RESOLVED to a known game (lol/valorant/bacon). Match the
                // existing active Riot session either by the resolved GameName OR by a
                // still-NULL GameName. The host-keyed session grouping (_riotgame:<name>)
                // already guarantees this batch's entries belong to exactly this game, so
                // adopting a previously-NULL Riot row is safe — and it lets the COALESCE
                // UPDATE below NAME that row IN THIS BATCH (the row never lingers unnamed
                // waiting for a later back-fill). A genuinely-unknown earlier host would
                // have used the _riot:<host> key + the GameName-IS-NULL branch below, so
                // it won't be wrongly adopted here. Prefer the exact-name match first.
                let resolved_name = game_name
                    .as_deref()
                    .ok_or_else(|| anyhow::anyhow!("riot game_name expected but was None"))?;
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true AND (\"GameName\" = $3 OR \"GameName\" IS NULL) ORDER BY (\"GameName\" = $3) DESC, \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .bind(resolved_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i64, _>("Id"))
            } else if let Some(resolved_name) = game_name.as_deref() {
                // For Blizzard services whose segment RESOLVED to a game (or the shared
                // label), match the existing session by the resolved GameName so a
                // title's multiple CDN paths (configs + data + patch) attach to ONE
                // session instead of splitting per CDN path.
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true AND \"GameName\" = $3 ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .bind(resolved_name)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i64, _>("Id"))
            } else if let Some(product) = primary_tact_product.as_deref() {
                // Blizzard segment that did NOT resolve to a known game (GameName NULL):
                // match by the raw `/tpr/<seg>/` path so distinct unknown segments still
                // map to distinct sessions instead of collapsing into one generic session.
                // `product` is already lowercased (extract_tact_product), but LastUrl keeps
                // its original case (e.g. /tpr/WoW/...), so match case-insensitively via
                // LOWER(LastUrl) LIKE <lowercased-pattern> to avoid spawning a duplicate session.
                let like_pattern = format!("%/tpr/{}/%", product);
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true AND LOWER(\"LastUrl\") LIKE $3 ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .bind(&like_pattern)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i64, _>("Id"))
            } else if service.to_lowercase() == "riot" && primary_cdn_host.is_some() {
                // Riot host that did NOT resolve to a known game (GameName NULL above; a
                // resolved Riot game is handled by the GameName branch). The CDN host is
                // NOT persisted in LastUrl (every Riot bundle shares the identical path
                // /channels/public/bundles/<hash>.bundle), so unknown Riot hosts cannot
                // be discriminated at the DB level — match the most recent active Riot
                // session for this client. In-batch, distinct unknown hosts are already
                // kept in separate session-key groups (_riot:<host>); they only converge
                // here across batches, which is acceptable for the rare unmapped-host case.
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"GameName\" IS NULL AND \"IsActive\" = true ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i64, _>("Id"))
            } else {
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i64, _>("Id"))
            };

            let game_image_url: Option<String> = None;

            let (download_id, is_new) = if let Some(id) = download_id_opt {
                (id, false)
            } else {
                // Convert NaiveDateTime to proper UTC DateTime for PostgreSQL timestamptz columns
                let first_utc_dt = Utc.from_utc_datetime(&first_timestamp);
                let last_utc_dt = Utc.from_utc_datetime(&last_timestamp);
                let first_local_dt = Utc.from_utc_datetime(&self.utc_to_local(first_timestamp));
                let last_local_dt = Utc.from_utc_datetime(&self.utc_to_local(last_timestamp));

                let row = sqlx::query(
                    "INSERT INTO \"Downloads\" (\"ClientIp\", \"Service\", \"StartTimeUtc\", \"EndTimeUtc\", \"StartTimeLocal\", \"EndTimeLocal\", \"CacheHitBytes\", \"CacheMissBytes\", \"IsActive\", \"GameAppId\", \"GameName\", \"GameImageUrl\", \"LastUrl\", \"DepotId\", \"Datasource\", \"XboxProductId\") VALUES ($1, $2, $3, $4, $5, $6, $7, $8, true, $9, $10, $11, $12, $13, $14, $15) RETURNING \"Id\""
                )
                .bind(client_ip)
                .bind(download_service)
                .bind(first_utc_dt)
                .bind(last_utc_dt)
                .bind(first_local_dt)
                .bind(last_local_dt)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(game_app_id.map(|id| id as i64))
                .bind(&game_name)
                .bind(&game_image_url)
                .bind(last_url)
                .bind(primary_depot_id.map(|d| d as i64))
                .bind(&self.datasource_name)
                .bind(&xbox_product_id)
                .fetch_one(&mut **tx)
                .await?;
                (row.get::<i64, _>("Id"), true)
            };

            // Convert NaiveDateTime to proper UTC DateTime for PostgreSQL timestamptz columns
            let last_utc_dt = Utc.from_utc_datetime(&last_timestamp);
            let last_local_dt = Utc.from_utc_datetime(&self.utc_to_local(last_timestamp));

            // Only update if we found existing download (not if we just created it).
            // XboxProductId is COALESCE'd in so a matched Xbox session that adopted a still-NULL
            // row gets its product id named in this batch (same pattern as GameName).
            if !is_new {
                sqlx::query(
                    "UPDATE \"Downloads\" SET \"EndTimeUtc\" = $1, \"EndTimeLocal\" = $2, \"CacheHitBytes\" = \"CacheHitBytes\" + $3, \"CacheMissBytes\" = \"CacheMissBytes\" + $4, \"LastUrl\" = $5, \"DepotId\" = COALESCE($6, \"DepotId\"), \"GameAppId\" = COALESCE($7, \"GameAppId\"), \"GameName\" = COALESCE($8, \"GameName\"), \"GameImageUrl\" = COALESCE($9, \"GameImageUrl\"), \"XboxProductId\" = COALESCE($10, \"XboxProductId\") WHERE \"Id\" = $11"
                )
                .bind(last_utc_dt)
                .bind(last_local_dt)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(last_url)
                .bind(primary_depot_id.map(|d| d as i64))
                .bind(game_app_id.map(|id| id as i64))
                .bind(&game_name)
                .bind(&game_image_url)
                .bind(&xbox_product_id)
                .bind(download_id)
                .execute(&mut **tx)
                .await?;
            }

            // Update client and service stats (for both new and existing downloads)
            sqlx::query(
                "UPDATE \"ClientStats\" SET \"TotalCacheHitBytes\" = \"TotalCacheHitBytes\" + $1, \"TotalCacheMissBytes\" = \"TotalCacheMissBytes\" + $2, \"LastActivityUtc\" = $3, \"LastActivityLocal\" = $4 WHERE \"ClientIp\" = $5"
            )
            .bind(total_hit_bytes)
            .bind(total_miss_bytes)
            .bind(last_utc_dt)
            .bind(last_local_dt)
            .bind(client_ip)
            .execute(&mut **tx)
            .await?;

            sqlx::query(
                "UPDATE \"ServiceStats\" SET \"TotalCacheHitBytes\" = \"TotalCacheHitBytes\" + $1, \"TotalCacheMissBytes\" = \"TotalCacheMissBytes\" + $2, \"LastActivityUtc\" = $3, \"LastActivityLocal\" = $4 WHERE \"Service\" = $5"
            )
            .bind(total_hit_bytes)
            .bind(total_miss_bytes)
            .bind(last_utc_dt)
            .bind(last_local_dt)
            .bind(service)
            .execute(&mut **tx)
            .await?;

            download_id
        };

        // Update session tracker
        self.session_tracker
            .update_session(session_key, last_timestamp);

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
    let start_position = args.start_position;
    let auto_map_depots = args.auto_map_depots == 1;
    let datasource_name = args
        .datasource_name
        .unwrap_or_else(|| "default".to_string());

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
    let positions = if args.positions_path.is_empty() {
        None
    } else {
        match load_positions(&args.positions_path) {
            Ok(map) => Some(map),
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
        start_position,
        auto_map_depots,
        datasource_name,
        positions,
        run_id,
    );

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

    fn test_pool() -> PgPool {
        sqlx::postgres::PgPoolOptions::new()
            .connect_lazy("postgres://postgres:password@127.0.0.1/lancache_test")
            .expect("create lazy test pool")
    }

    fn test_processor(
        log_dir: PathBuf,
        progress_path: PathBuf,
        positions: Option<HashMap<String, u64>>,
    ) -> Processor {
        Processor::new(
            test_pool(),
            log_dir,
            progress_path,
            0,
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
        let mut processor = test_processor(
            tmp.path().to_path_buf(),
            progress_path.clone(),
            Some(positions),
        );
        // Keep the fixture in the normal partial-terminal path without requiring a DB insert.
        processor.entries_saved.store(1, Ordering::Relaxed);

        let outcome = processor.process().await.expect("finish partial run");

        assert_eq!(outcome, ProcessingOutcome::Completed);
        let progress = read_progress(&progress_path);
        assert_eq!(progress["terminal_status"], "partial");
        assert_eq!(progress["source_positions"]["fallback-access.log"], 0);
        assert_eq!(progress["lines_parsed"], 2);
        assert_eq!(progress["files_with_errors"].as_array().unwrap().len(), 1);
    }

    #[tokio::test]
    async fn discovery_failure_writes_failed_terminal() {
        let tmp = tempfile::tempdir().expect("create fixture directory");
        let not_a_directory = tmp.path().join("access.log");
        std::fs::write(&not_a_directory, b"line\n").expect("write file path fixture");
        let progress_path = tmp.path().join("progress.json");
        let mut processor = test_processor(not_a_directory, progress_path.clone(), None);

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
        let processor = test_processor(tmp.path().to_path_buf(), tmp.path().to_path_buf(), None);

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
        let processor = test_processor(tmp.path().to_path_buf(), progress_path.clone(), None);

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
        let mut processor = test_processor(tmp.path().to_path_buf(), progress_path, None);
        let log_file = LogFile::from_path(log_path);
        let mut lines_to_skip = 0;
        let mut records_consumed = 0;

        let outcome = processor
            .process_single_file_with_cancel(
                &log_file,
                4,
                &mut lines_to_skip,
                &SourceKind::Fallback,
                &mut records_consumed,
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
mod log_entry_clamp_tests {
    use super::{
        clamp_chars, LOG_ENTRY_CLIENT_IP_MAX_CHARS, LOG_ENTRY_DATASOURCE_MAX_CHARS,
        LOG_ENTRY_HTTP_RANGE_MAX_CHARS, LOG_ENTRY_SERVICE_MAX_CHARS,
        LOG_ENTRY_URL_MAX_CHARS, LOG_ENTRY_VARCHAR_MAX_CHARS,
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
