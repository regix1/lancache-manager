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

/// A single calibration measurement for one test scenario
#[derive(Debug, Clone)]
struct ScenarioMeasurement {
    /// Number of files per directory
    files_per_dir: usize,
    /// Nesting depth (1 = flat, 2 = one level nested, etc.)
    depth: usize,
    /// Measured files deleted per second (preserve mode)
    preserve_files_per_sec: f64,
    /// Measured directories deleted per second with remove_dir_all (fast mode)
    full_dirs_per_sec: f64,
    /// Measured directories deleted per second with rsync (rsync mode)
    rsync_dirs_per_sec: f64,
}

/// Dynamic calibration result containing measurements from multiple test scenarios
#[derive(Debug, Clone)]
struct DynamicCalibrationResult {
    /// All scenario measurements
    scenarios: Vec<ScenarioMeasurement>,
    /// Whether this is a network filesystem (detected from measurements)
    is_network_fs: bool,
    /// CPU count for parallelism calculations
    cpu_count: usize,
}

/// Test scenario definition
struct TestScenario {
    name: &'static str,
    num_dirs: usize,
    files_per_dir: usize,
    depth: usize,
}

/// Measure preserve mode for a scenario: delete files individually
fn measure_preserve_mode(base_dir: &Path, scenario: &TestScenario) -> f64 {
    use std::io::Write;

    let test_dir = base_dir.join(format!("preserve_{}", scenario.name));
    let _ = fs::remove_dir_all(&test_dir);
    let _ = fs::create_dir_all(&test_dir);

    // Create the test structure with nested directories
    let mut all_files = Vec::new();
    for dir_idx in 0..scenario.num_dirs {
        let mut dir_path = test_dir.clone();
        for level in 0..scenario.depth {
            // Use hex-like naming similar to cache structure
            let hex_name = format!("{:02x}", (dir_idx * 17 + level * 37) % 256);
            dir_path = dir_path.join(hex_name);
        }
        let _ = fs::create_dir_all(&dir_path);

        for file_idx in 0..scenario.files_per_dir {
            let file_path = dir_path.join(format!("f_{:04}.tmp", file_idx));
            if let Ok(mut file) = fs::File::create(&file_path) {
                let _ = file.write_all(b"calibration");
                all_files.push(file_path);
            }
        }
    }

    if all_files.is_empty() {
        let _ = fs::remove_dir_all(&test_dir);
        return 0.0;
    }

    // Measure deletion time
    let start = Instant::now();
    let mut deleted = 0u64;
    for file_path in &all_files {
        if fs::remove_file(file_path).is_ok() {
            deleted += 1;
        }
    }
    let elapsed = start.elapsed().as_secs_f64();

    let _ = fs::remove_dir_all(&test_dir);

    if elapsed > 0.0001 && deleted > 0 {
        deleted as f64 / elapsed
    } else {
        0.0 // Will be handled by interpolation
    }
}

/// Measure fast mode for a scenario: delete directories with remove_dir_all
fn measure_fast_mode(base_dir: &Path, scenario: &TestScenario) -> f64 {
    use std::io::Write;

    let test_dir = base_dir.join(format!("fast_{}", scenario.name));
    let _ = fs::remove_dir_all(&test_dir);
    let _ = fs::create_dir_all(&test_dir);

    // Create root directories that will each be deleted with remove_dir_all
    let mut root_dirs = Vec::new();
    for dir_idx in 0..scenario.num_dirs {
        let root_dir = test_dir.join(format!("r_{:02}", dir_idx));
        let _ = fs::create_dir(&root_dir);

        // Build nested structure inside each root
        let mut dir_path = root_dir.clone();
        for level in 0..scenario.depth {
            let hex_name = format!("{:02x}", (dir_idx * 17 + level * 37) % 256);
            dir_path = dir_path.join(hex_name);
        }
        let _ = fs::create_dir_all(&dir_path);

        // Create files at the deepest level
        for file_idx in 0..scenario.files_per_dir {
            let file_path = dir_path.join(format!("f_{:04}.tmp", file_idx));
            if let Ok(mut file) = fs::File::create(&file_path) {
                let _ = file.write_all(b"calibration");
            }
        }

        root_dirs.push(root_dir);
    }

    if root_dirs.is_empty() {
        let _ = fs::remove_dir_all(&test_dir);
        return 0.0;
    }

    // Measure deletion time
    let start = Instant::now();
    let mut deleted = 0u64;
    for dir_path in &root_dirs {
        if fs::remove_dir_all(dir_path).is_ok() {
            deleted += 1;
        }
    }
    let elapsed = start.elapsed().as_secs_f64();

    let _ = fs::remove_dir_all(&test_dir);

    if elapsed > 0.0001 && deleted > 0 {
        deleted as f64 / elapsed
    } else {
        0.0
    }
}

