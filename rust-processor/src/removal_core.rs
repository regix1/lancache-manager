//! Shared cache-removal core for the per-service removal bins.
//!
//! All five removal bins (`cache_steam_remove`, `cache_epic_remove`,
//! `cache_blizzard_remove`, `cache_riot_remove`, `cache_xbox_remove`) share an
//! almost-identical TAIL: collect on-disk slices → parallel delete with progress →
//! clean up empty directories → permission-error gate → delete DB rows → write
//! report. Only the HEAD differs (how each service maps its identity to a
//! `HashMap<url, (service, bytes)>`) plus one report wrinkle unique to Steam (its
//! access.log purge targets are depot-scoped, not url-only). This module owns the
//! shared tail; each bin owns its head and hands the tail a `RemovalPlan`. The bins
//! never rewrite access.log: the report names the purge targets and the host removes
//! those lines in its own locked step.
//!
//! Behavior is byte-identical to the pre-consolidation bins:
//!   * `remove_cache_files` walks every on-disk slice via the scheme-aware
//!     `cache_utils::existing_keyed_paths_for_url_with_scheme` dispatcher,
//!   * progress is emitted in the 10%-70% band using each service's own stage keys,
//!   * the `ProgressCadence` enum reproduces the two existing emit cadences verbatim
//!     (Steam = every integer-percent advance OR every 8th probe; Epic/named =
//!     every integer-percent advance only).

use anyhow::Result;
use serde::Serialize;
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};

use crate::cache_utils;
use crate::cancel;
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

/// How often `remove_cache_files` emits a progress entry. Reproduces the two
/// distinct cadences that existed before consolidation so event volume is unchanged.
///
/// `#[allow(dead_code)]`: each variant / API item below is used by SOME removal bin
/// but not all, and every bin compiled `removal_core` independently before it moved
/// into the lib crate, so per-crate dead-code analysis flagged the items a given bin
/// does not touch (e.g. Epic/named never use the `OnPercentAdvanceOrEveryEighth`
/// cadence). This mirrors the per-bin `#[allow(dead_code)]` pattern already used in
/// `log_purge.rs`.
#[derive(Clone, Copy)]
#[allow(dead_code)]
pub enum ProgressCadence {
    /// Epic / named: write only when the integer percent advances.
    OnPercentAdvance,
    /// Steam: write on an integer-percent advance OR every 8th probed file, so small
    /// games still emit motion inside the brief poll window.
    OnPercentAdvanceOrEveryEighth,
}

/// nginx's `levels=2:2` layout has at most 256 top-level folders under a cache root. The KEY-header
/// sweep counts how many it has started reading; a cache that never filled every folder ends its count
/// below this total.
pub const TOP_LEVEL_CACHE_FOLDERS: usize = 256;

/// Whether the forward slice walk already reaches every slice of this removal's objects, or
/// the KEY-header pass has to look for the ones it cannot.
///
/// A range-served object (a wsus payload, a Blizzard TACT archive, a Riot bundle) spans many
/// 1 MiB slices, and partial eviction can hide some behind a hole wider than the walk's miss
/// tolerance. A Steam depot chunk or an Epic chunk is at most two slices, which a walk from
/// slice 0 always reaches, so those removals skip the pass rather than pay a header read per
/// unclaimed cache file. This is the same split detection makes when it decides which buckets
/// to register for late attribution.
#[derive(Clone, Copy, PartialEq)]
#[allow(dead_code)]
pub enum SliceReach {
    /// Steam / Epic: every slice is reachable by walking forward from slice 0.
    ForwardWalkIsComplete,
    /// Services and named games: sweep KEY headers for slices behind eviction holes, reporting the
    /// sweep's folder count under `stage_key`.
    SweepKeyHeaders { stage_key: &'static str },
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
    /// Files that are still on disk because their delete failed for a reason other than permission
    /// (in use, an I/O error, a read-only mount) or their path was refused as unsafe.
    pub undeleted_files: usize,
}

/// Where the collection walk reports its own progress, and under which stage key. A removal
/// passes None, so its event volume is unchanged; a count run passes Some, because the walk
/// IS the whole run and takes minutes on an entity with many logged URLs.
///
/// `#[allow(dead_code)]`: see the `ProgressCadence` note above. Each removal bin compiles this module
/// independently, and only the bins that offer a count construct this.
#[allow(dead_code)]
pub struct CollectionProgress<'a> {
    pub progress_path: &'a Path,
    pub reporter: &'a ProgressReporter,
    pub stage_key: &'a str,
}

/// Emit one collection-phase tick. Shared by every collection walk so the count's progress
/// shape is identical wherever it runs. The band starts at 5% and ends at `percent_to`: the whole bar when no KEY-header sweep can follow, 30% when one can.
#[allow(dead_code)]
pub fn report_collection_progress(
    progress: &CollectionProgress<'_>,
    urls_walked: usize,
    total_urls: usize,
    percent_to: f64,
) {
    let _ = write_progress(
        progress.progress_path,
        progress.reporter,
        "counting_files",
        progress.stage_key,
        json!({ "n": urls_walked, "total": total_urls }),
        5.0 + (urls_walked as f64 / total_urls as f64) * (percent_to - 5.0),
        urls_walked,
        total_urls,
    );
}

