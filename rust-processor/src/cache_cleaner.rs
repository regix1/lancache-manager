use anyhow::Result;
use chrono::Utc;
use rayon::prelude::*;
use rayon::ThreadPoolBuilder;
use serde::Serialize;
use std::env;
use std::fs;
use std::io::ErrorKind;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
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

fn delete_directory_contents(
    dir_path: &Path,
    files_counter: &AtomicU64,
) -> Result<()> {
    if !dir_path.exists() {
        return Ok(());
    }

    // Fast recursive deletion - NO metadata reads for speed
    fn delete_recursive(
        dir: &Path,
        files_counter: &AtomicU64,
    ) -> Result<()> {
        if dir.is_dir() {
            for entry_result in fs::read_dir(dir)? {
                let entry = entry_result?;
                let path = entry.path();

                if path.is_dir() {
                    delete_recursive(&path, files_counter)?;
                    // Try to remove the empty directory
                    let _ = fs::remove_dir(&path);
                } else {
                    // Just count and delete - NO metadata read for speed
                    files_counter.fetch_add(1, Ordering::Relaxed);
                    let _ = fs::remove_file(&path);
                }
            }
        }
        Ok(())
    }

    delete_recursive(dir_path, files_counter)?;

    Ok(())
}

fn delete_directory_full(
    dir_path: &Path,
    _files_counter: &AtomicU64,
) -> Result<()> {
    if !dir_path.exists() {
        return Ok(());
    }

    // Remove the entire directory tree in a single syscall. No need to recreate.
    match fs::remove_dir_all(dir_path) {
        Ok(_) => Ok(()),
        Err(err) if err.kind() == ErrorKind::NotFound => {
            // Directory doesn't exist, that's fine
            Ok(())
        }
        Err(err) => {
            // If the directory vanished despite the error, return success
            if !dir_path.exists() {
                return Ok(());
            }

            // Otherwise, fail with a clear error message
            anyhow::bail!(
                "Bulk removal failed for {}: {}. Please switch to 'Preserve Structure' or 'Rsync' mode.",
                dir_path.display(),
                err
            );
        }
    }
}

fn delete_directory_rsync(
    dir_path: &Path,
    _files_counter: &AtomicU64,
) -> Result<()> {
    if !dir_path.exists() {
        return Ok(());
    }

    // Create temporary empty directory
    let temp_empty = dir_path.with_file_name(format!(
        ".empty_{}",
        dir_path.file_name().and_then(|n| n.to_str()).unwrap_or("tmp")
    ));

    // Ensure temp dir exists and is empty
    let _ = fs::remove_dir_all(&temp_empty);
    fs::create_dir(&temp_empty)?;

    // Use rsync to delete contents by syncing with empty directory
    let output = std::process::Command::new("rsync")
        .arg("-a")
        .arg("--delete")
        .arg("--inplace")
        .arg(format!("{}/", temp_empty.display()))
        .arg(format!("{}/", dir_path.display()))
        .output();

    // Clean up temp directory
    let _ = fs::remove_dir_all(&temp_empty);

    match output {
        Ok(result) => {
            if !result.status.success() {
                let stderr = String::from_utf8_lossy(&result.stderr);
                anyhow::bail!(
                    "rsync failed for {}: {}. Please switch to 'Preserve Structure' or 'Bulk Removal' mode.",
                    dir_path.display(),
                    stderr
                );
            }
            Ok(())
        }
        Err(e) => {
            anyhow::bail!(
                "rsync command not available or failed for {}: {}. Please switch to 'Preserve Structure' or 'Bulk Removal' mode.",
                dir_path.display(),
                e
            );
        }
    }
}

