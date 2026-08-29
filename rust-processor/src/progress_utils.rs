use anyhow::Result;
use chrono::Utc;
use serde::Serialize;
use std::io::Write;
use std::path::Path;
use std::thread;
use std::time::Duration;
use tempfile::NamedTempFile;

use crate::progress_events::ProgressReporter;

/// Progress JSON written to the progress file and tailed by the C# poller. Shared
/// camelCase shape for every `cache_*` binary (removal, corruption, log purge) so
/// C#'s `RustProgressBase` can parse them all through one contract.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgressData {
    pub status: String,
    pub stage_key: String,
    pub context: serde_json::Value,
    #[serde(rename = "percentComplete")]
    pub percent_complete: f64,
    #[serde(rename = "filesProcessed")]
    pub files_processed: usize,
    #[serde(rename = "totalFiles")]
    pub total_files: usize,
    pub timestamp: String,
}

/// Dispatch the stdout progress event matching `status`: `starting` -> emit_started,
/// `completed` -> emit_complete, `failed` -> emit_failed (with `errorDetail` pulled from
/// `context` if present), anything else -> emit_progress. `reporter` no-ops every call
/// when `--progress` was not passed.
pub fn emit_progress_event(
    reporter: &ProgressReporter,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
) {
    match status {
        "starting" => reporter.emit_started(stage_key, context),
        "completed" => reporter.emit_complete(stage_key, context),
        "failed" => {
            let error_detail = context
                .get("errorDetail")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string());
            reporter.emit_failed(stage_key, context, error_detail);
        }
        _ => reporter.emit_progress(percent_complete, stage_key, context),
    }
}

/// Write a single progress entry to `progress_path`, then emit the matching stdout
/// event via `reporter`. File write always happens first, so a stdout-triggered C#
/// file re-read is never stale.
#[allow(clippy::too_many_arguments)]
pub fn write_progress(
    progress_path: &Path,
    reporter: &ProgressReporter,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
) -> Result<()> {
    let emit_context = context.clone();
    let progress = ProgressData {
        status: status.to_string(),
        stage_key: stage_key.to_string(),
        context,
        percent_complete,
        files_processed,
        total_files,
        timestamp: current_timestamp(),
    };

    write_progress_json(progress_path, &progress)?;
    emit_progress_event(reporter, status, stage_key, emit_context, percent_complete);

    Ok(())
}

/// Write progress data to a JSON file with atomic write-and-rename to avoid race conditions
#[allow(dead_code)]
pub fn write_progress_json<T: Serialize>(progress_path: &Path, progress: &T) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;

    // Use tempfile for automatic cleanup on error
    let parent_dir = progress_path.parent().unwrap_or(Path::new("."));
    let mut temp_file = NamedTempFile::new_in(parent_dir)?;

    // Write JSON to temp file
    temp_file.write_all(json.as_bytes())?;
    temp_file.flush()?;

    // IMPORTANT: On Windows, we must close the file handle before persisting
    // Convert to TempPath which closes the file while keeping the path
    let temp_path = temp_file.into_temp_path();

    // Now atomically replace the target file
    // persist() uses rename which can fail on Windows if file is locked
    if let Err(persist_err) = temp_path.persist(progress_path) {
        // Fallback: copy + delete (works even if target is locked by file watcher)
        std::fs::copy(&persist_err.path, progress_path)?;
        std::fs::remove_file(&persist_err.path).ok();
    }

    Ok(())
}

/// Write progress with exponential backoff retry logic using atomic operations
#[allow(dead_code)]
pub fn write_progress_with_retry<T: Serialize>(
    progress_path: &Path,
    progress: &T,
    max_retries: usize,
) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;
    let parent_dir = progress_path.parent().unwrap_or(Path::new("."));

    let mut retries = 0;
    loop {
        // Use atomic write-and-rename to avoid race conditions
        match NamedTempFile::new_in(parent_dir) {
            Ok(mut temp_file) => {
                match temp_file
                    .write_all(json.as_bytes())
                    .and_then(|_| temp_file.flush())
                {
                    Ok(_) => {
                        // Close file handle and persist atomically
                        let temp_path = temp_file.into_temp_path();
                        match temp_path.persist(progress_path) {
                            Ok(_) => break,
                            Err(persist_err) => {
                                // Fallback: copy + delete (works even if target is locked)
                                match std::fs::copy(&persist_err.path, progress_path) {
                                    Ok(_) => {
                                        std::fs::remove_file(&persist_err.path).ok();
                                        break;
                                    }
                                    Err(_) if retries < max_retries => {
                                        retries += 1;
                                        thread::sleep(Duration::from_millis(10 * retries as u64));
                                        continue;
                                    }
                                    Err(e) => return Err(e.into()),
                                }
                            }
                        }
                    }
                    Err(_) if retries < max_retries => {
                        retries += 1;
                        thread::sleep(Duration::from_millis(10 * retries as u64));
                        continue;
                    }
                    Err(e) => return Err(e.into()),
                }
            }
            Err(_) if retries < max_retries => {
                retries += 1;
                thread::sleep(Duration::from_millis(10 * retries as u64));
                continue;
            }
            Err(e) => return Err(e.into()),
        }
    }

    Ok(())
}

/// Helper to create a timestamp string in RFC3339 format
#[allow(dead_code)]
pub fn current_timestamp() -> String {
    Utc::now().to_rfc3339()
}
