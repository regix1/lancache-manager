use anyhow::{Context, Result};
use clap::Parser;
use rusqlite::Connection;
use serde::Serialize;
use std::collections::HashSet;
use std::fs;
use std::io::{BufWriter, Write as IoWrite};
use std::path::{Path, PathBuf};
use tempfile::NamedTempFile;
use flate2::write::GzEncoder;
use flate2::Compression;

mod cache_utils;
mod log_discovery;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;

use log_reader::LogFileReader;
use parser::LogParser;
use progress_events::ProgressReporter;

/// Service cache removal utility - removes all cache files for a specific service
#[derive(clap::Parser, Debug)]
#[command(name = "cache_service_remove")]
#[command(about = "Removes all cache files for a specific service")]
struct Args {
    /// Path to LancacheManager.db
    database_path: String,

    /// Directory containing log files
    log_dir: String,

    /// Cache directory root (e.g., /cache or H:/cache)
    cache_dir: String,

    /// Service name to remove (e.g., steam, epic, battlenet)
    service: String,

    /// Path to output JSON report
    output_json: String,

    /// Path to progress JSON file
    progress_json: String,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,
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

    progress_utils::write_progress_json(progress_path, &progress)
}

fn get_service_urls_from_db(db_path: &Path, service: &str) -> Result<HashSet<String>> {
    eprintln!("Querying database for {} URLs...", service);

    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    let service_lower = service.to_lowercase();

    let query = "
        SELECT DISTINCT Url
        FROM LogEntries
        WHERE LOWER(Service) = ?
        AND Url IS NOT NULL
    ";

    let mut stmt = conn.prepare(query)?;
    let rows = stmt.query_map([&service_lower], |row| row.get::<_, String>(0))?;

    let mut urls = HashSet::new();
    for row_result in rows {
        if let Ok(url) = row_result {
            urls.insert(url);
        }
    }

    eprintln!("Found {} unique URLs for service '{}'", urls.len(), service);

    Ok(urls)
}

fn remove_cache_files_for_service(
    cache_dir: &Path,
    service: &str,
    urls: &HashSet<String>,
    progress_path: &Path,
    reporter: &ProgressReporter,
) -> Result<(usize, u64, usize)> {  // Returns (deleted_count, bytes_freed, permission_errors)
    eprintln!("Removing cache files for service '{}'...", service);

    let service_lower = service.to_lowercase();
    let mut deleted_count = 0;
    let mut bytes_freed: u64 = 0;
    let mut permission_errors = 0;
    let slice_size: i64 = 1_048_576; // 1MB
    let total_urls = urls.len();
    let mut urls_processed: usize = 0;
    let mut last_reported_percent: usize = 0;

    for url in urls {
        urls_processed += 1;

        // Try no-range format first (standard lancache format)
        let cache_path_no_range = cache_utils::calculate_cache_path_no_range(cache_dir, &service_lower, url);

        if cache_path_no_range.exists() {
            // Get size before deleting
            if let Ok(metadata) = fs::metadata(&cache_path_no_range) {
                bytes_freed += metadata.len();
            }

            match fs::remove_file(&cache_path_no_range) {
                Ok(_) => {
                    deleted_count += 1;
                    if deleted_count % 100 == 0 {
                        eprintln!("  Deleted {} cache files ({:.2} MB freed)...",
                            deleted_count, bytes_freed as f64 / 1_048_576.0);
                    }
                }
                Err(e) => {
                    if e.kind() == std::io::ErrorKind::PermissionDenied {
                        permission_errors += 1;
                        if permission_errors <= 5 {
                            eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path_no_range.display(), e);
                        }
                    } else {
                        eprintln!("  Warning: Failed to delete {}: {}", cache_path_no_range.display(), e);
                    }
                }
            }
        } else {
            // Try chunked format (check first 100 chunks)
            for chunk in 0..100 {
                let start = chunk * slice_size;
                let end = start + slice_size - 1;
                let cache_path = cache_utils::calculate_cache_path(cache_dir, &service_lower, url, start as u64, end as u64);

                if !cache_path.exists() {
                    break; // No more chunks for this URL
                }

                if let Ok(metadata) = fs::metadata(&cache_path) {
                    bytes_freed += metadata.len();
                }

                match fs::remove_file(&cache_path) {
                    Ok(_) => {
                        deleted_count += 1;
                        if deleted_count % 100 == 0 {
                            eprintln!("  Deleted {} cache files ({:.2} MB freed)...",
                                deleted_count, bytes_freed as f64 / 1_048_576.0);
                        }
                    }
                    Err(e) => {
                        if e.kind() == std::io::ErrorKind::PermissionDenied {
                            permission_errors += 1;
                            if permission_errors <= 5 {
                                eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                            }
                        } else {
                            eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                        }
                    }
                }
            }
        }

        // Report granular progress during the removal phase (10% - 70%)
        // Only report when crossing a new whole-percent boundary to avoid excessive writes
        if total_urls > 0 {
            let current_pct = (urls_processed * 100) / total_urls;
            if current_pct > last_reported_percent {
                last_reported_percent = current_pct;
                let overall_percent = 10.0 + (urls_processed as f64 / total_urls as f64) * 60.0;
                let msg = format!(
                    "Removing cache files: {} deleted ({:.2} MB freed), processed {}/{}",
                    deleted_count,
                    bytes_freed as f64 / 1_048_576.0,
                    urls_processed,
                    total_urls
                );
                let _ = write_progress(progress_path, "removing_cache", &msg, overall_percent, deleted_count, total_urls);
                reporter.emit_progress(overall_percent, &msg);
            }
        }
    }

    if permission_errors > 5 {
        eprintln!("  ... and {} more permission errors", permission_errors - 5);
    }
    eprintln!("Deleted {} cache files ({:.2} GB freed), {} permission errors",
        deleted_count, bytes_freed as f64 / 1_073_741_824.0, permission_errors);

    Ok((deleted_count, bytes_freed, permission_errors))
}

