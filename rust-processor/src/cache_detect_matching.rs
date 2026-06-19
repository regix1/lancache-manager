use anyhow::{bail, Result};
use rayon::prelude::*;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::cache_detect_queries::{DownloadRecord, EpicDownloadRecord, NamedDownloadRecord};
use crate::cache_utils;
use crate::{CacheFileInfo, GameCacheInfo, ServiceCacheInfo};

/// (service, url, bytes_served) — `bytes_served` is the URL's `MAX(LogEntries.BytesServed)`,
/// used to size the probe chunk count via `cache_utils::probe_chunks_for_bytes` (0 = unknown).
type ServiceUrl = (String, String, i64);

struct SteamGameInputs {
    game_app_id: u32,
    game_name: String,
    unique_urls: HashSet<String>,
    depot_ids: HashSet<u32>,
    service_urls: Vec<ServiceUrl>,
}

pub(crate) fn group_game_records(records: Vec<DownloadRecord>) -> HashMap<u32, Vec<DownloadRecord>> {
    let mut games_map: HashMap<u32, Vec<DownloadRecord>> = HashMap::new();

    for record in records {
        games_map
            .entry(record.game_app_id)
            .or_insert_with(Vec::new)
            .push(record);
    }

    games_map
}

pub(crate) fn group_epic_records(
    records: &[EpicDownloadRecord],
) -> HashMap<String, (String, Vec<ServiceUrl>)> {
    let mut epic_map: HashMap<String, (String, Vec<ServiceUrl>)> = HashMap::new();

    for record in records {
        let entry = epic_map
            .entry(record.epic_app_id.clone())
            .or_insert_with(|| (record.game_name.clone(), Vec::new()));
        entry.1.push((
            record.service.to_lowercase(),
            record.url.clone(),
            record.bytes_served,
        ));
    }

    epic_map
}

/// Separator used to build the composite `(service, game_name)` key for named games.
/// `\u{1}` (Start-of-Heading) cannot appear in a service or game name, so it is a safe
/// delimiter that keeps the two parts unambiguously recoverable.
const NAMED_KEY_SEP: char = '\u{1}';

/// Build the composite key for a name-keyed game. Service is lowercased so
/// "Blizzard" and "blizzard" collapse to one game.
pub(crate) fn named_game_key(service: &str, game_name: &str) -> String {
    format!("{}{}{}", service.to_lowercase(), NAMED_KEY_SEP, game_name)
}

/// Group name-keyed (Blizzard/Riot) download records by `(service, game_name)`.
/// Mirrors `group_epic_records` but uses a composite string key instead of EpicAppId.
/// Returns: key -> (service_lowercase, game_name, service_urls).
pub(crate) fn group_named_records(
    records: &[NamedDownloadRecord],
) -> HashMap<String, (String, String, Vec<ServiceUrl>)> {
    let mut named_map: HashMap<String, (String, String, Vec<ServiceUrl>)> = HashMap::new();

    for record in records {
        let service_lc = record.service.to_lowercase();
        let key = named_game_key(&record.service, &record.game_name);
        let entry = named_map.entry(key).or_insert_with(|| {
            (service_lc.clone(), record.game_name.clone(), Vec::new())
        });
        entry.2.push((service_lc.clone(), record.url.clone(), record.bytes_served));
    }

    named_map
}

fn collect_steam_game_inputs(records: &[DownloadRecord]) -> Result<SteamGameInputs> {
    if records.is_empty() {
        bail!("No records provided");
    }

    let game_app_id = records[0].game_app_id;
    let game_name = records[0].game_name.clone();
    let mut unique_urls: HashSet<String> = HashSet::new();
    let mut depot_ids: HashSet<u32> = HashSet::new();
    let mut service_urls: Vec<ServiceUrl> = Vec::with_capacity(records.len());

    for record in records {
        unique_urls.insert(record.url.clone());
        service_urls.push((
            cache_utils::service_name_lowercase(&record.service),
            record.url.clone(),
            record.bytes_served,
        ));

        if let Some(depot_id) = record.depot_id {
            depot_ids.insert(depot_id);
        }
    }

    Ok(SteamGameInputs {
        game_app_id,
        game_name,
        unique_urls,
        depot_ids,
        service_urls,
    })
}

