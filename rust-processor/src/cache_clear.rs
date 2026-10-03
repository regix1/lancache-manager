use anyhow::{Context, Result};
use clap::Parser;
use rayon::prelude::*;
use rayon::ThreadPoolBuilder;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::fs;
use std::io::{self, ErrorKind};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

#[cfg(target_os = "linux")]
use std::path::PathBuf;
#[cfg(target_os = "linux")]
use std::sync::OnceLock;
use std::time::Instant;

use cache_utils::{detect_filesystem_type, FilesystemType};
use lancache_processor::cache_repair;
use lancache_processor::cache_utils;
use lancache_processor::cancel;
use lancache_processor::progress_events;
use lancache_processor::progress_utils;
use progress_events::ProgressReporter;

/// Cache clear utility - clears all cache directories
#[derive(Parser, Debug)]
#[command(name = "cache_clear")]
#[command(about = "Clears the lancache cache directory")]
struct Args {
    /// Path to the cache directory
    cache_path: String,

    /// Path to write progress JSON file
    progress_json_path: String,

    /// Deletion mode: preserve, full, or rsync
    #[arg(default_value = "preserve")]
    delete_mode: Option<String>,

    /// Number of threads to use
    thread_count: Option<usize>,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,

    /// Operation that owns the durable cache-root receipt.
    #[arg(long = "operation-id")]
    operation_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct ProgressData {
    #[serde(rename = "isProcessing")]
    is_processing: bool,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: String,
    stage_key: String,
    context: serde_json::Value,
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
    /// Files the clear could not delete; it cleared everything else.
    undeleted_files: u64,
    /// The first file the clear could not delete; none when it deleted everything.
    #[serde(skip_serializing_if = "Option::is_none")]
    first_undeleted: Option<String>,
}

impl ProgressData {
    #[allow(clippy::too_many_arguments)]
    fn new(
        is_processing: bool,
        percent_complete: f64,
        status: String,
        stage_key: String,
        context: serde_json::Value,
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
            stage_key,
            context,
            directories_processed,
            total_directories,
            bytes_deleted,
            files_deleted,
            active_directories,
            active_count,
            timestamp: progress_utils::current_timestamp(),
            undeleted_files: 0,
            first_undeleted: None,
        }
    }
}

fn write_progress(progress_path: &Path, progress: &ProgressData) -> Result<()> {
    progress_utils::write_progress_json(progress_path, progress)
}

fn is_hex(value: &str) -> bool {
    value.len() == 2 && value.chars().all(|c| c.is_ascii_hexdigit())
}

fn delete_directory_contents(
    dir_path: &Path,
    files_counter: &AtomicU64,
    bytes_counter: &AtomicU64,
) -> Result<()> {
    let inspect = |entry: &fs::DirEntry| {
        let file_type = entry.file_type()?;
        let length = if file_type.is_dir() || file_type.is_symlink() {
            0
        } else {
            entry.metadata()?.len()
        };
        Ok((file_type, length))
    };
    delete_directory_contents_with(
        dir_path,
        files_counter,
        bytes_counter,
        &inspect,
        &|path: &Path| fs::remove_file(path),
        &|path: &Path| fs::remove_dir(path),
    )
}

/// Cache files one folder's clear could not delete. The rest of the folder is still cleared, so the
/// clear completes and names them instead of failing.
#[derive(Debug)]
struct UndeletedFiles {
    count: u64,
    first_path: std::path::PathBuf,
    first_error: io::Error,
}

impl std::fmt::Display for UndeletedFiles {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{} cache file(s) could not be deleted: failed to delete cache file {}: {}",
            self.count,
            self.first_path.display(),
            self.first_error
        )
    }
}

impl std::error::Error for UndeletedFiles {}

fn delete_directory_contents_with<I, F, D>(
    dir_path: &Path,
    files_counter: &AtomicU64,
    bytes_counter: &AtomicU64,
    inspect: &I,
    remove_file: &F,
    remove_dir: &D,
) -> Result<()>
where
    I: Fn(&fs::DirEntry) -> io::Result<(fs::FileType, u64)>,
    F: Fn(&Path) -> std::io::Result<()>,
    D: Fn(&Path) -> std::io::Result<()>,
{
    // Canonicalize the sweep root once. All deletions must live under it.
    let root = match dir_path.canonicalize() {
        Ok(root) => root,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(error) => {
            return Err(error)
                .with_context(|| format!("failed to resolve clear root {}", dir_path.display()));
        }
    };

    #[allow(clippy::too_many_arguments)]
    fn delete_recursive<I, F, D>(
        root: &Path,
        dir: &Path,
        files_counter: &AtomicU64,
        bytes_counter: &AtomicU64,
        inspect: &I,
        remove_file: &F,
        remove_dir: &D,
        failed_deletes: &mut Option<UndeletedFiles>,
    ) -> Result<()>
    where
        I: Fn(&fs::DirEntry) -> io::Result<(fs::FileType, u64)>,
        F: Fn(&Path) -> std::io::Result<()>,
        D: Fn(&Path) -> std::io::Result<()>,
    {
        let entries = match fs::read_dir(dir) {
            Ok(entries) => entries,
            Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
            Err(error) => {
                return Err(error)
                    .with_context(|| format!("failed to enumerate clear path {}", dir.display()));
            }
        };
        for entry_result in entries {
            let entry = match entry_result {
                Ok(entry) => entry,
                Err(error) if error.kind() == ErrorKind::NotFound => continue,
                Err(error) => {
                    return Err(error).with_context(|| {
                        format!("failed to read an entry under {}", dir.display())
                    });
                }
            };
            let path = entry.path();
            let (file_type, length) = match inspect(&entry) {
                Ok(inspection) => inspection,
                Err(error) if error.kind() == ErrorKind::NotFound => continue,
                Err(error) => {
                    return Err(error).with_context(|| {
                        format!("failed to inspect clear path {}", path.display())
                    });
                }
            };
            if file_type.is_symlink() {
                eprintln!(
                    "skipping unsafe path {}: symlink not allowed",
                    path.display()
                );
                continue;
            }

            match cache_utils::safe_path_under_root(root, &path) {
                Ok(_) => {}
                Err(error) if error.kind() == ErrorKind::NotFound => continue,
                Err(error) => {
                    return Err(error)
                        .with_context(|| format!("refusing unsafe clear path {}", path.display()));
                }
            }

            if file_type.is_dir() {
                delete_recursive(
                    root,
                    &path,
                    files_counter,
                    bytes_counter,
                    inspect,
                    remove_file,
                    remove_dir,
                    failed_deletes,
                )?;
                match remove_dir(&path) {
                    Ok(()) => {}
                    Err(error)
                        if matches!(
                            error.kind(),
                            ErrorKind::NotFound | ErrorKind::DirectoryNotEmpty
                        ) => {}
                    Err(error) => {
                        return Err(error).with_context(|| {
                            format!("failed to remove clear directory {}", path.display())
                        });
                    }
                }
            } else {
                match remove_file(&path) {
                    Ok(()) => {
                        files_counter.fetch_add(1, Ordering::Relaxed);
                        bytes_counter.fetch_add(length, Ordering::Relaxed);
                    }
                    Err(error) if error.kind() == ErrorKind::NotFound => {}
                    // One file that cannot be deleted must not leave the rest of the cache in
                    // place; the count and the first failure are reported once at the end.
                    Err(error) => match failed_deletes {
                        Some(failed) => failed.count += 1,
                        None => {
                            *failed_deletes = Some(UndeletedFiles {
                                count: 1,
                                first_path: path.clone(),
                                first_error: error,
                            });
                        }
                    },
                }
            }
        }
        Ok(())
    }

    let mut failed_deletes = None;
    delete_recursive(
        &root,
        &root,
        files_counter,
        bytes_counter,
        inspect,
        remove_file,
        remove_dir,
        &mut failed_deletes,
    )?;

    if let Some(failed) = failed_deletes {
        return Err(failed.into());
    }
    Ok(())
}

