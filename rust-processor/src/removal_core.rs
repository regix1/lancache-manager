//! Shared cache-removal core for the per-service removal bins.
//!
//! All five removal bins (`cache_steam_remove`, `cache_epic_remove`,
//! `cache_blizzard_remove`, `cache_riot_remove`, `cache_xbox_remove`) share an
//! almost-identical TAIL: collect on-disk slices → parallel delete with progress →
//! clean up empty directories → purge access.log → permission-error gate → delete
//! DB rows → write report. Only the HEAD differs (how each service maps its identity
//! to a `HashMap<url, (service, bytes)>`) plus one tail wrinkle unique to Steam (the
//! access.log purge is depot-scoped, not url-only). This module owns the shared tail;
//! each bin owns its head and hands the tail a `RemovalPlan`.
//!
//! Behavior is byte-identical to the pre-consolidation bins:
//!   * `remove_cache_files` walks every on-disk slice via the scheme-aware
//!     `cache_utils::existing_keyed_paths_for_url_with_scheme` dispatcher,
//!   * progress is emitted in the 10%-70% band using each service's own stage keys,
//!   * the `ProgressCadence` enum reproduces the two existing emit cadences verbatim
//!     (Steam = every integer-percent advance OR every 8th probe; Epic/named =
//!     every integer-percent advance only),
//!   * the `LogScope` enum reproduces the two existing access.log purge predicates
//!     (Steam = url ∪ safe-depot-id; Epic/named = url-only).

use anyhow::Result;
use serde::Serialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};

use crate::cache_utils;
use crate::cancel;
use crate::log_purge;
use crate::progress_events::ProgressReporter;
use crate::progress_utils;

/// Progress JSON written to the progress file and tailed by the C# poller.
/// Identical shape (and camelCase field names) to every removal bin's prior
/// local `ProgressData` struct, so the frontend contract is unchanged.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgressData {
    pub status: String,
    pub stage_key: String,
    pub context: serde_json::Value,
    #[serde(rename = "percentComplete")]
    pub percent_complete: f64,
    #[serde(rename = "filesProcessed")]
    pub files_processed: usize,
    #[serde(rename = "totalFiles")]
    pub total_files: usize,
    pub timestamp: String,
}

