use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use crate::cache_utils;

use super::DatasourceConfig;

pub(super) struct DatasourceRoots {
    cache_paths_by_name: HashMap<String, (PathBuf, cache_utils::CacheKeyScheme)>,
    default_cache_path: PathBuf,
    default_scheme: cache_utils::CacheKeyScheme,
    allow_null_default: bool,
}

impl DatasourceRoots {
    pub(super) fn from_configs(datasources: &[DatasourceConfig]) -> Self {
        let mut cache_paths_by_name = HashMap::with_capacity(datasources.len());
        let mut default_entry: Option<(PathBuf, cache_utils::CacheKeyScheme)> = None;

        for ds in datasources {
            let cache_path = PathBuf::from(&ds.cache_path);
            let scheme = cache_utils::CacheKeyScheme::from_config_str(&ds.key_scheme);
            cache_paths_by_name.insert(
                datasource_lookup_key(&ds.name),
                (cache_path.clone(), scheme),
            );
            if ds.is_default {
                default_entry = Some((cache_path, scheme));
            }
        }

        let (default_cache_path, default_scheme) = default_entry.unwrap_or_else(|| {
            (
                PathBuf::from(&datasources[0].cache_path),
                cache_utils::CacheKeyScheme::from_config_str(&datasources[0].key_scheme),
            )
        });

        Self {
            cache_paths_by_name,
            default_cache_path,
            default_scheme,
            // Older single-datasource installs can have LogEntries written before Datasource
            // was populated. With exactly one datasource the fallback is unambiguous and keeps
            // those rows verifiable. Multiple datasources can share a root while using different
            // key schemes, so a NULL datasource there must remain unresolved.
            allow_null_default: datasources.len() == 1,
        }
    }