fn match_files_with_index(
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<String, CacheFileInfo>,
) -> HashSet<PathBuf> {
    let counter = AtomicUsize::new(0);
    match_files_with_index_tracked(service_urls, cache_files_index, &counter)
}

fn match_files_with_index_tracked(
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<String, CacheFileInfo>,
    counter: &AtomicUsize,
) -> HashSet<PathBuf> {
    service_urls
        .par_iter()
        .filter_map(|(service, url, bytes_served)| {
            // Lazy iterator early-exits on the first matching hash; the chunk count is sized
            // from the URL's byte size (clamped to [DEFAULT_MAX_CHUNKS, MAX_PROBE_CHUNKS]).
            let result = cache_utils::cache_hash_candidates_iter(
                service,
                url,
                cache_utils::probe_chunks_for_bytes(*bytes_served),
            )
            .find_map(|hash| cache_files_index.get(&hash).map(|info| info.path.clone()));
            counter.fetch_add(1, Ordering::Relaxed);
            result
        })
        .collect()
}

fn match_files_in_cache(service_urls: &[ServiceUrl], cache_dir: &Path) -> HashSet<PathBuf> {
    let counter = AtomicUsize::new(0);
    match_files_in_cache_tracked(service_urls, cache_dir, &counter)
}

fn match_files_in_cache_tracked(
    service_urls: &[ServiceUrl],
    cache_dir: &Path,
    counter: &AtomicUsize,
) -> HashSet<PathBuf> {
    service_urls
        .par_iter()
        .filter_map(|(service, url, bytes_served)| {
            // Chunk count sized from the URL's byte size (clamped); `.find` early-exits on the
            // first existing path so only genuine all-misses pay the full candidate cost.
            let result = cache_utils::cache_path_candidates_for_probe(
                cache_dir,
                service,
                url,
                cache_utils::probe_chunks_for_bytes(*bytes_served),
            )
            .into_iter()
            .find(|path| path.exists());
            counter.fetch_add(1, Ordering::Relaxed);
            result
        })
        .collect()
}

fn total_size_from_index(
    found_files: &HashSet<PathBuf>,
    cache_files_index: &HashMap<String, CacheFileInfo>,
) -> u64 {
    found_files
        .iter()
        .filter_map(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .and_then(|hash| cache_files_index.get(hash))
                .map(|info| info.size)
        })
        .sum()
}

fn total_size_from_filesystem(found_files: &HashSet<PathBuf>) -> u64 {
    found_files
        .iter()
        .filter_map(|path| fs::metadata(path).ok().map(|metadata| metadata.len()))
        .sum()
}

fn cache_file_paths(found_files: &HashSet<PathBuf>) -> Vec<String> {
    found_files
        .iter()
        .map(|path| path.display().to_string())
        .collect()
}

pub(crate) fn detect_service_cache_info(
    service_name: &str,
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<String, CacheFileInfo>,
    counter: &AtomicUsize,
) -> Result<ServiceCacheInfo> {
    let found_files = match_files_with_index_tracked(service_urls, cache_files_index, counter);
    let total_size = total_size_from_index(&found_files, cache_files_index);
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url, _)| url.as_str()), 5);

    Ok(ServiceCacheInfo {
        service_name: service_name.to_string(),
        cache_files_found: found_files.len(),
        total_size_bytes: total_size,
        sample_urls,
        cache_file_paths: cache_file_paths(&found_files),
    })
}

