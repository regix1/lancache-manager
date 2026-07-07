use anyhow::{Context, Result};
use clap::Parser;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use serde_json::json;
use sqlx::{Postgres, Transaction, Row};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

mod cache_utils;
mod cache_eviction_paths;
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

/// What to do with a download whose files could NOT be positively verified this scan
/// (none of its probe keys resolved to an on-disk indexed datasource root).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum UnverifiableAction {
    /// Orphaned shape: the download produced NO probe keys at all (no usable log entries —
    /// no rows, or every row's Service/Url was NULL). A stale `IsEvicted` flag here is an
    /// artifact (e.g. a shared-depot removal rewrote its lines out of access.log), so we
    /// clear it to self-heal.
    ClearStaleFlag,
    /// Offline-mount shape: the download HAS probe keys, but every key resolves to a
    /// datasource cache root that isn't indexed right now (relocated / offline / removed
    /// mount). We have no evidence either way, so we leave the flag untouched. It resolves
    /// via the normal positive-evidence path once the root returns. Also the no-op outcome
    /// for a not-currently-evicted download.
    Abstain,
}

/// Pure classification for the unverifiable branch of the eviction scan. Splitting the two
/// unverifiable shapes here (instead of blindly clearing every unverifiable flag) kills the
/// badge-flap during a transient mount outage while preserving the orphan self-heal.
///
/// `has_probe_keys` is true when the download produced at least one (service, url) probe key
/// this scan; `is_evicted` is its current DB flag. This is only consulted when the download is
/// NOT verifiable, so a `true`/`true` here always means the offline-mount shape.
fn classify_unverifiable(has_probe_keys: bool, is_evicted: bool) -> UnverifiableAction {
    if !has_probe_keys && is_evicted {
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
/// `was_cached` is true when nginx actually served bytes for the download (HIT or MISS - a
/// lancache MISS writes the content to cache, so both prove the content landed on disk).
fn classify_verifiable(has_cache_file: bool, is_evicted: bool, was_cached: bool) -> VerifiableAction {
    if has_cache_file {
        // Content present on disk → never evicted; clear any stale flag (re-cached).
        if is_evicted { VerifiableAction::Unevict } else { VerifiableAction::NoOp }
    } else if was_cached {
        // Absent AND was once cached → genuine nginx eviction.
        if !is_evicted { VerifiableAction::Evict } else { VerifiableAction::NoOp }
    } else {
        // Absent AND never cached (zero bytes served - aborted / metadata-only) → NOT an
        // eviction. Heal a pre-existing false-positive flag.
        if is_evicted { VerifiableAction::Unevict } else { VerifiableAction::NoOp }
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
        eprintln!("[EvictionScan] Warning: failed to seed progress file: {:#}", e);
    }
    reporter.emit_started("signalr.evictionScan.scanning", json!({}));

    match run_scan(&args.datasource_config, progress_path.as_deref(), &reporter).await {
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
            } else {
                reporter.emit_failed(
                    "signalr.evictionScan.error.fatal",
                    json!({ "errorDetail": result.error.clone().unwrap_or_default() }),
                );
            }
            // This final plain-JSON line stays the LAST line on stdout after the progress-event
            // lines above - the C# caller reads only the last stdout line as the ScanResult.
            let json = serde_json::to_string(&result)?;
            println!("{}", json);
            if result.success {
                Ok(())
            } else {
                std::process::exit(1);
            }
        }
        Err(e) => {
            let error_detail = format!("{:#}", e);
            reporter.emit_failed("signalr.evictionScan.error.fatal", json!({ "errorDetail": error_detail.clone() }));
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
            std::process::exit(1);
        }
    }
}

