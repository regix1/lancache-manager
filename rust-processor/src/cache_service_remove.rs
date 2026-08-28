use anyhow::Result;
use clap::Parser;
use serde::Serialize;
use serde_json::json;
use sqlx::PgPool;
use sqlx::Row;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};

use lancache_processor::cache_utils;
use lancache_processor::cancel;
use lancache_processor::db;
use lancache_processor::log_purge;
use lancache_processor::progress_events;
use lancache_processor::progress_utils;
use lancache_processor::removal_core;
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

    /// Per-stem saved ingestion positions (JSON object of stem name to line index). Lets the
    /// log purge split removed lines at the read position so the position adjustment is exact.
    #[arg(long = "stem-positions")]
    stem_positions: Option<String>,

    /// Cache-key recipe of the target datasource: "monolithic" (default) | "bare_metal"
    #[arg(
        long = "key-scheme",
        default_value = "monolithic",
        value_parser = ["monolithic", "bare_metal"]
    )]
    key_scheme: String,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,

    /// Report how many cache files a removal would delete, then exit without deleting
    /// anything. The confirmation the user answers needs the number the removal will
    /// actually reach, not a detection scan's older snapshot.
    #[arg(long = "count-only")]
    count_only: bool,
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

#[derive(Debug, Serialize)]
struct RemovalReport {
    service_name: String,
    cache_files_deleted: usize,
    total_bytes_freed: u64,
    log_entries_removed: u64,
    /// Removed-line count per log-source stem, series-wide - the caller subtracts these
    /// from the saved ingestion positions so a purge cannot shift them past unread lines.
    log_lines_removed_by_source: std::collections::HashMap<String, u64>,
    /// The already-read subset of the map above (series index below the saved position);
    /// the amount the position itself comes back by.
    log_lines_removed_before_position_by_source: std::collections::HashMap<String, u64>,
    database_entries_deleted: u64,
}

impl RemovalReport {
    fn partial(service: &str, cache_files_deleted: usize, total_bytes_freed: u64) -> Self {
        Self {
            service_name: service.to_string(),
            cache_files_deleted,
            total_bytes_freed,
            log_entries_removed: 0,
            log_lines_removed_by_source: Default::default(),
            log_lines_removed_before_position_by_source: Default::default(),
            database_entries_deleted: 0,
        }
    }
}

fn write_removal_report(path: &Path, report: &RemovalReport) -> Result<()> {
    fs::write(path, serde_json::to_string_pretty(report)?)?;
    Ok(())
}

/// Keep access-log and database provenance when any bare-metal candidate could not prove
/// its recipe-computed key. Otherwise a skipped cache file would become undiscoverable.
fn cache_candidate_verified_for_deletion(
    scheme: cache_utils::CacheKeyScheme,
    cache_path: &Path,
    expected_key: Option<&str>,
) -> bool {
    match scheme {
        cache_utils::CacheKeyScheme::Monolithic => true,
        cache_utils::CacheKeyScheme::BareMetal => {
            expected_key
                .and_then(|expected| cache_utils::cache_file_key_matches(cache_path, expected))
                == Some(true)
        }
    }
}

fn write_progress(
    progress_path: &Path,
    reporter: &ProgressReporter,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
) -> Result<()> {
    let emit_context = context.clone();
    let progress = ProgressData {
        status: status.to_string(),
        stage_key: stage_key.to_string(),
        context,
        percent_complete,
        files_processed,
        total_files,
        timestamp: progress_utils::current_timestamp(),
    };

    progress_utils::write_progress_json(progress_path, &progress)?;

    // File write above always precedes the stdout emit (mirrors cache_game_detect.rs's
    // checkpoint ordering / removal_core.rs's write_progress), so a stdout-triggered C#
    // file re-read is never stale.
    match status {
        "starting" => reporter.emit_started(stage_key, emit_context),
        "completed" => reporter.emit_complete(stage_key, emit_context),
        "failed" => {
            let error_detail = emit_context
                .get("errorDetail")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string());
            reporter.emit_failed(stage_key, emit_context, error_detail);
        }
        _ => reporter.emit_progress(percent_complete, stage_key, emit_context),
    }

    Ok(())
}

