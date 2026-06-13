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
mod cancel;
mod db;
mod log_discovery;
mod log_reader;
mod log_purge;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;
mod tact_products;

use log_purge::remove_log_entries_for_service;

/// Service cache removal utility - removes all cache files for a specific service
#[derive(clap::Parser, Debug)]
#[command(name = "cache_service_remove")]
#[command(about = "Removes all cache files for a specific service")]
struct Args {
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

/// Returns each unique URL for the service along with the max BytesServed observed for it,
/// mirroring cache_game_remove's (url, total_bytes) shape so the cache probe can derive a
/// real chunk count instead of always probing DEFAULT_MAX_CHUNKS candidates per URL.
async fn get_service_urls_from_db(pool: &PgPool, service: &str) -> Result<HashMap<String, i64>> {
    eprintln!("Querying database for {} URLs...", service);

    let service_lower = service.to_lowercase();

    let rows = sqlx::query(
        "SELECT \"Url\", MAX(\"BytesServed\") as max_bytes
        FROM \"LogEntries\"
        WHERE LOWER(\"Service\") = $1
        AND \"Url\" IS NOT NULL
        GROUP BY \"Url\""
    )
    .bind(&service_lower)
    .fetch_all(pool)
    .await?;

    let mut urls = HashMap::new();
    for row in rows {
        let url: String = row.get("Url");
        // MAX() is typed nullable; a NULL aggregate means no usable size for this URL,
        // which the probe phase handles explicitly by falling back to the full probe list.
        let max_bytes: Option<i64> = row.get("max_bytes");
        urls.insert(url, max_bytes.unwrap_or(0));
    }

    eprintln!("Found {} unique URLs for service '{}'", urls.len(), service);

    Ok(urls)
}

fn remove_cache_files_for_service(
    cache_dir: &Path,
    service: &str,
    urls: &HashMap<String, i64>,
    progress_path: &Path,
) -> Result<(usize, u64, usize)> {  // Returns (deleted_count, bytes_freed, permission_errors)
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Removing cache files for service '{}'...", service);
    eprintln!("Collecting cache file paths for deletion...");

    // Candidate paths per URL: chunk count derived from the URL's real max BytesServed
    // (a handful of candidates), exactly like cache_game_remove. URLs without a usable
    // size fall back to the full DEFAULT_MAX_CHUNKS probe list — no functionality loss.
    let paths_to_check: Vec<PathBuf> = urls
        .par_iter()
        .flat_map(|(url, total_bytes)| {
            if *total_bytes > 0 {
                cache_utils::cache_path_candidates_for_bytes(cache_dir, service, url, *total_bytes)
            } else {
                cache_utils::cache_path_candidates_for_probe(
                    cache_dir,
                    service,
                    url,
                    cache_utils::DEFAULT_MAX_CHUNKS,
                )
            }
        })
        .collect();

    let total_paths = paths_to_check.len();
    eprintln!("Checking {} potential cache file locations...", total_paths);

    let deleted_files = AtomicUsize::new(0);
    let bytes_freed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    // Track how many paths have been checked for progress (not just deleted)
    let paths_checked = AtomicUsize::new(0);
    // Track last reported percent to avoid writing progress too frequently
    let last_reported_percent = AtomicUsize::new(0);

    paths_to_check.par_iter().for_each(|cache_path| {
        // Cooperative cancellation: skip remaining files if cancel was requested.
        // Already-deleted files stay deleted — consistent partial state that C# reconciles.
        if cancel::is_cancelled() {
            return;
        }

        let checked = paths_checked.fetch_add(1, Ordering::Relaxed) + 1;

        if cache_path.exists() {
            match cache_utils::safe_path_under_root(cache_dir, cache_path) {
                Ok(_) => {
                    if let Ok(metadata) = fs::metadata(cache_path) {
                        bytes_freed.fetch_add(metadata.len(), Ordering::Relaxed);
                    }

                    match fs::remove_file(cache_path) {
                        Ok(_) => {
                            let count = deleted_files.fetch_add(1, Ordering::Relaxed) + 1;
                            if count.is_multiple_of(100) {
                                eprintln!("  Deleted {} cache files ({:.2} MB freed)...",
                                    count, bytes_freed.load(Ordering::Relaxed) as f64 / 1_048_576.0);
                            }
                        }
                        Err(e) => {
                            if e.kind() == std::io::ErrorKind::PermissionDenied {
                                let err_count = permission_errors.fetch_add(1, Ordering::Relaxed) + 1;
                                if err_count <= 5 {
                                    eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                                }
                            } else {
                                eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                            }
                        }
                    }
                }
                Err(e) => {
                    eprintln!("  skipping unsafe path {}: {}", cache_path.display(), e);
                }
            }
        }

        // Report granular progress during the removal phase (10% - 70%).
        // Write on EITHER an integer-percent advance OR every 8th path probed, so small
        // removals still emit motion while the C# poller / SignalR can observe updates.
        if total_paths > 0 {
            let current_pct = (checked * 100) / total_paths;
            let prev_pct = last_reported_percent.load(Ordering::Relaxed);
            let advanced_percent = current_pct > prev_pct;
            let every_n_files = checked & 0x7 == 0; // every 8 paths
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
                    let _ = write_progress(progress_path, "removing_cache", "signalr.serviceRemove.cache.file.progress", json!({ "n": del_count, "total": total_paths }), overall_percent, del_count, total_paths);
                }
            }
        }
    });

