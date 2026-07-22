use anyhow::{Context, Result};
use clap::Parser;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use serde_json::json;
use sqlx::{Postgres, Row, Transaction};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

mod cache_eviction_paths;
mod cache_utils;
mod cancel;
mod db;
mod progress_events;
mod progress_utils;

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
    let result =
        run_scan_and_report(&args.datasource_config, progress_path.as_deref(), &reporter).await;
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
    reporter: &ProgressReporter,
) -> Result<()> {
    match run_scan(datasource_config, progress_path, reporter).await {
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
    reporter: &ProgressReporter,
) -> Result<ScanResult> {
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
        return Ok(ScanResult {
            success: true,
            processed: 0,
            evicted: 0,
            un_evicted: 0,
            files_on_disk: 0,
            error: None,
        });
    }

    let datasource_roots = cache_eviction_paths::DatasourceRoots::from_configs(&datasources);

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
    let files_on_disk = cache_eviction_paths::collect_files_on_disk(&datasources, |files_found| {
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
    });

    if files_on_disk.is_empty() {
        eprintln!("[EvictionScan] No cache files found on disk - skipping to prevent false eviction flags");
        return Ok(ScanResult {
            success: true,
            processed: 0,
            evicted: 0,
            un_evicted: 0,
            files_on_disk: 0,
            error: None,
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
                   "CacheHitBytes" as cache_hit_bytes, "CacheMissBytes" as cache_miss_bytes
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
            AND "Service" IS NOT NULL AND "Url" IS NOT NULL
            GROUP BY "DownloadId", "Service", "Url", "Datasource"
            "#
        )
        .bind(&download_ids)
        .fetch_all(&pool)
        .await
        .with_context(|| "Failed to fetch log entries")?;

        // Group probe keys by download_id
        let mut download_keys: HashMap<i64, Vec<cache_eviction_paths::ProbeKey>> = HashMap::new();
        for row in &log_rows {
            let download_id: i64 = row.get("download_id");
            let service: String = row.get("service");
            let url: String = row.get("url");
            let datasource: Option<String> = row.get("datasource");
            // MAX() is typed nullable; NULL means no usable size → 0 → probe-chunk floor.
            let bytes_served: Option<i64> = row.get("bytes_served");

            download_keys.entry(download_id).or_default().push(
                cache_eviction_paths::ProbeKey::new(
                    &service,
                    url,
                    datasource.as_deref(),
                    bytes_served.unwrap_or(0),
                    &datasource_roots,
                ),
            );
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
            .map(|(memo_key, key)| (memo_key, key.has_cache_file(&files_on_disk)))
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

        for download_id in &download_ids {
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
            let can_verify_absence =
                cache_eviction_paths::keys_can_verify_absence(keys_for_probe, &files_on_disk);
            let has_unknown_recipe = keys_for_probe
                .iter()
                .any(cache_eviction_paths::ProbeKey::has_unknown_recipe);
            let (hit_bytes, miss_bytes) = download_cache_bytes
                .get(download_id)
                .copied()
                .unwrap_or((0, 0));
            let was_cached = download_was_cached(hit_bytes, miss_bytes, keys_for_probe);
            match classify_download(
                has_cache_file,
                can_verify_absence,
                has_probe_keys,
                has_unknown_recipe,
                is_evicted,
                was_cached,
            ) {
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

        // rust-3: wrap both evict/unevict UPDATEs for this batch in a single transaction
        // so a kill mid-batch cannot partially flag a batch.
        if !ids_to_evict.is_empty() || !ids_to_unevict.is_empty() {
            let mut tx = pool
                .begin()
                .await
                .with_context(|| "Failed to begin eviction batch transaction")?;
            if !ids_to_evict.is_empty() {
                update_download_eviction_state_tx(&mut tx, &ids_to_evict, true).await?;
                total_evicted += ids_to_evict.len();
            }
            if !ids_to_unevict.is_empty() {
                update_download_eviction_state_tx(&mut tx, &ids_to_unevict, false).await?;
                total_un_evicted += ids_to_unevict.len();
            }
            tx.commit()
                .await
                .with_context(|| "Failed to commit eviction batch transaction")?;
        }

        total_processed += batch_count;

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
    })
}

/// Transaction-scoped variant used by the per-batch transaction in run_scan (rust-3).
/// Executes within a caller-owned transaction so that evict + unevict UPDATEs for the
/// same batch are atomic.
async fn update_download_eviction_state_tx(
    tx: &mut Transaction<'_, Postgres>,
    ids: &[i64],
    is_evicted: bool,
) -> Result<()> {
    let error_context = if is_evicted {
        "Failed to update evicted downloads (tx)"
    } else {
        "Failed to update un-evicted downloads (tx)"
    };

    sqlx::query(r#"UPDATE "Downloads" SET "IsEvicted" = $1 WHERE "Id" = ANY($2)"#)
        .bind(is_evicted)
        .bind(ids)
        .execute(&mut **tx)
        .await
        .with_context(|| error_context)?;

    Ok(())
}

/// Writes the (optional) progress file exactly as before, then emits a stdout progress event via
/// `reporter` (file write first). Every checkpoint in this file uses status "running" - the
/// terminal started/complete/failed events are emitted separately in `main()` from the real
/// `ScanResult`, so this always maps to `emit_progress`.
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
        cache_eviction_paths, cache_utils, classify_download, classify_unverifiable,
        classify_verifiable, download_was_cached, DatasourceConfig, DownloadAction,
        UnverifiableAction, VerifiableAction,
    };

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