/// Returns each unique URL for the service along with the max BytesServed observed for it,
/// mirroring cache_steam_remove's (url, total_bytes) shape so the cache probe can derive a
/// real chunk count instead of always probing DEFAULT_MAX_CHUNKS candidates per URL.
async fn get_service_urls_from_db(pool: &PgPool, service: &str) -> Result<HashMap<String, i64>> {
    eprintln!("Querying database for {} URLs...", service);

    let service_lower = service.to_lowercase();

    let rows = sqlx::query(
        "SELECT \"Url\", MAX(\"BytesServed\") as max_bytes
        FROM \"LogEntries\"
        WHERE LOWER(\"Service\") = $1
        AND \"Url\" IS NOT NULL
        GROUP BY \"Url\"",
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

/// Every on-disk cache slice the service's URLs cover, existence-filtered through the
/// `cache_utils` chokepoint.
///
/// All-slice existence walk (matches detection coverage) instead of the size-derived
/// candidate list, so range-served objects that log each ~1 MiB range as a separate row are
/// fully enumerated rather than truncated to slice 0. Collected as 16-byte file-name digests -
/// a steam-sized service is millions of slices, and holding a full PathBuf per slice peaked at
/// hundreds of MB. The canonical path is rebuilt per digest at deletion time (the same layout
/// the existence probe here uses).
///
/// `remove_cache_files_for_service` deletes exactly this list, so `.len()` is the count of
/// files a removal will delete and it reaches no delete loop. Deriving that number any other
/// way would compute a cache key nginx never wrote. [6][7][8]
fn collect_cache_digests(
    cache_dir: &Path,
    service: &str,
    urls: &HashMap<String, i64>,
    scheme: cache_utils::CacheKeyScheme,
    progress: Option<&removal_core::CollectionProgress<'_>>,
) -> Vec<(u128, Option<String>)> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    let total_urls = urls.len();
    let urls_walked = AtomicUsize::new(0);
    let last_reported_percent = AtomicUsize::new(0);

    urls.par_iter()
        .flat_map(|(url, _total_bytes)| {
            let digests = match scheme {
                cache_utils::CacheKeyScheme::Monolithic => {
                    cache_utils::existing_cache_digests_for_url(service, url, |digest| {
                        cache_utils::cache_path_for_digest(cache_dir, digest).exists()
                    })
                    .into_iter()
                    .map(|digest| (digest, None))
                    .collect::<Vec<_>>()
                }
                cache_utils::CacheKeyScheme::BareMetal => {
                    cache_utils::existing_bare_metal_keyed_digests_for_url(service, url, |digest| {
                        cache_utils::cache_path_for_digest(cache_dir, digest).exists()
                    })
                    .into_iter()
                    .map(|(digest, key)| (digest, Some(key)))
                    .collect::<Vec<_>>()
                }
            };

            if let Some(progress) = progress {
                let walked = urls_walked.fetch_add(1, Ordering::Relaxed) + 1;
                let current_pct = (walked * 100) / total_urls;
                let prev_pct = last_reported_percent.load(Ordering::Relaxed);
                if current_pct > prev_pct
                    && last_reported_percent
                        .compare_exchange(
                            prev_pct,
                            current_pct,
                            Ordering::SeqCst,
                            Ordering::Relaxed,
                        )
                        .is_ok()
                {
                    removal_core::report_collection_progress(progress, walked, total_urls);
                }
            }

            digests
        })
        .collect()
}

fn remove_cache_files_for_service(
    cache_dir: &Path,
    service: &str,
    urls: &HashMap<String, i64>,
    progress_path: &Path,
    reporter: &ProgressReporter,
    scheme: cache_utils::CacheKeyScheme,
) -> Result<(usize, u64, usize, usize)> {
    // Returns (deleted_count, bytes_freed, permission_errors, verification_skips).
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Removing cache files for service '{}'...", service);
    eprintln!("Collecting cache file paths for deletion...");

    // Re-walked from disk here, inside the deleting process, so the set deleted is the set
    // that exists now rather than one an earlier pass recorded.
    let digests_to_delete = collect_cache_digests(cache_dir, service, urls, scheme, None);

    let total_paths = digests_to_delete.len();
    eprintln!("Checking {} potential cache file locations...", total_paths);

    let deleted_files = AtomicUsize::new(0);
    let bytes_freed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let verification_skips = AtomicUsize::new(0);
    // Track how many paths have been checked for progress (not just deleted)
    let paths_checked = AtomicUsize::new(0);
    // Track last reported percent to avoid writing progress too frequently
    let last_reported_percent = AtomicUsize::new(0);

    digests_to_delete.par_iter().for_each(|(digest, expected_key)| {
        // Cooperative cancellation: skip remaining files if cancel was requested.
        // Already-deleted files stay deleted — consistent partial state that C# reconciles.
        if cancel::is_cancelled() {
            return;
        }

        let checked = paths_checked.fetch_add(1, Ordering::Relaxed) + 1;

        let cache_path = cache_utils::cache_path_for_digest(cache_dir, *digest);
        if cache_path.exists() {
            match cache_utils::safe_path_under_root(cache_dir, &cache_path) {
                Ok(_) => {
                    // Bare-metal deletion gate: the file must prove it holds the
                    // recipe-computed key. A missing expected key, unreadable header,
                    // or mismatch all fail closed. Monolithic keeps its legacy no-header path.
                    if !cache_candidate_verified_for_deletion(
                        scheme,
                        &cache_path,
                        expected_key.as_deref(),
                    ) {
                        let skips = verification_skips.fetch_add(1, Ordering::Relaxed) + 1;
                        if skips <= 5 {
                            eprintln!(
                                "  skipping {}: embedded KEY did not verify against the computed key",
                                cache_path.display()
                            );
                        }
                    } else {
                        if let Ok(metadata) = fs::metadata(&cache_path) {
                            bytes_freed.fetch_add(metadata.len(), Ordering::Relaxed);
                        }

                        match fs::remove_file(&cache_path) {
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
                    let _ = write_progress(progress_path, reporter, "removing_cache", "signalr.serviceRemove.cache.file.progress", json!({ "n": del_count, "total": total_paths }), overall_percent, del_count, total_paths);
                }
            }
        }
    });

    let final_deleted = deleted_files.load(Ordering::Relaxed);
    let final_bytes = bytes_freed.load(Ordering::Relaxed);
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);
    let final_verification_skips = verification_skips.load(Ordering::Relaxed);

    if final_permission_errors > 5 {
        eprintln!(
            "  ... and {} more permission errors",
            final_permission_errors - 5
        );
    }
    eprintln!(
        "Deleted {} cache files ({:.2} GB freed), {} permission errors",
        final_deleted,
        final_bytes as f64 / 1_073_741_824.0,
        final_permission_errors
    );
    if final_verification_skips > 0 {
        eprintln!(
            "Left {} file(s) untouched because their embedded KEY did not verify against the computed key",
            final_verification_skips
        );
    }

    Ok((
        final_deleted,
        final_bytes,
        final_permission_errors,
        final_verification_skips,
    ))
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
    let key_scheme = cache_utils::CacheKeyScheme::from_config_str(&args.key_scheme);
    cache_utils::set_active_key_scheme(key_scheme);

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    let output_json = PathBuf::from(&args.output_json);
    let service = &args.service;
    let progress_path = PathBuf::from(&args.progress_json);
    let reporter = ProgressReporter::new(args.progress);

    // Whole removal routed through the single failure funnel; the permission-error abort
    // below now just `bail!`s with context instead of hand-emitting `failed` +
    // `process::exit(1)`, so finish_or_exit is the ONE place this bin's failures get emitted.
    let result: Result<()> = async {
    eprintln!("Service Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Service: {}", service);

    let pool = db::create_pool().await?;

    // A count run must not announce itself as a removal: the whole point of the number is
    // that the user can trust what the confirmation says.
    let starting_stage_key = if args.count_only {
        "signalr.serviceRemove.counting.starting"
    } else {
        "signalr.serviceRemove.starting.default"
    };
    write_progress(&progress_path, &reporter, "starting", starting_stage_key, json!({ "service": service }), 0.0, 0, 0)?;

    // Step 1: Get all URLs for this service from database
    write_progress(&progress_path, &reporter, "querying_database", "signalr.serviceRemove.db.querying", json!({}), 5.0, 0, 0)?;
    let urls = get_service_urls_from_db(&pool, service).await?;

    // A count run stops here. It walks the same list a removal would walk, reports how many of
    // those files exist on disk, and returns before the delete loop, the log purge and the
    // database delete below are reachable. A service with no URLs reports zero rather than
    // taking the no-URL exit, so the confirmation always has a number to show. [6][8]
    if args.count_only {
        let collection_progress = removal_core::CollectionProgress {
            progress_path: &progress_path,
            reporter: &reporter,
            stage_key: "signalr.serviceRemove.counting.progress",
        };
        let cache_files_found =
            collect_cache_digests(&cache_dir, service, &urls, key_scheme, Some(&collection_progress))
                .len();

        fs::write(
            &output_json,
            serde_json::to_string_pretty(&removal_core::CacheFileCount {
                entity: service.to_string(),
                cache_files_found,
            })?,
        )?;
        write_progress(&progress_path, &reporter, "completed", "signalr.serviceRemove.counting.complete", json!({ "files": cache_files_found, "service": service }), 100.0, cache_files_found, cache_files_found)?;

        eprintln!("Cache files found: {}", cache_files_found);
        return Ok(());
    }

    if urls.is_empty() {
        eprintln!("No URLs found for service '{}'", service);
        write_progress(&progress_path, &reporter, "completed", "signalr.serviceRemove.noUrls", json!({}), 100.0, 0, 0)?;
        return Ok(());
    }

    // Step 2: Remove cache files
    let url_count = urls.len();
    write_progress(&progress_path, &reporter, "removing_cache", "signalr.serviceRemove.cache.removing", json!({ "count": url_count }), 10.0, 0, url_count)?;
    let (
        cache_files_deleted,
        total_bytes_freed,
        cache_permission_errors,
        verification_skips,
    ) = remove_cache_files_for_service(
        &cache_dir,
        service,
        &urls,
        &progress_path,
        &reporter,
        key_scheme,
    )?;

    // After cache removal: if cancellation arrived, flush partial progress and exit 0.
    // C# re-runs reconciliation/detection after a cancelled remove.
    if cancel::is_cancelled() {
        eprintln!("Cancellation confirmed — flushing partial progress and exiting.");
        let _ = write_progress(
            &progress_path,
            &reporter,
            "removing_cache",
            "signalr.serviceRemove.cache.file.progress",
            json!({ "n": cache_files_deleted, "total": url_count }),
            10.0 + (cache_files_deleted as f64 / url_count.max(1) as f64) * 60.0,
            cache_files_deleted,
            url_count,
        );
        return Ok(());
    }

    // A failed bare-metal KEY check leaves the cache candidate untouched. Preserve its
    // access-log and database provenance, write the successfully completed cache portion,
    // and fail the logical removal so the caller can surface and retry it.
    if let Err(error) = removal_core::ensure_cache_deletions_verified(verification_skips) {
        let report = RemovalReport::partial(service, cache_files_deleted, total_bytes_freed);
        write_removal_report(&output_json, &report)?;
        return Err(error);
    }

    // Step 3: Remove log entries
    write_progress(&progress_path, &reporter, "removing_logs", "signalr.serviceRemove.logs.removing", json!({}), 70.0, cache_files_deleted, url_count)?;
    let url_set: HashSet<String> = urls.keys().cloned().collect();
    let stem_positions = args
        .stem_positions
        .as_deref()
        .and_then(log_purge::read_stem_positions);
    let log_outcome = remove_log_entries_for_service(&log_dir, service, &url_set, stem_positions.as_ref())?;
    let log_entries_removed = log_outcome.lines_removed;
    let log_permission_errors = log_outcome.permission_errors;
    let log_lines_removed_by_source = log_outcome.lines_removed_by_stem;
    let log_lines_removed_before_position_by_source =
        log_outcome.lines_removed_before_position_by_stem;

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
        // The log purge already ran, so its counts must reach the host even though this run
        // aborts: without them the saved read position stays ahead of the shortened log and
        // the next incremental run skips that many unread lines. Mirrors the sibling
        // removal binaries, which write their report on this same path.
        let report = RemovalReport {
            service_name: service.to_string(),
            cache_files_deleted,
            total_bytes_freed,
            log_entries_removed,
            log_lines_removed_by_source: log_lines_removed_by_source.clone(),
            log_lines_removed_before_position_by_source:
                log_lines_removed_before_position_by_source.clone(),
            database_entries_deleted: 0,
        };
        write_removal_report(&output_json, &report)?;
        anyhow::bail!("{}", error_msg);
    }

    // Step 4: Delete database records (only if no permission errors)
    write_progress(&progress_path, &reporter, "removing_database", "signalr.serviceRemove.db.deleting", json!({}), 90.0, cache_files_deleted, url_count)?;
    let database_entries_deleted = delete_service_from_database(&pool, service).await?;

    // Success report for the C# host. The stderr summary below stays as its fallback parse,
    // but only this JSON carries the per-stem purge counts the position adjustment needs.
    let report = RemovalReport {
        service_name: service.to_string(),
        cache_files_deleted,
        total_bytes_freed,
        log_entries_removed,
        log_lines_removed_by_source,
        log_lines_removed_before_position_by_source,
        database_entries_deleted,
    };
    write_removal_report(&output_json, &report)?;

    write_progress(&progress_path, &reporter, "completed", "signalr.serviceRemove.complete", json!({ "files": cache_files_deleted, "gb": total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": log_entries_removed, "dbRecords": database_entries_deleted, "service": service }), 100.0, cache_files_deleted, url_count)?;

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Service: {}", service);
    eprintln!("Cache files deleted: {}", cache_files_deleted);
    eprintln!("Bytes freed: {:.2} GB", total_bytes_freed as f64 / 1_073_741_824.0);
    eprintln!("Log entries removed: {}", log_entries_removed);
    eprintln!("Database entries deleted: {}", database_entries_deleted);
    eprintln!("Removal completed successfully");

    Ok(())
    }.await;
    progress_events::finish_or_exit(&reporter, "signalr.serviceRemove.error.fatal", result);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn write_cache_file(path: &Path, embedded_key: Option<&str>) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut contents = b"\0\n".to_vec();
        if let Some(key) = embedded_key {
            contents.extend_from_slice(format!("KEY: {key}\n").as_bytes());
        } else {
            contents.extend_from_slice(b"cache header without a key\n");
        }
        contents.extend_from_slice(b"body");
        fs::write(path, contents).unwrap();
    }

    #[test]
    fn bare_metal_requires_an_expected_key_while_monolithic_keeps_legacy_behavior() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("cache-file");
        write_cache_file(&path, Some("expected"));

        assert!(cache_candidate_verified_for_deletion(
            cache_utils::CacheKeyScheme::Monolithic,
            &path,
            None,
        ));
        assert!(!cache_candidate_verified_for_deletion(
            cache_utils::CacheKeyScheme::BareMetal,
            &path,
            None,
        ));
        assert!(cache_candidate_verified_for_deletion(
            cache_utils::CacheKeyScheme::BareMetal,
            &path,
            Some("expected"),
        ));
    }

    #[test]
    fn verification_skip_is_counted_and_still_completes_progress_accounting() {
        let temp = tempfile::tempdir().unwrap();
        let service = "steam";
        let url = "/depot/1/chunk/abcdef";
        let expected_key = cache_utils::bare_metal_object_key_base(service, url).unwrap();
        let cache_path = cache_utils::cache_path_for_digest(
            temp.path(),
            cache_utils::calculate_md5_digest(&expected_key),
        );
        write_cache_file(&cache_path, Some("wrong-key"));

        let progress_path = temp.path().join("progress.json");
        let urls = HashMap::from([(url.to_string(), 0_i64)]);
        let (deleted, bytes, permission_errors, verification_skips) =
            remove_cache_files_for_service(
                temp.path(),
                service,
                &urls,
                &progress_path,
                &ProgressReporter::new(false),
                cache_utils::CacheKeyScheme::BareMetal,
            )
            .unwrap();

        assert!(cache_path.exists(), "unverified file must remain untouched");
        assert_eq!(
            (deleted, bytes, permission_errors, verification_skips),
            (0, 0, 0, 1)
        );
        let progress: serde_json::Value =
            serde_json::from_slice(&fs::read(progress_path).unwrap()).unwrap();
        assert_eq!(progress["percentComplete"].as_f64(), Some(70.0));
        assert_eq!(progress["totalFiles"].as_u64(), Some(1));
    }

    #[test]
    fn verification_skips_write_a_partial_report_and_block_log_and_database_removal() {
        assert!(removal_core::ensure_cache_deletions_verified(0).is_ok());
        let error = removal_core::ensure_cache_deletions_verified(2).unwrap_err().to_string();
        assert!(error.contains("2 file(s)"));
        assert!(error.contains("access logs, and database records were left intact"));

        let temp = tempfile::tempdir().unwrap();
        let report_path = temp.path().join("report.json");
        write_removal_report(&report_path, &RemovalReport::partial("steam", 3, 4096)).unwrap();
        let report: serde_json::Value =
            serde_json::from_slice(&fs::read(report_path).unwrap()).unwrap();
        assert_eq!(report["service_name"], "steam");
        assert_eq!(report["cache_files_deleted"], 3);
        assert_eq!(report["total_bytes_freed"], 4096);
        assert_eq!(report["log_entries_removed"], 0);
        assert_eq!(report["database_entries_deleted"], 0);
    }

    #[test]
    fn counting_leaves_every_file_in_place_and_matches_what_the_removal_deletes() {
        let temp = tempfile::tempdir().unwrap();
        let service = "steam";
        // A query string is the case that made earlier probes disagree with nginx: the access
        // log keeps it, the cache key does not. Both the count and the delete go through
        // cache_utils, so they must agree on it.
        let url = "/depot/1/chunk/abcdef?token=xyz";
        let no_range = cache_utils::calculate_cache_path_no_range(temp.path(), service, url);
        let noslice = cache_utils::calculate_cache_path_noslice(temp.path(), service, url);
        write_cache_file(&no_range, None);
        write_cache_file(&noslice, None);

        let urls = HashMap::from([(url.to_string(), 0_i64)]);
        let counted = collect_cache_digests(
            temp.path(),
            service,
            &urls,
            cache_utils::CacheKeyScheme::Monolithic,
            None,
        )
        .len();

        assert_eq!(counted, 2);
        assert!(no_range.exists(), "counting must not delete anything");
        assert!(noslice.exists(), "counting must not delete anything");

        let (deleted, _bytes, permission_errors, verification_skips) =
            remove_cache_files_for_service(
                temp.path(),
                service,
                &urls,
                &temp.path().join("progress.json"),
                &ProgressReporter::new(false),
                cache_utils::CacheKeyScheme::Monolithic,
            )
            .unwrap();

        assert_eq!((deleted, permission_errors, verification_skips), (counted, 0, 0));
        assert!(!no_range.exists());
        assert!(!noslice.exists());
    }

    #[test]
    fn counting_a_service_with_no_urls_reports_zero() {
        let temp = tempfile::tempdir().unwrap();
        let counted = collect_cache_digests(
            temp.path(),
            "steam",
            &HashMap::new(),
            cache_utils::CacheKeyScheme::Monolithic,
            None,
        )
        .len();

        assert_eq!(counted, 0);
    }

    #[test]
    fn count_only_argument_parses_and_defaults_off() {
        let base = [
            "cache_service_remove",
            "logs",
            "cache",
            "steam",
            "report.json",
            "progress.json",
        ];
        assert!(!Args::try_parse_from(base).unwrap().count_only);

        let with_flag = [
            "cache_service_remove",
            "logs",
            "cache",
            "steam",
            "report.json",
            "progress.json",
            "--count-only",
        ];
        assert!(Args::try_parse_from(with_flag).unwrap().count_only);
    }

    #[test]
    fn key_scheme_argument_rejects_unknown_values() {
        let args = [
            "cache_service_remove",
            "logs",
            "cache",
            "steam",
            "report.json",
            "progress.json",
            "--key-scheme",
            "unknown",
        ];
        let error = Args::try_parse_from(args).unwrap_err();
        assert_eq!(error.kind(), clap::error::ErrorKind::InvalidValue);
    }
}
