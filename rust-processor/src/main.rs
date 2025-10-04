use anyhow::{Context, Result};
use chrono::Utc;
use rusqlite::{params, Connection, Transaction};
use serde::Serialize;
use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

#[cfg(target_os = "windows")]
use std::os::windows::fs::OpenOptionsExt;

mod models;
mod parser;
mod session;

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
}

/// Opens a file for reading with proper sharing on Windows
/// This allows other processes (like lancache) to continue writing while we read
fn open_shared_read(path: &Path) -> Result<File> {
    #[cfg(target_os = "windows")]
    {
        // On Windows, use share_mode to allow other processes to read and write
        // FILE_SHARE_READ (0x01) | FILE_SHARE_WRITE (0x02) = 0x03
        OpenOptions::new()
            .read(true)
            .share_mode(0x03)
            .open(path)
            .with_context(|| format!("Failed to open file: {}", path.display()))
    }

    #[cfg(not(target_os = "windows"))]
    {
        // On Unix, File::open already allows sharing
        File::open(path)
            .with_context(|| format!("Failed to open file: {}", path.display()))
    }
}

struct Processor {
    db_path: PathBuf,
    log_path: PathBuf,
    progress_path: PathBuf,
    start_position: u64,
    parser: LogParser,
    session_tracker: SessionTracker,
    total_lines: AtomicU64,
    lines_parsed: AtomicU64,
    entries_saved: AtomicU64,
    cancel_flag: Arc<AtomicBool>,
}

