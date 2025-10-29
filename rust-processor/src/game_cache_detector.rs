use anyhow::{Context, Result};
use jwalk::WalkDir;
use rayon::prelude::*;
use rusqlite::Connection;
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

mod progress_utils;

#[derive(Debug, Serialize)]
struct GameCacheInfo {
    game_app_id: u32,
    game_name: String,
    cache_files_found: usize,
    total_size_bytes: u64,
    depot_ids: Vec<u32>,
    sample_urls: Vec<String>,
    cache_file_paths: Vec<String>,
}

#[derive(Debug, Serialize)]
struct DetectionReport {
    total_games_detected: usize,
    games: Vec<GameCacheInfo>,
}

#[derive(Debug)]
struct DownloadRecord {
    service: String,
    game_app_id: u32,
    game_name: String,
    url: String,
    depot_id: Option<u32>,
}

#[derive(Debug, Clone)]
struct CacheFileInfo {
    path: PathBuf,
    size: u64,
    // Note: hash is stored as HashMap key, not needed in struct
}

fn calculate_md5(cache_key: &str) -> String {
    format!("{:x}", md5::compute(cache_key.as_bytes()))
}

/// Scan cache directory and build in-memory index of all cache files
/// This is much faster than checking 2.3M individual file.exists() calls
fn scan_cache_directory(cache_dir: &Path) -> Result<HashMap<String, CacheFileInfo>> {
    eprintln!("\n=== Phase 1: Scanning Cache Directory ===");
    eprintln!("Building in-memory index of cache files...");

    let cache_files = Arc::new(Mutex::new(HashMap::new()));
    let counter = Arc::new(AtomicUsize::new(0));

    // Use jwalk for parallel directory walking (4x faster than walkdir)
    WalkDir::new(cache_dir)
        .parallelism(jwalk::Parallelism::RayonNewPool(num_cpus::get()))
        .into_iter()
        .filter_map(|entry| entry.ok())
        .filter(|entry| entry.file_type().is_file())
        .par_bridge()
        .for_each(|entry| {
            let path = entry.path();

            // Extract hash from path (last component of path)
            if let Some(hash) = path.file_name().and_then(|n| n.to_str()) {
                if let Ok(metadata) = entry.metadata() {
                    let size = metadata.len();

                    let info = CacheFileInfo {
                        path: path.to_path_buf(),
                        size,
                    };

                    let mut files = cache_files.lock().unwrap();
                    files.insert(hash.to_string(), info);
                    drop(files);

                    // Progress counter
                    let count = counter.fetch_add(1, Ordering::Relaxed) + 1;
                    if count % 10000 == 0 {
                        eprint!("\r  Scanned {} files...", count);
                    }
                }
            }
        });

    let cache_files = Arc::try_unwrap(cache_files).unwrap().into_inner().unwrap();
    let total = cache_files.len();
    let total_size_gb = cache_files.values().map(|f| f.size).sum::<u64>() as f64 / 1_073_741_824.0;

    eprintln!("\r  ✓ Found {} cache files ({:.2} GB total)", total, total_size_gb);

    Ok(cache_files)
}

