use anyhow::{bail, Result};
use rayon::prelude::*;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};

use crate::cache_detect_queries::{DownloadRecord, EpicDownloadRecord};
use crate::cache_utils;
use crate::{CacheFileInfo, GameCacheInfo, ServiceCacheInfo};

type ServiceUrl = (String, String);

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
        entry.1.push((record.service.to_lowercase(), record.url.clone()));
    }

    epic_map
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
            cache_utils::normalize_service_name(&record.service),
            record.url.clone(),
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
    service_urls
        .par_iter()
        .filter_map(|(service, url)| {
            cache_utils::cache_hash_candidates_for_probe(
                service,
                url,
                cache_utils::DEFAULT_MAX_CHUNKS,
            )
            .into_iter()
            .find_map(|hash| cache_files_index.get(&hash).map(|info| info.path.clone()))
        })
        .collect()
}

fn match_files_in_cache(service_urls: &[ServiceUrl], cache_dir: &Path) -> HashSet<PathBuf> {
    service_urls
        .par_iter()
        .filter_map(|(service, url)| {
            cache_utils::cache_path_candidates_for_probe(
                cache_dir,
                service,
                url,
                cache_utils::DEFAULT_MAX_CHUNKS,
            )
            .into_iter()
            .find(|path| path.exists())
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
) -> Result<ServiceCacheInfo> {
    let found_files = match_files_with_index(service_urls, cache_files_index);
    let total_size = total_size_from_index(&found_files, cache_files_index);
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url)| url.as_str()), 5);

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
) -> Result<ServiceCacheInfo> {
    let found_files = match_files_in_cache(service_urls, cache_dir);
    let total_size = total_size_from_filesystem(&found_files);
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url)| url.as_str()), 5);

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
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url)| url.as_str()), 5);

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