/// Emit one KEY-header sweep tick for a count: the sweep has the bar from 30% to 95%, after the URL walk.
#[allow(dead_code)]
pub fn report_sweep_progress(
    progress: &CollectionProgress<'_>,
    stage_key: &str,
    folders_started: usize,
) {
    let _ = write_progress(
        progress.progress_path,
        progress.reporter,
        "counting_files",
        stage_key,
        json!({ "n": folders_started, "total": TOP_LEVEL_CACHE_FOLDERS }),
        30.0 + (folders_started as f64 / TOP_LEVEL_CACHE_FOLDERS as f64) * 65.0,
        folders_started,
        TOP_LEVEL_CACHE_FOLDERS,
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
/// number any other way would compute a cache key nginx never wrote.
///
/// The forward walk above is joined by [`key_header_residue`], which recovers the slices it
/// cannot reach, unless [`every_slice_claimed`] proves there are none; the count and the delete loop
/// run this same collection, so both see the whole object and agree.
pub fn collect_cache_paths(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    scheme: cache_utils::CacheKeyScheme,
    reach: SliceReach,
    progress: Option<&CollectionProgress<'_>>,
    on_sweep_folder: &(dyn Fn(&str, usize) + Sync),
) -> Result<Vec<(PathBuf, Option<String>)>> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    let walk_percent_to = if reach == SliceReach::ForwardWalkIsComplete {
        95.0
    } else {
        30.0
    };
    let total_urls = url_data.len();
    let urls_walked = AtomicUsize::new(0);
    let last_reported_percent = AtomicUsize::new(0);

    let walked: Vec<(PathBuf, Option<String>)> = url_data
        .par_iter()
        .flat_map(|(url, (service, _total_bytes))| {
            let paths = cache_utils::existing_keyed_paths_for_url_with_scheme(
                scheme, cache_dir, service, url,
            );

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
                    report_collection_progress(progress, walked, total_urls, walk_percent_to);
                }
            }

            paths
        })
        .collect::<Vec<_>>();

    let SliceReach::SweepKeyHeaders { stage_key } = reach else {
        return Ok(walked);
    };

    let claimed: HashSet<u128> = walked
        .iter()
        .filter_map(|(path, _)| {
            path.file_name()
                .and_then(|name| name.to_str())
                .and_then(cache_utils::parse_cache_file_digest)
        })
        .collect();

    let bases = object_key_bases(url_data.iter().map(|(url, (service, _))| (service, url)));

    if every_slice_claimed(
        cache_dir,
        url_data.iter().map(|(url, (service, _))| (service, url)),
        &claimed,
    ) {
        return Ok(walked);
    }

    let mut paths = walked;
    paths.extend(
        key_header_residue(cache_dir, &bases, &claimed, &|folders| {
            on_sweep_folder(stage_key, folders)
        })?
        .into_iter()
        .map(|(digest, key)| {
            (
                cache_utils::cache_path_for_digest(cache_dir, digest),
                Some(key),
            )
        }),
    );
    Ok(paths)
}

/// The md5 digest of each (service, url)'s unsliced object key: what a cache file's own
/// `KEY:` header hashes to once its slice suffix is stripped.
pub fn object_key_bases<'a>(
    url_data: impl Iterator<Item = (&'a String, &'a String)>,
) -> HashSet<u128> {
    url_data
        .filter_map(|(service, url)| cache_utils::object_key_base(service, url))
        .map(|base| cache_utils::calculate_md5_digest(&base))
        .collect()
}

/// Whether the forward walk already claimed every slice of every object, so no slice can sit behind an
/// eviction hole and the KEY-header sweep has nothing to find. An object counts when the walk claimed its
/// slice 0 and every slice up to the size slice 0's own `Content-Range` header names. A missing slice 0, an
/// unreadable header or a header with no size means the sweep runs.
pub fn every_slice_claimed<'a>(
    cache_dir: &Path,
    mut objects: impl Iterator<Item = (&'a String, &'a String)>,
    claimed: &HashSet<u128>,
) -> bool {
    use std::io::Read;

    objects.all(|(service, url)| {
        let Some(base) = cache_utils::object_key_base(service, url) else {
            return false;
        };
        let slice = |index: u64| {
            let start = index * cache_utils::DEFAULT_SLICE_SIZE;
            cache_utils::calculate_md5_digest(&format!(
                "{base}bytes={start}-{}",
                start + cache_utils::DEFAULT_SLICE_SIZE - 1
            ))
        };
        let first = slice(0);
        if !claimed.contains(&first) {
            return false;
        }
        // The cached response's headers follow the KEY line inside the same first 8 KiB the KEY read covers.
        let mut head = Vec::new();
        let Ok(file) = fs::File::open(cache_utils::cache_path_for_digest(cache_dir, first)) else {
            return false;
        };
        if file.take(8192).read_to_end(&mut head).is_err() {
            return false;
        }
        let size = head.split(|byte| *byte == b'\n').find_map(|line| {
            let colon = line.iter().position(|byte| *byte == b':')?;
            if !line[..colon].eq_ignore_ascii_case(b"content-range") {
                return None;
            }
            let (_, text) =
                crate::cache_structural_scanner::parse_content_range(&line[colon + 1..])?;
            text.rsplit_once('/')?.1.parse::<u64>().ok()
        });
        let Some(size) = size else {
            return false;
        };
        (1..size.div_ceil(cache_utils::DEFAULT_SLICE_SIZE))
            .all(|index| claimed.contains(&slice(index)))
    })
}

