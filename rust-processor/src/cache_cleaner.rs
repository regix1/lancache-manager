use anyhow::{Context, Result};
use chrono::Utc;
use rayon::prelude::*;
use serde::Serialize;
use std::env;
use std::fs::{self, File};
use std::io::Write as IoWrite;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

#[derive(Serialize, Clone)]
struct ProgressData {
    #[serde(rename = "isProcessing")]
    is_processing: bool,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: String,
    message: String,
    #[serde(rename = "directoriesProcessed")]
    directories_processed: usize,
    #[serde(rename = "totalDirectories")]
    total_directories: usize,
    #[serde(rename = "bytesDeleted")]
    bytes_deleted: u64,
    #[serde(rename = "filesDeleted")]
    files_deleted: u64,
    timestamp: String,
}

impl ProgressData {
    fn new(
        is_processing: bool,
        percent_complete: f64,
        status: String,
        message: String,
        directories_processed: usize,
        total_directories: usize,
        bytes_deleted: u64,
        files_deleted: u64,
    ) -> Self {
        Self {
            is_processing,
            percent_complete,
            status,
            message,
            directories_processed,
            total_directories,
            bytes_deleted,
            files_deleted,
            timestamp: Utc::now().to_rfc3339(),
        }
    }
}

fn write_progress(progress_path: &Path, progress: &ProgressData) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;

    #[cfg(windows)]
    {
        use std::fs::OpenOptions;
        use std::io::Write;

        let mut file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .share_mode(0x07) // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            .open(progress_path)?;

        file.write_all(json.as_bytes())?;
        file.flush()?;
    }

    #[cfg(not(windows))]
    {
        let temp_path = progress_path.with_extension("json.tmp");
        fs::write(&temp_path, &json)?;
        fs::rename(&temp_path, progress_path)?;
    }

    Ok(())
}

fn is_hex(value: &str) -> bool {
    value.len() == 2 && value.chars().all(|c| c.is_ascii_hexdigit())
}

fn delete_directory_contents(dir_path: &Path) -> Result<(u64, u64)> {
    let mut total_bytes = 0u64;
    let mut total_files = 0u64;

    if !dir_path.exists() {
        return Ok((0, 0));
    }

    // Walk through the directory and delete all files
    fn visit_dirs(dir: &Path, bytes: &mut u64, files: &mut u64) -> Result<()> {
        if dir.is_dir() {
            for entry_result in fs::read_dir(dir)? {
                let entry = entry_result?;
                let path = entry.path();

                if path.is_dir() {
                    visit_dirs(&path, bytes, files)?;
                    // Try to remove the empty directory
                    let _ = fs::remove_dir(&path);
                } else {
                    // Get file size before deleting
                    if let Ok(metadata) = entry.metadata() {
                        *bytes += metadata.len();
                    }
                    *files += 1;

                    // Delete the file
                    fs::remove_file(&path)?;
                }
            }
        }
        Ok(())
    }

    visit_dirs(dir_path, &mut total_bytes, &mut total_files)?;

    Ok((total_bytes, total_files))
}

