use anyhow::{Context, Result};
use clap::Parser;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use serde_json::json;
use sqlx::{PgPool, Postgres, Row, Transaction};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

mod cache_eviction_paths;
use lancache_processor::cache_repair;
use lancache_processor::cancel;
use lancache_processor::db;
use lancache_processor::progress_events;
use lancache_processor::progress_utils;
use progress_events::ProgressReporter;

/// Eviction scanner - checks which downloads have been evicted from the nginx cache
#[derive(Parser, Debug)]
#[command(name = "cache_eviction_scan")]
#[command(about = "Scans cache files and marks evicted downloads in the database")]
struct Args {
    /// Path to JSON file containing datasource configuration
    datasource_config: String,

    /// Path to progress JSON file (use "none" to skip)
    #[arg(default_value = "none")]
    progress_json: Option<String>,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,

    /// Durable checkpoint row that receives committed page counters.
    #[arg(long = "operation-id")]
    operation_id: Option<String>,

    /// Immutable repair scope for interruption recovery.
    #[arg(long, requires = "operation_id")]
    repair: Option<String>,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
struct DatasourceConfig {
    name: String,
    cache_path: String,
    is_default: bool,
    /// Cache-key recipe for this datasource: "monolithic" (default) | "bare_metal".
    #[serde(default)]
    key_scheme: String,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RepairDocument {
    version: u32,
    operation_id: String,
    sources: Vec<RepairSource>,
    #[serde(default)]
    target: Option<RepairTarget>,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RepairSource {
    name: String,
    cache_path: String,
    key_scheme: String,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RepairTarget {
    #[serde(default)]
    steam_app_id: Option<u32>,
    #[serde(default)]
    steam_depot_ids: Vec<u32>,
    #[serde(default)]
    epic_game: Option<String>,
    #[serde(default)]
    game_name: Option<String>,
    #[serde(default)]
    service: Option<String>,
}

#[derive(Debug, PartialEq, Eq)]
enum TargetSelector {
    Bulk,
    Steam { app_id: u32, depot_ids: Vec<u32> },
    Epic { game: String },
    Named { game: String, service: String },
    Service { service: String },
}

impl TargetSelector {
    fn from_target(target: Option<RepairTarget>) -> Result<Self> {
        let Some(target) = target else {
            return Ok(Self::Bulk);
        };
        let epic = nonempty(target.epic_game);
        let game = nonempty(target.game_name);
        let service = nonempty(target.service);

        match (target.steam_app_id, epic, game, service) {
            (Some(app_id), None, None, None) => Ok(Self::Steam {
                app_id,
                depot_ids: target.steam_depot_ids,
            }),
            (None, Some(game), None, None) if target.steam_depot_ids.is_empty() => {
                Ok(Self::Epic { game })
            }
            (None, None, Some(game), Some(service)) if target.steam_depot_ids.is_empty() => {
                Ok(Self::Named { game, service })
            }
            (None, None, None, Some(service)) if target.steam_depot_ids.is_empty() => {
                Ok(Self::Service { service })
            }
            _ => anyhow::bail!("repair target has an empty, mixed, or depot-only selector"),
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn matches(
        &self,
        game_app_id: Option<i64>,
        depot_id: Option<i64>,
        epic_app_id: Option<&str>,
        game_name: Option<&str>,
        service: Option<&str>,
    ) -> bool {
        match self {
            Self::Bulk => true,
            Self::Steam { app_id, depot_ids } => {
                game_app_id == Some(i64::from(*app_id))
                    || depot_id.is_some_and(|value| {
                        u32::try_from(value)
                            .ok()
                            .is_some_and(|value| depot_ids.contains(&value))
                    })
            }
            Self::Epic { game } => epic_app_id.is_some() && game_name == Some(game.as_str()),
            Self::Named {
                game,
                service: target_service,
            } => {
                game_app_id.is_none()
                    && epic_app_id.is_none()
                    && game_name == Some(game.as_str())
                    && service.is_some_and(|value| value.eq_ignore_ascii_case(target_service))
            }
            Self::Service {
                service: target_service,
            } => service.is_some_and(|value| value.eq_ignore_ascii_case(target_service)),
        }
    }
}

fn nonempty(value: Option<String>) -> Option<String> {
    value.filter(|value| !value.trim().is_empty())
}

fn read_repair(path: &Path) -> Result<(uuid::Uuid, Vec<RepairSource>, TargetSelector)> {
    let bytes = std::fs::read(path)
        .with_context(|| format!("Failed to read repair document: {}", path.display()))?;
    let repair: RepairDocument = serde_json::from_slice(&bytes)?;
    if repair.version != 1 {
        anyhow::bail!("unsupported repair document version {}", repair.version);
    }
    let original_id = cache_repair::operation_uuid(&repair.operation_id)?;
    if repair.sources.is_empty() {
        anyhow::bail!("repair document has no launched sources");
    }
    let mut source_names = std::collections::HashSet::new();
    for source in &repair.sources {
        if source.name.trim().is_empty()
            || source.cache_path.trim().is_empty()
            || source.key_scheme.trim().is_empty()
        {
            anyhow::bail!("repair document contains an incomplete source");
        }
        if !source_names.insert(source.name.to_lowercase()) {
            anyhow::bail!("repair document repeats datasource '{}'", source.name);
        }
    }
    let target = TargetSelector::from_target(repair.target)?;
    Ok((original_id, repair.sources, target))
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ProgressData {
    status: String,
    stage_key: String,
    context: serde_json::Value,
    percent_complete: f64,
    processed: usize,
    total_estimate: usize,
    evicted: usize,
    un_evicted: usize,
    timestamp: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ScanResult {
    success: bool,
    processed: usize,
    evicted: usize,
    un_evicted: usize,
    files_on_disk: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
    /// Cache folders the scan did not check (missing, empty, or with a folder of the cache layout
    /// that could not be read); none of their downloads was marked evicted.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    unchecked_folders: Vec<String>,
}

/// What to do after every probe missed but the key set cannot safely prove absence.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum UnverifiableAction {
    /// The download produced no probe keys, or a bare-metal key has no supported recipe. A
    /// pre-existing `IsEvicted` flag cannot be substantiated for either shape, so clear it.
    ClearStaleFlag,
    /// The download has probe keys, but an indexed root or unambiguous datasource resolution is
    /// unavailable. We have no evidence either way, so leave its flag untouched. Also the no-op
    /// outcome for a not-currently-evicted download.
    Abstain,
}

/// Pure classification for the unverifiable branch of the eviction scan. Offline mounts and
/// unresolved datasources abstain to avoid badge-flap. Orphans and unsupported bare-metal recipes
/// clear stale flags because they have no future absence recipe that could self-heal the flag.
///
/// `has_probe_keys` is true when the download produced at least one (service, url) probe key
/// this scan; `has_unknown_recipe` means at least one resolved bare-metal key has no stock recipe;
/// `is_evicted` is its current DB flag. This is only consulted after every on-disk probe missed.
fn classify_unverifiable(
    has_probe_keys: bool,
    has_unknown_recipe: bool,
    is_evicted: bool,
) -> UnverifiableAction {
    if is_evicted && (!has_probe_keys || has_unknown_recipe) {
        UnverifiableAction::ClearStaleFlag
    } else {
        UnverifiableAction::Abstain
    }
}

/// What to do with a download whose files COULD be positively verified this scan (at least one
/// probe key resolved to an on-disk indexed datasource root).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum VerifiableAction {
    /// Genuine eviction: content was cached (positive evidence) but is now confirmed absent.
    Evict,
    /// Clear the flag: either the content is back on disk (re-cached), OR it is absent AND there
    /// is no evidence it was ever cached (zero bytes served - aborted / metadata-only) — in which
    /// case a pre-existing `IsEvicted` flag is a false positive that we heal.
    Unevict,
    /// Nothing to change.
    NoOp,
}

/// Pure classification for the verifiable branch of the eviction scan. Gating evict on
/// `was_cached` stops content that was never written to disk (zero bytes served - aborted or
/// metadata-only rows) from being mislabeled "evicted", and heals rows already wrongly flagged.
///
/// `has_cache_file` is the ANY-probe-key on-disk result; `is_evicted` is the current DB flag;
/// `was_cached` is true when nginx provably wrote the download's content to cache (see
/// `download_was_cached` for the per-scheme evidence rule).
fn classify_verifiable(
    has_cache_file: bool,
    is_evicted: bool,
    was_cached: bool,
) -> VerifiableAction {
    if has_cache_file {
        // Content present on disk → never evicted; clear any stale flag (re-cached).
        if is_evicted {
            VerifiableAction::Unevict
        } else {
            VerifiableAction::NoOp
        }
    } else if was_cached {
        // Absent AND was once cached → genuine nginx eviction.
        if !is_evicted {
            VerifiableAction::Evict
        } else {
            VerifiableAction::NoOp
        }
    } else {
        // Absent AND never cached (zero bytes served - aborted / metadata-only) → NOT an
        // eviction. Heal a pre-existing false-positive flag.
        if is_evicted {
            VerifiableAction::Unevict
        } else {
            VerifiableAction::NoOp
        }
    }
}

/// Whether nginx provably wrote this download's content to the cache. HIT bytes are always
/// proof: a HIT can only be served from a file that existed on disk. MISS bytes are proof only
/// under the monolithic scheme, where a lancache MISS proxies the content AND writes it to
/// cache - gating on CacheHitBytes alone made every game downloaded exactly once (pure-MISS,
/// the common case) permanently undetectable as evicted. Bare-metal nginx breaks that premise:
/// a concurrent ranged request logs MISS with a response that is never written to cache, and
/// uncacheable or duplicate fetches do the same, so bare-metal MISS bytes are not evidence
/// that content landed on disk. Any bare-metal probe key therefore disqualifies MISS bytes for
/// the whole download (fail-closed for mixed-scheme key sets). Zero-byte rows (aborted
/// transfers / metadata-only sessions where nothing was ever written) remain excluded.
fn download_was_cached(
    hit_bytes: i64,
    miss_bytes: i64,
    keys: &[cache_eviction_paths::ProbeKey],
) -> bool {
    let miss_bytes_are_evidence = !keys
        .iter()
        .any(cache_eviction_paths::ProbeKey::is_bare_metal);
    hit_bytes > 0 || (miss_bytes > 0 && miss_bytes_are_evidence)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DownloadAction {
    Evict,
    Unevict,
    ClearUnverifiable,
    NoOp,
}

struct DownloadIdentity {
    datasource: Option<String>,
    service: Option<String>,
    game_name: Option<String>,
    game_app_id: Option<i64>,
    depot_id: Option<i64>,
    epic_app_id: Option<String>,
}

/// Classifies the complete probe result. Presence is intentionally evaluated before absence
/// verifiability: a positive digest hit is conclusive even when another key has an offline root,
/// unresolved datasource, or unsupported bare-metal recipe. The strict all-keys policy is only
/// allowed to gate an absent/Evict decision.
fn classify_download(
    has_cache_file: bool,
    can_verify_absence: bool,
    has_probe_keys: bool,
    has_unknown_recipe: bool,
    is_evicted: bool,
    was_cached: bool,
) -> DownloadAction {
    if has_cache_file {
        return match classify_verifiable(true, is_evicted, was_cached) {
            VerifiableAction::Unevict => DownloadAction::Unevict,
            VerifiableAction::NoOp => DownloadAction::NoOp,
            VerifiableAction::Evict => unreachable!("present content cannot be evicted"),
        };
    }

    if !can_verify_absence {
        return match classify_unverifiable(has_probe_keys, has_unknown_recipe, is_evicted) {
            UnverifiableAction::ClearStaleFlag => DownloadAction::ClearUnverifiable,
            UnverifiableAction::Abstain => DownloadAction::NoOp,
        };
    }

    match classify_verifiable(false, is_evicted, was_cached) {
        VerifiableAction::Evict => DownloadAction::Evict,
        VerifiableAction::Unevict => DownloadAction::Unevict,
        VerifiableAction::NoOp => DownloadAction::NoOp,
    }
}

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let progress_path = match args.progress_json.as_deref() {
        Some("none") | None => None,
        Some(p) => Some(PathBuf::from(p)),
    };

    // File-write-before-stdout-emit invariant: the C# event callback reads the progress file
    // the moment "started" arrives, and the C#-created temp file is empty until our first
    // write - seed it before emitting so that read never sees empty (unparseable) JSON.
    // A seed failure is only logged: the scan itself must still run, and the later
    // write_progress calls surface a persistent file problem anyway.
    if let Err(e) = write_progress_file(
        progress_path.as_deref(),
        "running",
        "signalr.evictionScan.scanning",
        &json!({}),
        0.0,
        0,
        0,
        0,
        0,
    ) {
        eprintln!(
            "[EvictionScan] Warning: failed to seed progress file: {:#}",
            e
        );
    }
    reporter.emit_started("signalr.evictionScan.scanning", json!({}));

    // Single failure funnel: run_scan_and_report prints the ScanResult JSON contract on every
    // path (success, business failure, and transport error) and only calls emit_complete on
    // genuine success; any Err it returns (business `success:false` or a propagated transport
    // error) is handed to finish_or_exit, the ONE place that emits the structured `failed`
    // event + errorDetail, so a business failure and a `?`-propagated one are no longer two
    // different failure shapes.
    let result = run_scan_and_report(
        &args.datasource_config,
        progress_path.as_deref(),
        args.operation_id.as_deref(),
        args.repair.as_deref(),
        &reporter,
    )
    .await;
    progress_events::finish_or_exit(&reporter, "signalr.evictionScan.error.fatal", result);
    Ok(())
}

/// Runs the scan, prints the `ScanResult` JSON contract as the last stdout line in every case
/// (the C# caller reads only the last stdout line), and returns `Err` on any failure (business
/// `success:false` or a propagated error) so the caller's single `finish_or_exit` funnel emits
/// the structured failed event exactly once.
async fn run_scan_and_report(
    datasource_config: &str,
    progress_path: Option<&Path>,
    operation_id: Option<&str>,
    repair_path: Option<&str>,
    reporter: &ProgressReporter,
) -> Result<()> {
    match run_scan(
        datasource_config,
        progress_path,
        operation_id,
        repair_path,
        reporter,
    )
    .await
    {
        Ok(result) => {
            // Real final counts (never hardcoded zeros) - the exact bug fixed in cache_clear.
            if result.success {
                reporter.emit_complete(
                    "signalr.evictionScan.complete",
                    json!({
                        "processed": result.processed,
                        "evicted": result.evicted,
                        "unEvicted": result.un_evicted,
                        "filesOnDisk": result.files_on_disk,
                    }),
                );
            }
            let json = serde_json::to_string(&result)?;
            println!("{}", json);
            if result.success {
                Ok(())
            } else {
                anyhow::bail!("{}", result.error.clone().unwrap_or_default());
            }
        }
        Err(e) => {
            let error_detail = format!("{:#}", e);
            let result = ScanResult {
                success: false,
                processed: 0,
                evicted: 0,
                un_evicted: 0,
                files_on_disk: 0,
                error: Some(error_detail),
                unchecked_folders: Vec::new(),
            };
            let json = serde_json::to_string(&result)?;
            println!("{}", json);
            Err(e)
        }
    }
}

async fn run_scan(
    datasource_config_path: &str,
    progress_path: Option<&Path>,
    operation_id: Option<&str>,
    repair_path: Option<&str>,
    reporter: &ProgressReporter,
) -> Result<ScanResult> {
    let operation_id = operation_id.map(cache_repair::operation_uuid).transpose()?;
    if repair_path.is_some() && operation_id.is_none() {
        anyhow::bail!("--repair requires --operation-id");
    }
    // Step 1: Read datasource configuration
    let config_content = std::fs::read_to_string(datasource_config_path).with_context(|| {
        format!(
            "Failed to read datasource config: {}",
            datasource_config_path
        )
    })?;
    let datasources: Vec<DatasourceConfig> = serde_json::from_str(&config_content)
        .with_context(|| "Failed to parse datasource config JSON")?;

    if datasources.is_empty() {
        if let Some(operation_id) = operation_id {
            let pool = db::create_pool().await?;
            ensure_checkpoint(&pool, operation_id).await?;
        }
        return Ok(ScanResult {
            success: true,
            processed: 0,
            evicted: 0,
            un_evicted: 0,
            files_on_disk: 0,
            error: None,
            unchecked_folders: Vec::new(),
        });
    }

    // Progress budget: file scan 0–50%, DB reconcile 50–99%. Reserve 100% for the
    // C# EvictionScanComplete event so the UI never shows "done" while post-processing runs.
    const FILE_SCAN_PROGRESS_START: f64 = 0.0;
    const FILE_SCAN_PROGRESS_END: f64 = 50.0;
    const DB_PROGRESS_START: f64 = 50.0;
    const DB_PROGRESS_END: f64 = 99.0;

    write_progress(
        progress_path,
        reporter,
        "running",
        "signalr.evictionScan.scanning",
        json!({}),
        FILE_SCAN_PROGRESS_START,
        0,
        0,
        0,
        0,
    )?;

    // Step 2: Build HashSet of all files on disk across all cache directories
    let file_scan_span = FILE_SCAN_PROGRESS_END - FILE_SCAN_PROGRESS_START;
    let mut report_file_count = |files_found| {
        // Asymptotic curve toward FILE_SCAN_PROGRESS_END — we don't know the total upfront.
        let fraction = 1.0 - 1.0 / (1.0 + files_found as f64 / 1_000_000.0);
        let percent = FILE_SCAN_PROGRESS_START + fraction * file_scan_span;
        let _ = write_progress(
            progress_path,
            reporter,
            "running",
            "signalr.evictionScan.scanningFiles",
            json!({ "filesFound": files_found }),
            percent,
            0,
            0,
            0,
            0,
        );
    };
    let normal_roots;
    let repair_index;
    let target;
    if let Some(repair_path) = repair_path {
        let operation_id = operation_id.context("--repair requires --operation-id")?;
        let (original_id, sources, selected_target) = read_repair(Path::new(repair_path))?;
        if original_id == operation_id {
            anyhow::bail!(
                "repair document operation ID must differ from the scan checkpoint operation ID"
            );
        }
        repair_index = Some(cache_eviction_paths::collect_files_for_repair(
            &datasources,
            &sources,
            original_id,
            &mut report_file_count,
        )?);
        normal_roots = None;
        target = selected_target;
    } else {
        repair_index = None;
        normal_roots = Some(cache_eviction_paths::DatasourceRoots::from_configs(
            &datasources,
        ));
        target = TargetSelector::Bulk;
    }
    let datasource_roots = repair_index
        .as_ref()
        .map(|repair| &repair.roots)
        .or(normal_roots.as_ref())
        .context("eviction scan datasource roots are unavailable")?;
    let normal_files;
    let unchecked_folders;
    let files_on_disk = if let Some(repair) = repair_index.as_ref() {
        unchecked_folders = Vec::new();
        &repair.files
    } else {
        let (files, unchecked) =
            cache_eviction_paths::collect_files_on_disk(&datasources, &mut report_file_count);
        normal_files = files;
        unchecked_folders = unchecked;
        &normal_files
    };

    if repair_index.is_none() && files_on_disk.is_empty() && operation_id.is_none() {
        eprintln!("[EvictionScan] No cache files found on disk - skipping to prevent false eviction flags");
        return Ok(ScanResult {
            success: true,
            processed: 0,
            evicted: 0,
            un_evicted: 0,
            files_on_disk: 0,
            error: None,
            unchecked_folders,
        });
    }

    let total_files = files_on_disk.len();
    eprintln!(
        "[EvictionScan] Found {} cache files on disk across {} datasource(s)",
        total_files,
        datasources.len()
    );

    // Step 3: Connect to database
    let pool = db::create_pool().await?;
    if let Some(operation_id) = operation_id {
        ensure_checkpoint(&pool, operation_id).await?;
    }
    if repair_index.is_none() && files_on_disk.is_empty() {
        eprintln!("[EvictionScan] No cache files found on disk - skipping to prevent false eviction flags");
        return Ok(ScanResult {
            success: true,
            processed: 0,
            evicted: 0,
            un_evicted: 0,
            files_on_disk: 0,
            error: None,
            unchecked_folders,
        });
    }

    // Count total inactive downloads for progress estimation
    let total_estimate: i64 =
        sqlx::query_scalar(r#"SELECT COUNT(*) FROM "Downloads" WHERE "IsActive" = false"#)
            .fetch_one(&pool)
            .await
            .unwrap_or_else(|e| {
                eprintln!(
                    "[EvictionScan] Warning: failed to estimate total downloads: {}",
                    e
                );
                0
            });

    let total_estimate = total_estimate as usize;

    // Step 4: Process downloads in batches
    let batch_size: i64 = 2000;
    let mut total_evicted: usize = 0;
    let mut total_un_evicted: usize = 0;
    let mut total_processed: usize = 0;
    let mut last_processed_id: i64 = 0;

    // Scan-wide probe memo: many downloads share the same (service, url, datasource)
    // tuple, and the on-disk file-name index is immutable for the whole scan, so each
    // unique tuple is hashed and probed exactly once. Keyed by ProbeKey::memo_key (the md5
    // digest of the resolved root + key scheme + service + URL) so the memo holds 16 bytes +
    // bool per unique tuple instead
    // of owning the tuple's strings - the old shape grew unbounded into hundreds of MB on
    // large libraries.
    let mut probe_memo: HashMap<u128, bool> = HashMap::new();

    loop {
        // Step A: Fetch a batch of distinct inactive download IDs
        let download_rows = sqlx::query(
            r#"
            SELECT "Id" as download_id, "IsEvicted" as is_evicted,
                   "CacheHitBytes" as cache_hit_bytes, "CacheMissBytes" as cache_miss_bytes,
                   "Datasource" as datasource, "Service" as service, "GameName" as game_name,
                   "GameAppId" as game_app_id, "DepotId" as depot_id,
                   "EpicAppId" as epic_app_id
            FROM "Downloads"
            WHERE "IsActive" = false AND "Id" > $1
            ORDER BY "Id"
            LIMIT $2
            "#,
        )
        .bind(last_processed_id)
        .bind(batch_size)
        .fetch_all(&pool)
        .await
        .with_context(|| "Failed to fetch downloads")?;

        if download_rows.is_empty() {
            break;
        }

        let download_ids: Vec<i64> = download_rows.iter().map(|r| r.get("download_id")).collect();
        let download_evicted: HashMap<i64, bool> = download_rows
            .iter()
            .map(|r| (r.get("download_id"), r.get("is_evicted")))
            .collect();
        // Cache-evidence inputs per download, kept as separate (hit, miss) byte counts
        // because MISS bytes only count as evidence under the monolithic key scheme - the
        // rule lives in download_was_cached, which also needs the download's probe keys.
        let download_cache_bytes: HashMap<i64, (i64, i64)> = download_rows
            .iter()
            .map(|r| {
                let hit: i64 = r.get("cache_hit_bytes");
                let miss: i64 = r.get("cache_miss_bytes");
                (r.get("download_id"), (hit, miss))
            })
            .collect();
        let download_identity: HashMap<i64, DownloadIdentity> = download_rows
            .iter()
            .map(|row| {
                (
                    row.get("download_id"),
                    DownloadIdentity {
                        datasource: row.get("datasource"),
                        service: row.get("service"),
                        game_name: row.get("game_name"),
                        game_app_id: row.get("game_app_id"),
                        depot_id: row.get("depot_id"),
                        epic_app_id: row.get("epic_app_id"),
                    },
                )
            })
            .collect();

        let Some(last_download_id) = download_ids.last().copied() else {
            break;
        };
        last_processed_id = last_download_id;
        let batch_count = download_ids.len();

        // Step B: Fetch log entries for these downloads (single int8[] array bind).
        // GROUP BY (download_id, service, url, datasource) + MAX("BytesServed") collapses
        // duplicate rows to one per probe tuple and carries the URL's largest observed byte
        // size, which sizes the probe chunk count (clamped) in ProbeKey::has_cache_file.
        let log_rows = sqlx::query(
            r#"
            SELECT "DownloadId" as download_id, "Service" as service, "Url" as url, "Datasource" as datasource, MAX("BytesServed") as bytes_served
            FROM "LogEntries"
            WHERE "DownloadId" = ANY($1)
            GROUP BY "DownloadId", "Service", "Url", "Datasource"
            "#
        )
        .bind(&download_ids)
        .fetch_all(&pool)
        .await
        .with_context(|| "Failed to fetch log entries")?;

        // Group probe keys by download_id
        let mut download_keys: HashMap<i64, Vec<cache_eviction_paths::ProbeKey>> = HashMap::new();
        let mut download_origins: HashMap<i64, Vec<Option<String>>> = HashMap::new();
        for row in &log_rows {
            let download_id: i64 = row.get("download_id");
            let service: Option<String> = row.get("service");
            let url: Option<String> = row.get("url");
            let datasource: Option<String> = row.get("datasource");
            download_origins
                .entry(download_id)
                .or_default()
                .push(datasource.clone());
            // MAX() is typed nullable; NULL means no usable size → 0 → probe-chunk floor.
            let bytes_served: Option<i64> = row.get("bytes_served");

            if let (Some(service), Some(url)) = (service, url) {
                download_keys.entry(download_id).or_default().push(
                    cache_eviction_paths::ProbeKey::new(
                        &service,
                        url,
                        datasource.as_deref(),
                        bytes_served.unwrap_or(0),
                        datasource_roots,
                    ),
                );
            }
        }

        // Probe each not-yet-memoized unique key once, in parallel (pure CPU over the
        // immutable on-disk index), then fold the results into the scan-wide memo. One
        // representative ProbeKey per memo_key suffices: keys with equal memo_key probe
        // the same candidate set (bytes_served is deliberately outside the identity).
        let mut unprobed: HashMap<u128, &cache_eviction_paths::ProbeKey> = HashMap::new();
        for key in download_keys.values().flatten() {
            if !probe_memo.contains_key(&key.memo_key) {
                unprobed.entry(key.memo_key).or_insert(key);
            }
        }
        let probe_results: Vec<(u128, bool)> = unprobed
            .into_par_iter()
            .map(|(memo_key, key)| (memo_key, key.has_cache_file(files_on_disk)))
            .collect();
        probe_memo.extend(probe_results);

        // Decide each download's eviction state from POSITIVE disk evidence only.
        //
        // A positive hit from any probe key proves presence regardless of whether the remaining
        // keys can prove absence. Only an all-miss result consults the absence policy: resolved
        // Monolithic keys retain the shipped any-indexed-root rule, while BareMetal requires
        // every key's root and recipe to be checkable. We assert IsEvicted=true ONLY for a
        // download whose files are confirmed absent. Unverifiable absence never creates a new
        // eviction; it either abstains or conservatively clears a stale flag as described below.
        //
        // Unverifiable shapes that must not stamp false "Evicted" badges on games
        // whose files are still on disk:
        //   1. No usable log entries (no rows, or every row had a NULL Service/Url and
        //      was filtered out by the Step B query) → no (service, url) to hash. The
        //      old code evicted these BLIND. This is exactly how a freshly-added game
        //      gets wrongly evicted — e.g. a shared/mis-mapped depot removal rewrote its
        //      lines out of access.log and a later reprocess left the Downloads row
        //      without LogEntries, or the Download↔LogEntry association simply lagged.
        //   2. Every key resolves to a datasource cache root that isn't on disk right
        //      now (relocated/offline/removed mount). `has_cache_file` returns false for
        //      an unindexed root, but that means "we didn't look here", not "it's gone".
        //   3. A bare-metal key has no supported per-vhost recipe, or any of its roots is
        //      offline. An empty candidate iterator is not proof that the file is gone.
        //
        // The verified branch below still evicts downloads whose files are genuinely
        // missing, so legitimate nginx evictions are unaffected. Clearing stale flags
        // here counts toward `un_evicted`, which drives the C# reverse-reconcile that
        // self-heals the dependent CachedGameDetections badge.
        let mut ids_to_evict: Vec<i64> = Vec::new();
        let mut ids_to_unevict: Vec<i64> = Vec::new();
        let mut unverifiable_cleared = 0usize;
        let mut page_processed = 0usize;

        for download_id in &download_ids {
            let identity = download_identity
                .get(download_id)
                .context("download identity is missing from its scan page")?;
            let origins = download_origins
                .get(download_id)
                .map(Vec::as_slice)
                .unwrap_or(&[]);
            if let Some(repair) = repair_index.as_ref() {
                if !target.matches(
                    identity.game_app_id,
                    identity.depot_id,
                    identity.epic_app_id.as_deref(),
                    identity.game_name.as_deref(),
                    identity.service.as_deref(),
                ) || !repair.intersects(identity.datasource.as_deref(), origins)
                {
                    continue;
                }
            }
            page_processed += 1;
            let is_evicted = download_evicted.get(download_id).copied().unwrap_or(false);
            let keys = download_keys.get(download_id);
            let keys_for_probe: &[cache_eviction_paths::ProbeKey] =
                keys.map(Vec::as_slice).unwrap_or(&[]);
            let mut has_cache_file = false;
            for key in keys_for_probe {
                let cached = probe_memo.get(&key.memo_key).copied().ok_or_else(|| {
                    anyhow::anyhow!("probe memo must contain every key collected this batch")
                })?;
                if cached {
                    has_cache_file = true;
                    break;
                }
            }

            // A positive memo hit is conclusive presence and is classified first. Absence is
            // verifiable only when the complete key set satisfies its scheme policy; unsupported
            // bare-metal recipes clear stale flags, while offline/unresolved keys abstain.
            let has_probe_keys = !keys_for_probe.is_empty();
            let mut can_verify_absence = if let Some(repair) = repair_index.as_ref() {
                let origins_checked =
                    repair.origins_can_verify_absence(identity.datasource.as_deref(), origins);
                let keyless_empty = keys_for_probe.is_empty()
                    && repair.origins_are_trusted_empty(identity.datasource.as_deref(), origins);
                origins_checked
                    && (cache_eviction_paths::keys_can_verify_repair_absence(
                        keys_for_probe,
                        repair,
                    ) || keyless_empty)
            } else {
                cache_eviction_paths::keys_can_verify_absence(keys_for_probe, files_on_disk)
            };
            let has_unknown_recipe = keys_for_probe
                .iter()
                .any(cache_eviction_paths::ProbeKey::has_unknown_recipe);
            let (hit_bytes, miss_bytes) = download_cache_bytes
                .get(download_id)
                .copied()
                .unwrap_or((0, 0));
            let was_cached = if keys_for_probe.is_empty() {
                hit_bytes > 0
                    || (miss_bytes > 0
                        && repair_index.as_ref().is_none_or(|repair| {
                            !repair
                                .origins_include_bare_metal(identity.datasource.as_deref(), origins)
                        }))
            } else {
                download_was_cached(hit_bytes, miss_bytes, keys_for_probe)
            };
            if repair_index.is_some() && keys_for_probe.is_empty() && !was_cached {
                can_verify_absence = false;
            }
            let action = if repair_index.is_some() && !has_cache_file && !can_verify_absence {
                DownloadAction::NoOp
            } else {
                classify_download(
                    has_cache_file,
                    can_verify_absence,
                    has_probe_keys,
                    has_unknown_recipe,
                    is_evicted,
                    was_cached,
                )
            };
            match action {
                DownloadAction::Evict => ids_to_evict.push(*download_id),
                DownloadAction::Unevict => ids_to_unevict.push(*download_id),
                DownloadAction::ClearUnverifiable => {
                    ids_to_unevict.push(*download_id);
                    unverifiable_cleared += 1;
                }
                DownloadAction::NoOp => {}
            }
        }

        if unverifiable_cleared > 0 {
            eprintln!(
                "[EvictionScan] Cleared stale IsEvicted on {} unverifiable download(s) with no probe keys or no supported bare-metal recipe. Offline-mount and unresolved-datasource downloads are left untouched to avoid badge-flap.",
                unverifiable_cleared
            );
        }

        // Apply every page and its durable counters in one transaction. A page that changes
        // no flags still advances Processed, so a stopped child cannot lose committed work
        // between PostgreSQL and the optional progress file.
        let mut page_evicted = 0usize;
        let mut page_un_evicted = 0usize;
        if (operation_id.is_some() && page_processed > 0)
            || !ids_to_evict.is_empty()
            || !ids_to_unevict.is_empty()
        {
            let mut tx = pool
                .begin()
                .await
                .with_context(|| "Failed to begin eviction batch transaction")?;
            if !ids_to_evict.is_empty() {
                let mut rows = Vec::with_capacity(ids_to_evict.len());
                for download_id in &ids_to_evict {
                    let (hit_bytes, miss_bytes) = download_cache_bytes
                        .get(download_id)
                        .copied()
                        .with_context(|| {
                            format!(
                                "Download {download_id} is missing byte totals from its scan page"
                            )
                        })?;
                    rows.push((*download_id, hit_bytes, miss_bytes));
                }
                let changed = evict_unchanged_downloads_tx(&mut tx, &rows).await? as usize;
                page_evicted = changed;
                if changed < rows.len() {
                    eprintln!(
                        "[EvictionScan] Left {} changed download(s) for the next scan.",
                        rows.len() - changed
                    );
                }
            }
            if !ids_to_unevict.is_empty() {
                let mut rows = Vec::with_capacity(ids_to_unevict.len());
                for download_id in &ids_to_unevict {
                    let (hit_bytes, miss_bytes) = download_cache_bytes
                        .get(download_id)
                        .copied()
                        .with_context(|| {
                            format!(
                                "Download {download_id} is missing byte totals from its scan page"
                            )
                        })?;
                    rows.push((*download_id, hit_bytes, miss_bytes));
                }
                page_un_evicted =
                    update_download_eviction_state_tx(&mut tx, &rows, false).await? as usize;
                if page_un_evicted < rows.len() {
                    eprintln!(
                        "[EvictionScan] Left {} changed download(s) for the next scan.",
                        rows.len() - page_un_evicted
                    );
                }
            }
            if let Some(operation_id) = operation_id {
                update_checkpoint_tx(
                    &mut tx,
                    operation_id,
                    page_processed,
                    page_evicted,
                    page_un_evicted,
                )
                .await?;
            }
            tx.commit()
                .await
                .with_context(|| "Failed to commit eviction batch transaction")?;
        }

        total_evicted += page_evicted;
        total_un_evicted += page_un_evicted;
        total_processed += page_processed;

        // Write progress (50–99% band; never 100% — C# owns completion)
        let db_span = DB_PROGRESS_END - DB_PROGRESS_START;
        let percent = if total_estimate > 0 {
            DB_PROGRESS_START
                + (total_processed as f64 / total_estimate as f64 * db_span).min(db_span)
        } else {
            DB_PROGRESS_START
        };

        write_progress(
            progress_path,
            reporter,
            "running",
            "signalr.evictionScan.progress",
            json!({ "totalProcessed": total_processed, "totalEstimate": total_estimate }),
            percent,
            total_processed,
            total_estimate,
            total_evicted,
            total_un_evicted,
        )?;

        // Cooperative cancellation: between-batch check (after a full batch has committed).
        // Never leaves a half-written batch: the transaction above either committed or rolled back.
        if cancel::is_cancelled() {
            eprintln!(
                "[EvictionScan] Cancellation requested after batch — processed {} downloads so far, exiting.",
                total_processed
            );
            break;
        }

        // If we got fewer downloads than batch_size, we've reached the end
        if (batch_count as i64) < batch_size {
            break;
        }
    }

    eprintln!(
        "[EvictionScan] Scan complete: processed {} downloads, {} newly evicted, {} un-evicted (re-cached)",
        total_processed, total_evicted, total_un_evicted
    );

    // Final Rust progress tick — still "running" at 99% so the frontend waits for
    // EvictionScanComplete from C# after post-processing (detection recovery, etc.).
    write_progress(
        progress_path,
        reporter,
        "running",
        "signalr.evictionScan.finalizing",
        json!({ "totalProcessed": total_processed, "totalEvicted": total_evicted, "totalUnEvicted": total_un_evicted }),
        DB_PROGRESS_END,
        total_processed,
        total_estimate,
        total_evicted,
        total_un_evicted,
    )?;

    Ok(ScanResult {
        success: true,
        processed: total_processed,
        evicted: total_evicted,
        un_evicted: total_un_evicted,
        files_on_disk: total_files,
        error: None,
        unchecked_folders,
    })
}

/// Marks rows evicted only when they are still the rows the scan judged: inactive, with the
/// byte totals it read with the page. An ingest pass that continued one of them since the page
/// read changed its totals or made it active, so it waits for the next scan instead of being
/// hidden (or, in remove mode, deleted with its log entries) on old evidence.
async fn evict_unchanged_downloads_tx(
    tx: &mut Transaction<'_, Postgres>,
    rows: &[(i64, i64, i64)],
) -> Result<u64> {
    let ids: Vec<i64> = rows.iter().map(|(id, _, _)| *id).collect();
    let hits: Vec<i64> = rows.iter().map(|(_, hit, _)| *hit).collect();
    let misses: Vec<i64> = rows.iter().map(|(_, _, miss)| *miss).collect();
    let result = sqlx::query(
        r#"
        UPDATE "Downloads" d
        SET "IsEvicted" = true
        FROM UNNEST($1::bigint[], $2::bigint[], $3::bigint[]) AS u(id, hit, miss)
        WHERE d."Id" = u.id
          AND d."CacheHitBytes" = u.hit
          AND d."CacheMissBytes" = u.miss
          AND d."IsActive" = false
          AND d."IsEvicted" = false
        "#,
    )
    .bind(ids)
    .bind(hits)
    .bind(misses)
    .execute(&mut **tx)
    .await
    .context("Failed to mark unchanged downloads evicted")?;

    Ok(result.rows_affected())
}

/// Transaction-scoped variant used by the per-batch transaction in run_scan (rust-3).
/// Executes within a caller-owned transaction so that evict + unevict UPDATEs for the
/// same batch are atomic.
async fn update_download_eviction_state_tx(
    tx: &mut Transaction<'_, Postgres>,
    rows: &[(i64, i64, i64)],
    is_evicted: bool,
) -> Result<u64> {
    let error_context = if is_evicted {
        "Failed to update evicted downloads (tx)"
    } else {
        "Failed to update un-evicted downloads (tx)"
    };

    let ids: Vec<i64> = rows.iter().map(|(id, _, _)| *id).collect();
    let hits: Vec<i64> = rows.iter().map(|(_, hit, _)| *hit).collect();
    let misses: Vec<i64> = rows.iter().map(|(_, _, miss)| *miss).collect();
    let result = sqlx::query(
        r#"
        UPDATE "Downloads" d
        SET "IsEvicted" = $1
        FROM UNNEST($2::bigint[], $3::bigint[], $4::bigint[]) AS u(id, hit, miss)
        WHERE d."Id" = u.id
          AND d."CacheHitBytes" = u.hit
          AND d."CacheMissBytes" = u.miss
          AND d."IsActive" = false
          AND d."IsEvicted" <> $1
        "#,
    )
    .bind(is_evicted)
    .bind(ids)
    .bind(hits)
    .bind(misses)
    .execute(&mut **tx)
    .await
    .with_context(|| error_context)?;

    Ok(result.rows_affected())
}

async fn update_checkpoint_tx(
    tx: &mut Transaction<'_, Postgres>,
    operation_id: uuid::Uuid,
    processed: usize,
    evicted: usize,
    un_evicted: usize,
) -> Result<()> {
    let processed =
        i32::try_from(processed).context("eviction processed counter exceeds integer")?;
    let evicted = i32::try_from(evicted).context("eviction counter exceeds integer")?;
    let un_evicted = i32::try_from(un_evicted).context("un-eviction counter exceeds integer")?;
    let result = sqlx::query(
        r#"
        UPDATE "EvictionScanCheckpoints"
        SET "Processed" = "Processed" + $2,
            "Evicted" = "Evicted" + $3,
            "UnEvicted" = "UnEvicted" + $4
        WHERE "OperationId" = $1::uuid
          AND "FinalizedAtUtc" IS NULL
        "#,
    )
    .bind(operation_id.hyphenated().to_string())
    .bind(processed)
    .bind(evicted)
    .bind(un_evicted)
    .execute(&mut **tx)
    .await
    .context("Failed to update eviction scan checkpoint")?;

    if result.rows_affected() != 1 {
        anyhow::bail!(
            "eviction scan checkpoint is missing or finalized for operation {}",
            operation_id
        );
    }

    Ok(())
}

async fn ensure_checkpoint(pool: &PgPool, operation_id: uuid::Uuid) -> Result<()> {
    let checkpoint_ready: bool = sqlx::query_scalar(
        r#"
        SELECT EXISTS (
            SELECT 1
            FROM "EvictionScanCheckpoints"
            WHERE "OperationId" = $1::uuid
              AND "FinalizedAtUtc" IS NULL
        )
        "#,
    )
    .bind(operation_id.hyphenated().to_string())
    .fetch_one(pool)
    .await
    .context("Failed to verify eviction scan checkpoint")?;
    if !checkpoint_ready {
        anyhow::bail!(
            "eviction scan checkpoint is missing or finalized for operation {}",
            operation_id
        );
    }
    Ok(())
}

/// Writes the (optional) progress file exactly as before, then emits a stdout progress event via
/// `reporter` (file write first). Every checkpoint in this file uses status "running" - the
/// terminal started/complete/failed events are emitted separately in `main()` from the real
/// `ScanResult`, so this always maps to `emit_progress`.
#[allow(clippy::too_many_arguments)]
fn write_progress(
    progress_path: Option<&Path>,
    reporter: &ProgressReporter,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    processed: usize,
    total_estimate: usize,
    evicted: usize,
    un_evicted: usize,
) -> Result<()> {
    write_progress_file(
        progress_path,
        status,
        stage_key,
        &context,
        percent_complete,
        processed,
        total_estimate,
        evicted,
        un_evicted,
    )?;

    reporter.emit_progress(percent_complete, stage_key, context);

    Ok(())
}

/// File-only half of `write_progress`: writes the checkpoint without emitting any stdout
/// event, so `main()` can seed the file before `emit_started` (a "started" event has its own
/// emit and must not be preceded by a stray progress event on the stdout channel).
#[allow(clippy::too_many_arguments)]
fn write_progress_file(
    progress_path: Option<&Path>,
    status: &str,
    stage_key: &str,
    context: &serde_json::Value,
    percent_complete: f64,
    processed: usize,
    total_estimate: usize,
    evicted: usize,
    un_evicted: usize,
) -> Result<()> {
    if let Some(path) = progress_path {
        let progress = ProgressData {
            status: status.to_string(),
            stage_key: stage_key.to_string(),
            context: context.clone(),
            percent_complete,
            processed,
            total_estimate,
            evicted,
            un_evicted,
            timestamp: progress_utils::current_timestamp(),
        };

        progress_utils::write_progress_json(path, &progress)?;
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{
        cache_eviction_paths, classify_download, classify_unverifiable, classify_verifiable,
        download_was_cached, evict_unchanged_downloads_tx, read_repair, run_scan,
        update_checkpoint_tx, Args, DatasourceConfig, DownloadAction, RepairTarget, TargetSelector,
        UnverifiableAction, VerifiableAction,
    };
    use clap::Parser;
    use lancache_processor::cache_repair;
    use lancache_processor::cache_utils;
    use lancache_processor::progress_events::ProgressReporter;
    use sqlx::postgres::PgPoolOptions;
    use sqlx::{PgPool, Row};
    use uuid::Uuid;

    async fn isolated_pool(test_name: &str) -> Option<(PgPool, String)> {
        let Ok(connection) = std::env::var("LANCACHE_TEST_DATABASE_URL") else {
            println!("SKIP (LANCACHE_TEST_DATABASE_URL unset): {test_name}");
            return None;
        };
        let pool = PgPoolOptions::new()
            .max_connections(1)
            .connect(&connection)
            .await
            .expect("connect to isolated test PostgreSQL");
        let schema = format!("eviction_{}", Uuid::new_v4().simple());
        sqlx::query(&format!(r#"CREATE SCHEMA "{schema}""#))
            .execute(&pool)
            .await
            .expect("create isolated test schema");
        Some((pool, schema))
    }

    /// Builds a resolved ProbeKey under the given key scheme for the was_cached tests.
    fn scheme_key(key_scheme: &str) -> cache_eviction_paths::ProbeKey {
        let datasources = [DatasourceConfig {
            name: "ds".to_string(),
            cache_path: "cache".to_string(),
            is_default: true,
            key_scheme: key_scheme.to_string(),
        }];
        let roots = cache_eviction_paths::DatasourceRoots::from_configs(&datasources);
        cache_eviction_paths::ProbeKey::new(
            "steam",
            "/depot/1/chunk/abcdef".to_string(),
            Some("ds"),
            0,
            &roots,
        )
    }

    fn write_repair(
        directory: &std::path::Path,
        original_id: Uuid,
        cache_path: &std::path::Path,
    ) -> std::path::PathBuf {
        let path = directory.join("repair.json");
        std::fs::write(
            &path,
            serde_json::to_vec(&serde_json::json!({
                "version": 1,
                "operationId": original_id.to_string(),
                "sources": [{
                    "name": "default",
                    "cachePath": cache_path,
                    "keyScheme": "monolithic"
                }],
                "target": null
            }))
            .unwrap(),
        )
        .unwrap();
        path
    }

    #[test]
    fn repair_document_keeps_original_receipt_owner_separate_from_scan_attempt() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_path = fixture.path().join("cache");
        std::fs::create_dir_all(cache_path.join("ef").join("cd")).unwrap();
        let cache_file = cache_path
            .join("ef")
            .join("cd")
            .join("0123456789abcdef0123456789abcdef");
        std::fs::write(&cache_file, b"cache").unwrap();
        let original_id = Uuid::new_v4();
        let attempt_id = Uuid::new_v4();
        let original_text = original_id.to_string();
        let receipt = cache_repair::prepare_receipt(&cache_path, Some(&original_text))
            .unwrap()
            .unwrap();
        let receipt_bytes = std::fs::read(&receipt).unwrap();
        std::fs::remove_file(cache_file).unwrap();
        let repair_path = write_repair(fixture.path(), original_id, &cache_path);

        let (receipt_owner, sources, target) = read_repair(&repair_path).unwrap();
        let datasources = [DatasourceConfig {
            name: "default".to_string(),
            cache_path: cache_path.to_string_lossy().into_owned(),
            is_default: true,
            key_scheme: "monolithic".to_string(),
        }];
        let repair = cache_eviction_paths::collect_files_for_repair(
            &datasources,
            &sources,
            receipt_owner,
            |_| {},
        )
        .unwrap();

        assert_eq!(receipt_owner, original_id);
        assert_ne!(receipt_owner, attempt_id);
        assert_eq!(target, TargetSelector::Bulk);
        assert!(repair.files.is_empty());
        assert_eq!(std::fs::read(&receipt).unwrap(), receipt_bytes);
        assert!(
            !cache_repair::receipt_path(&cache_path.canonicalize().unwrap(), attempt_id).exists()
        );
    }

    #[tokio::test]
    async fn repair_rejects_equal_receipt_and_checkpoint_ids_before_scan() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_path = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_path).unwrap();
        let operation_id = Uuid::new_v4();
        let repair_path = write_repair(fixture.path(), operation_id, &cache_path);
        let datasource_path = fixture.path().join("datasources.json");
        std::fs::write(
            &datasource_path,
            serde_json::to_vec(&serde_json::json!([{
                "name": "default",
                "cachePath": cache_path,
                "isDefault": true,
                "keyScheme": "monolithic"
            }]))
            .unwrap(),
        )
        .unwrap();

        let operation_text = operation_id.to_string();
        let repair_text = repair_path.to_string_lossy().into_owned();
        let datasource_text = datasource_path.to_string_lossy().into_owned();
        let error = run_scan(
            &datasource_text,
            None,
            Some(&operation_text),
            Some(&repair_text),
            &ProgressReporter::new(false),
        )
        .await
        .err()
        .expect("equal receipt and checkpoint IDs must fail");

        assert!(error
            .to_string()
            .contains("must differ from the scan checkpoint operation ID"));
    }

    #[test]
    fn repair_target_accepts_only_supported_selector_forms() {
        assert_eq!(
            TargetSelector::from_target(None).unwrap(),
            TargetSelector::Bulk
        );
        assert_eq!(
            TargetSelector::from_target(Some(RepairTarget {
                steam_app_id: Some(440),
                steam_depot_ids: vec![441, 442],
                epic_game: None,
                game_name: None,
                service: None,
            }))
            .unwrap(),
            TargetSelector::Steam {
                app_id: 440,
                depot_ids: vec![441, 442]
            }
        );
        assert_eq!(
            TargetSelector::from_target(Some(RepairTarget {
                steam_app_id: None,
                steam_depot_ids: vec![],
                epic_game: Some("Fortnite".to_string()),
                game_name: None,
                service: None,
            }))
            .unwrap(),
            TargetSelector::Epic {
                game: "Fortnite".to_string()
            }
        );
        assert_eq!(
            TargetSelector::from_target(Some(RepairTarget {
                steam_app_id: None,
                steam_depot_ids: vec![],
                epic_game: None,
                game_name: Some("Diablo IV".to_string()),
                service: Some("battlenet".to_string()),
            }))
            .unwrap(),
            TargetSelector::Named {
                game: "Diablo IV".to_string(),
                service: "battlenet".to_string()
            }
        );
        assert_eq!(
            TargetSelector::from_target(Some(RepairTarget {
                steam_app_id: None,
                steam_depot_ids: vec![],
                epic_game: None,
                game_name: None,
                service: Some("steam".to_string()),
            }))
            .unwrap(),
            TargetSelector::Service {
                service: "steam".to_string()
            }
        );

        for invalid in [
            RepairTarget {
                steam_app_id: None,
                steam_depot_ids: vec![],
                epic_game: None,
                game_name: None,
                service: None,
            },
            RepairTarget {
                steam_app_id: None,
                steam_depot_ids: vec![441],
                epic_game: None,
                game_name: None,
                service: None,
            },
            RepairTarget {
                steam_app_id: Some(440),
                steam_depot_ids: vec![],
                epic_game: Some("Fortnite".to_string()),
                game_name: None,
                service: None,
            },
            RepairTarget {
                steam_app_id: None,
                steam_depot_ids: vec![],
                epic_game: None,
                game_name: Some("Diablo IV".to_string()),
                service: None,
            },
        ] {
            assert!(TargetSelector::from_target(Some(invalid)).is_err());
        }
    }

    #[test]
    fn epic_target_uses_game_name_and_nonnull_epic_identity() {
        let target = TargetSelector::Epic {
            game: "Fortnite".to_string(),
        };

        assert!(target.matches(
            None,
            None,
            Some("catalog-slug"),
            Some("Fortnite"),
            Some("epic")
        ));
        assert!(!target.matches(
            None,
            None,
            Some("Fortnite"),
            Some("Different Game"),
            Some("epic")
        ));
        assert!(!target.matches(None, None, None, Some("Fortnite"), Some("epic")));
    }

    #[test]
    fn steam_target_uses_checked_app_or_captured_depot_identity() {
        let target = TargetSelector::Steam {
            app_id: u32::MAX,
            depot_ids: vec![42],
        };

        assert!(target.matches(
            Some(i64::from(u32::MAX)),
            None,
            None,
            Some("Steam Game"),
            Some("steam")
        ));
        assert!(target.matches(None, Some(42), None, Some("Steam Game"), Some("steam")));
        assert!(!target.matches(
            Some(i64::from(u32::MAX) + 1),
            Some(-1),
            None,
            Some("Steam Game"),
            Some("steam")
        ));
    }

    #[test]
    fn operation_id_only_is_valid_and_repair_requires_it() {
        let operation_id = Uuid::new_v4().to_string();
        assert!(Args::try_parse_from([
            "cache_eviction_scan",
            "datasources.json",
            "none",
            "--operation-id",
            operation_id.as_str(),
        ])
        .is_ok());
        assert!(Args::try_parse_from([
            "cache_eviction_scan",
            "datasources.json",
            "none",
            "--repair",
            "repair.json",
        ])
        .is_err());
        assert!(Args::try_parse_from([
            "cache_eviction_scan",
            "datasources.json",
            "none",
            "--operation-id",
            operation_id.as_str(),
            "--repair",
            "repair.json",
        ])
        .is_ok());
    }

    #[tokio::test]
    async fn conditional_eviction_marks_only_unchanged_inactive_rows() {
        const TEST_NAME: &str = "conditional_eviction_marks_only_unchanged_inactive_rows";
        let Some((pool, schema)) = isolated_pool(TEST_NAME).await else {
            return;
        };
        sqlx::query(&format!(
            r#"
            CREATE TABLE "{schema}"."Downloads" (
                "Id" bigint PRIMARY KEY,
                "CacheHitBytes" bigint NOT NULL,
                "CacheMissBytes" bigint NOT NULL,
                "IsActive" boolean NOT NULL,
                "IsEvicted" boolean NOT NULL
            )
            "#
        ))
        .execute(&pool)
        .await
        .expect("create Downloads fixture");
        sqlx::query(&format!(
            r#"
            INSERT INTO "{schema}"."Downloads"
                ("Id", "CacheHitBytes", "CacheMissBytes", "IsActive", "IsEvicted")
            VALUES
                (1, 10, 20, false, false),
                (2, 30, 40, false, false),
                (3, 50, 60, false, false)
            "#
        ))
        .execute(&pool)
        .await
        .expect("insert Downloads fixture");

        let judged = vec![(1, 10, 20), (2, 30, 40), (3, 50, 60)];
        sqlx::query(&format!(
            r#"UPDATE "{schema}"."Downloads" SET "CacheHitBytes" = 31 WHERE "Id" = 2"#
        ))
        .execute(&pool)
        .await
        .expect("continue one download");
        sqlx::query(&format!(
            r#"UPDATE "{schema}"."Downloads" SET "IsActive" = true WHERE "Id" = 3"#
        ))
        .execute(&pool)
        .await
        .expect("activate one download");

        let mut control = pool.begin().await.expect("begin control transaction");
        sqlx::query(&format!(r#"SET LOCAL search_path TO "{schema}""#))
            .execute(&mut *control)
            .await
            .expect("select isolated control schema");
        let control_changed =
            sqlx::query(r#"UPDATE "Downloads" SET "IsEvicted" = true WHERE "Id" = ANY($1)"#)
                .bind(vec![1_i64, 2, 3])
                .execute(&mut *control)
                .await
                .expect("run unconditional control update")
                .rows_affected();
        control.rollback().await.expect("roll back control update");
        assert_eq!(control_changed, 3);

        let mut tx = pool
            .begin()
            .await
            .expect("begin eviction fixture transaction");
        sqlx::query(&format!(r#"SET LOCAL search_path TO "{schema}""#))
            .execute(&mut *tx)
            .await
            .expect("select isolated test schema");
        let changed = evict_unchanged_downloads_tx(&mut tx, &judged)
            .await
            .expect("mark unchanged downloads");
        tx.commit()
            .await
            .expect("commit eviction fixture transaction");

        let rows = sqlx::query(&format!(
            r#"SELECT "Id", "IsEvicted" FROM "{schema}"."Downloads" ORDER BY "Id""#
        ))
        .fetch_all(&pool)
        .await
        .expect("read eviction results")
        .into_iter()
        .map(|row| (row.get::<i64, _>("Id"), row.get::<bool, _>("IsEvicted")))
        .collect::<Vec<_>>();
        sqlx::query(&format!(r#"DROP SCHEMA "{schema}" CASCADE"#))
            .execute(&pool)
            .await
            .expect("drop isolated test schema");

        assert_eq!(changed, 1);
        assert_eq!(rows, vec![(1, true), (2, false), (3, false)]);
    }

    #[tokio::test]
    async fn download_transition_and_checkpoint_commit_or_roll_back_together() {
        const TEST_NAME: &str = "download_transition_and_checkpoint_commit_or_roll_back_together";
        let Some((pool, schema)) = isolated_pool(TEST_NAME).await else {
            return;
        };
        let operation_id = Uuid::new_v4();
        sqlx::query(&format!(
            r#"
            CREATE TABLE "{schema}"."Downloads" (
                "Id" bigint PRIMARY KEY,
                "CacheHitBytes" bigint NOT NULL,
                "CacheMissBytes" bigint NOT NULL,
                "IsActive" boolean NOT NULL,
                "IsEvicted" boolean NOT NULL
            )
            "#
        ))
        .execute(&pool)
        .await
        .expect("create atomic download fixture");
        sqlx::query(&format!(
            r#"
            CREATE TABLE "{schema}"."EvictionScanCheckpoints" (
                "OperationId" uuid PRIMARY KEY,
                "Processed" integer NOT NULL,
                "Evicted" integer NOT NULL,
                "UnEvicted" integer NOT NULL,
                "StartedAtUtc" timestamptz NOT NULL,
                "FinalizedAtUtc" timestamptz NULL
            )
            "#
        ))
        .execute(&pool)
        .await
        .expect("create atomic checkpoint fixture");
        sqlx::query(&format!(
            r#"
            INSERT INTO "{schema}"."Downloads"
                ("Id", "CacheHitBytes", "CacheMissBytes", "IsActive", "IsEvicted")
            VALUES (1, 10, 20, false, false)
            "#
        ))
        .execute(&pool)
        .await
        .expect("seed atomic download fixture");
        sqlx::query(&format!(
            r#"
            INSERT INTO "{schema}"."EvictionScanCheckpoints"
                ("OperationId", "Processed", "Evicted", "UnEvicted", "StartedAtUtc")
            VALUES ($1::uuid, 0, 0, 0, NOW())
            "#
        ))
        .bind(operation_id.hyphenated().to_string())
        .execute(&pool)
        .await
        .expect("seed atomic checkpoint fixture");

        let judged = [(1_i64, 10_i64, 20_i64)];
        let mut rollback = pool.begin().await.expect("begin rollback transaction");
        sqlx::query(&format!(r#"SET LOCAL search_path TO "{schema}""#))
            .execute(&mut *rollback)
            .await
            .expect("select rollback schema");
        assert_eq!(
            evict_unchanged_downloads_tx(&mut rollback, &judged)
                .await
                .unwrap(),
            1
        );
        update_checkpoint_tx(&mut rollback, operation_id, 1, 1, 0)
            .await
            .unwrap();
        rollback.rollback().await.unwrap();

        let rolled_back: (bool, i32, i32) = sqlx::query_as(&format!(
            r#"
            SELECT d."IsEvicted", c."Processed", c."Evicted"
            FROM "{schema}"."Downloads" d
            CROSS JOIN "{schema}"."EvictionScanCheckpoints" c
            WHERE d."Id" = 1
            "#
        ))
        .fetch_one(&pool)
        .await
        .unwrap();
        assert_eq!(rolled_back, (false, 0, 0));

        let mut committed = pool.begin().await.expect("begin commit transaction");
        sqlx::query(&format!(r#"SET LOCAL search_path TO "{schema}""#))
            .execute(&mut *committed)
            .await
            .expect("select commit schema");
        assert_eq!(
            evict_unchanged_downloads_tx(&mut committed, &judged)
                .await
                .unwrap(),
            1
        );
        update_checkpoint_tx(&mut committed, operation_id, 1, 1, 0)
            .await
            .unwrap();
        committed.commit().await.unwrap();

        let committed_row: (bool, i32, i32) = sqlx::query_as(&format!(
            r#"
            SELECT d."IsEvicted", c."Processed", c."Evicted"
            FROM "{schema}"."Downloads" d
            CROSS JOIN "{schema}"."EvictionScanCheckpoints" c
            WHERE d."Id" = 1
            "#
        ))
        .fetch_one(&pool)
        .await
        .unwrap();

        let retry_id = Uuid::new_v4();
        sqlx::query(&format!(
            r#"
            INSERT INTO "{schema}"."EvictionScanCheckpoints"
                ("OperationId", "Processed", "Evicted", "UnEvicted", "StartedAtUtc")
            VALUES ($1::uuid, 0, 0, 0, NOW())
            "#
        ))
        .bind(retry_id.hyphenated().to_string())
        .execute(&pool)
        .await
        .unwrap();
        let mut retry = pool.begin().await.unwrap();
        sqlx::query(&format!(r#"SET LOCAL search_path TO "{schema}""#))
            .execute(&mut *retry)
            .await
            .unwrap();
        let repeated_transition = evict_unchanged_downloads_tx(&mut retry, &judged)
            .await
            .unwrap();
        update_checkpoint_tx(&mut retry, retry_id, 1, repeated_transition as usize, 0)
            .await
            .unwrap();
        retry.commit().await.unwrap();
        let attempt_rows: Vec<(String, i32, i32)> = sqlx::query_as(&format!(
            r#"
            SELECT "OperationId"::text, "Processed", "Evicted"
            FROM "{schema}"."EvictionScanCheckpoints"
            ORDER BY "StartedAtUtc", "OperationId"
            "#
        ))
        .fetch_all(&pool)
        .await
        .unwrap();
        assert_eq!(repeated_transition, 0);
        assert!(attempt_rows.contains(&(operation_id.to_string(), 1, 1)));
        assert!(attempt_rows.contains(&(retry_id.to_string(), 1, 0)));

        sqlx::query(&format!(
            r#"UPDATE "{schema}"."Downloads" SET "IsEvicted" = false WHERE "Id" = 1"#
        ))
        .execute(&pool)
        .await
        .unwrap();
        let missing_id = Uuid::new_v4();
        let mut missing = pool.begin().await.unwrap();
        sqlx::query(&format!(r#"SET LOCAL search_path TO "{schema}""#))
            .execute(&mut *missing)
            .await
            .unwrap();
        assert_eq!(
            evict_unchanged_downloads_tx(&mut missing, &judged)
                .await
                .unwrap(),
            1
        );
        assert!(update_checkpoint_tx(&mut missing, missing_id, 1, 1, 0)
            .await
            .is_err());
        missing.rollback().await.unwrap();
        let after_missing: bool = sqlx::query_scalar(&format!(
            r#"SELECT "IsEvicted" FROM "{schema}"."Downloads" WHERE "Id" = 1"#
        ))
        .fetch_one(&pool)
        .await
        .unwrap();
        assert!(!after_missing);

        let finalized_id = Uuid::new_v4();
        sqlx::query(&format!(
            r#"
            INSERT INTO "{schema}"."EvictionScanCheckpoints"
                ("OperationId", "Processed", "Evicted", "UnEvicted", "StartedAtUtc", "FinalizedAtUtc")
            VALUES ($1::uuid, 0, 0, 0, NOW(), NOW())
            "#
        ))
        .bind(finalized_id.hyphenated().to_string())
        .execute(&pool)
        .await
        .unwrap();
        let mut finalized = pool.begin().await.unwrap();
        sqlx::query(&format!(r#"SET LOCAL search_path TO "{schema}""#))
            .execute(&mut *finalized)
            .await
            .unwrap();
        assert_eq!(
            evict_unchanged_downloads_tx(&mut finalized, &judged)
                .await
                .unwrap(),
            1
        );
        assert!(update_checkpoint_tx(&mut finalized, finalized_id, 1, 1, 0)
            .await
            .is_err());
        finalized.rollback().await.unwrap();
        let after_finalized: bool = sqlx::query_scalar(&format!(
            r#"SELECT "IsEvicted" FROM "{schema}"."Downloads" WHERE "Id" = 1"#
        ))
        .fetch_one(&pool)
        .await
        .unwrap();
        assert!(!after_finalized);

        sqlx::query(&format!(r#"DROP SCHEMA "{schema}" CASCADE"#))
            .execute(&pool)
            .await
            .unwrap();

        assert_eq!(committed_row, (true, 1, 1));
    }

    #[test]
    fn omitted_key_scheme_keeps_monolithic_default() {
        let config: DatasourceConfig =
            serde_json::from_str(r#"{"name":"default","cachePath":"cache","isDefault":true}"#)
                .unwrap();

        assert_eq!(
            cache_utils::CacheKeyScheme::from_config_str(&config.key_scheme),
            cache_utils::CacheKeyScheme::Monolithic
        );
    }

    #[test]
    fn orphaned_evicted_download_clears_stale_flag() {
        // Shape (a): no probe keys at all + currently evicted → self-heal by clearing.
        assert_eq!(
            classify_unverifiable(false, false, true),
            UnverifiableAction::ClearStaleFlag
        );
    }

    #[test]
    fn orphaned_not_evicted_download_abstains() {
        // No keys but not evicted: nothing to clear.
        assert_eq!(
            classify_unverifiable(false, false, false),
            UnverifiableAction::Abstain
        );
    }

    #[test]
    fn offline_mount_evicted_download_abstains() {
        // Shape (b): has probe keys but no indexed root this scan. A genuine eviction must
        // NOT be flapped off during a transient mount outage — abstain and let the
        // positive-evidence path resolve it when the root returns.
        assert_eq!(
            classify_unverifiable(true, false, true),
            UnverifiableAction::Abstain
        );
    }

    #[test]
    fn offline_mount_not_evicted_download_abstains() {
        assert_eq!(
            classify_unverifiable(true, false, false),
            UnverifiableAction::Abstain
        );
    }

    #[test]
    fn unsupported_bare_metal_recipe_clears_preexisting_evicted_flag() {
        assert_eq!(
            classify_unverifiable(true, true, true),
            UnverifiableAction::ClearStaleFlag
        );
        assert_eq!(
            classify_unverifiable(true, true, false),
            UnverifiableAction::Abstain
        );
    }

    #[test]
    fn positive_hit_overrides_strict_mixed_key_absence_policy() {
        // One key hit is conclusive presence even though another bare-metal key makes an
        // all-keys absence decision unverifiable.
        assert_eq!(
            classify_download(true, false, true, true, true, true),
            DownloadAction::Unevict
        );
        assert_eq!(
            classify_download(true, false, true, true, false, true),
            DownloadAction::NoOp
        );
    }

    // --- classify_verifiable: full truth table (has_cache_file × is_evicted × was_cached) ---

    #[test]
    fn absent_was_cached_not_evicted_evicts() {
        // Cached then gone → genuine nginx eviction.
        assert_eq!(
            classify_verifiable(false, false, true),
            VerifiableAction::Evict
        );
    }

    #[test]
    fn absent_was_cached_already_evicted_noops() {
        // Already flagged correctly; nothing to change.
        assert_eq!(
            classify_verifiable(false, true, true),
            VerifiableAction::NoOp
        );
    }

    #[test]
    fn absent_never_cached_evicted_heals() {
        // THE BUG FIX: absent + never cached (pure MISS) but flagged → heal the false positive.
        assert_eq!(
            classify_verifiable(false, true, false),
            VerifiableAction::Unevict
        );
    }

    #[test]
    fn absent_never_cached_not_evicted_noops() {
        // Never cached and not flagged → never stamp "Evicted" on never-cached content.
        assert_eq!(
            classify_verifiable(false, false, false),
            VerifiableAction::NoOp
        );
    }

    #[test]
    fn present_evicted_unevicts() {
        // Content back on disk (re-cached) → clear the stale flag. was_cached is irrelevant.
        assert_eq!(
            classify_verifiable(true, true, true),
            VerifiableAction::Unevict
        );
        assert_eq!(
            classify_verifiable(true, true, false),
            VerifiableAction::Unevict
        );
    }

    #[test]
    fn present_not_evicted_noops() {
        // Content present and not flagged → no change. was_cached is irrelevant.
        assert_eq!(
            classify_verifiable(true, false, true),
            VerifiableAction::NoOp
        );
        assert_eq!(
            classify_verifiable(true, false, false),
            VerifiableAction::NoOp
        );
    }

    // --- download_was_cached: per-scheme MISS-byte evidence ---

    #[test]
    fn bare_metal_miss_only_bytes_are_not_cached_evidence() {
        // Bare-metal nginx logs MISS for responses it never writes to cache, so miss-only
        // bytes must not make the download evictable.
        let keys = [scheme_key("bare_metal")];
        let was_cached = download_was_cached(0, 500, &keys);
        assert!(!was_cached);
        assert_eq!(
            classify_download(false, true, true, false, false, was_cached),
            DownloadAction::NoOp
        );
    }

    #[test]
    fn bare_metal_hit_bytes_keep_download_evictable() {
        // A HIT can only be served from a file that existed on disk, on every scheme.
        let keys = [scheme_key("bare_metal")];
        assert!(download_was_cached(1, 0, &keys));
        assert!(download_was_cached(1, 500, &keys));
        assert_eq!(
            classify_download(false, true, true, false, false, true),
            DownloadAction::Evict
        );
    }

    #[test]
    fn monolithic_miss_only_bytes_remain_cached_evidence() {
        // Monolithic lancache writes the content to cache on MISS, so pure-MISS downloads
        // (the common downloaded-exactly-once case) stay detectable as evicted.
        let keys = [scheme_key("monolithic")];
        let was_cached = download_was_cached(0, 500, &keys);
        assert!(was_cached);
        assert!(!download_was_cached(0, 0, &keys));
        assert_eq!(
            classify_download(false, true, true, false, false, was_cached),
            DownloadAction::Evict
        );
    }

    #[test]
    fn mixed_scheme_miss_only_bytes_are_not_cached_evidence() {
        // Any bare-metal key disqualifies MISS bytes for the whole download (fail-closed);
        // HIT bytes still count.
        let keys = [scheme_key("monolithic"), scheme_key("bare_metal")];
        assert!(!download_was_cached(0, 500, &keys));
        assert!(download_was_cached(3, 500, &keys));
    }
}
