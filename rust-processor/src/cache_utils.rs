use std::collections::HashSet;
use std::io;
use std::path::{Path, PathBuf};

#[allow(dead_code)]
pub const DEFAULT_SLICE_SIZE: u64 = 1_048_576;

// Detect + evict probe the 1 MiB slices of each (service, url). The chunk count is now sized
// from the URL's observed byte size (`MAX(LogEntries.BytesServed)`) via `probe_chunks_for_bytes`,
// clamped to [`DEFAULT_MAX_CHUNKS`, `MAX_PROBE_CHUNKS`]. `DEFAULT_MAX_CHUNKS` is the unconditional
// FLOOR — sizing can only ever probe MORE slices than the old flat behavior, never fewer — and
// `MAX_PROBE_CHUNKS` is the perf CEILING that keeps the all-miss cost bounded (see below).
#[allow(dead_code)]
pub const DEFAULT_MAX_CHUNKS: usize = 100;

// Upper bound on probe chunk count when sizing from a URL's byte size.
//
// WHY A CAP IS REQUIRED: the eviction scan runs over ALL inactive downloads every 6h, and the
// all-absent (true-miss) case CANNOT early-exit — `has_cache_file` / the path probe must check
// every candidate before concluding "gone". Sizing chunk count uncapped from per-URL bytes would
// let a single large URL (e.g. a 100 GB session sum mis-attributed to one URL) explode to ~100k
// md5/stat candidates, multiplied across every unique (service, url) that genuinely misses.
//
// WHY 4096: DEFAULT_SLICE_SIZE is 1 MiB, so 4096 chunks == 4 GiB worth of slices. A single cached
// lancache OBJECT (one Steam/Epic/Blizzard chunk file, one WSUS .psf range) is realistically far
// smaller than 4 GiB — they are typically 1 MiB..256 MiB — so 4 GiB covers any realistic single
// sliced object with large margin while never undercounting. Worst-case all-miss cost per unique
// key is bounded at 4097 candidates (vs today's 101), each unique key is probed exactly once
// scan-wide (memoized), and the present case still short-circuits on the first hit.
#[allow(dead_code)]
pub const MAX_PROBE_CHUNKS: usize = 4096;

/// Derives the probe chunk count for a URL from its observed byte size, clamped to
/// `[DEFAULT_MAX_CHUNKS, MAX_PROBE_CHUNKS]`.
///
/// - `bytes <= 0` (unknown/0 size) → `DEFAULT_MAX_CHUNKS` (the old floor; never less coverage).
/// - small object → `DEFAULT_MAX_CHUNKS` (floor keeps the old 100-slice safety net).
/// - large object → `ceil(bytes / DEFAULT_SLICE_SIZE)` capped at `MAX_PROBE_CHUNKS`.
///
/// Sizing this way lets detect/evict find cached slices that fall past the first 100 MiB of a
/// large object, fixing the Phase-3 false-miss, while the cap bounds the all-miss scan cost.
#[allow(dead_code)]
pub fn probe_chunks_for_bytes(bytes: i64) -> usize {
    if bytes <= 0 {
        return DEFAULT_MAX_CHUNKS;
    }
    // ceil(bytes / slice) — number of 1 MiB slices needed to cover `bytes`.
    let needed = ((bytes as u64).div_ceil(DEFAULT_SLICE_SIZE)) as usize;
    needed.clamp(DEFAULT_MAX_CHUNKS, MAX_PROBE_CHUNKS)
}

/// Returns the canonical form of `candidate`, but only if it resides under `root` and is not a symlink.
/// Errors out for symlinks (refuses to follow), paths outside the root, or non-existent paths.
///
/// This is the canonical guard used before any `fs::remove_file`, `fs::remove_dir`, or
/// `fs::remove_dir_all` on URL-derived or admin-controlled paths.
#[allow(dead_code)]
pub fn safe_path_under_root(root: &Path, candidate: &Path) -> io::Result<PathBuf> {
    let meta = candidate.symlink_metadata()?;
    if meta.file_type().is_symlink() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("symlink not allowed: {}", candidate.display()),
        ));
    }
    let canonical = candidate.canonicalize()?;
    let canonical_root = root.canonicalize()?;
    if !canonical.starts_with(&canonical_root) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("path escapes root: {}", candidate.display()),
        ));
    }
    Ok(canonical)
}

