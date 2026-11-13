//! A cache file whose blocks the filesystem has not allocated yet (nginx still writing it over
//! NFS, or a compressing server that stores a run of zeros in no blocks) still takes its place
//! on disk: its service stays listed in Services on Disk, counted by what the file takes rather
//! than by its listed length.
#![cfg(unix)]

mod detect_run;

use std::fs;
use std::os::unix::fs::MetadataExt;

use detect_run::{LISTED_BYTES, SERVICE};
use lancache_processor::cache_utils;

const URL: &str = "/c/msdownload/update/software/not-yet-written.cab";

/// A cache file with a listed length and no allocated blocks, at the path nginx gives `URL`.
fn write_unallocated_cache_file(cache: &std::path::Path) -> fs::Metadata {
    let path = cache_utils::calculate_cache_path_no_range(cache, SERVICE, URL);
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    fs::File::create(&path)
        .unwrap()
        .set_len(LISTED_BYTES)
        .unwrap();
    let stat = fs::metadata(&path).unwrap();
    assert_eq!(
        stat.blocks(),
        0,
        "the filesystem under {} allocated blocks for a file with no data, so this run proves nothing",
        cache.display()
    );
    stat
}

#[test]
fn a_file_with_no_allocated_blocks_counts_less_than_its_listed_length_and_more_than_nothing() {
    let temp = tempfile::tempdir().unwrap();
    let cache = temp.path().join("cache");
    write_unallocated_cache_file(&cache);
    let path = cache_utils::calculate_cache_path_no_range(&cache, SERVICE, URL);

    let total = cache_utils::total_size_from_filesystem([&path]);

    assert!(
        total > 0,
        "a file with content counts 0 bytes, so its game or service drops out of the on-disk totals"
    );
    assert!(
        total < LISTED_BYTES,
        "a file that takes no blocks counts its listed {LISTED_BYTES} bytes ({total})"
    );
}

#[tokio::test]
async fn a_service_whose_file_has_no_allocated_blocks_stays_on_disk() {
    let temp = tempfile::tempdir().unwrap();
    let cache = temp.path().join("cache");
    write_unallocated_cache_file(&cache);

    let total = detect_run::service_bytes_on_disk(&cache, URL).await;
    assert!(
        total > 0,
        "the {SERVICE} row reads 0 bytes, so Services on Disk drops it while its file is on disk"
    );
    assert!(
        total < LISTED_BYTES,
        "Services on Disk counts the listed {LISTED_BYTES} bytes of a file that takes no blocks ({total})"
    );
}