pub(crate) fn detect_service_cache_info_incremental(
    service_name: &str,
    service_urls: &[ServiceUrl],
    cache_dir: &Path,
    counter: &AtomicUsize,
) -> Result<ServiceCacheInfo> {
    let found_files = match_files_in_cache_tracked(service_urls, cache_dir, counter);
    let total_size = total_size_from_filesystem(&found_files);
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url, _)| url.as_str()), 5);

    Ok(ServiceCacheInfo {
        service_name: service_name.to_string(),
        cache_files_found: found_files.len(),
        total_size_bytes: total_size,
        sample_urls,
        cache_file_paths: cache_file_paths(&found_files),
    })
}

fn build_steam_game_cache_info(
    inputs: SteamGameInputs,
    found_files: HashSet<PathBuf>,
    total_size_bytes: u64,
) -> GameCacheInfo {
    let sample_urls = cache_utils::sorted_sample_urls(inputs.unique_urls.iter(), 5);

    GameCacheInfo {
        game_app_id: inputs.game_app_id,
        game_name: inputs.game_name,
        cache_files_found: found_files.len(),
        total_size_bytes,
        depot_ids: inputs.depot_ids.into_iter().collect(),
        sample_urls,
        cache_file_paths: cache_file_paths(&found_files),
        service: None,
        epic_app_id: None,
    }
}

pub(crate) fn detect_steam_game_cache_info(
    records: &[DownloadRecord],
    cache_files_index: &HashMap<String, CacheFileInfo>,
) -> Result<GameCacheInfo> {
    let inputs = collect_steam_game_inputs(records)?;
    let found_files = match_files_with_index(&inputs.service_urls, cache_files_index);
    let total_size = total_size_from_index(&found_files, cache_files_index);

    Ok(build_steam_game_cache_info(inputs, found_files, total_size))
}

pub(crate) fn detect_steam_game_cache_info_incremental(
    records: &[DownloadRecord],
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    let inputs = collect_steam_game_inputs(records)?;
    let found_files = match_files_in_cache(&inputs.service_urls, cache_dir);
    let total_size = total_size_from_filesystem(&found_files);

    Ok(build_steam_game_cache_info(inputs, found_files, total_size))
}

fn generate_epic_game_app_id(epic_app_id: &str) -> u32 {
    let mut hasher = Sha256::new();
    hasher.update(epic_app_id.as_bytes());
    let hash = hasher.finalize();
    let raw_hash = u32::from_le_bytes([hash[0], hash[1], hash[2], hash[3]]);
    raw_hash | 0x80000000
}

fn build_epic_game_cache_info(
    epic_app_id: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    found_files: HashSet<PathBuf>,
    total_size_bytes: u64,
) -> GameCacheInfo {
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url, _)| url.as_str()), 5);

    GameCacheInfo {
        game_app_id: generate_epic_game_app_id(epic_app_id),
        game_name: game_name.to_string(),
        cache_files_found: found_files.len(),
        total_size_bytes,
        depot_ids: Vec::new(),
        sample_urls,
        cache_file_paths: cache_file_paths(&found_files),
        service: Some("epicgames".to_string()),
        epic_app_id: Some(epic_app_id.to_string()),
    }
}

pub(crate) fn detect_epic_game_cache_info(
    epic_app_id: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<String, CacheFileInfo>,
) -> Result<GameCacheInfo> {
    let found_files = match_files_with_index(service_urls, cache_files_index);
    let total_size = total_size_from_index(&found_files, cache_files_index);

    Ok(build_epic_game_cache_info(
        epic_app_id,
        game_name,
        service_urls,
        found_files,
        total_size,
    ))
}

pub(crate) fn detect_epic_game_cache_info_incremental(
    epic_app_id: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    let found_files = match_files_in_cache(service_urls, cache_dir);
    let total_size = total_size_from_filesystem(&found_files);

    Ok(build_epic_game_cache_info(
        epic_app_id,
        game_name,
        service_urls,
        found_files,
        total_size,
    ))
}

