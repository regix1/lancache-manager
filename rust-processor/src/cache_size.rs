use anyhow::Result;
use chrono::Utc;
use jwalk::WalkDir;
use rayon::prelude::*;
use serde::Serialize;
use std::env;
use std::fs;
use std::path::Path;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Instant;

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

mod cache_utils;
use cache_utils::detect_filesystem_type;

#[derive(Serialize)]
struct ProgressData {
    #[serde(rename = "isProcessing")]
    is_processing: bool,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: String,
    message: String,
    #[serde(rename = "directoriesScanned")]
    directories_scanned: usize,
    #[serde(rename = "totalDirectories")]
    total_directories: usize,
    #[serde(rename = "totalBytes")]
    total_bytes: u64,
    #[serde(rename = "totalFiles")]
    total_files: u64,
    timestamp: String,
}

#[derive(Serialize)]
struct CacheSizeResult {
    #[serde(rename = "totalBytes")]
    total_bytes: u64,
    #[serde(rename = "totalFiles")]
    total_files: u64,
    #[serde(rename = "totalDirectories")]
    total_directories: u64,
    #[serde(rename = "hexDirectories")]
    hex_directories: usize,
    #[serde(rename = "scanDurationMs")]
    scan_duration_ms: u64,
    #[serde(rename = "estimatedDeletionTimes")]
    estimated_deletion_times: EstimatedDeletionTimes,
    #[serde(rename = "formattedSize")]
    formatted_size: String,
    timestamp: String,
}

#[derive(Serialize)]
struct EstimatedDeletionTimes {
    #[serde(rename = "preserveSeconds")]
    preserve_seconds: f64,
    #[serde(rename = "fullSeconds")]
    full_seconds: f64,
    #[serde(rename = "rsyncSeconds")]
    rsync_seconds: f64,
    #[serde(rename = "preserveFormatted")]
    preserve_formatted: String,
    #[serde(rename = "fullFormatted")]
    full_formatted: String,
    #[serde(rename = "rsyncFormatted")]
    rsync_formatted: String,
}

fn format_duration(seconds: f64) -> String {
    if seconds < 1.0 {
        return "< 1 second".to_string();
    }
    
    let total_seconds = seconds.round() as u64;
    
    if total_seconds < 60 {
        return format!("{} second{}", total_seconds, if total_seconds == 1 { "" } else { "s" });
    }
    
    let minutes = total_seconds / 60;
    let secs = total_seconds % 60;
    
    if minutes < 60 {
        if secs == 0 {
            return format!("{} minute{}", minutes, if minutes == 1 { "" } else { "s" });
        }
        return format!("{}m {}s", minutes, secs);
    }
    
    let hours = minutes / 60;
    let mins = minutes % 60;
    
    if mins == 0 {
        return format!("{} hour{}", hours, if hours == 1 { "" } else { "s" });
    }
    format!("{}h {}m", hours, mins)
}