#[cfg(any(target_os = "linux", test))]
fn checked_file_totals<I>(dir_path: &Path, inspect: &I) -> Result<(u64, u64)>
where
    I: Fn(&fs::DirEntry) -> io::Result<(fs::FileType, u64)>,
{
    let mut files = 0_u64;
    let mut bytes = 0_u64;
    let mut pending = vec![dir_path.to_path_buf()];
    while let Some(directory) = pending.pop() {
        let entries = match fs::read_dir(&directory) {
            Ok(entries) => entries,
            Err(error) if error.kind() == ErrorKind::NotFound => continue,
            Err(error) => {
                return Err(error).with_context(|| {
                    format!("failed to enumerate clear path {}", directory.display())
                });
            }
        };
        for entry in entries {
            let entry = match entry {
                Ok(entry) => entry,
                Err(error) if error.kind() == ErrorKind::NotFound => continue,
                Err(error) => {
                    return Err(error).with_context(|| {
                        format!("failed to read an entry under {}", directory.display())
                    });
                }
            };
            let path = entry.path();
            let (file_type, length) = match inspect(&entry) {
                Ok(inspection) => inspection,
                Err(error) if error.kind() == ErrorKind::NotFound => continue,
                Err(error) => {
                    return Err(error).with_context(|| {
                        format!("failed to inspect clear path {}", path.display())
                    });
                }
            };
            if file_type.is_symlink() {
                continue;
            }
            if file_type.is_dir() {
                pending.push(path);
            } else if file_type.is_file() {
                files += 1;
                bytes = bytes.saturating_add(length);
            }
        }
    }
    Ok((files, bytes))
}

fn delete_directory_full(
    dir_path: &Path,
    files_counter: &AtomicU64,
    bytes_counter: &AtomicU64,
) -> Result<()> {
    let inspect = |entry: &fs::DirEntry| {
        let file_type = entry.file_type()?;
        let length = if file_type.is_dir() || file_type.is_symlink() {
            0
        } else {
            entry.metadata()?.len()
        };
        Ok((file_type, length))
    };
    delete_directory_full_with(
        dir_path,
        files_counter,
        bytes_counter,
        &inspect,
        &|path: &Path| fs::remove_file(path),
        &|path: &Path| fs::remove_dir(path),
    )
}

fn delete_directory_full_with<I, F, D>(
    dir_path: &Path,
    files_counter: &AtomicU64,
    bytes_counter: &AtomicU64,
    inspect: &I,
    remove_file: &F,
    remove_dir: &D,
) -> Result<()>
where
    I: Fn(&fs::DirEntry) -> io::Result<(fs::FileType, u64)>,
    F: Fn(&Path) -> io::Result<()>,
    D: Fn(&Path) -> io::Result<()>,
{
    delete_directory_contents_with(
        dir_path,
        files_counter,
        bytes_counter,
        inspect,
        remove_file,
        remove_dir,
    )?;

    // A hex folder that is a link keeps the link and its emptied target, as the default mode
    // does; removing a link with rmdir fails on Linux.
    if fs::symlink_metadata(dir_path).is_ok_and(|metadata| metadata.file_type().is_symlink()) {
        return Ok(());
    }

    match remove_dir(dir_path) {
        Ok(()) => Ok(()),
        Err(error)
            if matches!(
                error.kind(),
                ErrorKind::NotFound | ErrorKind::DirectoryNotEmpty
            ) =>
        {
            Ok(())
        }
        Err(error) => Err(error)
            .with_context(|| format!("failed to remove clear directory {}", dir_path.display())),
    }
}

#[cfg(target_os = "linux")]
fn delete_directory_rsync(
    dir_path: &Path,
    files_counter: &AtomicU64,
    bytes_counter: &AtomicU64,
) -> Result<()> {
    use std::process::Command;

    delete_directory_rsync_with(
        dir_path,
        files_counter,
        bytes_counter,
        |empty_dir, target_dir| {
            Command::new("rsync")
                .arg("-a")
                .arg("--delete")
                .arg("--stats")
                .arg(format!("{}/", empty_dir.display()))
                .arg(format!("{}/", target_dir.display()))
                .output()
        },
    )
}

