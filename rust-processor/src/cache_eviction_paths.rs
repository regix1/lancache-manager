use std::collections::{HashMap, HashSet};
use std::ffi::OsStr;
use std::path::{Path, PathBuf};

use crate::cache_utils;

use super::DatasourceConfig;

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

/// On-disk cache file names (32-char md5-hex cache keys) grouped by datasource cache root.
///
/// A lancache cache file's name IS the md5 hash of its cache key; the `last2/middle2/hash`
/// directory layout is derived from that hash, so the file name alone identifies the file
/// within a root. Keying by name (instead of full path) removes all PathBuf/path-String
/// work from both the disk walk (millions of files) and the probe side, while the per-root
/// grouping preserves the old full-path guarantee that a candidate can only match files
/// under its own resolved datasource root.
pub(super) struct FilesOnDisk {
    names_by_root: HashMap<PathBuf, HashSet<String>>,
}

impl FilesOnDisk {
    pub(super) fn len(&self) -> usize {
        self.names_by_root.values().map(HashSet::len).sum()
    }

    pub(super) fn is_empty(&self) -> bool {
        self.names_by_root.values().all(HashSet::is_empty)
    }

    fn names_for_root(&self, root: &Path) -> Option<&HashSet<String>> {
        self.names_by_root.get(root)
    }
}

/// Identity of one unique cache probe: resolved datasource root + normalized service + URL.
/// Probe results are memoized per key across the entire scan, since many downloads share
/// the same (service, url, datasource) tuple and the on-disk index is immutable scan-wide.
#[derive(Clone, PartialEq, Eq, Hash)]
pub(super) struct ProbeKey {
    root: PathBuf,
    service: String,
    url: String,
}

impl ProbeKey {
    pub(super) fn new(
        service: &str,
        url: String,
        datasource: Option<&str>,
        roots: &DatasourceRoots,
    ) -> Self {
        Self {
            root: roots.resolve(datasource).to_path_buf(),
            service: cache_utils::normalize_service_name(service),
            url,
        }
    }

    /// True when any cache-key hash candidate for this (service, url) exists in the
    /// resolved root's on-disk file-name set. Early-exits on the first hit.
    pub(super) fn has_cache_file(&self, files_on_disk: &FilesOnDisk) -> bool {
        let Some(names) = files_on_disk.names_for_root(&self.root) else {
            return false;
        };

        cache_utils::cache_hash_candidates_iter(
            &self.service,
            &self.url,
            cache_utils::DEFAULT_MAX_CHUNKS,
        )
        .any(|hash| names.contains(&hash))
    }
}

/// Walks all configured cache directories and indexes file names per datasource root.
/// Invokes `on_file_count` every `FILE_COUNT_PROGRESS_INTERVAL` files so the
/// caller can report incremental progress during large scans (millions of files).
const FILE_COUNT_PROGRESS_INTERVAL: usize = 25_000;

pub(super) fn collect_files_on_disk<F>(
    datasources: &[DatasourceConfig],
    mut on_file_count: F,
) -> FilesOnDisk
where
    F: FnMut(usize),
{
    let mut names_by_root: HashMap<PathBuf, HashSet<String>> = HashMap::new();
    let mut total_files = 0usize;
    let mut files_since_last_report = 0usize;

    for ds in datasources {
        let cache_dir = Path::new(&ds.cache_path);
        if !cache_dir.exists() {
            eprintln!(
                "[EvictionScan] Cache directory does not exist for datasource '{}': {}",
                ds.name, ds.cache_path
            );
            continue;
        }

        let root_names = names_by_root
            .entry(PathBuf::from(&ds.cache_path))
            .or_default();

        for entry in jwalk::WalkDir::new(cache_dir)
            .min_depth(1)
            .into_iter()
            .filter_map(|e| e.ok())
        {
            if entry.file_type().is_file() {
                if root_names.insert(file_name_lookup_key(entry.file_name())) {
                    total_files += 1;
                }
                files_since_last_report += 1;
                if files_since_last_report >= FILE_COUNT_PROGRESS_INTERVAL {
                    on_file_count(total_files);
                    files_since_last_report = 0;
                }
            }
        }
    }

    if files_since_last_report > 0 || total_files > 0 {
        on_file_count(total_files);
    }

    FilesOnDisk { names_by_root }
}

fn datasource_lookup_key(name: &str) -> String {
    name.to_lowercase()
}

#[cfg(windows)]
fn file_name_lookup_key(name: &OsStr) -> String {
    // Avoid false negatives from filesystem casing differences; md5 hex candidates from
    // calculate_md5 are always lowercase.
    name.to_string_lossy().to_lowercase()
}

#[cfg(not(windows))]
fn file_name_lookup_key(name: &OsStr) -> String {
    name.to_string_lossy().into_owned()
}
