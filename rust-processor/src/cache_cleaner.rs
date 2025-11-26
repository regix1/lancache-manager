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
use std::sync::{Arc, Mutex};

#[cfg(target_os = "linux")]
use std::sync::OnceLock;
use std::time::Instant;

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

#[cfg(unix)]
use std::os::unix::ffi::OsStrExt;

#[derive(Serialize)]
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
    #[serde(rename = "activeDirectories")]
    active_directories: Vec<String>,
    #[serde(rename = "activeCount")]
    active_count: usize,
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
        active_directories: Vec<String>,
    ) -> Self {
        let active_count = active_directories.len();
        Self {
            is_processing,
            percent_complete,
            status,
            message,
            directories_processed,
            total_directories,
            bytes_deleted,
            files_deleted,
            active_directories,
            active_count,
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
                "Fast Mode removal failed for {}: {}. Please switch to 'Preserve Structure' or 'Rsync' mode.",
                dir_path.display(),
                err
            );
        }
    }
}

#[cfg(target_os = "linux")]
fn delete_directory_rsync(dir_path: &Path, files_counter: &AtomicU64) -> Result<()> {
    use std::process::Command;

    if !dir_path.exists() {
        return Ok(());
    }

    static EMPTY_TEMPLATE: OnceLock<PathBuf> = OnceLock::new();

    let empty_dir = match EMPTY_TEMPLATE.get() {
        Some(path) => path,
        None => {
            let mut path = env::temp_dir();
            path.push(format!(".lancache-empty-{}", std::process::id()));

            if path.exists() {
                fs::remove_dir_all(&path)?;
            }
            fs::create_dir(&path)?;

            // Ignore error if another thread set it first.
            let _ = EMPTY_TEMPLATE.set(path);
            EMPTY_TEMPLATE.get().expect("empty template directory should be set")
        }
    };

    let output = Command::new("rsync")
        .arg("-a")
        .arg("--delete")
        .arg("--stats")
        .arg(format!("{}/", empty_dir.display()))
        .arg(format!("{}/", dir_path.display()))
        .output();

    match output {
        Ok(result) => {
            if !result.status.success() {
                let stderr = String::from_utf8_lossy(&result.stderr);
                eprintln!("rsync stderr for {}: {}", dir_path.display(), stderr);
                anyhow::bail!(
                    "rsync failed for {}: {}. Please switch to 'Preserve Structure' or 'Fast Mode' mode.",
                    dir_path.display(),
                    stderr
                );
            }

            let stdout = String::from_utf8_lossy(&result.stdout);
            eprintln!("rsync stats for {}:\n{}", dir_path.display(), stdout);

            if let Some(deleted) = parse_rsync_deleted_files(&stdout) {
                eprintln!("Parsed {} deleted files from rsync stats", deleted);
                files_counter.fetch_add(deleted, Ordering::Relaxed);
            } else {
                eprintln!("Warning: Could not parse deleted file count from rsync stats for {}", dir_path.display());
            }

            // Check if directory still contains entries (e.g., rsync couldn't remove them)
            if let Ok(mut entries) = fs::read_dir(dir_path) {
                if entries.next().is_some() {
                    anyhow::bail!(
                        "rsync failed to completely clear {}. Directory still contains files. Please try again or switch to a different deletion mode.",
                        dir_path.display()
                    );
                }
            }

            Ok(())
        }
        Err(e) => {
            anyhow::bail!(
                "rsync command not available or failed for {}: {}. Please switch to 'Preserve Structure' or 'Fast Mode' mode.",
                dir_path.display(),
                e
            );
        }
    }
}

#[cfg(not(target_os = "linux"))]
fn delete_directory_rsync(
    _dir_path: &Path,
    _files_counter: &AtomicU64,
) -> Result<()> {
    anyhow::bail!(
        "Rsync mode is only supported on Linux. Please switch to 'Preserve Structure' or 'Fast Mode' mode."
    );
}

#[cfg(target_os = "linux")]
fn parse_rsync_deleted_files(stats: &str) -> Option<u64> {
    eprintln!("Parsing rsync stats for deleted files...");

    for line in stats.lines() {
        let trimmed = line.trim();
        eprintln!("  Checking line: {}", trimmed);

        // Try multiple formats that rsync might use
        if let Some(rest) = trimmed.strip_prefix("Number of deleted files:") {
            let value_part = rest.trim().split_whitespace().next()?;
            eprintln!("  Found 'Number of deleted files:' with value: {}", value_part);
            if let Ok(value) = value_part.replace(",", "").parse::<u64>() {
                return Some(value);
            }
        }

        // Alternative format: "deleted: 12345"
        if trimmed.to_lowercase().starts_with("deleted:") || trimmed.to_lowercase().contains("files deleted:") {
            eprintln!("  Found alternative deleted format: {}", trimmed);
            for word in trimmed.split_whitespace() {
                if let Ok(value) = word.replace(",", "").parse::<u64>() {
                    return Some(value);
                }
            }
        }

        // Try to find patterns like "Number of files: 0 (reg: 0, dir: 0, link: 0)"
        // and "Number of deleted files: X"
        if trimmed.contains("deleted") && trimmed.contains(":") {
            eprintln!("  Line contains 'deleted' and ':': {}", trimmed);
            // Extract numbers from the line
            for part in trimmed.split(&[':', ',', '(', ')'][..]) {
                let part = part.trim();
                if let Ok(value) = part.replace(",", "").parse::<u64>() {
                    if value > 0 {
                        eprintln!("  Found potential deleted count: {}", value);
                        return Some(value);
                    }
                }
            }
        }
    }

    eprintln!("  No deleted file count found in stats");
    None
}