fn remove_log_entries_for_service(
    log_dir: &Path,
    service: &str,
    urls: &HashSet<String>,
) -> Result<(u64, usize)> {  // Returns (lines_removed, permission_errors)
    eprintln!("Filtering log files to remove service entries...");

    let parser = LogParser::new(chrono_tz::UTC);
    let log_files = log_discovery::discover_log_files(log_dir, "access.log")?;
    let service_lower = service.to_lowercase();

    let mut total_lines_removed: u64 = 0;
    let mut permission_errors: usize = 0;

    for (file_index, log_file) in log_files.iter().enumerate() {
        eprintln!("  Processing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

        let file_result = (|| -> Result<u64> {
            let file_dir = log_file.path.parent().context("Failed to get file directory")?;
            let temp_file = NamedTempFile::new_in(file_dir)?;

            let mut lines_removed: u64 = 0;
            let mut lines_processed: u64 = 0;

            {
                let mut log_reader = LogFileReader::open(&log_file.path)?;

                // Create writer that matches the compression of the original file
                let mut writer: Box<dyn std::io::Write> = if log_file.is_compressed {
                    let path_str = log_file.path.to_string_lossy();
                    if path_str.ends_with(".gz") {
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            GzEncoder::new(temp_file.as_file().try_clone()?, Compression::default())
                        ))
                    } else if path_str.ends_with(".zst") {
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            zstd::Encoder::new(temp_file.as_file().try_clone()?, 3)?
                        ))
                    } else {
                        Box::new(BufWriter::with_capacity(1024 * 1024, temp_file.as_file().try_clone()?))
                    }
                } else {
                    Box::new(BufWriter::with_capacity(1024 * 1024, temp_file.as_file().try_clone()?))
                };

                let mut line = String::new();

                loop {
                    line.clear();
                    let bytes_read = log_reader.read_line(&mut line)?;
                    if bytes_read == 0 {
                        break;
                    }

                    lines_processed += 1;
                    let mut should_remove = false;

                    if let Some(entry) = parser.parse_line(line.trim()) {
                        if entry.service == service_lower && urls.contains(&entry.url) {
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
                drop(writer);
            }

            // If all lines were removed, delete the file
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!("  INFO: All {} lines from this file are for this service, deleting file entirely", lines_processed);
                fs::remove_file(&log_file.path).ok();
                return Ok(lines_removed);
            }

            // Replace original with filtered version
            // persist() uses rename which can fail on Windows if file is locked
            let temp_path = temp_file.into_temp_path();

            if let Err(persist_err) = temp_path.persist(&log_file.path) {
                // Fallback: copy + delete (works even if target is locked by file watcher)
                eprintln!("    persist() failed ({}), using copy fallback...", persist_err);
                fs::copy(&persist_err.path, &log_file.path)?;
                fs::remove_file(&persist_err.path).ok();
            }

            Ok(lines_removed)
        })();

        match file_result {
            Ok(lines_removed) => {
                eprintln!("    Removed {} log lines from this file", lines_removed);
                total_lines_removed += lines_removed;
            }
            Err(e) => {
                // Check if this is a permission error
                let error_str = e.to_string();
                if error_str.contains("Permission denied") || error_str.contains("os error 13") {
                    permission_errors += 1;
                    eprintln!("  ERROR: Permission denied for file {}: {}", log_file.path.display(), e);
                } else {
                    eprintln!("  WARNING: Skipping file {}: {}", log_file.path.display(), e);
                }
            }
        }
    }

    eprintln!("Total log entries removed: {}, permission errors: {}", total_lines_removed, permission_errors);
    Ok((total_lines_removed, permission_errors))
}

