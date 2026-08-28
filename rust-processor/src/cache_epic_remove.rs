use anyhow::Result;
use clap::Parser;
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::PathBuf;

use lancache_processor::cache_utils;
use lancache_processor::cancel;
use lancache_processor::db;
use lancache_processor::progress_events;
use lancache_processor::removal_core;
use lancache_processor::log_purge;
use progress_events::ProgressReporter;
use removal_core::{LogScope, ProgressCadence, RemovalStageKeys};

/// Epic game cache removal utility - removes all cache files, log entries,
/// and database records for a specific Epic game identified by name.
///
/// Identity is `(GameName, EpicAppId IS NOT NULL)`. The shared delete/cleanup/
/// purge/permission tail lives in `removal_core`; this bin owns only the Epic
/// HEAD: the `GameName + EpicAppId` URL query and the matching DB-row delete.
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

    /// Per-stem saved ingestion positions (JSON object of stem name to line index). Lets the
    /// log purge split removed lines at the read position so the position adjustment is exact.
    #[arg(long = "stem-positions")]
    stem_positions: Option<String>,

    /// Cache-key recipe of the target datasource: "monolithic" (default) | "bare_metal"
    #[arg(long = "key-scheme", default_value = "monolithic")]
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

/// Epic removal stage keys (`signalr.epicRemove.*`). Only the per-file cache progress
/// key is consumed by `removal_core`; the remaining lifecycle keys are emitted directly
/// in `main` below with the same literal strings as before.
const EPIC_STAGE_KEYS: RemovalStageKeys = RemovalStageKeys {
    cache_file_progress: "signalr.epicRemove.cache.file.progress",
};

#[derive(Debug, Serialize)]
struct RemovalReport {
    game_name: String,
    cache_files_deleted: usize,
    total_bytes_freed: u64,
    empty_dirs_removed: usize,
    log_entries_removed: u64,
    /// Removed-line count per log-source stem, series-wide - the caller subtracts these
    /// from the saved ingestion positions so a purge cannot shift them past unread lines.
    log_lines_removed_by_source: std::collections::HashMap<String, u64>,
    /// The already-read subset of `log_lines_removed_by_source` (series index below the saved
    /// position). This is the amount the position itself comes back by; the full map above is
    /// what the on-disk total-line count comes down by.
    log_lines_removed_before_position_by_source: std::collections::HashMap<String, u64>,
}

/// Preserve URL provenance when a bare-metal candidate's recipe-computed key
/// could not be verified. The cache helper leaves that file untouched, so the
/// access-log and database rows must remain available for a corrected retry.
const PRIMARY_URL_QUERY: &str =
    "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
         WHERE d.\"GameName\" = $1 AND d.\"EpicAppId\" IS NOT NULL AND le.\"Url\" IS NOT NULL";

const FALLBACK_URL_QUERY: &str =
    "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         WHERE LOWER(le.\"Service\") = 'epicgames'
         AND le.\"Url\" IS NOT NULL
         AND le.\"DownloadId\" IN (
             SELECT \"Id\" FROM \"Downloads\" WHERE \"GameName\" = $1 AND \"EpicAppId\" IS NOT NULL
         )";