// Filesystem type magic numbers from statfs (Unix only)
#[cfg(unix)]
#[allow(dead_code)]
const NFS_SUPER_MAGIC: i64 = 0x6969;
#[cfg(unix)]
#[allow(dead_code)]
const NFS_V4_MAGIC: i64 = 0x6E667364; // "nfsd" in hex
#[cfg(unix)]
#[allow(dead_code)]
const CIFS_MAGIC_NUMBER: i64 = 0xFF534D42;
#[cfg(unix)]
#[allow(dead_code)]
const SMB_SUPER_MAGIC: i64 = 0x517B;
#[cfg(unix)]
#[allow(dead_code)]
const SMB2_MAGIC_NUMBER: i64 = 0xFE534D42;

/// Filesystem type enumeration for optimizing operations
/// Used on Linux to detect NFS/SMB and adjust parallelism accordingly
#[derive(Debug, Clone, Copy, PartialEq)]
#[allow(dead_code)]
pub enum FilesystemType {
    /// Local filesystem (ext4, xfs, btrfs, etc.)
    Local,
    /// NFS network mount
    Nfs,
    /// SMB/CIFS network mount
    Smb,
    /// Unknown filesystem type
    Unknown,
}

#[allow(dead_code)]
impl FilesystemType {
    /// Check if this is a network filesystem (NFS, SMB, CIFS)
    pub fn is_network(&self) -> bool {
        matches!(self, FilesystemType::Nfs | FilesystemType::Smb)
    }

    /// Get recommended parallelism level for this filesystem type
    /// Network filesystems perform poorly with high parallelism
    pub fn recommended_parallelism(&self) -> usize {
        match self {
            FilesystemType::Local => {
                // Local SSDs can handle high parallelism
                let cpus = std::thread::available_parallelism()
                    .map(|p| p.get())
                    .unwrap_or(4);
                std::cmp::min(cpus * 2, 16)
            }
            FilesystemType::Nfs | FilesystemType::Smb => {
                // Network filesystems: parallelism often HURTS performance
                // Each operation requires network round-trip
                // Use minimal parallelism to reduce NFS server load
                2
            }
            FilesystemType::Unknown => 4,
        }
    }
}

/// Detect the filesystem type for a given path
/// Uses statfs on Unix systems to check the filesystem magic number
#[cfg(unix)]
#[allow(dead_code)]
pub fn detect_filesystem_type(path: &Path) -> FilesystemType {
    use std::ffi::CString;
    use std::mem::MaybeUninit;
    use std::os::unix::ffi::OsStrExt;

    let c_path = match CString::new(path.as_os_str().as_bytes()) {
        Ok(p) => p,
        Err(_) => return FilesystemType::Unknown,
    };

    let mut stat: MaybeUninit<libc::statfs> = MaybeUninit::uninit();
    let res = unsafe { libc::statfs(c_path.as_ptr(), stat.as_mut_ptr()) };

    if res != 0 {
        return FilesystemType::Unknown;
    }

    let stat = unsafe { stat.assume_init() };
    let fs_type = stat.f_type as i64;

    match fs_type {
        x if x == NFS_SUPER_MAGIC || x == NFS_V4_MAGIC => FilesystemType::Nfs,
        x if x == CIFS_MAGIC_NUMBER || x == SMB_SUPER_MAGIC || x == SMB2_MAGIC_NUMBER => {
            FilesystemType::Smb
        }
        _ => FilesystemType::Local,
    }
}

/// Fallback for non-Unix systems - always returns Local
#[cfg(not(unix))]
#[allow(dead_code)]
pub fn detect_filesystem_type(_path: &Path) -> FilesystemType {
    // On Windows, network paths are typically detected via path prefix
    // For now, assume local
    FilesystemType::Local
}

/// Calculate MD5 hash for a cache key
/// Used for deriving cache file paths from service + URL combinations
pub fn calculate_md5(cache_key: &str) -> String {
    format!("{:x}", md5::compute(cache_key.as_bytes()))
}

