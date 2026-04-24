use std::io;
use std::path::{Path, PathBuf};

#[allow(dead_code)]
pub const DEFAULT_SLICE_SIZE: u64 = 1_048_576;

#[allow(dead_code)]
pub const DEFAULT_MAX_CHUNKS: usize = 100;

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
pub const NFS_SUPER_MAGIC: i64 = 0x6969;
#[cfg(unix)]
#[allow(dead_code)]
pub const NFS_V4_MAGIC: i64 = 0x6E667364; // "nfsd" in hex
#[cfg(unix)]
#[allow(dead_code)]
pub const CIFS_MAGIC_NUMBER: i64 = 0xFF534D42;
#[cfg(unix)]
#[allow(dead_code)]
pub const SMB_SUPER_MAGIC: i64 = 0x517B;
#[cfg(unix)]
#[allow(dead_code)]
pub const SMB2_MAGIC_NUMBER: i64 = 0xFE534D42;

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

#[allow(dead_code)]
pub fn normalize_service_name(service: &str) -> String {
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

#[allow(dead_code)]
pub fn cache_hash_candidates_for_probe(service: &str, url: &str, max_chunks: usize) -> Vec<String> {
    let service = normalize_service_name(service);
    let mut hashes = Vec::with_capacity(max_chunks + 1);
    hashes.push(calculate_md5(&format!("{}{}", service, url)));

    for (start, end) in chunk_ranges_for_probe(max_chunks) {
        hashes.push(calculate_md5(&format!("{}{}bytes={}-{}", service, url, start, end)));
    }

    hashes
}

#[allow(dead_code)]
pub fn cache_path_candidates_for_probe(
    cache_dir: &Path,
    service: &str,
    url: &str,
    max_chunks: usize,
) -> Vec<PathBuf> {
    let service = normalize_service_name(service);
    let mut paths = Vec::with_capacity(max_chunks + 1);
    paths.push(calculate_cache_path_no_range(cache_dir, &service, url));

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
    let service = normalize_service_name(service);
    let chunk_ranges = chunk_ranges_for_total_bytes(total_bytes);
    let mut paths = Vec::with_capacity(chunk_ranges.len() + 1);
    paths.push(calculate_cache_path_no_range(cache_dir, &service, url));

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