/// Measure rsync mode for a scenario: delete using rsync --delete
fn measure_rsync_mode(base_dir: &Path, scenario: &TestScenario) -> f64 {
    #[cfg(target_os = "linux")]
    {
        use std::io::Write;
        use std::process::Command;

        let test_dir = base_dir.join(format!("rsync_{}", scenario.name));
        let empty_dir = base_dir.join("empty_src");
        let _ = fs::remove_dir_all(&test_dir);
        let _ = fs::remove_dir_all(&empty_dir);
        let _ = fs::create_dir_all(&test_dir);
        let _ = fs::create_dir_all(&empty_dir);

        // Create root directories for rsync deletion
        let mut root_dirs = Vec::new();
        for dir_idx in 0..scenario.num_dirs {
            let root_dir = test_dir.join(format!("r_{:02}", dir_idx));
            let _ = fs::create_dir(&root_dir);

            let mut dir_path = root_dir.clone();
            for level in 0..scenario.depth {
                let hex_name = format!("{:02x}", (dir_idx * 17 + level * 37) % 256);
                dir_path = dir_path.join(hex_name);
            }
            let _ = fs::create_dir_all(&dir_path);

            for file_idx in 0..scenario.files_per_dir {
                let file_path = dir_path.join(format!("f_{:04}.tmp", file_idx));
                if let Ok(mut file) = fs::File::create(&file_path) {
                    let _ = file.write_all(b"calibration");
                }
            }

            root_dirs.push(root_dir);
        }

        if root_dirs.is_empty() {
            let _ = fs::remove_dir_all(&test_dir);
            let _ = fs::remove_dir_all(&empty_dir);
            return 0.0;
        }

        // Measure rsync deletion time
        let start = Instant::now();
        let mut deleted = 0u64;
        for dir_path in &root_dirs {
            let result = Command::new("rsync")
                .arg("-a")
                .arg("--delete")
                .arg(format!("{}/", empty_dir.display()))
                .arg(format!("{}/", dir_path.display()))
                .output();

            if result.is_ok() {
                deleted += 1;
                let _ = fs::remove_dir(dir_path);
            }
        }
        let elapsed = start.elapsed().as_secs_f64();

        let _ = fs::remove_dir_all(&test_dir);
        let _ = fs::remove_dir_all(&empty_dir);

        if elapsed > 0.0001 && deleted > 0 {
            deleted as f64 / elapsed
        } else {
            0.0
        }
    }

    #[cfg(not(target_os = "linux"))]
    {
        let _ = (base_dir, scenario);
        0.0 // rsync not available on Windows - will use fast mode measurement
    }
}

