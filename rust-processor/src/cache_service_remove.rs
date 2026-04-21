use anyhow::Result;
use clap::Parser;
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use serde_json::json;
use std::collections::HashSet;
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

use log_purge::remove_log_entries_for_service;
use progress_events::ProgressReporter;

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

async fn get_service_urls_from_db(pool: &PgPool, service: &str) -> Result<HashSet<String>> {
    eprintln!("Querying database for {} URLs...", service);

    let service_lower = service.to_lowercase();

    let rows = sqlx::query(
        "SELECT DISTINCT \"Url\"
        FROM \"LogEntries\"
        WHERE LOWER(\"Service\") = $1
        AND \"Url\" IS NOT NULL"
    )
    .bind(&service_lower)
    .fetch_all(pool)
    .await?;

    let mut urls = HashSet::new();
    for row in rows {
        let url: String = row.get("Url");
        urls.insert(url);
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

    let mut deleted_count = 0;
    let mut bytes_freed: u64 = 0;
    let mut permission_errors = 0;
    let total_urls = urls.len();
    let mut urls_processed: usize = 0;
    let mut last_reported_percent: usize = 0;

    for url in urls {
        urls_processed += 1;

        for cache_path in cache_utils::cache_path_candidates_for_probe(
            cache_dir,
            service,
            url,
            cache_utils::DEFAULT_MAX_CHUNKS,
        ) {
            if !cache_path.exists() {
                continue;
            }

            match cache_utils::safe_path_under_root(cache_dir, &cache_path) {
                Ok(_) => {
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
                Err(e) => {
                    eprintln!("  skipping unsafe path {}: {}", cache_path.display(), e);
                }
            }
        }

        // Report granular progress during the removal phase (10% - 70%)
        if total_urls > 0 {
            let current_pct = (urls_processed * 100) / total_urls;
            if current_pct > last_reported_percent {
                last_reported_percent = current_pct;
                let overall_percent = 10.0 + (urls_processed as f64 / total_urls as f64) * 60.0;
                let _ = write_progress(progress_path, "removing_cache", "signalr.serviceRemove.cache.file.progress", json!({ "n": deleted_count, "total": total_urls }), overall_percent, deleted_count, total_urls);
                reporter.emit_progress(overall_percent, "signalr.serviceRemove.cache.file.progress", json!({ "n": deleted_count, "total": total_urls }));
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
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    let service = &args.service;
    let progress_path = PathBuf::from(&args.progress_json);

    eprintln!("Service Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Service: {}", service);

    // Emit started event
    reporter.emit_started("signalr.serviceRemove.starting.default", json!({ "service": service }));

    let pool = db::create_pool().await?;

    write_progress(&progress_path, "starting", "signalr.serviceRemove.starting.default", json!({ "service": service }), 0.0, 0, 0)?;
    reporter.emit_progress(0.0, "signalr.serviceRemove.starting.default", json!({ "service": service }));

    // Step 1: Get all URLs for this service from database
    write_progress(&progress_path, "querying_database", "signalr.serviceRemove.db.querying", json!({}), 5.0, 0, 0)?;
    reporter.emit_progress(5.0, "signalr.serviceRemove.db.querying", json!({}));
    let urls = get_service_urls_from_db(&pool, service).await?;

    if urls.is_empty() {
        eprintln!("No URLs found for service '{}'", service);
        write_progress(&progress_path, "completed", "signalr.serviceRemove.noUrls", json!({}), 100.0, 0, 0)?;
        reporter.emit_complete("signalr.serviceRemove.noUrls", json!({}));
        return Ok(());
    }

    // Step 2: Remove cache files
    let url_count = urls.len();
    write_progress(&progress_path, "removing_cache", "signalr.serviceRemove.cache.removing", json!({ "count": url_count }), 10.0, 0, url_count)?;
    reporter.emit_progress(10.0, "signalr.serviceRemove.cache.removing", json!({ "count": url_count }));
    let (cache_files_deleted, total_bytes_freed, cache_permission_errors) = remove_cache_files_for_service(&cache_dir, service, &urls, &progress_path, &reporter)?;

    // Step 3: Remove log entries
    write_progress(&progress_path, "removing_logs", "signalr.serviceRemove.logs.removing", json!({}), 70.0, cache_files_deleted, url_count)?;
    reporter.emit_progress(70.0, "signalr.serviceRemove.logs.removing", json!({}));
    let (log_entries_removed, log_permission_errors) = remove_log_entries_for_service(&log_dir, service, &urls)?;

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
        reporter.emit_failed("signalr.serviceRemove.error.fatal", json!({ "errorDetail": error_msg }));
        std::process::exit(1);
    }

    // Step 4: Delete database records (only if no permission errors)
    write_progress(&progress_path, "removing_database", "signalr.serviceRemove.db.deleting", json!({}), 90.0, cache_files_deleted, url_count)?;
    reporter.emit_progress(90.0, "signalr.serviceRemove.db.deleting", json!({}));
    let database_entries_deleted = delete_service_from_database(&pool, service).await?;

    write_progress(&progress_path, "completed", "signalr.serviceRemove.complete", json!({ "files": cache_files_deleted, "gb": total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": log_entries_removed, "dbRecords": database_entries_deleted, "service": service }), 100.0, cache_files_deleted, url_count)?;
    reporter.emit_complete("signalr.serviceRemove.complete", json!({ "files": cache_files_deleted, "gb": total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": log_entries_removed, "dbRecords": database_entries_deleted, "service": service }));

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Service: {}", service);
    eprintln!("Cache files deleted: {}", cache_files_deleted);
    eprintln!("Bytes freed: {:.2} GB", total_bytes_freed as f64 / 1_073_741_824.0);
    eprintln!("Log entries removed: {}", log_entries_removed);
    eprintln!("Database entries deleted: {}", database_entries_deleted);
    eprintln!("Removal completed successfully");

    Ok(())
}
