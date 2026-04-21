use anyhow::{Context, Result};
use clap::Parser;
use jwalk::WalkDir;
use rayon::prelude::*;
use serde::Serialize;
use serde_json::json;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

mod cache_utils;
mod cache_detect_matching;
mod cache_detect_queries;
mod db;
mod progress_events;
mod progress_utils;

use cache_detect_matching::{
    detect_epic_game_cache_info,
    detect_epic_game_cache_info_incremental,
    detect_service_cache_info,
    detect_service_cache_info_incremental,
    detect_steam_game_cache_info,
    detect_steam_game_cache_info_incremental,
    group_epic_records,
    group_game_records,
};
use cache_detect_queries::{
    query_epic_game_downloads,
    query_game_downloads,
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

#[derive(Debug, Clone)]
struct CacheFileInfo {
    path: PathBuf,
    size: u64,
    // Note: hash is stored as HashMap key, not needed in struct
}

/// Scan cache directory and build in-memory index of all cache files
/// This is much faster than checking 2.3M individual file.exists() calls
fn scan_cache_directory(cache_dir: &Path) -> Result<HashMap<String, CacheFileInfo>> {
    eprintln!("\n=== Phase 1: Scanning Cache Directory ===");
    eprintln!("Building in-memory index of cache files...");

    let counter = AtomicUsize::new(0);

    // Use jwalk for parallel directory walking (4x faster than walkdir)
    let cache_files: HashMap<String, CacheFileInfo> = WalkDir::new(cache_dir)
        .parallelism(jwalk::Parallelism::RayonNewPool(num_cpus::get()))
        .into_iter()
        .filter_map(|entry| entry.ok())
        .filter(|entry| entry.file_type().is_file())
        .par_bridge()
        .filter_map(|entry| {
            let path = entry.path();

            // Extract hash from path (last component of path)
            let hash = path.file_name().and_then(|n| n.to_str())?;
            let metadata = entry.metadata().ok()?;
            let size = metadata.len();

            // Progress counter
            let count = counter.fetch_add(1, Ordering::Relaxed) + 1;
            if count % 10000 == 0 {
                eprint!("\r  Scanned {} files...", count);
            }

            Some((
                hash.to_string(),
                CacheFileInfo {
                    path: path.to_path_buf(),
                    size,
                },
            ))
        })
        .collect();

    let total = cache_files.len();
    let total_size_gb = cache_files.values().map(|f| f.size).sum::<u64>() as f64 / 1_073_741_824.0;

    eprintln!("\r  Found {} cache files ({:.2} GB total)", total, total_size_gb);

    Ok(cache_files)
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let cache_dir = PathBuf::from(&args.cache_dir);
    let output_json = PathBuf::from(&args.output_json);
    let incremental_mode = args.incremental;
    let progress_path = args.progress_file.map(PathBuf::from);

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

    // Emit started event
    reporter.emit_started("signalr.gameDetect.starting.default", json!({}));

    // Write initial progress
    write_progress(progress_path.as_deref(), "starting", "signalr.gameDetect.starting.default", json!({}), 0.0, 0, 0)?;
    reporter.emit_progress(0.0, "signalr.gameDetect.starting.default", json!({}));

    if !cache_dir.exists() {
        anyhow::bail!("Cache directory not found: {}", cache_dir.display());
    }

    let pool = db::create_pool().await?;

    // Variables that may or may not be used depending on mode
    let cache_files_index: Option<HashMap<String, CacheFileInfo>>;

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

    // Query ALL URLs to get accurate cache sizes (no sampling)
    let all_records = query_game_downloads(&pool, None, &excluded_game_ids).await?;
    write_progress(progress_path.as_deref(), "querying", "signalr.gameDetect.db.complete", json!({}), 30.0, 0, 0)?;
    reporter.emit_progress(30.0, "signalr.gameDetect.db.complete", json!({}));

    // Group records by game_app_id
    let games_map = group_game_records(all_records);

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
            let progress_percent = 30.0 + (processed_count as f64 / total_games.max(1) as f64) * 50.0;
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
            detect_steam_game_cache_info(&records, cache_index)
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
    eprintln!("\n=== Phase 3b: Detecting Epic Games ===");
    write_progress(progress_path.as_deref(), "matching", "signalr.gameDetect.epic.detecting", json!({}), 78.0, 0, 0)?;
    reporter.emit_progress(78.0, "signalr.gameDetect.epic.detecting", json!({}));

    let epic_records = query_epic_game_downloads(&pool).await?;

    if !epic_records.is_empty() {
        let epic_map = group_epic_records(&epic_records);

        let total_epic = epic_map.len();
        eprintln!("Found {} unique Epic games to check", total_epic);

        let mut epic_processed = 0;
        for (epic_id, (game_name, service_urls)) in &epic_map {
            epic_processed += 1;
            eprint!("  [{}/{}] Matching Epic game {} ({}) - {} URLs... ",
                    epic_processed, total_epic, epic_id, game_name, service_urls.len());

            let result = if incremental_mode {
                detect_epic_game_cache_info_incremental(epic_id, game_name, service_urls, &cache_dir)
            } else {
                let cache_index = cache_files_index
                    .as_ref()
                    .context("Cache file index missing during full Epic scan")?;
                detect_epic_game_cache_info(epic_id, game_name, service_urls, cache_index)
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

    eprintln!("\nTotal game cache files found: {}", total_files_found);
    eprintln!("Total game cache size: {:.2} GB", total_bytes_found as f64 / 1_073_741_824.0);

    // PHASE 4: Detect non-game services
    let mut detected_services = Vec::new();
    let mut service_files_found = 0;
    let mut service_bytes_found: u64 = 0;

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
        services_processed += 1;
        eprint!("  Matching service '{}' - {} URLs... ", service_name, service_urls.len());

        // Update progress for services (80-90%)
        let service_percent = 80.0 + (services_processed as f64 / total_services.max(1) as f64) * 10.0;
        write_progress(
            progress_path.as_deref(),
            "services",
            "signalr.gameDetect.services.progress",
            json!({ "processed": services_processed, "total": total_services }),
            service_percent,
            services_processed,
            total_services,
        )?;
        reporter.emit_progress(service_percent, "signalr.gameDetect.services.progress", json!({ "processed": services_processed, "total": total_services }));

        let result = if incremental_mode {
            detect_service_cache_info_incremental(&service_name, &service_urls, &cache_dir)
        } else {
            let cache_index = cache_files_index
                .as_ref()
                .context("Cache file index missing during service scan")?;

            detect_service_cache_info(&service_name, &service_urls, cache_index)
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

    let report = DetectionReport {
        total_games_detected: detected_games.len(),
        total_services_detected: detected_services.len(),
        games: detected_games,
        services: detected_services,
    };

    // Writing output (90-100%)
    write_progress(progress_path.as_deref(), "writing", "signalr.gameDetect.writing", json!({}), 90.0, 0, 0)?;
    reporter.emit_progress(90.0, "signalr.gameDetect.writing", json!({}));

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    let epic_game_count = report.games.iter().filter(|g| g.epic_app_id.is_some()).count();
    let steam_game_count = report.total_games_detected - epic_game_count;

    eprintln!("\n=== Detection Summary ===");
    eprintln!("Steam games with cache files: {}", steam_game_count);
    eprintln!("Epic games with cache files: {}", epic_game_count);
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
}
