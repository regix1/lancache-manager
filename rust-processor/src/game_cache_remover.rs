use anyhow::{Context, Result};
use rusqlite::Connection;
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::env;
use std::fs;
use std::io::{BufWriter, Write as IoWrite};
use std::path::{Path, PathBuf};
use tempfile::NamedTempFile;

mod log_discovery;
mod log_reader;
mod models;
mod parser;
mod progress_utils;
mod service_utils;

use log_reader::LogFileReader;
use parser::LogParser;

#[derive(Debug, Serialize)]
struct RemovalReport {
    game_app_id: u32,
    game_name: String,
    cache_files_deleted: usize,
    total_bytes_freed: u64,
    empty_dirs_removed: usize,
    log_entries_removed: u64,
    depot_ids: Vec<u32>,
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

fn calculate_cache_path_no_range(cache_dir: &Path, service: &str, url: &str) -> PathBuf {
    // Lancache nginx cache key format: $cacheidentifier$uri (NO slice_range!)
    let cache_key = format!("{}{}", service, url);
    let hash = calculate_md5(&cache_key);

    let len = hash.len();
    if len < 4 {
        return cache_dir.join(&hash);
    }

    let last_2 = &hash[len - 2..];
    let middle_2 = &hash[len - 4..len - 2];

    cache_dir.join(last_2).join(middle_2).join(&hash)
}

fn get_game_name_from_db(db_path: &Path, game_app_id: u32) -> Result<String> {
    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    let mut stmt = conn.prepare(
        "SELECT DISTINCT GameName FROM Downloads WHERE GameAppId = ? LIMIT 1"
    )?;

    let game_name = stmt.query_row([game_app_id], |row| row.get::<_, String>(0))
        .unwrap_or_else(|_| format!("Game {}", game_app_id));

    Ok(game_name)
}

fn get_game_urls_from_db(db_path: &Path, game_app_id: u32) -> Result<HashMap<String, (String, i64, HashSet<u32>)>> {
    eprintln!("Querying database for game URLs and depot IDs...");

    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    // CRITICAL FIX: Query LogEntries instead of Downloads to get ALL URLs
    // This matches the detector logic and ensures we delete ALL cache files
    let mut stmt = conn.prepare(
        "SELECT DISTINCT le.Service, le.Url, le.DepotId, le.BytesServed
         FROM LogEntries le
         INNER JOIN SteamDepotMappings sdm ON le.DepotId = sdm.DepotId
         WHERE sdm.AppId = ? AND le.Url IS NOT NULL"
    )?;

    // Build service_urls map just like the detector does
    let mut service_urls: HashMap<String, HashMap<String, (i64, HashSet<u32>)>> = HashMap::new();

    let rows = stmt.query_map([game_app_id], |row| {
        Ok((
            row.get::<_, String>(0)?,        // Service
            row.get::<_, String>(1)?,        // Url
            row.get::<_, Option<u32>>(2)?,   // DepotId (can be NULL)
            row.get::<_, i64>(3)?,           // BytesServed
        ))
    })?;

    for row in rows {
        let (service, url, depot_id_opt, bytes_served) = row?;

        // Lowercase service name to match cache file format (same as detector)
        let service_lower = service.to_lowercase();

        let url_map = service_urls
            .entry(service_lower.clone())
            .or_insert_with(HashMap::new);

        let entry = url_map
            .entry(url.clone())
            .or_insert_with(|| (0, HashSet::new()));

        // Track max bytes
        entry.0 = entry.0.max(bytes_served);

        // Track depot ID if present
        if let Some(depot_id) = depot_id_opt {
            entry.1.insert(depot_id);
        }
    }

    // Also get URLs for unknown games (depots not in mappings)
    let mut unknown_stmt = conn.prepare(
        "SELECT DISTINCT le.Service, le.Url, le.DepotId, le.BytesServed
         FROM LogEntries le
         WHERE le.DepotId IS NOT NULL
         AND le.Url IS NOT NULL
         AND le.DepotId = ?
         AND le.DepotId NOT IN (SELECT DepotId FROM SteamDepotMappings)"
    )?;

    let unknown_rows = unknown_stmt.query_map([game_app_id], |row| {
        Ok((
            row.get::<_, String>(0)?,
            row.get::<_, String>(1)?,
            row.get::<_, Option<u32>>(2)?,
            row.get::<_, i64>(3)?,
        ))
    })?;

    for row in unknown_rows {
        let (service, url, depot_id_opt, bytes_served) = row?;
        let service_lower = service.to_lowercase();

        let url_map = service_urls
            .entry(service_lower.clone())
            .or_insert_with(HashMap::new);

        let entry = url_map
            .entry(url.clone())
            .or_insert_with(|| (0, HashSet::new()));

        entry.0 = entry.0.max(bytes_served);

        if let Some(depot_id) = depot_id_opt {
            entry.1.insert(depot_id);
        }
    }

    // Flatten to URL -> (service, bytes, depot_ids) format
    let mut url_data: HashMap<String, (String, i64, HashSet<u32>)> = HashMap::new();

    for (service, urls) in service_urls {
        for (url, (bytes, depot_ids)) in urls {
            url_data.insert(url, (service.clone(), bytes, depot_ids));
        }
    }

    eprintln!("  Found {} unique URLs for game AppID {}", url_data.len(), game_app_id);
    Ok(url_data)
}

fn get_game_depot_ids(db_path: &Path, game_app_id: u32) -> Result<HashSet<u32>> {
    let conn = Connection::open(db_path)?;

    // Get depot IDs from SteamDepotMappings for mapped games
    let mut mapped_stmt = conn.prepare(
        "SELECT DISTINCT DepotId FROM SteamDepotMappings WHERE AppId = ?"
    )?;

    let mut depot_ids: HashSet<u32> = mapped_stmt.query_map([game_app_id], |row| row.get::<_, u32>(0))?
        .filter_map(|r| r.ok())
        .collect();

    // Also check Downloads table for any additional depot IDs
    let mut downloads_stmt = conn.prepare(
        "SELECT DISTINCT DepotId FROM Downloads WHERE GameAppId = ? AND DepotId IS NOT NULL"
    )?;

    let download_depot_ids: HashSet<u32> = downloads_stmt.query_map([game_app_id], |row| row.get::<_, u32>(0))?
        .filter_map(|r| r.ok())
        .collect();

    depot_ids.extend(download_depot_ids);

    Ok(depot_ids)
}

fn delete_game_from_database(db_path: &Path, game_app_id: u32) -> Result<usize> {
    eprintln!("Deleting database records for game AppID {}...", game_app_id);

    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    // First, delete LogEntries that reference these downloads (foreign key constraint)
    let mut log_entries_stmt = conn.prepare(
        "DELETE FROM LogEntries WHERE DownloadId IN (SELECT Id FROM Downloads WHERE GameAppId = ?)"
    )?;
    let log_entries_deleted = log_entries_stmt.execute([game_app_id])?;
    eprintln!("  Deleted {} log entry records", log_entries_deleted);

    // Now safe to delete the downloads
    let mut downloads_stmt = conn.prepare("DELETE FROM Downloads WHERE GameAppId = ?")?;
    let downloads_deleted = downloads_stmt.execute([game_app_id])?;

    eprintln!("  Deleted {} download records", downloads_deleted);
    Ok(downloads_deleted)
}

fn remove_cache_files_for_game(
    cache_dir: &Path,
    url_data: &HashMap<String, (String, i64, HashSet<u32>)>,
) -> Result<(usize, u64, HashSet<PathBuf>)> {
    let mut deleted_files = 0;
    let mut bytes_freed: u64 = 0;
    let mut parent_dirs: HashSet<PathBuf> = HashSet::new();
    let slice_size: i64 = 1_048_576; // 1MB

    for (url, (service, total_bytes, _depot_ids)) in url_data {
        // Lowercase service name to match cache format
        let service_lower = service.to_lowercase();

        // FIRST: Try the no-range format (standard lancache format)
        let cache_path_no_range = calculate_cache_path_no_range(cache_dir, &service_lower, url);

        if cache_path_no_range.exists() {
            // Found file with no-range format - delete it
            if let Ok(metadata) = fs::metadata(&cache_path_no_range) {
                bytes_freed += metadata.len();
            }

            match fs::remove_file(&cache_path_no_range) {
                Ok(_) => {
                    deleted_files += 1;
                    if let Some(parent) = cache_path_no_range.parent() {
                        parent_dirs.insert(parent.to_path_buf());
                    }
                    eprintln!("  Deleted (no-range): {}", cache_path_no_range.display());
                }
                Err(e) => {
                    eprintln!("  Warning: Failed to delete {}: {}", cache_path_no_range.display(), e);
                }
            }
        } else {
            // FALLBACK: Try the chunked format with bytes range
            if *total_bytes == 0 {
                // If no size info, check at least the first chunk
                let cache_path = calculate_cache_path(cache_dir, &service_lower, url, 0, 1_048_575);

                if cache_path.exists() {
                    if let Ok(metadata) = fs::metadata(&cache_path) {
                        bytes_freed += metadata.len();
                    }

                    match fs::remove_file(&cache_path) {
                        Ok(_) => {
                            deleted_files += 1;
                            if let Some(parent) = cache_path.parent() {
                                parent_dirs.insert(parent.to_path_buf());
                            }
                            eprintln!("  Deleted (chunked, first): {}", cache_path.display());
                        }
                        Err(e) => {
                            eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                        }
                    }
                }
            } else {
                // Calculate ALL chunks based on actual download size
                let mut start: i64 = 0;
                while start < *total_bytes {
                    let end = (start + slice_size - 1).min(*total_bytes - 1 + slice_size - 1);

                    let cache_path = calculate_cache_path(cache_dir, &service_lower, url, start as u64, end as u64);

                    if cache_path.exists() {
                        if let Ok(metadata) = fs::metadata(&cache_path) {
                            bytes_freed += metadata.len();
                        }

                        match fs::remove_file(&cache_path) {
                            Ok(_) => {
                                deleted_files += 1;

                                if let Some(parent) = cache_path.parent() {
                                    parent_dirs.insert(parent.to_path_buf());
                                }

                                if deleted_files % 100 == 0 {
                                    eprintln!("  Deleted {} cache files... ({:.2} MB freed)",
                                        deleted_files, bytes_freed as f64 / 1_048_576.0);
                                }
                            }
                            Err(e) => {
                                eprintln!("  Warning: Failed to delete {}: {}", cache_path.display(), e);
                            }
                        }
                    }

                    start += slice_size;
                }
            }
        }
    }

    Ok((deleted_files, bytes_freed, parent_dirs))
}

fn cleanup_empty_directories(cache_dir: &Path, dirs_to_check: HashSet<PathBuf>) -> usize {
    let mut removed_count = 0;

    let mut sorted_dirs: Vec<PathBuf> = dirs_to_check.into_iter().collect();
    sorted_dirs.sort_by(|a, b| b.components().count().cmp(&a.components().count()));

    for dir in sorted_dirs {
        if !dir.starts_with(cache_dir) {
            continue;
        }

        if let Ok(entries) = fs::read_dir(&dir) {
            if entries.count() == 0 {
                if fs::remove_dir(&dir).is_ok() {
                    removed_count += 1;

                    if let Some(parent) = dir.parent() {
                        if parent.starts_with(cache_dir) && parent != cache_dir {
                            if let Ok(parent_entries) = fs::read_dir(parent) {
                                if parent_entries.count() == 0 {
                                    fs::remove_dir(parent).ok();
                                    removed_count += 1;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    removed_count
}

fn remove_log_entries_for_game(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    valid_depot_ids: &HashSet<u32>,
) -> Result<u64> {
    eprintln!("Filtering log files to remove game entries...");

    let parser = LogParser::new(chrono_tz::UTC);
    let log_files = log_discovery::discover_log_files(log_dir, "access.log")?;

    let mut total_lines_removed: u64 = 0;

    for (file_index, log_file) in log_files.iter().enumerate() {
        eprintln!("  Processing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

        let file_result = (|| -> Result<u64> {
            let file_dir = log_file.path.parent().context("Failed to get file directory")?;
            let temp_file = NamedTempFile::new_in(file_dir)?;

            let mut lines_removed: u64 = 0;
            let mut lines_processed: u64 = 0;

            {
                let mut log_reader = LogFileReader::open(&log_file.path)?;
                let mut writer = BufWriter::with_capacity(1024 * 1024, temp_file.as_file());
                let mut line = String::new();

                loop {
                    line.clear();
                    let bytes_read = log_reader.read_line(&mut line)?;
                    if bytes_read == 0 {
                        break; // EOF
                    }

                    lines_processed += 1;
                    let mut should_remove = false;

                    // Parse the line and check if it belongs to this game
                    if let Some(entry) = parser.parse_line(line.trim()) {
                        // Skip health checks
                        if !service_utils::should_skip_url(&entry.url) {
                            // Check if this URL is for the game being removed
                            // Match by URL OR by depot_id
                            if urls_to_remove.contains(&entry.url) {
                                should_remove = true;
                            } else if let Some(depot_id) = entry.depot_id {
                                if valid_depot_ids.contains(&depot_id) {
                                    should_remove = true;
                                }
                            }
                        }
                    }

                    if !should_remove {
                        write!(writer, "{}", line)?;
                    } else {
                        lines_removed += 1;
                    }
                }

                writer.flush()?;
            }

            // If all lines would be removed, delete the entire file
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!("  INFO: All {} lines from this file are for this game, deleting file entirely", lines_processed);
                std::fs::remove_file(&log_file.path).ok();
                return Ok(lines_removed);
            }

            // Atomically replace original with filtered version
            let temp_path = temp_file.into_temp_path();
            temp_path.persist(&log_file.path)?;

            Ok(lines_removed)
        })();

        match file_result {
            Ok(lines_removed) => {
                eprintln!("    Removed {} log lines from this file", lines_removed);
                total_lines_removed += lines_removed;
            }
            Err(e) => {
                eprintln!("  WARNING: Skipping file {}: {}", log_file.path.display(), e);
            }
        }
    }

    eprintln!("Total log entries removed: {}", total_lines_removed);
    Ok(total_lines_removed)
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 5 {
        eprintln!("Usage: {} <database_path> <log_dir> <cache_dir> <game_app_id> <output_json>", args[0]);
        eprintln!();
        eprintln!("Removes all cache files for a specific game by scanning logs.");
        eprintln!();
        eprintln!("Arguments:");
        eprintln!("  database_path - Path to LancacheManager.db (for game name mapping)");
        eprintln!("  log_dir       - Directory containing log files");
        eprintln!("  cache_dir     - Cache directory root (e.g., /cache or H:/cache)");
        eprintln!("  game_app_id   - Game AppID to remove");
        eprintln!("  output_json   - Path to output JSON report");
        std::process::exit(1);
    }

    let db_path = PathBuf::from(&args[1]);
    let log_dir = PathBuf::from(&args[2]);
    let cache_dir = PathBuf::from(&args[3]);
    let game_app_id: u32 = args[4].parse()
        .context("Invalid game_app_id - must be a number")?;
    let output_json = PathBuf::from(&args[5]);

    eprintln!("Game Cache Removal");
    eprintln!("  Database: {}", db_path.display());
    eprintln!("  Log directory: {}", log_dir.display());
    eprintln!("  Cache directory: {}", cache_dir.display());
    eprintln!("  Game AppID: {}", game_app_id);

    if !db_path.exists() {
        anyhow::bail!("Database not found: {}", db_path.display());
    }

    if !log_dir.exists() {
        anyhow::bail!("Log directory not found: {}", log_dir.display());
    }

    if !cache_dir.exists() {
        anyhow::bail!("Cache directory not found: {}", cache_dir.display());
    }

    // Get game name from database
    let game_name = get_game_name_from_db(&db_path, game_app_id)?;
    eprintln!("Game: {}", game_name);

    // Get valid depot IDs for this game from database
    let valid_depot_ids = get_game_depot_ids(&db_path, game_app_id)?;
    eprintln!("Valid depot IDs for this game: {:?}", valid_depot_ids);

    // Query database directly for URLs - much faster than scanning logs!
    let url_data = get_game_urls_from_db(&db_path, game_app_id)?;

    if url_data.is_empty() {
        eprintln!("No URLs found in logs for game AppID {}", game_app_id);

        let report = RemovalReport {
            game_app_id,
            game_name,
            cache_files_deleted: 0,
            total_bytes_freed: 0,
            empty_dirs_removed: 0,
            log_entries_removed: 0,
            depot_ids: vec![],
        };

        let json = serde_json::to_string_pretty(&report)?;
        fs::write(&output_json, json)?;
        eprintln!("Report saved to: {}", output_json.display());
        return Ok(());
    }

    eprintln!("Found {} unique URLs for '{}'", url_data.len(), game_name);
    eprintln!("\nRemoving cache files...");

    let (deleted_files, bytes_freed, parent_dirs) = remove_cache_files_for_game(&cache_dir, &url_data)?;

    eprintln!("\nCleaning up empty directories...");
    let empty_dirs_removed = cleanup_empty_directories(&cache_dir, parent_dirs);

    // Remove log entries for this game
    eprintln!("\nRemoving log entries...");
    let urls_to_remove: HashSet<String> = url_data.keys().cloned().collect();
    let log_entries_removed = remove_log_entries_for_game(&log_dir, &urls_to_remove, &valid_depot_ids)?;

    // Delete database records for this game
    eprintln!("\nRemoving database records...");
    let _db_records_deleted = delete_game_from_database(&db_path, game_app_id)?;

    // Collect all depot IDs
    let mut all_depot_ids: HashSet<u32> = HashSet::new();
    for (_url, (_service, _bytes, depot_ids)) in &url_data {
        all_depot_ids.extend(depot_ids.iter());
    }

    let report = RemovalReport {
        game_app_id,
        game_name,
        cache_files_deleted: deleted_files,
        total_bytes_freed: bytes_freed,
        empty_dirs_removed,
        log_entries_removed,
        depot_ids: all_depot_ids.into_iter().collect(),
    };

    let json = serde_json::to_string_pretty(&report)?;
    fs::write(&output_json, json)?;

    eprintln!("\n=== Removal Summary ===");
    eprintln!("Cache files deleted: {}", report.cache_files_deleted);
    eprintln!("Space freed: {:.2} MB", report.total_bytes_freed as f64 / 1_048_576.0);
    eprintln!("Empty directories removed: {}", report.empty_dirs_removed);
    eprintln!("Log entries removed: {}", report.log_entries_removed);
    eprintln!("Report saved to: {}", output_json.display());

    Ok(())
}
