//! Shared logic for the name-keyed removal bins (`cache_blizzard_remove`,
//! `cache_riot_remove`, `cache_xbox_remove`).
//!
//! A name-keyed game is a Download with NEITHER a Steam AppId NOR an Epic AppId,
//! identified by `(Service, GameName)`. The three bins differ ONLY in which service
//! string they pin, so the entire body lives here and each bin is a three-line
//! wrapper that calls [`run`] with its service.
//!
//! Cache-file deletion, the permission gate, and the final report (which names the
//! URLs whose access.log lines the host removes) are all delegated to
//! [`crate::removal_core`] (the tail shared with the Steam and Epic bins). This module
//! owns only the name-keyed HEAD: the DB queries that map `(service, game_name)` to
//! URLs and the DB-row delete.

use anyhow::{Context, Result};
use clap::Parser;
use serde_json::json;
use sqlx::PgPool;
use sqlx::Row;
use std::collections::HashMap;
use std::path::PathBuf;

use crate::cache_repair;
use crate::cache_utils;
use crate::db;
use crate::progress_events::ProgressReporter;
use crate::removal_core::{self, ProgressCadence, RemovalReport, RemovalStageKeys};

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

    /// Report how many cache files a removal would delete, then exit without deleting
    /// anything. The confirmation the user answers needs the number the removal will
    /// actually reach, not a detection scan's older snapshot.
    #[arg(long = "count-only")]
    count_only: bool,

    /// Canonical datasource whose history may be deleted by a standalone run.
    #[arg(long)]
    datasource: Option<String>,

    /// Keep database history for manager-coordinated multi-datasource removal.
    #[arg(long = "skip-db-delete")]
    skip_db_delete: bool,

    /// Operation that owns the durable cache-root receipt.
    #[arg(long = "operation-id")]
    operation_id: Option<String>,
}

/// Name-keyed services reuse the Steam removal stage keys (`signalr.gameRemove.*`)
/// for every stage EXCEPT starting, matching the pre-consolidation
/// `cache_named_game_remove` bin. Steam still owns `signalr.gameRemove.*` for
/// starting (it has a real gameAppId); named services (blizzard/riot/xbox)
/// have neither a Steam nor an Epic AppId, so they get the existing AppID-free
/// `signalr.namedRemove.*` starting key. This family already exists in
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