/// Write a single progress entry to `progress_path`. Mirrors the prior per-bin
/// `write_progress` helper verbatim (same field population, same timestamp source),
/// then emits the matching stdout event via `reporter` — file write ALWAYS happens
/// first (mirrors cache_game_detect.rs's checkpoint ordering), so a stdout-triggered
/// C# file re-read is never stale. `status` selects the emit method: "starting" ->
/// emit_started, "completed" -> emit_complete, "failed" -> emit_failed, anything else
/// (querying_database/removing_cache/cleaning_directories/removing_logs/removing_database)
/// -> emit_progress. `reporter` no-ops every emit call when `--progress` was not passed.
pub fn write_progress(
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

/// Per-service progress stage keys. These are a "LOCKED CONTRACT" with the frontend
/// i18n + SignalR layer, so the core never invents keys — each bin passes its own
/// (Steam = `signalr.gameRemove.*`, Epic = `signalr.epicRemove.*`, named services
/// reuse `signalr.gameRemove.*`). Only the keys the core itself emits live here; the
/// per-bin `main` keeps emitting the remaining lifecycle keys with the same strings.
pub struct RemovalStageKeys {
    /// Emitted from inside `remove_cache_files` on every progress tick.
    pub cache_file_progress: &'static str,
}

/// How the access.log purge is scoped. Steam removal narrows the purge to lines
/// whose depot id is exclusively owned by the target game (cross-game safety);
/// every other service purges url-only.
///
/// `#[allow(dead_code)]`: each variant / API item below is used by SOME removal bin
/// but not all, and every bin compiles `removal_core` independently (no lib crate),
/// so per-crate dead-code analysis flags the items a given bin does not touch (e.g.
/// Steam constructs neither `LogScope` nor `purge_log_entries`; Epic/named never use
/// the `OnPercentAdvanceOrEveryEighth` cadence). This mirrors the per-bin
/// `#[allow(dead_code)]` pattern already used in `log_purge.rs`.
#[allow(dead_code)]
pub enum LogScope {
    /// Epic / Blizzard / Riot / Xbox: remove lines whose URL is in the removal set.
    Urls,
    /// Steam: remove lines whose URL is in the set OR whose depot id is in the
    /// (already cross-game-narrowed) `safe_depot_ids` set.
    UrlsAndDepots(HashSet<u32>),
}

/// How often `remove_cache_files` emits a progress entry. Reproduces the two
/// distinct cadences that existed before consolidation so event volume is unchanged.
/// See the `LogScope` note above for why `#[allow(dead_code)]` is needed here.
#[derive(Clone, Copy)]
#[allow(dead_code)]
pub enum ProgressCadence {
    /// Epic / named: write only when the integer percent advances.
    OnPercentAdvance,
    /// Steam: write on an integer-percent advance OR every 8th probed file, so small
    /// games still emit motion inside the brief poll window.
    OnPercentAdvanceOrEveryEighth,
}

/// Outcome of the cache-file deletion phase.
pub struct CacheRemovalOutcome {
    pub deleted_files: usize,
    pub bytes_freed: u64,
    pub parent_dirs: HashSet<PathBuf>,
    pub permission_errors: usize,
    /// Bare-metal candidates whose embedded KEY header did not match (or could not be
    /// read): left untouched. Some bins consume this to stop before deleting provenance.
    #[allow(dead_code)]
    pub verification_skips: usize,
}

/// Parallel cache-file deletion with progress reporting (the 10%-70% band).
///
/// Identical to the prior per-bin `remove_cache_files_for_*` bodies: collect every
/// on-disk slice for each (service, url) via `existing_cache_paths_for_url`, then
/// rayon-delete with a symlink/escape guard, atomic counters, cooperative cancel,
/// and a permission-error tally. The only parameterized difference is `cadence`,
/// which selects between the two pre-existing emit frequencies.
pub fn remove_cache_files(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    progress_path: &Path,
    reporter: &ProgressReporter,
    keys: &RemovalStageKeys,
    cadence: ProgressCadence,
    scheme: cache_utils::CacheKeyScheme,
) -> Result<CacheRemovalOutcome> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
    use std::sync::Mutex;

    let deleted_files = AtomicUsize::new(0);
    let bytes_freed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let verification_skips = AtomicUsize::new(0);
    let parent_dirs = Mutex::new(HashSet::new());

    eprintln!("Collecting cache file paths for deletion...");

    // Collect all paths to delete. All-slice existence walk (matches detection
    // coverage) instead of the size-derived candidate list, so range-served objects
    // that log each ~1 MiB range as a separate row are fully enumerated rather than
    // truncated to slice 0. The walk stat-probes every on-disk slice for the URL, so
    // `total_bytes` is no longer needed here. Under the bare-metal scheme each
    // candidate carries the literal key it must prove before deletion.
    let paths_to_check: Vec<(std::path::PathBuf, Option<String>)> = url_data
        .par_iter()
        .flat_map(|(url, (service, _total_bytes))| {
            cache_utils::existing_keyed_paths_for_url_with_scheme(scheme, cache_dir, service, url)
        })
        .collect();

    let total_paths = paths_to_check.len();
    eprintln!("Checking {} potential cache file locations...", total_paths);

    let paths_checked = AtomicUsize::new(0);
    let last_reported_percent = AtomicUsize::new(0);

    // Parallel deletion with progress reporting
    paths_to_check.par_iter().for_each(|(path, expected_key)| {
        // Cooperative cancellation: skip remaining files if cancel was requested.
        // Already-deleted files stay deleted — consistent partial state that C# reconciles.
        if cancel::is_cancelled() {
            return;
        }

        let checked = paths_checked.fetch_add(1, Ordering::Relaxed) + 1;

        if path.exists() {
            // Refuse to follow symlinks or delete anything outside the cache root.
            if let Err(e) = cache_utils::safe_path_under_root(cache_dir, path) {
                eprintln!("  skipping unsafe path {}: {}", path.display(), e);
                return;
            }

            // Bare-metal deletion gate: the file itself must prove it holds the
            // recipe-computed key. A mismatch, unreadable header, or unexpectedly
            // absent expected key means the recipe and disk disagree (customized
            // vhost, Vary variant, foreign file) — never delete on doubt. Keep going
            // to the progress block after a skip so a fully skipped batch can still
            // report that every candidate was processed.
            let verified_for_deletion = match scheme {
                cache_utils::CacheKeyScheme::Monolithic => true,
                cache_utils::CacheKeyScheme::BareMetal => expected_key
                    .as_deref()
                    .and_then(|expected| cache_utils::cache_file_key_matches(path, expected))
                    == Some(true),
            };

            if !verified_for_deletion {
                let skips = verification_skips.fetch_add(1, Ordering::Relaxed) + 1;
                if skips <= 5 {
                    eprintln!(
                        "  skipping {}: embedded KEY did not verify against the computed key",
                        path.display()
                    );
                }
            } else {
                if let Ok(metadata) = fs::metadata(path) {
                    bytes_freed.fetch_add(metadata.len(), Ordering::Relaxed);
                }

                match fs::remove_file(path) {
                    Ok(_) => {
                        let count = deleted_files.fetch_add(1, Ordering::Relaxed) + 1;

                        if let Some(parent) = path.parent() {
                            match parent_dirs.lock() {
                                Ok(mut dirs) => {
                                    dirs.insert(parent.to_path_buf());
                                }
                                Err(err) => {
                                    eprintln!("  Warning: failed to track parent directory after delete: {}", err);
                                }
                            }
                        }

                        if count % 100 == 0 {
                            let bytes = bytes_freed.load(Ordering::Relaxed);
                            eprintln!(
                                "  Deleted {} cache files... ({:.2} MB freed)",
                                count,
                                bytes as f64 / 1_048_576.0
                            );
                        }
                    }
                    Err(e) => {
                        if e.kind() == std::io::ErrorKind::PermissionDenied {
                            let err_count = permission_errors.fetch_add(1, Ordering::Relaxed) + 1;
                            if err_count <= 5 {
                                eprintln!("  ERROR: Permission denied deleting {}: {}", path.display(), e);
                            }
                        }
                    }
                }
            }
        }

        // Report progress (10% - 70% range during cache removal).
        if total_paths > 0 {
            let current_pct = (checked * 100) / total_paths;
            let prev_pct = last_reported_percent.load(Ordering::Relaxed);
            let advanced_percent = current_pct > prev_pct;
            let should_write = match cadence {
                ProgressCadence::OnPercentAdvance => {
                    advanced_percent
                        && last_reported_percent
                            .compare_exchange(prev_pct, current_pct, Ordering::SeqCst, Ordering::Relaxed)
                            .is_ok()
                }
                ProgressCadence::OnPercentAdvanceOrEveryEighth => {
                    // Write on EITHER an integer-percent advance OR every 8th file
                    // probed, so small games still emit motion during the short
                    // window where the C# poller (500ms) can observe updates.
                    let every_n_files = checked & 0x7 == 0; // every 8 files
                    if advanced_percent || every_n_files {
                        if advanced_percent {
                            last_reported_percent
                                .compare_exchange(prev_pct, current_pct, Ordering::SeqCst, Ordering::Relaxed)
                                .is_ok()
                        } else {
                            true
                        }
                    } else {
                        false
                    }
                }
            };
            if should_write {
                let overall_percent = 10.0 + (checked as f64 / total_paths as f64) * 60.0;
                let del_count = deleted_files.load(Ordering::Relaxed);
                let _ = bytes_freed.load(Ordering::Relaxed);
                let _ = write_progress(
                    progress_path,
                    reporter,
                    "removing_cache",
                    keys.cache_file_progress,
                    json!({ "n": del_count, "total": total_paths }),
                    overall_percent,
                    del_count,
                    total_paths,
                );
            }
        }
    });

    let final_deleted = deleted_files.load(Ordering::Relaxed);
    let final_bytes = bytes_freed.load(Ordering::Relaxed);
    let final_dirs = match parent_dirs.into_inner() {
        Ok(dirs) => dirs,
        Err(err) => {
            eprintln!(
                "  Warning: parent directory tracker was poisoned; continuing with recovered set"
            );
            err.into_inner()
        }
    };
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);

    if final_permission_errors > 5 {
        eprintln!(
            "  ... and {} more permission errors",
            final_permission_errors - 5
        );
    }
    if final_permission_errors > 0 {
        eprintln!("  Total permission errors: {}", final_permission_errors);
    }
    let final_verification_skips = verification_skips.load(Ordering::Relaxed);
    if final_verification_skips > 0 {
        eprintln!(
            "  Left {} file(s) untouched because their embedded KEY did not verify against the computed key",
            final_verification_skips
        );
    }

    // After the parallel deletion phase: flush partial progress on cancel.
    if cancel::is_cancelled() {
        eprintln!("Cancellation requested — flushing partial progress and stopping.");
        let _ = write_progress(
            progress_path,
            reporter,
            "removing_cache",
            keys.cache_file_progress,
            json!({ "n": final_deleted, "total": total_paths }),
            10.0 + (paths_checked.load(Ordering::Relaxed) as f64 / total_paths.max(1) as f64)
                * 60.0,
            final_deleted,
            total_paths,
        );
    }

    Ok(CacheRemovalOutcome {
        deleted_files: final_deleted,
        bytes_freed: final_bytes,
        parent_dirs: final_dirs,
        permission_errors: final_permission_errors,
        verification_skips: final_verification_skips,
    })
}