impl Processor {
    fn new(
        db_path: PathBuf,
        log_path: PathBuf,
        progress_path: PathBuf,
        cancel_path: PathBuf,
        start_position: u64,
    ) -> Self {
        let cancel_flag = Arc::new(AtomicBool::new(false));

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
            log_path,
            progress_path,
            start_position,
            parser: LogParser::new(),
            session_tracker: SessionTracker::new(Duration::from_secs(SESSION_GAP_MINUTES as u64 * 60)),
            total_lines: AtomicU64::new(0),
            lines_parsed: AtomicU64::new(0),
            entries_saved: AtomicU64::new(0),
            cancel_flag,
        }
    }

    fn should_cancel(&self) -> bool {
        self.cancel_flag.load(Ordering::Relaxed)
    }

    fn count_lines(&self) -> Result<u64> {
        let file = open_shared_read(&self.log_path)?;
        let reader = BufReader::with_capacity(8 * 1024 * 1024, file);
        Ok(reader.lines().count() as u64)
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
            timestamp: Utc::now().to_rfc3339(),
        };

        let json = serde_json::to_string_pretty(&progress)?;
        let mut file = File::create(&self.progress_path)?;
        file.write_all(json.as_bytes())?;
        file.flush()?;

        Ok(())
    }

    fn process(&mut self) -> Result<()> {
        println!("Starting log processing...");

        // Count total lines
        println!("Counting lines in log file...");
        let total_lines = self.count_lines()?;
        self.total_lines.store(total_lines, Ordering::Relaxed);
        println!("Total lines: {}", total_lines);

        self.write_progress("counting", &format!("Counted {} lines", total_lines))?;

        // Open database connection
        let mut conn = Connection::open(&self.db_path)?;
        conn.pragma_update(None, "journal_mode", "WAL")?;
        conn.pragma_update(None, "synchronous", "NORMAL")?;
        conn.pragma_update(None, "cache_size", 1000000)?;
        conn.pragma_update(None, "locking_mode", "EXCLUSIVE")?;
        conn.pragma_update(None, "temp_store", "MEMORY")?;

        // LogEntries table already exists from C# migrations, use it for duplicate detection
        // Index IX_LogEntries_DuplicateCheck on (ClientIp, Service, Timestamp, Url, BytesServed) exists

        // Open log file with shared access (allows lancache to continue writing)
        let file = open_shared_read(&self.log_path)?;
        let mut reader = BufReader::with_capacity(8 * 1024 * 1024, file);

        // Skip to start position
        if self.start_position > 0 {
            println!("Skipping to position {}", self.start_position);
            for _ in 0..self.start_position {
                let mut line = String::new();
                if reader.read_line(&mut line)? == 0 {
                    break;
                }
                self.lines_parsed.fetch_add(1, Ordering::Relaxed);
            }
        }

        let mut batch = Vec::with_capacity(BULK_BATCH_SIZE);
        let mut line_buffer = String::with_capacity(2048);

        println!("Processing log entries...");
        self.write_progress("processing", "Reading log file...")?;

        loop {
            // Check for cancellation (interrupt-driven, checked every iteration for fast response)
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
                    self.process_batch(&mut conn, &batch)?;
                    batch.clear();
                }
                break;
            }

            self.lines_parsed.fetch_add(1, Ordering::Relaxed);

            // Check for cancellation every CANCEL_CHECK_INTERVAL lines for responsive cancellation
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
                batch.push(entry);

                // Process batch when it reaches BULK_BATCH_SIZE
                if batch.len() >= BULK_BATCH_SIZE {
                    // Check for cancellation before processing batch
                    if self.should_cancel() {
                        println!("Cancellation requested - stopping processing");
                        self.write_progress("cancelled", "Processing cancelled by user")?;
                        return Err(anyhow::anyhow!("Processing cancelled by user"));
                    }

                    self.process_batch(&mut conn, &batch)?;
                    batch.clear();

                    let parsed = self.lines_parsed.load(Ordering::Relaxed);
                    let saved = self.entries_saved.load(Ordering::Relaxed);
                    let percent = (parsed as f64 / total_lines as f64) * 100.0;
                    println!(
                        "Progress: {}/{} lines ({:.1}%), {} entries saved",
                        parsed, total_lines, percent, saved
                    );
                    self.write_progress(
                        "processing",
                        &format!("{} lines parsed, {} entries saved", parsed, saved),
                    )?;
                }
            }
        }

        println!("Processing complete!");
        self.write_progress("complete", "Log processing finished")?;

        Ok(())
    }

    fn process_batch(&mut self, conn: &mut Connection, entries: &[LogEntry]) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }

        let tx = conn.transaction()?;

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
        // Query SteamDepotMappings table for AppId and AppName
        let result = tx.query_row(
            "SELECT AppId, AppName FROM SteamDepotMappings WHERE DepotId = ? LIMIT 1",
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

        // If all entries were duplicates, skip all processing
        if new_entries.is_empty() {
            if skipped > 0 {
                println!("Skipped {} duplicate entries (all duplicates)", skipped);
            }
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

        // Lookup depot mapping for Steam downloads
        let (game_app_id, game_name) = if service.to_lowercase() == "steam" {
            if let Some(depot_id) = primary_depot_id {
                match self.lookup_depot_mapping(tx, depot_id) {
                    Ok(Some((app_id, app_name))) => (Some(app_id), app_name),
                    Ok(None) => (None, None),
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
            // Mark old session as inactive if exists
            tx.execute(
                "UPDATE Downloads SET IsActive = 0 WHERE ClientIp = ? AND Service = ? AND IsActive = 1",
                params![client_ip, service],
            )?;

            // Create new download session with depot mapping
            tx.execute(
                "INSERT INTO Downloads (Service, ClientIp, StartTime, EndTime, CacheHitBytes, CacheMissBytes, IsActive, LastUrl, DepotId, GameAppId, GameName)
                 VALUES (?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?)",
                params![
                    service,
                    client_ip,
                    first_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(),
                    last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(),
                    total_hit_bytes,
                    total_miss_bytes,
                    last_url,
                    primary_depot_id,
                    game_app_id,
                    game_name,
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
                tx.execute(
                    "UPDATE ClientStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastSeen = ?, TotalDownloads = TotalDownloads + 1 WHERE ClientIp = ?",
                    params![total_hit_bytes, total_miss_bytes, last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(), client_ip],
                )?;
            } else {
                tx.execute(
                    "INSERT INTO ClientStats (ClientIp, TotalCacheHitBytes, TotalCacheMissBytes, LastSeen, TotalDownloads) VALUES (?, ?, ?, ?, 1)",
                    params![client_ip, total_hit_bytes, total_miss_bytes, last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string()],
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
                tx.execute(
                    "UPDATE ServiceStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivity = ?, TotalDownloads = TotalDownloads + 1 WHERE Service = ?",
                    params![total_hit_bytes, total_miss_bytes, last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(), service],
                )?;
            } else {
                tx.execute(
                    "INSERT INTO ServiceStats (Service, TotalCacheHitBytes, TotalCacheMissBytes, LastActivity, TotalDownloads) VALUES (?, ?, ?, ?, 1)",
                    params![service, total_hit_bytes, total_miss_bytes, last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string()],
                )?;
            }

            download_id
        } else {
            // Find existing active download
            let download_id: i64 = tx.query_row(
                "SELECT Id FROM Downloads WHERE ClientIp = ? AND Service = ? AND IsActive = 1 ORDER BY StartTime DESC LIMIT 1",
                params![client_ip, service],
                |row| row.get(0),
            )?;

            // Update existing download session with depot mapping
            tx.execute(
                "UPDATE Downloads SET EndTime = ?, CacheHitBytes = CacheHitBytes + ?, CacheMissBytes = CacheMissBytes + ?, LastUrl = ?, DepotId = COALESCE(?, DepotId), GameAppId = COALESCE(?, GameAppId), GameName = COALESCE(?, GameName) WHERE Id = ?",
                params![
                    last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(),
                    total_hit_bytes,
                    total_miss_bytes,
                    last_url,
                    primary_depot_id,
                    game_app_id,
                    game_name,
                    download_id,
                ],
            )?;

            // Update client stats (without incrementing TotalDownloads)
            tx.execute(
                "UPDATE ClientStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastSeen = ? WHERE ClientIp = ?",
                params![total_hit_bytes, total_miss_bytes, last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(), client_ip],
            )?;

            // Update service stats (without incrementing TotalDownloads)
            tx.execute(
                "UPDATE ServiceStats SET TotalCacheHitBytes = TotalCacheHitBytes + ?, TotalCacheMissBytes = TotalCacheMissBytes + ?, LastActivity = ? WHERE Service = ?",
                params![total_hit_bytes, total_miss_bytes, last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(), service],
            )?;

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

    if args.len() < 5 {
        eprintln!(
            "Usage: {} <db_path> <log_path> <progress_path> <start_position>",
            args[0]
        );
        std::process::exit(1);
    }

    let db_path = PathBuf::from(&args[1]);
    let log_path = PathBuf::from(&args[2]);
    let progress_path = PathBuf::from(&args[3]);
    let start_position: u64 = args[4].parse().context("Invalid start_position")?;

    // Create cancel marker path in same directory as progress file
    let mut cancel_path = progress_path.clone();
    cancel_path.set_file_name("cancel_processing.marker");

    // Delete cancel marker if it exists from previous run
    if cancel_path.exists() {
        let _ = std::fs::remove_file(&cancel_path);
    }

    let mut processor = Processor::new(db_path, log_path, progress_path, cancel_path, start_position);
    processor.process()?;

    Ok(())
}