fn query_game_downloads(db_path: &Path, max_urls_per_game: Option<usize>) -> Result<Vec<DownloadRecord>> {
    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    eprintln!("Querying LogEntries for game URLs...");

    // Query LogEntries joined with SteamDepotMappings to get URLs for mapped games
    // Strategy: Get a sample of URLs per game for faster scanning
    let query = if let Some(limit) = max_urls_per_game {
        eprintln!("Using sampling strategy: max {} URLs per game", limit);
        format!(
            "SELECT le.Service, sdm.AppId, sdm.AppName, le.Url, le.DepotId
             FROM LogEntries le
             INNER JOIN SteamDepotMappings sdm ON le.DepotId = sdm.DepotId
             WHERE sdm.AppId IS NOT NULL AND le.Url IS NOT NULL
             GROUP BY sdm.AppId, le.Url
             ORDER BY sdm.AppId, le.BytesServed DESC"
        )
    } else {
        "SELECT DISTINCT le.Service, sdm.AppId, sdm.AppName, le.Url, le.DepotId
         FROM LogEntries le
         INNER JOIN SteamDepotMappings sdm ON le.DepotId = sdm.DepotId
         WHERE sdm.AppId IS NOT NULL AND le.Url IS NOT NULL
         ORDER BY sdm.AppId".to_string()
    };

    let mut stmt = conn.prepare(&query)?;

    let mut records: Vec<DownloadRecord> = Vec::new();
    let mut current_game_id: Option<u32> = None;
    let mut current_game_count = 0;

    let rows = stmt.query_map([], |row| {
        Ok(DownloadRecord {
            service: row.get(0)?,
            game_app_id: row.get(1)?,
            game_name: row.get(2)?,
            url: row.get(3)?,
            depot_id: row.get(4)?,
        })
    })?;

    for row_result in rows {
        if let Ok(record) = row_result {
            // Apply per-game limit if specified
            if let Some(limit) = max_urls_per_game {
                if Some(record.game_app_id) != current_game_id {
                    current_game_id = Some(record.game_app_id);
                    current_game_count = 0;
                }

                if current_game_count >= limit {
                    continue;
                }
                current_game_count += 1;
            }

            records.push(record);
        }
    }

    eprintln!("Found {} URLs across all mapped games", records.len());

    // Also query unknown games (depots without mappings)
    eprintln!("Querying unknown games...");
    let unknown_query = if let Some(limit) = max_urls_per_game {
        format!(
            "SELECT le.Service, le.DepotId, le.Url
             FROM LogEntries le
             WHERE le.DepotId IS NOT NULL
             AND le.Url IS NOT NULL
             AND le.DepotId NOT IN (SELECT DepotId FROM SteamDepotMappings)
             GROUP BY le.DepotId, le.Url
             LIMIT {}",
            limit * 10 // Allow more URLs for unknown games combined
        )
    } else {
        "SELECT DISTINCT le.Service, le.DepotId, le.Url
         FROM LogEntries le
         WHERE le.DepotId IS NOT NULL
         AND le.Url IS NOT NULL
         AND le.DepotId NOT IN (SELECT DepotId FROM SteamDepotMappings)
         ORDER BY le.DepotId".to_string()
    };

    let mut unknown_stmt = conn.prepare(&unknown_query)?;

    let mut unknown_current_depot: Option<u32> = None;
    let mut unknown_depot_count = 0;

    let unknown_rows = unknown_stmt.query_map([], |row| {
        let depot_id: u32 = row.get(1)?;
        Ok((row.get::<_, String>(0)?, depot_id, row.get::<_, String>(2)?))
    })?;

    for row_result in unknown_rows {
        if let Ok((service, depot_id, url)) = row_result {
            // Apply per-depot limit for unknown games
            if let Some(limit) = max_urls_per_game {
                if Some(depot_id) != unknown_current_depot {
                    unknown_current_depot = Some(depot_id);
                    unknown_depot_count = 0;
                }

                if unknown_depot_count >= limit {
                    continue;
                }
                unknown_depot_count += 1;
            }

            records.push(DownloadRecord {
                service,
                game_app_id: depot_id,
                game_name: format!("Unknown Game (Depot {})", depot_id),
                url,
                depot_id: Some(depot_id),
            });
        }
    }

    eprintln!("Found {} total URLs to check", records.len());

    Ok(records)
}

