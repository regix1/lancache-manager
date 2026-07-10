use anyhow::{bail, Result};
use rayon::prelude::*;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::cache_detect_queries::{DownloadRecord, EpicDownloadRecord, NamedDownloadRecord};
use crate::cache_utils;
use crate::{GameCacheInfo, ServiceCacheInfo};

/// (service, url, bytes_served) — `bytes_served` is the URL's `MAX(LogEntries.BytesServed)`.
/// Detection IGNORES this value: for range-served objects (Blizzard /tpr/ TACT archives, Riot
/// bundles) each log row is a single ~1 MiB range, so MAX can be one slice and sizing the probe
/// from it would undercount a multi-slice object. Matching instead walks slice indices and collects
/// every slice that actually EXISTS (see `cache_utils::existing_cache_hashes_for_url`). The field is
/// kept in the tuple because the grouping/query layer populates it uniformly across services.
type ServiceUrl = (String, String, i64);

struct SteamGameInputs {
    game_app_id: u32,
    game_name: String,
    depot_ids: HashSet<u32>,
    service_urls: Vec<ServiceUrl>,
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

/// Group name-keyed (Blizzard/Riot/Xbox) download records by `(service, game_name)`.
/// Mirrors `group_epic_records` but uses a composite string key instead of EpicAppId.
/// Returns: key -> (identity_service_lowercase, game_name, service_urls).
///
/// IDENTITY vs CACHE-HASH split: the map key and the returned identity service use
/// `record.service` (Downloads.Service) — the detection/removal identity — while each
/// `ServiceUrl` tuple carries `record.cache_service` (LogEntries.Service), which is what
/// `existing_cache_*_for_url` hashes to find the on-disk slices. For Blizzard/Riot the two are
/// equal; for Xbox identity=`xbox` but cache-hash=`wsus`, so the cache lookup must use the latter.
pub(crate) fn group_named_records(
    records: &[NamedDownloadRecord],
) -> HashMap<String, (String, String, Vec<ServiceUrl>)> {
    let mut named_map: HashMap<String, (String, String, Vec<ServiceUrl>)> = HashMap::new();

    for record in records {
        let identity_service_lc = record.service.to_lowercase();
        let cache_service_lc = record.cache_service.to_lowercase();
        let key = named_game_key(&record.service, &record.game_name);
        let entry = named_map.entry(key).or_insert_with(|| {
            (identity_service_lc.clone(), record.game_name.clone(), Vec::new())
        });
        entry.2.push((cache_service_lc, record.url.clone(), record.bytes_served));
    }

    named_map
}

fn collect_steam_game_inputs(records: &[DownloadRecord]) -> Result<SteamGameInputs> {
    if records.is_empty() {
        bail!("No records provided");
    }

    let game_app_id = records[0].game_app_id;
    let game_name = records[0].game_name.clone();
    let mut depot_ids: HashSet<u32> = HashSet::new();
    let mut service_urls: Vec<ServiceUrl> = Vec::with_capacity(records.len());

    for record in records {
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
        depot_ids,
        service_urls,
    })
}

fn match_files_with_index(
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<u128, u64>,
) -> HashSet<u128> {
    let counter = AtomicUsize::new(0);
    match_files_with_index_tracked(service_urls, cache_files_index, &counter)
}

