use anyhow::{Context, Result};
use clap::Parser;
use jwalk::WalkDir;
use rayon::prelude::*;
use serde::Serialize;
use serde_json::json;
use std::collections::HashMap;
use std::fs;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

mod cancel;
mod cache_utils;
mod cache_detect_matching;
mod cache_detect_queries;
mod db;
mod progress_events;
mod progress_utils;

use cache_detect_matching::{
    detect_epic_game_cache_info,
    detect_epic_game_cache_info_incremental,
    detect_named_game_cache_info,
    detect_named_game_cache_info_incremental,
    detect_service_cache_info,
    detect_service_cache_info_incremental,
    detect_steam_game_cache_info,
    detect_steam_game_cache_info_incremental,
    group_epic_records,
    group_named_records,
};
use cache_detect_queries::{
    query_epic_game_downloads,
    query_game_downloads,
    query_named_game_downloads,
    query_service_downloads,
};
use progress_events::ProgressReporter;

/// Game cache detection utility - detects which games have files in the cache
#[derive(Parser, Debug)]
#[command(name = "cache_game_detect")]
#[command(about = "Detects which games have files in the cache directory")]
struct Args {
    /// Cache directory root (e.g., /cache or H:/cache)
    cache_dir: String,

    /// Path to output JSON report
    output_json: String,

    /// JSON file with array of game IDs to exclude for incremental scanning
    excluded_game_ids_json: Option<String>,

    /// Skip cache directory scan for faster incremental updates
    #[arg(long)]
    incremental: bool,

    /// Skip non-game service detection and preserve existing service rows from the caller
    #[arg(long = "skip-service-scan")]
    skip_service_scan: bool,

    /// Path to write progress JSON updates
    #[arg(long = "progress-file")]
    progress_file: Option<String>,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ProgressData {
    status: String,
    stage_key: String,
    context: serde_json::Value,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    #[serde(rename = "gamesProcessed")]
    games_processed: usize,
    #[serde(rename = "totalGames")]
    total_games: usize,
    timestamp: String,
}

fn write_progress(
    progress_path: Option<&Path>,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    games_processed: usize,
    total_games: usize,
) -> Result<()> {
    if let Some(path) = progress_path {
        let progress = ProgressData {
            status: status.to_string(),
            stage_key: stage_key.to_string(),
            context,
            percent_complete,
            games_processed,
            total_games,
            timestamp: progress_utils::current_timestamp(),
        };
        progress_utils::write_progress_json(path, &progress)?;
    }
    Ok(())
}

#[derive(Debug, Serialize)]
struct GameCacheInfo {
    game_app_id: u32,
    game_name: String,
    cache_files_found: usize,
    total_size_bytes: u64,
    depot_ids: Vec<u32>,
    sample_urls: Vec<String>,
    cache_file_paths: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    service: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    epic_app_id: Option<String>,
}

#[derive(Debug, Serialize)]
struct ServiceCacheInfo {
    service_name: String,
    cache_files_found: usize,
    total_size_bytes: u64,
    sample_urls: Vec<String>,
    cache_file_paths: Vec<String>,
}

#[derive(Debug, Serialize)]
struct DetectionReport {
    total_games_detected: usize,
    total_services_detected: usize,
    games: Vec<GameCacheInfo>,
    services: Vec<ServiceCacheInfo>,
}

/// Scan cache directory and build in-memory index of all cache files
/// This is much faster than checking 2.3M individual file.exists() calls
///
/// Index = file-name digest -> size. A lancache file's name IS the md5 of its cache key, so
/// the parsed u128 fully identifies the file; storing no path keeps the multi-million-file
/// index to ~24 bytes per entry instead of ~200. Report paths are reconstructed from the
/// digest via `cache_utils::cache_path_for_digest` (the `levels=2:2` layout every fs-probing
/// path in this binary already assumes).
fn scan_cache_directory(cache_dir: &Path) -> Result<HashMap<u128, u64>> {
    eprintln!("\n=== Phase 1: Scanning Cache Directory ===");
    eprintln!("Building in-memory index of cache files...");

    let counter = AtomicUsize::new(0);
    let non_hash_names = AtomicUsize::new(0);

    // Use jwalk for parallel directory walking (4x faster than walkdir)
    let cache_files: HashMap<u128, u64> = WalkDir::new(cache_dir)
        .parallelism(jwalk::Parallelism::RayonNewPool(num_cpus::get()))
        .into_iter()
        .filter_map(|entry| entry.ok())
        .filter(|entry| entry.file_type().is_file())
        .par_bridge()
        .filter_map(|entry| {
            // Progress counter
            let count = counter.fetch_add(1, Ordering::Relaxed) + 1;
            if count % 10000 == 0 {
                eprint!("\r  Scanned {} files...", count);
            }

            let digest = entry
                .file_name()
                .to_str()
                .and_then(cache_utils::parse_cache_file_digest);
            let Some(digest) = digest else {
                // Not a 32-hex md5 name -> can never match a probe candidate; keep it out of
                // the index but count it so a foreign cache layout stays visible.
                non_hash_names.fetch_add(1, Ordering::Relaxed);
                return None;
            };

            let metadata = entry.metadata().ok()?;
            Some((digest, metadata.len()))
        })
        .collect();

    let total = cache_files.len();
    let total_size_gb = cache_files.values().copied().sum::<u64>() as f64 / 1_073_741_824.0;

    eprintln!("\r  Found {} cache files ({:.2} GB total)", total, total_size_gb);
    let skipped = non_hash_names.load(Ordering::Relaxed);
    if skipped > 0 {
        eprintln!(
            "  Skipped {} file(s) whose names are not 32-hex md5 cache keys (nginx temp or foreign files)",
            skipped
        );
    }

    Ok(cache_files)
}

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();

