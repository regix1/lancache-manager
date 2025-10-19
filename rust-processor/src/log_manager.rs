use anyhow::{Context, Result};
use chrono::Utc;
use serde::Serialize;
use std::collections::HashMap;
use std::env;
use std::fs::{self, File};
use std::io::{BufWriter, Write as IoWrite};
use std::path::Path;
use std::time::Instant;

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

mod log_discovery;
mod log_reader;

use log_discovery::discover_log_files;
use log_reader::LogFileReader;

#[derive(Serialize, Clone)]
struct ProgressData {
    #[serde(rename = "isProcessing")]
    is_processing: bool,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: String,
    message: String,
    #[serde(rename = "linesProcessed")]
    lines_processed: u64,
    #[serde(rename = "linesRemoved")]
    lines_removed: Option<u64>,
    #[serde(rename = "serviceCounts")]
    service_counts: Option<HashMap<String, u64>>,
    timestamp: String,
}

impl ProgressData {
    fn new(
        is_processing: bool,
        percent_complete: f64,
        status: String,
        message: String,
        lines_processed: u64,
        lines_removed: Option<u64>,
        service_counts: Option<HashMap<String, u64>>,
    ) -> Self {
        Self {
            is_processing,
            percent_complete,
            status,
            message,
            lines_processed,
            lines_removed,
            service_counts,
            timestamp: Utc::now().to_rfc3339(),
        }
    }
}

fn write_progress(progress_path: &Path, progress: &ProgressData) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;

    // Use atomic write-and-rename on all platforms to avoid race conditions
    // where C# reads the file while it's being truncated/written
    let temp_path = progress_path.with_extension("json.tmp");

    #[cfg(windows)]
    {
        use std::fs::OpenOptions;
        use std::io::Write;

        // Write to temp file with sharing flags
        let mut file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .share_mode(0x07) // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            .open(&temp_path)?;

        file.write_all(json.as_bytes())?;
        file.flush()?;
        drop(file); // Ensure file is closed before rename

        // Atomic rename - Windows allows this when file is opened with FILE_SHARE_DELETE
        fs::rename(&temp_path, progress_path)?;
    }

    #[cfg(not(windows))]
    {
        fs::write(&temp_path, &json)?;
        fs::rename(&temp_path, progress_path)?;
    }

    Ok(())
}


fn extract_service_from_line(line: &str) -> Option<String> {
    // Log format: [service] ...
    if line.starts_with('[') {
        if let Some(end_idx) = line.find(']') {
            let service = line[1..end_idx].to_lowercase();

            // Normalize service names - use common name if it's an IP or localhost
            if service.starts_with("127.") || service == "127" || service == "localhost" {
                return Some("localhost".to_string());
            }

            // If it looks like an IP address (has dots and numbers), group as "ip-address"
            if service.contains('.') && service.chars().any(|c| c.is_numeric()) {
                // Check if it's mostly numbers and dots (likely an IP)
                let non_ip_chars = service.chars().filter(|c| !c.is_numeric() && *c != '.').count();
                if non_ip_chars == 0 {
                    return Some("ip-address".to_string());
                }
            }

            return Some(service);
        }
    }
    None
}

