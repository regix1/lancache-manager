use anyhow::Result;
use chrono::{NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use rusqlite::{params, Connection, OptionalExtension, TransactionBehavior};
use serde::Serialize;
use std::env;
use std::io::BufRead;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

mod log_discovery;
mod log_reader;
mod models;
mod progress_utils;
mod stream_parser;

use log_discovery::{discover_log_files, LogFile};
use log_reader::LogFileReader;
use models::StreamLogEntry;
use stream_parser::StreamLogParser;

const BULK_BATCH_SIZE: usize = 1_000;
const LINE_BUFFER_CAPACITY: usize = 1024;

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

struct StreamProcessor {
    db_path: PathBuf,
    log_dir: PathBuf,
    progress_path: PathBuf,
    start_position: u64,
    parser: StreamLogParser,
    total_lines: AtomicU64,
    lines_parsed: AtomicU64,
    entries_saved: AtomicU64,
    local_tz: Tz,
    last_logged_percent: AtomicU64,
    datasource_name: String,
}

impl StreamProcessor {
    fn new(
        db_path: PathBuf,
        log_dir: PathBuf,
        progress_path: PathBuf,
        start_position: u64,
        datasource_name: String,
    ) -> Self {
        // Get timezone from environment variable
        let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
        let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);
        println!("Using timezone: {} (from TZ env var)", local_tz);
        println!("Datasource: {}", datasource_name);

        Self {
            db_path,
            log_dir,
            progress_path,
            start_position,
            parser: StreamLogParser::new(local_tz),
            total_lines: AtomicU64::new(0),
            lines_parsed: AtomicU64::new(0),
            entries_saved: AtomicU64::new(0),
            local_tz,
            last_logged_percent: AtomicU64::new(0),
            datasource_name,
        }
    }

    /// Convert UTC NaiveDateTime to local timezone NaiveDateTime
    fn utc_to_local(&self, utc_dt: NaiveDateTime) -> NaiveDateTime {
        let utc_datetime = Utc.from_utc_datetime(&utc_dt);
        let local_datetime = utc_datetime.with_timezone(&self.local_tz);
        NaiveDateTime::new(local_datetime.date_naive(), local_datetime.time())
    }

    /// Count total lines across all discovered log files
    fn count_lines_all_files(&self, log_files: &[LogFile]) -> Result<u64> {
        let mut total = 0u64;
        for log_file in log_files {
            let file_result = (|| -> Result<u64> {
                let mut reader = LogFileReader::open(&log_file.path)?;
                let lines = reader.as_buf_read().lines().count() as u64;
                Ok(lines)
            })();

            match file_result {
                Ok(lines) => {
                    total += lines;
                    println!("  {} has {} lines", log_file.path.display(), lines);
                }
                Err(e) => {
                    eprintln!("WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
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

        progress_utils::write_progress_with_retry(&self.progress_path, &progress, 5)
    }

    fn process(&mut self) -> Result<()> {
        println!("Starting stream log processing...");
        println!("Log directory: {}", self.log_dir.display());

        // Discover all stream-access.log files
        let log_files = discover_log_files(&self.log_dir, "stream-access.log")?;

        if log_files.is_empty() {
            println!("No stream-access.log files found");
            self.write_progress("complete", "No stream log files found")?;
            return Ok(());
        }

        println!("Found {} stream log file(s):", log_files.len());
        for log_file in &log_files {
            let compression_info = if log_file.is_compressed { " (compressed)" } else { "" };
            let rotation_info = match log_file.rotation_number {
                Some(num) => format!(" [rotation {}]", num),
                None => " [current]".to_string(),
            };
            println!("  - {}{}{}", log_file.path.display(), rotation_info, compression_info);
        }

        // Count total lines
        println!("Counting lines in all stream log files...");
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
        conn.busy_timeout(Duration::from_secs(60))?;

        // Ensure StreamSessions table exists (migration should have created it)
        self.ensure_table_exists(&conn)?;

        // Process each log file
        let mut lines_to_skip = self.start_position;

        for (file_index, log_file) in log_files.iter().enumerate() {
            println!("\nProcessing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

            let file_result = self.process_single_file(&mut conn, log_file, &mut lines_to_skip, total_lines);

            if let Err(e) = file_result {
                eprintln!("⚠ Warning: Skipping corrupted file {}: {}", log_file.path.display(), e);
                continue;
            }
        }

        println!("\nAll stream files processed successfully!");
        self.write_progress("complete", "Stream log processing finished")?;

        Ok(())
    }

    fn ensure_table_exists(&self, conn: &Connection) -> Result<()> {
        // Check if StreamSessions table exists
        let table_exists: bool = conn.query_row(
            "SELECT COUNT(*) > 0 FROM sqlite_master WHERE type='table' AND name='StreamSessions'",
            [],
            |row| row.get(0),
        )?;

        if !table_exists {
            println!("Creating StreamSessions table...");
            conn.execute(
                "CREATE TABLE IF NOT EXISTS StreamSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClientIp TEXT NOT NULL,
                    SessionStartUtc TEXT NOT NULL,
                    SessionEndUtc TEXT NOT NULL,
                    SessionStartLocal TEXT NOT NULL,
                    SessionEndLocal TEXT NOT NULL,
                    Protocol TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    BytesSent INTEGER NOT NULL,
                    BytesReceived INTEGER NOT NULL,
                    DurationSeconds REAL NOT NULL,
                    UpstreamHost TEXT NOT NULL,
                    DownloadId INTEGER,
                    Datasource TEXT NOT NULL DEFAULT 'default',
                    FOREIGN KEY (DownloadId) REFERENCES Downloads(Id) ON DELETE SET NULL
                )",
                [],
            )?;

            // Create indexes
            conn.execute("CREATE INDEX IF NOT EXISTS IX_StreamSessions_ClientIp ON StreamSessions(ClientIp)", [])?;
            conn.execute("CREATE INDEX IF NOT EXISTS IX_StreamSessions_SessionEndUtc ON StreamSessions(SessionEndUtc)", [])?;
            conn.execute("CREATE INDEX IF NOT EXISTS IX_StreamSessions_DownloadId ON StreamSessions(DownloadId)", [])?;
            conn.execute("CREATE INDEX IF NOT EXISTS IX_StreamSessions_Datasource ON StreamSessions(Datasource)", [])?;
            conn.execute(
                "CREATE INDEX IF NOT EXISTS IX_StreamSessions_Correlation ON StreamSessions(ClientIp, SessionStartUtc, SessionEndUtc, UpstreamHost)",
                [],
            )?;
            conn.execute(
                "CREATE INDEX IF NOT EXISTS IX_StreamSessions_DuplicateCheck ON StreamSessions(ClientIp, SessionEndUtc, BytesSent, BytesReceived, DurationSeconds, UpstreamHost, Datasource)",
                [],
            )?;
        }

        Ok(())
    }

    fn process_single_file(
        &mut self,
        conn: &mut Connection,
        log_file: &LogFile,
        lines_to_skip: &mut u64,
        _total_lines: u64,
    ) -> Result<()> {
        let mut reader = LogFileReader::open(&log_file.path)?;

        // Skip lines if needed
        if *lines_to_skip > 0 {
            println!("Skipping {} lines to reach start position", lines_to_skip);
            let mut line = String::new();
            let mut skipped = 0u64;
            while skipped < *lines_to_skip {
                line.clear();
                if reader.as_buf_read().read_line(&mut line)? == 0 {
                    break;
                }
                skipped += 1;
            }
            *lines_to_skip = lines_to_skip.saturating_sub(skipped);
            if *lines_to_skip > 0 {
                println!("Reached end of file, {} lines remaining to skip", lines_to_skip);
                return Ok(());
            }
        }

        // Process lines in batches
        let mut line = String::with_capacity(LINE_BUFFER_CAPACITY);
        let mut batch: Vec<StreamLogEntry> = Vec::with_capacity(BULK_BATCH_SIZE);

        loop {
            line.clear();
            let bytes_read = reader.as_buf_read().read_line(&mut line)?;
            if bytes_read == 0 {
                break;
            }

            self.lines_parsed.fetch_add(1, Ordering::Relaxed);

            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }

            if let Some(entry) = self.parser.parse_line(trimmed) {
                batch.push(entry);

                if batch.len() >= BULK_BATCH_SIZE {
                    self.save_batch(conn, &batch)?;
                    batch.clear();
                }
            }

            // Update progress periodically
            let parsed = self.lines_parsed.load(Ordering::Relaxed);
            if parsed % 10_000 == 0 {
                let total = self.total_lines.load(Ordering::Relaxed);
                let percent = if total > 0 { (parsed as f64 / total as f64) * 100.0 } else { 0.0 };
                let current_percent = percent as u64;
                let last_percent = self.last_logged_percent.load(Ordering::Relaxed);

                if current_percent > last_percent {
                    self.last_logged_percent.store(current_percent, Ordering::Relaxed);
                    self.write_progress("processing", &format!("Processed {} lines ({:.1}%)", parsed, percent))?;
                }
            }
        }

        // Save remaining batch
        if !batch.is_empty() {
            self.save_batch(conn, &batch)?;
        }

        Ok(())
    }

    fn save_batch(&self, conn: &mut Connection, entries: &[StreamLogEntry]) -> Result<()> {
        let tx = conn.transaction_with_behavior(TransactionBehavior::Immediate)?;

        for entry in entries {
            // Check for duplicate
            let exists: bool = tx.query_row(
                "SELECT 1 FROM StreamSessions WHERE ClientIp = ?1 AND SessionEndUtc = ?2
                 AND BytesSent = ?3 AND BytesReceived = ?4 AND DurationSeconds = ?5
                 AND UpstreamHost = ?6 AND Datasource = ?7 LIMIT 1",
                params![
                    &entry.client_ip,
                    entry.timestamp.format("%Y-%m-%d %H:%M:%S").to_string(),
                    entry.bytes_sent,
                    entry.bytes_received,
                    entry.session_duration,
                    &entry.upstream_host,
                    &self.datasource_name,
                ],
                |_| Ok(true),
            ).optional()?.unwrap_or(false);

            if exists {
                continue;
            }

            let session_start = entry.session_start();
            let session_end = entry.timestamp;
            let session_start_local = self.utc_to_local(session_start);
            let session_end_local = self.utc_to_local(session_end);

            tx.execute(
                "INSERT INTO StreamSessions (
                    ClientIp, SessionStartUtc, SessionEndUtc, SessionStartLocal, SessionEndLocal,
                    Protocol, Status, BytesSent, BytesReceived, DurationSeconds, UpstreamHost, Datasource
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
                params![
                    &entry.client_ip,
                    session_start.format("%Y-%m-%d %H:%M:%S").to_string(),
                    session_end.format("%Y-%m-%d %H:%M:%S").to_string(),
                    session_start_local.format("%Y-%m-%d %H:%M:%S").to_string(),
                    session_end_local.format("%Y-%m-%d %H:%M:%S").to_string(),
                    &entry.protocol,
                    entry.status,
                    entry.bytes_sent,
                    entry.bytes_received,
                    entry.session_duration,
                    &entry.upstream_host,
                    &self.datasource_name,
                ],
            )?;

            self.entries_saved.fetch_add(1, Ordering::Relaxed);
        }

        tx.commit()?;
        Ok(())
    }
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 4 {
        eprintln!("Usage: stream_processor <db_path> <log_directory> <progress_file> [start_position] [datasource]");
        eprintln!("");
        eprintln!("Arguments:");
        eprintln!("  db_path         Path to SQLite database file");
        eprintln!("  log_directory   Directory containing stream-access.log files");
        eprintln!("  progress_file   Path to JSON file for progress updates");
        eprintln!("  start_position  (optional) Line number to start from (default: 0)");
        eprintln!("  datasource      (optional) Datasource name (default: 'default')");
        eprintln!("");
        eprintln!("Example:");
        eprintln!("  stream_processor data/LancacheManager.db logs data/stream_progress.json 0 default");
        std::process::exit(1);
    }

    let db_path = PathBuf::from(&args[1]);
    let log_dir = PathBuf::from(&args[2]);
    let progress_path = PathBuf::from(&args[3]);
    let start_position: u64 = args.get(4).and_then(|s| s.parse().ok()).unwrap_or(0);
    let datasource_name = args.get(5).cloned().unwrap_or_else(|| "default".to_string());

    println!("Stream Log Processor");
    println!("====================");
    println!("Database: {}", db_path.display());
    println!("Log directory: {}", log_dir.display());
    println!("Progress file: {}", progress_path.display());
    println!("Start position: {}", start_position);
    println!("Datasource: {}", datasource_name);
    println!();

    let mut processor = StreamProcessor::new(
        db_path,
        log_dir,
        progress_path,
        start_position,
        datasource_name,
    );

    processor.process()?;

    println!("\nProcessing complete!");
    println!("Total entries saved: {}", processor.entries_saved.load(Ordering::Relaxed));

    Ok(())
}