fn match_files_with_index_tracked(
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<u128, u64>,
    counter: &AtomicUsize,
) -> HashSet<u128> {
    service_urls
        .par_iter()
        .flat_map_iter(|(service, url, _bytes_served)| {
            // Collect EVERY existing slice for this URL, not just the first. Range-served objects
            // (Blizzard /tpr/ TACT archives, Riot bundles) span many 1 MiB slices; the old
            // single-slice `.find_map` early-exit undercounted them ~250x. We ignore `bytes_served`
            // here (it is MAX(BytesServed) per URL and can be a single range for range-served
            // objects) and instead walk slice indices, collecting all that exist in the index —
            // safe because non-existent candidates are simply not returned. The all-miss cost is
            // bounded by CONSECUTIVE_MISS_LIMIT, not MAX_PROBE_CHUNKS (see existing_cache_digests_for_url).
            let digests = cache_utils::existing_cache_digests_for_url(service, url, |digest| {
                cache_files_index.contains_key(&digest)
            });
            counter.fetch_add(1, Ordering::Relaxed);
            // The outer HashSet dedups physical files shared across URLs.
            digests.into_iter()
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
        .flat_map_iter(|(service, url, _bytes_served)| {
            // Filesystem twin of the index path: collect EVERY existing slice on disk for this URL,
            // not just the first. `bytes_served` (MAX per URL) is ignored — see the index-side
            // comment above. The all-miss cost is bounded by CONSECUTIVE_MISS_LIMIT.
            let paths = cache_utils::existing_cache_paths_for_url(cache_dir, service, url);
            counter.fetch_add(1, Ordering::Relaxed);
            // The outer HashSet dedups physical files shared across URLs.
            paths.into_iter()
        })
        .collect()
}

fn total_size_from_index(
    found_files: &HashSet<u128>,
    cache_files_index: &HashMap<u128, u64>,
) -> u64 {
    found_files
        .iter()
        .filter_map(|digest| cache_files_index.get(digest).copied())
        .sum()
}

fn total_size_from_filesystem(found_files: &HashSet<PathBuf>) -> u64 {
    found_files
        .iter()
        .filter_map(|path| fs::metadata(path).ok().map(|metadata| metadata.len()))
        .sum()
}

/// Report paths for index-matched digests, reconstructed via the canonical
/// `{last_2}/{middle_2}/{hash}` layout - the same layout the incremental fs probes and the
/// removers already assume, so this yields the same strings the disk walk used to carry.
fn cache_file_paths_from_digests(found_files: &HashSet<u128>, cache_dir: &Path) -> Vec<String> {
    found_files
        .iter()
        .map(|digest| cache_utils::cache_path_for_digest(cache_dir, *digest).display().to_string())
        .collect()
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
    cache_files_index: &HashMap<u128, u64>,
    cache_dir: &Path,
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
        cache_file_paths: cache_file_paths_from_digests(&found_files, cache_dir),
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
    cache_files_found: usize,
    total_size_bytes: u64,
    cache_file_paths: Vec<String>,
) -> GameCacheInfo {
    let sample_urls = cache_utils::sorted_sample_urls(
        inputs.service_urls.iter().map(|(_, url, _)| url.as_str()),
        5,
    );

    GameCacheInfo {
        game_app_id: inputs.game_app_id,
        game_name: inputs.game_name,
        cache_files_found,
        total_size_bytes,
        depot_ids: inputs.depot_ids.into_iter().collect(),
        sample_urls,
        cache_file_paths,
        service: None,
        epic_app_id: None,
    }
}

pub(crate) fn detect_steam_game_cache_info(
    records: &[DownloadRecord],
    cache_files_index: &HashMap<u128, u64>,
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    let inputs = collect_steam_game_inputs(records)?;
    let found_files = match_files_with_index(&inputs.service_urls, cache_files_index);
    let total_size = total_size_from_index(&found_files, cache_files_index);
    let paths = cache_file_paths_from_digests(&found_files, cache_dir);

    Ok(build_steam_game_cache_info(inputs, found_files.len(), total_size, paths))
}

pub(crate) fn detect_steam_game_cache_info_incremental(
    records: &[DownloadRecord],
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    let inputs = collect_steam_game_inputs(records)?;
    let found_files = match_files_in_cache(&inputs.service_urls, cache_dir);
    let total_size = total_size_from_filesystem(&found_files);
    let paths = cache_file_paths(&found_files);

    Ok(build_steam_game_cache_info(inputs, found_files.len(), total_size, paths))
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
    cache_files_found: usize,
    total_size_bytes: u64,
    cache_file_paths: Vec<String>,
) -> GameCacheInfo {
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url, _)| url.as_str()), 5);

    GameCacheInfo {
        game_app_id: generate_epic_game_app_id(epic_app_id),
        game_name: game_name.to_string(),
        cache_files_found,
        total_size_bytes,
        depot_ids: Vec::new(),
        sample_urls,
        cache_file_paths,
        service: Some("epicgames".to_string()),
        epic_app_id: Some(epic_app_id.to_string()),
    }
}

