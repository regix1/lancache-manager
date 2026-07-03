use anyhow::Result;
use clap::{Parser, Subcommand};
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use serde_json::json;
use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};

mod cache_utils;
mod cache_corruption_detector;
mod cancel;
mod db;
mod log_discovery;
mod log_purge;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;
mod tact_products;

use cache_corruption_detector::CorruptionDetector;
use progress_events::ProgressReporter;

/// Cache corruption detector and remover
#[derive(Parser, Debug)]
#[command(name = "cache_corruption")]
#[command(about = "Detects and removes corrupted cache chunks")]
struct Args {
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand, Debug)]
enum Commands {
    /// Find corrupted chunks and output detailed JSON report
    Detect {
        /// Directory containing log files
        log_dir: String,
        /// Cache directory root path
        cache_dir: String,
        /// Path to output JSON file
        output_json: String,
        /// Timezone (default: UTC)
        #[arg(default_value = "UTC")]
        timezone: Option<String>,
        /// Miss threshold (default: 3)
        #[arg(default_value = "3")]
        threshold: Option<usize>,
        /// Skip cache file existence check (logs-only mode)
        #[arg(long, default_value = "false")]
        no_cache_check: bool,
        /// Detect re-downloaded chunks (HIT retries) instead of MISS-based corruption
        #[arg(long, default_value = "false")]
        detect_redownloads: bool,
        /// Path to progress JSON file (omit to skip progress-file writes)
        #[arg(long)]
        progress_json: Option<String>,
        /// Emit JSON progress events to stdout
        #[arg(short, long)]
        progress: bool,
    },
    /// Quick JSON summary of corrupted chunk counts per service
    Summary {
        /// Directory containing log files
        log_dir: String,
        /// Cache directory root path
        cache_dir: String,
        /// Path to progress JSON file (use "none" to skip)
        #[arg(default_value = "none")]
        progress_json: Option<String>,
        /// Timezone (default: UTC)
        #[arg(default_value = "UTC")]
        timezone: Option<String>,
        /// Miss threshold (default: 3)
        #[arg(default_value = "3")]
        threshold: Option<usize>,
        /// Skip cache file existence check (logs-only mode)
        #[arg(long, default_value = "false")]
        no_cache_check: bool,
        /// Detect re-downloaded chunks (HIT retries) instead of MISS-based corruption
        #[arg(long, default_value = "false")]
        detect_redownloads: bool,
        /// Emit JSON progress events to stdout
        #[arg(short, long)]
        progress: bool,
    },
    /// Delete database records, cache files, and log entries for corrupted chunks
    Remove {
        /// Directory containing log files
        log_dir: String,
        /// Cache directory root path
        cache_dir: String,
        /// Service name to remove corrupted chunks for
        service: String,
        /// Path to progress JSON file
        progress_json: String,
        /// Miss threshold - minimum MISS/UNKNOWN count to consider corrupted (default: 3)
        #[arg(default_value = "3")]
        threshold: Option<usize>,
        /// Skip cache file existence check (logs-only mode)
        #[arg(long, default_value = "false")]
        no_cache_check: bool,
        /// Detect re-downloaded chunks (HIT retries) and delete only cache files (not logs/DB)
        #[arg(long, default_value = "false")]
        detect_redownloads: bool,
        /// Emit JSON progress events to stdout
        #[arg(short, long)]
        progress: bool,
    },
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

fn parse_timezone(tz_str: &str) -> chrono_tz::Tz {
    tz_str.parse().unwrap_or(chrono_tz::UTC)
}

/// Writes the progress file exactly as before, then emits the matching stdout event via
/// `reporter` (file write always first). Used by the `Remove` subcommand's own checkpoints;
/// `Summary`'s granular ticks come from `cache_corruption_detector.rs` (out of this migration's
/// lane) so `Summary` only brackets `started`/`complete` around that call - see the `Commands::Summary`
/// arm in `main()`.
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
    let progress = ProgressData {
        status: status.to_string(),
        stage_key: stage_key.to_string(),
        context: context.clone(),
        percent_complete,
        files_processed,
        total_files,
        timestamp: progress_utils::current_timestamp(),
    };

    // Use shared progress writing utility
    progress_utils::write_progress_json(progress_path, &progress)?;

    match status {
        "starting" => reporter.emit_started(stage_key, context),
        "completed" => reporter.emit_complete(stage_key, context),
        "failed" => reporter.emit_failed(stage_key, context),
        _ => reporter.emit_progress(percent_complete, stage_key, context),
    }

    Ok(())
}

