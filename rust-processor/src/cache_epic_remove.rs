use anyhow::Result;
use clap::Parser;
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};

mod cache_utils;
mod db;
mod log_discovery;
mod log_reader;
mod log_purge;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;

use log_purge::remove_log_entries_for_urls;
use progress_events::ProgressReporter;

/// Epic game cache removal utility - removes all cache files, log entries,
/// and database records for a specific Epic game identified by name.
/// Mirrors the Steam game removal flow (cache_game_remove) but queries
/// Downloads by GameName + EpicAppId instead of SteamDepotMappings.
#[derive(clap::Parser, Debug)]
#[command(name = "cache_epic_remove")]
#[command(about = "Removes all cache files for a specific Epic game by name")]
struct Args {
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

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ProgressData {
    status: String,
    stage_key: String,
    context: serde_json::Value,
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
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
) -> Result<()> {
    let progress = ProgressData {
        status: status.to_string(),
        stage_key: stage_key.to_string(),
        context,
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
    eprintln!("Collecting cache file paths for deletion...");

    // Collect all paths to delete
    let paths_to_check: Vec<_> = url_data
        .par_iter()
        .flat_map(|(url, (service, total_bytes))| {
            cache_utils::cache_path_candidates_for_bytes(cache_dir, service, url, *total_bytes)
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
            // Refuse to follow symlinks or delete anything outside the cache root.
            if let Err(e) = cache_utils::safe_path_under_root(cache_dir, path) {
                eprintln!("  skipping unsafe path {}: {}", path.display(), e);
                return;
            }

            if let Ok(metadata) = fs::metadata(path) {
                bytes_freed.fetch_add(metadata.len(), Ordering::Relaxed);
            }

            match fs::remove_file(path) {
                Ok(_) => {
                    let count = deleted_files.fetch_add(1, Ordering::Relaxed) + 1;

                    if let Some(parent) = path.parent() {
                        match parent_dirs.lock() {
                            Ok(mut dirs) => {
                                dirs.insert(parent.to_path_buf());
                            }
                            Err(err) => {
                                eprintln!("  Warning: failed to track parent directory after delete: {}", err);
                            }
                        }
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
                    let _ = bytes_freed.load(Ordering::Relaxed);
                    let _ = write_progress(progress_path, "removing_cache", "signalr.epicRemove.cache.file.progress", json!({ "n": del_count, "total": total_paths }), overall_percent, del_count, total_paths);
                    reporter.emit_progress(overall_percent, "signalr.epicRemove.cache.file.progress", json!({ "n": del_count, "total": total_paths }));
                }
            }
        }
    });

    let final_deleted = deleted_files.load(Ordering::Relaxed);
    let final_bytes = bytes_freed.load(Ordering::Relaxed);
    let final_dirs = match parent_dirs.into_inner() {
        Ok(dirs) => dirs,
        Err(err) => {
            eprintln!(
                "  Warning: parent directory tracker was poisoned; continuing with recovered set"
            );
            err.into_inner()
        }
    };
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
        // Canonical-under-root guard: refuses symlinks, paths outside root.
        if let Err(e) = cache_utils::safe_path_under_root(cache_dir, &dir) {
            eprintln!("  skipping unsafe dir {}: {}", dir.display(), e);
            continue;
        }

        if let Ok(entries) = fs::read_dir(&dir) {
            if entries.count() == 0 {
                if fs::remove_dir(&dir).is_ok() {
                    removed_count += 1;

                    if let Some(parent) = dir.parent() {
                        if parent != cache_dir {
                            match cache_utils::safe_path_under_root(cache_dir, parent) {
                                Ok(_) => {
                                    if let Ok(parent_entries) = fs::read_dir(parent) {
                                        if parent_entries.count() == 0 {
                                            fs::remove_dir(parent).ok();
                                            removed_count += 1;
                                        }
                                    }
                                }
                                Err(e) => {
                                    eprintln!("  skipping unsafe parent {}: {}", parent.display(), e);
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

    reporter.emit_started("signalr.epicRemove.starting", json!({ "gameName": game_name }));

    if !log_dir.exists() {
        let msg = format!("Log directory not found: {}", log_dir.display());
        reporter.emit_failed("signalr.epicRemove.error.fatal", json!({ "errorDetail": msg }));
        anyhow::bail!("{}", msg);
    }

    if !cache_dir.exists() {
        let msg = format!("Cache directory not found: {}", cache_dir.display());
        reporter.emit_failed("signalr.epicRemove.error.fatal", json!({ "errorDetail": msg }));
        anyhow::bail!("{}", msg);
    }

    let pool = db::create_pool().await?;

    write_progress(&progress_path, "starting", "signalr.epicRemove.starting", json!({ "gameName": game_name }), 0.0, 0, 0)?;
    reporter.emit_progress(0.0, "signalr.epicRemove.starting", json!({ "gameName": game_name }));

    // Query database for URLs
    write_progress(&progress_path, "querying_database", "signalr.epicRemove.db.querying", json!({}), 5.0, 0, 0)?;
    reporter.emit_progress(5.0, "signalr.epicRemove.db.querying", json!({}));
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

        write_progress(&progress_path, "completed", "signalr.epicRemove.noUrls", json!({}), 100.0, 0, 0)?;
        reporter.emit_complete("signalr.epicRemove.noUrls", json!({}));
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}'", url_data.len(), game_name);

    // Step 1: Remove cache files
    let url_count = url_data.len();
    write_progress(&progress_path, "removing_cache", "signalr.epicRemove.cache.removing", json!({ "count": url_count }), 10.0, 0, 0)?;
    reporter.emit_progress(10.0, "signalr.epicRemove.cache.removing", json!({ "count": url_count }));
    eprintln!("\nRemoving cache files...");
    let (deleted_files, bytes_freed, parent_dirs, cache_permission_errors) =
        remove_cache_files_for_epic_game(&cache_dir, &url_data, &progress_path, &reporter)?;

    // Step 2: Clean up empty directories
    write_progress(&progress_path, "cleaning_directories", "signalr.epicRemove.dirs.cleaning", json!({}), 70.0, 0, 0)?;
    reporter.emit_progress(70.0, "signalr.epicRemove.dirs.cleaning", json!({}));
    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cleanup_empty_directories(&cache_dir, parent_dirs);

    // Step 3: Remove log entries from access log text files
    write_progress(&progress_path, "removing_logs", "signalr.epicRemove.logs.removing", json!({}), 80.0, 0, 0)?;
    reporter.emit_progress(80.0, "signalr.epicRemove.logs.removing", json!({}));
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let (log_entries_removed, log_permission_errors) = remove_log_entries_for_urls(&log_dir, &urls_to_remove)?;

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

        write_progress(&progress_path, "failed", "signalr.epicRemove.error.fatal", json!({ "errorDetail": error_msg }), 90.0, 0, 0)?;
        reporter.emit_failed("signalr.epicRemove.error.fatal", json!({ "errorDetail": error_msg }));
        anyhow::bail!("{}", error_msg);
    }

    // Step 5: Delete database records
    write_progress(&progress_path, "removing_database", "signalr.epicRemove.db.deleting", json!({}), 90.0, 0, 0)?;
    reporter.emit_progress(90.0, "signalr.epicRemove.db.deleting", json!({}));
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

    write_progress(&progress_path, "completed", "signalr.epicRemove.complete", json!({ "files": report.cache_files_deleted, "gb": report.total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": report.log_entries_removed, "gameName": game_name }), 100.0, 0, 0)?;
    reporter.emit_complete("signalr.epicRemove.complete", json!({ "files": report.cache_files_deleted, "gb": report.total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": report.log_entries_removed, "gameName": game_name }));

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Cache files deleted: {}", report.cache_files_deleted);
    eprintln!("Space freed: {:.2} MB", report.total_bytes_freed as f64 / 1_048_576.0);
    eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
    eprintln!("Log entries removed: {}", report.log_entries_removed);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
}