fn clear_cache(cache_path: &str, progress_path: &Path, thread_count: usize, delete_mode: &str) -> Result<()> {
    let start_time = Instant::now();
    eprintln!("Starting cache clear operation...");
    eprintln!("Cache path: {}", cache_path);
    eprintln!("Deletion mode: {}", delete_mode);

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

    // Use configured thread count (clamped to reasonable values)
    let num_threads = std::cmp::max(1, std::cmp::min(thread_count, num_cpus::get()));
    eprintln!("Using {} threads for parallel processing", num_threads);

    let pool = ThreadPoolBuilder::new()
        .num_threads(num_threads)
        .build()
        .expect("Failed to build thread pool");

    // Clone Arc references for progress monitoring thread
    let bytes_for_monitor = Arc::clone(&total_bytes_deleted);
    let files_for_monitor = Arc::clone(&total_files_deleted);
    let dirs_for_monitor = Arc::clone(&dirs_processed);
    let progress_path_clone = progress_path.to_path_buf();

    // Start a background thread to update progress regularly
    let monitor_handle = std::thread::spawn(move || {
        let mut last_update = Instant::now();
        loop {
            std::thread::sleep(std::time::Duration::from_millis(500));

            let processed = dirs_for_monitor.load(Ordering::Relaxed);
            let bytes = bytes_for_monitor.load(Ordering::Relaxed);
            let files = files_for_monitor.load(Ordering::Relaxed);

            if processed >= total_dirs {
                break; // All done
            }

            if last_update.elapsed().as_millis() > 500 {
                let percent = (processed as f64 / total_dirs as f64) * 100.0;

                let progress = ProgressData::new(
                    true,
                    percent,
                    "running".to_string(),
                    format!("Clearing cache ({}/{})", processed, total_dirs),
                    processed,
                    total_dirs,
                    bytes,
                    files,
                );

                if let Err(e) = write_progress(&progress_path_clone, &progress) {
                    eprintln!("Warning: Failed to write progress: {}", e);
                }

                last_update = Instant::now();
            }
        }
    });

    // Process directories in parallel using rayon with limited thread pool
    pool.install(|| {
        hex_dirs.par_iter().for_each(|dir| {
        let dir_name = dir.file_name().and_then(|n| n.to_str()).unwrap_or("unknown");

        // Increment counter BEFORE processing so percentage updates immediately
        let processed = dirs_processed.fetch_add(1, Ordering::Relaxed) + 1;
        eprintln!("Processing directory {} ({}/{})", dir_name, processed, total_dirs);

        let result = match delete_mode {
            "full" => delete_directory_full(dir, &total_files_deleted),
            "rsync" => delete_directory_rsync(dir, &total_files_deleted),
            _ => delete_directory_contents(dir, &total_files_deleted),
        };

        match result {
            Ok(()) => {
                eprintln!("Completed directory {} ({}/{})", dir_name, processed, total_dirs);
            }
            Err(e) => {
                eprintln!("Warning: Failed to clear directory {}: {}", dir_name, e);
            }
        }
        });
    });

    // Wait for monitor thread to finish
    let _ = monitor_handle.join();

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

    if args.len() < 3 || args.len() > 5 {
        eprintln!("Usage:");
        eprintln!("  cache_cleaner <cache_path> <progress_json_path> [thread_count] [delete_mode]");
        eprintln!("\nExample:");
        eprintln!("  cache_cleaner /var/cache/lancache ./data/cache_clear_progress.json 4 preserve");
        eprintln!("\nOptions:");
        eprintln!("  thread_count: Number of threads (default: 4)");
        eprintln!("  delete_mode: Deletion method (default: preserve)");
        eprintln!("    - 'preserve': Delete files individually, preserve directory structure, shows file count");
        eprintln!("    - 'full': Bulk directory removal, removes entire directories at once");
        eprintln!("    - 'rsync': Use rsync --delete method, optimized for network storage (Linux only)");
        std::process::exit(1);
    }

    let cache_path = &args[1];
    let progress_path = Path::new(&args[2]);
    let thread_count = if args.len() >= 4 {
        args[3].parse::<usize>().unwrap_or(4)
    } else {
        4
    };
    let delete_mode = if args.len() >= 5 {
        &args[4]
    } else {
        "preserve"
    };

    match clear_cache(cache_path, progress_path, thread_count, delete_mode) {
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
