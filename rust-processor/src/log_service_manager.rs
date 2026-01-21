use anyhow::{Context, Result};
use serde::Serialize;
use std::collections::HashMap;
use std::env;
use std::fs;
use std::io::{BufWriter, Write as IoWrite};
use std::path::Path;
use std::time::Instant;
use tempfile::NamedTempFile;

mod log_discovery;
mod log_reader;
mod progress_utils;
mod service_utils;

use log_discovery::discover_log_files;
use log_reader::LogFileReader;

#[derive(Serialize, Clone)]
struct ProgressData {
    is_processing: bool,
    percent_complete: f64,
    status: String,
    message: String,
    lines_processed: u64,
    lines_removed: u64,
    files_processed: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    service_counts: Option<HashMap<String, u64>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    datasource_name: Option<String>,
    timestamp: String,
}

impl ProgressData {
    fn new(
        is_processing: bool,
        percent_complete: f64,
        status: String,
        message: String,
        lines_processed: u64,
        lines_removed: u64,
        files_processed: usize,
        service_counts: Option<HashMap<String, u64>>,
        datasource_name: Option<String>,
    ) -> Self {
        Self {
            is_processing,
            percent_complete,
            status,
            message,
            lines_processed,
            lines_removed,
            files_processed,
            service_counts,
            datasource_name,
            timestamp: progress_utils::current_timestamp(),
        }
    }
}

fn write_progress(progress_path: &Path, progress: &ProgressData) -> Result<()> {
    // Use shared progress writing utility
    progress_utils::write_progress_json(progress_path, progress)
}


// Use the shared service extraction utility for consistency
fn extract_service_from_line(line: &str) -> Option<String> {
    service_utils::extract_service_from_line(line)
}

fn count_services(log_path: &str, progress_path: &Path, datasource_name: Option<&str>) -> Result<HashMap<String, u64>> {
    let start_time = Instant::now();
    let ds_name = datasource_name.map(|s| s.to_string());
    if let Some(ds) = &ds_name {
        eprintln!("Counting services in log files for datasource: {}", ds);
    } else {
        eprintln!("Counting services in log files...");
    }

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

        // Write progress with empty service counts so UI updates correctly
        let progress = ProgressData::new(
            false,
            100.0,
            "complete".to_string(),
            "No log files found".to_string(),
            0,
            0,
            0,
            Some(HashMap::new()),
            ds_name.clone(),
        );
        write_progress(progress_path, &progress)?;

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
                        0,
                        file_index + 1,
                        None,
                        ds_name.clone(),
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
    eprintln!("\nService counting completed!");
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
        0,
        log_files.len(),
        Some(service_counts.clone()),
        ds_name,
    );
    write_progress(progress_path, &progress)?;

    Ok(service_counts)
}

