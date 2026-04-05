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

use crate::parser::LogParser;
use crate::log_reader::LogFileReader;
use crate::{log_discovery, service_utils};

/// Rewrite every nginx access.log file under `log_dir` to drop entries whose
/// URL is in `urls_to_remove` OR whose parsed depot_id is in `valid_depot_ids`.
///
/// Returns `(lines_removed, permission_errors)`.
///
/// Safe to run against a live cache host: each target file is rewritten via a
/// temp file in the same directory and then atomically persisted (or copy +
/// delete fallback) so partially-written files are never observed.
pub(crate) fn remove_log_entries_for_game(
    log_dir: &Path,
    urls_to_remove: &HashSet<String>,
    valid_depot_ids: &HashSet<u32>,
) -> Result<(u64, usize)> {
    use rayon::prelude::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

    eprintln!("Filtering log files to remove game entries...");

    let parser = LogParser::new(chrono_tz::UTC);
    let log_files = log_discovery::discover_log_files(log_dir, "access.log")?;

    let total_lines_removed = AtomicU64::new(0);
    let permission_errors = AtomicUsize::new(0);

    // Process log files in parallel for faster removal
    log_files.par_iter().enumerate().for_each(|(file_index, log_file)| {
        eprintln!("  Processing file {}/{}: {}", file_index + 1, log_files.len(), log_file.path.display());

        let file_result = (|| -> Result<u64> {
            let file_dir = log_file.path.parent().context("Failed to get file directory")?;
            let temp_file = NamedTempFile::new_in(file_dir)?;

            let mut lines_removed: u64 = 0;
            let mut lines_processed: u64 = 0;

            {
                let mut log_reader = LogFileReader::open(&log_file.path)?;

                // Create writer that matches the compression of the original file
                let mut writer: Box<dyn std::io::Write> = if log_file.is_compressed {
                    // Check extension to determine compression type
                    let path_str = log_file.path.to_string_lossy();
                    if path_str.ends_with(".gz") {
                        // Gzip compression
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            GzEncoder::new(temp_file.as_file().try_clone()?, Compression::default())
                        ))
                    } else if path_str.ends_with(".zst") {
                        // Zstd compression
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            zstd::Encoder::new(temp_file.as_file().try_clone()?, 3)?
                        ))
                    } else {
                        // Unknown compression, treat as plain
                        Box::new(BufWriter::with_capacity(1024 * 1024, temp_file.as_file().try_clone()?))
                    }
                } else {
                    // Plain text
                    Box::new(BufWriter::with_capacity(1024 * 1024, temp_file.as_file().try_clone()?))
                };

                let mut line = String::new();

                loop {
                    line.clear();
                    let bytes_read = log_reader.read_line(&mut line)?;
                    if bytes_read == 0 {
                        break; // EOF
                    }

                    lines_processed += 1;
                    let mut should_remove = false;

                    // Parse the line and check if it belongs to this game
                    if let Some(entry) = parser.parse_line(line.trim()) {
                        // Skip health checks
                        if !service_utils::should_skip_url(&entry.url) {
                            // Check if this URL is for the game being removed
                            // Match by URL OR by depot_id
                            if urls_to_remove.contains(&entry.url) {
                                should_remove = true;
                            } else if let Some(depot_id) = entry.depot_id {
                                if valid_depot_ids.contains(&depot_id) {
                                    should_remove = true;
                                }
                            }
                        }
                    }

                    if !should_remove {
                        write!(writer, "{}", line)?;
                    } else {
                        lines_removed += 1;
                    }
                }

                writer.flush()?;
                // Ensure compression is finalized
                drop(writer);
            }

            // If all lines would be removed, delete the entire file
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!("  INFO: All {} lines from this file are for this game, deleting file entirely", lines_processed);
                std::fs::remove_file(&log_file.path).ok();
                return Ok(lines_removed);
            }

            // Atomically replace original with filtered version
            let temp_path = temp_file.into_temp_path();

            if let Err(persist_err) = temp_path.persist(&log_file.path) {
                // Fallback: copy + delete
                eprintln!("    persist() failed ({}), using copy fallback...", persist_err);
                std::fs::copy(&persist_err.path, &log_file.path)?;
                std::fs::remove_file(&persist_err.path).ok();
            }

            Ok(lines_removed)
        })();

        match file_result {
            Ok(lines_removed) => {
                eprintln!("    Removed {} log lines from this file", lines_removed);
                total_lines_removed.fetch_add(lines_removed, Ordering::Relaxed);
            }
            Err(e) => {
                // Check if this is a permission error
                let error_str = e.to_string();
                if error_str.contains("Permission denied") || error_str.contains("os error 13") {
                    permission_errors.fetch_add(1, Ordering::Relaxed);
                    eprintln!("  ERROR: Permission denied for file {}: {}", log_file.path.display(), e);
                } else {
                    eprintln!("  WARNING: Skipping file {}: {}", log_file.path.display(), e);
                }
            }
        }
    });

    let final_removed = total_lines_removed.load(Ordering::Relaxed);
    let final_permission_errors = permission_errors.load(Ordering::Relaxed);
    eprintln!("Total log entries removed: {}, permission errors: {}", final_removed, final_permission_errors);
    Ok((final_removed, final_permission_errors))
}