fn delete_service_from_database(db_path: &Path, service: &str) -> Result<usize> {
    eprintln!("Deleting database records for service '{}'...", service);

    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    let service_lower = service.to_lowercase();

    // First delete LogEntries
    let mut log_stmt = conn.prepare("DELETE FROM LogEntries WHERE LOWER(Service) = ?")?;
    let log_deleted = log_stmt.execute([&service_lower])?;
    eprintln!("  Deleted {} log entry records", log_deleted);

    // Then delete Downloads
    let mut downloads_stmt = conn.prepare("DELETE FROM Downloads WHERE LOWER(Service) = ?")?;
    let downloads_deleted = downloads_stmt.execute([&service_lower])?;
    eprintln!("  Deleted {} download records", downloads_deleted);

    Ok(log_deleted + downloads_deleted)
}

fn main() -> Result<()> {
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let db_path = PathBuf::from(&args.database_path);
    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    let service = &args.service;
    let progress_path = PathBuf::from(&args.progress_json);

    eprintln!("Service Cache Removal");
    eprintln!("  Database: {}", db_path.display());
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Service: {}", service);

    // Emit started event
    reporter.emit_started();

    let start_msg = format!("Starting removal for service '{}'", service);
    write_progress(&progress_path, "starting", &start_msg, 0.0, 0, 0)?;
    reporter.emit_progress(0.0, &start_msg);

    // Step 1: Get all URLs for this service from database
    write_progress(&progress_path, "querying_database", "Querying database for service URLs", 5.0, 0, 0)?;
    reporter.emit_progress(5.0, "Querying database for service URLs");
    let urls = get_service_urls_from_db(&db_path, service)?;

    if urls.is_empty() {
        eprintln!("No URLs found for service '{}'", service);
        write_progress(&progress_path, "completed", "No URLs found for this service", 100.0, 0, 0)?;
        reporter.emit_complete("No URLs found for this service");
        return Ok(());
    }

    // Step 2: Remove cache files
    let remove_msg = format!("Removing cache files for {} URLs", urls.len());
    write_progress(&progress_path, "removing_cache", &remove_msg, 10.0, 0, urls.len())?;
    reporter.emit_progress(10.0, &remove_msg);
    let (cache_files_deleted, total_bytes_freed, cache_permission_errors) = remove_cache_files_for_service(&cache_dir, service, &urls, &progress_path, &reporter)?;

    // Step 3: Remove log entries
    write_progress(&progress_path, "removing_logs", "Removing log entries", 70.0, cache_files_deleted, urls.len())?;
    reporter.emit_progress(70.0, "Removing log entries");
    let (log_entries_removed, log_permission_errors) = remove_log_entries_for_service(&log_dir, service, &urls)?;

    // CRITICAL: Check for permission errors before deleting database records
    // This prevents the DB/filesystem mismatch that causes DbUpdateConcurrencyException
    let total_permission_errors = cache_permission_errors + log_permission_errors;
    if total_permission_errors > 0 {
        let error_msg = format!(
            "ABORTED: Cannot delete database records because {} file(s) could not be modified due to permission errors. \
            This is likely caused by incorrect PUID/PGID settings in your docker-compose.yml. \
            The lancache container usually runs as UID/GID 33:33 (www-data). \
            Cache permission errors: {}, Log permission errors: {}",
            total_permission_errors, cache_permission_errors, log_permission_errors
        );
        eprintln!("\n{}", error_msg);
        write_progress(&progress_path, "failed", &error_msg, 70.0, cache_files_deleted, urls.len())?;
        reporter.emit_failed(&error_msg);
        std::process::exit(1);
    }

    // Step 4: Delete database records (only if no permission errors)
    write_progress(&progress_path, "removing_database", "Deleting database records", 90.0, cache_files_deleted, urls.len())?;
    reporter.emit_progress(90.0, "Deleting database records");
    let database_entries_deleted = delete_service_from_database(&db_path, service)?;

    // Create summary message
    let summary_message = format!(
        "Removed {} cache files ({:.2} GB), {} log entries, {} database records for service '{}'",
        cache_files_deleted,
        total_bytes_freed as f64 / 1_073_741_824.0,
        log_entries_removed,
        database_entries_deleted,
        service
    );

    write_progress(&progress_path, "completed", &summary_message, 100.0, cache_files_deleted, urls.len())?;
    reporter.emit_complete(&summary_message);

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Service: {}", service);
    eprintln!("Cache files deleted: {}", cache_files_deleted);
    eprintln!("Bytes freed: {:.2} GB", total_bytes_freed as f64 / 1_073_741_824.0);
    eprintln!("Log entries removed: {}", log_entries_removed);
    eprintln!("Database entries deleted: {}", database_entries_deleted);
    eprintln!("Removal completed successfully");

    Ok(())
}