/// The cache files whose own `KEY:` header names one of `bases`' objects but which the
/// forward slice walk never reached.
///
/// That walk stops after `CONSECUTIVE_MISS_LIMIT` absent slices, so a slice sitting behind a
/// wider partial-eviction hole is invisible to it. Detection has recovered exactly those files
/// since Aug 2026 by reading each unclaimed file's embedded key (`cache_game_detect` Phase 5);
/// without the same reach a removal deletes what it can walk, leaves the rest on disk, and then
/// purges the log rows that were the only way to identify them afterwards. A real removal left
/// 369 of 398 files behind that way.
///
/// Costs one directory walk plus a header read per file the walk did not already claim, so the
/// deleting pass pays it once rather than per URL. Files already claimed are skipped without
/// being opened. A 2-hex folder linked to another disk is read like the eviction index reads it; a
/// folder of the layout it cannot read is an error.
/// Reports each top-level cache folder it starts reading through `on_folder`.
pub fn key_header_residue(
    cache_dir: &Path,
    bases: &HashSet<u128>,
    claimed: &HashSet<u128>,
    on_folder: &(dyn Fn(usize) + Sync),
) -> Result<Vec<(u128, String)>> {
    use rayon::prelude::*;

    if bases.is_empty() {
        return Ok(Vec::new());
    }

    let hidden = std::sync::atomic::AtomicBool::new(false);
    let folders_started = std::sync::atomic::AtomicUsize::new(0);
    let residue: Vec<(u128, String)> = cache_utils::cache_root_walk(cache_dir, |_| {})
        .parallelism(jwalk::Parallelism::RayonNewPool(num_cpus::get()))
        .into_iter()
        .inspect(|item| {
            if cache_utils::hides_cache_files(cache_dir, item) {
                hidden.store(true, std::sync::atomic::Ordering::Relaxed);
            }
            // The walk is depth first, so a top-level folder is reached once the folders before it were read.
            if matches!(item, Ok(entry) if entry.depth == 1) {
                let started =
                    folders_started.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1;
                on_folder(started.min(TOP_LEVEL_CACHE_FOLDERS));
            }
        })
        .filter_map(|entry| entry.ok())
        .filter(|entry| entry.file_type().is_file())
        .par_bridge()
        .filter_map(|entry| {
            if cancel::is_cancelled() {
                return None;
            }

            // A name that is not a 32-hex md5 was never written by nginx as a cache key, so no
            // key can hash to it; skipped before the file is opened, like the detection index.
            let digest = entry
                .file_name()
                .to_str()
                .and_then(cache_utils::parse_cache_file_digest)?;
            if claimed.contains(&digest) {
                return None;
            }

            let key = cache_utils::read_cache_file_key(&entry.path())?;
            let base = cache_utils::calculate_md5_digest(cache_utils::cache_key_base_of(&key));
            bases.contains(&base).then_some((digest, key))
        })
        .collect();
    // A folder the walk could not read may hold slices behind an eviction hole, so the removal cannot
    // prove it covers the whole object and stops before it deletes anything.
    if hidden.load(std::sync::atomic::Ordering::Relaxed) {
        anyhow::bail!(
            "a cache folder under {} could not be read, so this removal cannot reach every cached slice",
            cache_dir.display()
        );
    }
    Ok(residue)
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
/// different function, so it is unreachable from here, and no path is derived a second way.
///
/// `#[allow(dead_code)]`: see the `ProgressCadence` note above. Only the bins that offer a count call this.
#[allow(dead_code)]
pub fn count_cache_files(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    output_json: &Path,
    entity: &str,
    scheme: cache_utils::CacheKeyScheme,
    reach: SliceReach,
    progress: &CollectionProgress<'_>,
) -> Result<usize> {
    let cache_files_found = collect_cache_paths(
        cache_dir,
        url_data,
        scheme,
        reach,
        Some(progress),
        &|stage_key, folders| report_sweep_progress(progress, stage_key, folders),
    )?
    .len();

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
#[allow(clippy::too_many_arguments)]
pub fn remove_cache_files(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    progress_path: &Path,
    reporter: &ProgressReporter,
    keys: &RemovalStageKeys,
    cadence: ProgressCadence,
    scheme: cache_utils::CacheKeyScheme,
    reach: SliceReach,
) -> Result<CacheRemovalOutcome> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
    use std::sync::Mutex;

    let deleted_files = AtomicUsize::new(0);
    let bytes_freed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let verification_skips = AtomicUsize::new(0);
    let undeleted_files = AtomicUsize::new(0);
    let parent_dirs = Mutex::new(HashSet::new());

    eprintln!("Collecting cache file paths for deletion...");

    // Re-walked from disk here, inside the deleting process, so the set deleted is the set
    // that exists now rather than one an earlier pass recorded.
    let paths_to_check = collect_cache_paths(
        cache_dir,
        url_data,
        scheme,
        reach,
        None,
        &|stage_key, folders| {
            // The card stays at the removal's starting 10% while the sweep reads the rest of the cache.
            let _ = write_progress(
                progress_path,
                reporter,
                "removing_cache",
                stage_key,
                json!({ "n": folders, "total": TOP_LEVEL_CACHE_FOLDERS }),
                10.0,
                folders,
                TOP_LEVEL_CACHE_FOLDERS,
            );
        },
    )?;

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

        let present = match cache_utils::cache_file_presence(cache_dir, path) {
            Ok(present) => present,
            Err(e) => {
                // Behind a folder the app cannot search, or under a linked cache folder whose disk is
                // gone, the file may still be there, so the removal keeps the history.
                eprintln!("  cannot check {}: {}", path.display(), e);
                undeleted_files.fetch_add(1, Ordering::Relaxed);
                false
            }
        };
        if present {
            // Refuse a file that is a link, or anything outside the cache root other than under a
            // linked 2-hex cache folder. Such a file stays on disk, so the removal must not delete
            // its history; one that vanished first is gone.
            if let Err(e) = cache_utils::safe_cache_path_under_root(cache_dir, path) {
                eprintln!("  skipping unsafe path {}: {}", path.display(), e);
                if e.kind() != std::io::ErrorKind::NotFound {
                    undeleted_files.fetch_add(1, Ordering::Relaxed);
                }
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
                // Read before the delete and counted only after it, so a file that stays is never
                // reported freed.
                let size = match fs::metadata(path) {
                    Ok(metadata) => metadata.len(),
                    // The file is still deleted below; only the freed-bytes total is
                    // short by its size, so say which file it was short by.
                    Err(e) => {
                        eprintln!(
                            "  Warning: could not read the size of {}, it is missing from the freed total: {}",
                            path.display(),
                            e
                        );
                        0
                    }
                };

                match fs::remove_file(path) {
                    Ok(_) => {
                        bytes_freed.fetch_add(size, Ordering::Relaxed);
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

                        if count.is_multiple_of(100) {
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
                        } else if e.kind() != std::io::ErrorKind::NotFound {
                            // A busy file, an I/O error or a read-only mount leaves the file on disk.
                            let failures = undeleted_files.fetch_add(1, Ordering::Relaxed) + 1;
                            if failures <= 5 {
                                eprintln!("  ERROR: Failed to delete {}: {}", path.display(), e);
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
    let final_undeleted_files = undeleted_files.load(Ordering::Relaxed);
    if final_undeleted_files > 0 {
        eprintln!(
            "  Left {} file(s) on disk that could not be deleted",
            final_undeleted_files
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
        undeleted_files: final_undeleted_files,
    })
}

/// Bare-metal KEY verification left one or more cache files untouched, or a file could not be
/// deleted or was refused as unsafe. Either way the file is still cached, so abort the log/DB
/// tail and keep its provenance for a corrected retry.
pub fn ensure_cache_deletions_verified(
    verification_skips: usize,
    undeleted_files: usize,
) -> Result<()> {
    if verification_skips > 0 {
        anyhow::bail!(
            "Cache deletion safety verification failed for {} file(s); skipped files, access logs, and database records were left intact",
            verification_skips
        );
    }
    if undeleted_files > 0 {
        anyhow::bail!(
            "{} cache file(s) could not be deleted (in use, an I/O error, a read-only mount or an unsafe path); access logs and database records were left intact",
            undeleted_files
        );
    }
    Ok(())
}

/// Build the PUID/PGID permission-error abort message shared by every removal bin.
/// Returned so the caller can `eprintln!` it, write the report with `failed` status,
/// and `bail!` with the same text (identical to the prior per-bin logic).
pub fn permission_error_message(permission_errors: usize) -> String {
    let puid = std::env::var("PUID").unwrap_or_else(|_| "1000".to_string());
    let pgid = std::env::var("PGID").unwrap_or_else(|_| "1000".to_string());
    format!(
        "ABORTED: Cannot delete database records because {} file(s) could not be modified due to permission errors. \
        This is likely caused by incorrect PUID/PGID settings. The lancache container is configured to run as UID/GID {}:{}. \
        Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership.",
        permission_errors, puid, pgid
    )
}

/// Lifecycle stage keys for [`run_url_removal_steps`]: one per shared step, differing per bin
/// only in the `signalr.*` family they belong to (`epicRemove` vs `gameRemove`).
pub struct RemovalLifecycleKeys {
    pub cache_removing: &'static str,
    pub dirs_cleaning: &'static str,
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
    pub purge_urls: Vec<String>,
}

/// Final report the Epic and name-keyed removal bins write to their output JSON.
/// (Steam's own report additionally carries depot ids, so it keeps its own type.)
#[derive(Debug, Serialize)]
pub struct RemovalReport {
    pub game_name: String,
    pub cache_files_deleted: usize,
    pub total_bytes_freed: u64,
    pub empty_dirs_removed: usize,
    /// URLs whose access.log lines the host removes in its own locked step. Empty on the
    /// no-URL and failure exits, so a failed run leaves the lines in place.
    pub purge_urls: Vec<String>,
}

impl RemovalReport {
    /// Build the report from a removal tail. Called on all three exit paths: the
    /// no-URLs early return passes a default (zeroed) tail, the verification/permission
    /// failure closure passes the partial tail, and the final success path passes the
    /// completed tail.
    pub fn from_tail(game_name: &str, tail: &RemovalTail) -> Self {
        Self {
            game_name: game_name.to_string(),
            cache_files_deleted: tail.deleted_files,
            total_bytes_freed: tail.bytes_freed,
            empty_dirs_removed: tail.empty_dirs_removed,
            purge_urls: tail.purge_urls.clone(),
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
/// delete, empty-dir cleanup, bare-metal verification gate and the permission gate, ending
/// on the `removing_database` emit. The caller then deletes its own DB rows and writes the
/// final report from the returned tail, whose `purge_urls` the host purges from access.log.
/// Returns `Ok(None)` when a cancellation arrived during the cache sweep (partial dirs are
/// cleaned; DB work is skipped and the bin exits 0). `write_failure_report` runs on the two
/// abort paths so the bin's own report shape still lands on disk before the error propagates.
///
/// Steam does NOT use this: its purge targets are depot-scoped, and its report carries
/// depot ids - the one tail divergence that bin keeps.
#[allow(clippy::too_many_arguments)]
pub fn run_url_removal_steps(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64)>,
    progress_path: &Path,
    reporter: &ProgressReporter,
    per_file_keys: &RemovalStageKeys,
    lifecycle: &RemovalLifecycleKeys,
    cadence: ProgressCadence,
    reach: SliceReach,
    write_failure_report: &dyn Fn(&RemovalTail) -> Result<()>,
) -> Result<Option<RemovalTail>> {
    // Step 1: Remove cache files
    let url_count = url_data.len();
    write_progress(
        progress_path,
        reporter,
        "removing_cache",
        lifecycle.cache_removing,
        json!({ "count": url_count }),
        10.0,
        0,
        0,
    )?;
    eprintln!("\nRemoving cache files...");
    let outcome = remove_cache_files(
        cache_dir,
        url_data,
        progress_path,
        reporter,
        per_file_keys,
        cadence,
        cache_utils::active_key_scheme(),
        reach,
    )?;

    // If cancellation arrived during cache removal, do directory cleanup and exit 0.
    if cancel::is_cancelled() {
        eprintln!("Cancellation confirmed — cleaning up partial directories and exiting.");
        cache_utils::cleanup_empty_directories(cache_dir, outcome.parent_dirs);
        return Ok(None);
    }

    // Step 2: Clean up empty directories
    write_progress(
        progress_path,
        reporter,
        "cleaning_directories",
        lifecycle.dirs_cleaning,
        json!({}),
        70.0,
        0,
        0,
    )?;
    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cache_utils::cleanup_empty_directories(cache_dir, outcome.parent_dirs);

    let mut tail = RemovalTail {
        deleted_files: outcome.deleted_files,
        bytes_freed: outcome.bytes_freed,
        empty_dirs_removed,
        purge_urls: Vec::new(),
    };

    // A failed bare-metal KEY check is not a successful removal. The cache helper
    // correctly left the candidate untouched; preserve its URL provenance as well
    // so a corrected retry can still find it instead of turning it into an orphan.
    // A file that could not be deleted is still cached the same way.
    if let Err(error) =
        ensure_cache_deletions_verified(outcome.verification_skips, outcome.undeleted_files)
    {
        write_failure_report(&tail)?;
        return Err(error);
    }

    // Step 3: Check for permission errors before touching database
    if outcome.permission_errors > 0 {
        let error_msg = permission_error_message(outcome.permission_errors);
        eprintln!("\n{}", error_msg);
        write_failure_report(&tail)?;
        anyhow::bail!("{}", error_msg);
    }

    // Named only once both gates pass, so a failed run's report leaves the log lines in place.
    tail.purge_urls = url_data.keys().cloned().collect();

    // Step 4 hand-off: the caller deletes its own database records next.
    write_progress(
        progress_path,
        reporter,
        "removing_database",
        lifecycle.db_deleting,
        json!({}),
        90.0,
        0,
        0,
    )?;
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
            SliceReach::SweepKeyHeaders {
                stage_key: "test.cache.sweeping",
            },
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
            SliceReach::SweepKeyHeaders {
                stage_key: "test.cache.sweeping",
            },
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
            SliceReach::SweepKeyHeaders {
                stage_key: "test.cache.sweeping",
            },
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
        assert!(ensure_cache_deletions_verified(0, 0).is_ok());

        let error = ensure_cache_deletions_verified(2, 0)
            .unwrap_err()
            .to_string();
        assert!(error.contains("2 file(s)"));
        assert!(error.contains("access logs, and database records were left intact"));

        let error = ensure_cache_deletions_verified(0, 1)
            .unwrap_err()
            .to_string();
        assert!(error.contains("1 cache file(s) could not be deleted"));
        assert!(error.contains("access logs and database records were left intact"));
    }

    #[test]
    fn url_removal_leaves_access_logs_untouched_and_reports_the_purge_urls() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        let service = "epicgames";
        let url = "/Builds/Org/o-abc/chunk.chunk";
        let cache_path = cache_utils::calculate_cache_path_no_range(&cache_dir, service, url);
        write_cache_file(&cache_path, None);
        fs::create_dir_all(&log_dir).unwrap();
        let log_path = log_dir.join("access.log");
        fs::write(
            &log_path,
            format!(
                "[{service}] 192.0.2.10 / - - - [01/Oct/2026:10:00:00 +0000] \"GET {url} HTTP/1.1\" 200 1024 \"-\" \"Test\" \"HIT\" \"cdn.test\" \"-\"\n"
            ),
        )
        .unwrap();
        let log_bytes = fs::read(&log_path).unwrap();

        let urls = HashMap::from([(url.to_string(), (service.to_string(), 0_i64))]);
        let tail = run_url_removal_steps(
            &cache_dir,
            &urls,
            &temp.path().join("progress.json"),
            &ProgressReporter::new(false),
            &TEST_STAGE_KEYS,
            &RemovalLifecycleKeys {
                cache_removing: "test.cache.removing",
                dirs_cleaning: "test.dirs.cleaning",
                db_deleting: "test.db.deleting",
            },
            ProgressCadence::OnPercentAdvance,
            SliceReach::ForwardWalkIsComplete,
            &|_| panic!("a clean removal writes no failure report"),
        )
        .unwrap()
        .expect("no cancellation was requested");

        assert!(!cache_path.exists());
        assert_eq!(tail.deleted_files, 1);
        assert_eq!(tail.purge_urls, vec![url.to_string()]);
        assert_eq!(fs::read(&log_path).unwrap(), log_bytes);
    }

    /// What every case below expects: the file stays, nothing is reported freed, the failure report
    /// is written once, and the log step is not named a single URL.
    #[cfg(unix)]
    fn remove_and_expect_the_tail_stopped(cache_dir: &Path, url: &str, service: &str) -> String {
        let urls = HashMap::from([(url.to_string(), (service.to_string(), 0_i64))]);
        let failure_reports = std::cell::Cell::new(0);
        let error = run_url_removal_steps(
            cache_dir,
            &urls,
            &cache_dir.join("progress.json"),
            &ProgressReporter::new(false),
            &TEST_STAGE_KEYS,
            &RemovalLifecycleKeys {
                cache_removing: "test.cache.removing",
                dirs_cleaning: "test.dirs.cleaning",
                db_deleting: "test.db.deleting",
            },
            ProgressCadence::OnPercentAdvance,
            SliceReach::ForwardWalkIsComplete,
            &|tail| {
                assert_eq!(tail.bytes_freed, 0);
                assert!(tail.purge_urls.is_empty());
                failure_reports.set(failure_reports.get() + 1);
                Ok(())
            },
        )
        .err()
        .expect("a cache file left on disk stops the removal");
        assert_eq!(failure_reports.get(), 1);
        error.to_string()
    }

    #[cfg(unix)]
    #[test]
    fn a_cache_file_that_cannot_be_deleted_stops_the_log_and_database_steps() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let url = "/Builds/Org/o-abc/chunk.chunk";
        // A folder where the cache file should be: the unlink fails with "is a directory", which is
        // neither permission denied nor not found, as a busy file or an I/O error is.
        let cache_path = cache_utils::calculate_cache_path_no_range(&cache_dir, "epicgames", url);
        fs::create_dir_all(&cache_path).unwrap();

        let error = remove_and_expect_the_tail_stopped(&cache_dir, url, "epicgames");

        assert!(cache_path.exists());
        assert!(error.contains("1 cache file(s) could not be deleted"));
    }

    #[cfg(unix)]
    #[test]
    fn a_cache_path_refused_as_unsafe_stops_the_log_and_database_steps() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let url = "/Builds/Org/o-abc/chunk.chunk";
        let elsewhere = temp.path().join("elsewhere");
        fs::write(&elsewhere, b"cache").unwrap();
        let cache_path = cache_utils::calculate_cache_path_no_range(&cache_dir, "epicgames", url);
        fs::create_dir_all(cache_path.parent().unwrap()).unwrap();
        std::os::unix::fs::symlink(&elsewhere, &cache_path).unwrap();

        let error = remove_and_expect_the_tail_stopped(&cache_dir, url, "epicgames");

        assert!(elsewhere.exists());
        assert!(error.contains("1 cache file(s) could not be deleted"));
    }

    #[cfg(unix)]
    #[test]
    fn a_cache_file_under_a_linked_cache_folder_is_deleted_and_its_history_removed() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let other_disk = temp.path().join("other-disk");
        let url = "/Builds/Org/o-abc/chunk.chunk";
        let cache_path = cache_utils::calculate_cache_path_no_range(&cache_dir, "epicgames", url);
        // The cache file's 2-hex folder is a link to another disk, as cache clear supports.
        let hex_folder = cache_path.parent().unwrap().parent().unwrap().to_path_buf();
        fs::create_dir_all(&cache_dir).unwrap();
        fs::create_dir_all(&other_disk).unwrap();
        std::os::unix::fs::symlink(&other_disk, &hex_folder).unwrap();
        write_cache_file(&cache_path, None);

        let urls = HashMap::from([(url.to_string(), ("epicgames".to_string(), 0_i64))]);
        let tail = run_url_removal_steps(
            &cache_dir,
            &urls,
            &temp.path().join("progress.json"),
            &ProgressReporter::new(false),
            &TEST_STAGE_KEYS,
            &RemovalLifecycleKeys {
                cache_removing: "test.cache.removing",
                dirs_cleaning: "test.dirs.cleaning",
                db_deleting: "test.db.deleting",
            },
            ProgressCadence::OnPercentAdvance,
            SliceReach::ForwardWalkIsComplete,
            &|_| panic!("a removal through a linked cache folder writes no failure report"),
        )
        .unwrap()
        .expect("no cancellation was requested");

        assert!(!cache_path.exists());
        assert_eq!(tail.deleted_files, 1);
        assert_eq!(tail.purge_urls, vec![url.to_string()]);
        assert!(fs::symlink_metadata(&hex_folder)
            .unwrap()
            .file_type()
            .is_symlink());
        assert!(other_disk.is_dir());
    }

    #[cfg(unix)]
    #[test]
    fn a_cache_file_under_a_linked_cache_folder_whose_disk_is_gone_stops_the_removal() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let other_disk = temp.path().join("other-disk");
        let url = "/Builds/Org/o-abc/chunk.chunk";
        let cache_path = cache_utils::calculate_cache_path_no_range(&cache_dir, "epicgames", url);
        let hex_folder = cache_path.parent().unwrap().parent().unwrap().to_path_buf();
        fs::create_dir_all(&cache_dir).unwrap();
        fs::create_dir_all(&other_disk).unwrap();
        std::os::unix::fs::symlink(&other_disk, &hex_folder).unwrap();
        write_cache_file(&cache_path, None);
        // The disk behind the link is no longer mounted: the file may still be on it.
        fs::remove_dir_all(&other_disk).unwrap();

        let error = remove_and_expect_the_tail_stopped(&cache_dir, url, "epicgames");

        assert!(error.contains("1 cache file(s) could not be deleted"));
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

    // -------------------------------------------------------------------------------------
    // The KEY-header pass. lancache slices a range-served object into 1 MiB pieces and nginx
    // evicts them unevenly; the forward walk gives up after CONSECUTIVE_MISS_LIMIT absences,
    // so a piece behind a wider hole is only findable by reading the piece's own key.
    // -------------------------------------------------------------------------------------

    fn write_keyed_cache_file(root: &Path, key: &str) -> u128 {
        let digest = cache_utils::calculate_md5_digest(key);
        let path = cache_utils::cache_path_for_digest(root, digest);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, format!("KEY: {key}\nbody")).unwrap();
        digest
    }

    // The cache file nginx writes for slice 0 of a range-served object: its KEY line, then the
    // upstream 206 response headers, then the body.
    fn write_slice_zero(root: &Path, key: &str, object_size: u64) -> u128 {
        let digest = cache_utils::calculate_md5_digest(key);
        let path = cache_utils::cache_path_for_digest(root, digest);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(
            &path,
            format!(
                "KEY: {key}\nHTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-1048575/{object_size}\r\n\r\nbody"
            ),
        )
        .unwrap();
        digest
    }

    fn slice_key(base: &str, index: u64) -> String {
        let start = index * cache_utils::DEFAULT_SLICE_SIZE;
        format!(
            "{base}bytes={start}-{}",
            start + cache_utils::DEFAULT_SLICE_SIZE - 1
        )
    }

    fn count_riot_object(root: &Path, scratch: &Path, progress_path: &Path) -> usize {
        let urls = HashMap::from([("/bundle/one".to_string(), ("riot".to_string(), 0_i64))]);
        let reporter = ProgressReporter::new(false);
        count_cache_files(
            root,
            &urls,
            &scratch.join("count.json"),
            "Some Game",
            cache_utils::CacheKeyScheme::Monolithic,
            SliceReach::SweepKeyHeaders {
                stage_key: "test.cache.sweeping",
            },
            &CollectionProgress {
                progress_path,
                reporter: &reporter,
                stage_key: "test.cache.counting",
            },
        )
        .unwrap()
    }

    fn read_progress(progress_path: &Path) -> serde_json::Value {
        serde_json::from_slice(&fs::read(progress_path).unwrap()).unwrap()
    }

    #[test]
    fn the_key_header_pass_finds_a_slice_the_forward_walk_cannot_reach() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");

        let base = "wsus/filestreamingservice/files/object";
        // Chunk 0 is what a forward walk reaches; chunk 400 sits behind a hole far wider than
        // the walk's tolerance, which is exactly the file a removal used to leave behind.
        let reachable = write_keyed_cache_file(&root, &format!("{base}bytes=0-1048575"));
        let stranded = write_keyed_cache_file(
            &root,
            &format!("{base}bytes={}-{}", 400 * 1_048_576, 401 * 1_048_576 - 1),
        );
        // A file belonging to something else must survive the sweep untouched.
        write_keyed_cache_file(&root, "steam/depot/1234/chunk/abcdef");

        let bases: HashSet<u128> = [cache_utils::calculate_md5_digest(base)]
            .into_iter()
            .collect();
        let claimed: HashSet<u128> = [reachable].into_iter().collect();

        let residue = key_header_residue(&root, &bases, &claimed, &|_| {}).unwrap();

        let (digest, key) = assert_single(residue);
        assert_eq!(digest, stranded);
        assert!(key.starts_with(base));
    }

    #[cfg(unix)]
    #[test]
    fn the_key_header_pass_reads_a_linked_cache_folder() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let other_disk = temp.path().join("other-disk");

        let base = "wsus/filestreamingservice/files/object";
        let key = format!("{base}bytes={}-{}", 400 * 1_048_576, 401 * 1_048_576 - 1);
        // The stranded slice's 2-hex folder is a link to another disk, as cache clear supports.
        let slice_path =
            cache_utils::cache_path_for_digest(&root, cache_utils::calculate_md5_digest(&key));
        let hex_folder = slice_path.parent().unwrap().parent().unwrap().to_path_buf();
        fs::create_dir_all(&root).unwrap();
        fs::create_dir_all(&other_disk).unwrap();
        std::os::unix::fs::symlink(&other_disk, &hex_folder).unwrap();
        let stranded = write_keyed_cache_file(&root, &key);

        let bases: HashSet<u128> = [cache_utils::calculate_md5_digest(base)]
            .into_iter()
            .collect();

        let residue = key_header_residue(&root, &bases, &HashSet::new(), &|_| {}).unwrap();

        let (digest, _) = assert_single(residue);
        assert_eq!(digest, stranded);
    }

    #[cfg(unix)]
    #[test]
    fn the_key_header_pass_stops_when_a_linked_cache_folder_has_no_target() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");

        let base = "wsus/filestreamingservice/files/object";
        let key = format!("{base}bytes=0-1048575");
        let slice_folder =
            cache_utils::cache_path_for_digest(&root, cache_utils::calculate_md5_digest(&key))
                .parent()
                .unwrap()
                .parent()
                .unwrap()
                .file_name()
                .unwrap()
                .to_owned();
        write_keyed_cache_file(&root, &key);
        // A 2-hex folder linked to a disk that is not mounted may hold more slices of the object.
        let dangling = if slice_folder == "ab" { "cd" } else { "ab" };
        std::os::unix::fs::symlink(temp.path().join("unmounted"), root.join(dangling)).unwrap();

        let bases: HashSet<u128> = [cache_utils::calculate_md5_digest(base)]
            .into_iter()
            .collect();

        assert!(key_header_residue(&root, &bases, &HashSet::new(), &|_| {}).is_err());
    }

    #[test]
    fn the_key_header_pass_reports_nothing_when_the_walk_already_claimed_everything() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");

        let base = "riot/bundle/one";
        let only_slice = write_keyed_cache_file(&root, &format!("{base}bytes=0-1048575"));

        let bases: HashSet<u128> = [cache_utils::calculate_md5_digest(base)]
            .into_iter()
            .collect();
        let claimed: HashSet<u128> = [only_slice].into_iter().collect();

        assert!(key_header_residue(&root, &bases, &claimed, &|_| {})
            .unwrap()
            .is_empty());
    }

    #[test]
    fn the_count_reports_the_sweep_as_its_own_stage() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let base = cache_utils::object_key_base("riot", "/bundle/one").unwrap();
        // Slice 400 sits behind a hole far wider than the forward walk's tolerance.
        write_slice_zero(
            &root,
            &slice_key(&base, 0),
            401 * cache_utils::DEFAULT_SLICE_SIZE,
        );
        write_keyed_cache_file(&root, &slice_key(&base, 400));
        let progress_path = temp.path().join("progress.json");

        let counted = count_riot_object(&root, temp.path(), &progress_path);

        assert_eq!(counted, 2);
        let progress = read_progress(&progress_path);
        assert_eq!(progress["stageKey"], "test.cache.sweeping");
        assert_eq!(progress["context"]["total"], 256);
        let percent = progress["percentComplete"].as_f64().unwrap();
        assert!(
            percent > 30.0 && percent <= 95.0,
            "sweep percent was {percent}"
        );

        let outcome = remove_one(
            &root,
            "riot",
            "/bundle/one",
            cache_utils::CacheKeyScheme::Monolithic,
            &progress_path,
        );
        assert_eq!(outcome.deleted_files, counted);
    }

    #[test]
    fn the_sweep_is_skipped_when_the_walk_reached_every_slice() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let base = cache_utils::object_key_base("riot", "/bundle/one").unwrap();
        write_slice_zero(
            &root,
            &slice_key(&base, 0),
            2 * cache_utils::DEFAULT_SLICE_SIZE + 10,
        );
        write_keyed_cache_file(&root, &slice_key(&base, 1));
        write_keyed_cache_file(&root, &slice_key(&base, 2));
        let progress_path = temp.path().join("progress.json");

        let counted = count_riot_object(&root, temp.path(), &progress_path);

        assert_eq!(counted, 3);
        assert_eq!(
            read_progress(&progress_path)["stageKey"],
            "test.cache.counting"
        );

        let outcome = remove_one(
            &root,
            "riot",
            "/bundle/one",
            cache_utils::CacheKeyScheme::Monolithic,
            &progress_path,
        );
        assert_eq!(outcome.deleted_files, counted);
    }

    #[test]
    fn the_sweep_runs_when_the_walk_stopped_short_of_the_size() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let base = cache_utils::object_key_base("riot", "/bundle/one").unwrap();
        write_slice_zero(
            &root,
            &slice_key(&base, 0),
            21 * cache_utils::DEFAULT_SLICE_SIZE,
        );
        // Slices 3 through 19 are gone: a hole wider than the walk's tolerance hides slice 20.
        for index in [1, 2, 20] {
            write_keyed_cache_file(&root, &slice_key(&base, index));
        }
        let progress_path = temp.path().join("progress.json");

        let counted = count_riot_object(&root, temp.path(), &progress_path);

        assert_eq!(counted, 4);
        assert_eq!(
            read_progress(&progress_path)["stageKey"],
            "test.cache.sweeping"
        );

        let outcome = remove_one(
            &root,
            "riot",
            "/bundle/one",
            cache_utils::CacheKeyScheme::Monolithic,
            &progress_path,
        );
        assert_eq!(outcome.deleted_files, counted);
    }

    #[test]
    fn the_sweep_runs_when_slice_zero_is_gone() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let base = cache_utils::object_key_base("riot", "/bundle/one").unwrap();
        write_keyed_cache_file(&root, &slice_key(&base, 400));
        let progress_path = temp.path().join("progress.json");

        assert_eq!(count_riot_object(&root, temp.path(), &progress_path), 1);
    }

    #[test]
    fn object_key_bases_hashes_the_unsliced_key_each_slice_strips_back_to() {
        let service = "wsus".to_string();
        let url = "/filestreamingservice/files/object".to_string();

        let bases = object_key_bases([(&service, &url)].into_iter());

        let slice_key = format!(
            "{}bytes=0-1048575",
            cache_utils::object_key_base(&service, &url).unwrap()
        );
        let from_header =
            cache_utils::calculate_md5_digest(cache_utils::cache_key_base_of(&slice_key));
        assert!(bases.contains(&from_header));
    }

    fn assert_single<T>(mut items: Vec<T>) -> T {
        assert_eq!(items.len(), 1);
        items.pop().unwrap()
    }
}
