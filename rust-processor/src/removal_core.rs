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

/// Progress JSON written to the progress file and tailed by the C# poller, and its
/// writer. Both live in `progress_utils` (the neutral home every removal, corruption
/// and log-purge binary already imports) so the 7-field camelCase wire contract
/// C#'s `RustProgressBase` parses exists once.
pub use progress_utils::{write_progress, ProgressData};

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

/// Where the collection walk reports its own progress, and under which stage key. A removal
/// passes None, so its event volume is unchanged; a count run passes Some, because the walk
/// IS the whole run and takes minutes on an entity with many logged URLs.
///
/// `#[allow(dead_code)]`: see the `LogScope` note above. Each removal bin compiles this module
/// independently, and only the bins that offer a count construct this.
#[allow(dead_code)]
pub struct CollectionProgress<'a> {
    pub progress_path: &'a Path,
    pub reporter: &'a ProgressReporter,
    pub stage_key: &'a str,
}

/// Emit one collection-phase tick. Shared by every collection walk so the count's progress
/// shape is identical wherever it runs. The band is 5%-95%: the count's own run is the walk.
#[allow(dead_code)]
pub fn report_collection_progress(
    progress: &CollectionProgress<'_>,
    urls_walked: usize,
    total_urls: usize,
) {
    let _ = write_progress(
        progress.progress_path,
        progress.reporter,
        "counting_files",
        progress.stage_key,
        json!({ "n": urls_walked, "total": total_urls }),
        5.0 + (urls_walked as f64 / total_urls as f64) * 90.0,
        urls_walked,
        total_urls,
    );
}

/// Every on-disk cache slice the removal set covers, existence-filtered through the
/// `cache_utils` chokepoint.
///
/// All-slice existence walk (matches detection coverage) instead of the size-derived
/// candidate list, so range-served objects that log each ~1 MiB range as a separate row
/// are fully enumerated rather than truncated to slice 0. The walk stat-probes every
/// on-disk slice for the URL, so `total_bytes` is not needed here. Under the bare-metal
/// scheme each candidate carries the literal key it must prove before deletion.
///
/// `remove_cache_files` deletes exactly this list, so `collect_cache_paths(..).len()` is
/// the count of files a removal will delete and it reaches no delete loop. Deriving that
/// number any other way would compute a cache key nginx never wrote. [6][7][8]
pub fn collect_cache_paths(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    scheme: cache_utils::CacheKeyScheme,
    progress: Option<&CollectionProgress<'_>>,
) -> Vec<(PathBuf, Option<String>)> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    let total_urls = url_data.len();
    let urls_walked = AtomicUsize::new(0);
    let last_reported_percent = AtomicUsize::new(0);

    url_data
        .par_iter()
        .flat_map(|(url, (service, _total_bytes))| {
            let paths =
                cache_utils::existing_keyed_paths_for_url_with_scheme(scheme, cache_dir, service, url);

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
                    report_collection_progress(progress, walked, total_urls);
                }
            }

            paths
        })
        .collect()
}

/// What a count run writes for the C# side to read. Deliberately not shaped like a removal
/// report: nothing was deleted, so no field claims anything was.
#[derive(Debug, Serialize)]
pub struct CacheFileCount {
    pub entity: String,
    pub cache_files_found: usize,
}

/// A count-only pass: walk exactly the list `remove_cache_files` would delete, report how many
/// of those files exist on disk, and write the count report. The delete loop lives in a
/// different function, so it is unreachable from here, and no path is derived a second way. [6][7][8]
///
/// `#[allow(dead_code)]`: see the `LogScope` note above. Only the bins that offer a count call this.
#[allow(dead_code)]
pub fn count_cache_files(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    output_json: &Path,
    entity: &str,
    scheme: cache_utils::CacheKeyScheme,
    progress: &CollectionProgress<'_>,
) -> Result<usize> {
    let cache_files_found = collect_cache_paths(cache_dir, url_data, scheme, Some(progress)).len();

    fs::write(
        output_json,
        serde_json::to_string_pretty(&CacheFileCount {
            entity: entity.to_string(),
            cache_files_found,
        })?,
    )?;

    eprintln!("Cache files found: {}", cache_files_found);
    Ok(cache_files_found)
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

    // Re-walked from disk here, inside the deleting process, so the set deleted is the set
    // that exists now rather than one an earlier pass recorded.
    let paths_to_check = collect_cache_paths(cache_dir, url_data, scheme, None);

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
    stem_positions: Option<&std::collections::HashMap<String, u64>>,
) -> Result<log_purge::LogRewriteOutcome> {
    match scope {
        LogScope::Urls => {
            log_purge::remove_log_entries_for_urls(log_dir, urls_to_remove, stem_positions)
        }
        LogScope::UrlsAndDepots(safe_depot_ids) => log_purge::remove_log_entries_for_game(
            log_dir,
            urls_to_remove,
            safe_depot_ids,
            None,
            stem_positions,
        ),
    }
}

