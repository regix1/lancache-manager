use std::path::{Path, PathBuf};

// Filesystem type magic numbers from statfs (Unix only)
#[cfg(unix)]
pub const NFS_SUPER_MAGIC: i64 = 0x6969;
#[cfg(unix)]
pub const NFS_V4_MAGIC: i64 = 0x6E667364; // "nfsd" in hex
#[cfg(unix)]
pub const CIFS_MAGIC_NUMBER: i64 = 0xFF534D42;
#[cfg(unix)]
pub const SMB_SUPER_MAGIC: i64 = 0x517B;
#[cfg(unix)]
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

/// Get cache size using the system's `du` command
/// This is more reliable on NFS/SMB than walking the tree with stat() calls
/// because the NFS server may cache directory information more efficiently
#[cfg(unix)]
#[allow(dead_code)]
pub fn get_size_via_du(path: &Path) -> Option<u64> {
    use std::process::Command;

    let output = Command::new("du")
        .arg("-sb") // summarize, bytes
        .arg(path)
        .output()
        .ok()?;

    if !output.status.success() {
        return None;
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    // Output format: "12345678\t/path/to/dir"
    stdout
        .split_whitespace()
        .next()
        .and_then(|s| s.parse::<u64>().ok())
}

#[cfg(not(unix))]
#[allow(dead_code)]
pub fn get_size_via_du(_path: &Path) -> Option<u64> {
    None
}

/// Get file count using the system's `find` command
/// More reliable on NFS than walking with readdir
#[cfg(unix)]
#[allow(dead_code)]
pub fn get_file_count_via_find(path: &Path) -> Option<u64> {
    use std::process::Command;

    let output = Command::new("find")
        .arg(path)
        .arg("-type")
        .arg("f")
        .arg("-printf")
        .arg(".")
        .output()
        .ok()?;

    if !output.status.success() {
        return None;
    }

    Some(output.stdout.len() as u64)
}

#[cfg(not(unix))]
#[allow(dead_code)]
pub fn get_file_count_via_find(_path: &Path) -> Option<u64> {
    None
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
