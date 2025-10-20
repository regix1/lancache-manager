use anyhow::{Context, Result};
use chrono::{NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use rusqlite::{params, Connection, OptionalExtension, Transaction, TransactionBehavior};
use serde::Serialize;
use std::collections::HashMap;
use std::env;
use std::fs::File;
use std::io::{BufRead, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

mod log_discovery;
mod log_reader;
mod models;
mod parser;
mod service_utils;
mod session;

use log_discovery::{discover_log_files, LogFile};
use log_reader::LogFileReader;
use models::*;
use parser::LogParser;
use session::SessionTracker;

const BULK_BATCH_SIZE: usize = 2_000; // Smaller batches for more responsive cancellation
const SESSION_GAP_MINUTES: i64 = 5;
const CANCEL_CHECK_INTERVAL: usize = 1_000; // Check for cancellation every 1k lines

#[derive(Serialize)]
struct Progress {
    total_lines: u64,
    lines_parsed: u64,
    entries_saved: u64,
    percent_complete: f64,
    status: String,
    message: String,
    timestamp: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    warnings: Vec<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    errors: Vec<String>,
}


struct Processor {
    db_path: PathBuf,
    log_dir: PathBuf,
    log_base_name: String,
    progress_path: PathBuf,
    start_position: u64,
    parser: LogParser,
    session_tracker: SessionTracker,
    total_lines: AtomicU64,
    lines_parsed: AtomicU64,
    entries_saved: AtomicU64,
    cancel_flag: Arc<AtomicBool>,
    local_tz: Tz,
    auto_map_depots: bool,
    last_logged_percent: AtomicU64, // Store as integer (0-100) for atomic operations
    warnings: std::sync::Mutex<Vec<String>>,
    errors: std::sync::Mutex<Vec<String>>,
}

impl Processor {
    fn new(
        db_path: PathBuf,
        log_dir: PathBuf,
        log_base_name: String,
        progress_path: PathBuf,
        cancel_path: PathBuf,
        start_position: u64,
        auto_map_depots: bool,
    ) -> Self {
        let cancel_flag = Arc::new(AtomicBool::new(false));

        // Get timezone from environment variable (same as C# uses)
        let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
        let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);
        println!("Using timezone: {} (from TZ env var)", local_tz);
        println!("Auto-map depots: {}", auto_map_depots);

        // Spawn background thread to monitor cancel file
        let cancel_flag_clone = Arc::clone(&cancel_flag);
        let cancel_path_clone = cancel_path.clone();
        thread::spawn(move || {
            while !cancel_flag_clone.load(Ordering::Relaxed) {
                if cancel_path_clone.exists() {
                    println!("Cancellation detected by monitor thread!");
                    cancel_flag_clone.store(true, Ordering::Relaxed);
                    break;
                }
                thread::sleep(Duration::from_millis(50)); // Check every 50ms
            }
        });

        Self {
            db_path,
            log_dir,
            log_base_name,
            progress_path,
            start_position,
            parser: LogParser::new(local_tz),
            session_tracker: SessionTracker::new(Duration::from_secs(SESSION_GAP_MINUTES as u64 * 60)),
            total_lines: AtomicU64::new(0),
            lines_parsed: AtomicU64::new(0),
            entries_saved: AtomicU64::new(0),
            cancel_flag,
            local_tz,
            auto_map_depots,
            last_logged_percent: AtomicU64::new(0),
            warnings: std::sync::Mutex::new(Vec::new()),
            errors: std::sync::Mutex::new(Vec::new()),
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

    fn should_cancel(&self) -> bool {
        self.cancel_flag.load(Ordering::Relaxed)
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
                    let warning = format!("Skipping corrupted file {}: {}", log_file.path.display(), e);
                    eprintln!("WARNING: {}", warning);
                    eprintln!("  Continuing with remaining files...");
                    self.warnings.lock().unwrap().push(warning);
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

        let warnings = self.warnings.lock().unwrap().clone();
        let errors = self.errors.lock().unwrap().clone();

        let progress = Progress {
            total_lines: total,
            lines_parsed: parsed,
            entries_saved: saved,
            percent_complete: percent,
            status: status.to_string(),
            message: message.to_string(),
            timestamp: Utc::now().to_rfc3339(),
            warnings,
            errors,
        };

        let json = serde_json::to_string_pretty(&progress)?;

        // Retry file write with exponential backoff (up to 5 attempts)
        let mut retries = 0;
        let max_retries = 5;
        loop {
            match File::create(&self.progress_path) {
                Ok(mut file) => {
                    match file.write_all(json.as_bytes()).and_then(|_| file.flush()) {
                        Ok(_) => break,
                        Err(_) if retries < max_retries => {
                            retries += 1;
                            thread::sleep(Duration::from_millis(10 * retries));
                            continue;
                        }
                        Err(e) => return Err(e.into()),
                    }
                }
                Err(_) if retries < max_retries => {
                    retries += 1;
                    thread::sleep(Duration::from_millis(10 * retries));
                    continue;
                }
                Err(e) => return Err(e.into()),
            }
        }

        Ok(())
    }

    fn process(&mut self) -> Result<()> {
        println!("Starting log processing...");
        println!("Log directory: {}", self.log_dir.display());
        println!("Log base name: {}", self.log_base_name);

        // Discover all log files (access.log, access.log.1, access.log.2.gz, etc.)
        let log_files = discover_log_files(&self.log_dir, &self.log_base_name)?;

        if log_files.is_empty() {
            println!("No log files found matching pattern: {}", self.log_base_name);
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

        // Open database connection
        let mut conn = Connection::open(&self.db_path)?;
        conn.pragma_update(None, "journal_mode", "WAL")?;
        conn.pragma_update(None, "synchronous", "NORMAL")?;
        conn.pragma_update(None, "cache_size", 1000000)?;
        conn.pragma_update(None, "temp_store", "MEMORY")?;
        // Set busy timeout to 60 seconds to handle concurrent access from C# services
        conn.busy_timeout(Duration::from_secs(60))?;

        // LogEntries table already exists from C# migrations, use it for duplicate detection
        // Index IX_LogEntries_DuplicateCheck on (ClientIp, Service, Timestamp, Url, BytesServed) exists

        // Process each log file in order (oldest to newest)
        let mut lines_to_skip = self.start_position;

        for (file_index, log_file) in log_files.iter().enumerate() {
            println!("\nProcessing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

            // Try to process the file, but skip if it's corrupted (e.g., invalid gzip)
            let file_result = self.process_single_file(&mut conn, log_file, &mut lines_to_skip, total_lines);

            if let Err(e) = file_result {
                eprintln!("⚠ Warning: Skipping corrupted file {}: {}", log_file.path.display(), e);
                eprintln!("  Continuing with remaining files...");
                continue;
            }

            // Check for cancellation between files
            if self.should_cancel() {
                println!("Cancellation requested between files - stopping processing");
                self.write_progress("cancelled", "Processing cancelled by user")?;
                return Err(anyhow::anyhow!("Processing cancelled by user"));
            }
        }

        println!("\nAll files processed successfully!");
        self.write_progress("complete", "Log processing finished")?;

        Ok(())
    }

    /// Process a single log file
    fn process_single_file(
        &mut self,
        conn: &mut Connection,
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
                    self.lines_parsed.fetch_add(skipped, Ordering::Relaxed);
                    return Ok(());
                }
                skipped += 1;
                self.lines_parsed.fetch_add(1, Ordering::Relaxed);
            }

            *lines_to_skip = 0; // We've skipped enough, process remaining files normally
        }

        let mut batch = Vec::with_capacity(BULK_BATCH_SIZE);
        let mut line_buffer = String::with_capacity(2048);

        self.write_progress("processing", &format!("Reading {}...", log_file.path.display()))?;

        loop {
            // Check for cancellation
            if self.should_cancel() {
                println!("Cancellation requested - stopping processing");
                self.write_progress("cancelled", "Processing cancelled by user")?;
                return Err(anyhow::anyhow!("Processing cancelled by user"));
            }

            line_buffer.clear();
            let bytes_read = reader.read_line(&mut line_buffer)?;

            if bytes_read == 0 {
                // EOF - process remaining batch
                if !batch.is_empty() {
                    self.process_batch(conn, &batch)?;
                    batch.clear();
                }
                break;
            }

            self.lines_parsed.fetch_add(1, Ordering::Relaxed);

            // Check for cancellation every CANCEL_CHECK_INTERVAL lines
            if self.lines_parsed.load(Ordering::Relaxed) % CANCEL_CHECK_INTERVAL as u64 == 0 {
                if self.should_cancel() {
                    println!("Cancellation requested - stopping processing");
                    self.write_progress("cancelled", "Processing cancelled by user")?;
                    return Err(anyhow::anyhow!("Processing cancelled by user"));
                }
            }

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
                    if self.should_cancel() {
                        println!("Cancellation requested - stopping processing");
                        self.write_progress("cancelled", "Processing cancelled by user")?;
                        return Err(anyhow::anyhow!("Processing cancelled by user"));
                    }

                    self.process_batch(conn, &batch)?;
                    batch.clear();

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

    fn process_batch(&mut self, conn: &mut Connection, entries: &[LogEntry]) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }

        // Use IMMEDIATE transaction to get write lock immediately, avoiding "database is locked" errors
        let tx = conn.transaction_with_behavior(TransactionBehavior::Immediate)?;

        // Group entries by client_ip + service + depot_id to prevent different games from being merged
        let mut grouped: HashMap<String, Vec<&LogEntry>> = HashMap::new();
        for entry in entries {
            let depot_suffix = entry.depot_id.map_or("_nodepot".to_string(), |id| format!("_{}", id));
            let key = format!("{}_{}{}",  entry.client_ip, entry.service, depot_suffix);
            grouped.entry(key).or_insert_with(Vec::new).push(entry);
        }

        // Process each group and count actually inserted entries
        let mut total_inserted = 0u64;
        for (session_key, group_entries) in &grouped {
            // Check for cancellation between processing groups for faster response
            if self.should_cancel() {
                println!("Cancellation requested during batch processing - rolling back transaction");
                // Return error without committing, transaction will auto-rollback
                return Err(anyhow::anyhow!("Processing cancelled by user"));
            }
            total_inserted += self.process_session_group(&tx, session_key, group_entries)?;
        }

        tx.commit()?;

        // Only count entries that were actually inserted (not duplicates)
        self.entries_saved
            .fetch_add(total_inserted, Ordering::Relaxed);

        Ok(())
    }

    fn lookup_depot_mapping(&self, tx: &Transaction, depot_id: u32) -> Result<Option<(u32, Option<String>)>> {
        // Only use owner apps (IsOwner = true) - matches C# behavior
        // No fallback to non-owner apps to avoid incorrect mappings
        let result = tx.query_row(
            "SELECT AppId, AppName FROM SteamDepotMappings WHERE DepotId = ? AND IsOwner = 1 LIMIT 1",
            params![depot_id],
            |row| {
                let app_id: u32 = row.get(0)?;
                let app_name: Option<String> = row.get(1)?;
                Ok((app_id, app_name))
            }
        );

        match result {
            Ok(mapping) => Ok(Some(mapping)),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(e.into())
        }
    }

    fn process_session_group(
        &mut self,
        tx: &Transaction,
        session_key: &str,
        entries: &[&LogEntry],
    ) -> Result<u64> {
        if entries.is_empty() {
            return Ok(0);
        }

        // FIRST: Filter out duplicate entries before any processing
        let mut check_stmt = tx.prepare_cached(
            "SELECT 1 FROM LogEntries WHERE ClientIp = ? AND Service = ? AND Timestamp = ? AND Url = ? AND BytesServed = ? LIMIT 1"
        )?;

        let mut new_entries = Vec::new();
        let mut skipped = 0;

        for entry in entries {
            let timestamp_str = entry.timestamp.format("%Y-%m-%d %H:%M:%S").to_string();

            // Check if this entry already exists
            let exists = check_stmt.query_row(
                params![
                    &entry.client_ip,
                    &entry.service,
                    &timestamp_str,
                    &entry.url,
                    entry.bytes_served,
                ],
                |_| Ok(true)
            ).unwrap_or(false);

            if exists {
                skipped += 1;
            } else {
                new_entries.push(*entry);
            }
        }

        // If all entries were duplicates, skip all processing (silently)
        if new_entries.is_empty() {
            return Ok(0);
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

        // Lookup depot mappings only for live/background processing (auto_map_depots = true)
        // For manual processing from frontend, leave as NULL so step 5 can handle it
        let (game_app_id, game_name) = if self.auto_map_depots && service.to_lowercase() == "steam" {
            if let Some(depot_id) = primary_depot_id {
                match self.lookup_depot_mapping(tx, depot_id) {
                    Ok(Some((app_id, app_name))) => {
                        let game_display = app_name.as_ref().map(|n| n.as_str()).unwrap_or("Unknown");
                        println!("Mapped depot {} -> App {} ({})", depot_id, app_id, game_display);
                        (Some(app_id), app_name)
                    },
                    Ok(None) => {
                        (None, None)
                    },
                    Err(e) => {
                        println!("Warning: Failed to lookup depot mapping for {}: {}", depot_id, e);
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
            // Mark ALL old active sessions as inactive for this client/service
            // This immediately marks previous games as complete when a new game starts
            // Fixes the issue where old games stay active until the 30-second cleanup timeout
            tx.execute(
                "UPDATE Downloads SET IsActive = 0 WHERE ClientIp = ? AND Service = ? AND IsActive = 1",
                params![client_ip, service],
            )?;

            // Create new download session with depot mapping
            // Don't generate image URL here - let C# DatabaseService fetch it from Steam API
            // This ensures we get the correct URL including hash-based URLs for newer games
            let game_image_url: Option<String> = None;

            // Convert timestamps to both UTC and local timezone
            let first_utc = first_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
            let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
            let first_local = self.utc_to_local(first_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
            let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();

            tx.execute(
                "INSERT INTO Downloads (Service, ClientIp, StartTimeUtc, EndTimeUtc, StartTimeLocal, EndTimeLocal, CacheHitBytes, CacheMissBytes, IsActive, LastUrl, DepotId, GameAppId, GameName, GameImageUrl)
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?, ?)",
                params![
                    service,
                    client_ip,
                    first_utc,
                    last_utc,
                    first_local,
                    last_local,
                    total_hit_bytes,
                    total_miss_bytes,
                    last_url,
                    primary_depot_id,
                    game_app_id,
                    game_name,
                    game_image_url,
                ],
            )?;

            let download_id = tx.last_insert_rowid();

            // Update or create client stats
            let client_exists: bool = tx
                .query_row(
                    "SELECT COUNT(*) FROM ClientStats WHERE ClientIp = ?",
                    params![client_ip],
                    |row| row.get(0),
                )
                .map(|count: i64| count > 0)?;

            if client_exists {
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                tx.execute(
                    "UPDATE ClientStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivityUtc = ?, LastActivityLocal = ?, TotalDownloads = TotalDownloads + 1 WHERE ClientIp = ?",
                    params![total_hit_bytes, total_miss_bytes, last_utc, last_local, client_ip],
                )?;
            } else {
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                tx.execute(
                    "INSERT INTO ClientStats (ClientIp, TotalCacheHitBytes, TotalCacheMissBytes, LastActivityUtc, LastActivityLocal, TotalDownloads) VALUES (?, ?, ?, ?, ?, 1)",
                    params![client_ip, total_hit_bytes, total_miss_bytes, last_utc, last_local],
                )?;
            }

            // Update or create service stats
            let service_exists: bool = tx
                .query_row(
                    "SELECT COUNT(*) FROM ServiceStats WHERE Service = ?",
                    params![service],
                    |row| row.get(0),
                )
                .map(|count: i64| count > 0)?;

            if service_exists {
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                tx.execute(
                    "UPDATE ServiceStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivityUtc = ?, LastActivityLocal = ?, TotalDownloads = TotalDownloads + 1 WHERE Service = ?",
                    params![total_hit_bytes, total_miss_bytes, last_utc, last_local, service],
                )?;
            } else {
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                tx.execute(
                    "INSERT INTO ServiceStats (Service, TotalCacheHitBytes, TotalCacheMissBytes, LastActivityUtc, LastActivityLocal, TotalDownloads) VALUES (?, ?, ?, ?, ?, 1)",
                    params![service, total_hit_bytes, total_miss_bytes, last_utc, last_local],
                )?;
            }

            download_id
        } else {
            // Try to find existing active download for this specific depot
            let download_id_opt: Option<i64> = if let Some(depot_id) = primary_depot_id {
                tx.query_row(
                    "SELECT Id FROM Downloads WHERE ClientIp = ? AND Service = ? AND DepotId = ? AND IsActive = 1 ORDER BY StartTimeUtc DESC LIMIT 1",
                    params![client_ip, service, depot_id],
                    |row| row.get(0),
                )
                .optional()?
            } else {
                tx.query_row(
                    "SELECT Id FROM Downloads WHERE ClientIp = ? AND Service = ? AND DepotId IS NULL AND IsActive = 1 ORDER BY StartTimeUtc DESC LIMIT 1",
                    params![client_ip, service],
                    |row| row.get(0),
                )
                .optional()?
            };

            // If no active download found (e.g., cleanup service marked it complete), create a new one
            // Don't generate image URL here - let C# DatabaseService fetch it from Steam API
            // This ensures we get the correct URL including hash-based URLs for newer games
            let game_image_url: Option<String> = None;

            let (download_id, is_new) = if let Some(id) = download_id_opt {
                (id, false)
            } else {
                // Convert timestamps to both UTC and local timezone
                let first_utc = first_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let first_local = self.utc_to_local(first_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();

                tx.execute(
                    "INSERT INTO Downloads (ClientIp, Service, StartTimeUtc, EndTimeUtc, StartTimeLocal, EndTimeLocal, CacheHitBytes, CacheMissBytes, IsActive, GameAppId, GameName, GameImageUrl, LastUrl, DepotId) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    params![
                        client_ip,
                        service,
                        first_utc,
                        last_utc,
                        first_local,
                        last_local,
                        total_hit_bytes,
                        total_miss_bytes,
                        1, // IsActive
                        game_app_id,
                        game_name,
                        game_image_url,
                        last_url,
                        primary_depot_id,
                    ],
                )?;
                (tx.last_insert_rowid(), true)
            };

            // Only update if we found existing download (not if we just created it)
            if !is_new {
                // Convert timestamps to both UTC and local timezone
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();

                tx.execute(
                    "UPDATE Downloads SET EndTimeUtc = ?, EndTimeLocal = ?, CacheHitBytes = CacheHitBytes + ?, CacheMissBytes = CacheMissBytes + ?, LastUrl = ?, DepotId = COALESCE(?, DepotId), GameAppId = COALESCE(?, GameAppId), GameName = COALESCE(?, GameName), GameImageUrl = COALESCE(?, GameImageUrl) WHERE Id = ?",
                    params![
                        last_utc,
                        last_local,
                        total_hit_bytes,
                        total_miss_bytes,
                        last_url,
                        primary_depot_id,
                        game_app_id,
                        game_name,
                        game_image_url,
                        download_id,
                    ],
                )?;

                // Update client stats (without incrementing TotalDownloads)
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                tx.execute(
                    "UPDATE ClientStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivityUtc = ?, LastActivityLocal = ? WHERE ClientIp = ?",
                    params![total_hit_bytes, total_miss_bytes, last_utc, last_local, client_ip],
                )?;

                // Update service stats (without incrementing TotalDownloads)
                tx.execute(
                    "UPDATE ServiceStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivityUtc = ?, LastActivityLocal = ? WHERE Service = ?",
                    params![total_hit_bytes, total_miss_bytes, last_utc, last_local, service],
                )?;
            } else {
                // For new downloads, stats were already updated in the first branch, so update them here too
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                tx.execute(
                    "UPDATE ClientStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivityUtc = ?, LastActivityLocal = ? WHERE ClientIp = ?",
                    params![total_hit_bytes, total_miss_bytes, last_utc, last_local, client_ip],
                )?;
                tx.execute(
                    "UPDATE ServiceStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivityUtc = ?, LastActivityLocal = ? WHERE Service = ?",
                    params![total_hit_bytes, total_miss_bytes, last_utc, last_local, service],
                )?;
            }

            download_id
        };

        // Update session tracker
        self.session_tracker
            .update_session(session_key, last_timestamp);

        // Insert ONLY the new (non-duplicate) entries
        let mut insert_stmt = tx.prepare_cached(
            "INSERT INTO LogEntries (Timestamp, ClientIp, Service, Method, Url, StatusCode, BytesServed, CacheStatus, DepotId, DownloadId, CreatedAt)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        )?;

        let now = Utc::now().format("%Y-%m-%d %H:%M:%S").to_string();
        let inserted = new_entries.len();

        for entry in new_entries {
            let timestamp_str = entry.timestamp.format("%Y-%m-%d %H:%M:%S").to_string();

            insert_stmt.execute(params![
                timestamp_str,
                entry.client_ip,
                entry.service,
                "GET",
                entry.url,
                entry.status_code,
                entry.bytes_served,
                entry.cache_status,
                entry.depot_id,
                download_id,
                now,
            ])?;
        }

        // Log if duplicates were skipped
        if skipped > 0 {
            println!(
                "Skipped {} duplicate entries (inserted {}/{})",
                skipped,
                inserted,
                entries.len()
            );
        }

        Ok(inserted as u64)
    }
}

fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().collect();

    if args.len() < 6 {
        eprintln!(
            "Usage: {} <db_path> <log_dir> <progress_path> <start_position> <auto_map_depots>",
            args[0]
        );
        eprintln!("  db_path: Path to SQLite database");
        eprintln!("  log_dir: Directory containing log files (e.g., H:/logs)");
        eprintln!("  progress_path: Path to progress JSON file");
        eprintln!("  start_position: Line number to start from (0 for beginning)");
        eprintln!("  auto_map_depots: 1 for live/background processing (auto-map), 0 for manual processing");
        eprintln!("\nNote: Processor will discover all log files matching 'access.log*' pattern");
        eprintln!("      including rotated logs (access.log.1, access.log.2, etc.)");
        eprintln!("      and compressed logs (.gz, .zst)");
        std::process::exit(1);
    }

    let db_path = PathBuf::from(&args[1]);
    let log_dir = PathBuf::from(&args[2]);
    let progress_path = PathBuf::from(&args[3]);
    let start_position: u64 = args[4].parse().context("Invalid start_position")?;
    let auto_map_depots: bool = args[5].parse::<u8>().context("Invalid auto_map_depots")? == 1;

    // Log file base name (hardcoded for now, could be made configurable)
    let log_base_name = "access.log".to_string();

    // Create cancel marker path in same directory as progress file
    let mut cancel_path = progress_path.clone();
    cancel_path.set_file_name("cancel_processing.marker");

    // Delete cancel marker if it exists from previous run
    if cancel_path.exists() {
        let _ = std::fs::remove_file(&cancel_path);
    }

    let mut processor = Processor::new(
        db_path,
        log_dir,
        log_base_name,
        progress_path,
        cancel_path,
        start_position,
        auto_map_depots
    );
    processor.process()?;

    Ok(())
}