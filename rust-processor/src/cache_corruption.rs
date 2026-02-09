use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use rusqlite::Connection;
use serde::Serialize;
use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};
use tempfile::NamedTempFile;
use flate2::write::GzEncoder;
use flate2::Compression;

mod cache_utils;
mod cache_corruption_detector;
mod log_discovery;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;

use cache_corruption_detector::CorruptionDetector;
use progress_events::ProgressReporter;

/// Cache corruption detector and remover
#[derive(Parser, Debug)]
#[command(name = "cache_corruption")]
#[command(about = "Detects and removes corrupted cache chunks")]
struct Args {
    /// Emit JSON progress events to stdout
    #[arg(short, long, global = true)]
    progress: bool,

    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand, Debug)]
enum Commands {
    /// Find corrupted chunks and output detailed JSON report
    Detect {
        /// Directory containing log files
        log_dir: String,
        /// Cache directory root path
        cache_dir: String,
        /// Path to output JSON file
        output_json: String,
        /// Timezone (default: UTC)
        #[arg(default_value = "UTC")]
        timezone: Option<String>,
        /// Skip cache file existence check (logs-only mode)
        #[arg(long, default_value = "false")]
        no_cache_check: bool,
    },
    /// Quick JSON summary of corrupted chunk counts per service
    Summary {
        /// Directory containing log files
        log_dir: String,
        /// Cache directory root path
        cache_dir: String,
        /// Path to progress JSON file (use "none" to skip)
        #[arg(default_value = "none")]
        progress_json: Option<String>,
        /// Timezone (default: UTC)
        #[arg(default_value = "UTC")]
        timezone: Option<String>,
        /// Miss threshold (default: 3)
        #[arg(default_value = "3")]
        threshold: Option<usize>,
        /// Skip cache file existence check (logs-only mode)
        #[arg(long, default_value = "false")]
        no_cache_check: bool,
    },
    /// Delete database records, cache files, and log entries for corrupted chunks
    Remove {
        /// Path to LancacheManager.db
        database_path: String,
        /// Directory containing log files
        log_dir: String,
        /// Cache directory root path
        cache_dir: String,
        /// Service name to remove corrupted chunks for
        service: String,
        /// Path to progress JSON file
        progress_json: String,
        /// Miss threshold - minimum MISS/UNKNOWN count to consider corrupted (default: 3)
        #[arg(default_value = "3")]
        threshold: Option<usize>,
        /// Skip cache file existence check (logs-only mode)
        #[arg(long, default_value = "false")]
        no_cache_check: bool,
    },
}

#[derive(Serialize)]
struct ProgressData {
    status: String,
    message: String,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    #[serde(rename = "filesProcessed")]
    files_processed: usize,
    #[serde(rename = "totalFiles")]
    total_files: usize,
    timestamp: String,
}

fn parse_timezone(tz_str: &str) -> chrono_tz::Tz {
    tz_str.parse().unwrap_or(chrono_tz::UTC)
}

fn write_progress(
    progress_path: &Path,
    status: &str,
    message: &str,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
) -> Result<()> {
    let progress = ProgressData {
        status: status.to_string(),
        message: message.to_string(),
        percent_complete,
        files_processed,
        total_files,
        timestamp: progress_utils::current_timestamp(),
    };

    // Use shared progress writing utility
    progress_utils::write_progress_json(progress_path, &progress)
}

