//! Shared logic for the name-keyed removal bins (`cache_blizzard_remove`,
//! `cache_riot_remove`, `cache_xbox_remove`).
//!
//! A name-keyed game is a Download with NEITHER a Steam AppId NOR an Epic AppId,
//! identified by `(Service, GameName)`. The three bins differ ONLY in which service
//! string they pin, so the entire body lives here and each bin is a three-line
//! wrapper that calls [`run`] with its service.
//!
//! Cache-file deletion, the access.log purge, the permission gate, and the final
//! report are all delegated to [`crate::removal_core`] (the tail shared with the
//! Steam and Epic bins). This module owns only the name-keyed HEAD: the DB queries
//! that map `(service, game_name)` to URLs and the DB-row delete.

use anyhow::Result;
use clap::Parser;
use serde::Serialize;
use serde_json::json;
use sqlx::PgPool;
use sqlx::Row;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::PathBuf;

use crate::db;
use crate::cache_utils;
use crate::progress_events::ProgressReporter;
use crate::removal_core::{self, LogScope, ProgressCadence, RemovalStageKeys};

/// Positional args for a name-keyed removal bin. The owning service is pinned by the
/// wrapper (it is NOT a positional arg), so the contract matches the Epic bin:
/// `log_dir cache_dir game_name output_json progress_json`.
#[derive(clap::Parser, Debug)]
struct Args {
    /// Directory containing log files
    log_dir: String,

    /// Cache directory root (e.g., /cache or H:/cache)
    cache_dir: String,

    /// Game name to remove (e.g., "Diablo IV")
    game_name: String,

    /// Path to output JSON report
    output_json: String,

    /// Path to progress JSON file
    progress_json: String,

    /// Cache-key recipe of the target datasource: "monolithic" (default) | "bare_metal"
    #[arg(long = "key-scheme", default_value = "monolithic")]
    key_scheme: String,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,
}

#[derive(Debug, Serialize)]
struct RemovalReport {
    game_name: String,
    cache_files_deleted: usize,
    total_bytes_freed: u64,
    empty_dirs_removed: usize,
    log_entries_removed: u64,
}

/// Name-keyed services reuse the Steam removal stage keys (`signalr.gameRemove.*`)
/// for every stage EXCEPT starting/complete, matching the pre-consolidation
/// `cache_named_game_remove` bin. Steam still owns `signalr.gameRemove.*` for
/// starting/complete (it has a real gameAppId); named services (blizzard/riot/xbox)
/// have neither a Steam nor an Epic AppId, so they get the existing AppID-free
/// `signalr.namedRemove.*` starting/complete keys. This family already exists in
/// en.json/zh.json (mirrors `signalr.epicRemove.*` text) and is already computed by
/// `GamesController.StartRemovalAsync`'s `isNamed` branch for the Started/Complete
/// SignalR events — reused here (not a new `namedGameRemove.*` family) so the
/// mid-operation progress ticks Rust emits agree with the REST-side keys instead of
/// introducing a second, parallel AppID-free key family for the same purpose.
const NAMED_STAGE_KEYS: RemovalStageKeys = RemovalStageKeys {
    cache_file_progress: "signalr.gameRemove.cache.file.progress",
};

/// Stage key for the starting progress event. AppID-free — named games have no
/// Steam/Epic AppId, so the template must not reference `{{gameAppId}}`.
const NAMED_GAME_REMOVE_STARTING_KEY: &str = "signalr.namedRemove.starting";
/// Stage key for the completed progress event. Same AppID-free rationale.
const NAMED_GAME_REMOVE_COMPLETE_KEY: &str = "signalr.namedRemove.complete";

/// Context for the starting progress event. Carries `gameName` and `service`, and
/// deliberately never a `gameAppId` key — named games (blizzard/riot/xbox) don't have
/// one. Extracted as a pure fn so the shape is unit-testable without a live removal run.
fn starting_context(game_name: &str, service: &str) -> serde_json::Value {
    json!({ "gameName": game_name, "service": service })
}

/// Context for the completed progress event. Same no-`gameAppId` contract as
/// [`starting_context`].
fn complete_context(
    game_name: &str,
    service: &str,
    files: usize,
    gb: f64,
    log_entries: u64,
) -> serde_json::Value {
    json!({
        "files": files,
        "gb": gb,
        "logEntries": log_entries,
        "gameName": game_name,
        "service": service,
    })
}

