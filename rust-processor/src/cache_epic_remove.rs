use anyhow::{Context, Result};
use clap::Parser;
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::io::{BufWriter, Write as IoWrite};
use std::path::{Path, PathBuf};
use tempfile::NamedTempFile;
use flate2::write::GzEncoder;
use flate2::Compression;

mod cache_utils;
mod db;
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

/// Epic game cache removal utility - removes all cache files, log entries,
/// and database records for a specific Epic game identified by name.
/// Mirrors the Steam game removal flow (cache_game_remove) but queries
/// Downloads by GameName + EpicAppId instead of SteamDepotMappings.
#[derive(clap::Parser, Debug)]
#[command(name = "cache_epic_remove")]
#[command(about = "Removes all cache files for a specific Epic game by name")]
struct Args {
    /// Path to LancacheManager database (DATABASE_URL env var or connection string)
    database_path: String,

    /// Directory containing log files
    log_dir: String,

    /// Cache directory root (e.g., /cache or H:/cache)
    cache_dir: String,

    /// Epic game name to remove (e.g., "Fortnite")
    game_name: String,

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

#[derive(Debug, Serialize)]
struct RemovalReport {
    game_name: String,
    cache_files_deleted: usize,
    total_bytes_freed: u64,
    empty_dirs_removed: usize,
    log_entries_removed: u64,
}

/// Query the database for all URLs associated with an Epic game.
/// Joins LogEntries with Downloads via DownloadId to find URLs for the specific game.
/// Returns: HashMap<URL, (service_lowercase, max_bytes_served)>
async fn get_epic_game_urls_from_db(pool: &PgPool, game_name: &str) -> Result<HashMap<String, (String, i64)>> {
    eprintln!("Querying database for Epic game URLs...");

    // Query LogEntries joined with Downloads to find all URLs for this Epic game
    let rows = sqlx::query(
        "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
         WHERE d.\"GameName\" = $1 AND d.\"EpicAppId\" IS NOT NULL AND le.\"Url\" IS NOT NULL"
    )
    .bind(game_name)
    .fetch_all(pool)
    .await?;

    let mut url_data: HashMap<String, (String, i64)> = HashMap::new();

    for row in rows {
        let service: String = row.get("Service");
        let url: String = row.get("Url");
        let bytes_served: i64 = row.get("BytesServed");
        let service_lower = service.to_lowercase();

        let entry = url_data
            .entry(url)
            .or_insert_with(|| (service_lower.clone(), 0));

        // Track max bytes for chunk calculation
        entry.1 = entry.1.max(bytes_served);
    }

    // Also get URLs from LogEntries that match epicgames service but may not have DownloadId set
    // (fallback for entries processed before Epic game mapping was established)
    let fallback_rows = sqlx::query(
        "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         WHERE LOWER(le.\"Service\") = 'epicgames'
         AND le.\"Url\" IS NOT NULL
         AND le.\"DownloadId\" IN (
             SELECT \"Id\" FROM \"Downloads\" WHERE \"GameName\" = $1 AND \"EpicAppId\" IS NOT NULL
         )"
    )
    .bind(game_name)
    .fetch_all(pool)
    .await?;

    for row in fallback_rows {
        let service: String = row.get("Service");
        let url: String = row.get("Url");
        let bytes_served: i64 = row.get("BytesServed");
        let service_lower = service.to_lowercase();

        let entry = url_data
            .entry(url)
            .or_insert_with(|| (service_lower.clone(), 0));

        entry.1 = entry.1.max(bytes_served);
    }

    eprintln!("  Found {} unique URLs for Epic game '{}'", url_data.len(), game_name);
    Ok(url_data)
}

/// Delete database records for the Epic game (LogEntries + Downloads).
async fn delete_epic_game_from_database(pool: &PgPool, game_name: &str) -> Result<(u64, u64)> {
    eprintln!("Deleting database records for Epic game '{}'...", game_name);

    // First, delete LogEntries that reference these downloads (foreign key constraint)
    let log_result = sqlx::query(
        "DELETE FROM \"LogEntries\" WHERE \"DownloadId\" IN (
             SELECT \"Id\" FROM \"Downloads\" WHERE \"GameName\" = $1 AND \"EpicAppId\" IS NOT NULL
         )"
    )
    .bind(game_name)
    .execute(pool)
    .await?;
    let log_entries_deleted = log_result.rows_affected();
    eprintln!("  Deleted {} log entry records", log_entries_deleted);

    // Now safe to delete the downloads
    let downloads_result = sqlx::query(
        "DELETE FROM \"Downloads\" WHERE \"GameName\" = $1 AND \"EpicAppId\" IS NOT NULL"
    )
    .bind(game_name)
    .execute(pool)
    .await?;
    let downloads_deleted = downloads_result.rows_affected();
    eprintln!("  Deleted {} download records", downloads_deleted);

    Ok((log_entries_deleted, downloads_deleted))
}