#[cfg(unix)]
fn get_available_bytes(path: &Path) -> Result<u64> {
    use std::ffi::CString;
    use std::mem::MaybeUninit;

    let c_path = CString::new(path.as_os_str().as_bytes())?;
    let mut stat: MaybeUninit<libc::statvfs> = MaybeUninit::uninit();
    let res = unsafe { libc::statvfs(c_path.as_ptr(), stat.as_mut_ptr()) };
    if res != 0 {
        return Err(std::io::Error::last_os_error().into());
    }
    let stat = unsafe { stat.assume_init() };
    Ok(stat.f_bavail as u64 * stat.f_frsize as u64)
}

#[cfg(not(unix))]
fn get_available_bytes(_path: &Path) -> Result<u64> {
    Ok(0)
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
                .map(is_hex)
                .unwrap_or(false)
        })
        .collect();

    let total_dirs = hex_dirs.len();
    eprintln!("Found {} cache directories to clear", total_dirs);

    let initial_available = get_available_bytes(cache_dir).unwrap_or(0);

    // Atomic counters for progress tracking
    let dirs_processed = Arc::new(AtomicUsize::new(0));
    let total_bytes_deleted = Arc::new(AtomicU64::new(0));
    let total_files_deleted = Arc::new(AtomicU64::new(0));
    let active_dirs = Arc::new(Mutex::new(Vec::<String>::new()));

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
        Vec::new(),
    );
    write_progress(progress_path, &progress)?;

    // Use 4 threads for optimal I/O performance
    eprintln!("Using {} threads for parallel I/O operations", thread_count);

    let pool = ThreadPoolBuilder::new()
        .num_threads(thread_count)
        .build()
        .expect("Failed to build thread pool");

    // Clone Arc references for progress monitoring thread
    let bytes_for_monitor = Arc::clone(&total_bytes_deleted);
    let files_for_monitor = Arc::clone(&total_files_deleted);
    let dirs_for_monitor = Arc::clone(&dirs_processed);
    let active_for_monitor = Arc::clone(&active_dirs);
    let progress_path_clone = progress_path.to_path_buf();
    let cache_dir_for_monitor = cache_dir.to_path_buf();

    // Start a background thread to update progress regularly
    let monitor_handle = std::thread::spawn(move || {
        let mut last_update = Instant::now();
        loop {
            std::thread::sleep(std::time::Duration::from_millis(500));

            let processed = dirs_for_monitor.load(Ordering::Relaxed);
            if let Ok(current_available) = get_available_bytes(&cache_dir_for_monitor) {
                if current_available >= initial_available {
                    let freed = current_available - initial_available;
                    bytes_for_monitor.store(freed, Ordering::Relaxed);
                }
            }
            let bytes = bytes_for_monitor.load(Ordering::Relaxed);
            let files = files_for_monitor.load(Ordering::Relaxed);

            if processed >= total_dirs {
                break; // All done
            }

            if last_update.elapsed().as_millis() > 500 {
                let percent = (processed as f64 / total_dirs as f64) * 100.0;

                // Get snapshot of active directories
                let active_snapshot = if let Ok(active) = active_for_monitor.lock() {
                    active.clone()
                } else {
                    Vec::new()
                };
                let active_count = active_snapshot.len();

                // Log active directories if any
                if active_count > 0 {
                    eprintln!("Active: {} directories being processed: [{}]",
                             active_count,
                             active_snapshot.join(", "));
                }

                let progress = ProgressData::new(
                    true,
                    percent,
                    "running".to_string(),
                    format!("Clearing cache ({}/{}) - {} active", processed, total_dirs, active_count),
                    processed,
                    total_dirs,
                    bytes,
                    files,
                    active_snapshot,
                );

                if let Err(e) = write_progress(&progress_path_clone, &progress) {
                    eprintln!("Warning: Failed to write progress: {}", e);
                }

                last_update = Instant::now();
            }
        }
    });

    // Process directories in parallel using rayon with limited thread pool
    let active_for_workers = Arc::clone(&active_dirs);
    pool.install(|| {
        hex_dirs.par_iter().for_each(|dir| {
        let dir_name = dir.file_name().and_then(|n| n.to_str()).unwrap_or("unknown");
        let dir_name_str = dir_name.to_string();

        // Add to active list
        if let Ok(mut active) = active_for_workers.lock() {
            active.push(dir_name_str.clone());
        }

        eprintln!("Processing directory {}", dir_name);

        let result = match delete_mode {
            "full" => delete_directory_full(dir, &total_files_deleted),
            "rsync" => delete_directory_rsync(dir, &total_files_deleted),
            _ => delete_directory_contents(dir, &total_files_deleted),
        };

        // Remove from active list
        if let Ok(mut active) = active_for_workers.lock() {
            active.retain(|d| d != &dir_name_str);
        }

        match result {
            Ok(()) => {
                // Increment counter AFTER processing completes
                let processed = dirs_processed.fetch_add(1, Ordering::Relaxed) + 1;
                eprintln!("Completed directory {} ({}/{})", dir_name, processed, total_dirs);
            }
            Err(e) => {
                // Still increment on error so we don't get stuck
                let processed = dirs_processed.fetch_add(1, Ordering::Relaxed) + 1;
                eprintln!("Warning: Failed to clear directory {} ({}/{}): {}", dir_name, processed, total_dirs, e);
            }
        }
        });
    });

    // Wait for monitor thread to finish
    let _ = monitor_handle.join();

    let final_dirs = dirs_processed.load(Ordering::Relaxed);
    let final_bytes = get_available_bytes(cache_dir)
        .ok()
        .and_then(|current| {
            if current >= initial_available {
                Some(current - initial_available)
            } else {
                None
            }
        })
        .unwrap_or_else(|| total_bytes_deleted.load(Ordering::Relaxed));

    total_bytes_deleted.store(final_bytes, Ordering::Relaxed);
    let final_files = total_files_deleted.load(Ordering::Relaxed);
    let elapsed = start_time.elapsed();

    eprintln!("\nCache clear completed!");
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
        Vec::new(),
    );
    write_progress(progress_path, &progress)?;

    Ok(())
}