fn remove_service_from_logs(
    log_path: &str,
    service_to_remove: &str,
    progress_path: &Path,
    datasource_name: Option<&str>,
) -> Result<()> {
    let start_time = Instant::now();
    let ds_name = datasource_name.map(|s| s.to_string());
    if let Some(ds) = &ds_name {
        eprintln!("Removing {} entries from log files for datasource: {}", service_to_remove, ds);
    } else {
        eprintln!("Removing {} entries from log files...", service_to_remove);
    }

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
    let mut permission_errors: usize = 0;

    // Process each log file
    for (file_index, log_file) in log_files.iter().enumerate() {
        eprintln!("\nProcessing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

        // Try to process the file, but skip if it's corrupted (e.g., invalid gzip header)
        let file_result = (|| -> Result<(u64, u64)> {
            // Create temp file for filtered output with automatic cleanup
            let file_dir = log_file.path.parent().context("Failed to get file directory")?;
            let temp_file = NamedTempFile::new_in(file_dir)?;

            let mut lines_processed: u64 = 0;
            let mut lines_removed: u64 = 0;
            let service_lower = service_to_remove.to_lowercase();

            // Scope the file operations so handles are closed before deletion
            {
                // Use LogFileReader for automatic compression support (.gz, .zst)
                let mut log_reader = LogFileReader::open(&log_file.path)?;
                let file_size = std::fs::metadata(&log_file.path)?.len();

                let mut writer = BufWriter::with_capacity(1024 * 1024, temp_file.as_file());

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
                    total_lines_removed,
                    file_index + 1,
                    None,
                    ds_name.clone(),
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
                        if lines_removed % 10000 == 0 {
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

                        let current_removed = total_lines_removed + lines_removed;
                        let current_processed = total_lines_processed + lines_processed;
                        let message = if current_removed > 0 {
                            format!(
                                "File {}/{}: {} lines processed, {} removed",
                                file_index + 1, log_files.len(), current_processed, current_removed
                            )
                        } else {
                            format!(
                                "File {}/{}: {} lines processed",
                                file_index + 1, log_files.len(), current_processed
                            )
                        };

                        let progress = ProgressData::new(
                            true,
                            percent,
                            "removing".to_string(),
                            message,
                            current_processed,
                            current_removed,
                            file_index + 1,
                            None,
                            ds_name.clone(),
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

            // Allow removing all lines - user may want to clear all entries for a service
            // If file would be empty, just delete it instead of leaving an empty file
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!("  INFO: All {} lines from this file will be removed", lines_processed);
                eprintln!("    Deleting the log file entirely");
                // temp_file automatically deleted when it goes out of scope
                // Delete the original log file
                fs::remove_file(&log_file.path).ok();
                return Ok((lines_processed, lines_removed));
            }

            // Atomically replace original with filtered version
            // persist() uses rename which can fail on Windows if file is locked
            let temp_path = temp_file.into_temp_path();

            if let Err(persist_err) = temp_path.persist(&log_file.path) {
                // Fallback: copy + delete (works even if target is locked by file watcher)
                eprintln!("    persist() failed ({}), using copy fallback...", persist_err);
                fs::copy(&persist_err.path, &log_file.path)?;
                fs::remove_file(&persist_err.path).ok();
            }

            Ok((lines_processed, lines_removed))
        })();

        // If this file failed, check if it's a permission error
        match file_result {
            Ok((lines_processed, lines_removed)) => {
                eprintln!("  Lines processed: {}", lines_processed);
                eprintln!("  Lines removed: {}", lines_removed);

                total_lines_processed += lines_processed;
                total_lines_removed += lines_removed;
            }
            Err(e) => {
                // Check if this is a permission error
                let error_str = e.to_string();
                if error_str.contains("Permission denied") || error_str.contains("os error 13") {
                    permission_errors += 1;
                    eprintln!("ERROR: Permission denied for file {}: {}", log_file.path.display(), e);
                } else {
                    eprintln!("WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                }
                eprintln!("  Continuing with remaining files...");
                continue;
            }
        }
    }

    let elapsed = start_time.elapsed();

    // CRITICAL: Check for permission errors and fail if any occurred
    // This prevents the UI from showing success when files couldn't be modified
    if permission_errors > 0 {
        let error_msg = format!(
            "FAILED: {} log file(s) could not be modified due to permission errors. \
            This is likely caused by incorrect PUID/PGID settings in your docker-compose.yml. \
            The lancache container usually runs as UID/GID 33:33 (www-data).",
            permission_errors
        );
        eprintln!("\n{}", error_msg);
        
        let progress = ProgressData::new(
            false,
            0.0,
            "failed".to_string(),
            error_msg.clone(),
            total_lines_processed,
            total_lines_removed,
            log_files.len(),
            None,
            ds_name,
        );
        write_progress(progress_path, &progress)?;
        
        anyhow::bail!("{}", error_msg);
    }

    eprintln!("\nLog filtering completed!");
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
        total_lines_removed,
        log_files.len(),
        None,
        ds_name,
    );
    write_progress(progress_path, &progress)?;

    Ok(())
}

/// Check if cached progress file is still valid (newer than all log files)
/// Returns Ok with counts if cache is valid, Err if cache should be regenerated
fn check_cache_validity(log_path: &str, progress_path: &Path) -> Result<HashMap<String, u64>> {
    // Check if progress file exists
    if !progress_path.exists() {
        return Err(anyhow::anyhow!("Progress file doesn't exist"));
    }

    // Get progress file modification time
    let progress_metadata = fs::metadata(progress_path)?;
    let progress_modified = progress_metadata.modified()?;

    // Determine log directory and base name
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
        return Err(anyhow::anyhow!("No log files found"));
    }

    // Check if any log file is newer than progress file
    for log_file in &log_files {
        if let Ok(log_metadata) = fs::metadata(&log_file.path) {
            if let Ok(log_modified) = log_metadata.modified() {
                if log_modified > progress_modified {
                    return Err(anyhow::anyhow!("Log file {} is newer than progress file", log_file.path.display()));
                }
            }
        }
    }

    // Cache is valid, read and return the service counts
    let json = fs::read_to_string(progress_path)?;

    #[derive(serde::Deserialize)]
    struct CachedProgress {
        service_counts: Option<HashMap<String, u64>>,
    }

    let cached: CachedProgress = serde_json::from_str(&json)?;

    if let Some(counts) = cached.service_counts {
        Ok(counts)
    } else {
        Err(anyhow::anyhow!("Progress file doesn't contain service counts"))
    }
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() < 4 {
        eprintln!("Usage:");
        eprintln!("  log_manager count <log_path_or_directory> <progress_json_path> [datasource_name]");
        eprintln!("  log_manager remove <log_path_or_directory> <service_name> <progress_json_path> [datasource_name]");
        eprintln!("\nExamples:");
        eprintln!("  log_manager count ./logs ./data/log_count_progress.json");
        eprintln!("  log_manager count ./logs ./data/log_count_progress.json my-lancache");
        eprintln!("  log_manager remove ./logs steam ./data/log_remove_progress.json");
        eprintln!("  log_manager remove ./logs steam ./data/log_remove_progress.json my-lancache");
        eprintln!("\nNote: Will automatically discover and process all log files (access.log, access.log.1, .gz, .zst)");
        std::process::exit(1);
    }

    let command = &args[1];
    let log_path = &args[2];

    match command.as_str() {
        "count" => {
            if args.len() < 4 || args.len() > 5 {
                eprintln!("Usage: log_manager count <log_path_or_directory> <progress_json_path> [datasource_name]");
                std::process::exit(1);
            }
            let progress_path = Path::new(&args[3]);
            let datasource_name = args.get(4).map(|s| s.as_str());

            if let Some(ds) = datasource_name {
                eprintln!("Processing for datasource: {}", ds);
            }

            // Check if cached progress is still valid
            if let Ok(_cached_counts) = check_cache_validity(log_path, progress_path) {
                eprintln!("Using cached service counts (progress file is newer than all log files)");
                std::process::exit(0);
            }

            match count_services(log_path, progress_path, datasource_name) {
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
                        0,
                        0,
                        None,
                        datasource_name.map(|s| s.to_string()),
                    );
                    let _ = write_progress(progress_path, &error_progress);
                    std::process::exit(1);
                }
            }
        }
        "remove" => {
            if args.len() < 5 || args.len() > 6 {
                eprintln!("Usage: log_manager remove <log_path_or_directory> <service_name> <progress_json_path> [datasource_name]");
                std::process::exit(1);
            }
            let service_name = &args[3];
            let progress_path = Path::new(&args[4]);
            let datasource_name = args.get(5).map(|s| s.as_str());

            if let Some(ds) = datasource_name {
                eprintln!("Processing for datasource: {}", ds);
            }

            match remove_service_from_logs(log_path, service_name, progress_path, datasource_name) {
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
                        0,
                        0,
                        None,
                        datasource_name.map(|s| s.to_string()),
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