/// Run comprehensive dynamic calibration with multiple test scenarios
/// Each scenario tests a different cache structure pattern
fn run_dynamic_calibration(cache_dir: &Path) -> Option<DynamicCalibrationResult> {
    let calibration_dir = cache_dir.join(".lancache_calibration_temp");

    // Clean up any previous calibration directory
    let _ = fs::remove_dir_all(&calibration_dir);

    if fs::create_dir(&calibration_dir).is_err() {
        eprintln!("Warning: Could not create calibration directory");
        return None;
    }

    eprintln!("Running dynamic filesystem calibration...");

    // Define test scenarios covering different cache characteristics:
    // - Varying file densities (sparse to dense)
    // - Varying depths (flat to deeply nested like real cache)
    // - Varying directory counts
    let scenarios = vec![
        // Flat directories with varying file counts
        TestScenario { name: "flat_sparse", num_dirs: 5, files_per_dir: 2, depth: 1 },
        TestScenario { name: "flat_medium", num_dirs: 5, files_per_dir: 20, depth: 1 },
        TestScenario { name: "flat_dense", num_dirs: 5, files_per_dir: 100, depth: 1 },

        // 2-level nested directories (like cache hex/subdir structure)
        TestScenario { name: "nested2_sparse", num_dirs: 8, files_per_dir: 2, depth: 2 },
        TestScenario { name: "nested2_medium", num_dirs: 8, files_per_dir: 20, depth: 2 },
        TestScenario { name: "nested2_dense", num_dirs: 8, files_per_dir: 50, depth: 2 },

        // 3-level nested directories (deeper cache paths)
        TestScenario { name: "nested3_sparse", num_dirs: 6, files_per_dir: 3, depth: 3 },
        TestScenario { name: "nested3_medium", num_dirs: 6, files_per_dir: 15, depth: 3 },

        // Many directories with few files (common in lancache)
        TestScenario { name: "many_dirs_single", num_dirs: 25, files_per_dir: 1, depth: 2 },
        TestScenario { name: "many_dirs_few", num_dirs: 15, files_per_dir: 5, depth: 2 },
    ];

    let mut measurements = Vec::new();

    for scenario in &scenarios {
        eprintln!("  Testing: {} ({} dirs × {} files, depth {})",
            scenario.name, scenario.num_dirs, scenario.files_per_dir, scenario.depth);

        let preserve_rate = measure_preserve_mode(&calibration_dir, scenario);
        let fast_rate = measure_fast_mode(&calibration_dir, scenario);
        let rsync_rate = measure_rsync_mode(&calibration_dir, scenario);

        eprintln!("    → Preserve: {:.0}/s, Fast: {:.1}/s, Rsync: {:.1}/s",
            preserve_rate, fast_rate, rsync_rate);

        measurements.push(ScenarioMeasurement {
            files_per_dir: scenario.files_per_dir,
            depth: scenario.depth,
            preserve_files_per_sec: preserve_rate,
            full_dirs_per_sec: fast_rate,
            rsync_dirs_per_sec: rsync_rate,
        });
    }

    // Clean up calibration directory
    let _ = fs::remove_dir_all(&calibration_dir);

    // Detect network filesystem from average preserve speeds
    // Local SSDs typically achieve > 5000 files/sec, NFS is much slower
    let valid_preserve: Vec<f64> = measurements.iter()
        .map(|m| m.preserve_files_per_sec)
        .filter(|&r| r > 0.0)
        .collect();
    let avg_preserve = if !valid_preserve.is_empty() {
        valid_preserve.iter().sum::<f64>() / valid_preserve.len() as f64
    } else {
        0.0
    };
    let is_network_fs = avg_preserve > 0.0 && avg_preserve < 3000.0;

    let cpu_count = std::thread::available_parallelism()
        .map(|p| p.get())
        .unwrap_or(4);

    eprintln!("Calibration complete! (Network FS: {}, CPUs: {})", is_network_fs, cpu_count);

    Some(DynamicCalibrationResult {
        scenarios: measurements,
        is_network_fs,
        cpu_count,
    })
}

/// Interpolate between two scenarios to estimate rate for a target files_per_dir
fn interpolate_rate(
    scenarios: &[ScenarioMeasurement],
    target_files_per_dir: f64,
    depth: usize,
    get_rate: fn(&ScenarioMeasurement) -> f64,
) -> f64 {
    // Filter to scenarios with matching or similar depth
    let depth_matched: Vec<&ScenarioMeasurement> = scenarios
        .iter()
        .filter(|s| s.depth == depth || (s.depth as i32 - depth as i32).abs() <= 1)
        .filter(|s| get_rate(s) > 0.0)
        .collect();

    if depth_matched.is_empty() {
        // Fall back to any valid scenario
        let valid: Vec<&ScenarioMeasurement> = scenarios
            .iter()
            .filter(|s| get_rate(s) > 0.0)
            .collect();
        if valid.is_empty() {
            return 0.0;
        }
        return valid.iter().map(|s| get_rate(s)).sum::<f64>() / valid.len() as f64;
    }

    // Find scenarios below and above target for interpolation
    let mut below: Option<&ScenarioMeasurement> = None;
    let mut above: Option<&ScenarioMeasurement> = None;

    for scenario in &depth_matched {
        let fpd = scenario.files_per_dir as f64;
        if fpd <= target_files_per_dir {
            if below.is_none() || fpd > below.unwrap().files_per_dir as f64 {
                below = Some(scenario);
            }
        }
        if fpd >= target_files_per_dir {
            if above.is_none() || fpd < above.unwrap().files_per_dir as f64 {
                above = Some(scenario);
            }
        }
    }

    match (below, above) {
        (Some(b), Some(a)) if b.files_per_dir != a.files_per_dir => {
            // Linear interpolation between the two scenarios
            let b_fpd = b.files_per_dir as f64;
            let a_fpd = a.files_per_dir as f64;
            let t = (target_files_per_dir - b_fpd) / (a_fpd - b_fpd);
            let b_rate = get_rate(b);
            let a_rate = get_rate(a);
            b_rate + t * (a_rate - b_rate)
        }
        (Some(s), _) | (_, Some(s)) => get_rate(s),
        _ => depth_matched.iter().map(|s| get_rate(s)).sum::<f64>() / depth_matched.len() as f64,
    }
}

