use anyhow::{Context, Result};
use serde::Serialize;
use std::env;
use std::fs::File;
use std::io::Write;
use std::path::PathBuf;

mod corruption_detector;
mod log_discovery;
mod log_reader;
mod models;
mod parser;

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

fn write_progress(progress_path: &PathBuf, status: &str, message: &str) -> Result<()> {
    let progress = ProgressData {
        status: status.to_string(),
        message: message.to_string(),
        timestamp: chrono::Utc::now().to_rfc3339(),
    };

    let json = serde_json::to_string_pretty(&progress)?;
    let mut file = File::create(progress_path)?;
    file.write_all(json.as_bytes())?;
    file.flush()?;

    Ok(())
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

            // Step 1: Detect corrupted chunks for this service
            eprintln!("Step 1: Detecting corrupted chunks for {}...", service);
            let detector = CorruptionDetector::new(&cache_dir, 3);
            let corrupted_map = detector.detect_corrupted_chunks(&log_dir, "access.log", chrono_tz::UTC)
                .context("Failed to detect corrupted chunks")?;

            // Filter to only this service
            let service_corrupted: Vec<_> = corrupted_map
                .into_iter()
                .filter(|((s, _url), _count)| s == service)
                .collect();

            eprintln!("Found {} corrupted chunks for {}", service_corrupted.len(), service);

            write_progress(&progress_path, "removing_cache", &format!("Removing {} cache files for {}", service_corrupted.len(), service))?;

            // Step 2: Delete cache files from disk
            eprintln!("Step 2: Deleting cache files...");
            let mut deleted_count = 0;
            for ((svc, url), _count) in &service_corrupted {
                // Calculate cache file path for the first 1MB slice
                let cache_key = format!("{}{}bytes=0-1048575", svc, url);
                let hash = format!("{:x}", md5::compute(cache_key.as_bytes()));

                let len = hash.len();
                if len >= 4 {
                    let last_2 = &hash[len - 2..];
                    let middle_2 = &hash[len - 4..len - 2];
                    let cache_file = cache_dir.join(last_2).join(middle_2).join(&hash);

                    if cache_file.exists() {
                        match std::fs::remove_file(&cache_file) {
                            Ok(_) => {
                                deleted_count += 1;
                                if deleted_count % 100 == 0 {
                                    eprintln!("  Deleted {} cache files...", deleted_count);
                                }
                            }
                            Err(e) => {
                                eprintln!("  Warning: Failed to delete {}: {}", cache_file.display(), e);
                            }
                        }
                    }
                }
            }

            eprintln!("Deleted {} cache files", deleted_count);

            write_progress(&progress_path, "removing_logs", &format!("Removing {} log entries", service))?;

            // Step 3: Remove log lines for this service using log_manager functionality
            eprintln!("Step 3: Removing log entries for {}...", service);

            // Import the log filtering functionality
            use log_reader::LogFileReader;
            use std::io::Write as IoWrite;
            use std::fs::File;
            use std::io::BufWriter;
            use chrono::Utc;

            // Discover all log files
            let log_files = crate::log_discovery::discover_log_files(&log_dir, "access.log")?;
            let mut total_lines_removed: u64 = 0;

            for (file_index, log_file) in log_files.iter().enumerate() {
                eprintln!("  Processing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

                // Try to process the file, but skip if it's corrupted (same pattern as Step 1)
                let file_result = (|| -> Result<u64> {
                    // Create backup
                    let backup_path = format!("{}.bak", log_file.path.display());
                    std::fs::copy(&log_file.path, &backup_path)
                        .context("Failed to create backup")?;

                    // Create temp file for filtered output
                    let file_dir = log_file.path.parent().context("Failed to get file directory")?;
                    let temp_path = file_dir.join(format!("access.log.corruption_tmp.{}.{}", Utc::now().timestamp(), file_index));

                    let mut lines_removed: u64 = 0;
                    let mut lines_processed: u64 = 0;
                    let service_lower = service.to_lowercase();

                    // Scope to ensure files are closed
                    {
                        let mut log_reader = LogFileReader::open(&log_file.path)?;
                        let temp_file = File::create(&temp_path)?;
                        let mut writer = BufWriter::with_capacity(1024 * 1024, temp_file);
                        let mut line = String::new();

                        loop {
                            line.clear();
                            let bytes_read = log_reader.read_line(&mut line)?;
                            if bytes_read == 0 {
                                break; // EOF
                            }

                            lines_processed += 1;

                            // Check if line should be removed (only lines for this service with MISS/UNKNOWN)
                            let mut should_remove = false;

                            // Extract service from line (format: [service] ... status)
                            if line.starts_with('[') {
                                if let Some(end_idx) = line.find(']') {
                                    let line_service = line[1..end_idx].to_lowercase();
                                    if line_service == service_lower {
                                        // Check if it's a MISS or UNKNOWN status (quoted in nginx log format: "MISS" or "UNKNOWN")
                                        if line.contains("\"MISS\"") || line.contains("\"UNKNOWN\"") {
                                            should_remove = true;
                                        }
                                    }
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

                    // Check if the filtered file would be empty (all lines removed)
                    if lines_processed > 0 && lines_removed == lines_processed {
                        eprintln!("  WARNING: ALL {} lines from this file would be removed", lines_processed);
                        eprintln!("    Keeping original file unchanged and skipping to avoid data loss");
                        // Delete the empty temp file
                        std::fs::remove_file(&temp_path).ok();
                        return Ok(0); // Report 0 lines removed to indicate we skipped this file
                    }

                    // Replace original with filtered version (only if some lines remain)
                    // Use atomic file replacement to avoid race conditions
                    #[cfg(windows)]
                    {
                        // On Windows, use copy-and-delete to avoid the rename gap
                        // First copy the temp file over the original (atomic operation)
                        std::fs::copy(&temp_path, &log_file.path)?;
                        // Then delete the temp file
                        std::fs::remove_file(&temp_path).ok(); // Ignore errors on cleanup
                    }
                    #[cfg(not(windows))]
                    {
                        // On Unix, rename is atomic and will replace the existing file
                        std::fs::rename(&temp_path, &log_file.path)?;
                    }

                    Ok(lines_removed)
                })();

                // If this file failed (e.g., corrupted gzip), log warning and skip it
                match file_result {
                    Ok(lines_removed) => {
                        eprintln!("    Removed {} log lines from this file", lines_removed);
                        total_lines_removed += lines_removed;
                    }
                    Err(e) => {
                        eprintln!("  WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                        eprintln!("    Continuing with remaining files...");
                        continue;
                    }
                }
            }

            eprintln!("Removed {} total log lines across {} files", total_lines_removed, log_files.len());

            write_progress(&progress_path, "complete", &format!("Removed {} corrupted chunks for {} ({} cache files deleted, {} log lines removed)", service_corrupted.len(), service, deleted_count, total_lines_removed))?;
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