/// Bare-metal KEY verification left one or more cache files untouched. Abort the
/// log/DB tail so provenance is preserved for a corrected retry.
pub fn ensure_cache_deletions_verified(verification_skips: usize) -> Result<()> {
    if verification_skips == 0 {
        return Ok(());
    }

    anyhow::bail!(
        "Cache deletion safety verification failed for {} file(s); skipped files, access logs, and database records were left intact",
        verification_skips
    )
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

/// Lifecycle stage keys for [`run_url_removal_steps`]: one per shared step, differing per bin
/// only in the `signalr.*` family they belong to (`epicRemove` vs `gameRemove`).
pub struct RemovalLifecycleKeys {
    pub cache_removing: &'static str,
    pub dirs_cleaning: &'static str,
    pub logs_removing: &'static str,
    pub db_deleting: &'static str,
}

/// What [`run_url_removal_steps`] produced, for the caller's DB delete and final report.
/// Defaultable so the no-URLs early exit (which never runs the tail) can still build a
/// [`RemovalReport`] through the same [`RemovalReport::from_tail`] path as the other two
/// exit paths.
#[derive(Default)]
pub struct RemovalTail {
    pub deleted_files: usize,
    pub bytes_freed: u64,
    pub empty_dirs_removed: usize,
    pub log_entries_removed: u64,
    pub log_lines_removed_by_source: HashMap<String, u64>,
    pub log_lines_removed_before_position_by_source: HashMap<String, u64>,
}

/// Final report the Epic and name-keyed removal bins write to their output JSON.
/// (Steam's own report additionally carries depot ids, so it keeps its own type.)
#[derive(Debug, Serialize)]
pub struct RemovalReport {
    pub game_name: String,
    pub cache_files_deleted: usize,
    pub total_bytes_freed: u64,
    pub empty_dirs_removed: usize,
    pub log_entries_removed: u64,
    /// Removed-line count per log-source stem, series-wide - the caller subtracts these
    /// from the saved ingestion positions so a purge cannot shift them past unread lines.
    pub log_lines_removed_by_source: HashMap<String, u64>,
    /// The already-read subset of `log_lines_removed_by_source` (series index below the
    /// saved position). This is the amount the position itself comes back by; the full
    /// map above is what the on-disk total-line count comes down by.
    pub log_lines_removed_before_position_by_source: HashMap<String, u64>,
}

impl RemovalReport {
    /// Build the report from a removal tail. Called on all three exit paths: the
    /// no-URLs early return passes a default (zeroed) tail, the verification/permission
    /// failure closure passes the partial tail, and the final success path passes the
    /// completed tail. [17]
    pub fn from_tail(game_name: &str, tail: &RemovalTail) -> Self {
        Self {
            game_name: game_name.to_string(),
            cache_files_deleted: tail.deleted_files,
            total_bytes_freed: tail.bytes_freed,
            empty_dirs_removed: tail.empty_dirs_removed,
            log_entries_removed: tail.log_entries_removed,
            log_lines_removed_by_source: tail.log_lines_removed_by_source.clone(),
            log_lines_removed_before_position_by_source: tail
                .log_lines_removed_before_position_by_source
                .clone(),
        }
    }

    /// Persist the report to the bin's output JSON.
    pub fn write(&self, output_json: &Path) -> Result<()> {
        let json = serde_json::to_string_pretty(self)?;
        fs::write(output_json, json)?;
        Ok(())
    }
}

/// The URL-scoped removal step sequence shared by the Epic and name-keyed bins: cache-file
/// delete, empty-dir cleanup, bare-metal verification gate, access-log purge, and the
/// permission gate, ending on the `removing_database` emit. The caller then deletes its own
/// DB rows and writes the final report from the returned tail. Returns `Ok(None)` when a
/// cancellation arrived during the cache sweep (partial dirs are cleaned; log/DB work is
/// skipped and the bin exits 0). `write_failure_report` runs on the two abort paths so the
/// bin's own report shape still lands on disk before the error propagates.
///
/// Steam does NOT use this: its log purge is depot-scoped with per-file progress, and its
/// report carries depot ids - the one tail divergence that bin keeps.
pub fn run_url_removal_steps(
    cache_dir: &Path,
    log_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    progress_path: &Path,
    reporter: &ProgressReporter,
    per_file_keys: &RemovalStageKeys,
    lifecycle: &RemovalLifecycleKeys,
    cadence: ProgressCadence,
    stem_positions_path: Option<&str>,
    write_failure_report: &dyn Fn(&RemovalTail) -> Result<()>,
) -> Result<Option<RemovalTail>> {
    // Step 1: Remove cache files
    let url_count = url_data.len();
    write_progress(progress_path, reporter, "removing_cache", lifecycle.cache_removing, json!({ "count": url_count }), 10.0, 0, 0)?;
    eprintln!("\nRemoving cache files...");
    let outcome = remove_cache_files(
        cache_dir,
        url_data,
        progress_path,
        reporter,
        per_file_keys,
        cadence,
        cache_utils::active_key_scheme(),
    )?;

    // If cancellation arrived during cache removal, do directory cleanup and exit 0.
    if cancel::is_cancelled() {
        eprintln!("Cancellation confirmed — cleaning up partial directories and exiting.");
        cache_utils::cleanup_empty_directories(cache_dir, outcome.parent_dirs);
        return Ok(None);
    }

    // Step 2: Clean up empty directories
    write_progress(progress_path, reporter, "cleaning_directories", lifecycle.dirs_cleaning, json!({}), 70.0, 0, 0)?;
    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cache_utils::cleanup_empty_directories(cache_dir, outcome.parent_dirs);

    let mut tail = RemovalTail {
        deleted_files: outcome.deleted_files,
        bytes_freed: outcome.bytes_freed,
        empty_dirs_removed,
        log_entries_removed: 0,
        log_lines_removed_by_source: Default::default(),
        log_lines_removed_before_position_by_source: Default::default(),
    };

    // A failed bare-metal KEY check is not a successful removal. The cache helper
    // correctly left the candidate untouched; preserve its URL provenance as well
    // so a corrected retry can still find it instead of turning it into an orphan.
    if let Err(error) = ensure_cache_deletions_verified(outcome.verification_skips) {
        write_failure_report(&tail)?;
        return Err(error);
    }

    // Step 3: Remove log entries from access log text files
    write_progress(progress_path, reporter, "removing_logs", lifecycle.logs_removing, json!({}), 80.0, 0, 0)?;
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let stem_positions = stem_positions_path.and_then(log_purge::read_stem_positions);
    let log_outcome = purge_log_entries(
        log_dir,
        &urls_to_remove,
        &LogScope::Urls,
        stem_positions.as_ref(),
    )?;
    tail.log_entries_removed = log_outcome.lines_removed;
    let log_permission_errors = log_outcome.permission_errors;
    tail.log_lines_removed_by_source = log_outcome.lines_removed_by_stem;
    tail.log_lines_removed_before_position_by_source = log_outcome.lines_removed_before_position_by_stem;

    // Step 4: Check for permission errors before touching database
    let total_permission_errors = outcome.permission_errors + log_permission_errors;
    if total_permission_errors > 0 {
        let error_msg = permission_error_message(
            total_permission_errors,
            outcome.permission_errors,
            log_permission_errors,
        );
        eprintln!("\n{}", error_msg);
        write_failure_report(&tail)?;
        anyhow::bail!("{}", error_msg);
    }

    // Step 5 hand-off: the caller deletes its own database records next.
    write_progress(progress_path, reporter, "removing_database", lifecycle.db_deleting, json!({}), 90.0, 0, 0)?;
    eprintln!("\nRemoving database records...");
    Ok(Some(tail))
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
    fn counting_leaves_every_file_in_place_and_matches_what_the_removal_deletes() {
        let temp = tempfile::tempdir().unwrap();
        let service = "steam";
        // A query string is the case that made earlier probes disagree with nginx: the access
        // log keeps it, the cache key does not. Both the count and the delete go through
        // cache_utils, so they must agree on it.
        let url = "/depot/1/chunk/abcdef?token=xyz";
        let cache_path = cache_utils::calculate_cache_path_no_range(temp.path(), service, url);
        write_cache_file(&cache_path, None);

        let url_data = HashMap::from([(url.to_string(), (service.to_string(), 0_i64))]);
        let output_json = temp.path().join("count.json");
        let progress_path = temp.path().join("progress.json");
        let reporter = ProgressReporter::new(false);
        let counted = count_cache_files(
            temp.path(),
            &url_data,
            &output_json,
            "Some Game",
            cache_utils::CacheKeyScheme::Monolithic,
            &CollectionProgress {
                progress_path: &progress_path,
                reporter: &reporter,
                stage_key: "test.cache.counting",
            },
        )
        .unwrap();

        assert_eq!(counted, 1);
        assert!(cache_path.exists(), "counting must not delete anything");

        let report: serde_json::Value =
            serde_json::from_slice(&fs::read(&output_json).unwrap()).unwrap();
        assert_eq!(report["entity"], "Some Game");
        assert_eq!(report["cache_files_found"], 1);

        let outcome = remove_one(
            temp.path(),
            service,
            url,
            cache_utils::CacheKeyScheme::Monolithic,
            &progress_path,
        );

        assert_eq!(outcome.deleted_files, counted);
        assert!(!cache_path.exists());
    }

    #[test]
    fn counting_an_entity_with_no_urls_reports_zero() {
        let temp = tempfile::tempdir().unwrap();
        let progress_path = temp.path().join("progress.json");
        let reporter = ProgressReporter::new(false);
        let counted = count_cache_files(
            temp.path(),
            &HashMap::new(),
            &temp.path().join("count.json"),
            "Some Game",
            cache_utils::CacheKeyScheme::Monolithic,
            &CollectionProgress {
                progress_path: &progress_path,
                reporter: &reporter,
                stage_key: "test.cache.counting",
            },
        )
        .unwrap();

        assert_eq!(counted, 0);
    }

    #[test]
    fn verification_skips_block_log_and_database_removal() {
        assert!(ensure_cache_deletions_verified(0).is_ok());

        let error = ensure_cache_deletions_verified(2).unwrap_err().to_string();
        assert!(error.contains("2 file(s)"));
        assert!(error.contains("access logs, and database records were left intact"));
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
