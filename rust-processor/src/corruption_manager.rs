use anyhow::{Context, Result};
use serde::Serialize;
use std::env;
use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};
use tempfile::NamedTempFile;

mod corruption_detector;
mod log_discovery;
mod log_reader;
mod models;
mod parser;
mod progress_utils;
mod service_utils;

use corruption_detector::CorruptionDetector;

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

fn build_cache_path(cache_dir: &Path, hash: &str) -> Option<PathBuf> {
    let len = hash.len();
    if len < 4 {
        return None;
    }

    let last_2 = &hash[len - 2..];
    let middle_2 = &hash[len - 4..len - 2];
    Some(cache_dir.join(last_2).join(middle_2).join(hash))
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 2 {
        eprintln!("Usage:");
        eprintln!("  {} detect <log_dir> <cache_dir> <output_json> [timezone]", args[0]);
        eprintln!("  {} summary <log_dir> <cache_dir> [timezone] [threshold]", args[0]);
        eprintln!("  {} remove <log_dir> <cache_dir> <service> <progress_json>", args[0]);
        eprintln!();
        eprintln!("Commands:");
        eprintln!("  detect  - Find corrupted chunks and output detailed JSON report");
        eprintln!("  summary - Quick JSON summary of corrupted chunk counts per service");
        eprintln!("  remove  - Delete cache files and log entries for a specific service");
        eprintln!();
        eprintln!("Arguments:");
        eprintln!("  log_dir      - Directory containing log files (e.g., /logs or H:/logs)");
        eprintln!("  cache_dir    - Cache directory root path (e.g., /cache or H:/cache)");
        eprintln!("  output_json  - Path to output JSON file");
        eprintln!("  service      - Service name to remove (e.g., steam, epic)");
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
            if args.len() < 6 {
                eprintln!("Usage: {} remove <log_dir> <cache_dir> <service> <progress_json>", args[0]);
                std::process::exit(1);
            }

            let log_dir = PathBuf::from(&args[2]);
            let cache_dir = PathBuf::from(&args[3]);
            let service = &args[4];
            let progress_path = PathBuf::from(&args[5]);

            eprintln!("Removing corrupted chunks for service: {}", service);
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());

            write_progress(&progress_path, "starting", &format!("Starting removal for {}", service))?;

            // OPTIMIZED: Single-pass detection and removal
            // Instead of reading logs twice (once to detect, once to remove),
            // we detect corrupted URLs while filtering the log files in a single pass

            use log_reader::LogFileReader;
            use std::io::Write as IoWrite;
            use std::io::BufWriter;
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
                    // Create temp file for filtered output with automatic cleanup
                    let file_dir = log_file.path.parent().context("Failed to get file directory")?;
                    let temp_file = NamedTempFile::new_in(file_dir)?;

                    let mut lines_removed: u64 = 0;
                    let mut lines_processed: u64 = 0;

                    {
                        let mut log_reader = LogFileReader::open(&log_file.path)?;
                        let mut writer = BufWriter::with_capacity(1024 * 1024, temp_file.as_file());
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

            // Step 3: Delete ALL cache file chunks from disk (not just the first 1MB!)
            eprintln!("Step 3: Deleting cache files...");
            let mut deleted_count = 0;
            let slice_size: i64 = 1_048_576; // 1MB

            for (url, response_size) in &corrupted_urls_with_sizes {
                // Calculate ALL chunks for this URL based on response size
                // This matches the C# logic: CalculateRanges(ResponseSizeBytes)

                if *response_size == 0 {
                    // If no response size, assume at least first chunk exists
                    let cache_key = format!("{}{}bytes=0-1048575", service_lower, url);
                    let hash = format!("{:x}", md5::compute(cache_key.as_bytes()));

                    if let Some(cache_file) = build_cache_path(&cache_dir, &hash) {
                        if cache_file.exists() {
                            match std::fs::remove_file(&cache_file) {
                                Ok(_) => deleted_count += 1,
                                Err(e) => eprintln!("  Warning: Failed to delete {}: {}", cache_file.display(), e),
                            }
                        }
                    }
                } else {
                    // Calculate all 1MB slices based on actual response size
                    let mut start: i64 = 0;
                    while start < *response_size {
                        let end = (start + slice_size - 1).min(*response_size - 1 + slice_size - 1);

                        let cache_key = format!("{}{}bytes={}-{}", service_lower, url, start, end);
                        let hash = format!("{:x}", md5::compute(cache_key.as_bytes()));

                        if let Some(cache_file) = build_cache_path(&cache_dir, &hash) {
                            if cache_file.exists() {
                                match std::fs::remove_file(&cache_file) {
                                    Ok(_) => {
                                        deleted_count += 1;
                                        if deleted_count % 100 == 0 {
                                            eprintln!("  Deleted {} cache files...", deleted_count);
                                        }
                                    }
                                    Err(e) => eprintln!("  Warning: Failed to delete {}: {}", cache_file.display(), e),
                                }
                            }
                        }

                        start += slice_size;
                    }
                }
            }

            eprintln!("Deleted {} cache files", deleted_count);
            eprintln!("Removed {} total log lines across {} files", total_lines_removed, log_files.len());

            write_progress(&progress_path, "complete", &format!("Removed {} corrupted URLs for {} ({} cache files deleted, {} log lines removed)", corrupted_urls_with_sizes.len(), service, deleted_count, total_lines_removed))?;
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
