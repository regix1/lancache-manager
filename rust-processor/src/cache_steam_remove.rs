use anyhow::Result;
use clap::Parser;
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::PathBuf;

mod cache_utils;
mod cancel;
mod db;
mod log_discovery;
mod log_purge;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod removal_core;
mod service_utils;
mod tact_products;

use progress_events::ProgressReporter;
use removal_core::{ProgressCadence, RemovalStageKeys};

/// Steam game cache removal utility - removes all cache files for a specific game.
///
/// Unlike the other removal bins, Steam removal carries a depot-safety HEAD: cache-file
/// URL selection and the access.log purge are both narrowed to depots EXCLUSIVELY owned
/// by the target game, so removing one game never strips another game's cache slices or
/// HIT/MISS log lines (depots are many-to-one with AppId). The shared delete/cleanup/
/// purge/permission tail lives in `removal_core`; this bin owns the depot head, the
/// `--skip-file-probe` fast path, and the depot-bearing report.
#[derive(clap::Parser, Debug)]
#[command(name = "cache_steam_remove")]
#[command(about = "Removes all cache files for a specific Steam game by scanning logs")]
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

    /// Skip the cache-file disk probe (all game rows already evicted).
    /// When set, no `path.exists()` scanning of candidate cache files occurs
    /// and no directory cleanup runs, but the log rewrite and database
    /// cleanup still execute normally.
    #[arg(long)]
    skip_file_probe: bool,

    /// Cache-key recipe of the target datasource: "monolithic" (default) | "bare_metal"
    #[arg(long = "key-scheme", default_value = "monolithic")]
    key_scheme: String,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,
}

/// Steam removal stage keys (`signalr.gameRemove.*`). Only the per-file cache progress
/// key is consumed by `removal_core`; the remaining lifecycle keys are emitted directly
/// in `main` below with the same literal strings as before.
const STEAM_STAGE_KEYS: RemovalStageKeys = RemovalStageKeys {
    cache_file_progress: "signalr.gameRemove.cache.file.progress",
};

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