pub(crate) fn detect_epic_game_cache_info(
    epic_app_id: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<u128, u64>,
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    let found_files = match_files_with_index(service_urls, cache_files_index);
    let total_size = total_size_from_index(&found_files, cache_files_index);
    let paths = cache_file_paths_from_digests(&found_files, cache_dir);

    Ok(build_epic_game_cache_info(
        epic_app_id,
        game_name,
        service_urls,
        found_files.len(),
        total_size,
        paths,
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
    let paths = cache_file_paths(&found_files);

    Ok(build_epic_game_cache_info(
        epic_app_id,
        game_name,
        service_urls,
        found_files.len(),
        total_size,
        paths,
    ))
}

/// Build a `GameCacheInfo` for a name-keyed game (Blizzard/Riot). Unlike Epic, the
/// GameAppId stays 0 (no synthetic id — banners are name-keyed) and `epic_app_id` is None.
/// `service` carries the owning service ("blizzard"/"riot") so removal can scope by it.
fn build_named_game_cache_info(
    service: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    cache_files_found: usize,
    total_size_bytes: u64,
    cache_file_paths: Vec<String>,
) -> GameCacheInfo {
    let sample_urls =
        cache_utils::sorted_sample_urls(service_urls.iter().map(|(_, url, _)| url.as_str()), 5);

    GameCacheInfo {
        game_app_id: 0,
        game_name: game_name.to_string(),
        cache_files_found,
        total_size_bytes,
        depot_ids: Vec::new(),
        sample_urls,
        cache_file_paths,
        service: Some(service.to_lowercase()),
        epic_app_id: None,
    }
}

pub(crate) fn detect_named_game_cache_info(
    service: &str,
    game_name: &str,
    service_urls: &[ServiceUrl],
    cache_files_index: &HashMap<u128, u64>,
    cache_dir: &Path,
) -> Result<GameCacheInfo> {
    let found_files = match_files_with_index(service_urls, cache_files_index);
    let total_size = total_size_from_index(&found_files, cache_files_index);
    let paths = cache_file_paths_from_digests(&found_files, cache_dir);

    Ok(build_named_game_cache_info(
        service,
        game_name,
        service_urls,
        found_files.len(),
        total_size,
        paths,
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
    let paths = cache_file_paths(&found_files);

    Ok(build_named_game_cache_info(
        service,
        game_name,
        service_urls,
        found_files.len(),
        total_size,
        paths,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cache_detect_queries::NamedDownloadRecord;

    /// Same-service record (Blizzard/Riot): identity service == cache-hash service.
    fn rec(service: &str, game_name: &str, url: &str, bytes: i64) -> NamedDownloadRecord {
        rec_split(service, service, game_name, url, bytes)
    }

    /// Split record (Xbox): identity service differs from the cache-hash (LogEntries) service.
    fn rec_split(
        service: &str,
        cache_service: &str,
        game_name: &str,
        url: &str,
        bytes: i64,
    ) -> NamedDownloadRecord {
        NamedDownloadRecord {
            service: service.to_string(),
            cache_service: cache_service.to_string(),
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
    fn group_named_records_splits_identity_from_cache_hash_service() {
        // Xbox: identity service = `xbox` (keys the game + the removal gate), but cache files are
        // hashed under the LogEntries service `wsus`. The grouped key + identity service must be
        // `xbox`, while every ServiceUrl tuple must carry `wsus` so the cache lookup hashes correctly.
        let records = vec![
            rec_split("xbox", "wsus", "Halo Infinite", "http://x/filestreamingservice/files/abc", 100),
            rec_split("xbox", "wsus", "Halo Infinite", "http://x/filestreamingservice/files/def", 200),
        ];
        let grouped = group_named_records(&records);
        assert_eq!(grouped.len(), 1);

        let halo = grouped.get("xbox\u{1}Halo Infinite").expect("xbox halo present");
        // Identity service drives detection + removal.
        assert_eq!(halo.0, "xbox");
        assert_eq!(halo.1, "Halo Infinite");
        assert_eq!(halo.2.len(), 2);
        // Every ServiceUrl tuple hashes under the cache-hash service, NOT the identity.
        for (cache_service, _url, _bytes) in &halo.2 {
            assert_eq!(cache_service, "wsus");
        }
    }

    /// Build an index keyed by file-name digest containing the first `n_slices` ranged slices of
    /// (service, url), each `slice_size` bytes. Mirrors how the real on-disk index is keyed.
    fn build_multi_slice_index(
        service: &str,
        url: &str,
        n_slices: usize,
        slice_size: u64,
    ) -> HashMap<u128, u64> {
        let mut index = HashMap::new();
        for chunk in 0..n_slices {
            let start = chunk as u64 * cache_utils::DEFAULT_SLICE_SIZE;
            let end = start + cache_utils::DEFAULT_SLICE_SIZE - 1;
            let key = format!(
                "{}{}bytes={}-{}",
                cache_utils::service_name_lowercase(service),
                cache_utils::nginx_cache_uri(url),
                start,
                end
            );
            index.insert(cache_utils::calculate_md5_digest(&key), slice_size);
        }
        index
    }

    /// THE undercount regression gate: a range-served object spanning MANY 1 MiB slices under a
    /// SINGLE url must be counted as ALL its present slices (size = sum of every present slice),
    /// even though `bytes_served` (MAX per URL) is only ~one slice. The old `.find_map` early-exit
    /// stopped at the first slice → ~250x undercount; this proves the collect-all walk fixes it.
    #[test]
    fn multi_slice_url_counts_all_present_slices_not_just_first() {
        let service = "blizzard";
        let url = "/tpr/bnt001/data/05/d6/05d6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
        let n_slices = 100usize; // 100 contiguous 1 MiB slices, like a TACT archive
        let slice_size = cache_utils::DEFAULT_SLICE_SIZE;
        let index = build_multi_slice_index(service, url, n_slices, slice_size);

        // bytes_served is a SINGLE range (~1 MiB) — exactly the range-served undercount trigger.
        let service_urls: Vec<ServiceUrl> =
            vec![(service.to_string(), url.to_string(), slice_size as i64)];

        let found = match_files_with_index(&service_urls, &index);
        assert_eq!(
            found.len(),
            n_slices,
            "all {} present slices must be found, not just the first",
            n_slices
        );

        let total = total_size_from_index(&found, &index);
        assert_eq!(
            total,
            n_slices as u64 * slice_size,
            "total size must be the SUM of all present slices"
        );
    }

    /// The walk must bridge a small partial-eviction hole (gap < CONSECUTIVE_MISS_LIMIT) and still
    /// count slices on the far side of the gap.
    #[test]
    fn multi_slice_walk_bridges_small_eviction_holes() {
        let service = "riot";
        let url = "/channels/public/bundles/ABCDEF.bundle";
        let slice_size = cache_utils::DEFAULT_SLICE_SIZE;
        // Present slices 0..3, a 2-slice hole (3,4 absent), then 5..8 present → 7 present total.
        let mut index = build_multi_slice_index(service, url, 9, slice_size);
        for chunk in [3usize, 4usize] {
            let start = chunk as u64 * cache_utils::DEFAULT_SLICE_SIZE;
            let end = start + cache_utils::DEFAULT_SLICE_SIZE - 1;
            let digest = cache_utils::calculate_md5_digest(&format!(
                "{}{}bytes={}-{}",
                service,
                cache_utils::nginx_cache_uri(url),
                start,
                end
            ));
            index.remove(&digest);
        }

        let service_urls: Vec<ServiceUrl> = vec![(service.to_string(), url.to_string(), 0)];
        let found = match_files_with_index(&service_urls, &index);
        assert_eq!(found.len(), 7, "walk must bridge the 2-slice hole and count slices 0-2 and 5-8");
    }

    #[test]
    fn build_named_game_cache_info_keeps_app_id_zero_and_no_epic_id() {
        let info = build_named_game_cache_info(
            "Blizzard",
            "Diablo",
            &[("blizzard".to_string(), "http://b/1".to_string(), 0)],
            0,
            0,
            Vec::new(),
        );
        assert_eq!(info.game_app_id, 0);
        assert_eq!(info.epic_app_id, None);
        assert_eq!(info.service.as_deref(), Some("blizzard"));
        assert!(info.depot_ids.is_empty());
        assert_eq!(info.game_name, "Diablo");
    }
}