fn delete_corrupted_from_database(
    db_path: &Path,
    service: &str,
    corrupted_urls: &std::collections::HashSet<String>,
) -> Result<(usize, usize)> {
    eprintln!("Deleting corrupted database records for service: {}", service);

    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    let service_lower = service.to_lowercase();
    let miss_status = "MISS".to_string();
    let unknown_status = "UNKNOWN".to_string();

    let mut total_log_entries_deleted = 0;
    let mut total_downloads_deleted = 0;

    // Process in batches to avoid SQL parameter limits (SQLite has a default limit of 999)
    let batch_size = 400; // Reduced from 500 to accommodate extra parameters for cache status filter
    let urls: Vec<&String> = corrupted_urls.iter().collect();

    // STEP 1: Collect all unique DownloadIds that have corrupted (MISS/UNKNOWN) log entries
    let mut affected_download_ids = std::collections::HashSet::new();

    for chunk in urls.chunks(batch_size) {
        let placeholders = vec!["?"; chunk.len()].join(", ");

        let query = format!(
            "SELECT DISTINCT DownloadId FROM LogEntries WHERE LOWER(Service) = ? AND Url IN ({}) AND CacheStatus IN (?, ?) AND DownloadId IS NOT NULL",
            placeholders
        );

        let mut stmt = conn.prepare(&query)?;
        let mut params: Vec<&dyn rusqlite::ToSql> = vec![&service_lower];
        for url in chunk {
            params.push(url);
        }
        params.push(&miss_status);
        params.push(&unknown_status);

        let download_ids = stmt.query_map(rusqlite::params_from_iter(params.iter()), |row| {
            row.get::<_, i32>(0)
        })?;

        for download_id in download_ids {
            if let Ok(id) = download_id {
                affected_download_ids.insert(id);
            }
        }
    }

    eprintln!("  Found {} download sessions with corrupted entries", affected_download_ids.len());

    // STEP 2: Delete only MISS/UNKNOWN LogEntries for corrupted URLs
    // IMPORTANT: Keep HIT entries intact to prevent snowball corruption detection
    for chunk in urls.chunks(batch_size) {
        let placeholders = vec!["?"; chunk.len()].join(", ");

        let log_entries_query = format!(
            "DELETE FROM LogEntries WHERE LOWER(Service) = ? AND Url IN ({}) AND CacheStatus IN (?, ?)",
            placeholders
        );

        let mut log_stmt = conn.prepare(&log_entries_query)?;
        let mut params: Vec<&dyn rusqlite::ToSql> = vec![&service_lower];
        for url in chunk {
            params.push(url);
        }
        params.push(&miss_status);
        params.push(&unknown_status);
        let log_deleted = log_stmt.execute(rusqlite::params_from_iter(params.iter()))?;
        total_log_entries_deleted += log_deleted;
    }

    eprintln!("  Deleted {} log entry records", total_log_entries_deleted);

    // STEP 3: Only delete Download sessions that have NO remaining LogEntries
    // (i.e., sessions where ALL entries were corrupted MISS/UNKNOWN)
    let download_ids_vec: Vec<i32> = affected_download_ids.into_iter().collect();

    for chunk in download_ids_vec.chunks(batch_size) {
        let placeholders = vec!["?"; chunk.len()].join(", ");

        // Only delete Downloads that now have zero LogEntries remaining
        let downloads_query = format!(
            "DELETE FROM Downloads WHERE Id IN ({}) AND NOT EXISTS (SELECT 1 FROM LogEntries WHERE LogEntries.DownloadId = Downloads.Id)",
            placeholders
        );

        let mut downloads_stmt = conn.prepare(&downloads_query)?;
        let params: Vec<&dyn rusqlite::ToSql> = chunk.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
        let downloads_deleted = downloads_stmt.execute(rusqlite::params_from_iter(params.iter()))?;
        total_downloads_deleted += downloads_deleted;
    }

    eprintln!("  Deleted {} download records (sessions with only corrupted chunks)", total_downloads_deleted);

    // STEP 4: Update CacheMissBytes on remaining Downloads that had some corrupted entries removed
    // Recalculate miss bytes from remaining LogEntries
    let remaining_ids: Vec<i32> = download_ids_vec.iter()
        .copied()
        .collect();

    for chunk in remaining_ids.chunks(batch_size) {
        let placeholders = vec!["?"; chunk.len()].join(", ");

        let update_query = format!(
            "UPDATE Downloads SET CacheMissBytes = COALESCE((SELECT SUM(BytesServed) FROM LogEntries WHERE LogEntries.DownloadId = Downloads.Id AND CacheStatus IN ('MISS', 'UNKNOWN')), 0) WHERE Id IN ({})",
            placeholders
        );

        let mut update_stmt = conn.prepare(&update_query)?;
        let params: Vec<&dyn rusqlite::ToSql> = chunk.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
        update_stmt.execute(rusqlite::params_from_iter(params.iter()))?;
    }

    Ok((total_downloads_deleted, total_log_entries_deleted))
}