/// Determine optimal thread count based on delete mode and available CPUs
fn get_optimal_thread_count(delete_mode: &str) -> usize {
    let cpu_count = std::thread::available_parallelism()
        .map(|p| p.get())
        .unwrap_or(4);

    match delete_mode {
        // Fast mode uses remove_dir_all which is already efficient
        // Use fewer threads to avoid overwhelming the filesystem
        "full" => std::cmp::min(cpu_count, 8),

        // Rsync mode - each rsync process is independent
        // Can use moderate parallelism
        "rsync" => std::cmp::min(cpu_count, 6),

        // Preserve mode does individual file deletes - I/O bound
        // Use high parallelism to maximize throughput on SSDs
        // For NAS this might be too aggressive but user can override
        _ => std::cmp::min(cpu_count * 2, 16),
    }
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() < 3 || args.len() > 5 {
        eprintln!("Usage:");
        eprintln!("  cache_cleaner <cache_path> <progress_json_path> [delete_mode] [thread_count]");
        eprintln!("\nExample:");
        eprintln!("  cache_cleaner /var/cache/lancache ./data/cache_clear_progress.json preserve");
        eprintln!("  cache_cleaner /mnt/nas/cache ./data/progress.json full 8");
        eprintln!("\nOptions:");
        eprintln!("  delete_mode: Deletion method (default: preserve)");
        eprintln!("    - 'preserve': Safe Mode - Individual file deletion (slower, keeps structure)");
        eprintln!("    - 'full': Fast Mode - Directory removal (faster)");
        eprintln!("    - 'rsync': Rsync - With empty directory (network storage, Linux only)");
        eprintln!("  thread_count: Number of parallel threads (default: auto-detected based on mode)");
        eprintln!("    - preserve: 2x CPU cores (max 16) for I/O-bound operations");
        eprintln!("    - full: CPU cores (max 8) for syscall-efficient operations");
        eprintln!("    - rsync: CPU cores (max 6) for process-based operations");
        std::process::exit(1);
    }

    let cache_path = &args[1];
    let progress_path = Path::new(&args[2]);
    let delete_mode = if args.len() >= 4 {
        &args[3]
    } else {
        "preserve"
    };

    // Thread count: use provided value or auto-detect based on mode
    let thread_count = if args.len() >= 5 {
        args[4].parse::<usize>().unwrap_or_else(|_| get_optimal_thread_count(delete_mode))
    } else {
        get_optimal_thread_count(delete_mode)
    };

    eprintln!("Thread count: {} (mode: {})", thread_count, delete_mode);

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
                Vec::new(),
            );
            let _ = write_progress(progress_path, &error_progress);
            std::process::exit(1);
        }
    }
}