/// Preserve URL provenance when a bare-metal candidate's recipe-computed key
/// could not be verified. The cache helper leaves that file untouched, so the
/// access-log and database rows must remain available for a corrected retry.
fn ensure_cache_deletions_verified(verification_skips: usize) -> Result<()> {
    if verification_skips == 0 {
        return Ok(());
    }

    anyhow::bail!(
        "Cache deletion safety verification failed for {} file(s); skipped files, access logs, and database records were left intact",
        verification_skips
    )
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

/// Query the database for all URLs (and their depot ids) owned by this Steam game.
/// Returns: HashMap<URL, (service_lowercase, max_bytes_served, depot_ids)>. The depot
/// set rides along for the final report; cache-file deletion uses only (service, url).
async fn get_game_urls_from_db(pool: &PgPool, game_app_id: u32) -> Result<HashMap<String, (String, i64, HashSet<u32>)>> {
    eprintln!("Querying database for game URLs and depot IDs...");

    // Query 1: Mapped games - join LogEntries to SteamDepotMappings via DepotId.
    // Works for games where PicsDataService has populated SteamDepotMappings.
    //
    // DepotId is many-to-one with AppId (shared / mis-mapped depots; SteamDepotMappings has no
    // unique index on DepotId, and orphan-resolution `depotId-1/-2` can attach a depot to the
    // wrong app). A bare DepotId join therefore pulls in URLs that ANOTHER game downloaded from a
    // depot that also maps to this AppId, and `remove_cache_files` would then delete that
    // other game's cache files. Mirror the C# `safeDepotIds` idea here at the source: exclude any
    // DepotId that ALSO belongs to a different AppId so cache-file URL selection stays anchored to
    // depots EXCLUSIVELY owned by this game.
    //
    // CRITICAL: this exclusion must cover the SAME full "shared" set that `get_shared_depot_ids`
    // subtracts for the log purge — i.e. depots shared via SteamDepotMappings (AppId<>$1) AND
    // depots another game's Downloads carry (GameAppId<>$1). Excluding only the SteamDepotMappings
    // half would let cache-file selection delete a Downloads-shared depot's files that the log
    // purge protects, so the two halves of the cross-game guard would disagree. Both NOT IN clauses
    // below keep cache-file scope == log-purge scope. (Query 3 below, the Downloads-FK path, remains
    // fully AppId-scoped and is the correct delisted-app/Aion route — it returns this game's OWN
    // urls and is intentionally NOT narrowed by the shared-depot filter.)
    let rows = sqlx::query(
        "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"DepotId\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         INNER JOIN \"SteamDepotMappings\" sdm ON le.\"DepotId\" = sdm.\"DepotId\"
         WHERE sdm.\"AppId\" = $1 AND le.\"Url\" IS NOT NULL
           AND le.\"DepotId\" NOT IN (
               SELECT \"DepotId\" FROM \"SteamDepotMappings\" WHERE \"AppId\" <> $1
           )
           AND le.\"DepotId\" NOT IN (
               SELECT \"DepotId\" FROM \"Downloads\"
               WHERE \"DepotId\" IS NOT NULL AND \"GameAppId\" IS NOT NULL AND \"GameAppId\" <> $1
           )"
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

    // Query 3: Downloads-FK join - catches delisted apps (e.g., Aion AppID 373680 / depot 373681)
    // where SteamDepotMappings has no row but Downloads.GameAppId is set correctly.
    // Mirrors GameCacheDetectionService.ResolveUnknownGamesInCacheAsync (C# line 1033/1086).
    // Uses Option A (DownloadId FK) - no .sqlx offline cache present, DownloadId FK is populated
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

/// Depot IDs that are NOT exclusively owned by this game — i.e. they also belong to a DIFFERENT
/// AppId, either via `SteamDepotMappings` (AppId <> $1) or via another game's `Downloads`
/// (GameAppId <> $1). These are shared / mis-mapped depots: the access.log purge predicate matches
/// lines on `depot_id ∈ valid_depot_ids`, so feeding a shared depot id would strip the OTHER game's
/// HIT/MISS lines (cross-game data loss). Subtracting this set from `valid_depot_ids` yields the
/// "safe" depot set, mirroring the C# `safeDepotIds` partial-eviction guard but at cross-game scope.
async fn get_shared_depot_ids(pool: &PgPool, game_app_id: u32) -> Result<HashSet<u32>> {
    let mut shared: HashSet<u32> = HashSet::new();

    // Depots mapped to a DIFFERENT AppId in SteamDepotMappings.
    let mapping_rows = sqlx::query(
        "SELECT DISTINCT \"DepotId\" FROM \"SteamDepotMappings\" WHERE \"AppId\" <> $1"
    )
    .bind(game_app_id as i64)
    .fetch_all(pool)
    .await?;

    for row in mapping_rows {
        let depot_id: i64 = row.get("DepotId");
        shared.insert(depot_id as u32);
    }

    // Depots that another game's Downloads rows carry (GameAppId set and != this game).
    let download_rows = sqlx::query(
        "SELECT DISTINCT \"DepotId\" FROM \"Downloads\"
         WHERE \"DepotId\" IS NOT NULL AND \"GameAppId\" IS NOT NULL AND \"GameAppId\" <> $1"
    )
    .bind(game_app_id as i64)
    .fetch_all(pool)
    .await?;

    for row in download_rows {
        let depot_id: i64 = row.get("DepotId");
        shared.insert(depot_id as u32);
    }

    Ok(shared)
}

/// Pure set-narrowing used for the access.log purge: keep only depots EXCLUSIVELY owned by the
/// target game (present in `valid` but NOT in `shared`). Extracted so the cross-game safety logic
/// is unit-testable without a live database.
fn compute_safe_depot_ids(valid: &HashSet<u32>, shared: &HashSet<u32>) -> HashSet<u32> {
    valid.difference(shared).copied().collect()
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

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();
    cache_utils::set_active_key_scheme(cache_utils::CacheKeyScheme::from_config_str(
        &args.key_scheme,
    ));

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    let game_app_id = args.game_app_id;
    let output_json = PathBuf::from(&args.output_json);
    let progress_path = PathBuf::from(&args.progress_json);
    let reporter = ProgressReporter::new(args.progress);

    // Whole removal routed through the single failure funnel; the permission-error abort
    // below now just `bail!`s with context instead of also hand-emitting `failed`, so
    // finish_or_exit is the ONE place this bin's failures get emitted.
    let result: Result<()> = async {
    eprintln!("Game Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Game AppID: {}", game_app_id);

    if !log_dir.exists() {
        let msg = format!("Log directory not found: {}", log_dir.display());
        anyhow::bail!("{}", msg);
    }

    if !cache_dir.exists() {
        let msg = format!("Cache directory not found: {}", cache_dir.display());
        anyhow::bail!("{}", msg);
    }

    let pool = db::create_pool().await?;

    // Get game name from database
    let game_name = get_game_name_from_db(&pool, game_app_id).await?;
    eprintln!("Game: {}", game_name);

    removal_core::write_progress(&progress_path, &reporter, "starting", "signalr.gameRemove.starting", json!({ "gameName": game_name, "gameAppId": game_app_id }), 0.0, 0, 0)?;

    // Get valid depot IDs for this game from database
    removal_core::write_progress(&progress_path, &reporter, "querying_database", "signalr.gameRemove.db.querying", json!({}), 5.0, 0, 0)?;
    let valid_depot_ids = get_game_depot_ids(&pool, game_app_id).await?;
    eprintln!("Valid depot IDs for this game: {:?}", valid_depot_ids);

    // Narrow to depots EXCLUSIVELY owned by this game before they reach the access.log purge.
    // The log predicate (log_purge.rs) removes any line whose `depot_id ∈ valid_depot_ids`, so a
    // depot shared with another AppId (SteamDepotMappings AppId<>$1) or another game's Downloads
    // (GameAppId<>$1) would strip THAT game's HIT/MISS lines. Subtract the shared set; the result
    // (`safe_depot_ids`) is what we hand to the log purge below. This mirrors the C#
    // `safeDepotIds` guard. Cache-file URL selection is narrowed separately inside
    // get_game_urls_from_db (Query 1), so deletion and the log rewrite stay in the same scope.
    //
    // Short-circuit when this game owns no depots (e.g. the delisted-app/Aion path where URL/DB
    // removal goes through Query 3): `compute_safe_depot_ids(empty, _)` is always empty, so skip
    // the two wide SteamDepotMappings/Downloads scans entirely. Mirrors the C#
    // `evictedDepotIds.Count == 0` guard.
    let safe_depot_ids: HashSet<u32> = if valid_depot_ids.is_empty() {
        HashSet::new()
    } else {
        let shared_depot_ids = get_shared_depot_ids(&pool, game_app_id).await?;
        compute_safe_depot_ids(&valid_depot_ids, &shared_depot_ids)
    };
    let excluded_depot_count = valid_depot_ids.len() - safe_depot_ids.len();
    if excluded_depot_count > 0 {
        eprintln!(
            "Excluded {} depot ID(s) from log purge for game AppID {} because they also appear in other apps' mappings/downloads (cross-game safety)",
            excluded_depot_count, game_app_id
        );
    }
    eprintln!("Safe (exclusively-owned) depot IDs for log purge: {:?}", safe_depot_ids);

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

        removal_core::write_progress(&progress_path, &reporter, "completed", "signalr.gameRemove.noUrls", json!({}), 100.0, 0, 0)?;
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}'", url_data.len(), game_name);

    // File-probe + directory cleanup phase.
    // Skipped when `--skip-file-probe` is set (caller already knows every row for
    // this game is IsEvicted, so the lancache has nothing to delete on disk).
    // The log rewrite and DB cleanup below still run unconditionally.
    let (
        deleted_files,
        bytes_freed,
        empty_dirs_removed,
        cache_permission_errors,
        verification_skips,
    ) = if args.skip_file_probe {
        eprintln!("\nSkipping cache file probe for {} URLs (fully evicted game)", url_data.len());
        removal_core::write_progress(&progress_path, &reporter, "removing_cache", "signalr.gameRemove.cache.skippedEvicted", json!({}), 10.0, 0, 0)?;
        removal_core::write_progress(&progress_path, &reporter, "cleaning_directories", "signalr.gameRemove.dirs.skippedEvicted", json!({}), 70.0, 0, 0)?;
        (0usize, 0u64, 0usize, 0usize, 0usize)
    } else {
        let count = url_data.len();
        removal_core::write_progress(&progress_path, &reporter, "removing_cache", "signalr.gameRemove.cache.removing", json!({ "count": count }), 10.0, 0, 0)?;
        eprintln!("\nRemoving cache files...");

        // Steam shares the parallel-delete tail with every other bin; it just feeds a
        // (service, bytes) view of its (service, bytes, depots) map and uses the Steam
        // cadence (percent-advance OR every 8th probe). The depot set rides along in the
        // report below; cache-file deletion is purely (service, url) based.
        let url_data_for_delete: HashMap<String, (String, i64)> = url_data
            .iter()
            .map(|(url, (service, bytes, _depots))| (url.clone(), (service.clone(), *bytes)))
            .collect();
        let outcome = removal_core::remove_cache_files(
            &cache_dir,
            &url_data_for_delete,
            &progress_path,
            &reporter,
            &STEAM_STAGE_KEYS,
            ProgressCadence::OnPercentAdvanceOrEveryEighth,
            cache_utils::active_key_scheme(),
        )?;

        // If cancellation arrived during cache removal, finish directory cleanup of dirs
        // already collected, then exit 0.  Log/DB work is skipped — C# re-runs detection.
        if cancel::is_cancelled() {
            eprintln!("Cancellation confirmed — cleaning up partial directories and exiting.");
            cache_utils::cleanup_empty_directories(&cache_dir, outcome.parent_dirs);
            return Ok(());
        }

        removal_core::write_progress(&progress_path, &reporter, "cleaning_directories", "signalr.gameRemove.dirs.cleaning", json!({}), 70.0, 0, 0)?;
        eprintln!("\nCleaning up empty directories...");
        let empty_dirs = cache_utils::cleanup_empty_directories(&cache_dir, outcome.parent_dirs);
        (
            outcome.deleted_files,
            outcome.bytes_freed,
            empty_dirs,
            outcome.permission_errors,
            outcome.verification_skips,
        )
    };

    if let Err(error) = ensure_cache_deletions_verified(verification_skips) {
        let report = RemovalReport {
            game_app_id,
            game_name: game_name.to_string(),
            cache_files_deleted: deleted_files,
            total_bytes_freed: bytes_freed,
            empty_dirs_removed,
            log_entries_removed: 0,
            depot_ids: vec![],
        };
        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;
        return Err(error);
    }

    // Remove log entries for this game. Per-file progress fills the 80-90% band
    // so fast --skip-file-probe runs still surface visible stages.
    removal_core::write_progress(&progress_path, &reporter, "removing_logs", "signalr.gameRemove.logs.removing", json!({}), 80.0, 0, 0)?;
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let log_file_progress = |processed: usize, total: usize| {
        let percent = 80.0 + (processed as f64 / total.max(1) as f64) * 10.0;
        let _ = removal_core::write_progress(
            &progress_path,
            &reporter,
            "removing_logs",
            "signalr.gameRemove.logs.fileProgress",
            json!({ "n": processed, "total": total }),
            percent,
            processed,
            total,
        );
    };
    // Use the narrowed `safe_depot_ids` (exclusively-owned depots) for the log purge so shared-depot
    // lines belonging to OTHER games are not stripped. This call runs for both the normal and the
    // `--skip-file-probe` fast path, so the fast path inherits the same cross-game safety. This
    // depot-scoped purge is the one tail divergence Steam carries; every other bin purges url-only
    // (removal_core::LogScope::Urls). Steam calls log_purge directly so it can also pass the
    // per-file progress callback that fills the 80-90% band.
    let (log_entries_removed, log_permission_errors) = log_purge::remove_log_entries_for_game(
        &log_dir,
        &urls_to_remove,
        &safe_depot_ids,
        Some(&log_file_progress),
    )?;

    // CRITICAL: Check for permission errors before deleting database records
    let total_permission_errors = cache_permission_errors + log_permission_errors;
    if total_permission_errors > 0 {
        let error_msg = removal_core::permission_error_message(
            total_permission_errors,
            cache_permission_errors,
            log_permission_errors,
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

        anyhow::bail!("{}", error_msg);
    }

    // Delete database records for this game (only if no permission errors)
    removal_core::write_progress(&progress_path, &reporter, "removing_database", "signalr.gameRemove.db.deleting", json!({}), 90.0, 0, 0)?;
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

    removal_core::write_progress(&progress_path, &reporter, "completed", "signalr.gameRemove.complete", json!({ "files": report.cache_files_deleted, "gb": report.total_bytes_freed as f64 / 1_073_741_824.0, "logEntries": report.log_entries_removed, "gameName": game_name, "gameAppId": game_app_id }), 100.0, 0, 0)?;

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Cache files deleted: {}", report.cache_files_deleted);
    eprintln!("Space freed: {:.2} MB", report.total_bytes_freed as f64 / 1_048_576.0);
    eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
    eprintln!("Log entries removed: {}", report.log_entries_removed);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
    }.await;
    progress_events::finish_or_exit(&reporter, "signalr.gameRemove.error.fatal", result);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn set(items: &[u32]) -> HashSet<u32> {
        items.iter().copied().collect()
    }

    #[test]
    fn keeps_depot_owned_only_by_target_excludes_shared() {
        // Game A owns D1 (exclusive) and shares D2 with game B.
        let valid = set(&[1, 2]); // A's depots: D1, D2
        let shared = set(&[2]); // D2 also belongs to another app/game
        let safe = compute_safe_depot_ids(&valid, &shared);
        assert!(safe.contains(&1), "exclusively-owned depot D1 must be purged");
        assert!(!safe.contains(&2), "shared depot D2 must be excluded from the log purge");
        assert_eq!(safe.len(), 1);
    }

    #[test]
    fn no_shared_depots_keeps_all() {
        let valid = set(&[10, 11, 12]);
        let shared = HashSet::new();
        let safe = compute_safe_depot_ids(&valid, &shared);
        assert_eq!(safe, valid, "with no shared depots, all owned depots are safe");
    }

    #[test]
    fn all_shared_excludes_everything() {
        let valid = set(&[5, 6]);
        let shared = set(&[5, 6, 99]); // superset is fine
        let safe = compute_safe_depot_ids(&valid, &shared);
        assert!(safe.is_empty(), "every owned depot also shared → nothing safe to purge");
    }

    #[test]
    fn empty_valid_is_empty_safe() {
        // Delisted-app shape: no SteamDepotMappings depots; URL/DB removal goes via Query 3.
        let valid: HashSet<u32> = HashSet::new();
        let shared = set(&[1, 2, 3]);
        let safe = compute_safe_depot_ids(&valid, &shared);
        assert!(safe.is_empty());
    }

    #[test]
    fn verification_skips_block_log_and_database_removal() {
        assert!(ensure_cache_deletions_verified(0).is_ok());

        let error = ensure_cache_deletions_verified(3).unwrap_err().to_string();
        assert!(error.contains("3 file(s)"));
        assert!(error.contains("access logs, and database records were left intact"));
    }
}
