// Shared log-purge helper used by `cache_game_remove` and `cache_purge_log_entries`.
//
// This module contains `remove_log_entries_for_game`, which walks all nginx
// access.log files under a log directory (plain + gzip + zstd), rewrites each
// file to exclude lines matching the given URL set or depot-ID set, and
// atomically replaces the originals.
//
// It was extracted from `cache_game_remove.rs` so that both the single-game
// remover and the bulk evicted purge binary can share the exact same logic
// without duplication.

use anyhow::{Context, Result};
use std::collections::HashSet;
use std::io::{BufWriter, Write as IoWrite};
use std::path::Path;

use tempfile::NamedTempFile;
use flate2::write::GzEncoder;
use flate2::Compression;

use crate::cache_utils;
use crate::parser::LogParser;
use crate::log_reader::LogFileReader;
use crate::models::LogEntry;
use crate::{log_discovery, service_utils};

fn rewrite_matching_log_entries<F>(
    log_dir: &Path,
    description: &str,
    should_remove_entry: F,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
) -> Result<(u64, usize)>
where
    F: Fn(&LogEntry) -> bool + Send + Sync,
{
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Filtering log files to remove {} entries...", description);

    let parser = LogParser::new(chrono_tz::UTC);
    let log_files = log_discovery::discover_log_files(log_dir, "access.log")?;
    let total_files = log_files.len();

    let total_lines_removed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);
    let files_done = AtomicUsize::new(0);

    log_files.par_iter().enumerate().for_each(|(file_index, log_file)| {
        eprintln!(
            "  Processing file {}/{}: {}",
            file_index + 1,
            log_files.len(),
            log_file.path.display()
        );

        let file_result = (|| -> Result<u64> {
            let file_dir = log_file.path.parent().context("Failed to get file directory")?;
            let temp_file = NamedTempFile::new_in(file_dir)?;

            let mut lines_removed: u64 = 0;
            let mut lines_processed: u64 = 0;

            {
                let mut log_reader = LogFileReader::open(&log_file.path)?;

                let mut writer: Box<dyn std::io::Write> = if log_file.is_compressed {
                    let path_str = log_file.path.to_string_lossy();
                    if path_str.ends_with(".gz") {
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            GzEncoder::new(temp_file.as_file().try_clone()?, Compression::default()),
                        ))
                    } else if path_str.ends_with(".zst") {
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            zstd::Encoder::new(temp_file.as_file().try_clone()?, 3)?,
                        ))
                    } else {
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            temp_file.as_file().try_clone()?,
                        ))
                    }
                } else {
                    Box::new(BufWriter::with_capacity(
                        1024 * 1024,
                        temp_file.as_file().try_clone()?,
                    ))
                };

                let mut line = String::new();

                loop {
                    line.clear();
                    let bytes_read = log_reader.read_line(&mut line)?;
                    if bytes_read == 0 {
                        break;
                    }

                    lines_processed += 1;
                    let mut should_remove = false;

                    if let Some(entry) = parser.parse_line(line.trim()) {
                        if !service_utils::should_skip_url(&entry.url)
                            && should_remove_entry(&entry)
                        {
                            should_remove = true;
                        }
                    }

                    if !should_remove {
                        write!(writer, "{}", line)?;
                    } else {
                        lines_removed += 1;
                    }
                }

                writer.flush()?;
                drop(writer);
            }

            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!(
                    "  INFO: All {} lines from this file matched, deleting file entirely",
                    lines_processed
                );
                match cache_utils::safe_path_under_root(log_dir, &log_file.path) {
                    Ok(_) => {
                        std::fs::remove_file(&log_file.path).ok();
                    }
                    Err(e) => {
                        eprintln!("skipping unsafe path {}: {}", log_file.path.display(), e);
                    }
                }
                return Ok(lines_removed);
            }

            let temp_path = temp_file.into_temp_path();

            if let Err(persist_err) = temp_path.persist(&log_file.path) {
                eprintln!("    persist() failed ({}), using copy fallback...", persist_err);
                std::fs::copy(&persist_err.path, &log_file.path)?;
                match cache_utils::safe_path_under_root(log_dir, &persist_err.path) {
                    Ok(_) => {
                        std::fs::remove_file(&persist_err.path).ok();
                    }
                    Err(e) => {
                        eprintln!("skipping unsafe path {}: {}", persist_err.path.display(), e);
                    }
                }
            }

            Ok(lines_removed)
        })();

        match file_result {
            Ok(lines_removed) => {
                eprintln!("    Removed {} log lines from this file", lines_removed);
                total_lines_removed.fetch_add(lines_removed, Ordering::Relaxed);
            }
            Err(e) => {
                let error_str = e.to_string();
                if error_str.contains("Permission denied") || error_str.contains("os error 13") {
                    permission_errors.fetch_add(1, Ordering::Relaxed);
                    eprintln!("  ERROR: Permission denied for file {}: {}", log_file.path.display(), e);
                } else {
                    eprintln!("  WARNING: Skipping file {}: {}", log_file.path.display(), e);
                }
            }
        }

        if let Some(cb) = on_file_processed {
            let done = files_done.fetch_add(1, Ordering::Relaxed) + 1;
            cb(done, total_files);
        }
    });

    let final_removed = total_lines_removed.load(Ordering::Relaxed);
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);
    eprintln!(
        "Total log entries removed: {}, permission errors: {}",
        final_removed,
        final_permission_errors
    );
    Ok((final_removed, final_permission_errors))
}

/// Rewrite every nginx access.log file under `log_dir` to drop entries whose
/// URL is in `urls_to_remove` OR whose parsed depot_id is in `valid_depot_ids`.
///
/// Returns `(lines_removed, permission_errors)`.
///
/// An optional `on_file_processed` callback is invoked after each file completes,
/// receiving `(files_processed_so_far, total_files)`. The callback must be
/// `Send + Sync` because files are processed in parallel via rayon.
///
/// Safe to run against a live cache host: each target file is rewritten via a
/// temp file in the same directory and then atomically persisted (or copy +
/// delete fallback) so partially-written files are never observed.
#[allow(dead_code)]
pub(crate) fn remove_log_entries_for_game(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    valid_depot_ids: &HashSet<u32>,
    on_file_processed: Option<&(dyn Fn(usize, usize) + Send + Sync)>,
) -> Result<(u64, usize)> {
    rewrite_matching_log_entries(
        log_dir,
        "game",
        |entry| {
            if urls_to_remove.contains(&entry.url) {
                return true;
            }

            entry.depot_id
                .map(|depot_id| valid_depot_ids.contains(&depot_id))
                .unwrap_or(false)
        },
        on_file_processed,
    )
}

#[allow(dead_code)]
pub(crate) fn remove_log_entries_for_urls(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
) -> Result<(u64, usize)> {
    rewrite_matching_log_entries(
        log_dir,
        "URL-matched",
        |entry| urls_to_remove.contains(&entry.url),
        None,
    )
}

#[allow(dead_code)]
pub(crate) fn remove_log_entries_for_service(
    log_dir: &Path,
    service: &str,
    urls_to_remove: &HashSet<String>,
) -> Result<(u64, usize)> {
    let normalized_service = service_utils::normalize_service_name(service);
    let description = format!("service '{}'", service);

    rewrite_matching_log_entries(
        log_dir,
        &description,
        |entry| entry.service == normalized_service && urls_to_remove.contains(&entry.url),
        None,
    )
}
