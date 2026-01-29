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

mod cache_utils;
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

fn query_game_downloads(db_path: &Path, max_urls_per_game: Option<usize>, excluded_game_ids: &[u32]) -> Result<Vec<DownloadRecord>> {
    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    eprintln!("Querying LogEntries for game URLs...");

    // Build exclusion clause if we have games to exclude
    let exclusion_clause = if !excluded_game_ids.is_empty() {
        let ids_str = excluded_game_ids.iter()
            .map(|id| id.to_string())
            .collect::<Vec<_>>()
            .join(",");
        format!("AND sdm.AppId NOT IN ({})", ids_str)
    } else {
        String::new()
    };

    if !excluded_game_ids.is_empty() {
        eprintln!("Excluding {} already-detected games (incremental scan)", excluded_game_ids.len());
    }

    // Query LogEntries joined with SteamDepotMappings to get URLs for mapped games
    // Strategy: Get a sample of URLs per game for faster scanning
    // IMPORTANT: Only match depots where IsOwner=1 to avoid attributing shared depots to multiple games
    // Use COALESCE to fall back to DepotName when AppName is NULL (for redistributables like Ubisoft Connect)
    let query = if let Some(limit) = max_urls_per_game {
        eprintln!("Using sampling strategy: max {} URLs per game", limit);
        format!(
            "SELECT le.Service, sdm.AppId, COALESCE(sdm.AppName, sdm.DepotName, 'App ' || sdm.AppId), le.Url, le.DepotId
             FROM LogEntries le
             INNER JOIN SteamDepotMappings sdm ON le.DepotId = sdm.DepotId
             WHERE sdm.AppId IS NOT NULL AND le.Url IS NOT NULL AND sdm.IsOwner = 1 {}
             GROUP BY sdm.AppId, le.Url
             ORDER BY sdm.AppId, le.BytesServed DESC",
            exclusion_clause
        )
    } else {
        format!(
            "SELECT DISTINCT le.Service, sdm.AppId, COALESCE(sdm.AppName, sdm.DepotName, 'App ' || sdm.AppId), le.Url, le.DepotId
             FROM LogEntries le
             INNER JOIN SteamDepotMappings sdm ON le.DepotId = sdm.DepotId
             WHERE sdm.AppId IS NOT NULL AND le.Url IS NOT NULL AND sdm.IsOwner = 1 {}
             ORDER BY sdm.AppId",
            exclusion_clause
        )
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

    // Build exclusion clause for unknown depots (using depot_id as game_app_id for unknown games)
    let unknown_exclusion_clause = if !excluded_game_ids.is_empty() {
        let ids_str = excluded_game_ids.iter()
            .map(|id| id.to_string())
            .collect::<Vec<_>>()
            .join(",");
        format!("AND le.DepotId NOT IN ({})", ids_str)
    } else {
        String::new()
    };

    let unknown_query = if let Some(limit) = max_urls_per_game {
        format!(
            "SELECT le.Service, le.DepotId, le.Url
             FROM LogEntries le
             WHERE le.DepotId IS NOT NULL
             AND le.Url IS NOT NULL
             AND le.DepotId NOT IN (SELECT DepotId FROM SteamDepotMappings)
             {}
             GROUP BY le.DepotId, le.Url
             LIMIT {}",
            unknown_exclusion_clause,
            limit * 10 // Allow more URLs for unknown games combined
        )
    } else {
        format!(
            "SELECT DISTINCT le.Service, le.DepotId, le.Url
             FROM LogEntries le
             WHERE le.DepotId IS NOT NULL
             AND le.Url IS NOT NULL
             AND le.DepotId NOT IN (SELECT DepotId FROM SteamDepotMappings)
             {}
             ORDER BY le.DepotId",
            unknown_exclusion_clause
        )
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

fn query_service_downloads(db_path: &Path) -> Result<HashMap<String, Vec<(String, String)>>> {
    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    eprintln!("Querying LogEntries for non-game services...");

    // Services to detect (these typically don't have game AppIds)
    // We'll exclude 'steam' since it's covered by game detection
    let query = "
        SELECT DISTINCT le.Service, le.Url
        FROM LogEntries le
        WHERE le.Service IS NOT NULL
        AND le.Url IS NOT NULL
        AND LOWER(le.Service) NOT IN ('steam', 'unknown', 'localhost')
        AND le.Service != ''
        ORDER BY le.Service, le.BytesServed DESC
    ";

    let mut stmt = conn.prepare(query)?;

    let rows = stmt.query_map([], |row| {
        Ok((
            row.get::<_, String>(0)?,
            row.get::<_, String>(1)?,
        ))
    })?;

    let mut services: HashMap<String, Vec<(String, String)>> = HashMap::new();

    for row_result in rows {
        if let Ok((service, url)) = row_result {
            let service_lower = service.to_lowercase();
            services
                .entry(service_lower.clone())
                .or_insert_with(Vec::new)
                .push((service_lower, url));
        }
    }

    let service_count = services.len();
    let total_urls: usize = services.values().map(|v| v.len()).sum();
    eprintln!("Found {} unique services with {} URLs", service_count, total_urls);

    Ok(services)
}

/// Detect cache files for a service using direct file existence checks (for incremental mode)
/// This is faster when checking a small number of URLs
fn detect_cache_files_for_service_incremental(
    service_name: &str,
    service_urls: &[(String, String)],
    cache_dir: &Path,
) -> Result<ServiceCacheInfo> {
    let found_files: HashSet<PathBuf> = service_urls
        .par_iter()
        .filter_map(|(service, url)| {
            let cache_key = format!("{}{}", service, url);
            let hash = cache_utils::calculate_md5(&cache_key);
            
            // Calculate the hierarchical path (first 2 chars as subdirectory)
            let subdir = &hash[0..2];
            let file_path = cache_dir.join(subdir).join(&hash);
            
            if file_path.exists() {
                Some(file_path)
            } else {
                // Check chunked format
                (0..100).find_map(|chunk| {
                    let start = chunk * 1_048_576;
                    let end = start + 1_048_575;
                    let chunked_key = format!("{}{}bytes={}-{}", service, url, start, end);
                    let chunked_hash = cache_utils::calculate_md5(&chunked_key);
                    let chunked_subdir = &chunked_hash[0..2];
                    let chunked_path = cache_dir.join(chunked_subdir).join(&chunked_hash);
                    if chunked_path.exists() {
                        Some(chunked_path)
                    } else {
                        None
                    }
                })
            }
        })
        .collect();

    let total_size: u64 = found_files
        .iter()
        .filter_map(|path| fs::metadata(path).ok().map(|m| m.len()))
        .sum();

    let unique_urls: HashSet<String> = service_urls.iter().map(|(_, url)| url.clone()).collect();
    let sample_urls: Vec<String> = unique_urls.iter().take(5).cloned().collect();
    let cache_file_paths: Vec<String> = found_files.iter().map(|p| p.display().to_string()).collect();

    Ok(ServiceCacheInfo {
        service_name: service_name.to_string(),
        cache_files_found: found_files.len(),
        total_size_bytes: total_size,
        sample_urls,
        cache_file_paths,
    })
}

fn detect_cache_files_for_service(
    service_name: &str,
    service_urls: &[(String, String)],
    cache_files_index: &HashMap<String, CacheFileInfo>,
) -> Result<ServiceCacheInfo> {
    // Match URLs against in-memory cache index
    let found_files: HashSet<PathBuf> = service_urls
        .par_iter()
        .filter_map(|(service, url)| {
            // Calculate hash for this service+url combination
            let cache_key = format!("{}{}", service, url);
            let hash = cache_utils::calculate_md5(&cache_key);

            // Instant HashMap lookup
            if let Some(file_info) = cache_files_index.get(&hash) {
                Some(file_info.path.clone())
            } else {
                // Also check chunked format for backwards compatibility
                (0..100)
                    .find_map(|chunk| {
                        let start = chunk * 1_048_576;
                        let end = start + 1_048_575;
                        let chunked_key = format!("{}{}bytes={}-{}", service, url, start, end);
                        let chunked_hash = cache_utils::calculate_md5(&chunked_key);
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

    let unique_urls: HashSet<String> = service_urls.iter().map(|(_, url)| url.clone()).collect();
    let sample_urls: Vec<String> = unique_urls.iter().take(5).cloned().collect();

    // Convert cache file paths to strings for output
    let cache_file_paths: Vec<String> = found_files
        .iter()
        .map(|p| p.display().to_string())
        .collect();

    Ok(ServiceCacheInfo {
        service_name: service_name.to_string(),
        cache_files_found: found_files.len(),
        total_size_bytes: total_size,
        sample_urls,
        cache_file_paths,
    })
}

/// Detect cache files for a game using direct file existence checks (for incremental mode)
/// This is faster when checking a small number of URLs
fn detect_cache_files_for_game_incremental(
    records: &[DownloadRecord],
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    if records.is_empty() {
        anyhow::bail!("No records provided");
    }

    let game_app_id = records[0].game_app_id;
    let game_name = records[0].game_name.clone();

    let mut unique_urls: HashSet<String> = HashSet::new();
    let mut depot_ids: HashSet<u32> = HashSet::new();
    let mut service_urls: Vec<(String, String)> = Vec::new();

    for record in records {
        unique_urls.insert(record.url.clone());
        let service_lower = record.service.to_lowercase();
        service_urls.push((service_lower, record.url.clone()));
        if let Some(depot_id) = record.depot_id {
            depot_ids.insert(depot_id);
        }
    }

    // Use direct file existence checks instead of hash map lookups
    let found_files: HashSet<PathBuf> = service_urls
        .par_iter()
        .filter_map(|(service, url)| {
            let cache_key = format!("{}{}", service, url);
            let hash = cache_utils::calculate_md5(&cache_key);
            
            // Calculate the hierarchical path (first 2 chars as subdirectory)
            let subdir = &hash[0..2];
            let file_path = cache_dir.join(subdir).join(&hash);
            
            if file_path.exists() {
                Some(file_path)
            } else {
                // Check chunked format
                (0..100).find_map(|chunk| {
                    let start = chunk * 1_048_576;
                    let end = start + 1_048_575;
                    let chunked_key = format!("{}{}bytes={}-{}", service, url, start, end);
                    let chunked_hash = cache_utils::calculate_md5(&chunked_key);
                    let chunked_subdir = &chunked_hash[0..2];
                    let chunked_path = cache_dir.join(chunked_subdir).join(&chunked_hash);
                    if chunked_path.exists() {
                        Some(chunked_path)
                    } else {
                        None
                    }
                })
            }
        })
        .collect();

    let total_size: u64 = found_files
        .iter()
        .filter_map(|path| fs::metadata(path).ok().map(|m| m.len()))
        .sum();

    let sample_urls: Vec<String> = unique_urls.iter().take(5).cloned().collect();
    let cache_file_paths: Vec<String> = found_files.iter().map(|p| p.display().to_string()).collect();

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
            let hash = cache_utils::calculate_md5(&cache_key);

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
                        let chunked_hash = cache_utils::calculate_md5(&chunked_key);
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
        eprintln!("Usage: {} <database_path> <cache_dir> <output_json> [excluded_game_ids_json] [--incremental]", args[0]);
        eprintln!();
        eprintln!("Detects which games have files in the cache directory.");
        eprintln!();
        eprintln!("Arguments:");
        eprintln!("  database_path          - Path to LancacheManager.db");
        eprintln!("  cache_dir              - Cache directory root (e.g., /cache or H:/cache)");
        eprintln!("  output_json            - Path to output JSON report");
        eprintln!("  excluded_game_ids_json - (Optional) JSON file with array of game IDs to exclude for incremental scanning");
        eprintln!("  --incremental          - (Optional) Skip cache directory scan for faster incremental updates");
        std::process::exit(1);
    }

    let db_path = PathBuf::from(&args[1]);
    let cache_dir = PathBuf::from(&args[2]);
    let output_json = PathBuf::from(&args[3]);

    // Check for --incremental flag anywhere in args
    let incremental_mode = args.iter().any(|arg| arg == "--incremental");

    // Read excluded game IDs if provided (for incremental scanning)
    let excluded_game_ids: Vec<u32> = if args.len() >= 5 && !args[4].starts_with("--") {
        let excluded_path = PathBuf::from(&args[4]);
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
    eprintln!("  Database: {}", db_path.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Mode: {}", if incremental_mode { "Incremental (Quick Scan)" } else { "Full Scan" });

    if !db_path.exists() {
        anyhow::bail!("Database not found: {}", db_path.display());
    }

    if !cache_dir.exists() {
        anyhow::bail!("Cache directory not found: {}", cache_dir.display());
    }

    // Variables that may or may not be used depending on mode
    let cache_files_index: Option<HashMap<String, CacheFileInfo>>;

    if incremental_mode {
        // INCREMENTAL MODE: Skip expensive cache directory scan
        // Only check file existence for new games (fast when there are few new games)
        eprintln!("\n=== Incremental Mode: Skipping Cache Directory Scan ===");
        eprintln!("Will check file existence directly for new games only...");
        cache_files_index = None;
    } else {
        // FULL SCAN: Build in-memory index of all cache files
        // This is much faster than checking 2.3M individual file.exists() calls
        let index = scan_cache_directory(&cache_dir)?;
        cache_files_index = Some(index);
    }

    // PHASE 2: Query database for game URLs
    eprintln!("\n=== Phase 2: Querying Database ===");

    // Query ALL URLs to get accurate cache sizes (no sampling)
    // This ensures we find and measure every cache file for complete accuracy
    let all_records = query_game_downloads(&db_path, None, &excluded_game_ids)?;

    // Continue even if no games found - we still want to detect services

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
    if incremental_mode {
        eprintln!("Using direct file existence checks (optimized for few new games)...\n");
    } else {
        eprintln!("Using in-memory index for instant lookups...\n");
    }

    let mut detected_games = Vec::new();
    let mut processed_count = 0;
    let mut total_files_found = 0;
    let mut total_bytes_found: u64 = 0;

    for (game_id, records) in games_map {
        processed_count += 1;
        let url_count = records.len();
        eprint!("  [{}/{}] Matching game {} ({}) - {} URLs... ",
                processed_count, total_games, game_id, records[0].game_name, url_count);

        let result = if incremental_mode {
            detect_cache_files_for_game_incremental(&records, &cache_dir)
        } else {
            detect_cache_files_for_game(&records, cache_files_index.as_ref().unwrap())
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

    eprintln!("\n=== Game Scan Complete ===");
    eprintln!("Total game cache files found: {}", total_files_found);
    eprintln!("Total game cache size: {:.2} GB", total_bytes_found as f64 / 1_073_741_824.0);

    // PHASE 4: Detect non-game services
    // Skip service detection in incremental mode since services don't change often
    // and this adds significant overhead
    let mut detected_services = Vec::new();
    let mut service_files_found = 0;
    let mut service_bytes_found: u64 = 0;

    if !incremental_mode {
        eprintln!("\n=== Phase 4: Detecting Non-Game Services ===");

        let services_map = query_service_downloads(&db_path)?;

        for (service_name, service_urls) in services_map {
            eprint!("  Matching service '{}' - {} URLs... ", service_name, service_urls.len());

            match detect_cache_files_for_service(&service_name, &service_urls, cache_files_index.as_ref().unwrap()) {
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
    } else {
        eprintln!("\n=== Skipping Service Detection (Incremental Mode) ===");
    }

    let report = DetectionReport {
        total_games_detected: detected_games.len(),
        total_services_detected: detected_services.len(),
        games: detected_games,
        services: detected_services,
    };

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    eprintln!("\n=== Detection Summary ===");
    eprintln!("Games with cache files: {}", report.total_games_detected);
    eprintln!("Services with cache files: {}", report.total_services_detected);
    eprintln!("Total cache files: {}", total_files_found + service_files_found);
    eprintln!("Total cache size: {:.2} GB", (total_bytes_found + service_bytes_found) as f64 / 1_073_741_824.0);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
}
