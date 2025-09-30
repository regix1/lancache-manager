use anyhow::{Context, Result};
use chrono::Utc;
use rusqlite::{params, Connection, Transaction};
use serde::Serialize;
use std::collections::HashMap;
use std::fs::File;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

mod models;
mod parser;
mod session;

use models::*;
use parser::LogParser;
use session::SessionTracker;

const BULK_BATCH_SIZE: usize = 100_000;
const SESSION_GAP_MINUTES: i64 = 5;

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
}

impl Processor {
    fn new(
        db_path: PathBuf,
        log_path: PathBuf,
        progress_path: PathBuf,
        start_position: u64,
    ) -> Self {
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
        }
    }

    fn count_lines(&self) -> Result<u64> {
        let file = File::open(&self.log_path)?;
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

        // Open log file
        let file = File::open(&self.log_path)?;
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

            // Parse the line (trim to remove newline)
            let trimmed_line = line_buffer.trim();
            if let Some(entry) = self.parser.parse_line(trimmed_line) {
                batch.push(entry);

                // Process batch when it reaches BULK_BATCH_SIZE
                if batch.len() >= BULK_BATCH_SIZE {
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

        // Group entries by client_ip + service
        let mut grouped: HashMap<String, Vec<&LogEntry>> = HashMap::new();
        for entry in entries {
            let key = format!("{}_{}", entry.client_ip, entry.service);
            grouped.entry(key).or_insert_with(Vec::new).push(entry);
        }

        // Process each group
        for (session_key, group_entries) in &grouped {
            self.process_session_group(&tx, session_key, group_entries)?;
        }

        tx.commit()?;

        let saved = grouped.values().map(|v| v.len()).sum::<usize>();
        self.entries_saved
            .fetch_add(saved as u64, Ordering::Relaxed);

        Ok(())
    }

    fn process_session_group(
        &mut self,
        tx: &Transaction,
        session_key: &str,
        entries: &[&LogEntry],
    ) -> Result<()> {
        if entries.is_empty() {
            return Ok(());
        }

        let first_entry = entries[0];
        let client_ip = &first_entry.client_ip;
        let service = &first_entry.service;

        // Calculate timestamps and aggregations
        let first_timestamp = entries.iter().map(|e| e.timestamp).min().unwrap();
        let last_timestamp = entries.iter().map(|e| e.timestamp).max().unwrap();

        let total_hit_bytes: i64 = entries
            .iter()
            .filter(|e| e.cache_status == "HIT")
            .map(|e| e.bytes_served)
            .sum();

        let total_miss_bytes: i64 = entries
            .iter()
            .filter(|e| e.cache_status == "MISS")
            .map(|e| e.bytes_served)
            .sum();

        // Extract primary depot ID (most common)
        let primary_depot_id = entries
            .iter()
            .filter_map(|e| e.depot_id)
            .fold(HashMap::new(), |mut map, depot| {
                *map.entry(depot).or_insert(0) += 1;
                map
            })
            .into_iter()
            .max_by_key(|(_, count)| *count)
            .map(|(depot, _)| depot);

        let last_url = entries.last().map(|e| e.url.as_str());

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

            // Create new download session
            tx.execute(
                "INSERT INTO Downloads (Service, ClientIp, StartTime, EndTime, CacheHitBytes, CacheMissBytes, IsActive, LastUrl, DepotId)
                 VALUES (?, ?, ?, ?, ?, ?, 1, ?, ?)",
                params![
                    service,
                    client_ip,
                    first_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(),
                    last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(),
                    total_hit_bytes,
                    total_miss_bytes,
                    last_url,
                    primary_depot_id,
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

            // Update existing download session
            tx.execute(
                "UPDATE Downloads SET EndTime = ?, CacheHitBytes = CacheHitBytes + ?, CacheMissBytes = CacheMissBytes + ?, LastUrl = ?, DepotId = COALESCE(?, DepotId) WHERE Id = ?",
                params![
                    last_timestamp.format("%Y-%m-%d %H:%M:%S").to_string(),
                    total_hit_bytes,
                    total_miss_bytes,
                    last_url,
                    primary_depot_id,
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

        // Insert log entries (skip duplicates)
        // Check for duplicates before inserting using the duplicate check index
        let mut check_stmt = tx.prepare_cached(
            "SELECT 1 FROM LogEntries WHERE ClientIp = ? AND Service = ? AND Timestamp = ? AND Url = ? AND BytesServed = ? LIMIT 1"
        )?;

        let mut insert_stmt = tx.prepare_cached(
            "INSERT INTO LogEntries (Timestamp, ClientIp, Service, Method, Url, StatusCode, BytesServed, CacheStatus, DepotId, DownloadId, CreatedAt)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        )?;

        let now = Utc::now().format("%Y-%m-%d %H:%M:%S").to_string();
        let mut inserted = 0;
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
                continue;
            }

            // Insert if not exists
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
            inserted += 1;
        }

        // If some entries were skipped, print info
        if skipped > 0 {
            println!(
                "Skipped {} duplicate entries (inserted {}/{})",
                skipped,
                inserted,
                entries.len()
            );
        }

        Ok(())
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

    let mut processor = Processor::new(db_path, log_path, progress_path, start_position);
    processor.process()?;

    Ok(())
}