/// Context for the starting progress event. Carries `gameName` and `service`, and
/// deliberately never a `gameAppId` key — named games (blizzard/riot/xbox) don't have
/// one. Extracted as a pure fn so the shape is unit-testable without a live removal run.
fn starting_context(game_name: &str, service: &str) -> serde_json::Value {
    json!({ "gameName": game_name, "service": service })
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
/// The URL query: $1 = GameName, $2 = lowercased owning service. Gates identity on
/// `LOWER(d."Service") = $2` (the Download side) and returns each row's own `le."Service"`
/// (the cache-hash service) without constraining it. That last part is what lets an `xbox`
/// identity return its `wsus`-tagged URLs, and it is why this query alone is enough.
///
/// A second "fallback" query used to run after this one, selecting the same columns through
/// `le."DownloadId" IN (SELECT "Id" FROM "Downloads" WHERE <the same four predicates>)`. It could
/// never add a row: `Downloads."Id"` is the primary key, so the inner join above and that subquery
/// select exactly the same log rows, and the DISTINCT is over `le` columns either way. Every
/// Blizzard, Riot and Xbox removal paid for the table twice and merged the second result into the
/// first, changing nothing. Its comment defended it against constraining `le."Service"` - which
/// this query never did either.
const PRIMARY_URL_QUERY: &str = "SELECT DISTINCT le.\"Service\", le.\"Url\", le.\"BytesServed\"
         FROM \"LogEntries\" le
         INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
         WHERE d.\"GameName\" = $1
           AND d.\"GameAppId\" IS NULL
           AND d.\"EpicAppId\" IS NULL
           AND LOWER(d.\"Service\") = $2
           AND le.\"Url\" IS NOT NULL";

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
    datasource: &str,
) -> Result<(u64, u64)> {
    eprintln!(
        "Deleting database records for named game '{}/{}'...",
        service, game_name
    );

    let mut transaction = pool.begin().await?;
    sqlx::query("LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE")
        .execute(&mut *transaction)
        .await?;
    sqlx::query("LOCK TABLE \"LogEntries\" IN SHARE ROW EXCLUSIVE MODE")
        .execute(&mut *transaction)
        .await?;

    let mismatched_child: bool = sqlx::query_scalar(
        "SELECT EXISTS (
             SELECT 1 FROM \"LogEntries\" le
             INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
             WHERE d.\"GameName\" = $1 AND d.\"GameAppId\" IS NULL
               AND d.\"EpicAppId\" IS NULL AND LOWER(d.\"Service\") = $2
               AND d.\"Datasource\" = $3 AND le.\"Datasource\" <> $3
         )",
    )
    .bind(game_name)
    .bind(service)
    .bind(datasource)
    .fetch_one(&mut *transaction)
    .await?;
    if mismatched_child {
        anyhow::bail!(
            "selected named-game history has a differently attributed log entry for datasource '{}'",
            datasource
        )
    }

    let log_result = sqlx::query(
        "DELETE FROM \"LogEntries\" le WHERE le.\"Datasource\" = $3 AND le.\"DownloadId\" IN (
             SELECT \"Id\" FROM \"Downloads\"
             WHERE \"GameName\" = $1
               AND \"GameAppId\" IS NULL
               AND \"EpicAppId\" IS NULL
               AND LOWER(\"Service\") = $2
               AND \"Datasource\" = $3
         )",
    )
    .bind(game_name)
    .bind(service)
    .bind(datasource)
    .execute(&mut *transaction)
    .await?;
    let log_entries_deleted = log_result.rows_affected();
    eprintln!("  Deleted {} log entry records", log_entries_deleted);

    // Now safe to delete the downloads
    let downloads_result = sqlx::query(
        "DELETE FROM \"Downloads\"
         WHERE \"GameName\" = $1
           AND \"GameAppId\" IS NULL
           AND \"EpicAppId\" IS NULL
           AND LOWER(\"Service\") = $2
           AND \"Datasource\" = $3",
    )
    .bind(game_name)
    .bind(service)
    .bind(datasource)
    .execute(&mut *transaction)
    .await?;
    let downloads_deleted = downloads_result.rows_affected();
    eprintln!("  Deleted {} download records", downloads_deleted);

    transaction.commit().await?;
    Ok((log_entries_deleted, downloads_deleted))
}

async fn validate_named_game_selection(
    pool: &PgPool,
    service: &str,
    game_name: &str,
    datasource: &str,
) -> Result<()> {
    let mismatched_child: bool = sqlx::query_scalar(
        "SELECT EXISTS (
             SELECT 1 FROM \"LogEntries\" le
             INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
             WHERE d.\"GameName\" = $1
               AND LOWER(d.\"Service\") = $2
               AND d.\"GameAppId\" IS NULL
               AND d.\"EpicAppId\" IS NULL
               AND d.\"Datasource\" = $3
               AND le.\"Datasource\" <> $3
         )",
    )
    .bind(game_name)
    .bind(service)
    .bind(datasource)
    .fetch_one(pool)
    .await?;
    if mismatched_child {
        anyhow::bail!(
            "selected named-game history has a differently attributed log entry for datasource '{}'",
            datasource
        )
    }
    Ok(())
}

/// Entry point for the name-keyed removal bins. `service` is the lowercased owning
/// service ("blizzard", "riot", "xbox") pinned by the wrapper bin.
///
/// The orchestration skeleton (arg parse → starting → empty-url early return →
/// cache delete → cancel cleanup → dir cleanup → permission gate → DB delete →
/// final report) follows the prior `cache_named_game_remove` `main`, with the
/// cache delete / permission message delegated to `removal_core`. The host removes
/// the report's `purge_urls` from access.log in its own locked step.
pub async fn run(service: &str) -> Result<()> {
    let args = Args::parse();
    cache_utils::set_active_key_scheme(cache_utils::CacheKeyScheme::from_config_str(
        &args.key_scheme,
    ));

    let log_dir = PathBuf::from(&args.log_dir);
    let cache_dir = PathBuf::from(&args.cache_dir);
    // The service is pinned by the wrapper; normalize to lowercase so the DB gate
    // (LOWER(Service) = $2) matches.
    let service = normalize_service(service);
    let game_name = &args.game_name;
    let output_json = PathBuf::from(&args.output_json);
    let progress_path = PathBuf::from(&args.progress_json);
    let reporter = ProgressReporter::new(args.progress);

    if !args.count_only && !args.skip_db_delete && args.datasource.is_none() {
        anyhow::bail!("--datasource is required when database deletion is enabled")
    }

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

        // A count run must not announce itself as a removal: the whole point of the number is that
        // the user can trust what the confirmation says.
        let starting_stage_key = if args.count_only {
            "signalr.gameRemove.counting.starting"
        } else {
            NAMED_GAME_REMOVE_STARTING_KEY
        };
        removal_core::write_progress(
            &progress_path,
            &reporter,
            "starting",
            starting_stage_key,
            starting_context(game_name, &service),
            0.0,
            0,
            0,
        )?;

        // Query database for URLs
        removal_core::write_progress(
            &progress_path,
            &reporter,
            "querying_database",
            "signalr.gameRemove.db.querying",
            json!({}),
            5.0,
            0,
            0,
        )?;
        let url_data = get_named_game_urls_from_db(&pool, &service, game_name).await?;

        // A count run stops here. It walks the same list a removal would walk, reports how many of
        // those files exist on disk, and returns before the cache sweep and the database delete
        // below are reachable. A game with no URLs reports zero rather than taking the no-URL
        // exit, so the confirmation always has a number.
        if args.count_only {
            let collection_progress = removal_core::CollectionProgress {
                progress_path: &progress_path,
                reporter: &reporter,
                stage_key: "signalr.gameRemove.counting.progress",
            };
            let cache_files_found = removal_core::count_cache_files(
                &cache_dir,
                &url_data,
                &output_json,
                game_name,
                cache_utils::active_key_scheme(),
                removal_core::SliceReach::SweepKeyHeaders,
                &collection_progress,
            )?;
            removal_core::write_progress(
                &progress_path,
                &reporter,
                "completed",
                "signalr.gameRemove.counting.complete",
                json!({ "files": cache_files_found, "gameName": game_name }),
                100.0,
                cache_files_found,
                cache_files_found,
            )?;
            return Ok(());
        }

        if url_data.is_empty() {
            eprintln!("No URLs found for named game '{}/{}'", service, game_name);

            RemovalReport::from_tail(game_name, &removal_core::RemovalTail::default())
                .write(&output_json)?;

            removal_core::write_progress(
                &progress_path,
                &reporter,
                "completed",
                "signalr.gameRemove.noUrls",
                json!({}),
                100.0,
                0,
                0,
            )?;
            return Ok(());
        }

        eprintln!(
            "Found {} unique URLs for '{}/{}'",
            url_data.len(),
            service,
            game_name
        );

        if !args.skip_db_delete {
            validate_named_game_selection(
                &pool,
                &service,
                game_name,
                args.datasource
                    .as_deref()
                    .context("datasource missing after validation")?,
            )
            .await?;
        }

        // Steps 1-3 (cache delete, dir cleanup, verification gate, permission gate) are the
        // URL-scoped sequence shared with the Epic bin.
        let lifecycle = removal_core::RemovalLifecycleKeys {
            cache_removing: "signalr.gameRemove.cache.removing",
            dirs_cleaning: "signalr.gameRemove.dirs.cleaning",
            db_deleting: "signalr.gameRemove.db.deleting",
        };
        let write_failure_report = |tail: &removal_core::RemovalTail| -> Result<()> {
            RemovalReport::from_tail(game_name, tail).write(&output_json)
        };
        cache_repair::prepare_receipt(&cache_dir, args.operation_id.as_deref())?;
        let Some(tail) = removal_core::run_url_removal_steps(
            &cache_dir,
            &url_data,
            &progress_path,
            &reporter,
            &NAMED_STAGE_KEYS,
            &lifecycle,
            ProgressCadence::OnPercentAdvance,
            // Blizzard TACT archives, Riot bundles and Xbox payloads are range-served, so a slice
            // can sit behind an eviction hole the forward walk cannot cross.
            removal_core::SliceReach::SweepKeyHeaders,
            &write_failure_report,
        )?
        else {
            // Cancellation confirmed during the cache sweep - partial dirs cleaned, exit 0.
            return Ok(());
        };

        if !args.skip_db_delete {
            delete_named_game_from_database(
                &pool,
                &service,
                game_name,
                args.datasource
                    .as_deref()
                    .context("datasource missing after validation")?,
            )
            .await?;
        }

        // Write final report
        let report = RemovalReport::from_tail(game_name, &tail);
        report.write(&output_json)?;

        // The host's locked log step finishes the removal and owns the counted completion.
        removal_core::write_progress(
            &progress_path,
            &reporter,
            "completed",
            "signalr.gameRemove.finalizing",
            json!({}),
            100.0,
            0,
            0,
        )?;

        eprintln!("\n=== Removal Summary ===");
        eprintln!("Cache files deleted: {}", report.cache_files_deleted);
        eprintln!(
            "Space freed: {:.2} MB",
            report.total_bytes_freed as f64 / 1_048_576.0
        );
        eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
        eprintln!("Report saved to: {}", output_json.display());

        Ok(())
    }
    .await;
    crate::progress_events::finish_or_exit(&reporter, "signalr.gameRemove.error.fatal", result);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn operation_id_argument_is_optional() {
        let base = [
            "cache_named_remove",
            "/logs",
            "/cache",
            "Diablo IV",
            "report.json",
            "progress.json",
        ];
        assert!(Args::try_parse_from(base).unwrap().operation_id.is_none());
        let mut supplied = base.to_vec();
        supplied.extend(["--operation-id", "123e4567-e89b-12d3-a456-426614174000"]);
        assert_eq!(
            Args::try_parse_from(supplied)
                .unwrap()
                .operation_id
                .as_deref(),
            Some("123e4567-e89b-12d3-a456-426614174000")
        );
    }

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

    /// Load-bearing, and it outlived the second query it was written for: constraining
    /// `le.Service` anywhere in this path makes Xbox removal (le.Service='wsus', identity 'xbox')
    /// return zero rows and leave cache and log behind. The query scopes on the Download side and
    /// must keep doing so.
    #[test]
    fn url_query_does_not_constrain_log_entry_service() {
        // No predicate of the shape `LOWER(le."Service") = ...` anywhere in it.
        assert!(
            !PRIMARY_URL_QUERY.contains("LOWER(le.\"Service\")"),
            "the URL query must not constrain le.Service (breaks the Xbox wsus cache-service split)"
        );
        // Identity is gated on the Download side instead.
        assert!(PRIMARY_URL_QUERY.contains("LOWER(d.\"Service\") = $2"));
    }

    /// The named-removal starting stage key must be the existing AppID-free
    /// `namedRemove.*` family (already used by GamesController's REST-side Started/Complete
    /// events and already present in en.json/zh.json), not the Steam
    /// `signalr.gameRemove.starting` key (which the i18n template hardcodes
    /// `(AppID {{gameAppId}})` into — the placeholder bug this fix removes).
    #[test]
    fn named_stage_keys_are_the_appid_free_family() {
        assert_eq!(
            NAMED_GAME_REMOVE_STARTING_KEY,
            "signalr.namedRemove.starting"
        );
    }

    /// `starting_context` must carry `gameName` and must NEVER carry `gameAppId` — named
    /// games (blizzard/riot/xbox) have neither a Steam nor an Epic AppId.
    #[test]
    fn starting_context_has_game_name_and_no_game_app_id() {
        let ctx = starting_context("Diablo IV", "blizzard");
        assert_eq!(
            ctx.get("gameName").and_then(|v| v.as_str()),
            Some("Diablo IV")
        );
        assert_eq!(
            ctx.get("service").and_then(|v| v.as_str()),
            Some("blizzard")
        );
        assert!(
            ctx.get("gameAppId").is_none(),
            "named context must not carry gameAppId"
        );
    }
}