/// Query the database for all URLs associated with an Epic game.
/// Joins LogEntries with Downloads via DownloadId to find URLs for the specific game.
/// Returns: HashMap<URL, (service_lowercase, max_bytes_served)>
async fn get_epic_game_urls_from_db(pool: &PgPool, game_name: &str) -> Result<HashMap<String, (String, i64)>> {
    eprintln!("Querying database for Epic game URLs...");

    // Query LogEntries joined with Downloads to find all URLs for this Epic game
    let rows = sqlx::query(PRIMARY_URL_QUERY)
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
    let fallback_rows = sqlx::query(FALLBACK_URL_QUERY)
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

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();
    cache_utils::set_active_key_scheme(cache_utils::CacheKeyScheme::from_config_str(
        &args.key_scheme,
    ));

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    let game_name = &args.game_name;
    let output_json = PathBuf::from(&args.output_json);
    let progress_path = PathBuf::from(&args.progress_json);
    let reporter = ProgressReporter::new(args.progress);

    // Whole removal routed through the single failure funnel; the permission-error abort
    // below now just `bail!`s with context instead of also hand-emitting `failed`, so
    // finish_or_exit is the ONE place this bin's failures get emitted.
    let result: Result<()> = async {
    eprintln!("Epic Game Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Game name: {}", game_name);

    if !log_dir.exists() {
        let msg = format!("Log directory not found: {}", log_dir.display());
        anyhow::bail!("{}", msg);
    }

    if !cache_dir.exists() {
        let msg = format!("Cache directory not found: {}", cache_dir.display());
        anyhow::bail!("{}", msg);
    }

    let pool = db::create_pool().await?;

    // A count run must not announce itself as a removal: the whole point of the number is that
    // the user can trust what the confirmation says.
    let starting_stage_key = if args.count_only {
        "signalr.epicRemove.counting.starting"
    } else {
        "signalr.epicRemove.starting"
    };
    removal_core::write_progress(&progress_path, &reporter, "starting", starting_stage_key, json!({ "gameName": game_name }), 0.0, 0, 0)?;

    // Query database for URLs
    removal_core::write_progress(&progress_path, &reporter, "querying_database", "signalr.epicRemove.db.querying", json!({}), 5.0, 0, 0)?;
    let url_data = get_epic_game_urls_from_db(&pool, game_name).await?;

    // A count run stops here. It walks the same list a removal would walk, reports how many of
    // those files exist on disk, and returns before the cache sweep, the access.log purge and
    // the database delete below are reachable. A game with no URLs reports zero rather than
    // taking the no-URL exit, so the confirmation always has a number. [6][8]
    if args.count_only {
        let collection_progress = removal_core::CollectionProgress {
            progress_path: &progress_path,
            reporter: &reporter,
            stage_key: "signalr.epicRemove.counting.progress",
        };
        let cache_files_found = removal_core::count_cache_files(
            &cache_dir,
            &url_data,
            &output_json,
            game_name,
            cache_utils::active_key_scheme(),
            &collection_progress,
        )?;
        removal_core::write_progress(&progress_path, &reporter, "completed", "signalr.epicRemove.counting.complete", json!({ "files": cache_files_found, "gameName": game_name }), 100.0, cache_files_found, cache_files_found)?;
        return Ok(());
    }

    if url_data.is_empty() {
        eprintln!("No URLs found for Epic game '{}'", game_name);

        let report = RemovalReport {
            game_name: game_name.to_string(),
            cache_files_deleted: 0,
            total_bytes_freed: 0,
            empty_dirs_removed: 0,
            log_entries_removed: 0,
            log_lines_removed_by_source: Default::default(),
            log_lines_removed_before_position_by_source: Default::default(),
        };

        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;

        removal_core::write_progress(&progress_path, &reporter, "completed", "signalr.epicRemove.noUrls", json!({}), 100.0, 0, 0)?;
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}'", url_data.len(), game_name);

    // Step 1: Remove cache files
    let url_count = url_data.len();
    removal_core::write_progress(&progress_path, &reporter, "removing_cache", "signalr.epicRemove.cache.removing", json!({ "count": url_count }), 10.0, 0, 0)?;
    eprintln!("\nRemoving cache files...");
    let outcome = removal_core::remove_cache_files(
        &cache_dir,
        &url_data,
        &progress_path,
        &reporter,
        &EPIC_STAGE_KEYS,
        ProgressCadence::OnPercentAdvance,
        cache_utils::active_key_scheme(),
    )?;

    // If cancellation arrived during cache removal, do directory cleanup and exit 0.
    if cancel::is_cancelled() {
        eprintln!("Cancellation confirmed — cleaning up partial directories and exiting.");
        cache_utils::cleanup_empty_directories(&cache_dir, outcome.parent_dirs);
        return Ok(());
    }

    // Step 2: Clean up empty directories
    removal_core::write_progress(&progress_path, &reporter, "cleaning_directories", "signalr.epicRemove.dirs.cleaning", json!({}), 70.0, 0, 0)?;
    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cache_utils::cleanup_empty_directories(&cache_dir, outcome.parent_dirs);

    if let Err(error) = removal_core::ensure_cache_deletions_verified(outcome.verification_skips) {
        let report = RemovalReport {
            game_name: game_name.to_string(),
            cache_files_deleted: outcome.deleted_files,
            total_bytes_freed: outcome.bytes_freed,
            empty_dirs_removed,
            log_entries_removed: 0,
            log_lines_removed_by_source: Default::default(),
            log_lines_removed_before_position_by_source: Default::default(),
        };
        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;
        return Err(error);
    }

    // Step 3: Remove log entries from access log text files
    removal_core::write_progress(&progress_path, &reporter, "removing_logs", "signalr.epicRemove.logs.removing", json!({}), 80.0, 0, 0)?;
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let stem_positions = args
        .stem_positions
        .as_deref()
        .and_then(log_purge::read_stem_positions);
    let log_outcome = removal_core::purge_log_entries(
        &log_dir,
        &urls_to_remove,
        &LogScope::Urls,
        stem_positions.as_ref(),
    )?;
    let log_entries_removed = log_outcome.lines_removed;
    let log_permission_errors = log_outcome.permission_errors;
    let log_lines_removed_by_source = log_outcome.lines_removed_by_stem;
    let log_lines_removed_before_position_by_source =
        log_outcome.lines_removed_before_position_by_stem;

    // Step 4: Check for permission errors before touching database
    let total_permission_errors = outcome.permission_errors + log_permission_errors;
    if total_permission_errors > 0 {
        let error_msg = removal_core::permission_error_message(
            total_permission_errors,
            outcome.permission_errors,
            log_permission_errors,
        );
        eprintln!("\n{}", error_msg);

        let report = RemovalReport {
            game_name: game_name.to_string(),
            cache_files_deleted: outcome.deleted_files,
            total_bytes_freed: outcome.bytes_freed,
            empty_dirs_removed,
            log_entries_removed,
            log_lines_removed_by_source: log_lines_removed_by_source.clone(),
            log_lines_removed_before_position_by_source:
                log_lines_removed_before_position_by_source.clone(),
        };
        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;

        anyhow::bail!("{}", error_msg);
    }

    // Step 5: Delete database records
    removal_core::write_progress(&progress_path, &reporter, "removing_database", "signalr.epicRemove.db.deleting", json!({}), 90.0, 0, 0)?;
    eprintln!("\nRemoving database records...");
    let (_log_records, _download_records) = delete_epic_game_from_database(&pool, game_name).await?;

    // Write final report
    let report = RemovalReport {
        game_name: game_name.clone(),
        cache_files_deleted: outcome.deleted_files,
        total_bytes_freed: outcome.bytes_freed,
        empty_dirs_removed,
        log_entries_removed,
        log_lines_removed_by_source,
        log_lines_removed_before_position_by_source,
    };

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    removal_core::write_progress(&progress_path, &reporter, "completed", "signalr.epicRemove.complete", json!({ "files": report.cache_files_deleted, "gb": report.total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": report.log_entries_removed, "gameName": game_name }), 100.0, 0, 0)?;

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Cache files deleted: {}", report.cache_files_deleted);
    eprintln!("Space freed: {:.2} MB", report.total_bytes_freed as f64 / 1_048_576.0);
    eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
    eprintln!("Log entries removed: {}", report.log_entries_removed);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
    }.await;
    progress_events::finish_or_exit(&reporter, "signalr.epicRemove.error.fatal", result);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{FALLBACK_URL_QUERY, PRIMARY_URL_QUERY};

    #[test]
    fn primary_query_gates_identity_on_game_name_and_epic_app_id() {
        assert!(PRIMARY_URL_QUERY.contains("d.\"GameName\" = $1"));
        assert!(PRIMARY_URL_QUERY.contains("d.\"EpicAppId\" IS NOT NULL"));
        assert!(PRIMARY_URL_QUERY.contains("le.\"Url\" IS NOT NULL"));
    }

    #[test]
    fn fallback_query_gates_epic_service_and_download_identity() {
        assert!(FALLBACK_URL_QUERY.contains("LOWER(le.\"Service\") = 'epicgames'"));
        assert!(FALLBACK_URL_QUERY.contains("le.\"DownloadId\" IN ("));
        assert!(FALLBACK_URL_QUERY.contains("\"GameName\" = $1"));
        assert!(FALLBACK_URL_QUERY.contains("\"EpicAppId\" IS NOT NULL"));
    }
}