/// Remove cache files for the Epic game. Same logic as cache_game_remove but
/// without depot ID tracking (Epic games have no depots).
fn remove_cache_files_for_epic_game(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    progress_path: &Path,
    reporter: &ProgressReporter,
) -> Result<(usize, u64, HashSet<PathBuf>, usize)> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
    use std::sync::Mutex;

    let deleted_files = AtomicUsize::new(0);
    let bytes_freed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let parent_dirs = Mutex::new(HashSet::new());
    let slice_size: i64 = 1_048_576; // 1MB

    eprintln!("Collecting cache file paths for deletion...");

    // Collect all paths to delete
    let paths_to_check: Vec<_> = url_data
        .par_iter()
        .flat_map(|(url, (service, total_bytes))| {
            let service_lower = service.to_lowercase();
            let mut paths = Vec::new();

            // Add no-range format path
            paths.push(
                cache_utils::calculate_cache_path_no_range(cache_dir, &service_lower, url),
            );

            // If we have size info, add chunked format paths
            if *total_bytes > 0 {
                let mut start: i64 = 0;
                while start < *total_bytes {
                    let end = start + slice_size - 1;
                    paths.push(
                        cache_utils::calculate_cache_path(cache_dir, &service_lower, url, start as u64, end as u64),
                    );
                    start += slice_size;
                }
            } else {
                // Add first chunk as fallback
                paths.push(
                    cache_utils::calculate_cache_path(cache_dir, &service_lower, url, 0, 1_048_575),
                );
            }

            paths
        })
        .collect();

    let total_paths = paths_to_check.len();
    eprintln!("Checking {} potential cache file locations...", total_paths);

    let paths_checked = AtomicUsize::new(0);
    let last_reported_percent = AtomicUsize::new(0);

    // Parallel deletion with progress reporting
    paths_to_check.par_iter().for_each(|path| {
        let checked = paths_checked.fetch_add(1, Ordering::Relaxed) + 1;

        if path.exists() {
            if let Ok(metadata) = fs::metadata(path) {
                bytes_freed.fetch_add(metadata.len(), Ordering::Relaxed);
            }

            match fs::remove_file(path) {
                Ok(_) => {
                    let count = deleted_files.fetch_add(1, Ordering::Relaxed) + 1;

                    if let Some(parent) = path.parent() {
                        let mut dirs = parent_dirs.lock().unwrap();
                        dirs.insert(parent.to_path_buf());
                    }

                    if count % 100 == 0 {
                        let bytes = bytes_freed.load(Ordering::Relaxed);
                        eprintln!(
                            "  Deleted {} cache files... ({:.2} MB freed)",
                            count,
                            bytes as f64 / 1_048_576.0
                        );
                    }
                }
                Err(e) => {
                    if e.kind() == std::io::ErrorKind::PermissionDenied {
                        let err_count = permission_errors.fetch_add(1, Ordering::Relaxed) + 1;
                        if err_count <= 5 {
                            eprintln!("  ERROR: Permission denied deleting {}: {}", path.display(), e);
                        }
                    }
                }
            }
        }

        // Report progress (10% - 70% range during cache removal)
        if total_paths > 0 {
            let current_pct = (checked * 100) / total_paths;
            let prev_pct = last_reported_percent.load(Ordering::Relaxed);
            if current_pct > prev_pct {
                if last_reported_percent.compare_exchange(prev_pct, current_pct, Ordering::SeqCst, Ordering::Relaxed).is_ok() {
                    let overall_percent = 10.0 + (checked as f64 / total_paths as f64) * 60.0;
                    let del_count = deleted_files.load(Ordering::Relaxed);
                    let bytes = bytes_freed.load(Ordering::Relaxed);
                    let msg = format!(
                        "Removing cache files: {} deleted ({:.2} MB freed), checked {}/{}",
                        del_count,
                        bytes as f64 / 1_048_576.0,
                        checked,
                        total_paths
                    );
                    let _ = write_progress(progress_path, "removing_cache", &msg, overall_percent, del_count, total_paths);
                    reporter.emit_progress(overall_percent, &msg);
                }
            }
        }
    });

    let final_deleted = deleted_files.load(Ordering::Relaxed);
    let final_bytes = bytes_freed.load(Ordering::Relaxed);
    let final_dirs = parent_dirs.into_inner().unwrap();
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);

    if final_permission_errors > 5 {
        eprintln!("  ... and {} more permission errors", final_permission_errors - 5);
    }
    if final_permission_errors > 0 {
        eprintln!("  Total permission errors: {}", final_permission_errors);
    }

    Ok((final_deleted, final_bytes, final_dirs, final_permission_errors))
}

