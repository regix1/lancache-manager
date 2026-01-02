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
use std::time::{Duration, Instant};

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

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

/// Estimate deletion time based on file count, size, and deletion method
/// 
/// These estimates are based on typical filesystem performance:
/// - **preserve mode**: Individual file deletions, ~1000-5000 files/sec on SSD, ~200-500 on HDD/NAS
/// - **full mode**: Directory removal with remove_dir_all, much faster as it's a single syscall per dir
/// - **rsync mode**: rsync with empty directory, moderate speed due to process overhead
fn estimate_deletion_times(
    total_files: u64,
    total_dirs: u64,
    hex_dirs: usize,
    total_bytes: u64,
    scan_duration: Duration,
) -> EstimatedDeletionTimes {
    // Use scan performance as a proxy for filesystem speed
    // Faster scan = faster filesystem = adjust estimates
    let scan_secs = scan_duration.as_secs_f64().max(0.1);
    let files_per_sec_scan = total_files as f64 / scan_secs;
    
    // Base estimates (conservative for NAS/HDD, files per second for deletion)
    // These are intentionally conservative - better to overestimate than underestimate
    let base_delete_rate_preserve = 500.0;  // files/sec for individual deletes
    let base_delete_rate_full = 50000.0;    // files/sec equivalent for remove_dir_all
    let base_delete_rate_rsync = 2000.0;    // files/sec for rsync
    
    // Adjust based on observed scan speed (scan is read-only, deletion is slower)
    // If scan was fast, deletion will likely be faster too
    let speed_factor = (files_per_sec_scan / 10000.0).min(3.0).max(0.5);
    
    let adjusted_preserve_rate = base_delete_rate_preserve * speed_factor;
    let adjusted_full_rate = base_delete_rate_full * speed_factor;
    let adjusted_rsync_rate = base_delete_rate_rsync * speed_factor;
    
    // Calculate times
    let preserve_seconds = (total_files as f64 / adjusted_preserve_rate).max(1.0);
    
    // Full mode: primarily depends on directory count, not file count
    // Each remove_dir_all is roughly O(1) from our perspective
    let full_seconds = (hex_dirs as f64 * 0.5 + total_dirs as f64 / adjusted_full_rate * 10.0).max(0.5);
    
    // Rsync mode: process overhead per directory + file handling
    let rsync_seconds = (hex_dirs as f64 * 1.0 + total_files as f64 / adjusted_rsync_rate).max(1.0);
    
    // Add overhead based on total size (larger files = more disk activity)
    let size_factor = (total_bytes as f64 / (100.0 * 1024.0 * 1024.0 * 1024.0)).min(2.0); // Factor for 100GB+
    
    let preserve_seconds = preserve_seconds * (1.0 + size_factor * 0.1);
    let full_seconds = full_seconds * (1.0 + size_factor * 0.05);
    let rsync_seconds = rsync_seconds * (1.0 + size_factor * 0.1);
    
    EstimatedDeletionTimes {
        preserve_seconds,
        full_seconds,
        rsync_seconds,
        preserve_formatted: format_duration(preserve_seconds),
        full_formatted: format_duration(full_seconds),
        rsync_formatted: format_duration(rsync_seconds),
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

    // Atomic counters for parallel scanning
    let total_bytes = Arc::new(AtomicU64::new(0));
    let total_files = Arc::new(AtomicU64::new(0));
    let total_dirs = Arc::new(AtomicU64::new(0));
    let dirs_scanned = Arc::new(AtomicUsize::new(0));

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

    // Parallel scan using rayon
    hex_dirs.par_iter().for_each(|hex_dir| {
        // Use jwalk for fast parallel directory walking within each hex dir
        for entry in WalkDir::new(hex_dir)
            .skip_hidden(false)
            .follow_links(false)
            .into_iter()
            .filter_map(|e| e.ok())
        {
            let file_type = entry.file_type();
            
            if file_type.is_file() {
                total_files.fetch_add(1, Ordering::Relaxed);
                
                // Get file size - use metadata from entry if available
                if let Ok(metadata) = entry.metadata() {
                    total_bytes.fetch_add(metadata.len(), Ordering::Relaxed);
                }
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

    eprintln!("\nCache scan completed!");
    eprintln!("  Hex directories: {}", total_hex_dirs);
    eprintln!("  Total directories: {}", final_dirs);
    eprintln!("  Total files: {}", final_files);
    eprintln!("  Total size: {} ({} bytes)", format_bytes(final_bytes), final_bytes);
    eprintln!("  Scan time: {:.2}s", scan_duration.as_secs_f64());

    // Calculate deletion time estimates
    let estimates = estimate_deletion_times(
        final_files,
        final_dirs,
        total_hex_dirs,
        final_bytes,
        scan_duration,
    );

    eprintln!("\nEstimated deletion times:");
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
