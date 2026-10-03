use std::collections::{BTreeSet, HashMap, HashSet};
use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};
use lancache_processor::cache_repair;
use lancache_processor::cache_utils;
use uuid::Uuid;

use super::{DatasourceConfig, RepairSource};

pub(super) struct DatasourceRoots {
    cache_paths_by_name: HashMap<String, (PathBuf, cache_utils::CacheKeyScheme)>,
    default_cache_path: PathBuf,
    default_scheme: cache_utils::CacheKeyScheme,
    allow_null_default: bool,
}

impl DatasourceRoots {
    pub(super) fn from_configs(datasources: &[DatasourceConfig]) -> Self {
        let paths = datasources
            .iter()
            .map(|source| {
                (
                    datasource_lookup_key(&source.name),
                    PathBuf::from(&source.cache_path),
                )
            })
            .collect();
        Self::from_paths(datasources, &paths)
    }

    fn from_paths(datasources: &[DatasourceConfig], paths: &HashMap<String, PathBuf>) -> Self {
        let mut cache_paths_by_name = HashMap::with_capacity(datasources.len());
        let mut default_entry: Option<(PathBuf, cache_utils::CacheKeyScheme)> = None;

        for ds in datasources {
            let Some(cache_path) = paths.get(&datasource_lookup_key(&ds.name)).cloned() else {
                continue;
            };
            let scheme = cache_utils::CacheKeyScheme::from_config_str(&ds.key_scheme);
            cache_paths_by_name.insert(
                datasource_lookup_key(&ds.name),
                (cache_path.clone(), scheme),
            );
            if ds.is_default {
                default_entry = Some((cache_path, scheme));
            }
        }

        let (default_cache_path, default_scheme) = default_entry
            .or_else(|| cache_paths_by_name.values().next().cloned())
            .unwrap_or_else(|| {
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

pub(super) struct RepairIndex {
    pub(super) roots: DatasourceRoots,
    pub(super) files: FilesOnDisk,
    affected_roots: HashSet<PathBuf>,
    trusted_empty_roots: HashSet<PathBuf>,
}

impl RepairIndex {
    pub(super) fn intersects(&self, primary: Option<&str>, origins: &[Option<String>]) -> bool {
        std::iter::once(primary)
            .chain(origins.iter().map(Option::as_deref))
            .filter_map(|origin| self.roots.resolve(origin).map(|(root, _)| root))
            .any(|root| self.affected_roots.contains(root))
    }

    pub(super) fn origins_can_verify_absence(
        &self,
        primary: Option<&str>,
        origins: &[Option<String>],
    ) -> bool {
        let mut saw_origin = false;
        for origin in std::iter::once(primary).chain(origins.iter().map(Option::as_deref)) {
            saw_origin = true;
            let Some((root, _)) = self.roots.resolve(origin) else {
                return false;
            };
            let indexed = self
                .files
                .digests_for_root(root)
                .is_some_and(|digests| !digests.is_empty());
            if !indexed && !self.trusted_empty_roots.contains(root) {
                return false;
            }
        }
        saw_origin
    }

    pub(super) fn origins_are_trusted_empty(
        &self,
        primary: Option<&str>,
        origins: &[Option<String>],
    ) -> bool {
        let mut saw_origin = false;
        for origin in std::iter::once(primary).chain(origins.iter().map(Option::as_deref)) {
            saw_origin = true;
            let Some((root, _)) = self.roots.resolve(origin) else {
                return false;
            };
            if !self.trusted_empty_roots.contains(root) {
                return false;
            }
        }
        saw_origin
    }

    pub(super) fn origins_include_bare_metal(
        &self,
        primary: Option<&str>,
        origins: &[Option<String>],
    ) -> bool {
        std::iter::once(primary)
            .chain(origins.iter().map(Option::as_deref))
            .filter_map(|origin| self.roots.resolve(origin).map(|(_, scheme)| scheme))
            .any(|scheme| scheme == cache_utils::CacheKeyScheme::BareMetal)
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

    pub(super) fn can_verify_repair_absence(
        &self,
        files_on_disk: &FilesOnDisk,
        trusted_empty_roots: &HashSet<PathBuf>,
    ) -> bool {
        let ProbeRecipe::Resolved { root, .. } = &self.recipe else {
            return false;
        };
        let root_checked = files_on_disk
            .digests_for_root(root)
            .is_some_and(|digests| !digests.is_empty() || trusted_empty_roots.contains(root));
        root_checked && self.has_known_recipe()
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

pub(super) fn keys_can_verify_repair_absence(keys: &[ProbeKey], repair: &RepairIndex) -> bool {
    !keys.is_empty()
        && keys
            .iter()
            .all(|key| key.can_verify_repair_absence(&repair.files, &repair.trusted_empty_roots))
}

/// Walks all configured cache directories and indexes file-name digests per datasource root.
/// Invokes `on_file_count` every `FILE_COUNT_PROGRESS_INTERVAL` files so the
/// caller can report incremental progress during large scans (millions of files).
const FILE_COUNT_PROGRESS_INTERVAL: usize = 25_000;

pub(super) fn collect_files_on_disk<F>(
    datasources: &[DatasourceConfig],
    on_file_count: F,
) -> (FilesOnDisk, Vec<String>)
where
    F: FnMut(usize),
{
    collect_files_on_disk_with(datasources, on_file_count, cache_utils::walk_cache_root)
}

/// `collect_files_on_disk` with the folder walk passed in, so a test can report a folder that
/// could not be read. Returns the index and, sorted, every cache folder the scan did not check:
/// missing, holding no cache file, or with a folder of the cache layout that could not be read.
fn collect_files_on_disk_with<F, W>(
    datasources: &[DatasourceConfig],
    mut on_file_count: F,
    mut walk_root: W,
) -> (FilesOnDisk, Vec<String>)
where
    F: FnMut(usize),
    W: FnMut(&Path, &mut dyn FnMut(&str)) -> bool,
{
    let mut digests_by_root: HashMap<PathBuf, HashSet<u128>> = HashMap::new();
    let mut unchecked_roots = BTreeSet::new();
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
            unchecked_roots.insert(ds.cache_path.clone());
            continue;
        }

        let root = PathBuf::from(&ds.cache_path);
        let mut root_digests = digests_by_root.remove(&root).unwrap_or_default();
        let fully_checked = walk_root(cache_dir, &mut |name: &str| {
            match cache_utils::parse_cache_file_digest(name) {
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
        });

        // A folder of the cache layout that could not be read hides the files in it, and a missing
        // file reads as an eviction, so the whole root abstains, as the repair walk does.
        if !fully_checked {
            eprintln!(
                "[EvictionScan] Cache directory for datasource '{}' has folders that could not be read: {} - absence cannot be verified under this root, so its downloads will not be marked evicted",
                ds.name, ds.cache_path
            );
            unchecked_roots.insert(ds.cache_path.clone());
            continue;
        }
        // Two datasources can share one root; once a walk of it succeeds, it was checked.
        unchecked_roots.remove(&ds.cache_path);

        // A directory that exists but yields zero hash-named cache files is indistinguishable
        // from a wrong mount or path; a genuinely emptied cache is reconciled by the
        // cache-clear flow, not this scan, so this root abstains from absence verification.
        if root_digests.is_empty() {
            eprintln!(
                "[EvictionScan] Cache directory for datasource '{}' exists but contains no cache files: {} - absence cannot be verified under this root, so its downloads will not be marked evicted",
                ds.name, ds.cache_path
            );
            unchecked_roots.insert(ds.cache_path.clone());
        }
        digests_by_root.insert(root, root_digests);
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

    (
        FilesOnDisk { digests_by_root },
        unchecked_roots.into_iter().collect(),
    )
}

pub(super) fn collect_files_for_repair<F>(
    datasources: &[DatasourceConfig],
    sources: &[RepairSource],
    operation_id: Uuid,
    on_file_count: F,
) -> Result<RepairIndex>
where
    F: FnMut(usize),
{
    collect_files_for_repair_with(
        datasources,
        sources,
        operation_id,
        on_file_count,
        cache_repair::scan_root,
        cache_repair::validate_receipt,
    )
}

fn collect_files_for_repair_with<F, S, V>(
    datasources: &[DatasourceConfig],
    sources: &[RepairSource],
    operation_id: Uuid,
    mut on_file_count: F,
    mut scan_root: S,
    mut validate_receipt: V,
) -> Result<RepairIndex>
where
    F: FnMut(usize),
    S: FnMut(&Path) -> Result<cache_repair::RootFiles>,
    V: FnMut(&Path, Uuid) -> Result<PathBuf>,
{
    let current_by_name: HashMap<String, &DatasourceConfig> = datasources
        .iter()
        .map(|source| (datasource_lookup_key(&source.name), source))
        .collect();
    let mut paths_by_name = HashMap::with_capacity(datasources.len());
    for source in datasources {
        let cache_path = Path::new(&source.cache_path);
        match cache_path.canonicalize() {
            Ok(path) => {
                paths_by_name.insert(datasource_lookup_key(&source.name), path);
            }
            Err(error) => {
                eprintln!(
                    "[EvictionScan] Cache root is unavailable for datasource '{}': {}",
                    source.name, error
                );
            }
        }
    }

    let mut affected_roots = HashSet::new();
    for source in sources {
        let lookup = datasource_lookup_key(&source.name);
        let current = current_by_name.get(&lookup).copied().with_context(|| {
            format!(
                "repair datasource '{}' is missing from current configuration",
                source.name
            )
        })?;
        let current_scheme = parse_scheme(&current.key_scheme).with_context(|| {
            format!(
                "current datasource '{}' has an unsupported key scheme",
                current.name
            )
        })?;
        let captured_scheme = parse_scheme(&source.key_scheme).with_context(|| {
            format!(
                "repair datasource '{}' has an unsupported key scheme",
                source.name
            )
        })?;
        if current_scheme != captured_scheme {
            bail!(
                "repair datasource '{}' key scheme changed from {} to {}",
                source.name,
                source.key_scheme,
                current.key_scheme
            );
        }

        let current_path = paths_by_name.get(&lookup).with_context(|| {
            format!(
                "repair datasource '{}' cache root is unavailable",
                source.name
            )
        })?;
        let captured_path = Path::new(&source.cache_path)
            .canonicalize()
            .with_context(|| {
                format!(
                    "repair datasource '{}' captured cache root is unavailable",
                    source.name
                )
            })?;
        if current_path != &captured_path {
            bail!(
                "repair datasource '{}' cache root changed from {} to {}",
                source.name,
                captured_path.display(),
                current_path.display()
            );
        }
        affected_roots.insert(current_path.clone());
    }

    let roots = DatasourceRoots::from_paths(datasources, &paths_by_name);
    let mut digests_by_root = HashMap::new();
    let mut trusted_empty_roots = HashSet::new();
    let mut total_files = 0usize;
    let unique_roots: HashSet<PathBuf> = paths_by_name.values().cloned().collect();

    for root in unique_roots {
        let affected = affected_roots.contains(&root);
        let files = if affected {
            scan_root(&root)?
        } else {
            match scan_root(&root) {
                Ok(files) => files,
                Err(error) => {
                    eprintln!(
                        "[EvictionScan] Cache root could not be checked at {}: {:#}",
                        root.display(),
                        error
                    );
                    continue;
                }
            }
        };

        // A root with folders that could not be read stays out of both maps, so absence under
        // it remains unverified even with a valid empty-root receipt.
        if !files.fully_checked {
            eprintln!(
                "[EvictionScan] Cache root at {} has folders that could not be read. Absence under this root will remain unverified",
                root.display()
            );
            continue;
        }

        if affected && files.is_empty() {
            match validate_receipt(&root, operation_id) {
                Ok(_) => {
                    trusted_empty_roots.insert(files.canonical_path.clone());
                }
                Err(error) => {
                    eprintln!(
                        "[EvictionScan] Cache root at {} has no trusted empty-root receipt for operation {}: {:#}. Absence under this root will remain unverified",
                        root.display(),
                        operation_id,
                        error
                    );
                }
            }
        }
        total_files += files.digests.len();
        on_file_count(total_files);
        digests_by_root.insert(files.canonical_path, files.digests);
    }

    Ok(RepairIndex {
        roots,
        files: FilesOnDisk { digests_by_root },
        affected_roots,
        trusted_empty_roots,
    })
}

fn parse_scheme(value: &str) -> Option<cache_utils::CacheKeyScheme> {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "monolithic" => Some(cache_utils::CacheKeyScheme::Monolithic),
        "bare_metal" => Some(cache_utils::CacheKeyScheme::BareMetal),
        _ => None,
    }
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

    fn repair_source(name: &str, cache_path: &Path, key_scheme: &str) -> RepairSource {
        RepairSource {
            name: name.to_string(),
            cache_path: cache_path.to_string_lossy().into_owned(),
            key_scheme: key_scheme.to_string(),
        }
    }

    fn create_cache_file(root: &Path) -> PathBuf {
        let digest = "0123456789abcdef0123456789abcdef";
        let path = root.join("ef").join("cd").join(digest);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, b"cache").unwrap();
        path
    }

    #[test]
    fn repair_receipt_allows_a_formerly_populated_empty_root() {
        let root = tempfile::tempdir().unwrap();
        let cache_file = create_cache_file(root.path());
        let operation_id = Uuid::new_v4();
        cache_repair::prepare_receipt(root.path(), Some(&operation_id.to_string())).unwrap();
        std::fs::remove_file(cache_file).unwrap();
        let datasources = [datasource("default", root.path(), "monolithic")];
        let sources = [repair_source("DEFAULT", root.path(), "monolithic")];

        let repair = collect_files_for_repair(&datasources, &sources, operation_id, |_| {})
            .expect("collect receipt-backed empty root");

        assert!(repair.files.is_empty());
        assert!(repair.intersects(Some("default"), &[]));
        assert!(repair.origins_can_verify_absence(Some("default"), &[]));
        assert!(repair.origins_are_trusted_empty(Some("default"), &[]));
    }

    #[test]
    fn a_cache_root_with_a_folder_that_could_not_be_read_abstains_and_is_named() {
        let root = tempfile::tempdir().unwrap();
        create_cache_file(root.path());
        let datasources = [datasource("default", root.path(), "monolithic")];

        // The walk visits every file it can read and reports a folder of the layout it could not.
        let (files, unchecked) = collect_files_on_disk_with(
            &datasources,
            |_| {},
            |path, visit_file| {
                cache_utils::walk_cache_root(path, visit_file);
                false
            },
        );

        assert!(files.digests_for_root(root.path()).is_none());
        assert!(files.is_empty());
        assert_eq!(unchecked, vec![root.path().to_string_lossy().into_owned()]);
    }

    #[test]
    fn a_missing_or_empty_cache_root_is_named_as_unchecked() {
        let parent = tempfile::tempdir().unwrap();
        let empty = parent.path().join("empty");
        let missing = parent.path().join("missing");
        let populated = parent.path().join("populated");
        std::fs::create_dir_all(&empty).unwrap();
        std::fs::create_dir_all(&populated).unwrap();
        create_cache_file(&populated);
        let datasources = [
            datasource("empty", &empty, "monolithic"),
            datasource("missing", &missing, "monolithic"),
            datasource("populated", &populated, "monolithic"),
        ];

        let (files, unchecked) = collect_files_on_disk(&datasources, |_| {});

        assert_eq!(files.len(), 1);
        assert_eq!(
            unchecked,
            vec![
                empty.to_string_lossy().into_owned(),
                missing.to_string_lossy().into_owned()
            ]
        );
    }

    #[cfg(unix)]
    #[test]
    fn a_linked_cache_folder_whose_target_is_gone_makes_its_root_abstain() {
        let parent = tempfile::tempdir().unwrap();
        let root = parent.path().join("cache");
        create_cache_file(&root);
        // A 2-hex cache folder linked to a disk that is not mounted: its files cannot be read, so none
        // of this root's downloads may read as evicted.
        std::os::unix::fs::symlink(parent.path().join("unmounted"), root.join("ab")).unwrap();
        let datasources = [datasource("default", &root, "monolithic")];

        let (files, unchecked) = collect_files_on_disk(&datasources, |_| {});

        assert!(files.digests_for_root(&root).is_none());
        assert_eq!(unchecked, vec![root.to_string_lossy().into_owned()]);
    }

    #[cfg(unix)]
    #[test]
    fn a_file_under_a_linked_cache_folder_is_indexed_and_a_deeper_link_is_not_followed() {
        let parent = tempfile::tempdir().unwrap();
        let linked = parent.path().join("linked");
        let other_disk = parent.path().join("other-disk");
        std::fs::create_dir_all(&linked).unwrap();
        std::fs::create_dir_all(&other_disk).unwrap();
        std::os::unix::fs::symlink(&other_disk, linked.join("ef")).unwrap();
        // Written through the link, so the file sits on the other disk.
        create_cache_file(&linked);
        let deeper = parent.path().join("deeper");
        let elsewhere = parent.path().join("elsewhere");
        std::fs::create_dir_all(deeper.join("ef")).unwrap();
        std::fs::create_dir_all(&elsewhere).unwrap();
        std::os::unix::fs::symlink(&elsewhere, deeper.join("ef").join("cd")).unwrap();
        create_cache_file(&deeper);
        let datasources = [
            datasource("linked", &linked, "monolithic"),
            datasource("deeper", &deeper, "monolithic"),
        ];

        let (files, unchecked) = collect_files_on_disk(&datasources, |_| {});

        assert_eq!(
            files.digests_for_root(&linked).map(|digests| digests.len()),
            Some(1)
        );
        // Only a 2-hex folder directly under the root is followed, so the deeper root holds no file.
        assert_eq!(
            files.digests_for_root(&deeper).map(|digests| digests.len()),
            Some(0)
        );
        assert_eq!(unchecked, vec![deeper.to_string_lossy().into_owned()]);
    }

    #[test]
    fn a_root_two_datasources_share_is_named_only_while_no_walk_of_it_succeeded() {
        let root = tempfile::tempdir().unwrap();
        create_cache_file(root.path());
        let datasources = [
            datasource("first", root.path(), "monolithic"),
            datasource("second", root.path(), "monolithic"),
        ];
        let mut walks = 0;

        // The first walk of the shared root meets a folder it cannot read; the second reads it whole.
        let (files, unchecked) = collect_files_on_disk_with(
            &datasources,
            |_| {},
            |path, visit_file| {
                walks += 1;
                walks > 1 && cache_utils::walk_cache_root(path, visit_file)
            },
        );

        assert_eq!(walks, 2);
        assert_eq!(
            files
                .digests_for_root(root.path())
                .map(|digests| digests.len()),
            Some(1)
        );
        assert!(unchecked.is_empty());
    }

    #[test]
    fn repair_empty_root_without_receipt_abstains() {
        let root = tempfile::tempdir().unwrap();
        let operation_id = Uuid::new_v4();
        let datasources = [datasource("default", root.path(), "monolithic")];
        let sources = [repair_source("default", root.path(), "monolithic")];

        let repair = collect_files_for_repair(&datasources, &sources, operation_id, |_| {})
            .expect("missing receipt must abstain");

        assert!(repair.files.is_empty());
        assert!(repair.intersects(Some("default"), &[]));
        assert!(!repair.origins_can_verify_absence(Some("default"), &[]));
        assert!(!repair.origins_are_trusted_empty(Some("default"), &[]));
    }

    #[test]
    fn rejected_receipts_abstain_without_empty_root_authority() {
        for case in [
            "missing",
            "malformed",
            "wrong-id",
            "wrong-path",
            "unsupported",
        ] {
            let root = tempfile::tempdir().unwrap();
            std::fs::create_dir_all(root.path().join("00").join("01")).unwrap();
            let operation_id = Uuid::new_v4();
            let receipt_path =
                cache_repair::receipt_path(&root.path().canonicalize().unwrap(), operation_id);
            match case {
                "missing" => {}
                "malformed" => std::fs::write(&receipt_path, b"{").unwrap(),
                "wrong-id" => std::fs::write(
                    &receipt_path,
                    serde_json::to_vec(&serde_json::json!({
                        "version": 1,
                        "operationId": Uuid::new_v4().to_string(),
                        "cachePath": root.path().canonicalize().unwrap(),
                        "hadCacheFiles": true
                    }))
                    .unwrap(),
                )
                .unwrap(),
                "wrong-path" => std::fs::write(
                    &receipt_path,
                    serde_json::to_vec(&serde_json::json!({
                        "version": 1,
                        "operationId": operation_id.to_string(),
                        "cachePath": root.path().join("other"),
                        "hadCacheFiles": true
                    }))
                    .unwrap(),
                )
                .unwrap(),
                "unsupported" => std::fs::write(
                    &receipt_path,
                    serde_json::to_vec(&serde_json::json!({
                        "version": 2,
                        "operationId": operation_id.to_string(),
                        "cachePath": root.path().canonicalize().unwrap(),
                        "hadCacheFiles": true
                    }))
                    .unwrap(),
                )
                .unwrap(),
                _ => unreachable!(),
            }
            let datasources = [datasource("default", root.path(), "monolithic")];
            let sources = [repair_source("default", root.path(), "monolithic")];

            let repair = collect_files_for_repair(&datasources, &sources, operation_id, |_| {})
                .unwrap_or_else(|error| panic!("{case} receipt must abstain: {error:#}"));

            assert!(!repair.origins_can_verify_absence(Some("default"), &[]));
            assert!(!repair.origins_are_trusted_empty(Some("default"), &[]));
        }
    }

    #[test]
    fn injected_receipt_read_denial_abstains() {
        let root = tempfile::tempdir().unwrap();
        let operation_id = Uuid::new_v4();
        let datasources = [datasource("default", root.path(), "monolithic")];
        let sources = [repair_source("default", root.path(), "monolithic")];
        let validation_calls = std::cell::Cell::new(0usize);

        let repair = collect_files_for_repair_with(
            &datasources,
            &sources,
            operation_id,
            |_| {},
            cache_repair::scan_root,
            |_, _| {
                validation_calls.set(validation_calls.get() + 1);
                anyhow::bail!("injected receipt read denial")
            },
        )
        .expect("receipt read denial must abstain");

        assert_eq!(validation_calls.get(), 1);
        assert!(!repair.origins_can_verify_absence(Some("default"), &[]));
    }

    #[test]
    fn incompletely_checked_root_abstains_even_with_a_valid_receipt() {
        for populated in [true, false] {
            let root = tempfile::tempdir().unwrap();
            let cache_file = create_cache_file(root.path());
            let operation_id = Uuid::new_v4();
            cache_repair::prepare_receipt(root.path(), Some(&operation_id.to_string())).unwrap();
            if !populated {
                std::fs::remove_file(cache_file).unwrap();
            }
            let datasources = [datasource("default", root.path(), "monolithic")];
            let sources = [repair_source("default", root.path(), "monolithic")];

            let repair = collect_files_for_repair_with(
                &datasources,
                &sources,
                operation_id,
                |_| {},
                |path| {
                    Ok(cache_repair::RootFiles {
                        fully_checked: false,
                        ..cache_repair::scan_root(path)?
                    })
                },
                cache_repair::validate_receipt,
            )
            .expect("an incompletely checked root must abstain, not fail");

            assert!(repair.files.is_empty(), "populated: {populated}");
            assert!(
                !repair.origins_can_verify_absence(Some("default"), &[]),
                "populated: {populated}"
            );
            assert!(
                !repair.origins_are_trusted_empty(Some("default"), &[]),
                "populated: {populated}"
            );
        }
    }

    #[test]
    fn strict_scan_failure_after_deletion_still_fails_repair() {
        let root = tempfile::tempdir().unwrap();
        let cache_file = create_cache_file(root.path());
        let operation_id = Uuid::new_v4();
        cache_repair::prepare_receipt(root.path(), Some(&operation_id.to_string())).unwrap();
        std::fs::remove_file(cache_file).unwrap();
        let datasources = [datasource("default", root.path(), "monolithic")];
        let sources = [repair_source("default", root.path(), "monolithic")];
        let validation_calls = std::cell::Cell::new(0usize);

        let error = collect_files_for_repair_with(
            &datasources,
            &sources,
            operation_id,
            |_| {},
            |_| anyhow::bail!("injected entry inspection failure after deletion"),
            |_, _| {
                validation_calls.set(validation_calls.get() + 1);
                anyhow::bail!("receipt validation must not run")
            },
        )
        .err()
        .expect("strict scan failure must fail repair");

        assert!(error
            .to_string()
            .contains("injected entry inspection failure after deletion"));
        assert_eq!(validation_calls.get(), 0);
    }

    #[test]
    fn nonempty_root_ignores_an_invalid_receipt_and_keeps_positive_hits() {
        let root = tempfile::tempdir().unwrap();
        create_cache_file(root.path());
        let operation_id = Uuid::new_v4();
        let receipt_path =
            cache_repair::receipt_path(&root.path().canonicalize().unwrap(), operation_id);
        std::fs::write(receipt_path, b"{").unwrap();
        let datasources = [datasource("default", root.path(), "monolithic")];
        let sources = [repair_source("default", root.path(), "monolithic")];

        let repair = collect_files_for_repair(&datasources, &sources, operation_id, |_| {})
            .expect("nonempty strict index");

        assert_eq!(repair.files.len(), 1);
        assert!(repair.origins_can_verify_absence(Some("default"), &[]));
        assert!(!repair.origins_are_trusted_empty(Some("default"), &[]));
    }

    #[test]
    fn repair_rejects_changed_scheme() {
        let root = tempfile::tempdir().unwrap();
        create_cache_file(root.path());
        let operation_id = Uuid::new_v4();
        cache_repair::prepare_receipt(root.path(), Some(&operation_id.to_string())).unwrap();
        let datasources = [datasource("default", root.path(), "bare_metal")];
        let sources = [repair_source("default", root.path(), "monolithic")];

        let error = collect_files_for_repair(&datasources, &sources, operation_id, |_| {})
            .err()
            .expect("changed scheme must fail");

        assert!(error.to_string().contains("key scheme changed"));
    }

    #[test]
    fn canonical_aliases_share_one_physical_index_and_keep_logical_schemes() {
        let root = tempfile::tempdir().unwrap();
        create_cache_file(root.path());
        let operation_id = Uuid::new_v4();
        cache_repair::prepare_receipt(root.path(), Some(&operation_id.to_string())).unwrap();
        let datasources = [
            datasource("monolithic", root.path(), "monolithic"),
            datasource("bare", root.path(), "bare_metal"),
        ];
        let sources = [repair_source("monolithic", root.path(), "monolithic")];
        let mut counts = Vec::new();
        let walks = std::cell::Cell::new(0usize);

        let repair = collect_files_for_repair_with(
            &datasources,
            &sources,
            operation_id,
            |count| counts.push(count),
            |path| {
                walks.set(walks.get() + 1);
                cache_repair::scan_root(path)
            },
            cache_repair::validate_receipt,
        )
        .unwrap();

        assert_eq!(repair.files.len(), 1);
        assert_eq!(counts, vec![1]);
        assert_eq!(walks.get(), 1);
        assert!(repair.intersects(Some("monolithic"), &[]));
        assert!(repair.intersects(Some("bare"), &[]));
        assert!(repair.origins_include_bare_metal(Some("bare"), &[]));
        assert!(!repair.origins_include_bare_metal(Some("monolithic"), &[]));
    }

    #[cfg(unix)]
    #[test]
    fn roots_that_differ_only_by_case_are_walked_separately() {
        let parent = tempfile::tempdir().unwrap();
        let lower = parent.path().join("cache");
        let upper = parent.path().join("CACHE");
        std::fs::create_dir_all(&lower).unwrap();
        std::fs::create_dir_all(&upper).unwrap();
        create_cache_file(&lower);
        create_cache_file(&upper);
        let operation_id = Uuid::new_v4();
        let datasources = [
            datasource("lower", &lower, "monolithic"),
            datasource("upper", &upper, "monolithic"),
        ];
        let sources = [
            repair_source("lower", &lower, "monolithic"),
            repair_source("upper", &upper, "monolithic"),
        ];
        let walks = std::cell::Cell::new(0usize);

        collect_files_for_repair_with(
            &datasources,
            &sources,
            operation_id,
            |_| {},
            |path| {
                walks.set(walks.get() + 1);
                cache_repair::scan_root(path)
            },
            cache_repair::validate_receipt,
        )
        .unwrap();

        assert_eq!(walks.get(), 2);
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
        let key = ProbeKey::new(
            "steam",
            "/content".to_string(),
            Some("monolithic"),
            0,
            &roots,
        );
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
        let key = ProbeKey::new(
            "steam",
            "/content".to_string(),
            Some("monolithic"),
            0,
            &roots,
        );
        let files = indexed_root(root, [1]);

        assert!(key.can_verify_absence(&files));
        assert!(keys_can_verify_absence(&[key], &files));
    }
}