/// Normalize the wrapper-pinned service to the form the DB gate expects. The DB
/// predicate uses `LOWER(d.Service) = $2`, so the bound service must be lowercase;
/// extracted as a pure fn so the wrapper → core service contract is unit-testable
/// without a live database.
fn normalize_service(service: &str) -> String {
    service.to_lowercase()
}

/// Stop the logical removal when a bare-metal candidate could not prove its
/// recipe-computed key. Keeping the access-log and database rows makes the skipped
/// object discoverable for a corrected retry; deleting those rows would orphan it.
fn ensure_cache_deletions_verified(verification_skips: usize) -> Result<()> {
    if verification_skips == 0 {
        return Ok(());
    }

    anyhow::bail!(
        "Cache deletion safety verification failed for {} file(s); skipped files, access logs, and database records were left intact",
        verification_skips
    )
}

/// Primary URL query: $1 = GameName, $2 = lowercased owning service. Gates identity on
/// `LOWER(d."Service") = $2` (the Download side) and returns each row's own `le."Service"`
/// (the cache-hash service) without constraining it.
const PRIMARY_URL_QUERY: &str =
    "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
         WHERE d.\"GameName\" = $1
           AND d.\"GameAppId\" IS NULL
           AND d.\"EpicAppId\" IS NULL
           AND LOWER(d.\"Service\") = $2
           AND le.\"Url\" IS NOT NULL";

/// Fallback URL query: $1 = GameName, $2 = lowercased owning service. The owning service is
/// scoped via the Downloads subquery (`LOWER("Service") = $2`), NOT via `le.Service`.
/// This is load-bearing for the cache-service split: an Xbox game has Downloads.Service='xbox'
/// but its LogEntries are tagged le.Service='wsus' (the cache-hash service). Filtering the
/// fallback on `LOWER(le.Service) = 'xbox'` would return ZERO rows for Xbox. Constraining only
/// the Download side keeps the fallback correct for Xbox while staying a no-op-superset for
/// Blizzard/Riot/Epic (where le.Service == d.Service anyway). The DownloadId-IN subquery is what
/// ties each log row to the right game; le.Service must NOT be constrained.
const FALLBACK_URL_QUERY: &str =
    "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         WHERE le.\"Url\" IS NOT NULL
           AND le.\"DownloadId\" IN (
               SELECT \"Id\" FROM \"Downloads\"
               WHERE \"GameName\" = $1
                 AND \"GameAppId\" IS NULL
                 AND \"EpicAppId\" IS NULL
                 AND LOWER(\"Service\") = $2
           )";

/// Query the database for all URLs associated with a name-keyed game.
/// Joins LogEntries with Downloads via DownloadId, scoped to the (Service, GameName)
/// pair where the Download has neither a Steam AppId nor an Epic AppId.
/// Returns: HashMap<URL, (service_lowercase, max_bytes_served)>
async fn get_named_game_urls_from_db(
    pool: &PgPool,
    service: &str,
    game_name: &str,
) -> Result<HashMap<String, (String, i64)>> {
    eprintln!("Querying database for named game URLs...");

    // Primary join: URLs whose Download is the named game.
    let rows = sqlx::query(PRIMARY_URL_QUERY)
    .bind(game_name)
    .bind(service)
    .fetch_all(pool)
    .await?;

    let mut url_data: HashMap<String, (String, i64)> = HashMap::new();

    for row in rows {
        let row_service: String = row.get("Service");
        let url: String = row.get("Url");
        let bytes_served: i64 = row.get("BytesServed");
        let service_lower = row_service.to_lowercase();

        let entry = url_data
            .entry(url)
            .or_insert_with(|| (service_lower.clone(), 0));

        // Track max bytes for chunk calculation
        entry.1 = entry.1.max(bytes_served);
    }

    // Fallback: any LogEntries row pointing at one of this game's Download rows. Scoped on the
    // Download side ONLY — see `FALLBACK_URL_QUERY` for why `le.Service` must NOT be constrained
    // (load-bearing for the Xbox wsus cache-service split).
    let fallback_rows = sqlx::query(FALLBACK_URL_QUERY)
    .bind(game_name)
    .bind(service)
    .fetch_all(pool)
    .await?;

    for row in fallback_rows {
        let row_service: String = row.get("Service");
        let url: String = row.get("Url");
        let bytes_served: i64 = row.get("BytesServed");
        let service_lower = row_service.to_lowercase();

        let entry = url_data
            .entry(url)
            .or_insert_with(|| (service_lower.clone(), 0));

        entry.1 = entry.1.max(bytes_served);
    }

    eprintln!(
        "  Found {} unique URLs for named game '{}/{}'",
        url_data.len(),
        service,
        game_name
    );
    Ok(url_data)
}

/// Delete database records for the named game (LogEntries + Downloads), scoped to
/// (Service, GameName) with NULL Steam/Epic ids.
async fn delete_named_game_from_database(
    pool: &PgPool,
    service: &str,
    game_name: &str,
) -> Result<(u64, u64)> {
    eprintln!("Deleting database records for named game '{}/{}'...", service, game_name);

    // First, delete LogEntries that reference these downloads (foreign key constraint)
    let log_result = sqlx::query(
        "DELETE FROM \"LogEntries\" WHERE \"DownloadId\" IN (
             SELECT \"Id\" FROM \"Downloads\"
             WHERE \"GameName\" = $1
               AND \"GameAppId\" IS NULL
               AND \"EpicAppId\" IS NULL
               AND LOWER(\"Service\") = $2
         )"
    )
    .bind(game_name)
    .bind(service)
    .execute(pool)
    .await?;
    let log_entries_deleted = log_result.rows_affected();
    eprintln!("  Deleted {} log entry records", log_entries_deleted);

    // Now safe to delete the downloads
    let downloads_result = sqlx::query(
        "DELETE FROM \"Downloads\"
         WHERE \"GameName\" = $1
           AND \"GameAppId\" IS NULL
           AND \"EpicAppId\" IS NULL
           AND LOWER(\"Service\") = $2"
    )
    .bind(game_name)
    .bind(service)
    .execute(pool)
    .await?;
    let downloads_deleted = downloads_result.rows_affected();
    eprintln!("  Deleted {} download records", downloads_deleted);

    Ok((log_entries_deleted, downloads_deleted))
}

/// Entry point for the name-keyed removal bins. `service` is the lowercased owning
/// service ("blizzard", "riot", "xbox") pinned by the wrapper bin.
///
/// The orchestration skeleton (arg parse → starting → empty-url early return →
/// cache delete → cancel cleanup → dir cleanup → log purge → permission gate →
/// DB delete → final report) is identical to the prior `cache_named_game_remove`
/// `main`, with the cache delete / log purge / permission message delegated to
/// `removal_core`.
pub async fn run(service: &str) -> Result<()> {
    let args = Args::parse();
    cache_utils::set_active_key_scheme(cache_utils::CacheKeyScheme::from_config_str(&args.key_scheme));

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    // The service is pinned by the wrapper; normalize to lowercase so the DB gate
    // (LOWER(Service) = $2) matches.
    let service = normalize_service(service);
    let game_name = &args.game_name;
    let output_json = PathBuf::from(&args.output_json);
    let progress_path = PathBuf::from(&args.progress_json);
    let reporter = ProgressReporter::new(args.progress);

    // Whole removal routed through the single failure funnel: the wrapper bins
    // (cache_blizzard_remove / cache_riot_remove / cache_xbox_remove) just `await` this
    // function as their whole `main` body, so finish_or_exit here is what turns a
    // `?`-propagated failure into the structured stdout `failed` event for all three -
    // not only the one hand-checked permission-error abort below.
    let result: Result<()> = async {
    eprintln!("Named Game Cache Removal");
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Service: {}", service);
    eprintln!("  Game name: {}", game_name);

    // Per-game Riot removal works the same on bare-metal as on monolithic: the game -> URL
    // mapping is materialized in the database at ingest (the CDN host names the game, the
    // rows record its URLs), so removal targets that game's URLs by joining GameName -> URLs
    // and computing the per-URL cache key. The cache key need not encode the host. Bundles
    // shared byte-for-byte across Riot titles are a rare content-dedup case that affects
    // monolithic identically; the bare-metal KEY-header verification only ever prevents a
    // wrong delete, never causes one.
    if !log_dir.exists() {
        let msg = format!("Log directory not found: {}", log_dir.display());
        anyhow::bail!("{}", msg);
    }

    if !cache_dir.exists() {
        let msg = format!("Cache directory not found: {}", cache_dir.display());
        anyhow::bail!("{}", msg);
    }

    let pool = db::create_pool().await?;

    removal_core::write_progress(&progress_path, &reporter, "starting", NAMED_GAME_REMOVE_STARTING_KEY, starting_context(game_name, &service), 0.0, 0, 0)?;

    // Query database for URLs
    removal_core::write_progress(&progress_path, &reporter, "querying_database", "signalr.gameRemove.db.querying", json!({}), 5.0, 0, 0)?;
    let url_data = get_named_game_urls_from_db(&pool, &service, game_name).await?;

    if url_data.is_empty() {
        eprintln!("No URLs found for named game '{}/{}'", service, game_name);

        let report = RemovalReport {
            game_name: game_name.to_string(),
            cache_files_deleted: 0,
            total_bytes_freed: 0,
            empty_dirs_removed: 0,
            log_entries_removed: 0,
        };

        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;

        removal_core::write_progress(&progress_path, &reporter, "completed", "signalr.gameRemove.noUrls", json!({}), 100.0, 0, 0)?;
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}/{}'", url_data.len(), service, game_name);

    // Step 1: Remove cache files
    let url_count = url_data.len();
    removal_core::write_progress(&progress_path, &reporter, "removing_cache", "signalr.gameRemove.cache.removing", json!({ "count": url_count }), 10.0, 0, 0)?;
    eprintln!("\nRemoving cache files...");
    let outcome = removal_core::remove_cache_files(
        &cache_dir,
        &url_data,
        &progress_path,
        &reporter,
        &NAMED_STAGE_KEYS,
        ProgressCadence::OnPercentAdvance,
        cache_utils::active_key_scheme(),
    )?;

    // If cancellation arrived during cache removal, do directory cleanup and exit 0.
    if crate::cancel::is_cancelled() {
        eprintln!("Cancellation confirmed — cleaning up partial directories and exiting.");
        cache_utils::cleanup_empty_directories(&cache_dir, outcome.parent_dirs);
        return Ok(());
    }

    // Step 2: Clean up empty directories
    removal_core::write_progress(&progress_path, &reporter, "cleaning_directories", "signalr.gameRemove.dirs.cleaning", json!({}), 70.0, 0, 0)?;
    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cache_utils::cleanup_empty_directories(&cache_dir, outcome.parent_dirs);

    // A failed bare-metal KEY check is not a successful removal. The cache helper
    // correctly left the candidate untouched; preserve its URL provenance as well
    // so a corrected retry can still find it instead of turning it into an orphan.
    if let Err(error) = ensure_cache_deletions_verified(outcome.verification_skips) {
        let report = RemovalReport {
            game_name: game_name.to_string(),
            cache_files_deleted: outcome.deleted_files,
            total_bytes_freed: outcome.bytes_freed,
            empty_dirs_removed,
            log_entries_removed: 0,
        };
        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;
        return Err(error);
    }

    // Step 3: Remove log entries from access log text files
    removal_core::write_progress(&progress_path, &reporter, "removing_logs", "signalr.gameRemove.logs.removing", json!({}), 80.0, 0, 0)?;
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let (log_entries_removed, log_permission_errors) =
        removal_core::purge_log_entries(&log_dir, &urls_to_remove, &LogScope::Urls)?;

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
        };
        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;

        anyhow::bail!("{}", error_msg);
    }

    // Step 5: Delete database records
    removal_core::write_progress(&progress_path, &reporter, "removing_database", "signalr.gameRemove.db.deleting", json!({}), 90.0, 0, 0)?;
    eprintln!("\nRemoving database records...");
    let (_log_records, _download_records) = delete_named_game_from_database(&pool, &service, game_name).await?;

    // Write final report
    let report = RemovalReport {
        game_name: game_name.clone(),
        cache_files_deleted: outcome.deleted_files,
        total_bytes_freed: outcome.bytes_freed,
        empty_dirs_removed,
        log_entries_removed,
    };

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    removal_core::write_progress(
        &progress_path,
        &reporter,
        "completed",
        NAMED_GAME_REMOVE_COMPLETE_KEY,
        complete_context(
            game_name,
            &service,
            report.cache_files_deleted,
            report.total_bytes_freed as f64 / 1_073_741_824.0,
            report.log_entries_removed,
        ),
        100.0,
        0,
        0,
    )?;

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Cache files deleted: {}", report.cache_files_deleted);
    eprintln!("Space freed: {:.2} MB", report.total_bytes_freed as f64 / 1_048_576.0);
    eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
    eprintln!("Log entries removed: {}", report.log_entries_removed);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
    }.await;
    crate::progress_events::finish_or_exit(&reporter, "signalr.gameRemove.error.fatal", result);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The three wrapper bins pin their service as an already-lowercase literal; the core
    /// lowercases again defensively. Both the wrapper literals and the normalizer must agree
    /// so the DB gate `LOWER(d.Service) = $2` matches the stored identity.
    #[test]
    fn normalize_service_lowercases_and_is_idempotent_for_pinned_services() {
        assert_eq!(normalize_service("blizzard"), "blizzard");
        assert_eq!(normalize_service("riot"), "riot");
        assert_eq!(normalize_service("xbox"), "xbox");
        // Defensive: any casing collapses to the gate form.
        assert_eq!(normalize_service("Blizzard"), "blizzard");
        assert_eq!(normalize_service("XBOX"), "xbox");
    }

    /// A bare-metal KEY mismatch/unreadable header must stop the logical tail of
    /// removal, or the still-present file loses the DB/log provenance needed to
    /// discover it on a corrected retry.
    #[test]
    fn verification_skips_block_log_and_database_removal() {
        assert!(ensure_cache_deletions_verified(0).is_ok());

        let error = ensure_cache_deletions_verified(2).unwrap_err().to_string();
        assert!(error.contains("2 file(s)"));
        assert!(error.contains("access logs, and database records were left intact"));
    }

    /// Primary query gates identity on the Download side (`LOWER(d."Service") = $2`) and selects
    /// each log row's own `le."Service"` (the cache-hash service) without constraining it — this
    /// is what lets an `xbox` identity return its `wsus`-tagged URLs.
    #[test]
    fn primary_query_gates_identity_on_download_service() {
        assert!(PRIMARY_URL_QUERY.contains("LOWER(d.\"Service\") = $2"));
        assert!(PRIMARY_URL_QUERY.contains("d.\"GameName\" = $1"));
        assert!(PRIMARY_URL_QUERY.contains("d.\"GameAppId\" IS NULL"));
        assert!(PRIMARY_URL_QUERY.contains("d.\"EpicAppId\" IS NULL"));
    }

    /// Load-bearing: the fallback scopes via the Downloads subquery and must NEVER constrain
    /// `le.Service`, or Xbox removal (le.Service='wsus', identity 'xbox') returns zero rows and
    /// leaves cache/log behind. Guards the G3 fix against regression.
    #[test]
    fn fallback_query_does_not_constrain_log_entry_service() {
        // No predicate of the shape `LOWER(le."Service") = ...` anywhere in the fallback.
        assert!(
            !FALLBACK_URL_QUERY.contains("LOWER(le.\"Service\")"),
            "fallback must not constrain le.Service (breaks the Xbox wsus cache-service split)"
        );
        // It DOES scope on the Download side via the DownloadId-IN subquery.
        assert!(FALLBACK_URL_QUERY.contains("le.\"DownloadId\" IN ("));
        assert!(FALLBACK_URL_QUERY.contains("LOWER(\"Service\") = $2"));
    }

    /// The named-removal starting/complete stage keys must be the existing AppID-free
    /// `namedRemove.*` family (already used by GamesController's REST-side Started/Complete
    /// events and already present in en.json/zh.json), not the Steam
    /// `signalr.gameRemove.starting/.complete` keys (which the i18n templates hardcode
    /// `(AppID {{gameAppId}})` into — the placeholder bug this fix removes).
    #[test]
    fn named_stage_keys_are_the_appid_free_family() {
        assert_eq!(NAMED_GAME_REMOVE_STARTING_KEY, "signalr.namedRemove.starting");
        assert_eq!(NAMED_GAME_REMOVE_COMPLETE_KEY, "signalr.namedRemove.complete");
    }

    /// `starting_context` must carry `gameName` and must NEVER carry `gameAppId` — named
    /// games (blizzard/riot/xbox) have neither a Steam nor an Epic AppId.
    #[test]
    fn starting_context_has_game_name_and_no_game_app_id() {
        let ctx = starting_context("Diablo IV", "blizzard");
        assert_eq!(ctx.get("gameName").and_then(|v| v.as_str()), Some("Diablo IV"));
        assert_eq!(ctx.get("service").and_then(|v| v.as_str()), Some("blizzard"));
        assert!(ctx.get("gameAppId").is_none(), "named context must not carry gameAppId");
    }

    /// `complete_context` carries the same no-`gameAppId` contract plus the removal totals.
    #[test]
    fn complete_context_has_game_name_and_no_game_app_id() {
        let ctx = complete_context("Halo Infinite", "xbox", 11, 0.0094, 42);
        assert_eq!(ctx.get("gameName").and_then(|v| v.as_str()), Some("Halo Infinite"));
        assert_eq!(ctx.get("service").and_then(|v| v.as_str()), Some("xbox"));
        assert_eq!(ctx.get("files").and_then(|v| v.as_u64()), Some(11));
        assert_eq!(ctx.get("logEntries").and_then(|v| v.as_u64()), Some(42));
        assert!(ctx.get("gameAppId").is_none(), "named context must not carry gameAppId");
    }
}
