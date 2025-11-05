use std::path::{Path, PathBuf};

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