/// Reproduce nginx's `$uri` from a stored `LogEntries.Url`.
///
/// The lancache monolithic image keys the cache on `$cacheidentifier$uri$slice_range`
/// (`cache.conf.d/root/30_cache_key.conf:1`), where `$uri` is nginx's NORMALIZED, URL-DECODED
/// request path — NOT `$request_uri`. The access log records the raw `$request` line, so the
/// parser stores the URL with the query string retained and percent-escapes as-logged. To match
/// the on-disk md5 filename we must transform the stored URL into nginx's `$uri` before hashing:
///
///   (a) DROP everything from the first `?` (query string — nginx keys on `$uri`, not `$request_uri`).
///   (b) PERCENT-DECODE `%XX` escapes (nginx decodes `$uri`).
///   (c) COLLAPSE consecutive `//` to `/` (nginx default `merge_slashes on`).
///   (d) RESOLVE `.` / `..` path segments.
///
/// This is the shared key-derivation chokepoint, applied identically by detect, evict, all three
/// removers and both corruption binaries. A wrong transform here breaks all of them together, so
/// the transforms are deliberately minimal and exactly mirror nginx ground truth.
///
/// Borrowed fast path: if the URL has no `?`, no `%`, no `//`, and no `.`-only / `..` segment, the
/// stored URL already equals `$uri`, so we return `Cow::Borrowed` with zero allocation. This bounds
/// the blast radius to only previously-divergent URLs and keeps the hot rayon'd probe loop cheap.
pub fn nginx_cache_uri(url: &str) -> std::borrow::Cow<'_, str> {
    // (a) Strip the query string first; only the path participates in `$uri`.
    let path = match url.find('?') {
        Some(idx) => &url[..idx],
        None => url,
    };

    // Fast path: a clean, already-`$uri`-shaped path. No query was present (path == url), no
    // percent-escape, no `//` run, and no `.`/`..` segment that would resolve away. Return the
    // original borrowed slice unchanged — zero allocation in the hot loop.
    if std::ptr::eq(path, url) && !needs_uri_normalization(path) {
        return std::borrow::Cow::Borrowed(url);
    }

    // (b) Percent-decode, then (c) collapse `//`, then (d) resolve dot-segments.
    let decoded = percent_decode(path);
    let collapsed = collapse_double_slashes(&decoded);
    let resolved = resolve_dot_segments(&collapsed);
    std::borrow::Cow::Owned(resolved)
}

/// True if `path` contains anything that `nginx_cache_uri` would rewrite: a `%` escape, a `//`
/// run, or a `.`/`..` path segment. Used to decide whether the borrowed fast path applies.
fn needs_uri_normalization(path: &str) -> bool {
    let bytes = path.as_bytes();
    if bytes.contains(&b'%') {
        return true;
    }
    if bytes.windows(2).any(|pair| pair == b"//") {
        return true;
    }
    // A `.`/`..` segment is one bounded by `/` (or string edges) consisting only of dots.
    path.split('/').any(|seg| seg == "." || seg == "..")
}