async fn delete_corrupted_from_database(
    pool: &PgPool,
    service: &str,
    corrupted_urls: &std::collections::HashSet<String>,
) -> Result<(usize, usize)> {
    eprintln!("Deleting corrupted database records for service: {}", service);

    let service_lower = service.to_lowercase();
    let miss_status = "MISS";
    let unknown_status = "UNKNOWN";

    let mut total_log_entries_deleted = 0usize;
    let mut total_downloads_deleted = 0usize;

    // Process in batches to avoid query size limits
    let batch_size = 400;
    let urls: Vec<&String> = corrupted_urls.iter().collect();

    // STEP 1: Collect all unique DownloadIds that have corrupted (MISS/UNKNOWN) log entries
    let mut affected_download_ids: std::collections::HashSet<i64> = std::collections::HashSet::new();

    for chunk in urls.chunks(batch_size) {
        // Build $1, $2, ... placeholders for the IN clause
        // Parameters: service_lower = $1, then urls = $2..$(n+1), miss = $n+2, unknown = $n+3
        let url_placeholders: String = chunk.iter().enumerate()
            .map(|(i, _)| format!("${}", i + 2))
            .collect::<Vec<_>>()
            .join(", ");
        let miss_idx = chunk.len() + 2;
        let unknown_idx = chunk.len() + 3;

        let query_str = format!(
            "SELECT DISTINCT \"DownloadId\" FROM \"LogEntries\" WHERE LOWER(\"Service\") = $1 AND \"Url\" IN ({}) AND \"CacheStatus\" IN (${}, ${}) AND \"DownloadId\" IS NOT NULL",
            url_placeholders, miss_idx, unknown_idx
        );

        let mut query = sqlx::query(&query_str).bind(&service_lower);
        for url in chunk {
            query = query.bind(url.as_str());
        }
        query = query.bind(miss_status).bind(unknown_status);

        let rows = query.fetch_all(pool).await?;
        for row in rows {
            let download_id: i64 = row.get("DownloadId");
            affected_download_ids.insert(download_id);
        }
    }

    eprintln!("  Found {} download sessions with corrupted entries", affected_download_ids.len());

    // STEP 2: Delete only MISS/UNKNOWN LogEntries for corrupted URLs
    // IMPORTANT: Keep HIT entries intact to prevent snowball corruption detection
    for chunk in urls.chunks(batch_size) {
        let url_placeholders: String = chunk.iter().enumerate()
            .map(|(i, _)| format!("${}", i + 2))
            .collect::<Vec<_>>()
            .join(", ");
        let miss_idx = chunk.len() + 2;
        let unknown_idx = chunk.len() + 3;

        let log_query_str = format!(
            "DELETE FROM \"LogEntries\" WHERE LOWER(\"Service\") = $1 AND \"Url\" IN ({}) AND \"CacheStatus\" IN (${}, ${})",
            url_placeholders, miss_idx, unknown_idx
        );

        let mut query = sqlx::query(&log_query_str).bind(&service_lower);
        for url in chunk {
            query = query.bind(url.as_str());
        }
        query = query.bind(miss_status).bind(unknown_status);

        let result = query.execute(pool).await?;
        total_log_entries_deleted += result.rows_affected() as usize;
    }

    eprintln!("  Deleted {} log entry records", total_log_entries_deleted);

    // STEP 3: Only delete Download sessions that have NO remaining LogEntries
    let download_ids_vec: Vec<i64> = affected_download_ids.into_iter().collect();

    for chunk in download_ids_vec.chunks(batch_size) {
        let placeholders: String = chunk.iter().enumerate()
            .map(|(i, _)| format!("${}", i + 1))
            .collect::<Vec<_>>()
            .join(", ");

        let downloads_query_str = format!(
            "DELETE FROM \"Downloads\" WHERE \"Id\" IN ({}) AND NOT EXISTS (SELECT 1 FROM \"LogEntries\" WHERE \"LogEntries\".\"DownloadId\" = \"Downloads\".\"Id\")",
            placeholders
        );

        let mut query = sqlx::query(&downloads_query_str);
        for id in chunk {
            query = query.bind(*id);
        }

        let result = query.execute(pool).await?;
        total_downloads_deleted += result.rows_affected() as usize;
    }

    eprintln!("  Deleted {} download records (sessions with only corrupted chunks)", total_downloads_deleted);

    // STEP 4: Update CacheMissBytes on remaining Downloads that had some corrupted entries removed
    for chunk in download_ids_vec.chunks(batch_size) {
        let placeholders: String = chunk.iter().enumerate()
            .map(|(i, _)| format!("${}", i + 1))
            .collect::<Vec<_>>()
            .join(", ");

        let update_query_str = format!(
            "UPDATE \"Downloads\" SET \"CacheMissBytes\" = COALESCE((SELECT SUM(\"BytesServed\") FROM \"LogEntries\" WHERE \"LogEntries\".\"DownloadId\" = \"Downloads\".\"Id\" AND \"CacheStatus\" IN ('MISS', 'UNKNOWN')), 0) WHERE \"Id\" IN ({})",
            placeholders
        );

        let mut query = sqlx::query(&update_query_str);
        for id in chunk {
            query = query.bind(*id);
        }

        query.execute(pool).await?;
    }

    Ok((total_downloads_deleted, total_log_entries_deleted))
}

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();

    match args.command {
        Commands::Detect { log_dir, cache_dir, output_json, timezone, threshold, no_cache_check, detect_redownloads, progress_json, progress } => {

            let log_dir = PathBuf::from(&log_dir);
            let cache_dir = PathBuf::from(&cache_dir);
            let output_json = PathBuf::from(&output_json);
            let timezone = timezone.map(|tz| parse_timezone(&tz)).unwrap_or(chrono_tz::UTC);
            let threshold = threshold.unwrap_or(3);
            let reporter = ProgressReporter::new(progress);
            let progress_path = progress_json.map(PathBuf::from);

            if detect_redownloads {
                eprintln!("Detecting re-downloaded chunks (HIT retries)...");
            } else {
                eprintln!("Detecting corrupted chunks...");
            }
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());
            eprintln!("  Timezone: {}", timezone);
            eprintln!("  Threshold: {}", threshold);
            eprintln!("  Skip cache check: {}", no_cache_check);
            eprintln!("  Detect redownloads: {}", detect_redownloads);

            // Seed the (C#-pre-created, empty) progress file before the "started" event so an
            // event-triggered read never sees empty JSON - mirrors the Summary subcommand's invariant.
            if let Some(path) = progress_path.as_deref() {
                let starting = ProgressData {
                    status: "starting".to_string(),
                    stage_key: "signalr.corruptionRemove.scanningFiles".to_string(),
                    context: json!({}),
                    percent_complete: 0.0,
                    files_processed: 0,
                    total_files: 0,
                    timestamp: progress_utils::current_timestamp(),
                };
                if let Err(e) = progress_utils::write_progress_json(path, &starting) {
                    eprintln!("Warning: failed to seed progress file: {:#}", e);
                }
            }
            reporter.emit_started("signalr.corruptionRemove.scanningFiles", json!({}));

            let detector = CorruptionDetector::new(&cache_dir, threshold)
                .with_skip_cache_check(no_cache_check);
            // Granular scanning ticks come from cache_corruption_detector.rs's
            // detect_*_chunks_with_progress (progress-file only); the stdout channel here
            // brackets the coarse started/complete lifecycle, same split as Commands::Summary.
            let report = if detect_redownloads {
                match detector.generate_redownload_report(&log_dir, "access.log", timezone, progress_path.as_deref()) {
                    Ok(r) => r,
                    Err(e) => {
                        let msg = format!("Failed to generate re-download report: {}", e);
                        reporter.emit_failed("signalr.corruptionRemove.error.fatal", json!({ "errorDetail": msg }));
                        anyhow::bail!("{}", msg);
                    }
                }
            } else {
                match detector.generate_report(&log_dir, "access.log", timezone, progress_path.as_deref()) {
                    Ok(r) => r,
                    Err(e) => {
                        let msg = format!("Failed to generate corruption report: {}", e);
                        reporter.emit_failed("signalr.corruptionRemove.error.fatal", json!({ "errorDetail": msg }));
                        anyhow::bail!("{}", msg);
                    }
                }
            };

            eprintln!("Found {} corrupted chunks across {} services",
                report.summary.total_corrupted,
                report.summary.service_counts.len());

            reporter.emit_complete(
                "signalr.corruptionRemove.complete",
                json!({ "totalCorrupted": report.summary.total_corrupted, "serviceCounts": report.summary.service_counts }),
            );

            // Write detailed report to JSON
            let json = serde_json::to_string_pretty(&report)?;
            let mut file = File::create(&output_json)?;
            file.write_all(json.as_bytes())?;
            file.flush()?;

            eprintln!("Report saved to: {}", output_json.display());
        }

        Commands::Summary { log_dir, cache_dir, progress_json, timezone, threshold, no_cache_check, detect_redownloads, progress } => {

            let log_dir = PathBuf::from(&log_dir);
            let cache_dir = PathBuf::from(&cache_dir);
            let reporter = ProgressReporter::new(progress);

            // Parse optional progress file - use "none" to skip progress
            let progress_path = progress_json
                .filter(|p| p != "none" && !p.is_empty())
                .map(PathBuf::from);

            let timezone = timezone.map(|tz| parse_timezone(&tz)).unwrap_or(chrono_tz::UTC);
            let threshold = threshold.unwrap_or(3);

            // All diagnostic output to stderr so stdout only contains JSON
            if detect_redownloads {
                eprintln!("Generating re-download detection summary...");
            } else {
                eprintln!("Generating corruption summary...");
            }
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());
            eprintln!("  Progress file: {}", progress_path.as_ref().map(|p| p.display().to_string()).unwrap_or_else(|| "none".to_string()));
            eprintln!("  Timezone: {}", timezone);
            eprintln!("  Threshold: {}", threshold);
            eprintln!("  Skip cache check: {}", no_cache_check);
            eprintln!("  Detect redownloads: {}", detect_redownloads);

            // Granular scanning ticks for Summary come from cache_corruption_detector.rs's
            // generate_*_summary_with_progress (progress-file only, outside this migration's
            // lane) - the stdout channel here brackets the coarse started/complete lifecycle.
            // File-write-before-stdout-emit invariant: seed the (C#-pre-created, empty)
            // progress file before the "started" event so an event-triggered read never sees
            // empty JSON.
            if let Some(path) = progress_path.as_deref() {
                let starting = ProgressData {
                    status: "starting".to_string(),
                    stage_key: "signalr.corruptionRemove.scanningFiles".to_string(),
                    context: json!({}),
                    percent_complete: 0.0,
                    files_processed: 0,
                    total_files: 0,
                    timestamp: progress_utils::current_timestamp(),
                };
                if let Err(e) = progress_utils::write_progress_json(path, &starting) {
                    eprintln!("Warning: failed to seed progress file: {:#}", e);
                }
            }
            reporter.emit_started("signalr.corruptionRemove.scanningFiles", json!({}));

            let detector = CorruptionDetector::new(&cache_dir, threshold)
                .with_skip_cache_check(no_cache_check);
            let summary = if detect_redownloads {
                match detector.generate_redownload_summary_with_progress(
                    &log_dir, "access.log", timezone, progress_path.as_deref()
                ) {
                    Ok(s) => s,
                    Err(e) => {
                        let msg = format!("Failed to generate re-download summary: {}", e);
                        reporter.emit_failed("signalr.corruptionRemove.error.fatal", json!({ "errorDetail": msg }));
                        anyhow::bail!("{}", msg);
                    }
                }
            } else {
                match detector.generate_summary_with_progress(
                    &log_dir, "access.log", timezone, progress_path.as_deref()
                ) {
                    Ok(s) => s,
                    Err(e) => {
                        let msg = format!("Failed to generate corruption summary: {}", e);
                        reporter.emit_failed("signalr.corruptionRemove.error.fatal", json!({ "errorDetail": msg }));
                        anyhow::bail!("{}", msg);
                    }
                }
            };

            // Real final counts (never hardcoded zeros).
            reporter.emit_complete(
                "signalr.corruptionRemove.complete",
                json!({ "totalCorrupted": summary.total_corrupted, "serviceCounts": summary.service_counts }),
            );

            // Output JSON to stdout for C# to capture (ONLY stdout should be JSON)
            let json = serde_json::to_string(&summary)?;
            println!("{}", json);
        }

        Commands::Remove { log_dir, cache_dir, service, progress_json, threshold, no_cache_check, detect_redownloads, progress } => {

            let log_dir = PathBuf::from(&log_dir);
            let cache_dir = PathBuf::from(&cache_dir);
            let progress_path = PathBuf::from(&progress_json);
            let reporter = ProgressReporter::new(progress);

            if detect_redownloads {
                eprintln!("Removing re-download corrupted cache files for service: {}", service);
            } else {
                eprintln!("Removing corrupted chunks for service: {}", service);
            }
            eprintln!("  Log directory: {}", log_dir.display());
            eprintln!("  Cache directory: {}", cache_dir.display());

            write_progress(&progress_path, &reporter, "starting", "signalr.corruptionRemove.starting", json!({ "service": service }), 0.0, 0, 0)?;

            use log_reader::LogFileReader;
            use parser::LogParser;
            use std::collections::HashMap;

            // Re-download mode: detect via HIT retries within 60s window, then remove cache files, log lines, and DB records
            if detect_redownloads {
                let miss_threshold: usize = threshold.unwrap_or(3);
                let service_lower = service.to_lowercase();

                eprintln!("Step 1: Detecting re-downloaded URLs for {}...", service);
                write_progress(&progress_path, &reporter, "scanning", "signalr.corruptionRemove.scanningRedownload", json!({}), 5.0, 0, 0)?;

                let detector = CorruptionDetector::new(&cache_dir, miss_threshold);
                let timezone_tz: chrono_tz::Tz = chrono_tz::UTC;
                let redownload_map = detector.detect_redownloaded_chunks_with_progress(
                    &log_dir, "access.log", timezone_tz, Some(&progress_path)
                )?;

                // Filter to the requested service, capturing URL and response size
                let service_urls_with_sizes: HashMap<String, i64> = redownload_map
                    .into_iter()
                    .filter(|((svc, _url), (_count, _size))| svc.to_lowercase() == service_lower)
                    .map(|((_svc, url), (_count, size))| (url, size))
                    .collect();

                if service_urls_with_sizes.is_empty() {
                    eprintln!("No re-download corrupted chunks found for {}, nothing to remove", service);
                    write_progress(&progress_path, &reporter, "completed", "signalr.corruptionRemove.noChunksFound", json!({}), 100.0, 0, 0)?;
                    return Ok(());
                }

                let corrupted_urls: std::collections::HashSet<String> = service_urls_with_sizes.keys().cloned().collect();
                eprintln!("Found {} re-download corrupted URLs for {}", corrupted_urls.len(), service);

                // PASS 2: Filter log files, removing HIT lines for corrupted URLs.
                // Uses the shared scan-then-rewrite helper (Aho-Corasick prefilter,
                // untouched files skip recompression entirely).
                eprintln!("Step 2: Filtering log files to remove HIT entries for corrupted URLs...");

                let log_files = crate::log_discovery::discover_log_files(&log_dir, "access.log")?;
                let total_files = log_files.len();

                write_progress(&progress_path, &reporter, "filtering", "signalr.corruptionRemove.filteringLogs", json!({ "totalFiles": total_files }), 30.0, 0, total_files)?;

                let prefilter = log_purge::RemovalPrefilter::new(
                    corrupted_urls.iter().map(|url| url.as_bytes().to_vec()).collect::<Vec<_>>(),
                )?;
                let filter_progress_cb = |files_done: usize, total: usize| {
                    let filter_percent = 30.0 + (files_done as f64 / total.max(1) as f64) * 20.0;
                    let _ = write_progress(
                        &progress_path,
                        &reporter,
                        "filtering",
                        "signalr.corruptionRemove.filteringFile",
                        json!({ "fileIndex": files_done, "totalFiles": total }),
                        filter_percent,
                        files_done,
                        total,
                    );
                };
                // Remove HIT lines for corrupted URLs (re-download corruption serves bad HITs)
                let (total_lines_removed, _log_filter_permission_errors) =
                    log_purge::rewrite_matching_log_entries(
                        &log_dir,
                        "re-download corrupted",
                        &prefilter,
                        |entry| {
                            entry.service == service_lower
                                && corrupted_urls.contains(&entry.url)
                                && entry.cache_status == "HIT"
                        },
                        Some(&filter_progress_cb),
                    )?;

                // Step 3: Delete ALL cache file chunks from disk (multi-slice aware)
                let total_urls = service_urls_with_sizes.len();
                eprintln!("Step 3: Deleting cache files for {} corrupted URLs...", total_urls);
                write_progress(&progress_path, &reporter, "removing_cache", "signalr.corruptionRemove.removingCacheFiles", json!({ "totalUrls": total_urls }), 50.0, 0, total_urls)?;

                let mut deleted_count = 0usize;
                let mut permission_errors = 0usize;
                let mut other_errors = 0usize;
                let slice_size: i64 = 1_048_576; // 1MB

                for (url_index, (url, response_size)) in service_urls_with_sizes.iter().enumerate() {
                    // Cooperative cancellation: stop between chunks.
                    if cancel::is_cancelled() {
                        let percent = 50.0 + (url_index as f64 / total_urls.max(1) as f64) * 25.0;
                        eprintln!("Cancellation requested at URL {}/{} — flushing partial progress.", url_index, total_urls);
                        let _ = write_progress(&progress_path, &reporter, "removing_cache", "signalr.corruptionRemove.removingCacheFile", json!({ "urlIndex": url_index, "totalUrls": total_urls }), percent, url_index, total_urls);
                        return Ok(());
                    }

                    if url_index % 50 == 0 || url_index == total_urls - 1 {
                        let percent = 50.0 + (url_index as f64 / total_urls.max(1) as f64) * 25.0;
                        write_progress(&progress_path, &reporter, "removing_cache", "signalr.corruptionRemove.removingCacheFile", json!({ "urlIndex": url_index + 1, "totalUrls": total_urls }), percent, url_index, total_urls)?;
                    }

                    // FIRST: Try the no-range format (standard lancache format)
                    let cache_path_no_range = cache_utils::calculate_cache_path_no_range(&cache_dir, &service_lower, url);

                    if cache_path_no_range.exists() {
                        match cache_utils::safe_path_under_root(&cache_dir, &cache_path_no_range) {
                            Ok(_) => match std::fs::remove_file(&cache_path_no_range) {
                                Ok(_) => {
                                    deleted_count += 1;
                                    if deleted_count % 100 == 0 {
                                        eprintln!("  Deleted {} cache files...", deleted_count);
                                    }
                                }
                                Err(e) => {
                                    if e.kind() == std::io::ErrorKind::PermissionDenied {
                                        permission_errors += 1;
                                        if permission_errors <= 5 {
                                            eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path_no_range.display(), e);
                                        }
                                    } else {
                                        other_errors += 1;
                                        eprintln!("  Warning: Failed to delete {}: {}", cache_path_no_range.display(), e);
                                    }
                                }
                            },
                            Err(e) => {
                                other_errors += 1;
                                eprintln!("  skipping unsafe path {}: {}", cache_path_no_range.display(), e);
                            }
                        }
                    } else {
                        // FALLBACK: Try the chunked format with bytes range
                        if *response_size == 0 {
                            let cache_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, 0, 1_048_575);

                            if cache_path.exists() {
                                match cache_utils::safe_path_under_root(&cache_dir, &cache_path) {
                                    Ok(_) => match std::fs::remove_file(&cache_path) {
                                        Ok(_) => deleted_count += 1,
                                        Err(e) => {
                                            if e.kind() == std::io::ErrorKind::PermissionDenied {
                                                permission_errors += 1;
                                                if permission_errors <= 5 {
                                                    eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                                                }
                                            } else {
                                                other_errors += 1;
                                                eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                                            }
                                        }
                                    },
                                    Err(e) => {
                                        other_errors += 1;
                                        eprintln!("  skipping unsafe path {}: {}", cache_path.display(), e);
                                    }
                                }
                            }
                        } else {
                            let mut start: i64 = 0;
                            while start < *response_size {
                                let end = start + slice_size - 1;

                                let cache_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, start as u64, end as u64);

                                if cache_path.exists() {
                                    match cache_utils::safe_path_under_root(&cache_dir, &cache_path) {
                                        Ok(_) => match std::fs::remove_file(&cache_path) {
                                            Ok(_) => {
                                                deleted_count += 1;
                                                if deleted_count % 100 == 0 {
                                                    eprintln!("  Deleted {} cache files...", deleted_count);
                                                }
                                            }
                                            Err(e) => {
                                                if e.kind() == std::io::ErrorKind::PermissionDenied {
                                                    permission_errors += 1;
                                                    if permission_errors <= 5 {
                                                        eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                                                    }
                                                } else {
                                                    other_errors += 1;
                                                    eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                                                }
                                            }
                                        },
                                        Err(e) => {
                                            other_errors += 1;
                                            eprintln!("  skipping unsafe path {}: {}", cache_path.display(), e);
                                        }
                                    }
                                }

                                start += slice_size;
                            }
                        }
                    }
                }

                eprintln!("Deleted {} cache files", deleted_count);
                if permission_errors > 0 {
                    eprintln!("ERROR: {} files could not be deleted due to permission errors", permission_errors);
                    if permission_errors > 5 {
                        eprintln!("  (only first 5 errors shown)");
                    }
                }
                if other_errors > 0 {
                    eprintln!("WARNING: {} files could not be deleted due to other errors", other_errors);
                }
                eprintln!("Removed {} total log lines across {} files", total_lines_removed, log_files.len());

                // CRITICAL: If we had permission errors, do NOT delete database records
                if permission_errors > 0 {
                    let puid = std::env::var("PUID").unwrap_or_else(|_| "1000".to_string());
                    let pgid = std::env::var("PGID").unwrap_or_else(|_| "1000".to_string());
                    let error_msg = format!(
                        "ABORTED: Cannot delete database records because {} cache files could not be deleted due to permission errors. \
                        This is likely caused by incorrect PUID/PGID settings. The lancache container is configured to run as UID/GID {}:{}. \
                        Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership.",
                        permission_errors, puid, pgid
                    );
                    eprintln!("\n{}", error_msg);
                    write_progress(&progress_path, &reporter, "failed", "signalr.corruptionRemove.error.fatal", json!({ "errorDetail": error_msg }), 0.0, 0, 0)?;
                    std::process::exit(1);
                }

                // Step 4: Delete database records for corrupted downloads
                eprintln!("Step 4: Deleting database records...");
                write_progress(&progress_path, &reporter, "removing_database", "signalr.corruptionRemove.deletingDb", json!({}), 85.0, 0, 0)?;

                let pool = db::create_pool().await?;
                let (downloads_deleted, log_entries_deleted) = delete_corrupted_from_database(&pool, &service, &corrupted_urls).await?;

                write_progress(&progress_path, &reporter, "completed", "signalr.corruptionRemove.redownload.complete", json!({ "count": service_urls_with_sizes.len(), "service": service, "files": deleted_count, "logLines": total_lines_removed, "downloads": downloads_deleted, "logEntries": log_entries_deleted }), 100.0, 0, 0)?;
                eprintln!("\n=== Re-download Corruption Removal Summary ===");
                eprintln!("Corrupted URLs detected: {}", service_urls_with_sizes.len());
                eprintln!("Cache files deleted: {}", deleted_count);
                eprintln!("Log lines removed: {}", total_lines_removed);
                eprintln!("Database downloads deleted: {}", downloads_deleted);
                eprintln!("Database log entries deleted: {}", log_entries_deleted);
                eprintln!("Removal completed successfully");
                return Ok(());
            }

            eprintln!("Step 1: Detecting corrupted URLs for {}...", service);

            let service_lower = service.to_lowercase();
            let parser = LogParser::new(chrono_tz::UTC);
            let log_files = crate::log_discovery::discover_log_files(&log_dir, "access.log")?;

            // PASS 1: Scan all logs to identify corrupted URLs AND their sizes
            let mut miss_tracker: HashMap<String, usize> = HashMap::new();
            let mut url_sizes: HashMap<String, i64> = HashMap::new(); // Track max response size per URL
            let mut entries_processed: usize = 0;
            let miss_threshold: usize = threshold.unwrap_or(3);

            let total_files = log_files.len();
            write_progress(&progress_path, &reporter, "scanning", "signalr.corruptionRemove.scanningFiles", json!({ "totalFiles": total_files }), 0.0, 0, total_files)?;

            // First pass: identify all corrupted URLs AND track their response sizes
            for (file_index, log_file) in log_files.iter().enumerate() {
                // Update progress during scanning (0-30%)
                let scan_percent = (file_index as f64 / total_files as f64) * 30.0;
                write_progress(&progress_path, &reporter, "scanning", "signalr.corruptionRemove.scanningFile", json!({ "fileIndex": file_index + 1, "totalFiles": total_files }), scan_percent, file_index, total_files)?;
                eprintln!("  Scanning file {}/{}: {}", file_index + 1, total_files, log_file.path.display());

                let scan_result = (|| -> Result<()> {
                    let mut log_reader = LogFileReader::open(&log_file.path)?;
                    let mut line = String::new();

                    loop {
                        line.clear();
                        let bytes_read = log_reader.read_line(&mut line)?;
                        if bytes_read == 0 {
                            break; // EOF
                        }

                        // Parse the line
                        if let Some(entry) = parser.parse_line(line.trim()) {
                            // Skip health check/heartbeat endpoints
                            if !service_utils::should_skip_url(&entry.url) {
                                // Track MISS/UNKNOWN for this service
                                if entry.service == service_lower &&
                                   (entry.cache_status == "MISS" || entry.cache_status == "UNKNOWN") {
                                    *miss_tracker.entry(entry.url.clone()).or_insert(0) += 1;
                                    entries_processed += 1;

                                    // Track the maximum response size for this URL
                                    url_sizes.entry(entry.url.clone())
                                        .and_modify(|size| *size = (*size).max(entry.bytes_served))
                                        .or_insert(entry.bytes_served);

                                    // Log progress periodically
                                    if entries_processed % 500_000 == 0 {
                                        eprintln!("    Processed {} MISS/UNKNOWN entries, tracking {} unique URLs",
                                            entries_processed, miss_tracker.len());
                                    }
                                }
                            }
                        }
                    }
                    Ok(())
                })();

                if let Err(e) = scan_result {
                    eprintln!("  WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                    continue;
                }
            }

            // Build map of candidate URLs with their response sizes (those with threshold+ misses)
            let candidates: HashMap<String, i64> = miss_tracker
                .iter()
                .filter(|(_, &count)| count >= miss_threshold)
                .map(|(url, _)| {
                    let size = url_sizes.get(url).copied().unwrap_or(0);
                    (url.clone(), size)
                })
                .collect();

            let candidate_count = candidates.len();
            eprintln!("Found {} URLs with {}+ MISS/UNKNOWN entries for {}", candidate_count, miss_threshold, service);

            // Save all candidate URLs before filtering - needed for log/DB cleanup even when cache files are gone
            let all_candidate_urls: std::collections::HashSet<String> = candidates.keys().cloned().collect();

            let corrupted_urls_with_sizes: HashMap<String, i64> = if no_cache_check {
                eprintln!("Skipping cache file existence check (logs-only mode)");
                eprintln!("Using all {} candidate URLs", candidate_count);
                candidates
            } else {
                let filtered: HashMap<String, i64> = candidates
                    .into_iter()
                    .filter(|(url, _response_size)| {
                        let cache_path = cache_utils::calculate_cache_path_no_range(&cache_dir, &service_lower, url);
                        if cache_path.exists() {
                            return true;
                        }
                        let range_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, 0, 1_048_575);
                        range_path.exists()
                    })
                    .collect();

                let filtered_out = candidate_count - filtered.len();
                if filtered_out > 0 {
                    eprintln!("Filtered out {} URLs where cache file does not exist on disk (likely cold-cache misses)", filtered_out);
                }
                eprintln!("Confirmed {} corrupted URLs for {} (file exists on disk with {}+ MISS/UNKNOWN)", filtered.len(), service, miss_threshold);
                filtered
            };

            if corrupted_urls_with_sizes.is_empty() && all_candidate_urls.is_empty() {
                eprintln!("No corrupted chunks found, nothing to remove");
                write_progress(&progress_path, &reporter, "completed", "signalr.corruptionRemove.noChunksFound", json!({}), 100.0, 0, 0)?;
                return Ok(());
            }

            if corrupted_urls_with_sizes.is_empty() {
                eprintln!("No cache files found on disk, but {} stale log entries detected - cleaning up logs and database", all_candidate_urls.len());
            }

            // Use all candidates for log/DB cleanup (includes stale entries where cache files are gone)
            let corrupted_urls: std::collections::HashSet<String> = all_candidate_urls;

            // PASS 2: Filter log files, removing MISS/UNKNOWN lines for corrupted URLs.
            // Uses the shared scan-then-rewrite helper (Aho-Corasick prefilter,
            // untouched files skip recompression entirely).
            eprintln!("Step 2: Filtering log files to remove corrupted chunks...");

            write_progress(&progress_path, &reporter, "filtering", "signalr.corruptionRemove.filteringLogs", json!({ "totalFiles": total_files }), 30.0, 0, total_files)?;

            let prefilter = log_purge::RemovalPrefilter::new(
                corrupted_urls.iter().map(|url| url.as_bytes().to_vec()).collect::<Vec<_>>(),
            )?;
            let filter_progress_cb = |files_done: usize, total: usize| {
                // Update progress during filtering (30-70%)
                let filter_percent = 30.0 + (files_done as f64 / total.max(1) as f64) * 40.0;
                let _ = write_progress(
                    &progress_path,
                    &reporter,
                    "filtering",
                    "signalr.corruptionRemove.filteringFile",
                    json!({ "fileIndex": files_done, "totalFiles": total }),
                    filter_percent,
                    files_done,
                    total,
                );
            };
            // Only remove MISS/UNKNOWN lines for corrupted URLs (HIT lines are kept
            // intact to prevent snowball corruption detection)
            let (total_lines_removed, _log_filter_permission_errors) =
                log_purge::rewrite_matching_log_entries(
                    &log_dir,
                    "corrupted",
                    &prefilter,
                    |entry| {
                        entry.service == service_lower
                            && corrupted_urls.contains(&entry.url)
                            && (entry.cache_status == "MISS" || entry.cache_status == "UNKNOWN")
                    },
                    Some(&filter_progress_cb),
                )?;

            let total_urls = corrupted_urls_with_sizes.len();
            write_progress(&progress_path, &reporter, "removing_cache", "signalr.corruptionRemove.removingCacheFiles", json!({ "totalUrls": total_urls }), 70.0, 0, total_urls)?;

            // Step 3: Delete ALL cache file chunks from disk
            eprintln!("Step 3: Deleting cache files...");
            let mut deleted_count = 0;
            let mut permission_errors = 0;
            let mut other_errors = 0;
            let slice_size: i64 = 1_048_576; // 1MB

            for (url_index, (url, response_size)) in corrupted_urls_with_sizes.iter().enumerate() {
                // Cooperative cancellation: stop between chunks.
                if cancel::is_cancelled() {
                    let cache_percent = 70.0 + (url_index as f64 / total_urls.max(1) as f64) * 20.0;
                    eprintln!("Cancellation requested at URL {}/{} — flushing partial progress.", url_index, total_urls);
                    let _ = write_progress(&progress_path, &reporter, "removing_cache", "signalr.corruptionRemove.removingCacheFile", json!({ "urlIndex": url_index, "totalUrls": total_urls }), cache_percent, url_index, total_urls);
                    return Ok(());
                }

                // Update progress during cache removal (70-95%)
                if url_index % 50 == 0 || url_index == total_urls - 1 {
                    let cache_percent = 70.0 + (url_index as f64 / total_urls.max(1) as f64) * 20.0;
                    write_progress(&progress_path, &reporter, "removing_cache", "signalr.corruptionRemove.removingCacheFile", json!({ "urlIndex": url_index + 1, "totalUrls": total_urls }), cache_percent, url_index, total_urls)?;
                }

                // FIRST: Try the no-range format (standard lancache format)
                let cache_path_no_range = cache_utils::calculate_cache_path_no_range(&cache_dir, &service_lower, url);

                if cache_path_no_range.exists() {
                    match cache_utils::safe_path_under_root(&cache_dir, &cache_path_no_range) {
                        Ok(_) => match std::fs::remove_file(&cache_path_no_range) {
                            Ok(_) => {
                                deleted_count += 1;
                                if deleted_count % 100 == 0 {
                                    eprintln!("  Deleted {} cache files...", deleted_count);
                                }
                            }
                            Err(e) => {
                                if e.kind() == std::io::ErrorKind::PermissionDenied {
                                    permission_errors += 1;
                                    if permission_errors <= 5 {
                                        eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path_no_range.display(), e);
                                    }
                                } else {
                                    other_errors += 1;
                                    eprintln!("  Warning: Failed to delete {}: {}", cache_path_no_range.display(), e);
                                }
                            }
                        },
                        Err(e) => {
                            other_errors += 1;
                            eprintln!("  skipping unsafe path {}: {}", cache_path_no_range.display(), e);
                        }
                    }
                } else {
                    // FALLBACK: Try the chunked format with bytes range
                    if *response_size == 0 {
                        let cache_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, 0, 1_048_575);

                        if cache_path.exists() {
                            match cache_utils::safe_path_under_root(&cache_dir, &cache_path) {
                                Ok(_) => match std::fs::remove_file(&cache_path) {
                                    Ok(_) => deleted_count += 1,
                                    Err(e) => {
                                        if e.kind() == std::io::ErrorKind::PermissionDenied {
                                            permission_errors += 1;
                                            if permission_errors <= 5 {
                                                eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                                            }
                                        } else {
                                            other_errors += 1;
                                            eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                                        }
                                    }
                                },
                                Err(e) => {
                                    other_errors += 1;
                                    eprintln!("  skipping unsafe path {}: {}", cache_path.display(), e);
                                }
                            }
                        }
                    } else {
                        let mut start: i64 = 0;
                        while start < *response_size {
                            let end = start + slice_size - 1;

                            let cache_path = cache_utils::calculate_cache_path(&cache_dir, &service_lower, url, start as u64, end as u64);

                            if cache_path.exists() {
                                match cache_utils::safe_path_under_root(&cache_dir, &cache_path) {
                                    Ok(_) => match std::fs::remove_file(&cache_path) {
                                        Ok(_) => {
                                            deleted_count += 1;
                                            if deleted_count % 100 == 0 {
                                                eprintln!("  Deleted {} cache files...", deleted_count);
                                            }
                                        }
                                        Err(e) => {
                                            if e.kind() == std::io::ErrorKind::PermissionDenied {
                                                permission_errors += 1;
                                                if permission_errors <= 5 {
                                                    eprintln!("  ERROR: Permission denied deleting {}: {}", cache_path.display(), e);
                                                }
                                            } else {
                                                other_errors += 1;
                                                eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                                            }
                                        }
                                    },
                                    Err(e) => {
                                        other_errors += 1;
                                        eprintln!("  skipping unsafe path {}: {}", cache_path.display(), e);
                                    }
                                }
                            }

                            start += slice_size;
                        }
                    }
                }
            }

            eprintln!("Deleted {} cache files", deleted_count);
            if permission_errors > 0 {
                eprintln!("ERROR: {} files could not be deleted due to permission errors", permission_errors);
                if permission_errors > 5 {
                    eprintln!("  (only first 5 errors shown)");
                }
            }
            if other_errors > 0 {
                eprintln!("WARNING: {} files could not be deleted due to other errors", other_errors);
            }
            eprintln!("Removed {} total log lines across {} files", total_lines_removed, log_files.len());

            // CRITICAL: If we had permission errors, do NOT delete database records
            if permission_errors > 0 {
                let puid = std::env::var("PUID").unwrap_or_else(|_| "1000".to_string());
                let pgid = std::env::var("PGID").unwrap_or_else(|_| "1000".to_string());
                let error_msg = format!(
                    "ABORTED: Cannot delete database records because {} cache files could not be deleted due to permission errors. \
                    This is likely caused by incorrect PUID/PGID settings. The lancache container is configured to run as UID/GID {}:{}. \
                    Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership.",
                    permission_errors, puid, pgid
                );
                eprintln!("\n{}", error_msg);
                write_progress(&progress_path, &reporter, "failed", "signalr.corruptionRemove.error.fatal", json!({ "errorDetail": error_msg }), 0.0, 0, 0)?;
                std::process::exit(1);
            }

            // Step 4: Delete database records for corrupted downloads
            eprintln!("Step 4: Deleting database records...");
            write_progress(&progress_path, &reporter, "removing_database", "signalr.corruptionRemove.deletingDb", json!({}), 90.0, 0, 0)?;

            let pool = db::create_pool().await?;
            let (downloads_deleted, log_entries_deleted) = delete_corrupted_from_database(&pool, &service, &corrupted_urls).await?;

            write_progress(&progress_path, &reporter, "completed", "signalr.corruptionRemove.complete", json!({ "count": corrupted_urls_with_sizes.len(), "service": service, "files": deleted_count, "logLines": total_lines_removed, "downloads": downloads_deleted, "logEntries": log_entries_deleted }), 100.0, 0, 0)?;
            eprintln!("\n=== Corruption Removal Summary ===");
            eprintln!("Corrupted URLs removed: {}", corrupted_urls_with_sizes.len());
            eprintln!("Cache files deleted: {}", deleted_count);
            eprintln!("Log lines removed: {}", total_lines_removed);
            eprintln!("Database downloads deleted: {}", downloads_deleted);
            eprintln!("Database log entries deleted: {}", log_entries_deleted);
            eprintln!("Removal completed successfully");
        }
    }

    Ok(())
}