/// Run the access.log purge for the chosen scope. Steam narrows to safe depot ids;
/// every other service is url-only. (Steam calls `log_purge::remove_log_entries_for_game`
/// directly so it can also pass a per-file progress callback, so this helper is unused
/// in the Steam crate — see the `LogScope` `#[allow(dead_code)]` note above.)
#[allow(dead_code)]
pub fn purge_log_entries(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    scope: &LogScope,
) -> Result<(u64, usize)> {
    match scope {
        LogScope::Urls => log_purge::remove_log_entries_for_urls(log_dir, urls_to_remove),
        LogScope::UrlsAndDepots(safe_depot_ids) => {
            log_purge::remove_log_entries_for_game(log_dir, urls_to_remove, safe_depot_ids, None)
        }
    }
}

/// Build the PUID/PGID permission-error abort message shared by every removal bin.
/// Returned so the caller can `eprintln!` it, write the report with `failed` status,
/// and `bail!` with the same text (identical to the prior per-bin logic).
pub fn permission_error_message(
    total_permission_errors: usize,
    cache_permission_errors: usize,
    log_permission_errors: usize,
) -> String {
    let puid = std::env::var("PUID").unwrap_or_else(|_| "1000".to_string());
    let pgid = std::env::var("PGID").unwrap_or_else(|_| "1000".to_string());
    format!(
        "ABORTED: Cannot delete database records because {} file(s) could not be modified due to permission errors. \
        This is likely caused by incorrect PUID/PGID settings. The lancache container is configured to run as UID/GID {}:{}. \
        Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership. \
        Cache permission errors: {}, Log permission errors: {}",
        total_permission_errors, puid, pgid, cache_permission_errors, log_permission_errors
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    static TEST_STAGE_KEYS: RemovalStageKeys = RemovalStageKeys {
        cache_file_progress: "test.cache.remove",
    };

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

    fn remove_one(
        cache_dir: &Path,
        service: &str,
        url: &str,
        scheme: cache_utils::CacheKeyScheme,
        progress_path: &Path,
    ) -> CacheRemovalOutcome {
        let url_data = HashMap::from([(url.to_string(), (service.to_string(), 0_i64))]);
        remove_cache_files(
            cache_dir,
            &url_data,
            progress_path,
            &ProgressReporter::new(false),
            &TEST_STAGE_KEYS,
            ProgressCadence::OnPercentAdvance,
            scheme,
        )
        .unwrap()
    }

    #[test]
    fn bare_metal_mismatch_or_unreadable_key_is_skipped_and_progress_completes() {
        for embedded_key in [Some("wrong-key"), None] {
            let temp = tempfile::tempdir().unwrap();
            let url = "/depot/1/chunk/abcdef";
            let expected_key = cache_utils::bare_metal_object_key_base("steam", url).unwrap();
            let cache_path = cache_utils::cache_path_for_digest(
                temp.path(),
                cache_utils::calculate_md5_digest(&expected_key),
            );
            write_cache_file(&cache_path, embedded_key);

            let progress_path = temp.path().join("progress.json");
            let outcome = remove_one(
                temp.path(),
                "steam",
                url,
                cache_utils::CacheKeyScheme::BareMetal,
                &progress_path,
            );

            assert!(cache_path.exists(), "unverified file must remain untouched");
            assert_eq!(outcome.deleted_files, 0);
            assert_eq!(outcome.bytes_freed, 0);
            assert_eq!(outcome.verification_skips, 1);

            let progress: serde_json::Value =
                serde_json::from_slice(&fs::read(&progress_path).unwrap()).unwrap();
            assert_eq!(progress["percentComplete"].as_f64(), Some(70.0));
            assert_eq!(progress["totalFiles"].as_u64(), Some(1));
        }
    }

    #[test]
    fn bare_metal_exact_key_match_allows_deletion() {
        let temp = tempfile::tempdir().unwrap();
        let url = "/depot/1/chunk/abcdef";
        let expected_key = cache_utils::bare_metal_object_key_base("steam", url).unwrap();
        let cache_path = cache_utils::cache_path_for_digest(
            temp.path(),
            cache_utils::calculate_md5_digest(&expected_key),
        );
        write_cache_file(&cache_path, Some(&expected_key));

        let outcome = remove_one(
            temp.path(),
            "steam",
            url,
            cache_utils::CacheKeyScheme::BareMetal,
            &temp.path().join("progress.json"),
        );

        assert!(!cache_path.exists());
        assert_eq!(outcome.deleted_files, 1);
        assert_eq!(outcome.verification_skips, 0);
    }

    #[test]
    fn monolithic_deletion_does_not_require_key_header() {
        let temp = tempfile::tempdir().unwrap();
        let url = "/depot/1/chunk/abcdef";
        let cache_path = cache_utils::calculate_cache_path_no_range(temp.path(), "steam", url);
        write_cache_file(&cache_path, None);

        let outcome = remove_one(
            temp.path(),
            "steam",
            url,
            cache_utils::CacheKeyScheme::Monolithic,
            &temp.path().join("progress.json"),
        );

        assert!(!cache_path.exists());
        assert_eq!(outcome.deleted_files, 1);
        assert_eq!(outcome.verification_skips, 0);
    }
}
