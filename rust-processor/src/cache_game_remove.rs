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

/// Game cache removal utility - removes all cache files for a specific game
#[derive(clap::Parser, Debug)]
#[command(name = "cache_game_remove")]
#[command(about = "Removes all cache files for a specific game by scanning logs")]
struct Args {
    /// Path to LancacheManager database (DATABASE_URL env var or connection string)
    database_path: String,

    /// Directory containing log files
    log_dir: String,

    /// Cache directory root (e.g., /cache or H:/cache)
    cache_dir: String,

    /// Game AppID to remove
    game_app_id: u32,

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
    game_app_id: u32,
    game_name: String,
    cache_files_deleted: usize,
    total_bytes_freed: u64,
    empty_dirs_removed: usize,
    log_entries_removed: u64,
    depot_ids: Vec<u32>,
}

async fn get_game_name_from_db(pool: &PgPool, game_app_id: u32) -> Result<String> {
    let row = sqlx::query(
        "SELECT DISTINCT \"GameName\" FROM \"Downloads\" WHERE \"GameAppId\" = $1 LIMIT 1"
    )
    .bind(game_app_id as i64)
    .fetch_optional(pool)
    .await?;

    let game_name = row
        .map(|r| r.get::<Option<String>, _>("GameName").unwrap_or_default())
        .filter(|n| !n.is_empty())
        .unwrap_or_else(|| format!("Game {}", game_app_id));

    Ok(game_name)
}

async fn get_game_urls_from_db(pool: &PgPool, game_app_id: u32) -> Result<HashMap<String, (String, i64, HashSet<u32>)>> {
    eprintln!("Querying database for game URLs and depot IDs...");

    // CRITICAL FIX: Query LogEntries instead of Downloads to get ALL URLs
    let rows = sqlx::query(
        "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"DepotId\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         INNER JOIN \"SteamDepotMappings\" sdm ON le.\"DepotId\" = sdm.\"DepotId\"
         WHERE sdm.\"AppId\" = $1 AND le.\"Url\" IS NOT NULL"
    )
    .bind(game_app_id as i64)
    .fetch_all(pool)
    .await?;

    // Build service_urls map just like the detector does
    let mut service_urls: HashMap<String, HashMap<String, (i64, HashSet<u32>)>> = HashMap::new();

    for row in rows {
        let service: String = row.get("Service");
        let url: String = row.get("Url");
        let depot_id_opt: Option<i64> = row.get("DepotId");
        let bytes_served: i64 = row.get("BytesServed");

        // Lowercase service name to match cache file format (same as detector)
        let service_lower = service.to_lowercase();

        let url_map = service_urls
            .entry(service_lower.clone())
            .or_insert_with(HashMap::new);

        let entry = url_map
            .entry(url.clone())
            .or_insert_with(|| (0, HashSet::new()));

        // Track max bytes
        entry.0 = entry.0.max(bytes_served);

        // Track depot ID if present
        if let Some(depot_id) = depot_id_opt {
            entry.1.insert(depot_id as u32);
        }
    }

    // Also get URLs for unknown games (depots not in mappings)
    let unknown_rows = sqlx::query(
        "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"DepotId\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         WHERE le.\"DepotId\" IS NOT NULL
         AND le.\"Url\" IS NOT NULL
         AND le.\"DepotId\" = $1
         AND le.\"DepotId\" NOT IN (SELECT \"DepotId\" FROM \"SteamDepotMappings\")"
    )
    .bind(game_app_id as i64)
    .fetch_all(pool)
    .await?;

    for row in unknown_rows {
        let service: String = row.get("Service");
        let url: String = row.get("Url");
        let depot_id_opt: Option<i64> = row.get("DepotId");
        let bytes_served: i64 = row.get("BytesServed");
        let service_lower = service.to_lowercase();

        let url_map = service_urls
            .entry(service_lower.clone())
            .or_insert_with(HashMap::new);

        let entry = url_map
            .entry(url.clone())
            .or_insert_with(|| (0, HashSet::new()));

        entry.0 = entry.0.max(bytes_served);

        if let Some(depot_id) = depot_id_opt {
            entry.1.insert(depot_id as u32);
        }
    }

    // Flatten to URL -> (service, bytes, depot_ids) format
    let mut url_data: HashMap<String, (String, i64, HashSet<u32>)> = HashMap::new();

    for (service, urls) in service_urls {
        for (url, (bytes, depot_ids)) in urls {
            url_data.insert(url, (service.clone(), bytes, depot_ids));
        }
    }

    eprintln!("  Found {} unique URLs for game AppID {}", url_data.len(), game_app_id);
    Ok(url_data)
}

