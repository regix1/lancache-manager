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
mod log_purge;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;

use log_purge::remove_log_entries_for_game;
use progress_events::ProgressReporter;

/// Game cache removal utility - removes all cache files for a specific game
#[derive(clap::Parser, Debug)]
#[command(name = "cache_game_remove")]
#[command(about = "Removes all cache files for a specific game by scanning logs")]
struct Args {
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

    /// Skip the cache-file disk probe (all game rows already evicted).
    /// When set, no `path.exists()` scanning of candidate cache files occurs
    /// and no directory cleanup runs, but the log rewrite and database
    /// cleanup still execute normally.
    #[arg(long)]
    skip_file_probe: bool,
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

    // Query 1: Mapped games — join LogEntries to SteamDepotMappings via DepotId.
    // Works for games where PicsDataService has populated SteamDepotMappings.
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

    // Query 3: Downloads-FK join — catches delisted apps (e.g., Aion AppID 373680 / depot 373681)
    // where SteamDepotMappings has no row but Downloads.GameAppId is set correctly.
    // Mirrors GameCacheDetectionService.ResolveUnknownGamesInCacheAsync (C# line 1033/1086).
    // Uses Option A (DownloadId FK) — no .sqlx offline cache present, DownloadId FK is populated
    // by the log processor for all ingest paths; runtime sqlx::query() used throughout this crate.
    let downloads_fk_rows = sqlx::query(
        "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"DepotId\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
         WHERE d.\"GameAppId\" = $1 AND le.\"Url\" IS NOT NULL"
    )
    .bind(game_app_id as i64)
    .fetch_all(pool)
    .await?;