/// Build a `GameCacheInfo` for a name-keyed game (Blizzard/Riot). Unlike Epic, the
/// GameAppId stays 0 (no synthetic id — banners are name-keyed) and `epic_app_id` is None.
/// `service` carries the owning service ("blizzard"/"riot") so removal can scope by it.
fn build_named_game_cache_info(
    service: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    found_files: HashSet<PathBuf>,
    total_size_bytes: u64,
) -> GameCacheInfo {
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url, _)| url.as_str()), 5);

    GameCacheInfo {
        game_app_id: 0,
        game_name: game_name.to_string(),
        cache_files_found: found_files.len(),
        total_size_bytes,
        depot_ids: Vec::new(),
        sample_urls,
        cache_file_paths: cache_file_paths(&found_files),
        service: Some(service.to_lowercase()),
        epic_app_id: None,
    }
}

pub(crate) fn detect_named_game_cache_info(
    service: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<String, CacheFileInfo>,
) -> Result<GameCacheInfo> {
    let found_files = match_files_with_index(service_urls, cache_files_index);
    let total_size = total_size_from_index(&found_files, cache_files_index);

    Ok(build_named_game_cache_info(
        service,
        game_name,
        service_urls,
        found_files,
        total_size,
    ))
}

pub(crate) fn detect_named_game_cache_info_incremental(
    service: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    let found_files = match_files_in_cache(service_urls, cache_dir);
    let total_size = total_size_from_filesystem(&found_files);

    Ok(build_named_game_cache_info(
        service,
        game_name,
        service_urls,
        found_files,
        total_size,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cache_detect_queries::NamedDownloadRecord;

    fn rec(service: &str, game_name: &str, url: &str, bytes: i64) -> NamedDownloadRecord {
        NamedDownloadRecord {
            service: service.to_string(),
            game_name: game_name.to_string(),
            url: url.to_string(),
            bytes_served: bytes,
        }
    }

    #[test]
    fn named_key_is_service_lowercased_plus_name() {
        // Service is lowercased; game name preserved verbatim.
        assert_eq!(named_game_key("Blizzard", "Diablo IV"), "blizzard\u{1}Diablo IV");
        assert_eq!(named_game_key("blizzard", "Diablo IV"), "blizzard\u{1}Diablo IV");
    }

    #[test]
    fn group_named_records_keeps_distinct_games_separate() {
        // Same game name under two services must NOT collapse (Blizzard "X" vs Riot "X").
        let records = vec![
            rec("Blizzard", "Diablo", "http://b/1", 100),
            rec("Blizzard", "Diablo", "http://b/2", 200),
            rec("riot", "Diablo", "http://r/1", 50),
            rec("blizzard", "Overwatch", "http://b/3", 300),
        ];
        let grouped = group_named_records(&records);
        // 3 distinct (service, game) keys.
        assert_eq!(grouped.len(), 3);

        let blizz_diablo = grouped.get("blizzard\u{1}Diablo").expect("blizzard diablo present");
        assert_eq!(blizz_diablo.0, "blizzard");
        assert_eq!(blizz_diablo.1, "Diablo");
        assert_eq!(blizz_diablo.2.len(), 2);

        let riot_diablo = grouped.get("riot\u{1}Diablo").expect("riot diablo present");
        assert_eq!(riot_diablo.0, "riot");
        assert_eq!(riot_diablo.2.len(), 1);
    }

    #[test]
    fn build_named_game_cache_info_keeps_app_id_zero_and_no_epic_id() {
        let info = build_named_game_cache_info(
            "Blizzard",
            "Diablo",
            &[("blizzard".to_string(), "http://b/1".to_string(), 0)],
            HashSet::new(),
            0,
        );
        assert_eq!(info.game_app_id, 0);
        assert_eq!(info.epic_app_id, None);
        assert_eq!(info.service.as_deref(), Some("blizzard"));
        assert!(info.depot_ids.is_empty());
        assert_eq!(info.game_name, "Diablo");
    }
}