async fn get_game_depot_ids(pool: &PgPool, game_app_id: u32) -> Result<HashSet<u32>> {
    // Get depot IDs from SteamDepotMappings for mapped games
    let mapped_rows = sqlx::query(
        "SELECT DISTINCT \"DepotId\" FROM \"SteamDepotMappings\" WHERE \"AppId\" = $1"
    )
    .bind(game_app_id as i64)
    .fetch_all(pool)
    .await?;

    let mut depot_ids: HashSet<u32> = mapped_rows.iter()
        .map(|r| r.get::<i64, _>("DepotId") as u32)
        .collect();

    // Also check Downloads table for any additional depot IDs
    let download_rows = sqlx::query(
        "SELECT DISTINCT \"DepotId\" FROM \"Downloads\" WHERE \"GameAppId\" = $1 AND \"DepotId\" IS NOT NULL"
    )
    .bind(game_app_id as i64)
    .fetch_all(pool)
    .await?;

    for row in download_rows {
        let depot_id: i64 = row.get("DepotId");
        depot_ids.insert(depot_id as u32);
    }

    Ok(depot_ids)
}

async fn delete_game_from_database(pool: &PgPool, game_app_id: u32) -> Result<u64> {
    eprintln!("Deleting database records for game AppID {}...", game_app_id);

    // First, delete LogEntries that reference these downloads (foreign key constraint)
    let log_result = sqlx::query(
        "DELETE FROM \"LogEntries\" WHERE \"DownloadId\" IN (SELECT \"Id\" FROM \"Downloads\" WHERE \"GameAppId\" = $1)"
    )
    .bind(game_app_id as i64)
    .execute(pool)
    .await?;
    let log_entries_deleted = log_result.rows_affected();
    eprintln!("  Deleted {} log entry records", log_entries_deleted);

    // Now safe to delete the downloads
    let downloads_result = sqlx::query("DELETE FROM \"Downloads\" WHERE \"GameAppId\" = $1")
        .bind(game_app_id as i64)
        .execute(pool)
        .await?;
    let downloads_deleted = downloads_result.rows_affected();

    eprintln!("  Deleted {} download records", downloads_deleted);
    Ok(downloads_deleted)
}

