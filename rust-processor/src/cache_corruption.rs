use anyhow::{Context, Result};
use rusqlite::Connection;
use serde::Serialize;
use std::env;
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
mod progress_utils;
mod service_utils;

use cache_corruption_detector::CorruptionDetector;

#[derive(Serialize)]
struct ProgressData {
    status: String,
    message: String,
    timestamp: String,
}

fn parse_timezone(tz_str: &str) -> chrono_tz::Tz {
    tz_str.parse().unwrap_or(chrono_tz::UTC)
}

fn write_progress(progress_path: &Path, status: &str, message: &str) -> Result<()> {
    let progress = ProgressData {
        status: status.to_string(),
        message: message.to_string(),
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

    let mut total_log_entries_deleted = 0;
    let mut total_downloads_deleted = 0;

    // Process in batches to avoid SQL parameter limits (SQLite has a default limit of 999)
    let batch_size = 500;
    let urls: Vec<&String> = corrupted_urls.iter().collect();

    // STEP 1: Collect all unique DownloadIds that have corrupted log entries
    // We need to do this BEFORE deleting LogEntries, so we know which Downloads to remove
    let mut download_ids_to_delete = std::collections::HashSet::new();

    for chunk in urls.chunks(batch_size) {
        let placeholders = vec!["?"; chunk.len()].join(", ");

        // Find all DownloadIds that have at least one corrupted log entry
        let query = format!(
            "SELECT DISTINCT DownloadId FROM LogEntries WHERE LOWER(Service) = ? AND Url IN ({}) AND DownloadId IS NOT NULL",
            placeholders
        );

        let mut stmt = conn.prepare(&query)?;
        let mut params: Vec<&dyn rusqlite::ToSql> = vec![&service_lower];
        for url in chunk {
            params.push(url);
        }

        let download_ids = stmt.query_map(rusqlite::params_from_iter(params.iter()), |row| {
            row.get::<_, i32>(0)
        })?;

        for download_id in download_ids {
            if let Ok(id) = download_id {
                download_ids_to_delete.insert(id);
            }
        }
    }

    eprintln!("  Found {} download sessions with corrupted entries", download_ids_to_delete.len());

    // STEP 2: Delete LogEntries with corrupted URLs
    for chunk in urls.chunks(batch_size) {
        let placeholders = vec!["?"; chunk.len()].join(", ");

        let log_entries_query = format!(
            "DELETE FROM LogEntries WHERE LOWER(Service) = ? AND Url IN ({})",
            placeholders
        );

        let mut log_stmt = conn.prepare(&log_entries_query)?;
        let mut params: Vec<&dyn rusqlite::ToSql> = vec![&service_lower];
        for url in chunk {
            params.push(url);
        }
        let log_deleted = log_stmt.execute(rusqlite::params_from_iter(params.iter()))?;
        total_log_entries_deleted += log_deleted;
    }

    eprintln!("  Deleted {} log entry records", total_log_entries_deleted);

    // STEP 3: Delete the entire Download sessions that had corrupted entries
    // Process download IDs in batches as well
    let download_ids_vec: Vec<i32> = download_ids_to_delete.into_iter().collect();

    for chunk in download_ids_vec.chunks(batch_size) {
        let placeholders = vec!["?"; chunk.len()].join(", ");

        // First delete all remaining LogEntries for these downloads (cleanup)
        let cleanup_query = format!(
            "DELETE FROM LogEntries WHERE DownloadId IN ({})",
            placeholders
        );

        let mut cleanup_stmt = conn.prepare(&cleanup_query)?;
        let params: Vec<&dyn rusqlite::ToSql> = chunk.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
        cleanup_stmt.execute(rusqlite::params_from_iter(params.iter()))?;

        // Then delete the Download records
        let downloads_query = format!(
            "DELETE FROM Downloads WHERE Id IN ({})",
            placeholders
        );

        let mut downloads_stmt = conn.prepare(&downloads_query)?;
        let params: Vec<&dyn rusqlite::ToSql> = chunk.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
        let downloads_deleted = downloads_stmt.execute(rusqlite::params_from_iter(params.iter()))?;
        total_downloads_deleted += downloads_deleted;
    }

    eprintln!("  Deleted {} download records (entire sessions with corrupted chunks)", total_downloads_deleted);

    Ok((total_downloads_deleted, total_log_entries_deleted))
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 2 {
        eprintln!("Usage:");
        eprintln!("  {} detect <log_dir> <cache_dir> <output_json> [timezone]", args[0]);
        eprintln!("  {} summary <log_dir> <cache_dir> [timezone] [threshold]", args[0]);
        eprintln!("  {} remove <database_path> <log_dir> <cache_dir> <service> <progress_json>", args[0]);
        eprintln!();
        eprintln!("Commands:");
        eprintln!("  detect  - Find corrupted chunks and output detailed JSON report");
        eprintln!("  summary - Quick JSON summary of corrupted chunk counts per service");
        eprintln!("  remove  - Delete database records, cache files, and log entries for corrupted chunks");
        eprintln!();
        eprintln!("Arguments:");
        eprintln!("  database_path - Path to LancacheManager.db");
        eprintln!("  log_dir      - Directory containing log files (e.g., /logs or H:/logs)");
        eprintln!("  cache_dir    - Cache directory root path (e.g., /cache or H:/cache)");
        eprintln!("  output_json  - Path to output JSON file");
        eprintln!("  service      - Service name to remove corrupted chunks for (e.g., steam, epic)");
        eprintln!("  progress_json - Path to progress JSON file for removal tracking");
        eprintln!("  timezone     - Optional timezone (default: UTC)");
        eprintln!("  threshold    - Optional miss threshold (default: 3)");
        std::process::exit(1);
    }

    let command = &args[1];

    match command.as_str() {
        "detect" => {
            if args.len() < 5 {
                eprintln!("Usage: {} detect <log_dir> <cache_dir> <output_json> [timezone]", args[0]);
                std::process::exit(1);
            }

            let log_dir = PathBuf::from(&args[2]);
            let cache_dir = PathBuf::from(&args[3]);
            let output_json = PathBuf::from(&args[4]);
            let timezone = if args.len() > 5 {
                parse_timezone(&args[5])
            } else {
                chrono_tz::UTC
            };

            println!("Detecting corrupted chunks...");
            println!("  Log directory: {}", log_dir.display());
            println!("  Cache directory: {}", cache_dir.display());
            println!("  Timezone: {}", timezone);

            let detector = CorruptionDetector::new(&cache_dir, 3);
            let report = detector.generate_report(&log_dir, "access.log", timezone)
                .context("Failed to generate corruption report")?;

            println!("Found {} corrupted chunks across {} services",
                report.summary.total_corrupted,
                report.summary.service_counts.len());

            // Write detailed report to JSON
            let json = serde_json::to_string_pretty(&report)?;
            let mut file = File::create(&output_json)?;
            file.write_all(json.as_bytes())?;
            file.flush()?;

            println!("Report saved to: {}", output_json.display());
        }

        "summary" => {
            if args.len() < 4 {
                eprintln!("Usage: {} summary <log_dir> <cache_dir> [timezone] [threshold]", args[0]);
                std::process::exit(1);
            }

            let log_dir = PathBuf::from(&args[2]);
            let cache_dir = PathBuf::from(&args[3]);
            let timezone = if args.len() > 4 {
                parse_timezone(&args[4])
            } else {
                chrono_tz::UTC
            };
            let threshold = if args.len() > 5 {
                args[5].parse::<usize>().unwrap_or(3)
            } else {
                3
            };

            // All diagnostic output to stderr so stdout only contains JSON
            eprintln!("Generating corruption summary...");
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());
            eprintln!("  Timezone: {}", timezone);
            eprintln!("  Miss threshold: {}", threshold);

            let detector = CorruptionDetector::new(&cache_dir, threshold);
            let summary = detector.generate_summary(&log_dir, "access.log", timezone)
                .context("Failed to generate corruption summary")?;

            // Output JSON to stdout for C# to capture (ONLY stdout should be JSON)
            let json = serde_json::to_string(&summary)?;
            println!("{}", json);
        }

        "remove" => {
            if args.len() < 7 {
                eprintln!("Usage: {} remove <database_path> <log_dir> <cache_dir> <service> <progress_json>", args[0]);
                std::process::exit(1);
            }

            let db_path = PathBuf::from(&args[2]);
            let log_dir = PathBuf::from(&args[3]);
            let cache_dir = PathBuf::from(&args[4]);
            let service = &args[5];
            let progress_path = PathBuf::from(&args[6]);

            eprintln!("Removing corrupted chunks for service: {}", service);
            eprintln!("  Database: {}", db_path.display());
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());

            write_progress(&progress_path, "starting", &format!("Starting removal for {}", service))?;

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
            let miss_threshold: usize = 3; // Same threshold as corruption_detector

            write_progress(&progress_path, "scanning", &format!("Scanning {} log files for corrupted chunks", log_files.len()))?;

            // First pass: identify all corrupted URLs AND track their response sizes
            for (file_index, log_file) in log_files.iter().enumerate() {
                eprintln!("  Scanning file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

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

                                    // MEMORY OPTIMIZATION: Periodically clean up entries that won't reach threshold
                                    if entries_processed % 100_000 == 0 {
                                        let before_size = miss_tracker.len();
                                        miss_tracker.retain(|_, count| *count >= miss_threshold - 1);
                                        // Also clean up url_sizes for URLs we're not tracking anymore
                                        url_sizes.retain(|url, _| miss_tracker.contains_key(url));
                                        // Actually release memory back to the system
                                        miss_tracker.shrink_to_fit();
                                        url_sizes.shrink_to_fit();
                                        let after_size = miss_tracker.len();
                                        if before_size > after_size {
                                            eprintln!("    Memory cleanup: Removed {} low-count entries (kept {})",
                                                before_size - after_size, after_size);
                                        }
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

            // Build map of corrupted URLs with their response sizes (those with threshold+ misses)
            let corrupted_urls_with_sizes: HashMap<String, i64> = miss_tracker
                .iter()
                .filter(|(_, &count)| count >= miss_threshold)
                .map(|(url, _)| {
                    let size = url_sizes.get(url).copied().unwrap_or(0);
                    (url.clone(), size)
                })
                .collect();

            eprintln!("Found {} corrupted URLs for {}", corrupted_urls_with_sizes.len(), service);

            if corrupted_urls_with_sizes.is_empty() {
                eprintln!("No corrupted chunks found, nothing to remove");
                write_progress(&progress_path, "complete", "No corrupted chunks found")?;
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

            write_progress(&progress_path, "filtering", &format!("Filtering {} log files", log_files.len()))?;

            for (file_index, log_file) in log_files.iter().enumerate() {
                eprintln!("  Processing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

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
                                // Check if this URL is corrupted (from Pass 1 scan)
                                if entry.service == service_lower && corrupted_urls.contains(&entry.url) {
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
                    // persist() handles platform differences (Windows vs Unix)
                    let temp_path = temp_file.into_temp_path();
                    temp_path.persist(&log_file.path)?;

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

            write_progress(&progress_path, "removing_cache", &format!("Removing cache files for {} corrupted URLs", corrupted_urls_with_sizes.len()))?;

            // Step 3: Delete ALL cache file chunks from disk
            // IMPORTANT: Use same logic as game_cache_remover - try no-range format first!
            eprintln!("Step 3: Deleting cache files...");
            let mut deleted_count = 0;
            let slice_size: i64 = 1_048_576; // 1MB

            for (url, response_size) in &corrupted_urls_with_sizes {
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
                        Err(e) => eprintln!("  Warning: Failed to delete {}: {}", cache_path_no_range.display(), e),
                    }
                } else {
                    // FALLBACK: Try the chunked format with bytes range
                    if *response_size == 0 {
                        // If no response size, check at least the first chunk
                        let cache_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, 0, 1_048_575);

                        if cache_path.exists() {
                            match std::fs::remove_file(&cache_path) {
                                Ok(_) => deleted_count += 1,
                                Err(e) => eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e),
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
                                    Err(e) => eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e),
                                }
                            }

                            start += slice_size;
                        }
                    }
                }
            }

            eprintln!("Deleted {} cache files", deleted_count);
            eprintln!("Removed {} total log lines across {} files", total_lines_removed, log_files.len());

            // Step 4: Delete database records for corrupted downloads
            eprintln!("Step 4: Deleting database records...");
            write_progress(&progress_path, "removing_database", "Deleting database records for corrupted chunks")?;

            let (downloads_deleted, log_entries_deleted) = delete_corrupted_from_database(&db_path, service, &corrupted_urls)?;

            write_progress(&progress_path, "complete", &format!("Removed {} corrupted URLs for {} ({} cache files deleted, {} log lines removed, {} downloads deleted, {} log entries deleted)", corrupted_urls_with_sizes.len(), service, deleted_count, total_lines_removed, downloads_deleted, log_entries_deleted))?;
            eprintln!("\n=== Corruption Removal Summary ===");
            eprintln!("Corrupted URLs removed: {}", corrupted_urls_with_sizes.len());
            eprintln!("Cache files deleted: {}", deleted_count);
            eprintln!("Log lines removed: {}", total_lines_removed);
            eprintln!("Database downloads deleted: {}", downloads_deleted);
            eprintln!("Database log entries deleted: {}", log_entries_deleted);
            eprintln!("Removal completed successfully");
        }

        _ => {
            eprintln!("Unknown command: {}", command);
            eprintln!("Valid commands: detect, summary, remove");
            std::process::exit(1);
        }
    }

    Ok(())
}
