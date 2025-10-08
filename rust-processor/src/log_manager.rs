use anyhow::{Context, Result};
use chrono::Utc;
use serde::Serialize;
use std::collections::HashMap;
use std::env;
use std::fs::{self, File};
use std::io::{BufRead, BufReader, BufWriter, Write as IoWrite};
use std::path::Path;
use std::time::Instant;

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

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

    // On Windows with FileShare.Delete, we can't reliably use atomic rename
    // when C# has the file open. Just write directly with proper sharing.
    #[cfg(windows)]
    {
        use std::fs::OpenOptions;
        use std::io::Write;

        // Open with sharing flags that allow C# to read while we write
        let mut file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .share_mode(0x07) // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            .open(progress_path)?;

        file.write_all(json.as_bytes())?;
        file.flush()?;
    }

    // On Unix, use atomic rename
    #[cfg(not(windows))]
    {
        let temp_path = progress_path.with_extension("json.tmp");
        fs::write(&temp_path, &json)?;
        fs::rename(&temp_path, progress_path)?;
    }

    Ok(())
}

/// Open a file for reading with shared access on Windows
/// This allows multiple processes to read the file simultaneously
fn open_file_shared_read(path: &str) -> Result<File> {
    #[cfg(windows)]
    {
        use std::fs::OpenOptions;
        // On Windows, use FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
        // This allows other processes to read, write, and delete while we're reading
        const FILE_SHARE_READ: u32 = 0x00000001;
        const FILE_SHARE_WRITE: u32 = 0x00000002;
        const FILE_SHARE_DELETE: u32 = 0x00000004;

        OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
            .open(path)
            .context("Failed to open log file with shared access")
    }

    #[cfg(not(windows))]
    {
        File::open(path).context("Failed to open log file")
    }
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
    eprintln!("Counting services in log file...");
    eprintln!("Log file: {}", log_path);

    let file = open_file_shared_read(log_path)?;
    let file_size = file.metadata()?.len();
    let reader = BufReader::with_capacity(1024 * 1024, file); // 1MB buffer

    let mut service_counts: HashMap<String, u64> = HashMap::new();
    let mut lines_processed: u64 = 0;
    let mut bytes_processed: u64 = 0;
    let mut last_progress_update = Instant::now();

    for line_result in reader.lines() {
        let line = line_result?;
        bytes_processed += line.len() as u64 + 1; // +1 for newline
        lines_processed += 1;

        if let Some(service) = extract_service_from_line(&line) {
            // Use the service name directly - auto-discover all services
            *service_counts.entry(service).or_insert(0) += 1;
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
                "counting".to_string(),
                format!("Counting services... {} lines processed", lines_processed),
                lines_processed,
                None,
                None,
            );
            write_progress(progress_path, &progress)?;
            last_progress_update = Instant::now();
        }
    }

    let elapsed = start_time.elapsed();
    eprintln!("\n✓ Service counting completed!");
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
        format!("Service counting completed. Found {} services in {} lines.", service_counts.len(), lines_processed),
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
    eprintln!("Removing {} entries from log file...", service_to_remove);
    eprintln!("Log file: {}", log_path);

    // Create backup
    let backup_path = format!("{}.bak", log_path);
    fs::copy(log_path, &backup_path).context("Failed to create backup")?;
    eprintln!("Backup created at: {}", backup_path);

    // Create temp file for filtered output
    let log_dir = Path::new(log_path)
        .parent()
        .context("Failed to get log directory")?;
    let temp_path = log_dir.join(format!("access.log.tmp.{}", Utc::now().timestamp()));

    let mut lines_processed: u64 = 0;
    let mut lines_removed: u64 = 0;
    let service_lower = service_to_remove.to_lowercase();

    // Scope the file operations so handles are closed before deletion
    {
        let file = open_file_shared_read(log_path)?;
        let file_size = file.metadata()?.len();
        let reader = BufReader::with_capacity(1024 * 1024, file);

        let temp_file = File::create(&temp_path).context("Failed to create temp file")?;
        let mut writer = BufWriter::with_capacity(1024 * 1024, temp_file);

        let mut bytes_processed: u64 = 0;
        let mut last_progress_update = Instant::now();

        // Initial progress
        let progress = ProgressData::new(
            true,
            0.0,
            "starting".to_string(),
            format!("Starting removal of {} entries...", service_to_remove),
            0,
            Some(0),
            None,
        );
        write_progress(progress_path, &progress)?;

        for line_result in reader.lines() {
            let line = line_result?;
            bytes_processed += line.len() as u64 + 1;
            lines_processed += 1;

            let mut should_remove = false;

            if let Some(line_service) = extract_service_from_line(&line) {
                // Exact match - remove entries for this service
                if line_service == service_lower {
                    should_remove = true;
                }
            }

            if !should_remove {
                writeln!(writer, "{}", line)?;
            } else {
                lines_removed += 1;
                if lines_removed % 10000 == 0 {
                    eprintln!("Removed {} {} entries", lines_removed, service_to_remove);
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
                        "Removing {} entries... {} of {} lines processed",
                        service_to_remove, lines_processed, lines_processed
                    ),
                    lines_processed,
                    Some(lines_removed),
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

    // Replace original with filtered version
    fs::remove_file(log_path).context("Failed to remove original log file")?;
    fs::rename(&temp_path, log_path).context("Failed to rename temp file")?;

    let elapsed = start_time.elapsed();
    eprintln!("\n✓ Log filtering completed!");
    eprintln!("  Lines processed: {}", lines_processed);
    eprintln!("  Lines removed: {}", lines_removed);
    eprintln!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    // Final progress
    let progress = ProgressData::new(
        false,
        100.0,
        "complete".to_string(),
        format!(
            "Removed {} {} entries from {} total lines in {:.2}s",
            lines_removed,
            service_to_remove,
            lines_processed,
            elapsed.as_secs_f64()
        ),
        lines_processed,
        Some(lines_removed),
        None,
    );
    write_progress(progress_path, &progress)?;

    Ok(())
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() < 4 {
        eprintln!("Usage:");
        eprintln!("  log_manager count <log_path> <progress_json_path>");
        eprintln!("  log_manager remove <log_path> <service_name> <progress_json_path>");
        eprintln!("\nExamples:");
        eprintln!("  log_manager count ./logs/access.log ./data/log_count_progress.json");
        eprintln!("  log_manager remove ./logs/access.log steam ./data/log_remove_progress.json");
        std::process::exit(1);
    }

    let command = &args[1];
    let log_path = &args[2];

    match command.as_str() {
        "count" => {
            if args.len() != 4 {
                eprintln!("Usage: log_manager count <log_path> <progress_json_path>");
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
                eprintln!("Usage: log_manager remove <log_path> <service_name> <progress_json_path>");
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