#[cfg(target_os = "linux")]
fn delete_directory_rsync_with<E>(
    dir_path: &Path,
    files_counter: &AtomicU64,
    bytes_counter: &AtomicU64,
    execute: E,
) -> Result<()>
where
    E: FnOnce(&Path, &Path) -> io::Result<std::process::Output>,
{
    use std::env;

    if !dir_path
        .try_exists()
        .with_context(|| format!("failed to inspect rsync target {}", dir_path.display()))?
    {
        return Ok(());
    }

    let inspect = |entry: &fs::DirEntry| {
        let file_type = entry.file_type()?;
        let length = if file_type.is_dir() || file_type.is_symlink() {
            0
        } else {
            entry.metadata()?.len()
        };
        Ok((file_type, length))
    };
    let (file_count, byte_count) = checked_file_totals(dir_path, &inspect)?;

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
            EMPTY_TEMPLATE
                .get()
                .expect("empty template directory should be set")
        }
    };

    let output = execute(empty_dir, dir_path);

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

            if parse_rsync_deleted_files(&stdout).is_none() {
                eprintln!(
                    "Warning: Could not parse deleted file count from rsync stats for {}",
                    dir_path.display()
                );
            }

            // Rsync reports estimates from the pre-pass. The later repair scan owns absence proof.
            files_counter.fetch_add(file_count, Ordering::Relaxed);
            bytes_counter.fetch_add(byte_count, Ordering::Relaxed);

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
    _bytes_counter: &AtomicU64,
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
            eprintln!(
                "  Found 'Number of deleted files:' with value: {}",
                value_part
            );
            if let Ok(value) = value_part.replace(",", "").parse::<u64>() {
                return Some(value);
            }
        }

        // Alternative format: "deleted: 12345"
        if trimmed.to_lowercase().starts_with("deleted:")
            || trimmed.to_lowercase().contains("files deleted:")
        {
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

fn clear_cache(
    cache_path: &str,
    progress_path: &Path,
    thread_count: usize,
    delete_mode: &str,
    operation_id: Option<&str>,
    reporter: &Arc<ProgressReporter>,
) -> Result<(usize, usize)> {
    clear_cache_with(
        cache_path,
        progress_path,
        thread_count,
        delete_mode,
        operation_id,
        reporter,
        |_| {},
    )
}

#[allow(clippy::too_many_arguments)]
fn clear_cache_with<F>(
    cache_path: &str,
    progress_path: &Path,
    thread_count: usize,
    delete_mode: &str,
    operation_id: Option<&str>,
    reporter: &Arc<ProgressReporter>,
    after_delete: F,
) -> Result<(usize, usize)>
where
    F: FnOnce(&Path),
{
    let start_time = Instant::now();
    eprintln!("Starting cache clear operation...");
    eprintln!("Cache path: {}", cache_path);
    eprintln!("Deletion mode: {}", delete_mode);

    // File-write-before-stdout-emit invariant: C#'s event callback reads the progress file
    // the moment "started" arrives, and the C#-created temp file is empty until our first
    // write - seed it before emitting so that read never sees empty (unparseable) JSON.
    // Total directory count isn't known yet; the real initial tick below overwrites this.
    let starting = ProgressData::new(
        true,
        0.0,
        "running".to_string(),
        "signalr.cacheClear.starting".to_string(),
        json!({}),
        0,
        0,
        0,
        0,
        Vec::new(),
    );
    if let Err(e) = write_progress(progress_path, &starting) {
        eprintln!("Warning: failed to seed progress file: {:#}", e);
    }

    // Emit started event
    reporter.emit_started("signalr.cacheClear.starting", json!({}));

    let cache_dir = Path::new(cache_path);
    if !cache_dir
        .try_exists()
        .with_context(|| format!("failed to inspect cache directory {}", cache_dir.display()))?
    {
        let msg = format!("Cache directory does not exist: {}", cache_path);
        reporter.emit_failed(
            "signalr.cacheClear.error.dirNotFound",
            json!({ "cachePath": cache_path }),
            Some(msg.clone()),
        );
        anyhow::bail!("{}", msg);
    }

    // Find all hex directories (00-ff) without hiding enumeration or inspection failures.
    let mut hex_dirs = Vec::new();
    let entries = fs::read_dir(cache_dir)
        .with_context(|| format!("failed to enumerate cache root {}", cache_dir.display()))?;
    for entry in entries {
        let entry = entry
            .with_context(|| format!("failed to read an entry under {}", cache_dir.display()))?;
        let path = entry.path();
        let file_type = entry
            .file_type()
            .with_context(|| format!("failed to inspect cache path {}", path.display()))?;
        if !entry.file_name().to_str().map(is_hex).unwrap_or(false) {
            continue;
        }
        // A hex folder can be a link to another disk; its target is cleared like a real folder.
        let is_dir = if file_type.is_symlink() {
            match cache_utils::linked_hex_folder_target(&path) {
                Ok(target) => target.is_some(),
                // A link whose target is gone has nothing to clear.
                Err(error) if error.kind() == ErrorKind::NotFound => false,
                Err(error) => {
                    return Err(error).with_context(|| {
                        format!("failed to inspect linked cache path {}", path.display())
                    });
                }
            }
        } else {
            file_type.is_dir()
        };
        if is_dir {
            hex_dirs.push(path);
        }
    }

    let total_dirs = hex_dirs.len();
    eprintln!("Found {} cache files to clear", total_dirs);

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
        "signalr.cacheClear.starting".to_string(),
        json!({}),
        0,
        total_dirs,
        0,
        0,
        Vec::new(),
    );
    write_progress(progress_path, &progress)?;

    cache_repair::prepare_receipt(cache_dir, operation_id)?;

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
    let progress_enabled = reporter.is_enabled();
    let workers_done = Arc::new(AtomicBool::new(false));
    let done_for_monitor = Arc::clone(&workers_done);
    // Owned handle for the monitor thread so it can call the shared ProgressReporter
    // methods directly instead of hand-rolling the JSON envelope itself.
    let reporter_for_monitor = Arc::clone(reporter);

    // Start a background thread to update progress regularly
    let monitor_handle = std::thread::spawn(move || {
        let mut last_update = Instant::now();
        loop {
            std::thread::sleep(std::time::Duration::from_millis(500));

            let processed = dirs_for_monitor.load(Ordering::Relaxed);
            let bytes = bytes_for_monitor.load(Ordering::Relaxed);
            let files = files_for_monitor.load(Ordering::Relaxed);

            // Stop monitoring when all dirs are done OR a cancel was requested.
            // On cancel the worker closures skip remaining dirs without advancing
            // `processed`, so without this check the monitor would loop forever and
            // `monitor_handle.join()` below would deadlock instead of exiting 0.
            if done_for_monitor.load(Ordering::Relaxed) || cancel::is_cancelled() {
                break; // All done, or cancellation requested
            }

            if last_update.elapsed().as_millis() > 500 {
                let percent = (processed as f64 / total_dirs as f64) * 100.0;

                // Get snapshot of active directories. A poisoned lock still holds a valid
                // list of names, so recover it rather than reporting "nothing is running".
                let active_snapshot = active_for_monitor
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner())
                    .clone();
                let active_count = active_snapshot.len();

                // Log active directories if any
                if active_count > 0 {
                    eprintln!(
                        "Active: {} directories being processed: [{}]",
                        active_count,
                        active_snapshot.join(", ")
                    );
                }

                let progress = ProgressData::new(
                    true,
                    percent,
                    "running".to_string(),
                    "signalr.cacheClear.progress".to_string(),
                    json!({ "processed": processed, "totalDirs": total_dirs, "activeCount": active_count }),
                    processed,
                    total_dirs,
                    bytes,
                    files,
                    active_snapshot,
                );

                if let Err(e) = write_progress(&progress_path_clone, &progress) {
                    eprintln!("Warning: Failed to write progress: {}", e);
                }

                // File write happens before the stdout emit so a stdout-triggered C#
                // file read is never stale (mirrors cache_game_detect.rs's ordering).
                if progress_enabled {
                    reporter_for_monitor.emit_progress(
                        percent.clamp(0.0, 100.0),
                        "signalr.cacheClear.progress",
                        json!({ "processed": processed, "totalDirs": total_dirs, "activeCount": active_count }),
                    );
                }

                last_update = Instant::now();
            }
        }
    });

    // Process directories in parallel using rayon with limited thread pool. A directory that
    // fails does not stop the others; the first failure is reported once after all of them.
    let active_for_workers = Arc::clone(&active_dirs);
    let failures = Mutex::new(Vec::<anyhow::Error>::new());
    pool.install(|| {
        hex_dirs.par_iter().for_each(|dir| {
            // Cooperative cancellation: skip new hex-dirs if cancel was requested.
            // An in-flight filesystem call finishes; no new directory starts.
            if cancel::is_cancelled() {
                return;
            }

            let dir_name = dir
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("unknown");
            let dir_name_str = dir_name.to_string();

            // Add to active list
            active_for_workers
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .push(dir_name_str.clone());

            eprintln!("Processing directory {}", dir_name);

            let result = match delete_mode {
                "full" => delete_directory_full(dir, &total_files_deleted, &total_bytes_deleted),
                "rsync" => delete_directory_rsync(dir, &total_files_deleted, &total_bytes_deleted),
                _ => delete_directory_contents(dir, &total_files_deleted, &total_bytes_deleted),
            };

            // Remove from active list
            active_for_workers
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .retain(|d| d != &dir_name_str);

            if let Err(error) = result {
                // A folder that only left files it could not delete is still cleared; it counts as
                // processed and its files are named at the end.
                let undeleted = error.is::<UndeletedFiles>();
                failures
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner())
                    .push(error);
                if !undeleted {
                    return;
                }
            }
            let processed = dirs_processed.fetch_add(1, Ordering::Relaxed) + 1;
            eprintln!(
                "Completed directory {} ({}/{})",
                dir_name, processed, total_dirs
            );
        })
    });
    workers_done.store(true, Ordering::Relaxed);
    let failures = failures
        .into_inner()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    // Files a folder could not delete leave the rest of the cache cleared, so the clear completes
    // and names them; any other failure left a folder uncleared and fails the clear.
    let mut undeleted_files = 0_u64;
    let mut first_undeleted = None;
    let mut other_failures = Vec::new();
    for failure in failures {
        match failure.downcast::<UndeletedFiles>() {
            Ok(undeleted) => {
                undeleted_files += undeleted.count;
                first_undeleted.get_or_insert_with(|| undeleted.first_path.display().to_string());
            }
            Err(failure) => other_failures.push(failure),
        }
    }
    let failed_dirs = other_failures.len();
    let deletion_result = match other_failures.into_iter().next() {
        Some(first_failure) => Err(first_failure.context(format!(
            "{failed_dirs} of {total_dirs} cache directories could not be fully cleared"
        ))),
        None => Ok(()),
    };

    // Wait for monitor thread to finish
    monitor_handle
        .join()
        .map_err(|_| anyhow::anyhow!("cache clear progress monitor stopped unexpectedly"))?;

    // If cancellation was requested: flush a partial progress event and exit 0.
    // An in-flight filesystem call may have finished; no new dirs were started after the flag.
    if cancel::is_cancelled() {
        let processed = dirs_processed.load(Ordering::Relaxed);
        let percent = if total_dirs > 0 {
            (processed as f64 / total_dirs as f64) * 100.0
        } else {
            0.0
        };
        eprintln!(
            "Cancellation confirmed — processed {}/{} hex dirs, exiting.",
            processed, total_dirs
        );
        let progress = ProgressData::new(
            true,
            percent,
            "running".to_string(),
            "signalr.cacheClear.progress".to_string(),
            json!({ "processed": processed, "totalDirs": total_dirs, "activeCount": 0usize }),
            processed,
            total_dirs,
            total_bytes_deleted.load(Ordering::Relaxed),
            total_files_deleted.load(Ordering::Relaxed),
            Vec::new(),
        );
        let _ = write_progress(progress_path, &progress);
        // File write happens before the stdout emit (same ordering as the monitor
        // thread above); reporter.emit_progress reads its own operation_id, avoiding
        // a re-borrow of the String already moved into the monitor thread's closure.
        reporter.emit_progress(
            percent.clamp(0.0, 100.0),
            "signalr.cacheClear.progress",
            json!({ "processed": processed, "totalDirs": total_dirs, "activeCount": 0usize }),
        );
        std::process::exit(0);
    }

    if let Err(error) = deletion_result {
        let processed = dirs_processed.load(Ordering::Relaxed);
        let total_bytes = total_bytes_deleted.load(Ordering::Relaxed);
        let total_files = total_files_deleted.load(Ordering::Relaxed);
        let percent = if total_dirs > 0 {
            (processed as f64 / total_dirs as f64) * 100.0
        } else {
            0.0
        };
        let error_detail = format!("{error:#}");
        let failed = ProgressData::new(
            false,
            percent,
            "failed".to_string(),
            "signalr.cacheClear.error.fatal".to_string(),
            json!({ "errorDetail": error_detail }),
            processed,
            total_dirs,
            total_bytes,
            total_files,
            Vec::new(),
        );
        let _ = write_progress(progress_path, &failed);
        return Err(error);
    }

    after_delete(cache_dir);

    let final_dirs = dirs_processed.load(Ordering::Relaxed);
    let final_bytes = total_bytes_deleted.load(Ordering::Relaxed);
    let final_files = total_files_deleted.load(Ordering::Relaxed);
    let elapsed = start_time.elapsed();

    eprintln!("\nCache clear completed!");
    eprintln!("  Directories processed: {}", final_dirs);
    eprintln!("  Files deleted: {}", final_files);
    eprintln!(
        "  Bytes deleted: {} ({:.2} GB)",
        final_bytes,
        final_bytes as f64 / 1_073_741_824.0
    );
    eprintln!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());
    if undeleted_files > 0 {
        eprintln!("  Files that could not be deleted: {}", undeleted_files);
    }

    // Final progress
    let mut progress = ProgressData::new(
        false,
        100.0,
        "completed".to_string(),
        "signalr.cacheClear.progress".to_string(),
        json!({ "processed": final_dirs, "totalDirs": total_dirs, "activeCount": 0usize }),
        final_dirs,
        total_dirs,
        final_bytes,
        final_files,
        Vec::new(),
    );
    progress.undeleted_files = undeleted_files;
    progress.first_undeleted = first_undeleted;
    write_progress(progress_path, &progress)?;

    Ok((final_dirs, total_dirs))
}

