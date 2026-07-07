use std::collections::{HashMap, HashSet};
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

/// On-disk cache file names grouped by datasource cache root, stored as parsed u128 md5
/// digests instead of 32-char hex Strings.
///
/// A lancache cache file's name IS the md5 hash of its cache key; the `last2/middle2/hash`
/// directory layout is derived from that hash, so the file name alone identifies the file
/// within a root. Parsing the name to its 16-byte numeric form removes the per-file heap
/// String from the index (millions of files -> hundreds of MB), while the per-root grouping
/// preserves the old full-path guarantee that a candidate can only match files under its own
/// resolved datasource root. A name that is not exactly 32 hex chars cannot equal any md5
/// probe candidate, so skipping it from the index cannot change any probe outcome.
pub(super) struct FilesOnDisk {
    digests_by_root: HashMap<PathBuf, HashSet<u128>>,
}

impl FilesOnDisk {
    pub(super) fn len(&self) -> usize {
        self.digests_by_root.values().map(HashSet::len).sum()
    }

    pub(super) fn is_empty(&self) -> bool {
        self.digests_by_root.values().all(HashSet::is_empty)
    }

    fn digests_for_root(&self, root: &Path) -> Option<&HashSet<u128>> {
        self.digests_by_root.get(root)
    }
}

/// Identity of one unique cache probe: resolved datasource root + normalized service + URL.
/// Probe results are memoized per `memo_key` across the entire scan, since many downloads share
/// the same (service, url, datasource) tuple and the on-disk index is immutable scan-wide.
///
/// `memo_key` is the md5 digest of the `(root, service, url)` composite - the memo stores only
/// this 16-byte key and the bool result, so the scan-wide memo no longer retains an owned root
/// PathBuf + service + full URL String per unique tuple (the unbounded growth on large
/// libraries). The strings on this struct live only for the current batch.
///
/// `bytes_served` (the URL's `MAX(LogEntries.BytesServed)`) sizes the probe chunk count via
/// `cache_utils::probe_chunks_for_bytes`, but is DELIBERATELY EXCLUDED from the memo identity.
/// The byte size is a deterministic function of (service, url) within a scan, so two keys with
/// the same tuple probe the same candidate set; keeping it out of the identity guarantees the
/// scan-wide memo still dedups each unique (root, service, url) to exactly one probe regardless
/// of per-row size noise.
pub(super) struct ProbeKey {
    pub(super) memo_key: u128,
    root: PathBuf,
    service: String,
    url: String,
    bytes_served: i64,
}

/// Separator for the memo-key composite. `\u{1}` cannot appear in a datasource path, service
/// name, or logged URL, so the three parts stay unambiguous.
const MEMO_KEY_SEP: char = '\u{1}';

impl ProbeKey {
    pub(super) fn new(
        service: &str,
        url: String,
        datasource: Option<&str>,
        bytes_served: i64,
        roots: &DatasourceRoots,
    ) -> Self {
        let root = roots.resolve(datasource).to_path_buf();
        let service = cache_utils::service_name_lowercase(service);
        // Incremental md5 over the parts - this runs once per log row, and a composite
        // format! String here would be a per-row allocation on multi-million-row scans.
        let mut memo_hash = md5::Context::new();
        memo_hash.consume(root.as_os_str().as_encoded_bytes());
        memo_hash.consume([MEMO_KEY_SEP as u8]);
        memo_hash.consume(service.as_bytes());
        memo_hash.consume([MEMO_KEY_SEP as u8]);
        memo_hash.consume(url.as_bytes());
        let memo_key = u128::from_be_bytes(memo_hash.compute().0);
        Self {
            memo_key,
            root,
            service,
            url,
            bytes_served,
        }
    }

    /// True when any cache-key digest candidate for this (service, url) exists in the
    /// resolved root's on-disk digest set. Early-exits on the first hit. The candidate
    /// count is sized from `bytes_served` (clamped to [DEFAULT_MAX_CHUNKS, MAX_PROBE_CHUNKS]),
    /// so large objects whose present slices fall past the first 100 MiB are still found.
    pub(super) fn has_cache_file(&self, files_on_disk: &FilesOnDisk) -> bool {
        let Some(digests) = files_on_disk.digests_for_root(&self.root) else {
            return false;
        };

        cache_utils::cache_digest_candidates_iter(
            &self.service,
            &self.url,
            cache_utils::probe_chunks_for_bytes(self.bytes_served),
        )
        .any(|digest| digests.contains(&digest))
    }

    /// True when this key's resolved datasource cache root was actually indexed this
    /// scan (its directory existed and was walked). A key whose root is NOT indexed
    /// cannot be verified: a probe miss means "we never looked here", not "the file is
    /// gone". Callers must not treat such a miss as eviction evidence.
    pub(super) fn root_is_indexed(&self, files_on_disk: &FilesOnDisk) -> bool {
        files_on_disk.digests_for_root(&self.root).is_some()
    }
}

/// Walks all configured cache directories and indexes file-name digests per datasource root.
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
    let mut digests_by_root: HashMap<PathBuf, HashSet<u128>> = HashMap::new();
    let mut total_files = 0usize;
    let mut files_since_last_report = 0usize;
    let mut non_hash_names = 0usize;

    for ds in datasources {
        let cache_dir = Path::new(&ds.cache_path);
        if !cache_dir.exists() {
            eprintln!(
                "[EvictionScan] Cache directory does not exist for datasource '{}': {}",
                ds.name, ds.cache_path
            );
            continue;
        }

        let root_digests = digests_by_root
            .entry(PathBuf::from(&ds.cache_path))
            .or_default();

        for entry in jwalk::WalkDir::new(cache_dir)
            .min_depth(1)
            .into_iter()
            .filter_map(|e| e.ok())
        {
            if entry.file_type().is_file() {
                match entry
                    .file_name()
                    .to_str()
                    .and_then(cache_utils::parse_cache_file_digest)
                {
                    Some(digest) => {
                        if root_digests.insert(digest) {
                            total_files += 1;
                        }
                    }
                    // Not a 32-hex md5 name -> can never match a probe candidate; keep it out
                    // of the index but surface the count so a weird cache layout is visible.
                    None => non_hash_names += 1,
                }
                files_since_last_report += 1;
                if files_since_last_report >= FILE_COUNT_PROGRESS_INTERVAL {
                    on_file_count(total_files);
                    files_since_last_report = 0;
                }
            }
        }
    }

    if non_hash_names > 0 {
        eprintln!(
            "[EvictionScan] Skipped {} file(s) whose names are not 32-hex md5 cache keys (nginx temp or foreign files - they can never match a probe candidate)",
            non_hash_names
        );
    }

    if files_since_last_report > 0 || total_files > 0 {
        on_file_count(total_files);
    }

    FilesOnDisk { digests_by_root }
}

fn datasource_lookup_key(name: &str) -> String {
    name.to_lowercase()
}
