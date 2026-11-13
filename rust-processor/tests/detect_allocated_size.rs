//! Games on Disk and Services on Disk sit beside Used Space, which is the drive's allocated
//! bytes. A cache file on a compressing or sparse filesystem lists a length larger than the
//! blocks it holds, so detection has to count what the file takes on disk, or the parts add up
//! to more than the whole.
#![cfg(unix)]

mod detect_run;

use std::fs;
use std::io::Write;
use std::os::unix::fs::MetadataExt;

use detect_run::{LISTED_BYTES, SERVICE};
use lancache_processor::cache_utils;

const URL: &str = "/c/msdownload/update/software/partly-written.cab";
const WRITTEN_BYTES: usize = 65_536;

#[tokio::test]
async fn a_partly_written_cache_file_adds_only_its_allocated_bytes_to_services_on_disk() {
    // One cache file nginx has only partly written: its first 64 KiB hold data and the rest of
    // its listed megabyte holds no blocks, as a file on a compressing NFS share reports.
    let temp = tempfile::tempdir().unwrap();
    let cache = temp.path().join("cache");
    let path = cache_utils::calculate_cache_path_no_range(&cache, SERVICE, URL);
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    let mut head = format!("\nKEY: {SERVICE}{URL}\n").into_bytes();
    head.resize(WRITTEN_BYTES, 0xAB);
    let mut file = fs::File::create(&path).unwrap();
    file.write_all(&head).unwrap();
    file.set_len(LISTED_BYTES).unwrap();
    file.sync_all().unwrap();
    drop(file);
    let stat = fs::metadata(&path).unwrap();
    let allocated = stat.blocks() * 512;
    assert!(
        allocated < stat.len(),
        "the filesystem under {} kept no hole in the file ({} allocated of {} listed), so this run proves nothing",
        temp.path().display(),
        allocated,
        stat.len()
    );

    let total = detect_run::service_bytes_on_disk(&cache, URL).await;
    assert!(
        total <= allocated,
        "Services on Disk counts {total} bytes for a {SERVICE} file that takes {allocated} bytes on disk (listed length {LISTED_BYTES}), so a part reads larger than Used Space"
    );
}