/// Determine optimal thread count based on delete mode, filesystem type, and available CPUs
/// For network filesystems (NFS/SMB), parallelism is significantly reduced as it often
/// hurts rather than helps performance due to network round-trip overhead
fn get_optimal_thread_count(delete_mode: &str, fs_type: FilesystemType) -> usize {
    let cpu_count = std::thread::available_parallelism()
        .map(|p| p.get())
        .unwrap_or(4);

    // Network filesystems: parallelism often HURTS performance
    // Each unlink/rmdir requires a network round-trip
    // Reference: https://www.baeldung.com/linux/delete-large-directory
    if fs_type.is_network() {
        return match delete_mode {
            // Rsync is most efficient on NFS - can use moderate parallelism
            // as each rsync process handles a whole directory tree
            "rsync" => std::cmp::min(cpu_count, 4),
            // Full mode uses a counted recursive walk - keep network churn bounded.
            "full" => 2,
            // Preserve mode is VERY slow on NFS - minimal parallelism
            // to avoid overwhelming the NFS server with unlink() calls
            _ => 2,
        };
    }

    // Local filesystems: can use higher parallelism
    match delete_mode {
        // Full mode removes files and then empty directories with exact unlink counters.
        // Use fewer threads to avoid overwhelming the filesystem.
        "full" => std::cmp::min(cpu_count, 8),

        // Rsync mode - each rsync process is independent
        // Can use moderate parallelism
        "rsync" => std::cmp::min(cpu_count, 6),

        // Preserve mode does individual file deletes - I/O bound
        // Use high parallelism to maximize throughput on SSDs
        _ => std::cmp::min(cpu_count * 2, 16),
    }
}