    for row in downloads_fk_rows {
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

    eprintln!("Collecting cache file paths for deletion...");

    // Collect all paths to delete (without actually checking existence yet)
    let paths_to_check: Vec<_> = url_data
        .par_iter()
        .flat_map(|(url, (service, total_bytes, _depot_ids))| {
            cache_utils::cache_path_candidates_for_bytes(cache_dir, service, url, *total_bytes)
                .into_iter()
                .map(|path| (path, false))
                .collect::<Vec<_>>()
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
            // Refuse to follow symlinks or delete anything outside the cache root.
            if let Err(e) = cache_utils::safe_path_under_root(cache_dir, path) {
                eprintln!("  skipping unsafe path {}: {}", path.display(), e);
                return;
            }

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
                        match parent_dirs.lock() {
                            Ok(mut dirs) => {
                                dirs.insert(parent.to_path_buf());
                            }
                            Err(err) => {
                                eprintln!("  Warning: failed to track parent directory after delete: {}", err);
                            }
                        }
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

        // Report granular progress during the removal phase (10% - 70%).
        // Write on EITHER an integer-percent advance OR every 8th file probed,
        // so small games still emit motion during the short window where the
        // C# poller (500ms) and frontend SignalR can observe updates.
        if total_paths > 0 {
            let current_pct = (checked * 100) / total_paths;
            let prev_pct = last_reported_percent.load(Ordering::Relaxed);
            let advanced_percent = current_pct > prev_pct;
            let every_n_files = checked & 0x7 == 0; // every 8 files
            if advanced_percent || every_n_files {
                let should_write = if advanced_percent {
                    last_reported_percent
                        .compare_exchange(prev_pct, current_pct, Ordering::SeqCst, Ordering::Relaxed)
                        .is_ok()
                } else {
                    true
                };
                if should_write {
                    let overall_percent = 10.0 + (checked as f64 / total_paths as f64) * 60.0;
                    let del_count = deleted_files.load(Ordering::Relaxed);
                    let _ = bytes_freed.load(Ordering::Relaxed);
                    let _ = write_progress(progress_path, "removing_cache", "signalr.gameRemove.cache.file.progress", json!({ "n": del_count, "total": total_paths }), overall_percent, del_count, total_paths);
                    reporter.emit_progress(overall_percent, "signalr.gameRemove.cache.file.progress", json!({ "n": del_count, "total": total_paths }));
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

// `remove_log_entries_for_game` now lives in `log_purge.rs` and is shared with
// `cache_purge_log_entries`. See the top-of-file `use log_purge::...` import.

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
    reporter.emit_started("signalr.gameRemove.starting", json!({}));

    if !log_dir.exists() {
        let msg = format!("Log directory not found: {}", log_dir.display());
        reporter.emit_failed("signalr.gameRemove.error.fatal", json!({ "errorDetail": msg }));
        anyhow::bail!("{}", msg);
    }

    if !cache_dir.exists() {
        let msg = format!("Cache directory not found: {}", cache_dir.display());
        reporter.emit_failed("signalr.gameRemove.error.fatal", json!({ "errorDetail": msg }));
        anyhow::bail!("{}", msg);
    }

    let pool = db::create_pool().await?;

    // Get game name from database
    let game_name = get_game_name_from_db(&pool, game_app_id).await?;
    eprintln!("Game: {}", game_name);

    write_progress(&progress_path, "starting", "signalr.gameRemove.starting", json!({ "gameName": game_name, "gameAppId": game_app_id }), 0.0, 0, 0)?;
    reporter.emit_progress(0.0, "signalr.gameRemove.starting", json!({ "gameName": game_name, "gameAppId": game_app_id }));

    // Get valid depot IDs for this game from database
    write_progress(&progress_path, "querying_database", "signalr.gameRemove.db.querying", json!({}), 5.0, 0, 0)?;
    reporter.emit_progress(5.0, "signalr.gameRemove.db.querying", json!({}));
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

        write_progress(&progress_path, "completed", "signalr.gameRemove.noUrls", json!({}), 100.0, 0, 0)?;
        reporter.emit_complete("signalr.gameRemove.noUrls", json!({}));
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}'", url_data.len(), game_name);

    // File-probe + directory cleanup phase.
    // Skipped when `--skip-file-probe` is set (caller already knows every row for
    // this game is IsEvicted, so the lancache has nothing to delete on disk).
    // The log rewrite and DB cleanup below still run unconditionally.
    let (deleted_files, bytes_freed, empty_dirs_removed, cache_permission_errors) = if args.skip_file_probe {
        eprintln!("\nSkipping cache file probe for {} URLs (fully evicted game)", url_data.len());
        write_progress(&progress_path, "removing_cache", "signalr.gameRemove.cache.skippedEvicted", json!({}), 10.0, 0, 0)?;
        reporter.emit_progress(10.0, "signalr.gameRemove.cache.skippedEvicted", json!({}));
        write_progress(&progress_path, "cleaning_directories", "signalr.gameRemove.dirs.skippedEvicted", json!({}), 70.0, 0, 0)?;
        reporter.emit_progress(70.0, "signalr.gameRemove.dirs.skippedEvicted", json!({}));
        (0usize, 0u64, 0usize, 0usize)
    } else {
        let count = url_data.len();
        write_progress(&progress_path, "removing_cache", "signalr.gameRemove.cache.removing", json!({ "count": count }), 10.0, 0, 0)?;
        reporter.emit_progress(10.0, "signalr.gameRemove.cache.removing", json!({ "count": count }));
        eprintln!("\nRemoving cache files...");
        let (deleted, bytes, parent_dirs, cache_errs) =
            remove_cache_files_for_game(&cache_dir, &url_data, &progress_path, &reporter)?;

        write_progress(&progress_path, "cleaning_directories", "signalr.gameRemove.dirs.cleaning", json!({}), 70.0, 0, 0)?;
        reporter.emit_progress(70.0, "signalr.gameRemove.dirs.cleaning", json!({}));
        eprintln!("\nCleaning up empty directories...");
        let empty_dirs = cleanup_empty_directories(&cache_dir, parent_dirs);
        (deleted, bytes, empty_dirs, cache_errs)
    };

    // Remove log entries for this game
    write_progress(&progress_path, "removing_logs", "signalr.gameRemove.logs.removing", json!({}), 80.0, 0, 0)?;
    reporter.emit_progress(80.0, "signalr.gameRemove.logs.removing", json!({}));
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let (log_entries_removed, log_permission_errors) = remove_log_entries_for_game(&log_dir, &urls_to_remove, &valid_depot_ids, None)?;

    // CRITICAL: Check for permission errors before deleting database records
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

        write_progress(&progress_path, "failed", "signalr.gameRemove.error.fatal", json!({ "errorDetail": error_msg }), 90.0, 0, 0)?;
        reporter.emit_failed("signalr.gameRemove.error.fatal", json!({ "errorDetail": error_msg }));
        anyhow::bail!("{}", error_msg);
    }

    // Delete database records for this game (only if no permission errors)
    write_progress(&progress_path, "removing_database", "signalr.gameRemove.db.deleting", json!({}), 90.0, 0, 0)?;
    reporter.emit_progress(90.0, "signalr.gameRemove.db.deleting", json!({}));
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

    write_progress(&progress_path, "completed", "signalr.gameRemove.complete", json!({ "files": report.cache_files_deleted, "gb": report.total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": report.log_entries_removed, "gameName": game_name, "gameAppId": game_app_id }), 100.0, 0, 0)?;
    reporter.emit_complete("signalr.gameRemove.complete", json!({ "files": report.cache_files_deleted, "gb": report.total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": report.log_entries_removed, "gameName": game_name, "gameAppId": game_app_id }));

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Cache files deleted: {}", report.cache_files_deleted);
    eprintln!("Space freed: {:.2} MB", report.total_bytes_freed as f64 / 1_048_576.0);
    eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
    eprintln!("Log entries removed: {}", report.log_entries_removed);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
}