fn count_services(log_path: &str, progress_path: &Path) -> Result<HashMap<String, u64>> {
    let start_time = Instant::now();
    eprintln!("Counting services in log files...");

    // Determine if log_path is a file or directory
    let (log_dir, base_name) = if Path::new(log_path).is_dir() {
        (Path::new(log_path), "access.log")
    } else {
        // Extract directory and base name from file path
        let path = Path::new(log_path);
        let dir = path.parent().context("Failed to get parent directory")?;
        let name = path.file_name()
            .and_then(|n| n.to_str())
            .context("Failed to get file name")?;
        (dir, name)
    };

    // Discover all log files (access.log, access.log.1, access.log.2.gz, etc.)
    let log_files = discover_log_files(log_dir, base_name)?;

    if log_files.is_empty() {
        eprintln!("No log files found matching pattern: {}", base_name);
        return Ok(HashMap::new());
    }

    eprintln!("Found {} log file(s) to process:", log_files.len());
    for log_file in &log_files {
        eprintln!("  - {}", log_file.path.display());
    }

    // Calculate total size across all files for progress tracking
    let mut total_size = 0u64;
    for log_file in &log_files {
        if let Ok(metadata) = std::fs::metadata(&log_file.path) {
            total_size += metadata.len();
        }
    }

    let mut service_counts: HashMap<String, u64> = HashMap::new();
    let mut lines_processed: u64 = 0;
    let mut bytes_processed: u64 = 0;
    let mut last_progress_update = Instant::now();

    // Process each log file in order (oldest to newest)
    for (file_index, log_file) in log_files.iter().enumerate() {
        eprintln!("\nProcessing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

        // Try to open and read the file, but skip if it's corrupted
        let file_result = (|| -> Result<()> {
            let mut log_reader = LogFileReader::open(&log_file.path)?;
            let mut line = String::new();

            loop {
                line.clear();
                let bytes_read = log_reader.read_line(&mut line)?;
                if bytes_read == 0 {
                    break; // EOF
                }

                bytes_processed += line.len() as u64;
                lines_processed += 1;

                if let Some(service) = extract_service_from_line(line.trim()) {
                    // Use the service name directly - auto-discover all services
                    *service_counts.entry(service).or_insert(0) += 1;
                }

                // Update progress every 500ms
                if last_progress_update.elapsed().as_millis() > 500 {
                    let percent = if total_size > 0 {
                        (bytes_processed as f64 / total_size as f64) * 100.0
                    } else {
                        0.0
                    };

                    let progress = ProgressData::new(
                        true,
                        percent,
                        "counting".to_string(),
                        format!("Counting services... {} lines processed across {} files", lines_processed, file_index + 1),
                        lines_processed,
                        None,
                        None,
                    );
                    write_progress(progress_path, &progress)?;
                    last_progress_update = Instant::now();
                }
            }
            Ok(())
        })();

        // If this file failed (e.g., corrupted gzip), log warning and skip it
        if let Err(e) = file_result {
            eprintln!("WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
            eprintln!("  Continuing with remaining files...");
            continue;
        }
    }

    let elapsed = start_time.elapsed();
    eprintln!("\n✓ Service counting completed!");
    eprintln!("  Files processed: {}", log_files.len());
    eprintln!("  Lines processed: {}", lines_processed);
    eprintln!("  Services found: {}", service_counts.len());
    eprintln!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    for (service, count) in &service_counts {
        eprintln!("  {}: {}", service, count);
    }

    // Final progress with service counts
    let progress = ProgressData::new(
        false,
        100.0,
        "complete".to_string(),
        format!("Service counting completed. Found {} services in {} lines across {} files.",
            service_counts.len(), lines_processed, log_files.len()),
        lines_processed,
        None,
        Some(service_counts.clone()),
    );
    write_progress(progress_path, &progress)?;

    Ok(service_counts)
}

fn remove_service_from_logs(
    log_path: &str,
    service_to_remove: &str,
    progress_path: &Path,
) -> Result<()> {
    let start_time = Instant::now();
    eprintln!("Removing {} entries from log files...", service_to_remove);

    // Determine the target log file(s)
    // For removal, we want to process all log files (current + rotated)
    let (log_dir, base_name) = if Path::new(log_path).is_dir() {
        (Path::new(log_path), "access.log")
    } else {
        let path = Path::new(log_path);
        let dir = path.parent().context("Failed to get parent directory")?;
        let name = path.file_name()
            .and_then(|n| n.to_str())
            .context("Failed to get file name")?;
        (dir, name)
    };

    // Discover all log files
    let log_files = discover_log_files(log_dir, base_name)?;

    if log_files.is_empty() {
        eprintln!("No log files found matching pattern: {}", base_name);
        return Ok(());
    }

    eprintln!("Found {} log file(s) to process:", log_files.len());
    for log_file in &log_files {
        eprintln!("  - {}", log_file.path.display());
    }

    let mut total_lines_processed: u64 = 0;
    let mut total_lines_removed: u64 = 0;

    // Process each log file
    for (file_index, log_file) in log_files.iter().enumerate() {
        eprintln!("\nProcessing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

        // Try to process the file, but skip if it's corrupted (e.g., invalid gzip header)
        let file_result = (|| -> Result<(u64, u64)> {
            let file_path_str = log_file.path.to_str().context("Invalid file path")?;

            // Create backup for this file
            let backup_path = format!("{}.bak", file_path_str);
            fs::copy(&log_file.path, &backup_path).context("Failed to create backup")?;
            eprintln!("Backup created at: {}", backup_path);

            // Create temp file for filtered output
            let file_dir = log_file.path.parent().context("Failed to get file directory")?;
            let temp_path = file_dir.join(format!("access.log.tmp.{}.{}", Utc::now().timestamp(), file_index));

            let mut lines_processed: u64 = 0;
            let mut lines_removed: u64 = 0;
            let service_lower = service_to_remove.to_lowercase();

            // Scope the file operations so handles are closed before deletion
            {
                // Use LogFileReader for automatic compression support (.gz, .zst)
                let mut log_reader = LogFileReader::open(&log_file.path)?;
                let file_size = std::fs::metadata(&log_file.path)?.len();

                let temp_file = File::create(&temp_path).context("Failed to create temp file")?;
                let mut writer = BufWriter::with_capacity(1024 * 1024, temp_file);

                let mut bytes_processed: u64 = 0;
                let mut last_progress_update = Instant::now();
                let mut line = String::new();

                // Progress update for this file
                let progress = ProgressData::new(
                    true,
                    0.0,
                    "removing".to_string(),
                    format!("Processing file {}/{}: removing {} entries...", file_index + 1, log_files.len(), service_to_remove),
                    total_lines_processed,
                    Some(total_lines_removed),
                    None,
                );
                write_progress(progress_path, &progress)?;

                loop {
                    line.clear();
                    let bytes_read = log_reader.read_line(&mut line)?;
                    if bytes_read == 0 {
                        break; // EOF
                    }

                    bytes_processed += line.len() as u64;
                    lines_processed += 1;

                    let mut should_remove = false;

                    if let Some(line_service) = extract_service_from_line(line.trim()) {
                        // Exact match - remove entries for this service
                        if line_service == service_lower {
                            should_remove = true;
                        }
                    }

                    if !should_remove {
                        write!(writer, "{}", line)?;
                    } else {
                        lines_removed += 1;
                        if lines_removed.is_multiple_of(10000) {
                            eprintln!("Removed {} {} entries from this file", lines_removed, service_to_remove);
                        }
                    }

                    // Update progress every 500ms
                    if last_progress_update.elapsed().as_millis() > 500 {
                        let percent = if file_size > 0 {
                            (bytes_processed as f64 / file_size as f64) * 100.0
                        } else {
                            0.0
                        };

                        let progress = ProgressData::new(
                            true,
                            percent,
                            "removing".to_string(),
                            format!(
                                "File {}/{}: {} lines processed, {} removed",
                                file_index + 1, log_files.len(), total_lines_processed + lines_processed, total_lines_removed + lines_removed
                            ),
                            total_lines_processed + lines_processed,
                            Some(total_lines_removed + lines_removed),
                            None,
                        );
                        write_progress(progress_path, &progress)?;
                        last_progress_update = Instant::now();
                    }
                }

                // Flush and close writer
                writer.flush()?;
                drop(writer);

                // reader and file are automatically dropped here when scope ends
            }

            // Check if the filtered file would be empty (all lines removed)
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!("  WARNING: ALL {} lines from this file would be removed", lines_processed);
                eprintln!("    Keeping original file unchanged and skipping to avoid data loss");
                // Delete the empty temp file
                fs::remove_file(&temp_path).ok();
                return Ok((lines_processed, 0)); // Report 0 lines removed to indicate we skipped this file
            }

            // Replace original with filtered version (only if some lines remain)
            // Use atomic file replacement to avoid race conditions
            #[cfg(windows)]
            {
                // On Windows, use copy-and-delete to avoid the rename gap
                // First copy the temp file over the original (atomic operation)
                fs::copy(&temp_path, &log_file.path).context("Failed to copy temp file over original")?;
                // Then delete the temp file
                fs::remove_file(&temp_path).ok(); // Ignore errors on cleanup
            }
            #[cfg(not(windows))]
            {
                // On Unix, rename is atomic and will replace the existing file
                fs::rename(&temp_path, &log_file.path).context("Failed to rename temp file")?;
            }

            Ok((lines_processed, lines_removed))
        })();

        // If this file failed (e.g., corrupted gzip), log warning and skip it
        match file_result {
            Ok((lines_processed, lines_removed)) => {
                eprintln!("  Lines processed: {}", lines_processed);
                eprintln!("  Lines removed: {}", lines_removed);

                total_lines_processed += lines_processed;
                total_lines_removed += lines_removed;
            }
            Err(e) => {
                eprintln!("WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                eprintln!("  Continuing with remaining files...");
                continue;
            }
        }
    }

    let elapsed = start_time.elapsed();
    eprintln!("\n✓ Log filtering completed!");
    eprintln!("  Files processed: {}", log_files.len());
    eprintln!("  Lines processed: {}", total_lines_processed);
    eprintln!("  Lines removed: {}", total_lines_removed);
    eprintln!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    // Final progress
    let progress = ProgressData::new(
        false,
        100.0,
        "complete".to_string(),
        format!(
            "Removed {} {} entries from {} total lines across {} files in {:.2}s",
            total_lines_removed,
            service_to_remove,
            total_lines_processed,
            log_files.len(),
            elapsed.as_secs_f64()
        ),
        total_lines_processed,
        Some(total_lines_removed),
        None,
    );
    write_progress(progress_path, &progress)?;

    Ok(())
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() < 4 {
        eprintln!("Usage:");
        eprintln!("  log_manager count <log_path_or_directory> <progress_json_path>");
        eprintln!("  log_manager remove <log_path_or_directory> <service_name> <progress_json_path>");
        eprintln!("\nExamples:");
        eprintln!("  log_manager count ./logs ./data/log_count_progress.json");
        eprintln!("  log_manager count ./logs/access.log ./data/log_count_progress.json");
        eprintln!("  log_manager remove ./logs steam ./data/log_remove_progress.json");
        eprintln!("\nNote: Will automatically discover and process all log files (access.log, access.log.1, .gz, .zst)");
        std::process::exit(1);
    }

    let command = &args[1];
    let log_path = &args[2];

    match command.as_str() {
        "count" => {
            if args.len() != 4 {
                eprintln!("Usage: log_manager count <log_path_or_directory> <progress_json_path>");
                std::process::exit(1);
            }
            let progress_path = Path::new(&args[3]);

            match count_services(log_path, progress_path) {
                Ok(_) => {
                    std::process::exit(0);
                }
                Err(e) => {
                    eprintln!("Error: {:?}", e);
                    let error_progress = ProgressData::new(
                        false,
                        0.0,
                        "error".to_string(),
                        format!("Service counting failed: {}", e),
                        0,
                        None,
                        None,
                    );
                    let _ = write_progress(progress_path, &error_progress);
                    std::process::exit(1);
                }
            }
        }
        "remove" => {
            if args.len() != 5 {
                eprintln!("Usage: log_manager remove <log_path_or_directory> <service_name> <progress_json_path>");
                std::process::exit(1);
            }
            let service_name = &args[3];
            let progress_path = Path::new(&args[4]);

            match remove_service_from_logs(log_path, service_name, progress_path) {
                Ok(_) => {
                    std::process::exit(0);
                }
                Err(e) => {
                    eprintln!("Error: {:?}", e);
                    let error_progress = ProgressData::new(
                        false,
                        0.0,
                        "error".to_string(),
                        format!("Service removal failed: {}", e),
                        0,
                        Some(0),
                        None,
                    );
                    let _ = write_progress(progress_path, &error_progress);
                    std::process::exit(1);
                }
            }
        }
        _ => {
            eprintln!("Unknown command: {}", command);
            eprintln!("Valid commands: count, remove");
            std::process::exit(1);
        }
    }
}