fn format_bytes(bytes: u64) -> String {
    const KB: u64 = 1024;
    const MB: u64 = KB * 1024;
    const GB: u64 = MB * 1024;
    const TB: u64 = GB * 1024;

    if bytes >= TB {
        format!("{:.2} TB", bytes as f64 / TB as f64)
    } else if bytes >= GB {
        format!("{:.2} GB", bytes as f64 / GB as f64)
    } else if bytes >= MB {
        format!("{:.2} MB", bytes as f64 / MB as f64)
    } else if bytes >= KB {
        format!("{:.2} KB", bytes as f64 / KB as f64)
    } else {
        format!("{} bytes", bytes)
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
            .share_mode(0x07)
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

fn write_result(result_path: &Path, result: &CacheSizeResult) -> Result<()> {
    let json = serde_json::to_string_pretty(result)?;

    #[cfg(windows)]
    {
        use std::fs::OpenOptions;
        use std::io::Write;

        let mut file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .share_mode(0x07)
            .open(result_path)?;

        file.write_all(json.as_bytes())?;
        file.flush()?;
    }

    #[cfg(not(windows))]
    {
        let temp_path = result_path.with_extension("json.tmp");
        fs::write(&temp_path, &json)?;
        fs::rename(&temp_path, result_path)?;
    }

    Ok(())
}

fn is_hex(value: &str) -> bool {
    value.len() == 2 && value.chars().all(|c| c.is_ascii_hexdigit())
}

/// Calibration result from running benchmarks on the actual filesystem
#[derive(Debug, Clone)]
struct CalibrationResult {
    /// Files deleted per second (individual unlink operations) - for preserve mode
    preserve_files_per_sec: f64,
    /// Directories deleted per second with remove_dir_all - for full mode
    full_dirs_per_sec: f64,
    /// Directories deleted per second with rsync - for rsync mode
    rsync_dirs_per_sec: f64,
}

/// Run calibration benchmarks to measure actual deletion speed for each mode
/// Tests the real operations: individual file deletion, remove_dir_all, and rsync
fn run_deletion_calibration(cache_dir: &Path) -> Option<CalibrationResult> {
    use std::io::Write;
    
    let calibration_dir = cache_dir.join(".lancache_calibration_temp");
    
    // Clean up any previous calibration directory
    let _ = fs::remove_dir_all(&calibration_dir);
    
    // Create calibration directory
    if fs::create_dir(&calibration_dir).is_err() {
        eprintln!("Warning: Could not create calibration directory, using fallback estimates");
        return None;
    }
    
    eprintln!("Running filesystem calibration benchmarks...");
    
    // === Test 1: Preserve mode (individual file deletions) ===
    let preserve_test_dir = calibration_dir.join("preserve_test");
    let _ = fs::create_dir(&preserve_test_dir);
    
    let num_test_files = 100;
    for i in 0..num_test_files {
        let file_path = preserve_test_dir.join(format!("file_{}.tmp", i));
        if let Ok(mut file) = fs::File::create(&file_path) {
            let _ = file.write_all(b"test data for calibration");
        }
    }
    
    let preserve_start = Instant::now();
    let mut files_deleted = 0u64;
    if let Ok(entries) = fs::read_dir(&preserve_test_dir) {
        for entry in entries.filter_map(|e| e.ok()) {
            if fs::remove_file(entry.path()).is_ok() {
                files_deleted += 1;
            }
        }
    }
    let preserve_elapsed = preserve_start.elapsed();
    let preserve_files_per_sec = if preserve_elapsed.as_secs_f64() > 0.001 {
        files_deleted as f64 / preserve_elapsed.as_secs_f64()
    } else {
        10000.0
    };
    let _ = fs::remove_dir(&preserve_test_dir);
    
    eprintln!("  Preserve mode: {:.1} files/sec", preserve_files_per_sec);
    
    // === Test 2: Full mode (remove_dir_all on directories with files) ===
    let num_test_dirs = 5;
    let files_per_dir = 50;
    
    for i in 0..num_test_dirs {
        let subdir = calibration_dir.join(format!("full_test_{:02}", i));
        let _ = fs::create_dir(&subdir);
        for j in 0..files_per_dir {
            let file_path = subdir.join(format!("file_{}.tmp", j));
            if let Ok(mut file) = fs::File::create(&file_path) {
                let _ = file.write_all(b"test data");
            }
        }
    }
    
    let full_start = Instant::now();
    let mut dirs_deleted = 0u64;
    for i in 0..num_test_dirs {
        let subdir = calibration_dir.join(format!("full_test_{:02}", i));
        if fs::remove_dir_all(&subdir).is_ok() {
            dirs_deleted += 1;
        }
    }
    let full_elapsed = full_start.elapsed();
    let full_dirs_per_sec = if full_elapsed.as_secs_f64() > 0.001 {
        dirs_deleted as f64 / full_elapsed.as_secs_f64()
    } else {
        100.0
    };
    
    eprintln!("  Full mode: {:.1} dirs/sec (with {} files each)", full_dirs_per_sec, files_per_dir);
    
    // === Test 3: Rsync mode (only on Linux) ===
    #[cfg(target_os = "linux")]
    let rsync_dirs_per_sec = {
        use std::process::Command;
        
        // Create empty source directory for rsync
        let empty_dir = calibration_dir.join("empty_source");
        let _ = fs::create_dir(&empty_dir);
        
        // Create test directories with files
        for i in 0..num_test_dirs {
            let subdir = calibration_dir.join(format!("rsync_test_{:02}", i));
            let _ = fs::create_dir(&subdir);
            for j in 0..files_per_dir {
                let file_path = subdir.join(format!("file_{}.tmp", j));
                if let Ok(mut file) = fs::File::create(&file_path) {
                    let _ = file.write_all(b"test data");
                }
            }
        }
        
        let rsync_start = Instant::now();
        let mut rsync_dirs_deleted = 0u64;
        for i in 0..num_test_dirs {
            let subdir = calibration_dir.join(format!("rsync_test_{:02}", i));
            let result = Command::new("rsync")
                .arg("-a")
                .arg("--delete")
                .arg(format!("{}/", empty_dir.display()))
                .arg(format!("{}/", subdir.display()))
                .output();
            
            if result.is_ok() {
                rsync_dirs_deleted += 1;
            }
            // Clean up the now-empty directory
            let _ = fs::remove_dir(&subdir);
        }
        let rsync_elapsed = rsync_start.elapsed();
        
        let _ = fs::remove_dir(&empty_dir);
        
        if rsync_elapsed.as_secs_f64() > 0.001 && rsync_dirs_deleted > 0 {
            let rate = rsync_dirs_deleted as f64 / rsync_elapsed.as_secs_f64();
            eprintln!("  Rsync mode: {:.1} dirs/sec", rate);
            rate
        } else {
            eprintln!("  Rsync mode: using fallback estimate");
            25.0 // Conservative fallback
        }
    };
    
    #[cfg(not(target_os = "linux"))]
    let rsync_dirs_per_sec = {
        eprintln!("  Rsync mode: N/A (Linux only)");
        25.0 // Fallback for non-Linux
    };
    
    // Clean up calibration directory
    let _ = fs::remove_dir_all(&calibration_dir);
    
    eprintln!("Calibration complete!");
    
    Some(CalibrationResult {
        preserve_files_per_sec,
        full_dirs_per_sec,
        rsync_dirs_per_sec,
    })
}

/// Estimate deletion time based on calibration results and file/directory counts
/// Uses actual measured performance from the filesystem for accurate estimates
/// Accounts for parallelism used during actual deletion operations
fn estimate_deletion_times_calibrated(
    total_files: u64,
    total_dirs: u64,
    hex_dirs: usize,
    total_bytes: u64,
    calibration: &CalibrationResult,
) -> EstimatedDeletionTimes {
    let _ = total_dirs; // Not used directly; we use hex_dirs and total_files
    
    // Get CPU count for parallelism estimation
    let cpu_count = std::thread::available_parallelism()
        .map(|p| p.get())
        .unwrap_or(4) as f64;
    
    // Detect if this is likely a network filesystem based on calibration speeds
    // NFS typically shows < 1000 files/sec for individual operations
    let is_likely_network = calibration.preserve_files_per_sec < 5000.0;
    
    // Calculate parallelism factors (matching cache_clear.rs logic)
    // Network filesystems use reduced parallelism to avoid overwhelming the server
    let (preserve_threads, full_threads, rsync_threads) = if is_likely_network {
        (2.0, 2.0, cpu_count.min(4.0))  // NFS: limited parallelism
    } else {
        (cpu_count.min(16.0), cpu_count.min(8.0), cpu_count.min(6.0))  // Local: higher parallelism
    };
    
    // Parallelism efficiency (not quite linear due to I/O contention)
    // Network filesystems see less benefit from parallelism
    let efficiency = if is_likely_network { 0.7 } else { 0.85 };
    
    // Preserve mode: individual file deletions with parallelism
    let effective_preserve_rate = calibration.preserve_files_per_sec * preserve_threads * efficiency;
    let preserve_seconds = (total_files as f64 / effective_preserve_rate).max(1.0);
    
    // Full mode: remove_dir_all on each hex directory
    let files_per_hex_dir = if hex_dirs > 0 { 
        total_files as f64 / hex_dirs as f64 
    } else { 
        50.0 
    };
    let scale_factor = (files_per_hex_dir / 50.0).max(1.0);
    let effective_full_rate = (calibration.full_dirs_per_sec / scale_factor) * full_threads * efficiency;
    let full_seconds = (hex_dirs as f64 / effective_full_rate).max(0.5);
    
    // Rsync mode: calibrated rate with parallelism
    let effective_rsync_rate = calibration.rsync_dirs_per_sec * rsync_threads * efficiency;
    let rsync_seconds = (hex_dirs as f64 / effective_rsync_rate).max(1.0);
    
    // Add small overhead for very large caches (I/O scheduling, buffer flushes)
    let size_gb = total_bytes as f64 / (1024.0 * 1024.0 * 1024.0);
    let size_overhead = if size_gb > 500.0 { 1.1 } else { 1.0 };
    
    let preserve_seconds = preserve_seconds * size_overhead;
    let full_seconds = full_seconds * size_overhead;
    let rsync_seconds = rsync_seconds * size_overhead;
    
    EstimatedDeletionTimes {
        preserve_seconds,
        full_seconds,
        rsync_seconds,
        preserve_formatted: format_duration(preserve_seconds),
        full_formatted: format_duration(full_seconds),
        rsync_formatted: format_duration(rsync_seconds),
    }
}

/// Fallback estimation when calibration is not available
/// Uses conservative estimates based on typical filesystem performance
fn estimate_deletion_times_fallback(
    total_files: u64,
    total_dirs: u64,
    hex_dirs: usize,
    total_bytes: u64,
    is_network_fs: bool,
) -> EstimatedDeletionTimes {
    let _ = total_dirs; // Not used in this estimation method
    
    // Get CPU count for parallelism
    let cpu_count = std::thread::available_parallelism()
        .map(|p| p.get())
        .unwrap_or(4) as f64;
    
    // Base single-threaded rates and parallelism factors
    let (preserve_rate, full_rate, rsync_rate, threads) = if is_network_fs {
        // NFS/SMB: conservative rates with limited parallelism
        (300.0, 1.0, 15.0, cpu_count.min(4.0) * 0.7)
    } else {
        // Local: higher rates with more parallelism
        (2000.0, 10.0, 50.0, cpu_count.min(8.0) * 0.85)
    };
    
    let preserve_seconds = (total_files as f64 / (preserve_rate * threads.min(2.0))).max(1.0);
    let full_seconds = (hex_dirs as f64 / (full_rate * threads.min(2.0))).max(0.5);
    let rsync_seconds = (hex_dirs as f64 / (rsync_rate * threads)).max(1.0);
    
    // Size overhead
    let size_gb = total_bytes as f64 / (1024.0 * 1024.0 * 1024.0);
    let size_overhead = if size_gb > 500.0 { 1.15 } else { 1.0 };
    
    EstimatedDeletionTimes {
        preserve_seconds: preserve_seconds * size_overhead,
        full_seconds: full_seconds * size_overhead,
        rsync_seconds: rsync_seconds * size_overhead,
        preserve_formatted: format_duration(preserve_seconds * size_overhead),
        full_formatted: format_duration(full_seconds * size_overhead),
        rsync_formatted: format_duration(rsync_seconds * size_overhead),
    }
}

fn calculate_cache_size(cache_path: &str, progress_path: &Path) -> Result<CacheSizeResult> {
    let start_time = Instant::now();
    eprintln!("Starting cache size calculation...");
    eprintln!("Cache path: {}", cache_path);

    let cache_dir = Path::new(cache_path);
    if !cache_dir.exists() {
        anyhow::bail!("Cache directory does not exist: {}", cache_path);
    }

    // Detect filesystem type - NFS/SMB require different strategies
    let fs_type = detect_filesystem_type(cache_dir);
    let is_network_fs = fs_type.is_network();

    eprintln!("Filesystem type: {:?} (network: {})", fs_type, is_network_fs);

    // Find all hex directories (00-ff)
    let hex_dirs: Vec<_> = fs::read_dir(cache_dir)?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_dir()
                && path
                    .file_name()
                    .and_then(|n| n.to_str())
                    .map(is_hex)
                    .unwrap_or(false)
        })
        .collect();

    let total_hex_dirs = hex_dirs.len();
    eprintln!("Found {} hex directories to scan", total_hex_dirs);

    if total_hex_dirs == 0 {
        // Empty cache
        let result = CacheSizeResult {
            total_bytes: 0,
            total_files: 0,
            total_directories: 0,
            hex_directories: 0,
            scan_duration_ms: start_time.elapsed().as_millis() as u64,
            estimated_deletion_times: EstimatedDeletionTimes {
                preserve_seconds: 0.0,
                full_seconds: 0.0,
                rsync_seconds: 0.0,
                preserve_formatted: "< 1 second".to_string(),
                full_formatted: "< 1 second".to_string(),
                rsync_formatted: "< 1 second".to_string(),
            },
            formatted_size: "0 bytes".to_string(),
            timestamp: Utc::now().to_rfc3339(),
        };
        write_result(progress_path, &result)?;
        return Ok(result);
    }

    // For network filesystems (NFS/SMB), use system commands which are more reliable
    // The NFS client caches directory information and 'du' leverages this efficiently
    if is_network_fs {
        eprintln!("Network filesystem detected - using optimized du/find approach");
        return calculate_cache_size_network(cache_dir, progress_path, total_hex_dirs, start_time);
    }

    // For local filesystems, use parallel scanning (fast and reliable)
    eprintln!("Local filesystem - using parallel scan approach");

    // Atomic counters for parallel scanning
    let total_bytes = Arc::new(AtomicU64::new(0));
    let total_files = Arc::new(AtomicU64::new(0));
    let total_dirs = Arc::new(AtomicU64::new(0));
    let dirs_scanned = Arc::new(AtomicUsize::new(0));
    let failed_entries = Arc::new(AtomicU64::new(0));
    let failed_metadata = Arc::new(AtomicU64::new(0));

    // Write initial progress
    let progress = ProgressData {
        is_processing: true,
        percent_complete: 0.0,
        status: "scanning".to_string(),
        message: "Starting cache size scan...".to_string(),
        directories_scanned: 0,
        total_directories: total_hex_dirs,
        total_bytes: 0,
        total_files: 0,
        timestamp: Utc::now().to_rfc3339(),
    };
    write_progress(progress_path, &progress)?;

    // Clone references for progress monitoring
    let bytes_for_monitor = Arc::clone(&total_bytes);
    let files_for_monitor = Arc::clone(&total_files);
    let dirs_for_monitor = Arc::clone(&dirs_scanned);
    let progress_path_clone = progress_path.to_path_buf();

    // Progress monitoring thread
    let monitor_handle = std::thread::spawn(move || {
        loop {
            std::thread::sleep(std::time::Duration::from_millis(300));
            
            let scanned = dirs_for_monitor.load(Ordering::Relaxed);
            if scanned >= total_hex_dirs {
                break;
            }
            
            let bytes = bytes_for_monitor.load(Ordering::Relaxed);
            let files = files_for_monitor.load(Ordering::Relaxed);
            let percent = (scanned as f64 / total_hex_dirs as f64) * 100.0;
            
            let progress = ProgressData {
                is_processing: true,
                percent_complete: percent,
                status: "scanning".to_string(),
                message: format!("Scanning directories ({}/{})...", scanned, total_hex_dirs),
                directories_scanned: scanned,
                total_directories: total_hex_dirs,
                total_bytes: bytes,
                total_files: files,
                timestamp: Utc::now().to_rfc3339(),
            };
            
            if let Err(e) = write_progress(&progress_path_clone, &progress) {
                eprintln!("Warning: Failed to write progress: {}", e);
            }
        }
    });

    // Clone error counters for parallel loop
    let failed_entries_clone = Arc::clone(&failed_entries);
    let failed_metadata_clone = Arc::clone(&failed_metadata);

    // Parallel scan using rayon
    // Process hex directories in parallel, but use serial walking within each
    // to reduce concurrent NFS operations which can cause stale file handles
    hex_dirs.par_iter().for_each(|hex_dir| {
        // Use jwalk with serial mode for more reliable NFS handling
        for result in WalkDir::new(hex_dir)
            .skip_hidden(false)
            .follow_links(false)
            .parallelism(jwalk::Parallelism::Serial)
            .into_iter()
        {
            let entry = match result {
                Ok(e) => e,
                Err(_) => {
                    failed_entries_clone.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
            };

            let file_type = entry.file_type();

            if file_type.is_file() {
                total_files.fetch_add(1, Ordering::Relaxed);

                // Get file size - try jwalk's cached metadata first, then fallback to std::fs
                let size = match entry.metadata() {
                    Ok(metadata) => metadata.len(),
                    Err(_) => {
                        // Fallback: try direct filesystem metadata call
                        match fs::metadata(entry.path()) {
                            Ok(metadata) => metadata.len(),
                            Err(_) => {
                                failed_metadata_clone.fetch_add(1, Ordering::Relaxed);
                                0
                            }
                        }
                    }
                };
                total_bytes.fetch_add(size, Ordering::Relaxed);
            } else if file_type.is_dir() {
                total_dirs.fetch_add(1, Ordering::Relaxed);
            }
        }

        dirs_scanned.fetch_add(1, Ordering::Relaxed);
    });

    // Wait for monitor to finish
    let _ = monitor_handle.join();

    let scan_duration = start_time.elapsed();
    let final_bytes = total_bytes.load(Ordering::Relaxed);
    let final_files = total_files.load(Ordering::Relaxed);
    let final_dirs = total_dirs.load(Ordering::Relaxed);
    let final_failed_entries = failed_entries.load(Ordering::Relaxed);
    let final_failed_metadata = failed_metadata.load(Ordering::Relaxed);

    eprintln!("\nCache scan completed!");
    eprintln!("  Hex directories: {}", total_hex_dirs);
    eprintln!("  Total directories: {}", final_dirs);
    eprintln!("  Total files: {}", final_files);
    eprintln!("  Total size: {} ({} bytes)", format_bytes(final_bytes), final_bytes);
    eprintln!("  Scan time: {:.2}s", scan_duration.as_secs_f64());

    // Report any failures - these indicate potential NFS or permission issues
    if final_failed_entries > 0 || final_failed_metadata > 0 {
        eprintln!("\n  ⚠ Warning: Some files could not be read:");
        if final_failed_entries > 0 {
            eprintln!("    - Failed to read {} directory entries", final_failed_entries);
        }
        if final_failed_metadata > 0 {
            eprintln!("    - Failed to get metadata for {} files (size counted as 0)", final_failed_metadata);
        }
        eprintln!("    This may indicate NFS issues, permission problems, or stale file handles.");
    }

    // Run calibration benchmark to measure actual filesystem performance
    eprintln!("\nRunning deletion speed calibration...");
    let estimates = if let Some(calibration) = run_deletion_calibration(cache_dir) {
        estimate_deletion_times_calibrated(
            final_files,
            final_dirs,
            total_hex_dirs,
            final_bytes,
            &calibration,
        )
    } else {
        // Fallback if calibration fails
        estimate_deletion_times_fallback(
            final_files,
            final_dirs,
            total_hex_dirs,
            final_bytes,
            false, // not network fs
        )
    };

    eprintln!("\nEstimated deletion times (based on filesystem calibration):");
    eprintln!("  Safe Mode (preserve): {}", estimates.preserve_formatted);
    eprintln!("  Fast Mode (full): {}", estimates.full_formatted);
    eprintln!("  Rsync Mode: {}", estimates.rsync_formatted);

    let result = CacheSizeResult {
        total_bytes: final_bytes,
        total_files: final_files,
        total_directories: final_dirs,
        hex_directories: total_hex_dirs,
        scan_duration_ms: scan_duration.as_millis() as u64,
        estimated_deletion_times: estimates,
        formatted_size: format_bytes(final_bytes),
        timestamp: Utc::now().to_rfc3339(),
    };

    write_result(progress_path, &result)?;

    Ok(result)
}

/// Calculate cache size using system commands (du/find)
/// This is optimized for NFS/SMB where individual stat() calls are expensive
/// but system commands can leverage NFS client caching effectively
fn calculate_cache_size_network(
    cache_dir: &Path,
    progress_path: &Path,
    hex_dir_count: usize,
    start_time: Instant,
) -> Result<CacheSizeResult> {
    use std::process::Command;

    eprintln!("Using du command to calculate total size (optimized for network filesystems)...");

    // Write initial progress
    let progress = ProgressData {
        is_processing: true,
        percent_complete: 10.0,
        status: "scanning".to_string(),
        message: "Calculating cache size using du (optimized for network storage)...".to_string(),
        directories_scanned: 0,
        total_directories: hex_dir_count,
        total_bytes: 0,
        total_files: 0,
        timestamp: Utc::now().to_rfc3339(),
    };
    write_progress(progress_path, &progress)?;

    // Use du -sb for total size (summarize, bytes)
    // This is much more reliable on NFS than iterating with stat()
    let du_output = Command::new("du")
        .arg("-sb")
        .arg(cache_dir)
        .output();

    let total_bytes = match du_output {
        Ok(output) if output.status.success() => {
            let stdout = String::from_utf8_lossy(&output.stdout);
            eprintln!("du output: {}", stdout.trim());
            stdout
                .split_whitespace()
                .next()
                .and_then(|s| s.parse::<u64>().ok())
                .unwrap_or(0)
        }
        Ok(output) => {
            let stderr = String::from_utf8_lossy(&output.stderr);
            eprintln!("du command failed: {}", stderr);
            // Fallback to du without -b (some systems don't support it)
            let fallback = Command::new("du")
                .arg("-s")
                .arg(cache_dir)
                .output();
            match fallback {
                Ok(out) if out.status.success() => {
                    let stdout = String::from_utf8_lossy(&out.stdout);
                    // du -s returns kilobytes
                    stdout
                        .split_whitespace()
                        .next()
                        .and_then(|s| s.parse::<u64>().ok())
                        .map(|kb| kb * 1024)
                        .unwrap_or(0)
                }
                _ => 0,
            }
        }
        Err(e) => {
            eprintln!("Failed to execute du: {}", e);
            0
        }
    };

    eprintln!("Total size from du: {} ({} bytes)", format_bytes(total_bytes), total_bytes);

    // Update progress
    let progress = ProgressData {
        is_processing: true,
        percent_complete: 50.0,
        status: "scanning".to_string(),
        message: "Counting files...".to_string(),
        directories_scanned: hex_dir_count / 2,
        total_directories: hex_dir_count,
        total_bytes,
        total_files: 0,
        timestamp: Utc::now().to_rfc3339(),
    };
    write_progress(progress_path, &progress)?;

    // Count files using find -type f | wc -l (more reliable on NFS)
    eprintln!("Counting files using find command...");
    let find_output = Command::new("sh")
        .arg("-c")
        .arg(format!("find '{}' -type f | wc -l", cache_dir.display()))
        .output();

    let total_files = match find_output {
        Ok(output) if output.status.success() => {
            let stdout = String::from_utf8_lossy(&output.stdout);
            stdout.trim().parse::<u64>().unwrap_or(0)
        }
        _ => {
            eprintln!("find command failed, estimating file count...");
            // Rough estimate: typical lancache has ~1 file per 1MB average
            total_bytes / (1024 * 1024)
        }
    };

    eprintln!("Total files: {}", total_files);

    // Count directories
    let dir_output = Command::new("sh")
        .arg("-c")
        .arg(format!("find '{}' -type d | wc -l", cache_dir.display()))
        .output();

    let total_dirs = match dir_output {
        Ok(output) if output.status.success() => {
            let stdout = String::from_utf8_lossy(&output.stdout);
            stdout.trim().parse::<u64>().unwrap_or(hex_dir_count as u64)
        }
        _ => hex_dir_count as u64 * 257, // Estimate: 256 subdirs per hex dir + 1
    };

    let scan_duration = start_time.elapsed();

    eprintln!("\nNetwork filesystem scan completed!");
    eprintln!("  Hex directories: {}", hex_dir_count);
    eprintln!("  Total directories: {}", total_dirs);
    eprintln!("  Total files: {}", total_files);
    eprintln!("  Total size: {} ({} bytes)", format_bytes(total_bytes), total_bytes);
    eprintln!("  Scan time: {:.2}s", scan_duration.as_secs_f64());

    // Run calibration benchmark to measure actual network filesystem performance
    // This is the most accurate way since NFS/SMB performance varies widely
    eprintln!("\nRunning deletion speed calibration on network filesystem...");
    let estimates = if let Some(calibration) = run_deletion_calibration(cache_dir) {
        estimate_deletion_times_calibrated(
            total_files,
            total_dirs,
            hex_dir_count,
            total_bytes,
            &calibration,
        )
    } else {
        // Fallback with network filesystem flag
        estimate_deletion_times_fallback(
            total_files,
            total_dirs,
            hex_dir_count,
            total_bytes,
            true, // is network fs
        )
    };

    eprintln!("\nEstimated deletion times (based on filesystem calibration):");
    eprintln!("  Safe Mode (preserve): {} ⚠ Slow on NFS", estimates.preserve_formatted);
    eprintln!("  Fast Mode (full): {}", estimates.full_formatted);
    eprintln!("  Rsync Mode: {} ✓ Recommended for NFS", estimates.rsync_formatted);

    let result = CacheSizeResult {
        total_bytes,
        total_files,
        total_directories: total_dirs,
        hex_directories: hex_dir_count,
        scan_duration_ms: scan_duration.as_millis() as u64,
        estimated_deletion_times: estimates,
        formatted_size: format_bytes(total_bytes),
        timestamp: Utc::now().to_rfc3339(),
    };

    write_result(progress_path, &result)?;

    Ok(result)
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() != 3 {
        eprintln!("Usage:");
        eprintln!("  cache_size <cache_path> <output_json_path>");
        eprintln!("\nExample:");
        eprintln!("  cache_size /var/cache/lancache ./data/cache_size.json");
        eprintln!("\nOutput:");
        eprintln!("  Writes JSON with total size, file count, and estimated deletion times");
        std::process::exit(1);
    }

    let cache_path = &args[1];
    let output_path = Path::new(&args[2]);

    match calculate_cache_size(cache_path, output_path) {
        Ok(result) => {
            println!("{}", serde_json::to_string_pretty(&result).unwrap());
            std::process::exit(0);
        }
        Err(e) => {
            eprintln!("Error: {:?}", e);
            
            // Write error to output file
            let error_data = serde_json::json!({
                "error": e.to_string(),
                "status": "failed",
                "timestamp": Utc::now().to_rfc3339()
            });
            let _ = fs::write(output_path, serde_json::to_string_pretty(&error_data).unwrap());
            
            std::process::exit(1);
        }
    }
}