    fn resolve(&self, datasource: Option<&str>) -> Option<(&Path, cache_utils::CacheKeyScheme)> {
        match datasource {
            Some(name) if !name.trim().is_empty() => self
                .cache_paths_by_name
                .get(&datasource_lookup_key(name))
                .map(|(path, scheme)| (path.as_path(), *scheme)),
            None if self.allow_null_default => {
                Some((self.default_cache_path.as_path(), self.default_scheme))
            }
            Some(_) | None => None,
        }
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

/// Identity of one unique cache probe: datasource resolution + normalized service + URL.
/// Probe results are memoized per `memo_key` across the entire scan, since many downloads share
/// the same (service, url, datasource) tuple and the on-disk index is immutable scan-wide.
///
/// For resolved datasources, `memo_key` is the md5 digest of the `(root, scheme, service, url)`
/// composite. Unresolved datasource names use an explicit sentinel plus the raw datasource value;
/// they never probe or support absence decisions. The memo stores only this 16-byte key and the
/// bool result, so the scan-wide memo no longer retains an owned root PathBuf + service + full URL
/// String per unique tuple (the unbounded growth on large libraries). Including the scheme
/// prevents datasources that share a root but use different key recipes from reusing one another's
/// probe result. The strings on this struct live only for the current batch.
///
/// `bytes_served` (the URL's `MAX(LogEntries.BytesServed)`) sizes the probe chunk count via
/// `cache_utils::probe_chunks_for_bytes`, but is DELIBERATELY EXCLUDED from the memo identity.
/// The byte size is a deterministic function of (service, url) within a scan, so two keys with
/// the same tuple probe the same candidate set; keeping it out of the identity guarantees the
/// scan-wide memo still dedups each unique (root, scheme, service, url) to exactly one probe
/// regardless of per-row size noise.
pub(super) struct ProbeKey {
    pub(super) memo_key: u128,
    recipe: ProbeRecipe,
    service: String,
    url: String,
    bytes_served: i64,
}

enum ProbeRecipe {
    Resolved {
        root: PathBuf,
        scheme: cache_utils::CacheKeyScheme,
    },
    /// The datasource was NULL in a multi-datasource install, empty, or unknown. Treating it as
    /// the configured default can select the wrong key scheme on a shared cache root.
    Unresolved,
}

/// Separator for the memo-key composite. `\u{1}` cannot appear in a datasource path, scheme,
/// service name, or logged URL, so the four parts stay unambiguous.
const MEMO_KEY_SEP: char = '\u{1}';

impl ProbeKey {
    pub(super) fn new(
        service: &str,
        url: String,
        datasource: Option<&str>,
        bytes_served: i64,
        roots: &DatasourceRoots,
    ) -> Self {
        let recipe = match roots.resolve(datasource) {
            Some((root, scheme)) => ProbeRecipe::Resolved {
                root: root.to_path_buf(),
                scheme,
            },
            None => ProbeRecipe::Unresolved,
        };
        let service = cache_utils::service_name_lowercase(service);
        // Incremental md5 over the parts - this runs once per log row, and a composite
        // format! String here would be a per-row allocation on multi-million-row scans.
        let mut memo_hash = md5::Context::new();
        match &recipe {
            ProbeRecipe::Resolved { root, scheme } => {
                memo_hash.consume(b"resolved");
                memo_hash.consume([MEMO_KEY_SEP as u8]);
                memo_hash.consume(root.as_os_str().as_encoded_bytes());
                memo_hash.consume([MEMO_KEY_SEP as u8]);
                memo_hash.consume(match scheme {
                    cache_utils::CacheKeyScheme::Monolithic => b"monolithic".as_slice(),
                    cache_utils::CacheKeyScheme::BareMetal => b"bare_metal".as_slice(),
                });
            }
            ProbeRecipe::Unresolved => {
                memo_hash.consume(b"unresolved");
                memo_hash.consume([MEMO_KEY_SEP as u8]);
                memo_hash.consume(datasource.unwrap_or("<null>").as_bytes());
            }
        }
        memo_hash.consume([MEMO_KEY_SEP as u8]);
        memo_hash.consume(service.as_bytes());
        memo_hash.consume([MEMO_KEY_SEP as u8]);
        memo_hash.consume(url.as_bytes());
        let memo_key = u128::from_be_bytes(memo_hash.compute().0);
        Self {
            memo_key,
            recipe,
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
        let ProbeRecipe::Resolved { root, scheme } = &self.recipe else {
            return false;
        };
        let Some(digests) = files_on_disk.digests_for_root(root) else {
            return false;
        };

        let max_chunks = cache_utils::probe_chunks_for_bytes(self.bytes_served);
        match scheme {
            cache_utils::CacheKeyScheme::Monolithic => {
                cache_utils::cache_digest_candidates_iter(&self.service, &self.url, max_chunks)
                    .any(|digest| digests.contains(&digest))
            }
            cache_utils::CacheKeyScheme::BareMetal => {
                cache_utils::bare_metal_digest_candidates_iter(&self.service, &self.url, max_chunks)
                    .any(|digest| digests.contains(&digest))
            }
        }
    }

    /// True when this key's resolved datasource cache root was indexed with at least one cache
    /// file and its service has a known recipe under the selected scheme. A missing root means
    /// "we never looked here"; an indexed-but-empty root is indistinguishable from a wrong
    /// mount or path, so it abstains exactly like a missing root (a genuinely emptied cache is
    /// reconciled by the cache-clear flow, not this scan); an unknown bare-metal service means
    /// "we do not know which key to look for". None of these misses is eviction evidence.
    pub(super) fn can_verify_absence(&self, files_on_disk: &FilesOnDisk) -> bool {
        let ProbeRecipe::Resolved { root, .. } = &self.recipe else {
            return false;
        };
        let root_indexed_with_files = files_on_disk
            .digests_for_root(root)
            .is_some_and(|digests| !digests.is_empty());
        if !root_indexed_with_files {
            return false;
        }

        self.has_known_recipe()
    }

    fn has_known_recipe(&self) -> bool {
        match &self.recipe {
            ProbeRecipe::Resolved {
                scheme: cache_utils::CacheKeyScheme::Monolithic,
                ..
            } => true,
            ProbeRecipe::Resolved {
                scheme: cache_utils::CacheKeyScheme::BareMetal,
                ..
            } => cache_utils::bare_metal_prefix(&self.service).is_some(),
            ProbeRecipe::Unresolved => false,
        }
    }

    pub(super) fn has_unknown_recipe(&self) -> bool {
        matches!(
            &self.recipe,
            ProbeRecipe::Resolved {
                scheme: cache_utils::CacheKeyScheme::BareMetal,
                ..
            }
        ) && !self.has_known_recipe()
    }

    pub(super) fn is_bare_metal(&self) -> bool {
        matches!(
            &self.recipe,
            ProbeRecipe::Resolved {
                scheme: cache_utils::CacheKeyScheme::BareMetal,
                ..
            }
        )
    }

    fn is_unresolved(&self) -> bool {
        matches!(&self.recipe, ProbeRecipe::Unresolved)
    }
}

/// Whether a download's complete probe-key set can support an absence decision. For fully
/// resolved Monolithic keys, the shipped policy remains unchanged: one indexed datasource root
/// makes the download verifiable. Bare-metal and unresolved-datasource absence are fail-closed
/// because an unknown recipe, scheme, or root otherwise looks exactly like a cache miss.
pub(super) fn keys_can_verify_absence(keys: &[ProbeKey], files_on_disk: &FilesOnDisk) -> bool {
    if keys.is_empty() {
        return false;
    }

    // An unresolved datasource may refer to a different key scheme on the same root. Even if a
    // different key is checkable, the unresolved key makes an all-absent conclusion unsafe.
    if keys.iter().any(ProbeKey::is_unresolved) {
        return false;
    }

    if keys.iter().any(ProbeKey::is_bare_metal) {
        keys.iter().all(|key| key.can_verify_absence(files_on_disk))
    } else {
        keys.iter().any(|key| key.can_verify_absence(files_on_disk))
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

        // A directory that exists but yields zero hash-named cache files is indistinguishable
        // from a wrong mount or path; a genuinely emptied cache is reconciled by the
        // cache-clear flow, not this scan, so this root abstains from absence verification.
        if root_digests.is_empty() {
            eprintln!(
                "[EvictionScan] Cache directory for datasource '{}' exists but contains no cache files: {} - absence cannot be verified under this root, so its downloads will not be marked evicted",
                ds.name, ds.cache_path
            );
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

#[cfg(test)]
mod tests {
    use super::*;

    fn datasource(name: &str, cache_path: &Path, key_scheme: &str) -> DatasourceConfig {
        DatasourceConfig {
            name: name.to_string(),
            cache_path: cache_path.to_string_lossy().into_owned(),
            is_default: name == "monolithic",
            key_scheme: key_scheme.to_string(),
        }
    }

    fn indexed_root(root: &Path, digests: impl IntoIterator<Item = u128>) -> FilesOnDisk {
        FilesOnDisk {
            digests_by_root: HashMap::from([(root.to_path_buf(), digests.into_iter().collect())]),
        }
    }

    #[test]
    fn memo_identity_and_probe_recipe_include_datasource_scheme() {
        let root = Path::new("cache");
        let datasources = [
            datasource("monolithic", root, "monolithic"),
            datasource("bare", root, "bare_metal"),
        ];
        let roots = DatasourceRoots::from_configs(&datasources);
        let url = "/depot/1/chunk/abcdef";
        let monolithic = ProbeKey::new("steam", url.to_string(), Some("monolithic"), 0, &roots);
        let bare = ProbeKey::new("steam", url.to_string(), Some("bare"), 0, &roots);

        assert_ne!(monolithic.memo_key, bare.memo_key);

        let bare_digest = cache_utils::bare_metal_digest_candidates_iter("steam", url, 100)
            .next()
            .unwrap();
        let files = indexed_root(root, [bare_digest]);
        assert!(!monolithic.has_cache_file(&files));
        assert!(bare.has_cache_file(&files));
    }

    #[test]
    fn ambiguous_datasources_are_unresolved_on_multi_datasource_installs() {
        let root = Path::new("shared-cache");
        let datasources = [
            datasource("monolithic", root, "monolithic"),
            datasource("bare", root, "bare_metal"),
        ];
        let roots = DatasourceRoots::from_configs(&datasources);
        let url = "/depot/1/chunk/abcdef";
        let bare_digest = cache_utils::bare_metal_digest_candidates_iter("steam", url, 100)
            .next()
            .unwrap();
        let files = indexed_root(root, [bare_digest]);

        for datasource in [None, Some(""), Some("missing")] {
            let key = ProbeKey::new("steam", url.to_string(), datasource, 0, &roots);
            assert!(!key.has_cache_file(&files));
            assert!(!key.can_verify_absence(&files));
            assert!(!keys_can_verify_absence(&[key], &files));
        }
    }

    #[test]
    fn unresolved_key_prevents_mixed_key_absence_decision() {
        let root = Path::new("shared-cache");
        let datasources = [
            datasource("monolithic", root, "monolithic"),
            datasource("bare", root, "bare_metal"),
        ];
        let roots = DatasourceRoots::from_configs(&datasources);
        let files = indexed_root(root, [1]);
        let resolved = ProbeKey::new("steam", "/known".to_string(), Some("monolithic"), 0, &roots);
        let unresolved = ProbeKey::new(
            "steam",
            "/ambiguous".to_string(),
            Some("missing"),
            0,
            &roots,
        );

        assert!(resolved.can_verify_absence(&files));
        assert!(!keys_can_verify_absence(&[resolved, unresolved], &files));
    }

    #[test]
    fn null_datasource_keeps_unambiguous_single_datasource_fallback() {
        let root = Path::new("only-cache");
        let datasources = [datasource("only", root, "bare_metal")];
        let roots = DatasourceRoots::from_configs(&datasources);
        let url = "/depot/1/chunk/abcdef";
        let bare_digest = cache_utils::bare_metal_digest_candidates_iter("steam", url, 100)
            .next()
            .unwrap();
        let files = indexed_root(root, [bare_digest]);
        let null_key = ProbeKey::new("steam", url.to_string(), None, 0, &roots);
        let unknown_key = ProbeKey::new("steam", url.to_string(), Some("missing"), 0, &roots);

        assert!(null_key.has_cache_file(&files));
        assert!(null_key.can_verify_absence(&files));
        assert!(!unknown_key.can_verify_absence(&files));
    }

    #[test]
    fn unknown_bare_metal_service_cannot_verify_absence() {
        let root = Path::new("cache");
        let datasources = [
            datasource("monolithic", root, "monolithic"),
            datasource("bare", root, "bare_metal"),
        ];
        let roots = DatasourceRoots::from_configs(&datasources);
        let files = indexed_root(root, [1]);
        let monolithic = ProbeKey::new(
            "unsupported",
            "/content".to_string(),
            Some("monolithic"),
            0,
            &roots,
        );
        let bare = ProbeKey::new(
            "unsupported",
            "/content".to_string(),
            Some("bare"),
            0,
            &roots,
        );

        assert!(monolithic.can_verify_absence(&files));
        assert!(!bare.can_verify_absence(&files));
        assert!(bare.has_unknown_recipe());
        assert!(!keys_can_verify_absence(&[monolithic, bare], &files));
    }

    #[test]
    fn monolithic_multi_root_verifiability_policy_is_unchanged() {
        let indexed = Path::new("indexed");
        let offline = Path::new("offline");
        let datasources = [
            datasource("monolithic", indexed, "monolithic"),
            datasource("offline", offline, "monolithic"),
        ];
        let roots = DatasourceRoots::from_configs(&datasources);
        let files = indexed_root(indexed, [1]);
        let indexed_key = ProbeKey::new(
            "steam",
            "/content".to_string(),
            Some("monolithic"),
            0,
            &roots,
        );
        let offline_key =
            ProbeKey::new("steam", "/content".to_string(), Some("offline"), 0, &roots);

        assert!(keys_can_verify_absence(&[indexed_key, offline_key], &files));
    }

    #[test]
    fn empty_indexed_root_cannot_verify_absence() {
        // An indexed-but-empty root is indistinguishable from a wrong mount or path, so it
        // abstains exactly like a missing root instead of confirming every probe as absent.
        let root = Path::new("cache");
        let datasources = [datasource("monolithic", root, "monolithic")];
        let roots = DatasourceRoots::from_configs(&datasources);
        let key = ProbeKey::new("steam", "/content".to_string(), Some("monolithic"), 0, &roots);
        let files = indexed_root(root, []);

        assert!(!key.has_cache_file(&files));
        assert!(!key.can_verify_absence(&files));
        assert!(!keys_can_verify_absence(&[key], &files));
    }

    #[test]
    fn indexed_root_with_cache_files_verifies_absence() {
        let root = Path::new("cache");
        let datasources = [datasource("monolithic", root, "monolithic")];
        let roots = DatasourceRoots::from_configs(&datasources);
        let key = ProbeKey::new("steam", "/content".to_string(), Some("monolithic"), 0, &roots);
        let files = indexed_root(root, [1]);

        assert!(key.can_verify_absence(&files));
        assert!(keys_can_verify_absence(&[key], &files));
    }
}