async fn run_scan(datasource_config_path: &str, progress_path: Option<&Path>, reporter: &ProgressReporter) -> Result<ScanResult> {
    // Step 1: Read datasource configuration
    let config_content = std::fs::read_to_string(datasource_config_path)
        .with_context(|| format!("Failed to read datasource config: {}", datasource_config_path))?;
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
    eprintln!("[EvictionScan] Found {} cache files on disk across {} datasource(s)", total_files, datasources.len());

    // Step 3: Connect to database
    let pool = db::create_pool().await?;

    // Count total inactive downloads for progress estimation
    let total_estimate: i64 = sqlx::query_scalar(
        r#"SELECT COUNT(*) FROM "Downloads" WHERE "IsActive" = false"#
    )
    .fetch_one(&pool)
    .await
    .unwrap_or_else(|e| {
        eprintln!("[EvictionScan] Warning: failed to estimate total downloads: {}", e);
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
    // unique tuple is hashed and probed exactly once. Keyed by ProbeKey::memo_key (the
    // md5 digest of the tuple) so the memo holds 16 bytes + bool per unique tuple instead
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
            "#
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
        let download_evicted: HashMap<i64, bool> = download_rows.iter()
            .map(|r| (r.get("download_id"), r.get("is_evicted")))
            .collect();
        // Positive cache evidence per download: any bytes actually served (HIT or MISS). A
        // lancache MISS proxies the content AND writes it to the cache, so MISS bytes are
        // just as much proof the content landed on disk as HIT bytes are - gating on
        // CacheHitBytes alone made every game downloaded exactly once (pure-MISS, the common
        // case) permanently undetectable as evicted. Zero-byte rows (aborted transfers /
        // metadata-only sessions where nothing was ever written) remain excluded.
        let download_served_bytes: HashMap<i64, i64> = download_rows.iter()
            .map(|r| {
                let hit: i64 = r.get("cache_hit_bytes");
                let miss: i64 = r.get("cache_miss_bytes");
                (r.get("download_id"), hit.saturating_add(miss))
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

            download_keys
                .entry(download_id)
                .or_default()
                .push(cache_eviction_paths::ProbeKey::new(
                    &service,
                    url,
                    datasource.as_deref(),
                    bytes_served.unwrap_or(0),
                    &datasource_roots,
                ));
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
        // A download is only "verifiable" when it has at least one probe key whose
        // datasource cache root was actually indexed this scan (the directory existed
        // and was walked). We assert IsEvicted=true ONLY for a verifiable download whose
        // files are confirmed absent. When a download is NOT verifiable we have zero
        // evidence either way, so we must never evict it — and we proactively CLEAR any
        // stale flag so it self-heals instead of being stuck "Evicted" forever.
        //
        // Two unverifiable shapes both used to stamp false "Evicted" badges on games
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

            // Verifiable iff at least one key could actually be checked against an
            // indexed on-disk root this scan.
            let has_probe_keys = keys.map(|ks| !ks.is_empty()).unwrap_or(false);
            let verifiable = keys
                .map(|ks| ks.iter().any(|key| key.root_is_indexed(&files_on_disk)))
                .unwrap_or(false);

            if !verifiable {
                // Split the two unverifiable shapes: clear a stale flag ONLY for the orphaned
                // (no-probe-keys) shape; ABSTAIN for the offline-mount shape (has keys but no
                // indexed root) so a transient mount outage does not flap the badge. The
                // offline case self-heals via the positive-evidence branch when the root returns.
                match classify_unverifiable(has_probe_keys, is_evicted) {
                    UnverifiableAction::ClearStaleFlag => {
                        ids_to_unevict.push(*download_id);
                        unverifiable_cleared += 1;
                    }
                    UnverifiableAction::Abstain => {}
                }
                continue;
            }

            let has_cache_file = keys
                .expect("verifiable implies the download has probe keys")
                .iter()
                .any(|key| {
                    probe_memo
                        .get(&key.memo_key)
                        .copied()
                        .expect("probe memo must contain every key collected this batch")
                });

            // Gate the evict decision on POSITIVE cache evidence: only content nginx actually
            // served bytes for (HIT or MISS - a MISS writes to cache too) can be "evicted".
            // Absent content with zero served bytes (never written) is healed of any stale
            // flag, not freshly flagged.
            let was_cached = download_served_bytes.get(download_id).copied().unwrap_or(0) > 0;
            match classify_verifiable(has_cache_file, is_evicted, was_cached) {
                VerifiableAction::Evict => ids_to_evict.push(*download_id),
                VerifiableAction::Unevict => ids_to_unevict.push(*download_id),
                VerifiableAction::NoOp => {}
            }
        }

        if unverifiable_cleared > 0 {
            eprintln!(
                "[EvictionScan] Cleared stale IsEvicted on {} orphaned download(s) with no service/url to probe (refusing to keep an Evicted badge we can't verify). Offline-mount downloads (keys present, root not indexed) are left untouched to avoid badge-flap.",
                unverifiable_cleared
            );
        }

        // rust-3: wrap both evict/unevict UPDATEs for this batch in a single transaction
        // so a kill mid-batch cannot partially flag a batch.
        if !ids_to_evict.is_empty() || !ids_to_unevict.is_empty() {
            let mut tx = pool.begin().await.with_context(|| "Failed to begin eviction batch transaction")?;
            if !ids_to_evict.is_empty() {
                update_download_eviction_state_tx(&mut tx, &ids_to_evict, true).await?;
                total_evicted += ids_to_evict.len();
            }
            if !ids_to_unevict.is_empty() {
                update_download_eviction_state_tx(&mut tx, &ids_to_unevict, false).await?;
                total_un_evicted += ids_to_unevict.len();
            }
            tx.commit().await.with_context(|| "Failed to commit eviction batch transaction")?;
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
    use super::{classify_unverifiable, classify_verifiable, UnverifiableAction, VerifiableAction};

    #[test]
    fn orphaned_evicted_download_clears_stale_flag() {
        // Shape (a): no probe keys at all + currently evicted → self-heal by clearing.
        assert_eq!(
            classify_unverifiable(false, true),
            UnverifiableAction::ClearStaleFlag
        );
    }

    #[test]
    fn orphaned_not_evicted_download_abstains() {
        // No keys but not evicted: nothing to clear.
        assert_eq!(
            classify_unverifiable(false, false),
            UnverifiableAction::Abstain
        );
    }

    #[test]
    fn offline_mount_evicted_download_abstains() {
        // Shape (b): has probe keys but no indexed root this scan. A genuine eviction must
        // NOT be flapped off during a transient mount outage — abstain and let the
        // positive-evidence path resolve it when the root returns.
        assert_eq!(
            classify_unverifiable(true, true),
            UnverifiableAction::Abstain
        );
    }

    #[test]
    fn offline_mount_not_evicted_download_abstains() {
        assert_eq!(
            classify_unverifiable(true, false),
            UnverifiableAction::Abstain
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
}
