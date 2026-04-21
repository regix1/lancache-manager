use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use crate::cache_utils;

use super::{DatasourceConfig, DownloadEntry};

pub(super) struct DatasourceRoots {
    cache_paths_by_name: HashMap<String, PathBuf>,
    default_cache_path: PathBuf,
}

impl DatasourceRoots {
    pub(super) fn from_configs(datasources: &[DatasourceConfig]) -> Self {
        let mut cache_paths_by_name = HashMap::with_capacity(datasources.len());
        let mut default_cache_path: Option<PathBuf> = None;

        for ds in datasources {
            let cache_path = PathBuf::from(&ds.cache_path);
            cache_paths_by_name.insert(datasource_lookup_key(&ds.name), cache_path.clone());
            if ds.is_default {
                default_cache_path = Some(cache_path.clone());
            }
        }

        let default_cache_path = default_cache_path
            .unwrap_or_else(|| PathBuf::from(&datasources[0].cache_path));

        Self {
            cache_paths_by_name,
            default_cache_path,
        }
    }

    fn resolve(&self, datasource: Option<&str>) -> &Path {
        datasource
            .map(datasource_lookup_key)
            .and_then(|name| self.cache_paths_by_name.get(&name))
            .map(PathBuf::as_path)
            .unwrap_or(self.default_cache_path.as_path())
    }
}

pub(super) fn collect_files_on_disk(datasources: &[DatasourceConfig]) -> HashSet<String> {
    let mut files_on_disk = HashSet::new();

    for ds in datasources {
        let cache_dir = Path::new(&ds.cache_path);
        if !cache_dir.exists() {
            eprintln!(
                "[EvictionScan] Cache directory does not exist for datasource '{}': {}",
                ds.name, ds.cache_path
            );
            continue;
        }

        for entry in jwalk::WalkDir::new(cache_dir)
            .min_depth(1)
            .into_iter()
            .filter_map(|e| e.ok())
        {
            if entry.file_type().is_file() {
                let path = entry.path();
                files_on_disk.insert(path_lookup_key(&path));
            }
        }
    }

    files_on_disk
}

pub(super) fn download_has_cache_file(
    entries: &[DownloadEntry],
    datasource_roots: &DatasourceRoots,
    files_on_disk: &HashSet<String>,
) -> bool {
    entries.iter().any(|entry| {
        let cache_dir = datasource_roots.resolve(entry.datasource.as_deref());
        let service = cache_utils::normalize_service_name(&entry.service);

        cache_utils::cache_path_candidates_for_probe(
            cache_dir,
            &service,
            &entry.url,
            cache_utils::DEFAULT_MAX_CHUNKS,
        )
        .into_iter()
        .any(|path| files_on_disk.contains(&path_lookup_key(&path)))
    })
}

fn datasource_lookup_key(name: &str) -> String {
    name.to_lowercase()
}

#[cfg(windows)]
fn path_lookup_key(path: &Path) -> String {
    // Avoid false negatives from drive-letter or separator casing differences.
    path.to_string_lossy().replace('\\', "/").to_lowercase()
}

#[cfg(not(windows))]
fn path_lookup_key(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}
