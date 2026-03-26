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

const BULK_BATCH_SIZE: usize = 1_000; // Reduced from 2000 to lower memory spikes
const SESSION_GAP_MINUTES: i64 = 5;
const LINE_BUFFER_CAPACITY: usize = 1024; // Reduced from 2048 for better memory efficiency

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
    datasource_name: String, // Datasource identifier for multi-datasource support
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

        // LogEntries table already exists from C# migrations, use it for duplicate detection
        // Index IX_LogEntries_DuplicateCheck on (ClientIp, Service, Timestamp, Url, BytesServed) exists

        // Process each log file in order (oldest to newest)
        let mut lines_to_skip = self.start_position;

        for (file_index, log_file) in log_files.iter().enumerate() {
            println!("\nProcessing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

            // Try to process the file, but skip if it's corrupted (e.g., invalid gzip)
            let file_result = self.process_single_file(log_file, &mut lines_to_skip, total_lines).await;

            if let Err(e) = file_result {
                eprintln!("⚠ Warning: Skipping corrupted file {}: {}", log_file.path.display(), e);
                eprintln!("  Continuing with remaining files...");
                continue;
            }
        }

        println!("\nAll files processed successfully!");
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

        // Process each group and count actually inserted entries
        let mut total_inserted = 0u64;
        for (session_key, group_entries) in &grouped {
            total_inserted += self.process_session_group(&mut tx, session_key, group_entries).await?;
        }

        tx.commit().await?;

        // Only count entries that were actually inserted (not duplicates)
        self.entries_saved
            .fetch_add(total_inserted, Ordering::Relaxed);

        Ok(())
    }

    async fn lookup_depot_mapping(&self, tx: &mut sqlx::Transaction<'_, sqlx::Postgres>, depot_id: u32) -> Result<Option<(u32, Option<String>)>> {
        // Only use owner apps (IsOwner = true) - matches C# behavior
        // No fallback to non-owner apps to avoid incorrect mappings
        let row = sqlx::query(
            "SELECT \"AppId\", \"AppName\" FROM \"SteamDepotMappings\" WHERE \"DepotId\" = $1 AND \"IsOwner\" = true LIMIT 1"
        )
        .bind(depot_id as i32)
        .fetch_optional(&mut **tx)
        .await?;

        match row {
            Some(r) => {
                let app_id: i32 = r.get("AppId");
                let app_name: Option<String> = r.get("AppName");
                Ok(Some((app_id as u32, app_name)))
            }
            None => Ok(None),
        }
    }

    async fn process_session_group(
        &mut self,
        tx: &mut sqlx::Transaction<'_, sqlx::Postgres>,
        session_key: &str,
        entries: &[&LogEntry],
    ) -> Result<u64> {
        if entries.is_empty() {
            return Ok(0);
        }

        // Simple duplicate detection
        let mut new_entries = Vec::with_capacity(entries.len());
        let mut skipped = 0;

        for entry in entries {
            let timestamp_str = entry.timestamp.format("%Y-%m-%d %H:%M:%S").to_string();

            let exists = sqlx::query(
                "SELECT 1 FROM \"LogEntries\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"Timestamp\" = $3 AND \"Url\" = $4 AND \"BytesServed\" = $5 LIMIT 1"
            )
            .bind(&entry.client_ip)
            .bind(&entry.service)
            .bind(&timestamp_str)
            .bind(&entry.url)
            .bind(entry.bytes_served)
            .fetch_optional(&mut **tx)
            .await?
            .is_some();

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

        // Lookup depot mappings during log processing (auto_map_depots = true)
        // This ensures Downloads have GameAppId/GameName set immediately, avoiding "Unknown Game" in UI
        let (game_app_id, game_name) = if self.auto_map_depots && service.to_lowercase() == "steam" {
            if let Some(depot_id) = primary_depot_id {
                match self.lookup_depot_mapping(tx, depot_id).await {
                    Ok(Some((app_id, app_name))) => {
                        // Only log each depot mapping once to avoid log spam
                        if !self.logged_depots.contains(&depot_id) {
                            let game_display = app_name.as_ref().map(|n| n.as_str()).unwrap_or("Unknown");
                            println!("Mapped depot {} -> App {} ({})", depot_id, app_id, game_display);
                            self.logged_depots.insert(depot_id);
                        }
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
            sqlx::query(
                "UPDATE \"Downloads\" SET \"IsActive\" = false WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"IsActive\" = true"
            )
            .bind(client_ip)
            .bind(service)
            .execute(&mut **tx)
            .await?;

            // Create new download session with depot mapping
            let game_image_url: Option<String> = None;

            // Convert timestamps to both UTC and local timezone - format once and reuse
            let first_utc = first_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
            let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
            let first_local = self.utc_to_local(first_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
            let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();

            let row = sqlx::query(
                "INSERT INTO \"Downloads\" (\"Service\", \"ClientIp\", \"StartTimeUtc\", \"EndTimeUtc\", \"StartTimeLocal\", \"EndTimeLocal\", \"CacheHitBytes\", \"CacheMissBytes\", \"IsActive\", \"LastUrl\", \"DepotId\", \"GameAppId\", \"GameName\", \"GameImageUrl\", \"Datasource\")
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, true, $9, $10, $11, $12, $13, $14)
                 RETURNING \"Id\""
            )
            .bind(service)
            .bind(client_ip)
            .bind(&first_utc)
            .bind(&last_utc)
            .bind(&first_local)
            .bind(&last_local)
            .bind(total_hit_bytes)
            .bind(total_miss_bytes)
            .bind(last_url)
            .bind(primary_depot_id.map(|d| d as i32))
            .bind(game_app_id.map(|id| id as i32))
            .bind(&game_name)
            .bind(&game_image_url)
            .bind(&self.datasource_name)
            .fetch_one(&mut **tx)
            .await?;

            let download_id: i32 = row.get("Id");

            // Update or create client stats
            let client_count: i64 = sqlx::query_scalar(
                "SELECT COUNT(*) FROM \"ClientStats\" WHERE \"ClientIp\" = $1"
            )
            .bind(client_ip)
            .fetch_one(&mut **tx)
            .await?;

            if client_count > 0 {
                sqlx::query(
                    "UPDATE \"ClientStats\" SET \"TotalCacheHitBytes\" = \"TotalCacheHitBytes\" + $1, \"TotalCacheMissBytes\" = \"TotalCacheMissBytes\" + $2, \"LastActivityUtc\" = $3, \"LastActivityLocal\" = $4, \"TotalDownloads\" = \"TotalDownloads\" + 1 WHERE \"ClientIp\" = $5"
                )
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(&last_utc)
                .bind(&last_local)
                .bind(client_ip)
                .execute(&mut **tx)
                .await?;
            } else {
                sqlx::query(
                    "INSERT INTO \"ClientStats\" (\"ClientIp\", \"TotalCacheHitBytes\", \"TotalCacheMissBytes\", \"LastActivityUtc\", \"LastActivityLocal\", \"TotalDownloads\") VALUES ($1, $2, $3, $4, $5, 1)"
                )
                .bind(client_ip)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(&last_utc)
                .bind(&last_local)
                .execute(&mut **tx)
                .await?;
            }

            // Update or create service stats
            let service_count: i64 = sqlx::query_scalar(
                "SELECT COUNT(*) FROM \"ServiceStats\" WHERE \"Service\" = $1"
            )
            .bind(service)
            .fetch_one(&mut **tx)
            .await?;

            if service_count > 0 {
                sqlx::query(
                    "UPDATE \"ServiceStats\" SET \"TotalCacheHitBytes\" = \"TotalCacheHitBytes\" + $1, \"TotalCacheMissBytes\" = \"TotalCacheMissBytes\" + $2, \"LastActivityUtc\" = $3, \"LastActivityLocal\" = $4, \"TotalDownloads\" = \"TotalDownloads\" + 1 WHERE \"Service\" = $5"
                )
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(&last_utc)
                .bind(&last_local)
                .bind(service)
                .execute(&mut **tx)
                .await?;
            } else {
                sqlx::query(
                    "INSERT INTO \"ServiceStats\" (\"Service\", \"TotalCacheHitBytes\", \"TotalCacheMissBytes\", \"LastActivityUtc\", \"LastActivityLocal\", \"TotalDownloads\") VALUES ($1, $2, $3, $4, $5, 1)"
                )
                .bind(service)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(&last_utc)
                .bind(&last_local)
                .execute(&mut **tx)
                .await?;
            }

            download_id
        } else {
            // Try to find existing active download for this specific depot/game
            let download_id_opt: Option<i32> = if let Some(depot_id) = primary_depot_id {
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" = $3 AND \"IsActive\" = true ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .bind(depot_id as i32)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i32, _>("Id"))
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
                    .map(|r| r.get::<i32, _>("Id"))
                } else {
                    sqlx::query(
                        "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                    )
                    .bind(client_ip)
                    .bind(service)
                    .fetch_optional(&mut **tx)
                    .await?
                    .map(|r| r.get::<i32, _>("Id"))
                }
            } else {
                sqlx::query(
                    "SELECT \"Id\" FROM \"Downloads\" WHERE \"ClientIp\" = $1 AND \"Service\" = $2 AND \"DepotId\" IS NULL AND \"IsActive\" = true ORDER BY \"StartTimeUtc\" DESC LIMIT 1"
                )
                .bind(client_ip)
                .bind(service)
                .fetch_optional(&mut **tx)
                .await?
                .map(|r| r.get::<i32, _>("Id"))
            };

            let game_image_url: Option<String> = None;

            let (download_id, is_new) = if let Some(id) = download_id_opt {
                (id, false)
            } else {
                // Convert timestamps to both UTC and local timezone
                let first_utc = first_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
                let first_local = self.utc_to_local(first_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();
                let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();

                let row = sqlx::query(
                    "INSERT INTO \"Downloads\" (\"ClientIp\", \"Service\", \"StartTimeUtc\", \"EndTimeUtc\", \"StartTimeLocal\", \"EndTimeLocal\", \"CacheHitBytes\", \"CacheMissBytes\", \"IsActive\", \"GameAppId\", \"GameName\", \"GameImageUrl\", \"LastUrl\", \"DepotId\", \"Datasource\") VALUES ($1, $2, $3, $4, $5, $6, $7, $8, true, $9, $10, $11, $12, $13, $14) RETURNING \"Id\""
                )
                .bind(client_ip)
                .bind(service)
                .bind(&first_utc)
                .bind(&last_utc)
                .bind(&first_local)
                .bind(&last_local)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(game_app_id.map(|id| id as i32))
                .bind(&game_name)
                .bind(&game_image_url)
                .bind(last_url)
                .bind(primary_depot_id.map(|d| d as i32))
                .bind(&self.datasource_name)
                .fetch_one(&mut **tx)
                .await?;
                (row.get::<i32, _>("Id"), true)
            };

            // Convert timestamps once for reuse in updates
            let last_utc = last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string();
            let last_local = self.utc_to_local(last_timestamp).format("%Y-%m-%d %H:%M:%S").to_string();

            // Only update if we found existing download (not if we just created it)
            if !is_new {
                sqlx::query(
                    "UPDATE \"Downloads\" SET \"EndTimeUtc\" = $1, \"EndTimeLocal\" = $2, \"CacheHitBytes\" = \"CacheHitBytes\" + $3, \"CacheMissBytes\" = \"CacheMissBytes\" + $4, \"LastUrl\" = $5, \"DepotId\" = COALESCE($6, \"DepotId\"), \"GameAppId\" = COALESCE($7, \"GameAppId\"), \"GameName\" = COALESCE($8, \"GameName\"), \"GameImageUrl\" = COALESCE($9, \"GameImageUrl\") WHERE \"Id\" = $10"
                )
                .bind(&last_utc)
                .bind(&last_local)
                .bind(total_hit_bytes)
                .bind(total_miss_bytes)
                .bind(last_url)
                .bind(primary_depot_id.map(|d| d as i32))
                .bind(game_app_id.map(|id| id as i32))
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
            .bind(&last_utc)
            .bind(&last_local)
            .bind(client_ip)
            .execute(&mut **tx)
            .await?;

            sqlx::query(
                "UPDATE \"ServiceStats\" SET \"TotalCacheHitBytes\" = \"TotalCacheHitBytes\" + $1, \"TotalCacheMissBytes\" = \"TotalCacheMissBytes\" + $2, \"LastActivityUtc\" = $3, \"LastActivityLocal\" = $4 WHERE \"Service\" = $5"
            )
            .bind(total_hit_bytes)
            .bind(total_miss_bytes)
            .bind(&last_utc)
            .bind(&last_local)
            .bind(service)
            .execute(&mut **tx)
            .await?;

            download_id
        };

        // Update session tracker
        self.session_tracker
            .update_session(session_key, last_timestamp);

        // Insert ONLY the new (non-duplicate) entries
        let now = Utc::now().format("%Y-%m-%d %H:%M:%S").to_string();
        let inserted = new_entries.len();

        for entry in &new_entries {
            let timestamp_str = entry.timestamp.format("%Y-%m-%d %H:%M:%S").to_string();

            sqlx::query(
                "INSERT INTO \"LogEntries\" (\"Timestamp\", \"ClientIp\", \"Service\", \"Method\", \"Url\", \"StatusCode\", \"BytesServed\", \"CacheStatus\", \"DepotId\", \"DownloadId\", \"CreatedAt\", \"Datasource\")
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)"
            )
            .bind(&timestamp_str)
            .bind(&entry.client_ip)
            .bind(&entry.service)
            .bind("GET")
            .bind(&entry.url)
            .bind(entry.status_code as i32)
            .bind(entry.bytes_served)
            .bind(&entry.cache_status)
            .bind(entry.depot_id.map(|d| d as i32))
            .bind(download_id)
            .bind(&now)
            .bind(&self.datasource_name)
            .execute(&mut **tx)
            .await?;
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

    let mut processor = Processor::new(
        pool,
        log_dir,
        log_base_name,
        progress_path,
        start_position,
        auto_map_depots,
        datasource_name,
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