fn main() -> Result<()> {
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    match args.command {
        Commands::Detect { log_dir, cache_dir, output_json, timezone, no_cache_check } => {
            reporter.emit_started();

            let log_dir = PathBuf::from(&log_dir);
            let cache_dir = PathBuf::from(&cache_dir);
            let output_json = PathBuf::from(&output_json);
            let timezone = timezone.map(|tz| parse_timezone(&tz)).unwrap_or(chrono_tz::UTC);

            eprintln!("Detecting corrupted chunks...");
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());
            eprintln!("  Timezone: {}", timezone);
            eprintln!("  Skip cache check: {}", no_cache_check);

            reporter.emit_progress(10.0, "Scanning log files for corrupted chunks...");

            let detector = CorruptionDetector::new(&cache_dir, 3)
                .with_skip_cache_check(no_cache_check);
            let report = match detector.generate_report(&log_dir, "access.log", timezone) {
                Ok(r) => r,
                Err(e) => {
                    let msg = format!("Failed to generate corruption report: {}", e);
                    reporter.emit_failed(&msg);
                    anyhow::bail!("{}", msg);
                }
            };

            reporter.emit_progress(80.0, &format!("Found {} corrupted chunks across {} services", report.summary.total_corrupted, report.summary.service_counts.len()));

            eprintln!("Found {} corrupted chunks across {} services",
                report.summary.total_corrupted,
                report.summary.service_counts.len());

            // Write detailed report to JSON
            let json = serde_json::to_string_pretty(&report)?;
            let mut file = File::create(&output_json)?;
            file.write_all(json.as_bytes())?;
            file.flush()?;

            eprintln!("Report saved to: {}", output_json.display());
            reporter.emit_complete(&format!("Report saved to: {}", output_json.display()));
        }

        Commands::Summary { log_dir, cache_dir, progress_json, timezone, threshold, no_cache_check } => {
            reporter.emit_started();

            let log_dir = PathBuf::from(&log_dir);
            let cache_dir = PathBuf::from(&cache_dir);

            // Parse optional progress file - use "none" to skip progress
            let progress_path = progress_json
                .filter(|p| p != "none" && !p.is_empty())
                .map(PathBuf::from);

            let timezone = timezone.map(|tz| parse_timezone(&tz)).unwrap_or(chrono_tz::UTC);
            let threshold = threshold.unwrap_or(3);

            // All diagnostic output to stderr so stdout only contains JSON
            eprintln!("Generating corruption summary...");
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());
            eprintln!("  Progress file: {}", progress_path.as_ref().map(|p| p.display().to_string()).unwrap_or_else(|| "none".to_string()));
            eprintln!("  Timezone: {}", timezone);
            eprintln!("  Miss threshold: {}", threshold);
            eprintln!("  Skip cache check: {}", no_cache_check);

            reporter.emit_progress(10.0, "Scanning log files...");

            let detector = CorruptionDetector::new(&cache_dir, threshold)
                .with_skip_cache_check(no_cache_check);
            let summary = match detector.generate_summary_with_progress(
                &log_dir,
                "access.log",
                timezone,
                progress_path.as_deref()
            ) {
                Ok(s) => s,
                Err(e) => {
                    let msg = format!("Failed to generate corruption summary: {}", e);
                    reporter.emit_failed(&msg);
                    anyhow::bail!("{}", msg);
                }
            };

            // Output JSON to stdout for C# to capture (ONLY stdout should be JSON)
            let json = serde_json::to_string(&summary)?;
            println!("{}", json);
            reporter.emit_complete("Corruption summary generated");
        }

        Commands::Remove { database_path, log_dir, cache_dir, service, progress_json, threshold, no_cache_check } => {
            reporter.emit_started();

            let db_path = PathBuf::from(&database_path);
            let log_dir = PathBuf::from(&log_dir);
            let cache_dir = PathBuf::from(&cache_dir);
            let progress_path = PathBuf::from(&progress_json);

            eprintln!("Removing corrupted chunks for service: {}", service);
            eprintln!("  Database: {}", db_path.display());
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());

            write_progress(&progress_path, "starting", &format!("Starting removal for {}", service), 0.0, 0, 0)?;
            reporter.emit_progress(0.0, &format!("Starting removal for {}", service));

            // OPTIMIZED: Single-pass detection and removal
            // Instead of reading logs twice (once to detect, once to remove),
            // we detect corrupted URLs while filtering the log files in a single pass

            use log_reader::LogFileReader;
            use std::io::Write as IoWrite;
            use parser::LogParser;
            use std::collections::HashMap;

            eprintln!("Step 1: Detecting corrupted URLs for {}...", service);

            let service_lower = service.to_lowercase();
            let parser = LogParser::new(chrono_tz::UTC);
            let log_files = crate::log_discovery::discover_log_files(&log_dir, "access.log")?;

            // PASS 1: Scan all logs to identify corrupted URLs AND their sizes
            let mut miss_tracker: HashMap<String, usize> = HashMap::new();
            let mut url_sizes: HashMap<String, i64> = HashMap::new(); // Track max response size per URL
            let mut entries_processed: usize = 0;
            let miss_threshold: usize = threshold.unwrap_or(3);

            let total_files = log_files.len();
            write_progress(&progress_path, "scanning", &format!("Scanning {} log files for corrupted chunks", total_files), 0.0, 0, total_files)?;
            reporter.emit_progress(0.0, &format!("Scanning {} log files for corrupted chunks", total_files));

            // First pass: identify all corrupted URLs AND track their response sizes
            for (file_index, log_file) in log_files.iter().enumerate() {
                // Update progress during scanning (0-30%)
                let scan_percent = (file_index as f64 / total_files as f64) * 30.0;
                write_progress(&progress_path, "scanning", &format!("Scanning file {}/{}", file_index + 1, total_files), scan_percent, file_index, total_files)?;
                reporter.emit_progress(scan_percent, &format!("Scanning file {}/{}", file_index + 1, total_files));
                eprintln!("  Scanning file {}/{}: {}", file_index + 1, total_files, log_file.path.display());

                let scan_result = (|| -> Result<()> {
                    let mut log_reader = LogFileReader::open(&log_file.path)?;
                    let mut line = String::new();

                    loop {
                        line.clear();
                        let bytes_read = log_reader.read_line(&mut line)?;
                        if bytes_read == 0 {
                            break; // EOF
                        }

                        // Parse the line
                        if let Some(entry) = parser.parse_line(line.trim()) {
                            // Skip health check/heartbeat endpoints
                            if !service_utils::should_skip_url(&entry.url) {
                                // Track MISS/UNKNOWN for this service
                                if entry.service == service_lower &&
                                   (entry.cache_status == "MISS" || entry.cache_status == "UNKNOWN") {
                                    *miss_tracker.entry(entry.url.clone()).or_insert(0) += 1;
                                    entries_processed += 1;

                                    // Track the maximum response size for this URL
                                    // (same URL might have different sizes in different requests)
                                    url_sizes.entry(entry.url.clone())
                                        .and_modify(|size| *size = (*size).max(entry.bytes_served))
                                        .or_insert(entry.bytes_served);

                                    // Log progress periodically
                                    if entries_processed % 500_000 == 0 {
                                        eprintln!("    Processed {} MISS/UNKNOWN entries, tracking {} unique URLs",
                                            entries_processed, miss_tracker.len());
                                    }
                                }
                            }
                        }
                    }
                    Ok(())
                })();

                if let Err(e) = scan_result {
                    eprintln!("  WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                    continue;
                }
            }

            // Build map of candidate URLs with their response sizes (those with threshold+ misses)
            let candidates: HashMap<String, i64> = miss_tracker
                .iter()
                .filter(|(_, &count)| count >= miss_threshold)
                .map(|(url, _)| {
                    let size = url_sizes.get(url).copied().unwrap_or(0);
                    (url.clone(), size)
                })
                .collect();

            let candidate_count = candidates.len();
            eprintln!("Found {} URLs with {}+ MISS/UNKNOWN entries for {}", candidate_count, miss_threshold, service);

            let corrupted_urls_with_sizes: HashMap<String, i64> = if no_cache_check {
                eprintln!("Skipping cache file existence check (logs-only mode)");
                eprintln!("Using all {} candidate URLs", candidate_count);
                candidates
            } else {
                // Filter to only URLs where the cache file actually exists on disk
                // If the file doesn't exist, the MISSes were likely legitimate cold-cache or eviction events
                // True corruption = file exists but nginx can't serve it (repeated MISSes despite file presence)
                let filtered: HashMap<String, i64> = candidates
                    .into_iter()
                    .filter(|(url, _response_size)| {
                        // Check no-range format first (modern lancache)
                        let cache_path = cache_utils::calculate_cache_path_no_range(&cache_dir, &service_lower, url);
                        if cache_path.exists() {
                            return true;
                        }
                        // Fallback: check first chunk in range format (legacy lancache)
                        let range_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, 0, 1_048_575);
                        range_path.exists()
                    })
                    .collect();

                let filtered_out = candidate_count - filtered.len();
                if filtered_out > 0 {
                    eprintln!("Filtered out {} URLs where cache file does not exist on disk (likely cold-cache misses)", filtered_out);
                }
                eprintln!("Confirmed {} corrupted URLs for {} (file exists on disk with {}+ MISS/UNKNOWN)", filtered.len(), service, miss_threshold);
                filtered
            };
            reporter.emit_progress(30.0, &format!("Found {} corrupted URLs for {}", corrupted_urls_with_sizes.len(), service));

            if corrupted_urls_with_sizes.is_empty() {
                eprintln!("No corrupted chunks found, nothing to remove");
                write_progress(&progress_path, "completed", "No corrupted chunks found", 100.0, 0, 0)?;
                reporter.emit_complete("No corrupted chunks found, nothing to remove");
                return Ok(());
            }

            // Build set for fast lookup during log filtering
            let corrupted_urls: std::collections::HashSet<String> = corrupted_urls_with_sizes
                .keys()
                .cloned()
                .collect();

            // PASS 2: Filter log files, removing ALL lines with corrupted URLs
            eprintln!("Step 2: Filtering log files to remove corrupted chunks...");

            let mut total_lines_removed: u64 = 0;

            write_progress(&progress_path, "filtering", &format!("Filtering {} log files", total_files), 30.0, 0, total_files)?;
            reporter.emit_progress(30.0, &format!("Filtering {} log files", total_files));

            for (file_index, log_file) in log_files.iter().enumerate() {
                // Update progress during filtering (30-70%)
                let filter_percent = 30.0 + (file_index as f64 / total_files as f64) * 40.0;
                write_progress(&progress_path, "filtering", &format!("Filtering file {}/{}", file_index + 1, total_files), filter_percent, file_index, total_files)?;
                reporter.emit_progress(filter_percent, &format!("Filtering file {}/{}", file_index + 1, total_files));
                eprintln!("  Processing file {}/{}: {}", file_index + 1, total_files, log_file.path.display());

                let file_result = (|| -> Result<u64> {
                    use std::io::BufWriter;

                    // Create temp file for filtered output with automatic cleanup
                    let file_dir = log_file.path.parent().context("Failed to get file directory")?;
                    let temp_file = NamedTempFile::new_in(file_dir)?;

                    let mut lines_removed: u64 = 0;
                    let mut lines_processed: u64 = 0;

                    {
                        let mut log_reader = LogFileReader::open(&log_file.path)?;

                        // Create writer that matches the compression of the original file
                        let mut writer: Box<dyn std::io::Write> = if log_file.is_compressed {
                            // Check extension to determine compression type
                            let path_str = log_file.path.to_string_lossy();
                            if path_str.ends_with(".gz") {
                                // Gzip compression
                                Box::new(BufWriter::with_capacity(
                                    1024 * 1024,
                                    GzEncoder::new(temp_file.as_file().try_clone()?, Compression::default())
                                ))
                            } else if path_str.ends_with(".zst") {
                                // Zstd compression
                                Box::new(BufWriter::with_capacity(
                                    1024 * 1024,
                                    zstd::Encoder::new(temp_file.as_file().try_clone()?, 3)?
                                ))
                            } else {
                                // Unknown compression, treat as plain
                                Box::new(BufWriter::with_capacity(1024 * 1024, temp_file.as_file().try_clone()?))
                            }
                        } else {
                            // Plain text
                            Box::new(BufWriter::with_capacity(1024 * 1024, temp_file.as_file().try_clone()?))
                        };

                        let mut line = String::new();

                        loop {
                            line.clear();
                            let bytes_read = log_reader.read_line(&mut line)?;
                            if bytes_read == 0 {
                                break; // EOF
                            }

                            lines_processed += 1;
                            let mut should_remove = false;

                            // Parse the line and check if URL is in corrupted set
                            if let Some(entry) = parser.parse_line(line.trim()) {
                                // Only remove MISS/UNKNOWN lines for corrupted URLs
                                // IMPORTANT: Keep HIT lines intact - removing them would skew future
                                // corruption detection by eliminating evidence of successful cache hits,
                                // causing a snowball effect where each removal doubles the corruption count
                                if entry.service == service_lower
                                    && corrupted_urls.contains(&entry.url)
                                    && (entry.cache_status == "MISS" || entry.cache_status == "UNKNOWN")
                                {
                                    should_remove = true;
                                }
                            }

                            if !should_remove {
                                write!(writer, "{}", line)?;
                            } else {
                                lines_removed += 1;
                            }
                        }

                        writer.flush()?;
                        // Ensure compression is finalized for zstd
                        drop(writer);
                    }

                    // If all lines would be removed, delete the entire file instead
                    if lines_processed > 0 && lines_removed == lines_processed {
                        eprintln!("  INFO: All {} lines from this file are corrupted, deleting file entirely", lines_processed);
                        // temp_file automatically deleted when it goes out of scope
                        // Delete the original log file
                        std::fs::remove_file(&log_file.path).ok();
                        return Ok(lines_removed);
                    }

                    // Atomically replace original with filtered version
                    // persist() uses rename which can fail on Windows if file is locked
                    let temp_path = temp_file.into_temp_path();

                    if let Err(persist_err) = temp_path.persist(&log_file.path) {
                        // Fallback: copy + delete (works even if target is locked by file watcher)
                        eprintln!("    persist() failed ({}), using copy fallback...", persist_err);

                        // Copy temp file contents to original
                        std::fs::copy(&persist_err.path, &log_file.path)
                            .with_context(|| format!("Failed to copy temp file to {}", log_file.path.display()))?;

                        // Delete the temp file manually (persist_err.path is the temp path)
                        std::fs::remove_file(&persist_err.path).ok();
                    }

                    Ok(lines_removed)
                })();

                match file_result {
                    Ok(lines_removed) => {
                        eprintln!("    Removed {} log lines from this file", lines_removed);
                        total_lines_removed += lines_removed;
                    }
                    Err(e) => {
                        eprintln!("  WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                        continue;
                    }
                }
            }

            let total_urls = corrupted_urls_with_sizes.len();
            write_progress(&progress_path, "removing_cache", &format!("Removing cache files for {} corrupted URLs", total_urls), 70.0, 0, total_urls)?;
            reporter.emit_progress(70.0, &format!("Removing cache files for {} corrupted URLs", total_urls));

            // Step 3: Delete ALL cache file chunks from disk
            // IMPORTANT: Use same logic as game_cache_remover - try no-range format first!
            // Track permission errors separately - these indicate PUID/PGID mismatch
            eprintln!("Step 3: Deleting cache files...");
            let mut deleted_count = 0;
            let mut permission_errors = 0;
            let mut other_errors = 0;
            let slice_size: i64 = 1_048_576; // 1MB

            for (url_index, (url, response_size)) in corrupted_urls_with_sizes.iter().enumerate() {
                // Update progress during cache removal (70-95%)
                if url_index % 50 == 0 || url_index == total_urls - 1 {
                    let cache_percent = 70.0 + (url_index as f64 / total_urls.max(1) as f64) * 20.0;
                    write_progress(&progress_path, "removing_cache", &format!("Removing cache file {}/{}", url_index + 1, total_urls), cache_percent, url_index, total_urls)?;
                    reporter.emit_progress(cache_percent, &format!("Removing cache file {}/{}", url_index + 1, total_urls));
                }
                
                // FIRST: Try the no-range format (standard lancache format)
                let cache_path_no_range = cache_utils::calculate_cache_path_no_range(&cache_dir, &service_lower, url);

                if cache_path_no_range.exists() {
                    // Found file with no-range format - delete it
                    match std::fs::remove_file(&cache_path_no_range) {
                        Ok(_) => {
                            deleted_count += 1;
                            if deleted_count % 100 == 0 {
                                eprintln!("  Deleted {} cache files...", deleted_count);
                            }
                        }
                        Err(e) => {
                            // Check if this is a permission error
                            if e.kind() == std::io::ErrorKind::PermissionDenied {
                                permission_errors += 1;
                                if permission_errors <= 5 {
                                    eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path_no_range.display(), e);
                                }
                            } else {
                                other_errors += 1;
                                eprintln!("  Warning: Failed to delete {}: {}", cache_path_no_range.display(), e);
                            }
                        }
                    }
                } else {
                    // FALLBACK: Try the chunked format with bytes range
                    if *response_size == 0 {
                        // If no response size, check at least the first chunk
                        let cache_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, 0, 1_048_575);

                        if cache_path.exists() {
                            match std::fs::remove_file(&cache_path) {
                                Ok(_) => deleted_count += 1,
                                Err(e) => {
                                    if e.kind() == std::io::ErrorKind::PermissionDenied {
                                        permission_errors += 1;
                                        if permission_errors <= 5 {
                                            eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                                        }
                                    } else {
                                        other_errors += 1;
                                        eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                                    }
                                }
                            }
                        }
                    } else {
                        // Calculate ALL chunks based on actual response size
                        let mut start: i64 = 0;
                        while start < *response_size {
                            let end = (start + slice_size - 1).min(*response_size - 1 + slice_size - 1);

                            let cache_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, start as u64, end as u64);

                            if cache_path.exists() {
                                match std::fs::remove_file(&cache_path) {
                                    Ok(_) => {
                                        deleted_count += 1;
                                        if deleted_count % 100 == 0 {
                                            eprintln!("  Deleted {} cache files...", deleted_count);
                                        }
                                    }
                                    Err(e) => {
                                        if e.kind() == std::io::ErrorKind::PermissionDenied {
                                            permission_errors += 1;
                                            if permission_errors <= 5 {
                                                eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                                            }
                                        } else {
                                            other_errors += 1;
                                            eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                                        }
                                    }
                                }
                            }

                            start += slice_size;
                        }
                    }
                }
            }

            eprintln!("Deleted {} cache files", deleted_count);
            if permission_errors > 0 {
                eprintln!("ERROR: {} files could not be deleted due to permission errors", permission_errors);
                if permission_errors > 5 {
                    eprintln!("  (only first 5 errors shown)");
                }
            }
            if other_errors > 0 {
                eprintln!("WARNING: {} files could not be deleted due to other errors", other_errors);
            }
            eprintln!("Removed {} total log lines across {} files", total_lines_removed, log_files.len());

            // CRITICAL: If we had permission errors, do NOT delete database records
            // This prevents the DB/filesystem state mismatch that causes issues
            if permission_errors > 0 {
                let error_msg = format!(
                    "ABORTED: Cannot delete database records because {} cache files could not be deleted due to permission errors. \
                    This is likely caused by incorrect PUID/PGID settings. The lancache container typically runs as UID/GID 33:33 (www-data). \
                    Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership.",
                    permission_errors
                );
                eprintln!("\n{}", error_msg);
                write_progress(&progress_path, "failed", &error_msg, 0.0, 0, 0)?;
                reporter.emit_failed(&error_msg);
                std::process::exit(1);
            }

            // Step 4: Delete database records for corrupted downloads
            // Only reached if ALL file deletions succeeded (no permission errors)
            eprintln!("Step 4: Deleting database records...");
            write_progress(&progress_path, "removing_database", "Deleting database records for corrupted chunks", 90.0, 0, 0)?;
            reporter.emit_progress(90.0, "Deleting database records for corrupted chunks");

            let (downloads_deleted, log_entries_deleted) = delete_corrupted_from_database(&db_path, &service, &corrupted_urls)?;

            let summary_msg = format!("Removed {} corrupted URLs for {} ({} cache files deleted, {} log lines removed, {} downloads deleted, {} log entries deleted)", corrupted_urls_with_sizes.len(), service, deleted_count, total_lines_removed, downloads_deleted, log_entries_deleted);
            write_progress(&progress_path, "completed", &summary_msg, 100.0, 0, 0)?;
            reporter.emit_complete(&summary_msg);
            eprintln!("\n=== Corruption Removal Summary ===");
            eprintln!("Corrupted URLs removed: {}", corrupted_urls_with_sizes.len());
            eprintln!("Cache files deleted: {}", deleted_count);
            eprintln!("Log lines removed: {}", total_lines_removed);
            eprintln!("Database downloads deleted: {}", downloads_deleted);
            eprintln!("Database log entries deleted: {}", log_entries_deleted);
            eprintln!("Removal completed successfully");
        }
    }

    Ok(())
}