/// Estimate deletion times using dynamic calibration measurements
/// Uses interpolation from measured scenarios - no hard-coded scaling factors
fn estimate_deletion_times_dynamic(
    total_files: u64,
    hex_dirs: usize,
    calibration: &DynamicCalibrationResult,
) -> EstimatedDeletionTimes {
    let scenarios = &calibration.scenarios;

    if scenarios.is_empty() || hex_dirs == 0 {
        return EstimatedDeletionTimes {
            preserve_seconds: 1.0,
            full_seconds: 1.0,
            rsync_seconds: 1.0,
            preserve_formatted: "< 1 second".to_string(),
            full_formatted: "< 1 second".to_string(),
            rsync_formatted: "< 1 second".to_string(),
        };
    }

    // Calculate actual cache characteristics
    let files_per_dir = total_files as f64 / hex_dirs as f64;

    // Estimate typical depth from cache structure (usually 2-3 levels: hex/subhex/files)
    let estimated_depth = 2;

    // === PRESERVE MODE ===
    // Interpolate preserve rate from measured scenarios
    let preserve_rate = interpolate_rate(
        scenarios,
        files_per_dir,
        estimated_depth,
        |s| s.preserve_files_per_sec,
    );

    // Calculate parallelism boost from measured CPU count
    // Use measured ratio between single-threaded and multi-threaded performance
    let cpu_count = calibration.cpu_count as f64;
    let parallel_efficiency = if calibration.is_network_fs {
        // Network FS: limited parallelism benefit (measured from our scenarios)
        (cpu_count / 4.0).min(2.0)
    } else {
        // Local FS: better parallelism (but not perfect linear scaling)
        (cpu_count / 2.0).min(8.0)
    };

    let effective_preserve_rate = if preserve_rate > 0.0 {
        preserve_rate * parallel_efficiency
    } else {
        1.0
    };
    let preserve_seconds = (total_files as f64 / effective_preserve_rate).max(0.1);

    // === FAST MODE ===
    // Interpolate full mode rate (dirs/sec with remove_dir_all)
    let full_rate = interpolate_rate(
        scenarios,
        files_per_dir,
        estimated_depth,
        |s| s.full_dirs_per_sec,
    );

    let effective_full_rate = if full_rate > 0.0 {
        full_rate * parallel_efficiency
    } else {
        1.0
    };
    let full_seconds = (hex_dirs as f64 / effective_full_rate).max(0.1);

    // === RSYNC MODE ===
    // Interpolate rsync rate from measured scenarios
    let rsync_rate = interpolate_rate(
        scenarios,
        files_per_dir,
        estimated_depth,
        |s| s.rsync_dirs_per_sec,
    );

    let effective_rsync_rate = if rsync_rate > 0.0 {
        // rsync has its own parallelism, but we can run multiple rsync processes
        rsync_rate * (cpu_count / 2.0).min(4.0)
    } else {
        // Fallback: use fast mode rate as approximation
        effective_full_rate
    };
    let rsync_seconds = (hex_dirs as f64 / effective_rsync_rate).max(0.1);

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

    // Detect filesystem type - NFS/SMB require different strategies
    let fs_type = detect_filesystem_type(cache_dir);
    let is_network_fs = fs_type.is_network();

    eprintln!("Filesystem type: {:?} (network: {})", fs_type, is_network_fs);

    // Find all hex directories (00-ff)
    // Cache structure can be either:
    //   /cache/XX/... (hex dirs directly under cache)
    //   /cache/steam/XX/... (service dirs containing hex dirs)
    let mut hex_dirs: Vec<std::path::PathBuf> = Vec::new();
    let mut service_dirs: Vec<std::path::PathBuf> = Vec::new();

    for entry in fs::read_dir(cache_dir)?.filter_map(|e| e.ok()) {
        let path = entry.path();
        if path.is_dir() {
            let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
            if is_hex(name) {
                // Direct hex directory under cache
                hex_dirs.push(path);
            } else if !name.starts_with('.') {
                // Service directory (steam, epic, etc.)
                service_dirs.push(path);
            }
        }
    }

    // If we found service directories, look for hex dirs inside them
    if hex_dirs.is_empty() && !service_dirs.is_empty() {
        eprintln!("Found {} service directories: {:?}",
            service_dirs.len(),
            service_dirs.iter().map(|p| p.file_name().unwrap_or_default().to_string_lossy()).collect::<Vec<_>>());

        for service_dir in &service_dirs {
            if let Ok(entries) = fs::read_dir(service_dir) {
                for entry in entries.filter_map(|e| e.ok()) {
                    let path = entry.path();
                    if path.is_dir() {
                        let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
                        if is_hex(name) {
                            hex_dirs.push(path);
                        }
                    }
                }
            }
        }
    }

    let total_hex_dirs = hex_dirs.len();
    eprintln!("Found {} hex directories to scan", total_hex_dirs);

    if total_hex_dirs == 0 {
        // Empty cache - still run calibration to measure filesystem performance
        eprintln!("Cache is empty, but running calibration to measure filesystem speeds...");

        let calibration = run_dynamic_calibration(cache_dir);

        // Build estimates info even for empty cache (shows filesystem capability)
        let estimates = if let Some(ref cal) = calibration {
            // Show what the filesystem CAN do, even with empty cache
            eprintln!("\nFilesystem calibration results (cache is empty):");
            for scenario in &cal.scenarios {
                eprintln!("  {} files/dir, depth {}: preserve={:.0}/s, fast={:.1}/s, rsync={:.1}/s",
                    scenario.files_per_dir, scenario.depth,
                    scenario.preserve_files_per_sec,
                    scenario.full_dirs_per_sec,
                    scenario.rsync_dirs_per_sec);
            }
            EstimatedDeletionTimes {
                preserve_seconds: 0.0,
                full_seconds: 0.0,
                rsync_seconds: 0.0,
                preserve_formatted: "< 1 second (empty cache)".to_string(),
                full_formatted: "< 1 second (empty cache)".to_string(),
                rsync_formatted: "< 1 second (empty cache)".to_string(),
            }
        } else {
            EstimatedDeletionTimes {
                preserve_seconds: 0.0,
                full_seconds: 0.0,
                rsync_seconds: 0.0,
                preserve_formatted: "< 1 second".to_string(),
                full_formatted: "< 1 second".to_string(),
                rsync_formatted: "< 1 second".to_string(),
            }
        };

        let result = CacheSizeResult {
            total_bytes: 0,
            total_files: 0,
            total_directories: 0,
            hex_directories: 0,
            scan_duration_ms: start_time.elapsed().as_millis() as u64,
            estimated_deletion_times: estimates,
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

    // Run dynamic calibration to measure actual filesystem performance
    eprintln!("\nRunning deletion speed calibration...");
    let estimates = if let Some(calibration) = run_dynamic_calibration(cache_dir) {
        estimate_deletion_times_dynamic(
            final_files,
            total_hex_dirs,
            &calibration,
        )
    } else {
        // Fallback if calibration fails - return minimal estimates
        eprintln!("Warning: Calibration failed, estimates may be inaccurate");
        EstimatedDeletionTimes {
            preserve_seconds: 1.0,
            full_seconds: 1.0,
            rsync_seconds: 1.0,
            preserve_formatted: "Unknown (calibration failed)".to_string(),
            full_formatted: "Unknown (calibration failed)".to_string(),
            rsync_formatted: "Unknown (calibration failed)".to_string(),
        }
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

    // Run dynamic calibration to measure actual network filesystem performance
    eprintln!("\nRunning deletion speed calibration on network filesystem...");
    let estimates = if let Some(calibration) = run_dynamic_calibration(cache_dir) {
        estimate_deletion_times_dynamic(
            total_files,
            hex_dir_count,
            &calibration,
        )
    } else {
        // Fallback if calibration fails - return minimal estimates
        eprintln!("Warning: Calibration failed, estimates may be inaccurate");
        EstimatedDeletionTimes {
            preserve_seconds: 1.0,
            full_seconds: 1.0,
            rsync_seconds: 1.0,
            preserve_formatted: "Unknown (calibration failed)".to_string(),
            full_formatted: "Unknown (calibration failed)".to_string(),
            rsync_formatted: "Unknown (calibration failed)".to_string(),
        }
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