    let args = Args::parse();
    let reporter = Arc::new(ProgressReporter::new(args.progress));

    let cache_dir = PathBuf::from(&args.cache_dir);
    let output_json = PathBuf::from(&args.output_json);
    let incremental_mode = args.incremental;
    let progress_path = args.progress_file.map(PathBuf::from);

    // Whole scan routed through the single failure funnel: this bin previously had ZERO
    // structured `failed` events (every `?` reached the host as stderr + bare exit 1).
    // finish_or_exit is now the ONE place a scan failure gets emitted.
    let result: Result<()> = async {

    // Read excluded game IDs if provided (for incremental scanning)
    let excluded_game_ids: Vec<u32> = if let Some(ref excluded_path_str) = args.excluded_game_ids_json {
        let excluded_path = PathBuf::from(excluded_path_str);
        if excluded_path.exists() {
            let json = fs::read_to_string(&excluded_path)
                .context("Failed to read excluded game IDs file")?;
            serde_json::from_str(&json)
                .context("Failed to parse excluded game IDs JSON")?
        } else {
            eprintln!("Warning: Excluded game IDs file not found, proceeding with full scan");
            Vec::new()
        }
    } else {
        Vec::new()
    };

    eprintln!("Game Cache Detection");
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Mode: {}", if incremental_mode { "Incremental (Quick Scan)" } else { "Full Scan" });
    if let Some(ref path) = progress_path {
        eprintln!("  Progress file: {}", path.display());
    }

    // Write initial progress BEFORE the started event (file-write-before-stdout-emit
    // invariant): C#'s event callback reads the progress file the moment "started" arrives,
    // and the C#-created temp file is empty until this first write.
    write_progress(progress_path.as_deref(), "starting", "signalr.gameDetect.starting.default", json!({}), 0.0, 0, 0)?;

    // Emit started event
    reporter.emit_started("signalr.gameDetect.starting.default", json!({}));
    reporter.emit_progress(0.0, "signalr.gameDetect.starting.default", json!({}));

    if !cache_dir.exists() {
        anyhow::bail!("Cache directory not found: {}", cache_dir.display());
    }

    let pool = db::create_pool().await?;

    // Variables that may or may not be used depending on mode
    let cache_files_index: Option<HashMap<u128, u64>>;

    if incremental_mode {
        // INCREMENTAL MODE: Skip expensive cache directory scan
        eprintln!("\n=== Incremental Mode: Skipping Cache Directory Scan ===");
        eprintln!("Will check file existence directly for new games only...");
        write_progress(progress_path.as_deref(), "scanning", "signalr.gameDetect.scan.skippedIncremental", json!({}), 20.0, 0, 0)?;
        reporter.emit_progress(20.0, "signalr.gameDetect.scan.skippedIncremental", json!({}));
        cache_files_index = None;
    } else {
        // FULL SCAN: Build in-memory index of all cache files
        write_progress(progress_path.as_deref(), "scanning", "signalr.gameDetect.scan.inProgress", json!({}), 5.0, 0, 0)?;
        reporter.emit_progress(5.0, "signalr.gameDetect.scan.inProgress", json!({}));
        let index = scan_cache_directory(&cache_dir)?;
        write_progress(progress_path.as_deref(), "scanning", "signalr.gameDetect.scan.complete", json!({}), 20.0, 0, 0)?;
        reporter.emit_progress(20.0, "signalr.gameDetect.scan.complete", json!({}));
        cache_files_index = Some(index);
    }

    // PHASE 2: Query database for game URLs
    eprintln!("\n=== Phase 2: Querying Database ===");
    write_progress(progress_path.as_deref(), "querying", "signalr.gameDetect.db.querying", json!({}), 20.0, 0, 0)?;
    reporter.emit_progress(20.0, "signalr.gameDetect.db.querying", json!({}));

    // Query ALL URLs to get accurate cache sizes (no sampling); rows stream straight into
    // the per-game map inside the query helper.
    let games_map = query_game_downloads(&pool, None, &excluded_game_ids).await?;
    write_progress(progress_path.as_deref(), "querying", "signalr.gameDetect.db.complete", json!({}), 30.0, 0, 0)?;
    reporter.emit_progress(30.0, "signalr.gameDetect.db.complete", json!({}));

    let total_games = games_map.len();
    eprintln!("Found {} unique games in database", total_games);

    let total_urls: usize = games_map.values().map(|v| v.len()).sum();
    eprintln!("Total URLs to match: {} across all games", total_urls);

    // PHASE 3: Match database URLs to cache files
    eprintln!("\n=== Phase 3: Matching Games to Cache Files ===");
    if incremental_mode {
        eprintln!("Using direct file existence checks (optimized for few new games)...\n");
    } else {
        eprintln!("Using in-memory index for instant lookups...\n");
    }
    write_progress(progress_path.as_deref(), "matching", "signalr.gameDetect.matching.starting", json!({ "totalGames": total_games }), 30.0, 0, total_games)?;
    reporter.emit_progress(30.0, "signalr.gameDetect.matching.starting", json!({ "totalGames": total_games }));

    let mut detected_games = Vec::new();
    let mut processed_count = 0;
    let mut total_files_found = 0;
    let mut total_bytes_found: u64 = 0;
    let mut last_progress_update = 0;

    for (game_id, records) in games_map {
        // Cooperative cancel: check between Steam game iterations (read-only, safe to stop here)
        if cancel::is_cancelled() {
            eprintln!("\nCancel requested — stopping Steam game scan after {}/{} games", processed_count, total_games);
            let partial_percent = 30.0 + (processed_count as f64 / total_games.max(1) as f64) * 40.0;
            write_progress(
                progress_path.as_deref(),
                "cancelled",
                "signalr.gameDetect.matching.progress",
                json!({ "processed": processed_count, "totalGames": total_games }),
                partial_percent,
                processed_count,
                total_games,
            )?;
            reporter.emit_progress(partial_percent, "signalr.gameDetect.matching.progress", json!({ "processed": processed_count, "totalGames": total_games }));
            std::process::exit(0);
        }

        processed_count += 1;
        let url_count = records.len();
        eprint!("  [{}/{}] Matching game {} ({}) - {} URLs... ",
                processed_count, total_games, game_id, records[0].game_name, url_count);

        // Update progress every 5 games or every 10%
        let current_percent = if total_games > 0 {
            (processed_count as f64 / total_games as f64 * 100.0) as usize
        } else {
            0
        };
        if processed_count % 5 == 0 || current_percent >= last_progress_update + 10 || processed_count == total_games {
            let progress_percent = 30.0 + (processed_count as f64 / total_games.max(1) as f64) * 40.0;
            write_progress(
                progress_path.as_deref(),
                "matching",
                "signalr.gameDetect.matching.progress",
                json!({ "processed": processed_count, "totalGames": total_games }),
                progress_percent,
                processed_count,
                total_games,
            )?;
            reporter.emit_progress(progress_percent, "signalr.gameDetect.matching.progress", json!({ "processed": processed_count, "totalGames": total_games }));
            last_progress_update = current_percent;
        }

        let result = if incremental_mode {
            detect_steam_game_cache_info_incremental(&records, &cache_dir)
        } else {
            let cache_index = cache_files_index
                .as_ref()
                .context("Cache file index missing during full game scan")?;
            detect_steam_game_cache_info(&records, cache_index, &cache_dir)
        };

        match result {
            Ok(info) => {
                if info.cache_files_found > 0 {
                    let size_gb = info.total_size_bytes as f64 / 1_073_741_824.0;
                    let size_mb = info.total_size_bytes as f64 / 1_048_576.0;

                    if size_gb >= 1.0 {
                        eprintln!("FOUND {} files ({:.2} GB)", info.cache_files_found, size_gb);
                    } else {
                        eprintln!("FOUND {} files ({:.2} MB)", info.cache_files_found, size_mb);
                    }

                    total_files_found += info.cache_files_found;
                    total_bytes_found += info.total_size_bytes;
                    detected_games.push(info);
                } else {
                    eprintln!("no cache files found");
                }
            }
            Err(e) => {
                eprintln!("ERROR: {}", e);
            }
        }
    }

    eprintln!("\n=== Steam Game Scan Complete ===");
    eprintln!("Total Steam game cache files found: {}", total_files_found);
    eprintln!("Total Steam game cache size: {:.2} GB", total_bytes_found as f64 / 1_073_741_824.0);

    // PHASE 3b: Detect Epic games using cache file scanning (same approach as Steam)
    // Band 70.0 -> 75.0 so the phase advances perceptibly with per-game ticks
    // (matching Steam's `processed`/`totalGames` context shape) instead of a
    // single flash at one percent.
    eprintln!("\n=== Phase 3b: Detecting Epic Games ===");
    write_progress(progress_path.as_deref(), "matching", "signalr.gameDetect.epic.detecting", json!({}), 70.0, 0, 0)?;
    reporter.emit_progress(70.0, "signalr.gameDetect.epic.detecting", json!({}));

    let epic_records = query_epic_game_downloads(&pool).await?;

    if !epic_records.is_empty() {
        let epic_map = group_epic_records(&epic_records);

        let total_epic = epic_map.len();
        eprintln!("Found {} unique Epic games to check", total_epic);

        let mut epic_processed = 0;
        for (epic_id, (game_name, service_urls)) in &epic_map {
            // Cooperative cancel: check between Epic game iterations
            if cancel::is_cancelled() {
                eprintln!("\nCancel requested — stopping Epic game scan after {}/{} games", epic_processed, total_epic);
                write_progress(
                    progress_path.as_deref(),
                    "cancelled",
                    "signalr.gameDetect.epic.progress",
                    json!({ "processed": epic_processed, "totalGames": total_epic, "name": game_name }),
                    70.0 + (epic_processed as f64 / total_epic.max(1) as f64) * 5.0,
                    processed_count + epic_processed,
                    total_games + total_epic,
                )?;
                std::process::exit(0);
            }

            epic_processed += 1;
            eprint!("  [{}/{}] Matching Epic game {} ({}) - {} URLs... ",
                    epic_processed, total_epic, epic_id, game_name, service_urls.len());

            // Per-game progress so the Epic phase visibly advances across its band.
            let epic_percent = 70.0 + (epic_processed as f64 / total_epic.max(1) as f64) * 5.0;
            let epic_context = json!({
                "processed": epic_processed,
                "totalGames": total_epic,
                "name": game_name,
            });
            write_progress(
                progress_path.as_deref(),
                "matching",
                "signalr.gameDetect.epic.progress",
                epic_context.clone(),
                epic_percent,
                processed_count + epic_processed,
                total_games + total_epic,
            )?;
            reporter.emit_progress(epic_percent, "signalr.gameDetect.epic.progress", epic_context);

            let result = if incremental_mode {
                detect_epic_game_cache_info_incremental(epic_id, game_name, service_urls, &cache_dir)
            } else {
                let cache_index = cache_files_index
                    .as_ref()
                    .context("Cache file index missing during full Epic scan")?;
                detect_epic_game_cache_info(epic_id, game_name, service_urls, cache_index, &cache_dir)
            };

            match result {
                Ok(info) => {
                    if info.cache_files_found > 0 {
                        let size_gb = info.total_size_bytes as f64 / 1_073_741_824.0;
                        let size_mb = info.total_size_bytes as f64 / 1_048_576.0;

                        if size_gb >= 1.0 {
                            eprintln!("FOUND {} files ({:.2} GB)", info.cache_files_found, size_gb);
                        } else {
                            eprintln!("FOUND {} files ({:.2} MB)", info.cache_files_found, size_mb);
                        }

                        total_files_found += info.cache_files_found;
                        total_bytes_found += info.total_size_bytes;
                        detected_games.push(info);
                    } else {
                        eprintln!("no cache files found");
                    }
                }
                Err(e) => {
                    eprintln!("ERROR: {}", e);
                }
            }
        }

        eprintln!("\n=== Epic Game Scan Complete ===");
    } else {
        eprintln!("No Epic games found in database");
    }

    // PHASE 3c: Detect name-keyed games (Blizzard/Riot) - same approach as Epic, but
    // identity = (Service, GameName) and GameAppId stays 0 (no AppId, no EpicAppId).
    // Band 75.0 -> 80.0 with per-game ticks so the Blizzard/Riot phase is as
    // visible as Steam/Epic (was a single flash inside a 1% band, 79->80).
    eprintln!("\n=== Phase 3c: Detecting Named (Blizzard/Riot) Games ===");
    write_progress(progress_path.as_deref(), "matching", "signalr.gameDetect.named.detecting", json!({}), 75.0, 0, 0)?;
    reporter.emit_progress(75.0, "signalr.gameDetect.named.detecting", json!({}));

    let named_records = query_named_game_downloads(&pool).await?;

    if !named_records.is_empty() {
        let named_map = group_named_records(&named_records);

        let total_named = named_map.len();
        eprintln!("Found {} unique named games to check", total_named);

        let mut named_processed = 0;
        for (_key, (service, game_name, service_urls)) in &named_map {
            // Cooperative cancel: check between named game iterations
            if cancel::is_cancelled() {
                eprintln!("\nCancel requested — stopping named game scan after {}/{} games", named_processed, total_named);
                write_progress(
                    progress_path.as_deref(),
                    "cancelled",
                    "signalr.gameDetect.named.progress",
                    json!({ "processed": named_processed, "totalGames": total_named, "name": game_name }),
                    75.0 + (named_processed as f64 / total_named.max(1) as f64) * 5.0,
                    processed_count + named_processed,
                    total_games + total_named,
                )?;
                std::process::exit(0);
            }

            named_processed += 1;
            eprint!("  [{}/{}] Matching named game {}/{} - {} URLs... ",
                    named_processed, total_named, service, game_name, service_urls.len());

            // Per-game progress so the named phase visibly advances across its band.
            let named_percent = 75.0 + (named_processed as f64 / total_named.max(1) as f64) * 5.0;
            let named_context = json!({
                "processed": named_processed,
                "totalGames": total_named,
                "name": game_name,
            });
            write_progress(
                progress_path.as_deref(),
                "matching",
                "signalr.gameDetect.named.progress",
                named_context.clone(),
                named_percent,
                processed_count + named_processed,
                total_games + total_named,
            )?;
            reporter.emit_progress(named_percent, "signalr.gameDetect.named.progress", named_context);

            let result = if incremental_mode {
                detect_named_game_cache_info_incremental(service, game_name, service_urls, &cache_dir)
            } else {
                let cache_index = cache_files_index
                    .as_ref()
                    .context("Cache file index missing during full named game scan")?;
                detect_named_game_cache_info(service, game_name, service_urls, cache_index, &cache_dir)
            };

            match result {
                Ok(info) => {
                    if info.cache_files_found > 0 {
                        let size_gb = info.total_size_bytes as f64 / 1_073_741_824.0;
                        let size_mb = info.total_size_bytes as f64 / 1_048_576.0;

                        if size_gb >= 1.0 {
                            eprintln!("FOUND {} files ({:.2} GB)", info.cache_files_found, size_gb);
                        } else {
                            eprintln!("FOUND {} files ({:.2} MB)", info.cache_files_found, size_mb);
                        }

                        total_files_found += info.cache_files_found;
                        total_bytes_found += info.total_size_bytes;
                        detected_games.push(info);
                    } else {
                        eprintln!("no cache files found");
                    }
                }
                Err(e) => {
                    eprintln!("ERROR: {}", e);
                }
            }
        }

        eprintln!("\n=== Named Game Scan Complete ===");
    } else {
        eprintln!("No named (Blizzard/Riot) games found in database");
    }

    eprintln!("\nTotal game cache files found: {}", total_files_found);
    eprintln!("Total game cache size: {:.2} GB", total_bytes_found as f64 / 1_073_741_824.0);

    // PHASE 4: Detect non-game services
    let mut detected_services = Vec::new();
    let mut service_files_found = 0;
    let mut service_bytes_found: u64 = 0;

    if args.skip_service_scan {
        eprintln!("\n=== Phase 4: Skipping Non-Game Service Detection ===");
        eprintln!("Preserving existing service detections from the caller");
    } else {
        eprintln!(
            "\n=== Phase 4: Detecting Non-Game Services{} ===",
            if incremental_mode {
                " (Incremental Mode)"
            } else {
                ""
            }
        );
        write_progress(progress_path.as_deref(), "services", "signalr.gameDetect.services.detecting", json!({}), 80.0, 0, 0)?;
        reporter.emit_progress(80.0, "signalr.gameDetect.services.detecting", json!({}));

        let services_map = query_service_downloads(&pool).await?;
        let total_services = services_map.len();
        let mut services_processed = 0;

        for (service_name, service_urls) in services_map {
            // Cooperative cancel: check between service iterations
            if cancel::is_cancelled() {
                eprintln!("\nCancel requested — stopping service scan after {}/{} services", services_processed, total_services);
                let partial_percent = 80.0 + (services_processed as f64 / total_services.max(1) as f64) * 10.0;
                write_progress(
                    progress_path.as_deref(),
                    "cancelled",
                    "signalr.gameDetect.services.progress",
                    json!({ "processed": services_processed, "total": total_services }),
                    partial_percent,
                    services_processed,
                    total_services,
                )?;
                std::process::exit(0);
            }

            services_processed += 1;
            let total_urls = service_urls.len();
            eprint!("  Matching service '{}' - {} URLs... ", service_name, total_urls);

            // Allocate the 80-90% band evenly across services so per-URL
            // progress can slide smoothly from base → base+span while this
            // service runs. Without this sub-progress, the final service
            // bucket (often "steam" with millions of URLs) would look frozen
            // for hours - see services_map iteration below.
            let service_span = 10.0 / total_services.max(1) as f64;
            let service_base_percent =
                80.0 + (services_processed as f64 - 1.0) * service_span;
            let service_end_percent = service_base_percent + service_span;

            write_progress(
                progress_path.as_deref(),
                "services",
                "signalr.gameDetect.services.progress",
                json!({
                    "processed": services_processed,
                    "total": total_services,
                    "service": service_name,
                    "urlsProcessed": 0usize,
                    "urlsTotal": total_urls,
                }),
                service_base_percent,
                services_processed,
                total_services,
            )?;
            reporter.emit_progress(
                service_base_percent,
                "signalr.gameDetect.services.progress",
                json!({
                    "processed": services_processed,
                    "total": total_services,
                    "service": service_name,
                    "urlsProcessed": 0usize,
                    "urlsTotal": total_urls,
                }),
            );

            // Shared counter incremented by rayon workers in match_files_*_tracked.
            // The monitor thread below reads it every 500ms and publishes progress
            // so the UI stays live even on million-URL buckets.
            let url_counter = Arc::new(AtomicUsize::new(0));
            let stop_monitor = Arc::new(AtomicBool::new(false));
            let monitor_handle = {
                let url_counter = Arc::clone(&url_counter);
                let stop_monitor = Arc::clone(&stop_monitor);
                let reporter = Arc::clone(&reporter);
                let progress_path_for_monitor = progress_path.clone();
                let service_name_for_monitor = service_name.clone();
                thread::spawn(move || {
                    while !stop_monitor.load(Ordering::Relaxed) && !cancel::is_cancelled() {
                        thread::sleep(Duration::from_millis(500));
                        if stop_monitor.load(Ordering::Relaxed) || cancel::is_cancelled() {
                            break;
                        }
                        let done = url_counter.load(Ordering::Relaxed);
                        let fraction = if total_urls == 0 {
                            1.0
                        } else {
                            (done as f64 / total_urls as f64).min(1.0)
                        };
                        let percent = service_base_percent + service_span * fraction;
                        let context = json!({
                            "processed": services_processed,
                            "total": total_services,
                            "service": service_name_for_monitor,
                            "urlsProcessed": done,
                            "urlsTotal": total_urls,
                        });
                        let _ = write_progress(
                            progress_path_for_monitor.as_deref(),
                            "services",
                            "signalr.gameDetect.services.progress",
                            context.clone(),
                            percent,
                            services_processed,
                            total_services,
                        );
                        reporter.emit_progress(
                            percent,
                            "signalr.gameDetect.services.progress",
                            context,
                        );
                    }
                })
            };

            let result = if incremental_mode {
                detect_service_cache_info_incremental(
                    &service_name,
                    &service_urls,
                    &cache_dir,
                    &url_counter,
                )
            } else {
                let cache_index = cache_files_index
                    .as_ref()
                    .context("Cache file index missing during service scan")?;

                detect_service_cache_info(
                    &service_name,
                    &service_urls,
                    cache_index,
                    &cache_dir,
                    &url_counter,
                )
            };

            stop_monitor.store(true, Ordering::Relaxed);
            let _ = monitor_handle.join();

            // Emit a final completion tick for this service so the UI hits the
            // band's top edge before the next service starts.
            let final_context = json!({
                "processed": services_processed,
                "total": total_services,
                "service": service_name,
                "urlsProcessed": total_urls,
                "urlsTotal": total_urls,
            });
            write_progress(
                progress_path.as_deref(),
                "services",
                "signalr.gameDetect.services.progress",
                final_context.clone(),
                service_end_percent,
                services_processed,
                total_services,
            )?;
            reporter.emit_progress(
                service_end_percent,
                "signalr.gameDetect.services.progress",
                final_context,
            );

            match result {
                Ok(info) => {
                    if info.cache_files_found > 0 {
                        let size_gb = info.total_size_bytes as f64 / 1_073_741_824.0;
                        let size_mb = info.total_size_bytes as f64 / 1_048_576.0;

                        if size_gb >= 1.0 {
                            eprintln!("FOUND {} files ({:.2} GB)", info.cache_files_found, size_gb);
                        } else {
                            eprintln!("FOUND {} files ({:.2} MB)", info.cache_files_found, size_mb);
                        }

                        service_files_found += info.cache_files_found;
                        service_bytes_found += info.total_size_bytes;
                        detected_services.push(info);
                    } else {
                        eprintln!("no cache files found");
                    }
                }
                Err(e) => {
                    eprintln!("ERROR: {}", e);
                }
            }
        }

        eprintln!("\n=== Service Scan Complete ===");
        eprintln!("Total service cache files found: {}", service_files_found);
        eprintln!("Total service cache size: {:.2} GB", service_bytes_found as f64 / 1_073_741_824.0);
    }

    // Phase 4 was the last consumer of the on-disk index; on a multi-million-file cache it
    // holds hundreds of MB, so it must be gone before the report (which carries its own copy
    // of every matched path) is serialized on top of it.
    drop(cache_files_index);

    let report = DetectionReport {
        total_games_detected: detected_games.len(),
        total_services_detected: detected_services.len(),
        games: detected_games,
        services: detected_services,
    };

    // Writing output (90-100%)
    write_progress(progress_path.as_deref(), "writing", "signalr.gameDetect.writing", json!({}), 90.0, 0, 0)?;
    reporter.emit_progress(90.0, "signalr.gameDetect.writing", json!({}));

    // Stream the report straight to the file: to_string_pretty would materialize the whole
    // JSON (all cache_file_paths again) as one giant String before writing.
    let output_file = fs::File::create(&output_json)
        .with_context(|| format!("Failed to create output file: {}", output_json.display()))?;
    let mut output_writer = BufWriter::new(output_file);
    serde_json::to_writer_pretty(&mut output_writer, &report)?;
    output_writer.flush()?;

    let epic_game_count = report.games.iter().filter(|g| g.epic_app_id.is_some()).count();
    // Named (Blizzard/Riot) games: GameAppId 0, no EpicAppId, but carry a non-steam service.
    let named_game_count = report
        .games
        .iter()
        .filter(|g| {
            g.epic_app_id.is_none()
                && g.game_app_id == 0
                && g.service.as_deref().map(|s| s != "steam").unwrap_or(false)
        })
        .count();
    let steam_game_count = report.total_games_detected - epic_game_count - named_game_count;

    eprintln!("\n=== Detection Summary ===");
    eprintln!("Steam games with cache files: {}", steam_game_count);
    eprintln!("Epic games with cache files: {}", epic_game_count);
    eprintln!("Named (Blizzard/Riot) games with cache files: {}", named_game_count);
    eprintln!("Total games with cache files: {}", report.total_games_detected);
    eprintln!("Services with cache files: {}", report.total_services_detected);
    eprintln!("Total cache files: {}", total_files_found + service_files_found);
    eprintln!("Total cache size: {:.2} GB", (total_bytes_found + service_bytes_found) as f64 / 1_073_741_824.0);
    eprintln!("Report saved to: {}", output_json.display());

    // Final completion progress
    write_progress(
        progress_path.as_deref(),
        "completed",
        "signalr.gameDetect.complete.default",
        json!({ "totalGames": report.total_games_detected, "totalServices": report.total_services_detected }),
        100.0,
        report.total_games_detected,
        report.total_games_detected,
    )?;
    reporter.emit_complete("signalr.gameDetect.complete.default", json!({ "totalGames": report.total_games_detected, "totalServices": report.total_services_detected }));

    Ok(())
    }.await;
    progress_events::finish_or_exit(&reporter, "signalr.gameDetect.error.fatal", result);
    Ok(())
}