    let final_deleted = deleted_files.load(Ordering::Relaxed);
    let final_bytes = bytes_freed.load(Ordering::Relaxed);
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);

    if final_permission_errors > 5 {
        eprintln!("  ... and {} more permission errors", final_permission_errors - 5);
    }
    eprintln!("Deleted {} cache files ({:.2} GB freed), {} permission errors",
        final_deleted, final_bytes as f64 / 1_073_741_824.0, final_permission_errors);

    Ok((final_deleted, final_bytes, final_permission_errors))
}

async fn delete_service_from_database(pool: &PgPool, service: &str) -> Result<u64> {
    eprintln!("Deleting database records for service '{}'...", service);

    let service_lower = service.to_lowercase();

    // First delete LogEntries
    let log_result = sqlx::query("DELETE FROM \"LogEntries\" WHERE LOWER(\"Service\") = $1")
        .bind(&service_lower)
        .execute(pool)
        .await?;
    let log_deleted = log_result.rows_affected();
    eprintln!("  Deleted {} log entry records", log_deleted);

    // Then delete Downloads
    let downloads_result = sqlx::query("DELETE FROM \"Downloads\" WHERE LOWER(\"Service\") = $1")
        .bind(&service_lower)
        .execute(pool)
        .await?;
    let downloads_deleted = downloads_result.rows_affected();
    eprintln!("  Deleted {} download records", downloads_deleted);

    Ok(log_deleted + downloads_deleted)
}

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    let service = &args.service;
    let progress_path = PathBuf::from(&args.progress_json);

    eprintln!("Service Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Service: {}", service);

    let pool = db::create_pool().await?;

    write_progress(&progress_path, "starting", "signalr.serviceRemove.starting.default", json!({ "service": service }), 0.0, 0, 0)?;

    // Step 1: Get all URLs for this service from database
    write_progress(&progress_path, "querying_database", "signalr.serviceRemove.db.querying", json!({}), 5.0, 0, 0)?;
    let urls = get_service_urls_from_db(&pool, service).await?;

    if urls.is_empty() {
        eprintln!("No URLs found for service '{}'", service);
        write_progress(&progress_path, "completed", "signalr.serviceRemove.noUrls", json!({}), 100.0, 0, 0)?;
        return Ok(());
    }

    // Step 2: Remove cache files
    let url_count = urls.len();
    write_progress(&progress_path, "removing_cache", "signalr.serviceRemove.cache.removing", json!({ "count": url_count }), 10.0, 0, url_count)?;
    let (cache_files_deleted, total_bytes_freed, cache_permission_errors) = remove_cache_files_for_service(&cache_dir, service, &urls, &progress_path)?;

    // After cache removal: if cancellation arrived, flush partial progress and exit 0.
    // C# re-runs reconciliation/detection after a cancelled remove.
    if cancel::is_cancelled() {
        eprintln!("Cancellation confirmed — flushing partial progress and exiting.");
        let _ = write_progress(
            &progress_path,
            "removing_cache",
            "signalr.serviceRemove.cache.file.progress",
            json!({ "n": cache_files_deleted, "total": url_count }),
            10.0 + (cache_files_deleted as f64 / url_count.max(1) as f64) * 60.0,
            cache_files_deleted,
            url_count,
        );
        return Ok(());
    }

    // Step 3: Remove log entries
    write_progress(&progress_path, "removing_logs", "signalr.serviceRemove.logs.removing", json!({}), 70.0, cache_files_deleted, url_count)?;
    let url_set: HashSet<String> = urls.keys().cloned().collect();
    let (log_entries_removed, log_permission_errors) = remove_log_entries_for_service(&log_dir, service, &url_set)?;

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
        write_progress(&progress_path, "failed", "signalr.serviceRemove.error.fatal", json!({ "errorDetail": error_msg }), 70.0, cache_files_deleted, url_count)?;
        std::process::exit(1);
    }

    // Step 4: Delete database records (only if no permission errors)
    write_progress(&progress_path, "removing_database", "signalr.serviceRemove.db.deleting", json!({}), 90.0, cache_files_deleted, url_count)?;
    let database_entries_deleted = delete_service_from_database(&pool, service).await?;

    write_progress(&progress_path, "completed", "signalr.serviceRemove.complete", json!({ "files": cache_files_deleted, "gb": total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": log_entries_removed, "dbRecords": database_entries_deleted, "service": service }), 100.0, cache_files_deleted, url_count)?;

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Service: {}", service);
    eprintln!("Cache files deleted: {}", cache_files_deleted);
    eprintln!("Bytes freed: {:.2} GB", total_bytes_freed as f64 / 1_073_741_824.0);
    eprintln!("Log entries removed: {}", log_entries_removed);
    eprintln!("Database entries deleted: {}", database_entries_deleted);
    eprintln!("Removal completed successfully");

    Ok(())
}
