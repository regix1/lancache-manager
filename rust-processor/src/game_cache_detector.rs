use anyhow::{Context, Result};
use rusqlite::Connection;
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};

mod progress_utils;

#[derive(Debug, Serialize)]
struct GameCacheInfo {
    game_app_id: u32,
    game_name: String,
    cache_files_found: usize,
    total_size_bytes: u64,
    depot_ids: Vec<u32>,
    sample_urls: Vec<String>,
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
    last_url: String,
    depot_id: Option<u32>,
}

fn calculate_md5(cache_key: &str) -> String {
    format!("{:x}", md5::compute(cache_key.as_bytes()))
}

fn calculate_cache_path(cache_dir: &Path, service: &str, url: &str, start: u64, end: u64) -> PathBuf {
    let cache_key = format!("{}{}bytes={}-{}", service, url, start, end);
    let hash = calculate_md5(&cache_key);

    let len = hash.len();
    if len < 4 {
        return cache_dir.join(&hash);
    }

    let last_2 = &hash[len - 2..];
    let middle_2 = &hash[len - 4..len - 2];

    cache_dir.join(last_2).join(middle_2).join(&hash)
}

fn get_file_size(path: &Path) -> u64 {
    fs::metadata(path).map(|m| m.len()).unwrap_or(0)
}

fn query_game_downloads(db_path: &Path) -> Result<Vec<DownloadRecord>> {
    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    let mut stmt = conn.prepare(
        "SELECT DISTINCT Service, GameAppId, GameName, LastUrl, DepotId
         FROM Downloads
         WHERE GameAppId IS NOT NULL AND LastUrl IS NOT NULL
         ORDER BY GameAppId"
    )?;

    let records = stmt.query_map([], |row| {
        Ok(DownloadRecord {
            service: row.get(0)?,
            game_app_id: row.get(1)?,
            game_name: row.get(2)?,
            last_url: row.get(3)?,
            depot_id: row.get(4)?,
        })
    })?
    .filter_map(|r| r.ok())
    .collect();

    Ok(records)
}

fn detect_cache_files_for_game(
    cache_dir: &Path,
    records: &[DownloadRecord],
) -> Result<GameCacheInfo> {
    if records.is_empty() {
        anyhow::bail!("No records provided");
    }

    let game_app_id = records[0].game_app_id;
    let game_name = records[0].game_name.clone();

    // Collect unique URLs and depot IDs
    let mut unique_urls: HashSet<String> = HashSet::new();
    let mut depot_ids: HashSet<u32> = HashSet::new();
    let mut service_urls: HashMap<String, HashSet<String>> = HashMap::new();

    for record in records {
        unique_urls.insert(record.last_url.clone());
        service_urls
            .entry(record.service.clone())
            .or_insert_with(HashSet::new)
            .insert(record.last_url.clone());

        if let Some(depot_id) = record.depot_id {
            depot_ids.insert(depot_id);
        }
    }

    // Scan cache directory for actual files
    let mut found_files = HashSet::new();
    let mut total_size: u64 = 0;

    for (service, urls) in &service_urls {
        for url in urls {
            // Check first 100 chunks (100MB) for each URL
            for chunk in 0..100 {
                let start = chunk * 1_048_576;
                let end = start + 1_048_575;

                let cache_path = calculate_cache_path(cache_dir, service, url, start, end);

                if cache_path.exists() {
                    let size = get_file_size(&cache_path);
                    total_size += size;
                    found_files.insert(cache_path);
                }
            }
        }
    }

    let sample_urls: Vec<String> = unique_urls.iter().take(5).cloned().collect();

    Ok(GameCacheInfo {
        game_app_id,
        game_name,
        cache_files_found: found_files.len(),
        total_size_bytes: total_size,
        depot_ids: depot_ids.into_iter().collect(),
        sample_urls,
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

    eprintln!("\nQuerying database for games...");
    let all_records = query_game_downloads(&db_path)?;

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

    eprintln!("Found {} unique games in database", games_map.len());
    eprintln!("\nScanning cache directory for game files...");

    let mut detected_games = Vec::new();

    for (game_id, records) in games_map {
        eprint!("  Checking game {} ({})... ", game_id, records[0].game_name);

        match detect_cache_files_for_game(&cache_dir, &records) {
            Ok(info) => {
                if info.cache_files_found > 0 {
                    eprintln!(
                        "FOUND {} files ({:.2} MB)",
                        info.cache_files_found,
                        info.total_size_bytes as f64 / 1_048_576.0
                    );
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