fn cleanup_empty_directories(cache_dir: &Path, dirs_to_check: HashSet<PathBuf>) -> usize {
    let mut removed_count = 0;

    let mut sorted_dirs: Vec<PathBuf> = dirs_to_check.into_iter().collect();
    sorted_dirs.sort_by(|a, b| b.components().count().cmp(&a.components().count()));

    for dir in sorted_dirs {
        if !dir.starts_with(cache_dir) {
            continue;
        }

        if let Ok(entries) = fs::read_dir(&dir) {
            if entries.count() == 0 {
                if fs::remove_dir(&dir).is_ok() {
                    removed_count += 1;

                    if let Some(parent) = dir.parent() {
                        if parent.starts_with(cache_dir) && parent != cache_dir {
                            if let Ok(parent_entries) = fs::read_dir(parent) {
                                if parent_entries.count() == 0 {
                                    fs::remove_dir(parent).ok();
                                    removed_count += 1;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    removed_count
}

/// Remove log entries for the Epic game from access log text files.
/// Matches by URL only (Epic games have no depot IDs).
fn remove_log_entries_for_epic_game(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
) -> Result<(u64, usize)> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Filtering log files to remove Epic game entries...");

    let parser = LogParser::new(chrono_tz::UTC);
    let log_files = log_discovery::discover_log_files(log_dir, "access.log")?;

    let total_lines_removed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);

    log_files.par_iter().enumerate().for_each(|(file_index, log_file)| {
        eprintln!("  Processing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

        let file_result = (|| -> Result<u64> {
            let file_dir = log_file.path.parent().context("Failed to get file directory")?;
            let temp_file = NamedTempFile::new_in(file_dir)?;

            let mut lines_removed: u64 = 0;
            let mut lines_processed: u64 = 0;

            {
                let mut log_reader = LogFileReader::open(&log_file.path)?;

                // Create writer matching original compression
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
                        if !service_utils::should_skip_url(&entry.url) {
                            // Match by URL only (no depot IDs for Epic)
                            if urls_to_remove.contains(&entry.url) {
                                should_remove = true;
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
                drop(writer);
            }

            // If all lines removed, delete the entire file
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!("  INFO: All {} lines from this file are for this game, deleting file entirely", lines_processed);
                std::fs::remove_file(&log_file.path).ok();
                return Ok(lines_removed);
            }

            // Atomically replace original with filtered version
            let temp_path = temp_file.into_temp_path();

            if let Err(persist_err) = temp_path.persist(&log_file.path) {
                eprintln!("    persist() failed ({}), using copy fallback...", persist_err);
                std::fs::copy(&persist_err.path, &log_file.path)?;
                std::fs::remove_file(&persist_err.path).ok();
            }

            Ok(lines_removed)
        })();

        match file_result {
            Ok(lines_removed) => {
                eprintln!("    Removed {} log lines from this file", lines_removed);
                total_lines_removed.fetch_add(lines_removed, Ordering::Relaxed);
            }
            Err(e) => {
                let error_str = e.to_string();
                if error_str.contains("Permission denied") || error_str.contains("os error 13") {
                    permission_errors.fetch_add(1, Ordering::Relaxed);
                    eprintln!("  ERROR: Permission denied for file {}: {}", log_file.path.display(), e);
                } else {
                    eprintln!("  WARNING: Skipping file {}: {}", log_file.path.display(), e);
                }
            }
        }
    });

    let final_removed = total_lines_removed.load(Ordering::Relaxed);
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);
    eprintln!("Total log entries removed: {}, permission errors: {}", final_removed, final_permission_errors);
    Ok((final_removed, final_permission_errors))
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    let game_name = &args.game_name;
    let output_json = PathBuf::from(&args.output_json);
    let progress_path = PathBuf::from(&args.progress_json);

    eprintln!("Epic Game Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Game name: {}", game_name);

    reporter.emit_started();

    if !log_dir.exists() {
        let msg = format!("Log directory not found: {}", log_dir.display());
        reporter.emit_failed(&msg);
        anyhow::bail!("{}", msg);
    }

    if !cache_dir.exists() {
        let msg = format!("Cache directory not found: {}", cache_dir.display());
        reporter.emit_failed(&msg);
        anyhow::bail!("{}", msg);
    }

    let pool = db::create_pool().await;

    let start_msg = format!("Starting removal for Epic game '{}'", game_name);
    write_progress(&progress_path, "starting", &start_msg, 0.0, 0, 0)?;
    reporter.emit_progress(0.0, &start_msg);

    // Query database for URLs
    write_progress(&progress_path, "querying_database", "Querying database for game URLs", 5.0, 0, 0)?;
    reporter.emit_progress(5.0, "Querying database for game URLs");
    let url_data = get_epic_game_urls_from_db(&pool, game_name).await?;

    if url_data.is_empty() {
        eprintln!("No URLs found for Epic game '{}'", game_name);

        let report = RemovalReport {
            game_name: game_name.to_string(),
            cache_files_deleted: 0,
            total_bytes_freed: 0,
            empty_dirs_removed: 0,
            log_entries_removed: 0,
        };

        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;

        write_progress(&progress_path, "completed", "No URLs found for this game", 100.0, 0, 0)?;
        reporter.emit_complete("No URLs found for this game");
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}'", url_data.len(), game_name);

    // Step 1: Remove cache files
    let remove_msg = format!("Removing cache files for {} URLs", url_data.len());
    write_progress(&progress_path, "removing_cache", &remove_msg, 10.0, 0, 0)?;
    reporter.emit_progress(10.0, &remove_msg);
    eprintln!("\nRemoving cache files...");
    let (deleted_files, bytes_freed, parent_dirs, cache_permission_errors) =
        remove_cache_files_for_epic_game(&cache_dir, &url_data, &progress_path, &reporter)?;

    // Step 2: Clean up empty directories
    write_progress(&progress_path, "cleaning_directories", "Cleaning up empty directories", 70.0, 0, 0)?;
    reporter.emit_progress(70.0, "Cleaning up empty directories");
    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cleanup_empty_directories(&cache_dir, parent_dirs);

    // Step 3: Remove log entries from access log text files
    write_progress(&progress_path, "removing_logs", "Removing log entries", 80.0, 0, 0)?;
    reporter.emit_progress(80.0, "Removing log entries");
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let (log_entries_removed, log_permission_errors) = remove_log_entries_for_epic_game(&log_dir, &urls_to_remove)?;

    // Step 4: Check for permission errors before touching database
    let total_permission_errors = cache_permission_errors + log_permission_errors;
    if total_permission_errors > 0 {
        let puid = std::env::var("PUID").unwrap_or_else(|_| "1000".to_string());
        let pgid = std::env::var("PGID").unwrap_or_else(|_| "1000".to_string());
        let error_msg = format!(
            "ABORTED: Cannot delete database records because {} file(s) could not be modified due to permission errors. \
            This is likely caused by incorrect PUID/PGID settings. The lancache container is configured to run as UID/GID {}:{}. \
            Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership. \
            Cache permission errors: {}, Log permission errors: {}",
            total_permission_errors, puid, pgid, cache_permission_errors, log_permission_errors
        );
        eprintln!("\n{}", error_msg);

        let report = RemovalReport {
            game_name: game_name.to_string(),
            cache_files_deleted: deleted_files,
            total_bytes_freed: bytes_freed,
            empty_dirs_removed,
            log_entries_removed,
        };
        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;

        write_progress(&progress_path, "failed", &error_msg, 90.0, 0, 0)?;
        reporter.emit_failed(&error_msg);
        anyhow::bail!("{}", error_msg);
    }

    // Step 5: Delete database records
    write_progress(&progress_path, "removing_database", "Deleting database records", 90.0, 0, 0)?;
    reporter.emit_progress(90.0, "Deleting database records");
    eprintln!("\nRemoving database records...");
    let (_log_records, _download_records) = delete_epic_game_from_database(&pool, game_name).await?;

    // Write final report
    let report = RemovalReport {
        game_name: game_name.clone(),
        cache_files_deleted: deleted_files,
        total_bytes_freed: bytes_freed,
        empty_dirs_removed,
        log_entries_removed,
    };

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    let summary_message = format!(
        "Removed {} cache files ({:.2} GB), {} log entries for Epic game '{}'",
        report.cache_files_deleted,
        report.total_bytes_freed as f64 / 1_073_741_824.0,
        report.log_entries_removed,
        game_name
    );

    write_progress(&progress_path, "completed", &summary_message, 100.0, 0, 0)?;
    reporter.emit_complete(&summary_message);

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Cache files deleted: {}", report.cache_files_deleted);
    eprintln!("Space freed: {:.2} MB", report.total_bytes_freed as f64 / 1_048_576.0);
    eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
    eprintln!("Log entries removed: {}", report.log_entries_removed);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
}