fn detect_cache_files_for_game(
    records: &[DownloadRecord],
    cache_files_index: &HashMap<String, CacheFileInfo>,
) -> Result<GameCacheInfo> {
    if records.is_empty() {
        anyhow::bail!("No records provided");
    }

    let game_app_id = records[0].game_app_id;
    let game_name = records[0].game_name.clone();

    // Collect unique URLs and depot IDs
    let mut unique_urls: HashSet<String> = HashSet::new();
    let mut depot_ids: HashSet<u32> = HashSet::new();
    let mut service_urls: Vec<(String, String)> = Vec::new();

    for record in records {
        unique_urls.insert(record.url.clone());
        // Lowercase service name to match cache file format
        let service_lower = record.service.to_lowercase();
        service_urls.push((service_lower, record.url.clone()));

        if let Some(depot_id) = record.depot_id {
            depot_ids.insert(depot_id);
        }
    }

    // Match URLs against in-memory cache index (instant lookups instead of filesystem checks!)
    let found_files: HashSet<PathBuf> = service_urls
        .par_iter()
        .filter_map(|(service, url)| {
            // Calculate hash for this service+url combination
            let cache_key = format!("{}{}", service, url);
            let hash = calculate_md5(&cache_key);

            // Instant HashMap lookup instead of file.exists()!
            if let Some(file_info) = cache_files_index.get(&hash) {
                Some(file_info.path.clone())
            } else {
                // Also check chunked format (bytes range) for backwards compatibility
                (0..100)
                    .find_map(|chunk| {
                        let start = chunk * 1_048_576;
                        let end = start + 1_048_575;
                        let chunked_key = format!("{}{}bytes={}-{}", service, url, start, end);
                        let chunked_hash = calculate_md5(&chunked_key);
                        cache_files_index.get(&chunked_hash).map(|f| f.path.clone())
                    })
            }
        })
        .collect();

    // Calculate total size from found files
    let total_size: u64 = found_files
        .iter()
        .filter_map(|path| {
            path.file_name()
                .and_then(|n| n.to_str())
                .and_then(|hash| cache_files_index.get(hash))
                .map(|info| info.size)
        })
        .sum();

    let sample_urls: Vec<String> = unique_urls.iter().take(5).cloned().collect();

    // Convert cache file paths to strings for output
    let cache_file_paths: Vec<String> = found_files
        .iter()
        .map(|p| p.display().to_string())
        .collect();

    Ok(GameCacheInfo {
        game_app_id,
        game_name,
        cache_files_found: found_files.len(),
        total_size_bytes: total_size,
        depot_ids: depot_ids.into_iter().collect(),
        sample_urls,
        cache_file_paths,
    })
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 4 {
        eprintln!("Usage: {} <database_path> <cache_dir> <output_json>", args[0]);
        eprintln!();
        eprintln!("Detects which games have files in the cache directory.");
        eprintln!();
        eprintln!("Arguments:");
        eprintln!("  database_path - Path to LancacheManager.db");
        eprintln!("  cache_dir     - Cache directory root (e.g., /cache or H:/cache)");
        eprintln!("  output_json   - Path to output JSON report");
        std::process::exit(1);
    }

    let db_path = PathBuf::from(&args[1]);
    let cache_dir = PathBuf::from(&args[2]);
    let output_json = PathBuf::from(&args[3]);

    eprintln!("Game Cache Detection");
    eprintln!("  Database: {}", db_path.display());
    eprintln!("  Cache directory: {}", cache_dir.display());

    if !db_path.exists() {
        anyhow::bail!("Database not found: {}", db_path.display());
    }

    if !cache_dir.exists() {
        anyhow::bail!("Cache directory not found: {}", cache_dir.display());
    }

    // PHASE 1: Scan cache directory and build in-memory index
    // This is much faster than checking 2.3M individual file.exists() calls
    let cache_files_index = scan_cache_directory(&cache_dir)?;

    // PHASE 2: Query database for game URLs
    eprintln!("\n=== Phase 2: Querying Database ===");

    // Query ALL URLs to get accurate cache sizes (no sampling)
    // This ensures we find and measure every cache file for complete accuracy
    let all_records = query_game_downloads(&db_path, None)?;

    if all_records.is_empty() {
        eprintln!("No games found in database.");
        let report = DetectionReport {
            total_games_detected: 0,
            games: vec![],
        };

        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;
        eprintln!("Report saved to: {}", output_json.display());
        return Ok(());
    }

    // Group records by game_app_id
    let mut games_map: HashMap<u32, Vec<DownloadRecord>> = HashMap::new();
    for record in all_records {
        games_map
            .entry(record.game_app_id)
            .or_insert_with(Vec::new)
            .push(record);
    }

    let total_games = games_map.len();
    eprintln!("Found {} unique games in database", total_games);

    // Calculate total URLs to give user an idea of scope
    let total_urls: usize = games_map.values().map(|v| v.len()).sum();
    eprintln!("Total URLs to match: {} across all games", total_urls);

    // PHASE 3: Match database URLs to cache files
    eprintln!("\n=== Phase 3: Matching Games to Cache Files ===");
    eprintln!("Using in-memory index for instant lookups...\n");

    let mut detected_games = Vec::new();
    let mut processed_count = 0;
    let mut total_files_found = 0;
    let mut total_bytes_found: u64 = 0;

    for (game_id, records) in games_map {
        processed_count += 1;
        let url_count = records.len();
        eprint!("  [{}/{}] Matching game {} ({}) - {} URLs... ",
                processed_count, total_games, game_id, records[0].game_name, url_count);

        match detect_cache_files_for_game(&records, &cache_files_index) {
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

    eprintln!("\n=== Scan Complete ===");
    eprintln!("Total cache files found: {}", total_files_found);
    eprintln!("Total cache size: {:.2} GB", total_bytes_found as f64 / 1_073_741_824.0);

    let report = DetectionReport {
        total_games_detected: detected_games.len(),
        games: detected_games,
    };

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    eprintln!("\n=== Detection Summary ===");
    eprintln!("Games with cache files: {}", report.total_games_detected);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
}