/// Decode `%XX` percent-escapes the way nginx decodes `$uri`. Lone or malformed `%` sequences are
/// left verbatim (nginx is lenient here and so are we). Self-contained: no extra crate dependency.
fn percent_decode(input: &str) -> String {
    let bytes = input.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            let hi = (bytes[i + 1] as char).to_digit(16);
            let lo = (bytes[i + 2] as char).to_digit(16);
            if let (Some(hi), Some(lo)) = (hi, lo) {
                out.push((hi * 16 + lo) as u8);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    // The decoded bytes form a path; cache keys are hashed as bytes so lossy UTF-8 is harmless,
    // and lancache paths are ASCII in practice.
    String::from_utf8_lossy(&out).into_owned()
}

/// Collapse runs of consecutive `/` to a single `/` (nginx `merge_slashes on`). Idempotent: an
/// already-collapsed path is returned with the same content.
fn collapse_double_slashes(input: &str) -> String {
    if !input.as_bytes().windows(2).any(|pair| pair == b"//") {
        return input.to_string();
    }
    let mut out = String::with_capacity(input.len());
    let mut prev_slash = false;
    for ch in input.chars() {
        if ch == '/' {
            if !prev_slash {
                out.push(ch);
            }
            prev_slash = true;
        } else {
            out.push(ch);
            prev_slash = false;
        }
    }
    out
}

/// Resolve `.` and `..` path segments the way nginx normalizes `$uri`. Operates on a
/// `/`-collapsed path. A leading `/` is preserved; a `..` cannot escape above the root.
fn resolve_dot_segments(input: &str) -> String {
    if !input.split('/').any(|seg| seg == "." || seg == "..") {
        return input.to_string();
    }

    let leading_slash = input.starts_with('/');
    let trailing_slash = input.len() > 1 && input.ends_with('/');

    let mut stack: Vec<&str> = Vec::new();
    for seg in input.split('/') {
        match seg {
            "" | "." => {} // empty (from leading/trailing/collapsed) and `.` are dropped
            ".." => {
                stack.pop();
            }
            other => stack.push(other),
        }
    }

    let mut out = String::with_capacity(input.len());
    if leading_slash {
        out.push('/');
    }
    out.push_str(&stack.join("/"));
    if trailing_slash && !out.ends_with('/') {
        out.push('/');
    }
    out
}

/// Calculate cache file path with byte range using lancache's MD5 structure:
/// /cache/{last_2_chars}/{2_chars_before_that}/{full_hash}
///
/// Cache key format: "{service}{url}bytes={start}-{end}"
///
/// Note: This function is used by multiple binaries but not all, hence the allow(dead_code)
#[allow(dead_code)]
pub fn calculate_cache_path(
    cache_dir: &Path,
    service: &str,
    url: &str,
    start: u64,
    end: u64,
) -> PathBuf {
    // Transform the stored URL into nginx's `$uri` so the md5 matches the on-disk filename.
    let url = nginx_cache_uri(url);
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

/// Calculate cache file path without byte range
/// Lancache nginx cache key format: $cacheidentifier$uri (NO slice_range!)
///
/// Cache key format: "{service}{url}"
///
/// Note: This function is used by multiple binaries but not all, hence the allow(dead_code)
#[allow(dead_code)]
pub fn calculate_cache_path_no_range(
    cache_dir: &Path,
    service: &str,
    url: &str,
) -> PathBuf {
    // Transform the stored URL into nginx's `$uri` so the md5 matches the on-disk filename.
    let url = nginx_cache_uri(url);
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

/// Calculate cache file path for the `@noslice` location.
/// Lancache nginx cache key format: `$cacheidentifier$uri::noslice` (literal `::noslice` suffix,
/// NO byte range — `15_noslice.conf:51`). Mirrors `calculate_cache_path_no_range` exactly except
/// for the trailing `::noslice`, so the produced filename matches the no-range/ranged forms in shape.
///
/// Note: This function is used by multiple binaries but not all, hence the allow(dead_code)
#[allow(dead_code)]
pub fn calculate_cache_path_noslice(
    cache_dir: &Path,
    service: &str,
    url: &str,
) -> PathBuf {
    // Transform the stored URL into nginx's `$uri` so the md5 matches the on-disk filename.
    let url = nginx_cache_uri(url);
    let cache_key = format!("{}{}::noslice", service, url);
    let hash = calculate_md5(&cache_key);

    let len = hash.len();
    if len < 4 {
        return cache_dir.join(&hash);
    }

    let last_2 = &hash[len - 2..];
    let middle_2 = &hash[len - 4..len - 2];

    cache_dir.join(last_2).join(middle_2).join(&hash)
}

/// Lowercase service name for cache-path MD5 hashing.
///
/// Feeds MD5 cache-path hashing and must NOT canonicalize IPs/localhost.
/// This is deliberately different from `service_utils::normalize_service_name`,
/// which canonicalizes IPs and localhost for log parsing. Do not merge these.
#[allow(dead_code)]
pub fn service_name_lowercase(service: &str) -> String {
    service.to_lowercase()
}

#[allow(dead_code)]
pub fn sorted_sample_urls<I, S>(urls: I, limit: usize) -> Vec<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut sample_urls: Vec<String> = urls
        .into_iter()
        .map(|url| url.as_ref().to_string())
        .collect();
    sample_urls.sort();
    sample_urls.dedup();
    sample_urls.truncate(limit);
    sample_urls
}

/// Lazily yields the md5-hex cache-key hashes probed for (service, url) — exactly the
/// file-name components of `cache_path_candidates_for_probe`, in the same order, without
/// constructing any paths. Lazy so callers doing set-membership checks can early-exit
/// on the first hit instead of always hashing all `max_chunks + 2` candidates.
///
/// Order: `[no_range, noslice, chunk0, chunk1, ...]`. The `::noslice` candidate matches the
/// lancache `@noslice` location's `$cacheidentifier$uri::noslice` key (literal suffix, no byte
/// range) so the probe also finds noslice-cached content.
#[allow(dead_code)]
pub fn cache_hash_candidates_iter(
    service: &str,
    url: &str,
    max_chunks: usize,
) -> impl Iterator<Item = String> {
    let service = service_name_lowercase(service);
    // Transform the stored URL into nginx's `$uri` once so every candidate md5 (no-range +
    // noslice + ranged) matches the on-disk filename. Owned because the ranged closure outlives `url`.
    let url = nginx_cache_uri(url).into_owned();
    let no_range_hash = calculate_md5(&format!("{}{}", service, url));
    let noslice_hash = calculate_md5(&format!("{}{}::noslice", service, url));

    std::iter::once(no_range_hash).chain(std::iter::once(noslice_hash)).chain(
        chunk_ranges_for_probe(max_chunks).into_iter().map(move |(start, end)| {
            calculate_md5(&format!("{}{}bytes={}-{}", service, url, start, end))
        }),
    )
}

#[allow(dead_code)]
pub fn cache_hash_candidates_for_probe(service: &str, url: &str, max_chunks: usize) -> Vec<String> {
    cache_hash_candidates_iter(service, url, max_chunks).collect()
}

#[allow(dead_code)]
pub fn cache_path_candidates_for_probe(
    cache_dir: &Path,
    service: &str,
    url: &str,
    max_chunks: usize,
) -> Vec<PathBuf> {
    let service = service_name_lowercase(service);
    let mut paths = Vec::with_capacity(max_chunks + 2);
    paths.push(calculate_cache_path_no_range(cache_dir, &service, url));
    paths.push(calculate_cache_path_noslice(cache_dir, &service, url));

    for (start, end) in chunk_ranges_for_probe(max_chunks) {
        paths.push(calculate_cache_path(cache_dir, &service, url, start, end));
    }

    paths
}

#[allow(dead_code)]
pub fn cache_path_candidates_for_bytes(
    cache_dir: &Path,
    service: &str,
    url: &str,
    total_bytes: i64,
) -> Vec<PathBuf> {
    let service = service_name_lowercase(service);
    let chunk_ranges = chunk_ranges_for_total_bytes(total_bytes);
    let mut paths = Vec::with_capacity(chunk_ranges.len() + 2);
    paths.push(calculate_cache_path_no_range(cache_dir, &service, url));
    paths.push(calculate_cache_path_noslice(cache_dir, &service, url));

    for (start, end) in chunk_ranges {
        paths.push(calculate_cache_path(cache_dir, &service, url, start, end));
    }

    paths
}

fn chunk_ranges_for_probe(max_chunks: usize) -> Vec<(u64, u64)> {
    (0..max_chunks)
        .map(|chunk| {
            let start = chunk as u64 * DEFAULT_SLICE_SIZE;
            (start, chunk_end(start))
        })
        .collect()
}

fn chunk_ranges_for_total_bytes(total_bytes: i64) -> Vec<(u64, u64)> {
    if total_bytes <= 0 {
        return vec![(0, chunk_end(0))];
    }

    let mut ranges = Vec::new();
    let mut start = 0u64;
    let total_bytes = total_bytes as u64;
    while start < total_bytes {
        ranges.push((start, chunk_end(start)));
        start += DEFAULT_SLICE_SIZE;
    }

    ranges
}

fn chunk_end(start: u64) -> u64 {
    start + DEFAULT_SLICE_SIZE - 1
}

/// Removes empty directories from the given set, deepest-first, also pruning empty parents
/// one level up (stopping at `cache_dir`). Uses `safe_path_under_root` as a guard before
/// any removal. Returns the count of directories successfully removed.
#[allow(dead_code)]
pub(crate) fn cleanup_empty_directories(cache_dir: &Path, dirs_to_check: HashSet<PathBuf>) -> usize {
    let mut removed_count = 0;

    let mut sorted_dirs: Vec<PathBuf> = dirs_to_check.into_iter().collect();
    sorted_dirs.sort_by(|a, b| b.components().count().cmp(&a.components().count()));

    for dir in sorted_dirs {
        // Canonical-under-root guard: refuses symlinks, paths outside root.
        if let Err(e) = safe_path_under_root(cache_dir, &dir) {
            eprintln!("  skipping unsafe dir {}: {}", dir.display(), e);
            continue;
        }

        if let Ok(entries) = std::fs::read_dir(&dir) {
            if entries.count() == 0 {
                if std::fs::remove_dir(&dir).is_ok() {
                    removed_count += 1;

                    if let Some(parent) = dir.parent() {
                        if parent != cache_dir {
                            match safe_path_under_root(cache_dir, parent) {
                                Ok(_) => {
                                    if let Ok(parent_entries) = std::fs::read_dir(parent) {
                                        if parent_entries.count() == 0 {
                                            std::fs::remove_dir(parent).ok();
                                            removed_count += 1;
                                        }
                                    }
                                }
                                Err(e) => {
                                    eprintln!("  skipping unsafe parent {}: {}", parent.display(), e);
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    /// Equivalence guarantee for the filename-keyed eviction probe: the hash candidates
    /// produced by `cache_hash_candidates_for_probe` must equal, element-for-element, the
    /// file_name components of the path candidates from `cache_path_candidates_for_probe`.
    #[test]
    fn hash_candidates_match_probe_path_file_names() {
        let cases: [(&str, &str); 4] = [
            ("steam", "/depot/881100/chunk/9b5af6c1d3e8a2f4b7c0d9e8f1a2b3c4d5e6f708"),
            ("Epic", "/Builds/Org/CloudDir/ChunksV4/12/ABCD_0123456789abcdef.chunk"),
            ("blizzard", "/tpr/bnt001/data/05/d6/05d6a7b8c9d0e1f2a3b4c5d6e7f8a9b0"),
            // The `?range=1` query is now stripped by nginx_cache_uri (nginx keys on `$uri`,
            // not `$request_uri`). Both the hash form and the path form apply the SAME transform,
            // so this test still proves internal consistency — only the absolute md5 changed (to
            // the nginx-correct, query-stripped value).
            ("wsus", "/c/upgr/2021/01/windows10-kb5000802-x64_abc123.psf?range=1"),
        ];
        let cache_dir = Path::new("/cache");

        for (service, url) in cases {
            let paths = cache_path_candidates_for_probe(cache_dir, service, url, DEFAULT_MAX_CHUNKS);
            let hashes = cache_hash_candidates_for_probe(service, url, DEFAULT_MAX_CHUNKS);

            assert_eq!(
                paths.len(),
                hashes.len(),
                "candidate count diverged for ({}, {})",
                service,
                url
            );

            for (index, (path, hash)) in paths.iter().zip(hashes.iter()).enumerate() {
                let file_name = path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .expect("probe path candidate must end in a UTF-8 file name");
                assert_eq!(
                    file_name, hash,
                    "candidate {} diverged for ({}, {})",
                    index, service, url
                );
            }
        }
    }

    /// The on-disk filename index relies on `calculate_md5` emitting 32-char lowercase hex.
    #[test]
    fn calculate_md5_is_lowercase_hex() {
        let hash = calculate_md5("steam/depot/881100/chunk/ABCDEF");
        assert_eq!(hash.len(), 32);
        assert!(
            hash.chars()
                .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()),
            "md5 hex must be lowercase: {}",
            hash
        );
    }

    /// The probe-list iterator and the eager Vec form must agree (the scan probes via the
    /// lazy iterator while the equivalence test above exercises the Vec form).
    #[test]
    fn hash_candidates_iter_matches_vec_form() {
        let collected: Vec<String> =
            cache_hash_candidates_iter("steam", "/depot/1/chunk/aa", DEFAULT_MAX_CHUNKS).collect();
        let eager = cache_hash_candidates_for_probe("steam", "/depot/1/chunk/aa", DEFAULT_MAX_CHUNKS);
        assert_eq!(collected, eager);
        // no_range + noslice + MAX_CHUNKS ranged slices.
        assert_eq!(collected.len(), DEFAULT_MAX_CHUNKS + 2);
    }

    /// The probe must include the `@noslice` location's key:
    /// `md5(lower(service) + nginx_cache_uri(url) + "::noslice")` — with NO byte range — so
    /// noslice-cached content is matched by detect/evict/remove. Pinned at the second position
    /// (immediately after the no-range candidate, before the chunk slices).
    #[test]
    fn hash_candidates_contains_noslice_key() {
        let service = "epicgames";
        let url = "/Builds/Org/CloudDir/ChunksV4/12/ABCD_0123456789abcdef.chunk";

        let expected_noslice = calculate_md5(&format!(
            "{}{}::noslice",
            service_name_lowercase(service),
            nginx_cache_uri(url)
        ));

        let hashes = cache_hash_candidates_for_probe(service, url, DEFAULT_MAX_CHUNKS);
        assert!(
            hashes.contains(&expected_noslice),
            "probe candidates must contain the ::noslice key {} for ({}, {})",
            expected_noslice,
            service,
            url
        );
        // Order contract: [no_range, noslice, chunk0, ...] — noslice is the second candidate.
        let expected_no_range = calculate_md5(&format!(
            "{}{}",
            service_name_lowercase(service),
            nginx_cache_uri(url)
        ));
        assert_eq!(hashes[0], expected_no_range, "first candidate must be no_range");
        assert_eq!(hashes[1], expected_noslice, "second candidate must be ::noslice");
    }

    /// `probe_chunks_for_bytes` sizes the probe chunk count from the URL's byte size, never
    /// below the `DEFAULT_MAX_CHUNKS` floor and never above the `MAX_PROBE_CHUNKS` ceiling.
    #[test]
    fn probe_chunks_for_bytes_clamps_to_floor_and_ceiling() {
        // 0 / unknown / negative size → the old flat floor (never less coverage than before).
        assert_eq!(probe_chunks_for_bytes(0), DEFAULT_MAX_CHUNKS);
        assert_eq!(probe_chunks_for_bytes(-1), DEFAULT_MAX_CHUNKS);

        // A small object (well under the floor's coverage) still probes the full floor.
        assert_eq!(probe_chunks_for_bytes(1), DEFAULT_MAX_CHUNKS);
        assert_eq!(
            probe_chunks_for_bytes(DEFAULT_SLICE_SIZE as i64),
            DEFAULT_MAX_CHUNKS
        );
        // Exactly at the floor boundary: 100 MiB → 100 slices == floor.
        assert_eq!(
            probe_chunks_for_bytes(DEFAULT_MAX_CHUNKS as i64 * DEFAULT_SLICE_SIZE as i64),
            DEFAULT_MAX_CHUNKS
        );

        // A large object (between floor and ceiling) → ceil(bytes / slice), exceeding the
        // old flat 100. 1 GiB = 1024 MiB → 1024 slices.
        let one_gib = 1024i64 * DEFAULT_SLICE_SIZE as i64;
        assert_eq!(probe_chunks_for_bytes(one_gib), 1024);
        assert!(probe_chunks_for_bytes(one_gib) > DEFAULT_MAX_CHUNKS);

        // ceil rounding: floor + half a slice rounds UP to floor + 1.
        let just_over_floor =
            DEFAULT_MAX_CHUNKS as i64 * DEFAULT_SLICE_SIZE as i64 + DEFAULT_SLICE_SIZE as i64 / 2;
        assert_eq!(probe_chunks_for_bytes(just_over_floor), DEFAULT_MAX_CHUNKS + 1);

        // A huge (e.g. mis-attributed 100 GB) size is capped at MAX_PROBE_CHUNKS so the
        // all-miss eviction scan stays bounded.
        let one_hundred_gb = 100i64 * 1024 * DEFAULT_SLICE_SIZE as i64;
        assert_eq!(probe_chunks_for_bytes(one_hundred_gb), MAX_PROBE_CHUNKS);
        // Exactly at the ceiling boundary stays at the ceiling.
        assert_eq!(
            probe_chunks_for_bytes(MAX_PROBE_CHUNKS as i64 * DEFAULT_SLICE_SIZE as i64),
            MAX_PROBE_CHUNKS
        );
    }

    use std::borrow::Cow;

    #[test]
    fn nginx_cache_uri_strips_query_string() {
        assert_eq!(nginx_cache_uri("/a/b?x=1"), "/a/b");
        assert_eq!(nginx_cache_uri("/a/b?x=1&y=2"), "/a/b");
        // A bare trailing `?` yields the empty query → path only.
        assert_eq!(nginx_cache_uri("/a/b?"), "/a/b");
    }

    #[test]
    fn nginx_cache_uri_percent_decodes() {
        assert_eq!(nginx_cache_uri("/a%20b"), "/a b"); // %20 -> space
        assert_eq!(nginx_cache_uri("/a%2Fb"), "/a/b"); // %2F -> '/'
        assert_eq!(nginx_cache_uri("/a%2fb"), "/a/b"); // lowercase hex
        // Query stripped THEN decoded: the decode operates on the path only.
        assert_eq!(nginx_cache_uri("/a%20b?token=%2F"), "/a b");
    }

    #[test]
    fn nginx_cache_uri_collapses_double_slashes_idempotent() {
        assert_eq!(nginx_cache_uri("/a//b"), "/a/b");
        assert_eq!(nginx_cache_uri("/a///b//c"), "/a/b/c");
        // Already-clean path is unchanged (idempotent collapse).
        assert_eq!(nginx_cache_uri("/a/b/c"), "/a/b/c");
    }

    #[test]
    fn nginx_cache_uri_resolves_dot_segments() {
        assert_eq!(nginx_cache_uri("/a/./b"), "/a/b");
        assert_eq!(nginx_cache_uri("/a/b/../c"), "/a/c");
        assert_eq!(nginx_cache_uri("/a/b/../../c"), "/c");
        // `..` cannot escape above the root.
        assert_eq!(nginx_cache_uri("/../a"), "/a");
    }

    #[test]
    fn nginx_cache_uri_borrowed_fast_path_for_clean_url() {
        // A clean depot path needs no transform → returned BORROWED (zero allocation in the hot
        // rayon'd probe loop). This is the safety mechanism that bounds the fix's blast radius.
        let clean = "/depot/881100/chunk/9b5af6c1d3e8a2f4b7c0d9e8f1a2b3c4d5e6f708";
        assert!(matches!(nginx_cache_uri(clean), Cow::Borrowed(_)));
        // A URL that needs ANY transform is Owned.
        assert!(matches!(nginx_cache_uri("/a/b?x=1"), Cow::Owned(_)));
        assert!(matches!(nginx_cache_uri("/a//b"), Cow::Owned(_)));
        assert!(matches!(nginx_cache_uri("/a%20b"), Cow::Owned(_)));
        assert!(matches!(nginx_cache_uri("/a/./b"), Cow::Owned(_)));
    }

    /// REAL-SAMPLE REGRESSION GATE — pins the nginx-correct md5 key against a known on-disk file.
    ///
    /// This is `#[ignore]`'d so CI stays green until a real sample is pasted in. To enable it:
    ///
    ///   1. On the server, run `rust-processor/scripts/verify_cache_key.py --sample-evicted 25`
    ///      (or pass an explicit `--service`/`--url`). It prints, per (service, url), which URL
    ///      variant produced an md5 whose file EXISTS under /data/cache/cache. The WINNING variant
    ///      is the confirmed nginx derivation, and the printed `md5` is the on-disk filename.
    ///   2. Replace the three placeholders below with that real (service, url, expected_md5).
    ///   3. Drop the `#[ignore]` (or run `cargo test -- --ignored`) to assert the SHIPPED
    ///      derivation reproduces the real on-disk filename.
    ///
    /// Do NOT fabricate a hash — a fabricated value would make this a tautology and defeat the
    /// purpose of pinning against ground truth.
    #[test]
    #[ignore = "fill in a real (service, url, md5) captured by scripts/verify_cache_key.py first"]
    fn nginx_key_matches_real_on_disk_file() {
        // ---- PASTE REAL SAMPLE HERE (from verify_cache_key.py) ----
        let service: &str = "REPLACE_ME_service"; // e.g. "steam"
        let url: &str = "REPLACE_ME_url"; // e.g. "/depot/123/chunk/abc?token=xyz"
        let expected_md5: &str = "REPLACE_ME_32_char_md5"; // the on-disk filename the script found
        // -----------------------------------------------------------

        // Reproduce nginx's key the SHIPPED way: lower(service) + $uri + slice range, then md5.
        let uri = nginx_cache_uri(url);
        let svc = service_name_lowercase(service);

        // Try the no-range key and the first-slice key; one of them must match the on-disk file.
        let no_range = calculate_md5(&format!("{}{}", svc, uri));
        let first_slice = calculate_md5(&format!("{}{}bytes=0-1048575", svc, uri));

        assert!(
            no_range == expected_md5 || first_slice == expected_md5,
            "nginx-correct key did not reproduce the on-disk filename {} (got no_range={}, first_slice={}) for service={} url={}",
            expected_md5,
            no_range,
            first_slice,
            service,
            url
        );
    }
}