fn main() -> anyhow::Result<()> {
    cancel::install();
    let args = Args::parse();

    let cache_path = &args.cache_path;
    let progress_path = Path::new(&args.progress_json_path);

    // Create progress reporter, wrapped in Arc so the monitor thread inside
    // clear_cache can hold its own owned handle without cloning ProgressReporter.
    let reporter = Arc::new(ProgressReporter::new(args.progress));

    // Detect filesystem type for optimal configuration
    let cache_dir = Path::new(cache_path);
    let fs_type = detect_filesystem_type(cache_dir);
    let is_network_fs = fs_type.is_network();

    eprintln!(
        "Filesystem type: {:?} (network: {})",
        fs_type, is_network_fs
    );

    // Get delete mode, with recommendation for network filesystems
    let delete_mode = if let Some(ref mode) = args.delete_mode {
        // Warn if using suboptimal mode on network filesystem
        if is_network_fs && mode == "preserve" {
            eprintln!("Warning: 'preserve' mode is VERY slow on NFS/SMB filesystems.");
            eprintln!("  Consider using 'rsync' mode instead for much better performance.");
            eprintln!("  rsync empty-directory trick is ~3x faster on network storage.");
        }
        mode.as_str()
    } else if is_network_fs {
        // Default to rsync for network filesystems on Linux
        #[cfg(target_os = "linux")]
        {
            eprintln!(
                "Network filesystem detected - defaulting to 'rsync' mode (optimal for NFS/SMB)"
            );
            "rsync"
        }
        #[cfg(not(target_os = "linux"))]
        {
            eprintln!("Network filesystem detected - using 'full' mode (rsync not available on this platform)");
            "full"
        }
    } else {
        "preserve"
    };

    // Thread count: use provided value or auto-detect based on mode and filesystem
    let thread_count = args
        .thread_count
        .unwrap_or_else(|| get_optimal_thread_count(delete_mode, fs_type));

    if is_network_fs {
        eprintln!(
            "Using reduced parallelism ({} threads) for network filesystem",
            thread_count
        );
    }

    eprintln!("Thread count: {} (mode: {})", thread_count, delete_mode);

    match clear_cache(
        cache_path,
        progress_path,
        thread_count,
        delete_mode,
        args.operation_id.as_deref(),
        &reporter,
    ) {
        Ok((final_dirs, total_dirs)) => {
            // Same final counts the last write_progress call persisted to the file -
            // no more hardcoded zeros on the stdout complete event.
            reporter.emit_complete(
                "signalr.cacheClear.progress",
                json!({ "processed": final_dirs, "totalDirs": total_dirs, "activeCount": 0usize }),
            );
            Ok(())
        }
        Err(e) => {
            eprintln!("Error: {:?}", e);
            let error_detail = format!("{e:#}");
            reporter.emit_failed(
                "signalr.cacheClear.error.fatal",
                json!({ "errorDetail": error_detail }),
                Some(error_detail.clone()),
            );
            let previous = fs::read(progress_path)
                .ok()
                .and_then(|bytes| serde_json::from_slice::<ProgressData>(&bytes).ok());
            let error_progress = ProgressData::new(
                false,
                previous
                    .as_ref()
                    .map(|progress| progress.percent_complete)
                    .unwrap_or(0.0),
                "failed".to_string(),
                "signalr.cacheClear.error.fatal".to_string(),
                json!({ "errorDetail": error_detail }),
                previous
                    .as_ref()
                    .map(|progress| progress.directories_processed)
                    .unwrap_or(0),
                previous
                    .as_ref()
                    .map(|progress| progress.total_directories)
                    .unwrap_or(0),
                previous
                    .as_ref()
                    .map(|progress| progress.bytes_deleted)
                    .unwrap_or(0),
                previous
                    .as_ref()
                    .map(|progress| progress.files_deleted)
                    .unwrap_or(0),
                Vec::new(),
            );
            let _ = write_progress(progress_path, &error_progress);
            Err(e)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const FIRST_DIGEST: &str = "0123456789abcdef0123456789abcdef";
    const NEW_DIGEST: &str = "fedcba9876543210fedcba987654cdef";

    fn inspect_entry(entry: &fs::DirEntry) -> io::Result<(fs::FileType, u64)> {
        let file_type = entry.file_type()?;
        let length = if file_type.is_dir() || file_type.is_symlink() {
            0
        } else {
            entry.metadata()?.len()
        };
        Ok((file_type, length))
    }

    fn create_cache_file(root: &Path, digest: &str, contents: &[u8]) -> std::path::PathBuf {
        let path = root
            .join(&digest[30..32])
            .join(&digest[28..30])
            .join(digest);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, contents).unwrap();
        path
    }

    #[cfg(target_os = "linux")]
    static RSYNC_TEST_LOCK: Mutex<()> = Mutex::new(());

    #[cfg(target_os = "linux")]
    fn run_rsync(empty_dir: &Path, target_dir: &Path) -> io::Result<std::process::Output> {
        std::process::Command::new("rsync")
            .arg("-a")
            .arg("--delete")
            .arg("--stats")
            .arg(format!("{}/", empty_dir.display()))
            .arg(format!("{}/", target_dir.display()))
            .output()
    }

    #[test]
    fn preserve_mode_reports_only_successful_unlinks() {
        let root = tempfile::tempdir().unwrap();
        let directory = root.path().join("00");
        fs::create_dir_all(&directory).unwrap();
        let first = directory.join("first");
        let second = directory.join("second");
        fs::write(&first, b"one").unwrap();
        fs::write(&second, b"second").unwrap();
        let remove_calls = AtomicUsize::new(0);
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        let error = delete_directory_contents_with(
            &directory,
            &files,
            &bytes,
            &inspect_entry,
            &|path: &Path| {
                if remove_calls.fetch_add(1, Ordering::SeqCst) == 0 {
                    fs::remove_file(path)
                } else {
                    Err(std::io::Error::new(
                        ErrorKind::Other,
                        "injected unlink failure",
                    ))
                }
            },
            &|path: &Path| fs::remove_dir(path),
        )
        .unwrap_err();

        let removed_bytes = [(&first, 3_u64), (&second, 6_u64)]
            .into_iter()
            .filter_map(|(path, length)| (!path.exists()).then_some(length))
            .sum::<u64>();
        let message = format!("{error:#}");
        assert_eq!(
            error
                .downcast_ref::<UndeletedFiles>()
                .map(|failed| failed.count),
            Some(1)
        );
        assert!(message.contains("1 cache file(s) could not be deleted"));
        assert!(message.contains("failed to delete cache file"));
        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), removed_bytes);
    }

    #[test]
    fn a_file_that_cannot_be_deleted_does_not_stop_the_other_deletes() {
        let root = tempfile::tempdir().unwrap();
        let directory = root.path().join("00");
        let paths = [
            directory.join("first"),
            directory.join("11").join("second"),
            directory.join("22").join("third"),
        ];
        for path in &paths {
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, b"cache").unwrap();
        }
        let failed_path = std::cell::RefCell::new(None::<std::path::PathBuf>);
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        let error = delete_directory_contents_with(
            &directory,
            &files,
            &bytes,
            &inspect_entry,
            &|path: &Path| {
                let mut failed_path = failed_path.borrow_mut();
                if failed_path.is_none() {
                    *failed_path = Some(path.to_path_buf());
                    return Err(io::Error::new(
                        ErrorKind::PermissionDenied,
                        "injected unlink failure",
                    ));
                }
                fs::remove_file(path)
            },
            &|path: &Path| fs::remove_dir(path),
        )
        .unwrap_err();

        let failed_path = failed_path.into_inner().unwrap();
        let message = format!("{error:#}");
        assert_eq!(
            error
                .downcast_ref::<UndeletedFiles>()
                .map(|failed| failed.count),
            Some(1)
        );
        assert!(failed_path.exists());
        assert_eq!(paths.iter().filter(|path| path.exists()).count(), 1);
        assert_eq!(files.load(Ordering::SeqCst), 2);
        assert_eq!(bytes.load(Ordering::SeqCst), 10);
        assert!(message.contains("1 cache file(s) could not be deleted"));
        assert!(message.contains(&failed_path.display().to_string()));
    }

    #[test]
    fn the_final_progress_names_the_files_a_clear_could_not_delete() {
        let mut progress = ProgressData::new(
            false,
            100.0,
            "completed".to_string(),
            "signalr.cacheClear.progress".to_string(),
            json!({ "processed": 1, "totalDirs": 1, "activeCount": 0 }),
            1,
            1,
            0,
            0,
            Vec::new(),
        );
        progress.undeleted_files = 2;
        progress.first_undeleted = Some("/cache/00/ab/first".to_string());

        let value = serde_json::to_value(&progress).unwrap();

        assert_eq!(value["undeletedFiles"], 2);
        assert_eq!(value["firstUndeleted"], "/cache/00/ab/first");
    }

    #[cfg(unix)]
    #[test]
    fn linked_hex_folders_are_cleared_in_the_default_and_full_modes() {
        for mode in ["preserve", "full"] {
            let root = tempfile::tempdir().unwrap();
            let target = tempfile::tempdir().unwrap();
            let cached = create_cache_file(target.path(), FIRST_DIGEST, b"old");
            let link = root.path().join("00");
            std::os::unix::fs::symlink(target.path(), &link).unwrap();
            let progress_path = root.path().join("progress.json");
            let reporter = Arc::new(ProgressReporter::new(false));

            let result = clear_cache_with(
                root.path().to_str().unwrap(),
                &progress_path,
                1,
                mode,
                None,
                &reporter,
                |_| {},
            )
            .unwrap_or_else(|error| panic!("{mode} clear of a linked hex folder: {error:#}"));

            assert_eq!(result, (1, 1), "{mode}");
            assert!(!cached.exists(), "{mode}");
            assert!(
                fs::symlink_metadata(&link)
                    .unwrap()
                    .file_type()
                    .is_symlink(),
                "{mode}"
            );
            assert!(target.path().is_dir(), "{mode}");
        }
    }

    #[test]
    fn file_vanishing_during_inspection_is_not_counted() {
        let root = tempfile::tempdir().unwrap();
        let directory = root.path().join("00");
        fs::create_dir_all(&directory).unwrap();
        let vanished = directory.join("vanished");
        let remaining = directory.join("remaining");
        fs::write(&vanished, b"gone").unwrap();
        fs::write(&remaining, b"remain").unwrap();
        let vanished = vanished.canonicalize().unwrap();
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        delete_directory_contents_with(
            &directory,
            &files,
            &bytes,
            &|entry: &fs::DirEntry| {
                if entry.path() == vanished {
                    fs::remove_file(entry.path())?;
                    return Err(io::Error::new(
                        ErrorKind::NotFound,
                        "injected inspection disappearance",
                    ));
                }
                inspect_entry(entry)
            },
            &|path: &Path| fs::remove_file(path),
            &|path: &Path| fs::remove_dir(path),
        )
        .expect("inspection disappearance must be benign");

        assert!(!vanished.exists());
        assert!(!remaining.exists());
        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), 6);
    }

    #[test]
    fn file_vanishing_during_unlink_is_not_counted() {
        let root = tempfile::tempdir().unwrap();
        let directory = root.path().join("00");
        fs::create_dir_all(&directory).unwrap();
        let vanished = directory.join("vanished");
        let remaining = directory.join("remaining");
        fs::write(&vanished, b"gone").unwrap();
        fs::write(&remaining, b"remain").unwrap();
        let vanished = vanished.canonicalize().unwrap();
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        delete_directory_contents_with(
            &directory,
            &files,
            &bytes,
            &inspect_entry,
            &|path: &Path| {
                if path == vanished {
                    fs::remove_file(path)?;
                    return Err(io::Error::new(
                        ErrorKind::NotFound,
                        "injected unlink disappearance",
                    ));
                }
                fs::remove_file(path)
            },
            &|path: &Path| fs::remove_dir(path),
        )
        .expect("unlink disappearance must be benign");

        assert!(!vanished.exists());
        assert!(!remaining.exists());
        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), 6);
    }

    #[test]
    fn directory_not_empty_races_preserve_new_files_and_allow_the_next_root() {
        let child_root = tempfile::tempdir().unwrap();
        let original = create_cache_file(child_root.path(), FIRST_DIGEST, b"old");
        let child = original.parent().unwrap().canonicalize().unwrap();
        let sweep = child.parent().unwrap().to_path_buf();
        let new_in_child = child.join(NEW_DIGEST);
        let created_in_child = std::cell::Cell::new(false);
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        delete_directory_full_with(
            &sweep,
            &files,
            &bytes,
            &inspect_entry,
            &|path: &Path| fs::remove_file(path),
            &|path: &Path| {
                if path == child && !created_in_child.replace(true) {
                    fs::write(&new_in_child, b"new")?;
                }
                fs::remove_dir(path)
            },
        )
        .expect("child writer race must be benign");

        assert!(new_in_child.exists());
        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), 3);

        let top_root = tempfile::tempdir().unwrap();
        let original = create_cache_file(top_root.path(), FIRST_DIGEST, b"old");
        let sweep = original
            .parent()
            .unwrap()
            .parent()
            .unwrap()
            .canonicalize()
            .unwrap();
        let new_at_top = sweep.join("11").join(NEW_DIGEST);
        let created_at_top = std::cell::Cell::new(false);
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        delete_directory_full_with(
            &sweep,
            &files,
            &bytes,
            &inspect_entry,
            &|path: &Path| fs::remove_file(path),
            &|path: &Path| {
                if path == sweep && !created_at_top.replace(true) {
                    fs::create_dir_all(new_at_top.parent().unwrap())?;
                    fs::write(&new_at_top, b"new")?;
                }
                fs::remove_dir(path)
            },
        )
        .expect("top writer race must be benign");

        assert!(new_at_top.exists());
        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), 3);

        let next_root = tempfile::tempdir().unwrap();
        let next = create_cache_file(next_root.path(), FIRST_DIGEST, b"next");
        let next_sweep = next.parent().unwrap().parent().unwrap().to_path_buf();
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        delete_directory_full(&next_sweep, &files, &bytes).expect("a later root must still run");

        assert!(!next_sweep.exists());
        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), 4);
    }

    #[test]
    fn full_mode_keeps_successful_unlink_counts_before_a_later_error() {
        let root = tempfile::tempdir().unwrap();
        let directory = root.path().join("00");
        fs::create_dir_all(&directory).unwrap();
        let first = directory.join("first");
        let second = directory.join("second");
        fs::write(&first, b"one").unwrap();
        fs::write(&second, b"second").unwrap();
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        let error = delete_directory_full_with(
            &directory,
            &files,
            &bytes,
            &inspect_entry,
            &|path: &Path| fs::remove_file(path),
            &|_| {
                Err(io::Error::new(
                    ErrorKind::Other,
                    "injected directory removal failure",
                ))
            },
        )
        .unwrap_err();

        assert!(error
            .to_string()
            .contains("failed to remove clear directory"));
        assert!(!first.exists());
        assert!(!second.exists());
        assert_eq!(files.load(Ordering::SeqCst), 2);
        assert_eq!(bytes.load(Ordering::SeqCst), 9);
    }

    #[test]
    fn rsync_precount_ignores_a_file_that_vanishes_during_inspection() {
        let root = tempfile::tempdir().unwrap();
        let directory = root.path().join("00");
        fs::create_dir_all(&directory).unwrap();
        let vanished = directory.join("vanished");
        let remaining = directory.join("remaining");
        fs::write(&vanished, b"gone").unwrap();
        fs::write(&remaining, b"remain").unwrap();

        let totals = checked_file_totals(&directory, &|entry: &fs::DirEntry| {
            if entry.path() == vanished {
                fs::remove_file(entry.path())?;
                return Err(io::Error::new(
                    ErrorKind::NotFound,
                    "injected precount disappearance",
                ));
            }
            inspect_entry(entry)
        })
        .expect("precount disappearance must be benign");

        assert_eq!(totals, (1, 6));
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn rsync_keeps_a_file_created_after_the_command_and_allows_the_next_root() {
        let _guard = RSYNC_TEST_LOCK.lock().unwrap();
        let first_root = tempfile::tempdir().unwrap();
        let original = create_cache_file(first_root.path(), FIRST_DIGEST, b"old");
        let first_sweep = original.parent().unwrap().parent().unwrap().to_path_buf();
        let new_file = first_sweep.join(NEW_DIGEST);
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        delete_directory_rsync_with(&first_sweep, &files, &bytes, |empty_dir, target_dir| {
            let output = run_rsync(empty_dir, target_dir)?;
            if output.status.success() {
                fs::write(&new_file, b"new")?;
            }
            Ok(output)
        })
        .expect("post-rsync writer must not fail the operation");

        assert!(new_file.exists());
        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), 3);

        let next_root = tempfile::tempdir().unwrap();
        let next = create_cache_file(next_root.path(), FIRST_DIGEST, b"next");
        let next_sweep = next.parent().unwrap().parent().unwrap().to_path_buf();
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        delete_directory_rsync_with(&next_sweep, &files, &bytes, run_rsync)
            .expect("a later rsync root must still run");

        assert_eq!(files.load(Ordering::SeqCst), 1);
        assert_eq!(bytes.load(Ordering::SeqCst), 4);
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn rsync_spawn_failure_remains_fatal() {
        let _guard = RSYNC_TEST_LOCK.lock().unwrap();
        let root = tempfile::tempdir().unwrap();
        let original = create_cache_file(root.path(), FIRST_DIGEST, b"old");
        let sweep = original.parent().unwrap().parent().unwrap().to_path_buf();
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        let error = delete_directory_rsync_with(&sweep, &files, &bytes, |_, _| {
            Err(io::Error::new(
                ErrorKind::NotFound,
                "injected rsync spawn failure",
            ))
        })
        .unwrap_err();

        assert!(error
            .to_string()
            .contains("rsync command not available or failed"));
        assert_eq!(files.load(Ordering::SeqCst), 0);
        assert_eq!(bytes.load(Ordering::SeqCst), 0);
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn rsync_nonzero_exit_remains_fatal() {
        use std::os::unix::process::ExitStatusExt;

        let _guard = RSYNC_TEST_LOCK.lock().unwrap();
        let root = tempfile::tempdir().unwrap();
        let original = create_cache_file(root.path(), FIRST_DIGEST, b"old");
        let sweep = original.parent().unwrap().parent().unwrap().to_path_buf();
        let files = AtomicU64::new(0);
        let bytes = AtomicU64::new(0);

        let error = delete_directory_rsync_with(&sweep, &files, &bytes, |_, _| {
            Ok(std::process::Output {
                status: std::process::ExitStatus::from_raw(1 << 8),
                stdout: Vec::new(),
                stderr: b"injected rsync failure".to_vec(),
            })
        })
        .unwrap_err();

        assert!(error.to_string().contains("rsync failed"));
        assert_eq!(files.load(Ordering::SeqCst), 0);
        assert_eq!(bytes.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn files_created_after_deletion_do_not_stop_the_next_root() {
        let roots = [tempfile::tempdir().unwrap(), tempfile::tempdir().unwrap()];
        let mut completed = 0usize;
        let mut surviving = None;

        for (index, root) in roots.iter().enumerate() {
            let original = root.path().join("00").join("00").join(FIRST_DIGEST);
            fs::create_dir_all(original.parent().unwrap()).unwrap();
            fs::write(&original, b"old").unwrap();
            let progress_path = root.path().join("progress.json");
            let reporter = Arc::new(ProgressReporter::new(false));
            let operation_id = uuid::Uuid::new_v4().to_string();
            let mut created = None;

            let result = clear_cache_with(
                root.path().to_str().unwrap(),
                &progress_path,
                1,
                "preserve",
                Some(&operation_id),
                &reporter,
                |cache_root| {
                    if index == 0 {
                        let path = cache_root.join("00").join("11").join(NEW_DIGEST);
                        fs::create_dir_all(path.parent().unwrap()).unwrap();
                        fs::write(&path, b"new").unwrap();
                        created = Some(path);
                    }
                },
            )
            .expect("clear root while a writer creates a later file");
            let progress: ProgressData =
                serde_json::from_slice(&fs::read(&progress_path).unwrap()).unwrap();

            assert_eq!(result, (1, 1));
            assert_eq!(progress.status, "completed");
            assert_eq!(progress.files_deleted, 1);
            assert_eq!(progress.bytes_deleted, 3);
            completed += 1;
            if index == 0 {
                surviving = created;
            }
        }

        assert_eq!(completed, 2);
        assert!(surviving.unwrap().exists());
    }

    #[test]
    fn operation_id_is_optional_and_accepts_uuid_text() {
        let omitted = Args::try_parse_from(["cache_clear", "cache", "progress.json"]).unwrap();
        assert!(omitted.operation_id.is_none());

        let operation_id = uuid::Uuid::new_v4();
        let operation_text = operation_id.to_string();
        let supplied = Args::try_parse_from([
            "cache_clear",
            "cache",
            "progress.json",
            "--operation-id",
            &operation_text,
        ])
        .unwrap();
        assert_eq!(
            supplied.operation_id.as_deref(),
            Some(operation_text.as_str())
        );
    }
}