fn remove_cache_files_for_game(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64, HashSet<u32>)>,
    progress_path: &Path,
    reporter: &ProgressReporter,
) -> Result<(usize, u64, HashSet<PathBuf>, usize)> {  // Returns (deleted, bytes, dirs, permission_errors)
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
    use std::sync::Mutex;

    let deleted_files = AtomicUsize::new(0);
    let bytes_freed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let parent_dirs = Mutex::new(HashSet::new());
    let slice_size: i64 = 1_048_576; // 1MB

    eprintln!("Collecting cache file paths for deletion...");

    // Collect all paths to delete (without actually checking existence yet)
    let paths_to_check: Vec<_> = url_data
        .par_iter()
        .flat_map(|(url, (service, total_bytes, _depot_ids))| {
            let service_lower = service.to_lowercase();
            let mut paths = Vec::new();

            // Add no-range format path
            paths.push((
                cache_utils::calculate_cache_path_no_range(cache_dir, &service_lower, url),
                false, // not chunked
            ));

            // If we have size info, also add chunked format paths
            if *total_bytes > 0 {
                let mut start: i64 = 0;
                while start < *total_bytes {
                    let end = start + slice_size - 1;
                    paths.push((
                        cache_utils::calculate_cache_path(cache_dir, &service_lower, url, start as u64, end as u64),
                        true, // chunked
                    ));
                    start += slice_size;
                }
            } else {
                // Add first chunk as fallback
                paths.push((
                    cache_utils::calculate_cache_path(cache_dir, &service_lower, url, 0, 1_048_575),
                    true,
                ));
            }

            paths
        })
        .collect();

    let total_paths = paths_to_check.len();
    eprintln!("Checking {} potential cache file locations...", total_paths);

    // Track how many paths have been checked for progress (not just deleted)
    let paths_checked = AtomicUsize::new(0);
    // Track last reported percent to avoid writing progress too frequently
    let last_reported_percent = AtomicUsize::new(0);

    // Parallel deletion with progress reporting
    paths_to_check.par_iter().for_each(|(path, _is_chunked)| {
        let checked = paths_checked.fetch_add(1, Ordering::Relaxed) + 1;

        if path.exists() {
            // Get size before deleting
            if let Ok(metadata) = fs::metadata(path) {
                bytes_freed.fetch_add(metadata.len(), Ordering::Relaxed);
            }

            // Delete the file
            match fs::remove_file(path) {
                Ok(_) => {
                    let count = deleted_files.fetch_add(1, Ordering::Relaxed) + 1;

                    // Track parent directory for cleanup
                    if let Some(parent) = path.parent() {
                        let mut dirs = parent_dirs.lock().unwrap();
                        dirs.insert(parent.to_path_buf());
                    }

                    // Progress reporting every 100 files
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

        // Report granular progress during the removal phase (10% - 70%)
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

fn remove_log_entries_for_game(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    valid_depot_ids: &HashSet<u32>,
) -> Result<(u64, usize)> {  // Returns (lines_removed, permission_errors)
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Filtering log files to remove game entries...");

    let parser = LogParser::new(chrono_tz::UTC);
    let log_files = log_discovery::discover_log_files(log_dir, "access.log")?;

    let total_lines_removed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);

    // Process log files in parallel for faster removal
    log_files.par_iter().enumerate().for_each(|(file_index, log_file)| {
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

                    // Parse the line and check if it belongs to this game
                    if let Some(entry) = parser.parse_line(line.trim()) {
                        // Skip health checks
                        if !service_utils::should_skip_url(&entry.url) {
                            // Check if this URL is for the game being removed
                            // Match by URL OR by depot_id
                            if urls_to_remove.contains(&entry.url) {
                                should_remove = true;
                            } else if let Some(depot_id) = entry.depot_id {
                                if valid_depot_ids.contains(&depot_id) {
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
                // Ensure compression is finalized
                drop(writer);
            }

            // If all lines would be removed, delete the entire file
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!("  INFO: All {} lines from this file are for this game, deleting file entirely", lines_processed);
                std::fs::remove_file(&log_file.path).ok();
                return Ok(lines_removed);
            }

            // Atomically replace original with filtered version
            let temp_path = temp_file.into_temp_path();

            if let Err(persist_err) = temp_path.persist(&log_file.path) {
                // Fallback: copy + delete
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
                // Check if this is a permission error
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
    let game_app_id = args.game_app_id;
    let output_json = PathBuf::from(&args.output_json);
    let progress_path = PathBuf::from(&args.progress_json);

    eprintln!("Game Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Game AppID: {}", game_app_id);

    // Emit started event
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

    // Get game name from database
    let game_name = get_game_name_from_db(&pool, game_app_id).await?;
    eprintln!("Game: {}", game_name);

    let start_msg = format!("Starting removal for game '{}' (AppID {})", game_name, game_app_id);
    write_progress(&progress_path, "starting", &start_msg, 0.0, 0, 0)?;
    reporter.emit_progress(0.0, &start_msg);

    // Get valid depot IDs for this game from database
    write_progress(&progress_path, "querying_database", "Querying database for depot IDs and URLs", 5.0, 0, 0)?;
    reporter.emit_progress(5.0, "Querying database for depot IDs and URLs");
    let valid_depot_ids = get_game_depot_ids(&pool, game_app_id).await?;
    eprintln!("Valid depot IDs for this game: {:?}", valid_depot_ids);

    // Query database directly for URLs - much faster than scanning logs!
    let url_data = get_game_urls_from_db(&pool, game_app_id).await?;

    if url_data.is_empty() {
        eprintln!("No URLs found in logs for game AppID {}", game_app_id);

        let report = RemovalReport {
            game_app_id,
            game_name,
            cache_files_deleted: 0,
            total_bytes_freed: 0,
            empty_dirs_removed: 0,
            log_entries_removed: 0,
            depot_ids: vec![],
        };

        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;
        eprintln!("Report saved to: {}", output_json.display());

        write_progress(&progress_path, "completed", "No URLs found for this game", 100.0, 0, 0)?;
        reporter.emit_complete("No URLs found for this game");
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}'", url_data.len(), game_name);

    let remove_msg = format!("Removing cache files for {} URLs", url_data.len());
    write_progress(&progress_path, "removing_cache", &remove_msg, 10.0, 0, 0)?;
    reporter.emit_progress(10.0, &remove_msg);
    eprintln!("\nRemoving cache files...");
    let (deleted_files, bytes_freed, parent_dirs, cache_permission_errors) = remove_cache_files_for_game(&cache_dir, &url_data, &progress_path, &reporter)?;

    write_progress(&progress_path, "cleaning_directories", "Cleaning up empty directories", 70.0, 0, 0)?;
    reporter.emit_progress(70.0, "Cleaning up empty directories");
    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cleanup_empty_directories(&cache_dir, parent_dirs);

    // Remove log entries for this game
    write_progress(&progress_path, "removing_logs", "Removing log entries", 80.0, 0, 0)?;
    reporter.emit_progress(80.0, "Removing log entries");
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let (log_entries_removed, log_permission_errors) = remove_log_entries_for_game(&log_dir, &urls_to_remove, &valid_depot_ids)?;

    // CRITICAL: Check for permission errors before deleting database records
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

        // Still write report but with error status
        let report = RemovalReport {
            game_app_id,
            game_name,
            cache_files_deleted: deleted_files,
            total_bytes_freed: bytes_freed,
            empty_dirs_removed,
            log_entries_removed,
            depot_ids: vec![],
        };
        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;

        write_progress(&progress_path, "failed", &error_msg, 90.0, 0, 0)?;
        reporter.emit_failed(&error_msg);
        anyhow::bail!("{}", error_msg);
    }

    // Delete database records for this game (only if no permission errors)
    write_progress(&progress_path, "removing_database", "Deleting database records", 90.0, 0, 0)?;
    reporter.emit_progress(90.0, "Deleting database records");
    eprintln!("\nRemoving database records...");
    let _db_records_deleted = delete_game_from_database(&pool, game_app_id).await?;

    // Collect all depot IDs
    let mut all_depot_ids: HashSet<u32> = HashSet::new();
    for (_url, (_service, _bytes, depot_ids)) in &url_data {
        all_depot_ids.extend(depot_ids.iter());
    }

    let report = RemovalReport {
        game_app_id,
        game_name: game_name.clone(),
        cache_files_deleted: deleted_files,
        total_bytes_freed: bytes_freed,
        empty_dirs_removed,
        log_entries_removed,
        depot_ids: all_depot_ids.into_iter().collect(),
    };

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    let summary_message = format!(
        "Removed {} cache files ({:.2} GB), {} log entries for game '{}' (AppID {})",
        report.cache_files_deleted,
        report.total_bytes_freed as f64 / 1_073_741_824.0,
        report.log_entries_removed,
        game_name,
        game_app_id
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
