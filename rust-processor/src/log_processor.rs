use anyhow::Result;
use chrono::{NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use clap::Parser;
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::env;
use std::io::BufRead;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

mod db;
mod log_discovery;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;
mod session;

use progress_events::ProgressReporter;

/// Log processor utility - parses lancache access logs and stores in database
#[derive(clap::Parser, Debug)]
#[command(name = "log_processor")]
#[command(about = "Parses lancache access logs and stores entries in the database")]
struct Args {
    /// Path to PostgreSQL database URL (or use DATABASE_URL env var)
    db_path: String,

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

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,
}

use log_discovery::{discover_log_files, LogFile};
use log_reader::LogFileReader;
use models::*;
use parser::LogParser;
use session::SessionTracker;

const BULK_BATCH_SIZE: usize = 5_000;
const SESSION_GAP_MINUTES: i64 = 5;
const LINE_BUFFER_CAPACITY: usize = 1024;

/// Buffered log entry ready for bulk INSERT — owns its data to avoid lifetime issues across session groups
struct PendingLogEntry {
    timestamp: chrono::DateTime<Utc>,
    client_ip: String,
    service: String,
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
    total_lines: u64,
    lines_parsed: u64,
    entries_saved: u64,
    percent_complete: f64,
    status: String,
    message: String,
    timestamp: String,
    // NOTE: warnings/errors removed - they're only used for C# logging (stderr capture)
    // and are NOT displayed in UI. Keeping them caused unbounded memory growth.
}


struct Processor {
    pool: PgPool,
    log_dir: PathBuf,
    log_base_name: String,
    progress_path: PathBuf,
    start_position: u64,
    parser: LogParser,
    session_tracker: SessionTracker,
    total_lines: AtomicU64,
    lines_parsed: AtomicU64,
    entries_saved: AtomicU64,
    local_tz: Tz,
    auto_map_depots: bool,
    last_logged_percent: AtomicU64, // Store as integer (0-100) for atomic operations
    logged_depots: HashSet<u32>, // Track depots that have already been logged
    datasource_name: String,
    depot_map: HashMap<u32, (u32, Option<String>)>,
    skip_dedup: bool, // True when table is empty — skip duplicate checks for max speed
}

impl Processor {
    fn new(
        pool: PgPool,
        log_dir: PathBuf,
        log_base_name: String,
        progress_path: PathBuf,
        start_position: u64,
        auto_map_depots: bool,
        datasource_name: String,
        depot_map: HashMap<u32, (u32, Option<String>)>,
    ) -> Self {
        // Get timezone from environment variable (same as C# uses)
        let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
        let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);
        println!("Using timezone: {} (from TZ env var)", local_tz);
        println!("Auto-map depots: {}", auto_map_depots);
        println!("Datasource: {}", datasource_name);

        Self {
            pool,
            log_dir,
            log_base_name,
            progress_path,
            start_position,
            parser: LogParser::new(local_tz),
            session_tracker: SessionTracker::new(Duration::from_secs(SESSION_GAP_MINUTES as u64 * 60)),
            total_lines: AtomicU64::new(0),
            lines_parsed: AtomicU64::new(0),
            entries_saved: AtomicU64::new(0),
            local_tz,
            auto_map_depots,
            last_logged_percent: AtomicU64::new(0),
            logged_depots: HashSet::new(),
            datasource_name,
            depot_map,
            skip_dedup: false,
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

    /// Count total lines across all discovered log files
    fn count_lines_all_files(&self, log_files: &[LogFile]) -> Result<u64> {
        let mut total = 0u64;
        for log_file in log_files {
            // Try to count lines, but skip if file is corrupted
            let file_result = (|| -> Result<u64> {
                let mut reader = LogFileReader::open(&log_file.path)?;
                // Count lines using BufRead trait
                let lines = reader.as_buf_read().lines().count() as u64;
                Ok(lines)
            })();

            match file_result {
                Ok(lines) => {
                    total += lines;
                    println!("  {} has {} lines", log_file.path.display(), lines);
                }
                Err(e) => {
                    // Log to stderr - C# captures this for logging
                    eprintln!("WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                    eprintln!("  Continuing with remaining files...");
                    continue;
                }
            }
        }
        Ok(total)
    }

    fn write_progress(&self, status: &str, message: &str) -> Result<()> {
        let total = self.total_lines.load(Ordering::Relaxed);
        let parsed = self.lines_parsed.load(Ordering::Relaxed);
        let saved = self.entries_saved.load(Ordering::Relaxed);

        let percent = if total > 0 {
            (parsed as f64 / total as f64) * 100.0
        } else {
            0.0
        };

        let progress = Progress {
            total_lines: total,
            lines_parsed: parsed,
            entries_saved: saved,
            percent_complete: percent,
            status: status.to_string(),
            message: message.to_string(),
            timestamp: progress_utils::current_timestamp(),
        };

        // Use shared progress writing utility with retry logic
        progress_utils::write_progress_with_retry(&self.progress_path, &progress, 5)
    }

    async fn process(&mut self) -> Result<()> {
        println!("Starting log processing...");
        println!("Log directory: {}", self.log_dir.display());
        println!("Log base name: {}", self.log_base_name);

        // Discover all log files (access.log, access.log.1, access.log.2.gz, etc.)
        let log_files = discover_log_files(&self.log_dir, &self.log_base_name)?;

        if log_files.is_empty() {
            println!("No log files found matching pattern: {}", self.log_base_name);
            self.write_progress("completed", "No log files found")?;
            return Ok(());
        }

        println!("Found {} log file(s):", log_files.len());
        for log_file in &log_files {
            let compression_info = if log_file.is_compressed {
                " (compressed)"
            } else {
                ""
            };
            let rotation_info = match log_file.rotation_number {
                Some(num) => format!(" [rotation {}]", num),
                None => " [current]".to_string(),
            };
            println!("  - {}{}{}", log_file.path.display(), rotation_info, compression_info);
        }

        // Count total lines across all files
        println!("Counting lines in all log files...");
        let total_lines = self.count_lines_all_files(&log_files)?;
        self.total_lines.store(total_lines, Ordering::Relaxed);
        println!("Total lines across all files: {}", total_lines);

        self.write_progress("counting", &format!("Counted {} lines across {} file(s)", total_lines, log_files.len()))?;

        // Check if this is a fresh database — skip dedup for maximum speed
        if self.start_position == 0 {
            let is_empty: bool = sqlx::query_scalar(
                r#"SELECT NOT EXISTS(SELECT 1 FROM "LogEntries" LIMIT 1)"#
            )
            .fetch_one(&self.pool)
            .await
            .unwrap_or(false);
            if is_empty {
                println!("Fresh database detected — skipping duplicate checks for maximum speed");
                self.skip_dedup = true;
            }
        }

        // LogEntries table already exists from C# migrations
        // Index IX_LogEntries_DuplicateCheck on (ClientIp, Service, Timestamp, Url, BytesServed) exists

        // Process each log file in order (oldest to newest)
        let mut lines_to_skip = self.start_position;

        let mut files_with_errors = Vec::new();
        let mut files_processed = 0u64;

        for (file_index, log_file) in log_files.iter().enumerate() {
            println!("\nProcessing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

            let file_result = self.process_single_file(log_file, &mut lines_to_skip, total_lines).await;

            if let Err(e) = file_result {
                let error_str = format!("{}", e);
                // Classify the error: IO/decompression errors are "corrupted file",
                // database errors are infrastructure failures that should not be silenced
                let is_db_error = error_str.contains("error returned from database")
                    || error_str.contains("pool timed out")
                    || error_str.contains("connection refused")
                    || error_str.contains("operator does not exist");

                if is_db_error {
                    eprintln!("ERROR: Database error processing {}: {}", log_file.path.display(), e);
                    files_with_errors.push((log_file.path.display().to_string(), error_str));
                    // Database errors affect ALL files, no point continuing
                    break;
                } else {
                    eprintln!("⚠ Warning: Skipping corrupted file {}: {}", log_file.path.display(), e);
                    eprintln!("  Continuing with remaining files...");
                    files_with_errors.push((log_file.path.display().to_string(), error_str));
                    continue;
                }
            } else {
                files_processed += 1;
            }
        }

        let entries_saved = self.entries_saved.load(Ordering::Relaxed);

        // If we had errors and processed zero entries, this is a failure
        if !files_with_errors.is_empty() && entries_saved == 0 && total_lines > 0 {
            let error_summary: Vec<String> = files_with_errors
                .iter()
                .map(|(path, err)| format!("{}: {}", path, err))
                .collect();
            let msg = format!(
                "Log processing failed - 0 entries processed from {} lines. Errors: {}",
                total_lines,
                error_summary.join("; ")
            );
            eprintln!("{}", msg);
            self.write_progress("failed", &msg)?;
            return Err(anyhow::anyhow!(msg));
        }

        if files_with_errors.is_empty() {
            println!("\nAll files processed successfully!");
        } else {
            println!(
                "\nProcessing completed with {} error(s) in {} file(s), {} entries saved",
                files_with_errors.len(),
                files_with_errors.len(),
                entries_saved
            );
        }
        self.write_progress("completed", "Log processing finished")?;

        Ok(())
    }

    /// Process a single log file
    async fn process_single_file(
        &mut self,
        log_file: &LogFile,
        lines_to_skip: &mut u64,
        total_lines: u64,
    ) -> Result<()> {
        // Open log file with automatic compression detection
        let mut reader = LogFileReader::open(&log_file.path)?;

        // Skip lines if we haven't reached the start position yet
        if *lines_to_skip > 0 {
            println!("Skipping {} lines in this file to reach start position", lines_to_skip);
            let mut line = String::new();
            let mut skipped = 0u64;

            while skipped < *lines_to_skip {
                line.clear();
                let bytes_read = reader.read_line(&mut line)?;
                if bytes_read == 0 {
                    // Reached EOF before skipping all lines - this file is exhausted
                    *lines_to_skip -= skipped;
                    return Ok(());
                }
                skipped += 1;
                self.lines_parsed.fetch_add(1, Ordering::Relaxed);
            }

            *lines_to_skip = 0; // We've skipped enough, process remaining files normally
        }

        let mut batch = Vec::with_capacity(BULK_BATCH_SIZE);
        let mut line_buffer = String::with_capacity(LINE_BUFFER_CAPACITY);

        self.write_progress("processing", &format!("Reading {}...", log_file.path.display()))?;

        loop {
            line_buffer.clear();
            let bytes_read = reader.read_line(&mut line_buffer)?;

            if bytes_read == 0 {
                // EOF - process remaining batch
                if !batch.is_empty() {
                    self.process_batch(&batch).await?;
                    batch.clear();
                    batch.shrink_to_fit(); // Release memory since we're done
                }
                break;
            }

            self.lines_parsed.fetch_add(1, Ordering::Relaxed);

            // Parse the line (trim to remove newline)
            let trimmed_line = line_buffer.trim();
            if let Some(entry) = self.parser.parse_line(trimmed_line) {
                // Skip health check/heartbeat endpoints
                if service_utils::should_skip_url(&entry.url) {
                    continue;
                }

                batch.push(entry);

                // Process batch when it reaches BULK_BATCH_SIZE
                if batch.len() >= BULK_BATCH_SIZE {
                    self.process_batch(&batch).await?;
                    batch.clear();
                    // Don't shrink here - we'll reuse the capacity for the next batch

                    let parsed = self.lines_parsed.load(Ordering::Relaxed);
                    let saved = self.entries_saved.load(Ordering::Relaxed);
                    let percent = (parsed as f64 / total_lines as f64) * 100.0;
                    let current_percent_bucket = (percent / 5.0).floor() as u64 * 5; // Round down to nearest 5%
                    let last_logged = self.last_logged_percent.load(Ordering::Relaxed);

                    // Only log when we cross a 5% boundary
                    if current_percent_bucket > last_logged {
                        self.last_logged_percent.store(current_percent_bucket, Ordering::Relaxed);
                        println!(
                            "Progress: {}/{} lines ({:.1}%), {} entries saved",
                            parsed, total_lines, percent, saved
                        );
                    }

                    self.write_progress(
                        "processing",
                        &format!("{} lines parsed, {} entries saved", parsed, saved),
                    )?;
                }
            }
        }

        Ok(())
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

        // Group entries by client_ip + service + depot_id to prevent different games from being merged
        // For Epic services without a depot_id, use the URL path prefix as a discriminator
        // so different Epic games get separate sessions instead of being merged into one
        let mut grouped: HashMap<String, Vec<&LogEntry>> = HashMap::new();
        for entry in entries {
            let depot_suffix = if let Some(id) = entry.depot_id {
                format!("_{}", id)
            } else if entry.service.to_lowercase().contains("epic") {
                // For Epic entries, use the CDN path prefix (e.g., /Builds/Org/o-xxx/hash/default)
                // as the session discriminator to keep different games in separate sessions
                Self::extract_epic_path_prefix(&entry.url)
                    .map(|prefix| format!("_epic:{}", prefix))
                    .unwrap_or_else(|| "_nodepot".to_string())
            } else {
                "_nodepot".to_string()
            };
            let key = format!("{}_{}{}",  entry.client_ip, entry.service, depot_suffix);
            grouped.entry(key).or_insert_with(Vec::new).push(entry);
        }

        // Process each group (Downloads, stats, session tracking)
        // Collect entries to insert into a shared buffer for ONE bulk INSERT
        let mut pending_inserts: Vec<PendingLogEntry> = Vec::with_capacity(entries.len());
        for (session_key, group_entries) in &grouped {
            self.process_session_group(&mut tx, session_key, group_entries, &mut pending_inserts).await?;
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

    fn lookup_depot_mapping(&self, depot_id: u32) -> Option<(u32, Option<String>)> {
        self.depot_map.get(&depot_id).cloned()
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

        // Duplicate detection — skip on fresh database for maximum speed
        let (new_entries, skipped): (Vec<&LogEntry>, usize) = if self.skip_dedup {
            // Fresh database — all entries are new, no dedup needed
            (entries.iter().map(|e| *e).collect(), 0)
        } else {
            // Bulk duplicate detection — single query for the whole group
            let mut check_client_ips: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_services: Vec<&str> = Vec::with_capacity(entries.len());
            let mut check_timestamps: Vec<chrono::DateTime<Utc>> = Vec::with_capacity(entries.len());
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
                    (client_ip, service, ts.timestamp_nanos_opt().unwrap_or(0), url, bytes)
                })
                .collect();

            let mut new_vec: Vec<&LogEntry> = Vec::with_capacity(entries.len());
            let mut skip_count = 0usize;

            for entry in entries {
                let ts_nanos = Utc.from_utc_datetime(&entry.timestamp).timestamp_nanos_opt().unwrap_or(0);
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

        let last_url = new_entries.last().map(|e| e.url.as_str());

        // Lookup depot mappings during log processing (auto_map_depots = true)
        // This ensures Downloads have GameAppId/GameName set immediately, avoiding "Unknown Game" in UI
        let (game_app_id, game_name) = if self.auto_map_depots && service.to_lowercase() == "steam" {
            if let Some(depot_id) = primary_depot_id {
                match self.lookup_depot_mapping(depot_id) {
                    Some((app_id, app_name)) => {
                        // Only log each depot mapping once to avoid log spam
                        if !self.logged_depots.contains(&depot_id) {
                            let game_display = app_name.as_ref().map(|n| n.as_str()).unwrap_or("Unknown");
                            println!("Mapped depot {} -> App {} ({})", depot_id, app_id, game_display);
                            self.logged_depots.insert(depot_id);
                        }
                        (Some(app_id), app_name)
                    },
                    None => {
                        (None, None)
                    },
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
            // Mark ALL old active sessions as inactive for this client/service
            sqlx::query(
                "UPDATE \"Downloads\" SET \"IsActive\" = false WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"IsActive\" = true"
            )
            .bind(client_ip)
            .bind(service)
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
                "INSERT INTO \"Downloads\" (\"Service\", \"ClientIp\", \"StartTimeUtc\", \"EndTimeUtc\", \"StartTimeLocal\", \"EndTimeLocal\", \"CacheHitBytes\", \"CacheMissBytes\", \"IsActive\", \"LastUrl\", \"DepotId\", \"GameAppId\", \"GameName\", \"GameImageUrl\", \"Datasource\")
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, true, $9, $10, $11, $12, $13, $14)
                 RETURNING \"Id\""
            )
            .bind(service)
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
            .fetch_one(&mut **tx)
            .await?;

            let download_id: i64 = row.get("Id");

            // Upsert client stats — no pre-check SELECT needed
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

            // Upsert service stats — no pre-check SELECT needed
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
            let download_id_opt: Option<i64> = if let Some(depot_id) = primary_depot_id {
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
                if let Some(path_prefix) = last_url.and_then(|u| Self::extract_epic_path_prefix(u)) {
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
                    "INSERT INTO \"Downloads\" (\"ClientIp\", \"Service\", \"StartTimeUtc\", \"EndTimeUtc\", \"StartTimeLocal\", \"EndTimeLocal\", \"CacheHitBytes\", \"CacheMissBytes\", \"IsActive\", \"GameAppId\", \"GameName\", \"GameImageUrl\", \"LastUrl\", \"DepotId\", \"Datasource\") VALUES ($1, $2, $3, $4, $5, $6, $7, $8, true, $9, $10, $11, $12, $13, $14) RETURNING \"Id\""
                )
                .bind(client_ip)
                .bind(service)
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
                .fetch_one(&mut **tx)
                .await?;
                (row.get::<i64, _>("Id"), true)
            };

            // Convert NaiveDateTime to proper UTC DateTime for PostgreSQL timestamptz columns
            let last_utc_dt = Utc.from_utc_datetime(&last_timestamp);
            let last_local_dt = Utc.from_utc_datetime(&self.utc_to_local(last_timestamp));

            // Only update if we found existing download (not if we just created it)
            if !is_new {
                sqlx::query(
                    "UPDATE \"Downloads\" SET \"EndTimeUtc\" = $1, \"EndTimeLocal\" = $2, \"CacheHitBytes\" = \"CacheHitBytes\" + $3, \"CacheMissBytes\" = \"CacheMissBytes\" + $4, \"LastUrl\" = $5, \"DepotId\" = COALESCE($6, \"DepotId\"), \"GameAppId\" = COALESCE($7, \"GameAppId\"), \"GameName\" = COALESCE($8, \"GameName\"), \"GameImageUrl\" = COALESCE($9, \"GameImageUrl\") WHERE \"Id\" = $10"
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

        // Push entries to pending buffer — will be bulk-inserted by process_batch
        let now = Utc::now();
        for entry in &new_entries {
            pending_inserts.push(PendingLogEntry {
                timestamp: Utc.from_utc_datetime(&entry.timestamp),
                client_ip: entry.client_ip.clone(),
                service: entry.service.clone(),
                url: entry.url.clone(),
                status_code: entry.status_code,
                bytes_served: entry.bytes_served,
                cache_status: entry.cache_status.clone(),
                depot_id: entry.depot_id.map(|d| d as i64),
                download_id,
                created_at: now,
                datasource: self.datasource_name.clone(),
            });
        }

        if skipped > 0 {
            println!(
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
            method_vec.push("GET");
            url_vec.push(&entry.url);
            status_code_vec.push(entry.status_code);
            bytes_served_vec.push(entry.bytes_served);
            cache_status_vec.push(&entry.cache_status);
            depot_id_vec.push(entry.depot_id);
            download_id_vec.push(entry.download_id);
            created_at_vec.push(&entry.created_at);
            datasource_vec.push(&entry.datasource);
        }

        // PostgreSQL parameter limit is 65535; 12 columns → max 5461 rows per call
        const MAX_ROWS: usize = 5000;
        let n = entries.len();
        let mut offset = 0usize;
        while offset < n {
            let end = std::cmp::min(offset + MAX_ROWS, n);
            sqlx::query(
                r#"INSERT INTO "LogEntries" ("Timestamp", "ClientIp", "Service", "Method", "Url", "StatusCode", "BytesServed", "CacheStatus", "DepotId", "DownloadId", "CreatedAt", "Datasource")
                   SELECT * FROM UNNEST($1::timestamptz[], $2::text[], $3::text[], $4::text[], $5::text[], $6::int[], $7::bigint[], $8::text[], $9::bigint[], $10::bigint[], $11::timestamptz[], $12::text[])"#
            )
            .bind(&ts_vec[offset..end])
            .bind(&client_ip_vec[offset..end])
            .bind(&service_vec[offset..end])
            .bind(&method_vec[offset..end])
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

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let log_dir = PathBuf::from(&args.log_dir);
    let progress_path = PathBuf::from(&args.progress_path);
    let start_position = args.start_position;
    let auto_map_depots = args.auto_map_depots == 1;
    let datasource_name = args.datasource_name.unwrap_or_else(|| "default".to_string());

    // Log file base name (hardcoded for now, could be made configurable)
    let log_base_name = "access.log".to_string();

    // Emit started event
    reporter.emit_started();
    reporter.emit_progress(0.0, "Starting log processing");

    let pool = db::create_pool().await;

    // Pre-load all SteamDepotMappings into a HashMap so no per-session DB lookups are needed.
    // Only owner apps (IsOwner = true) — matches prior C# behavior.
    let depot_map: HashMap<u32, (u32, Option<String>)> = if auto_map_depots {
        println!("Pre-loading Steam depot mappings...");
        let rows = sqlx::query(
            r#"SELECT "DepotId", "AppId", "AppName" FROM "SteamDepotMappings" WHERE "IsOwner" = true"#
        )
        .fetch_all(&pool)
        .await
        .unwrap_or_else(|e| {
            eprintln!("Warning: Failed to load depot mappings: {}", e);
            vec![]
        });
        let map: HashMap<u32, (u32, Option<String>)> = rows
            .into_iter()
            .map(|row| {
                let depot_id: i64 = row.get("DepotId");
                let app_id: i64 = row.get("AppId");
                let app_name: Option<String> = row.get("AppName");
                (depot_id as u32, (app_id as u32, app_name))
            })
            .collect();
        println!("Loaded {} depot mappings", map.len());
        map
    } else {
        HashMap::new()
    };

    let mut processor = Processor::new(
        pool,
        log_dir,
        log_base_name,
        progress_path,
        start_position,
        auto_map_depots,
        datasource_name,
        depot_map,
    );

    match processor.process().await {
        Ok(()) => {
            reporter.emit_complete("Log processing completed successfully");
            Ok(())
        }
        Err(e) => {
            reporter.emit_failed(&format!("Log processing failed: {}", e));
            Err(e)
        }
    }
}