fn clear_cache(cache_path: &str, progress_path: &Path) -> Result<()> {
    let start_time = Instant::now();
    eprintln!("Starting cache clear operation...");
    eprintln!("Cache path: {}", cache_path);

    let cache_dir = Path::new(cache_path);
    if !cache_dir.exists() {
        anyhow::bail!("Cache directory does not exist: {}", cache_path);
    }

    // Find all hex directories (00-ff)
    let hex_dirs: Vec<PathBuf> = fs::read_dir(cache_dir)?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_dir() && path.file_name()
                .and_then(|n| n.to_str())
                .map(|n| is_hex(n))
                .unwrap_or(false)
        })
        .collect();

    if hex_dirs.is_empty() {
        anyhow::bail!("No cache directories found in {}", cache_path);
    }

    let total_dirs = hex_dirs.len();
    eprintln!("Found {} cache directories to clear", total_dirs);

    // Atomic counters for progress tracking
    let dirs_processed = Arc::new(AtomicUsize::new(0));
    let total_bytes_deleted = Arc::new(AtomicU64::new(0));
    let total_files_deleted = Arc::new(AtomicU64::new(0));
    let last_progress_update = Arc::new(Mutex::new(Instant::now()));

    // Initial progress
    let progress = ProgressData::new(
        true,
        0.0,
        "running".to_string(),
        "Starting cache deletion...".to_string(),
        0,
        total_dirs,
        0,
        0,
    );
    write_progress(progress_path, &progress)?;

    // Process directories in parallel using rayon
    hex_dirs.par_iter().for_each(|dir| {
        let dir_name = dir.file_name().and_then(|n| n.to_str()).unwrap_or("unknown");

        match delete_directory_contents(dir) {
            Ok((bytes, files)) => {
                total_bytes_deleted.fetch_add(bytes, Ordering::Relaxed);
                total_files_deleted.fetch_add(files, Ordering::Relaxed);

                let processed = dirs_processed.fetch_add(1, Ordering::Relaxed) + 1;

                // Update progress every 500ms
                if let Ok(mut last_update) = last_progress_update.try_lock() {
                    if last_update.elapsed().as_millis() > 500 {
                        let percent = (processed as f64 / total_dirs as f64) * 100.0;
                        let bytes = total_bytes_deleted.load(Ordering::Relaxed);
                        let files = total_files_deleted.load(Ordering::Relaxed);

                        let progress = ProgressData::new(
                            true,
                            percent,
                            "running".to_string(),
                            format!("Clearing directory {} ({}/{})", dir_name, processed, total_dirs),
                            processed,
                            total_dirs,
                            bytes,
                            files,
                        );

                        if let Err(e) = write_progress(progress_path, &progress) {
                            eprintln!("Warning: Failed to write progress: {}", e);
                        }

                        *last_update = Instant::now();
                    }
                }
            }
            Err(e) => {
                eprintln!("Warning: Failed to clear directory {}: {}", dir_name, e);
                dirs_processed.fetch_add(1, Ordering::Relaxed);
            }
        }
    });

    let final_dirs = dirs_processed.load(Ordering::Relaxed);
    let final_bytes = total_bytes_deleted.load(Ordering::Relaxed);
    let final_files = total_files_deleted.load(Ordering::Relaxed);
    let elapsed = start_time.elapsed();

    eprintln!("\n✓ Cache clear completed!");
    eprintln!("  Directories processed: {}", final_dirs);
    eprintln!("  Files deleted: {}", final_files);
    eprintln!("  Bytes deleted: {} ({:.2} GB)", final_bytes, final_bytes as f64 / 1_073_741_824.0);
    eprintln!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    // Final progress
    let progress = ProgressData::new(
        false,
        100.0,
        "complete".to_string(),
        format!(
            "Cache cleared successfully! Deleted {} files ({:.2} GB) from {} directories in {:.2}s",
            final_files,
            final_bytes as f64 / 1_073_741_824.0,
            final_dirs,
            elapsed.as_secs_f64()
        ),
        final_dirs,
        total_dirs,
        final_bytes,
        final_files,
    );
    write_progress(progress_path, &progress)?;

    Ok(())
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() != 3 {
        eprintln!("Usage:");
        eprintln!("  cache_cleaner <cache_path> <progress_json_path>");
        eprintln!("\nExample:");
        eprintln!("  cache_cleaner /var/cache/lancache ./data/cache_clear_progress.json");
        std::process::exit(1);
    }

    let cache_path = &args[1];
    let progress_path = Path::new(&args[2]);

    match clear_cache(cache_path, progress_path) {
        Ok(_) => {
            std::process::exit(0);
        }
        Err(e) => {
            eprintln!("Error: {:?}", e);
            let error_progress = ProgressData::new(
                false,
                0.0,
                "failed".to_string(),
                format!("Cache clear failed: {}", e),
                0,
                0,
                0,
                0,
            );
            let _ = write_progress(progress_path, &error_progress);
            std::process::exit(1);
        }
    }
}
