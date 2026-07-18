use anyhow::{Context, Result};
use flate2::write::GzEncoder;
use flate2::Compression;
use serde::Serialize;
use std::collections::HashMap;
use std::env;
use std::fs;
use std::io::{BufWriter, Write as IoWrite};
use std::path::Path;
use std::time::Instant;
use tempfile::NamedTempFile;

mod cancel;
mod log_discovery;
mod log_layout;
mod log_reader;
mod models;
mod progress_events;
mod progress_utils;
mod service_utils;

use log_discovery::{discover_log_files, LogFile};
use log_layout::{discover_log_sources, kind_for_stem, LogSource, SourceKind};
use log_reader::LogFileReader;
use progress_events::ProgressReporter;

#[derive(Serialize, Clone)]
struct ProgressData {
    is_processing: bool,
    percent_complete: f64,
    status: String,
    message: String,
    lines_processed: u64,
    lines_removed: u64,
    files_processed: usize,
    bytes_deleted: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    service_counts: Option<HashMap<String, u64>>,
    /// Complete-record counts per logical source stem (access.log, steam-access.log, ...).
    /// The host uses these to seed per-stem positions on reset-to-end.
    #[serde(skip_serializing_if = "Option::is_none")]
    source_line_counts: Option<HashMap<String, u64>>,
    /// Lines in the fallback-access.log series. Reported separately, never as a service.
    #[serde(skip_serializing_if = "Option::is_none")]
    fallback_lines: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    datasource_name: Option<String>,
    // i18n stage key consumed by C# RustLogRemovalService.ProgressData.StageKey
    // (read via [JsonPropertyName("stage_key")]). Empty unless explicitly set so the
    // frontend progress card always has a non-blank stage to render. See arch-rust-progress.md.
    stage_key: String,
    timestamp: String,
}

impl ProgressData {
    fn new(
        is_processing: bool,
        percent_complete: f64,
        status: String,
        message: String,
        lines_processed: u64,
        lines_removed: u64,
        files_processed: usize,
        service_counts: Option<HashMap<String, u64>>,
        datasource_name: Option<String>,
    ) -> Self {
        Self {
            is_processing,
            percent_complete,
            status,
            message,
            lines_processed,
            lines_removed,
            files_processed,
            bytes_deleted: 0,
            service_counts,
            source_line_counts: None,
            fallback_lines: None,
            datasource_name,
            stage_key: String::new(),
            timestamp: progress_utils::current_timestamp(),
        }
    }

    fn with_source_line_counts(mut self, counts: HashMap<String, u64>) -> Self {
        self.source_line_counts = Some(counts);
        self
    }

    fn with_fallback_lines(mut self, fallback_lines: Option<u64>) -> Self {
        self.fallback_lines = fallback_lines;
        self
    }

    /// Attaches an i18n stage key for the C#/frontend progress card. Returns self so it can
    /// be chained onto a `ProgressData::new(...)` call without touching the other call sites.
    fn with_stage_key(mut self, stage_key: &str) -> Self {
        self.stage_key = stage_key.to_string();
        self
    }

    fn with_bytes_deleted(mut self, bytes_deleted: u64) -> Self {
        self.bytes_deleted = bytes_deleted;
        self
    }
}

/// Default stdout stage key when `ProgressData.stage_key` is empty (the "count" command's ticks
/// never call `.with_stage_key(...)`, a pre-existing gap not touched here).
fn default_stage_key_for_status(status: &str) -> &'static str {
    match status {
        "counting" => "signalr.logService.count.counting",
        "deleting" => "signalr.logService.delete.deleting",
        "removing" => "signalr.logRemoval.removing",
        "completed" => "signalr.logService.complete",
        "cancelled" => "signalr.logService.cancelled",
        "failed" | "error" => "signalr.logService.error.fatal",
        _ => "signalr.logService.progress",
    }
}

/// Writes the backward-compatible, extended progress file, then emits the matching stdout event via
/// `reporter` (file write always first). Reuses `progress.stage_key` when the "remove" path has
/// already set one via `.with_stage_key(...)`; falls back to a status-derived default otherwise
/// (the "count" path's ticks) so the stdout channel is always meaningful.
fn write_progress(
    progress_path: &Path,
    reporter: &ProgressReporter,
    progress: &ProgressData,
) -> Result<()> {
    // Use shared progress writing utility
    progress_utils::write_progress_json(progress_path, progress)?;

    let stage_key = if progress.stage_key.is_empty() {
        default_stage_key_for_status(&progress.status)
    } else {
        progress.stage_key.as_str()
    };

    let context = serde_json::json!({
        "message": progress.message,
        "linesProcessed": progress.lines_processed,
        "linesRemoved": progress.lines_removed,
        "filesProcessed": progress.files_processed,
        "bytesDeleted": progress.bytes_deleted,
        "datasourceName": progress.datasource_name,
        // Real data (never omitted on the terminal "completed" tick) - this is the count
        // command's actual result, otherwise only available via the file.
        "serviceCounts": progress.service_counts,
    });

    match progress.status.as_str() {
        "completed" => reporter.emit_complete(stage_key, context),
        "cancelled" => reporter.emit_cancelled(stage_key, context),
        "failed" | "error" => {
            reporter.emit_failed(stage_key, context, Some(progress.message.clone()))
        }
        _ => reporter.emit_progress(progress.percent_complete, stage_key, context),
    }

    Ok(())
}

// Use the shared service extraction utility for consistency
fn extract_service_from_line(line: &str) -> Option<String> {
    service_utils::extract_service_from_line(line)
}

/// Byte-level equivalent of `extract_service_from_line(line.trim()) == Some(service_lower)`.
/// Only the `[service]` tag is UTF-8 decoded, so the hot rewrite loop can work on raw
/// line bytes without validating the whole line. A line that is not valid UTF-8 in its
/// tag (or has no tag) can never match a service and is passed through unchanged.
fn line_matches_service(raw_line: &[u8], service_lower: &str) -> bool {
    let mut start = 0;
    while start < raw_line.len() && raw_line[start].is_ascii_whitespace() {
        start += 1;
    }
    let trimmed = &raw_line[start..];
    if trimmed.first() != Some(&b'[') {
        return false;
    }
    let Some(end) = trimmed.iter().position(|&b| b == b']') else {
        return false;
    };
    match std::str::from_utf8(&trimmed[1..end]) {
        Ok(tag) => service_utils::normalize_service_name(tag) == service_lower,
        Err(_) => false,
    }
}

#[derive(Debug, PartialEq, Eq)]
struct LineCountOutcome {
    lines_processed: u64,
    files_processed: usize,
    cancelled: bool,
    /// Complete-record count per logical source stem.
    source_line_counts: HashMap<String, u64>,
}

#[derive(Debug, PartialEq, Eq)]
struct DeleteFileOutcome {
    bytes_deleted: u64,
    cancelled: bool,
}

#[derive(Debug, PartialEq, Eq)]
struct CompleteRecordCount {
    lines: u64,
    ended_incomplete: bool,
    cancelled: bool,
}

/// Resolve a log path (directory or explicit file) into its logical sources. A directory
/// enumerates EVERY source series (access.log AND per-service *-access.log files, with the
/// bare-metal logs/ -> logs/http descent); an explicit file resolves to that one stem's
/// rotation series, exactly as before.
fn resolve_sources(log_path: &str) -> Result<Vec<LogSource>> {
    let path = Path::new(log_path);
    if path.is_dir() {
        return Ok(discover_log_sources(path)?.sources);
    }

    // Do not require the explicit current file itself to exist. Rotation may have moved
    // `access.log` to `access.log.1` between calls; discovery by the supplied base name must
    // still find that surviving series member. A genuinely missing directory/file remains
    // an empty input because discovery below simply finds no matching siblings.
    let directory = path.parent().context("Failed to get parent directory")?;
    let base_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .context("Failed to get file name")?;
    let files = discover_log_files(directory, base_name)?;
    if files.is_empty() {
        return Ok(Vec::new());
    }
    Ok(vec![LogSource {
        stem: base_name.to_string(),
        kind: kind_for_stem(base_name),
        files,
    }])
}

/// Count complete (newline-terminated) records in one file. The unterminated final record
/// of a live file is NOT counted: a position seeded past it would skip the finished line
/// on the next processing run. The outcome also preserves the partial complete-record
/// count on cancellation and reports whether an unterminated record stopped the file.
fn count_complete_records_in_file<F, T>(
    path: &Path,
    bytes_processed: &mut u64,
    is_cancelled: &F,
    tick: &mut T,
) -> Result<CompleteRecordCount>
where
    F: Fn() -> bool,
    T: FnMut(u64, u64) -> Result<()>,
{
    let mut reader = LogFileReader::open(path)?;
    let mut line: Vec<u8> = Vec::with_capacity(1024);
    let mut file_lines = 0u64;
    let mut ended_incomplete = false;
    loop {
        if is_cancelled() {
            return Ok(CompleteRecordCount {
                lines: file_lines,
                ended_incomplete,
                cancelled: true,
            });
        }
        line.clear();
        let bytes_read = reader.read_until_newline(&mut line)?;
        if bytes_read == 0 {
            break;
        }
        *bytes_processed = bytes_processed.saturating_add(bytes_read as u64);
        if !line.ends_with(b"\n") {
            ended_incomplete = true;
            break;
        }
        file_lines += 1;
        tick(file_lines, *bytes_processed)?;
    }
    Ok(CompleteRecordCount {
        lines: file_lines,
        ended_incomplete,
        cancelled: false,
    })
}

/// Remove an access-log file while treating a concurrent NotFound as success. Every other
/// unlink failure must propagate; otherwise an all-matching file can survive while the
/// operation reports that its service entries were removed.
fn remove_log_file_if_present(path: &Path) -> Result<()> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => {
            Err(error).with_context(|| format!("Failed to delete log file: {}", path.display()))
        }
    }
}

fn finish_cancelled_line_count(
    progress_path: &Path,
    reporter: &ProgressReporter,
    datasource_name: Option<&str>,
    lines_processed: u64,
    files_processed: usize,
    source_line_counts: HashMap<String, u64>,
) -> Result<LineCountOutcome> {
    let progress = ProgressData::new(
        false,
        0.0,
        "cancelled".to_string(),
        "Line counting cancelled".to_string(),
        lines_processed,
        0,
        files_processed,
        None,
        datasource_name.map(str::to_string),
    )
    .with_source_line_counts(source_line_counts.clone())
    .with_stage_key("signalr.logService.cancelled");
    write_progress(progress_path, reporter, &progress)?;

    Ok(LineCountOutcome {
        lines_processed,
        files_processed,
        cancelled: true,
        source_line_counts,
    })
}

fn source_counts_through_current(
    completed_counts: &HashMap<String, u64>,
    stem: &str,
    current_lines: u64,
) -> HashMap<String, u64> {
    let mut counts = completed_counts.clone();
    counts.insert(stem.to_string(), current_lines);
    counts
}

fn count_log_lines<F>(
    log_path: &str,
    progress_path: &Path,
    reporter: &ProgressReporter,
    datasource_name: Option<&str>,
    is_cancelled: F,
) -> Result<LineCountOutcome>
where
    F: Fn() -> bool,
{
    if is_cancelled() {
        return finish_cancelled_line_count(
            progress_path,
            reporter,
            datasource_name,
            0,
            0,
            HashMap::new(),
        );
    }

    let sources = resolve_sources(log_path)?;

    if sources.is_empty() {
        let progress = ProgressData::new(
            false,
            100.0,
            "completed".to_string(),
            "No log files found".to_string(),
            0,
            0,
            0,
            None,
            datasource_name.map(str::to_string),
        )
        .with_source_line_counts(HashMap::new())
        .with_stage_key("signalr.logService.complete");
        write_progress(progress_path, reporter, &progress)?;

        return Ok(LineCountOutcome {
            lines_processed: 0,
            files_processed: 0,
            cancelled: false,
            source_line_counts: HashMap::new(),
        });
    }

    let total_size = sources
        .iter()
        .flat_map(|source| &source.files)
        .filter_map(|log_file| fs::metadata(&log_file.path).ok())
        .map(|metadata| metadata.len())
        .sum::<u64>();
    let mut lines_processed = 0u64;
    let mut files_processed = 0usize;
    let mut bytes_processed = 0u64;
    let mut last_progress_update = Instant::now();
    let mut source_line_counts: HashMap<String, u64> = HashMap::new();

    for source in &sources {
        let mut source_lines = 0u64;

        for log_file in &source.files {
            if is_cancelled() {
                return finish_cancelled_line_count(
                    progress_path,
                    reporter,
                    datasource_name,
                    lines_processed + source_lines,
                    files_processed,
                    source_counts_through_current(&source_line_counts, &source.stem, source_lines),
                );
            }

            let mut tick = |file_lines: u64, bytes_so_far: u64| -> Result<()> {
                if last_progress_update.elapsed().as_millis() > 500 {
                    let percent_complete = if total_size == 0 {
                        0.0
                    } else {
                        ((bytes_so_far as f64 / total_size as f64) * 100.0).min(100.0)
                    };
                    let progress = ProgressData::new(
                        true,
                        percent_complete,
                        "counting".to_string(),
                        format!(
                            "Counting log lines... {} lines across {} files",
                            lines_processed + source_lines + file_lines,
                            files_processed + 1
                        ),
                        lines_processed + source_lines + file_lines,
                        0,
                        files_processed + 1,
                        None,
                        datasource_name.map(str::to_string),
                    );
                    write_progress(progress_path, reporter, &progress)?;
                    last_progress_update = Instant::now();
                }
                Ok(())
            };

            // These counts seed per-stem positions (reset-to-end / fresh install), so a
            // seeded value must describe one clean PREFIX of the stem's series: stop the
            // source at the first unreadable or unterminated member. The next processing
            // run re-reads from the prefix and dedup absorbs any overlap.
            match count_complete_records_in_file(
                &log_file.path,
                &mut bytes_processed,
                &is_cancelled,
                &mut tick,
            ) {
                Ok(outcome) if outcome.cancelled => {
                    return finish_cancelled_line_count(
                        progress_path,
                        reporter,
                        datasource_name,
                        lines_processed + source_lines + outcome.lines,
                        files_processed,
                        source_counts_through_current(
                            &source_line_counts,
                            &source.stem,
                            source_lines + outcome.lines,
                        ),
                    );
                }
                Ok(outcome) => {
                    files_processed += 1;
                    source_lines += outcome.lines;
                    if outcome.ended_incomplete {
                        break;
                    }
                }
                Err(error) => {
                    files_processed += 1;
                    eprintln!(
                        "WARNING: Corrupted log file {} stops this source's count at its last clean prefix: {error:#}",
                        log_file.path.display()
                    );
                    break;
                }
            }
        }

        lines_processed += source_lines;
        source_line_counts.insert(source.stem.clone(), source_lines);
    }

    let progress = ProgressData::new(
        false,
        100.0,
        "completed".to_string(),
        format!("Line counting completed. {lines_processed} lines across {files_processed} files."),
        lines_processed,
        0,
        files_processed,
        None,
        datasource_name.map(str::to_string),
    )
    .with_source_line_counts(source_line_counts.clone())
    .with_stage_key("signalr.logService.complete");
    write_progress(progress_path, reporter, &progress)?;

    Ok(LineCountOutcome {
        lines_processed,
        files_processed,
        cancelled: false,
        source_line_counts,
    })
}

fn delete_log_file<F>(
    file_path: &Path,
    progress_path: &Path,
    reporter: &ProgressReporter,
    datasource_name: Option<&str>,
    is_cancelled: F,
) -> Result<DeleteFileOutcome>
where
    F: Fn() -> bool,
{
    if is_cancelled() {
        let progress = ProgressData::new(
            false,
            0.0,
            "cancelled".to_string(),
            "Log file deletion cancelled".to_string(),
            0,
            0,
            0,
            None,
            datasource_name.map(str::to_string),
        )
        .with_stage_key("signalr.logService.cancelled");
        write_progress(progress_path, reporter, &progress)?;
        return Ok(DeleteFileOutcome {
            bytes_deleted: 0,
            cancelled: true,
        });
    }

    // A directory means "delete the whole log-file set": every source series (per-service
    // files, access.log, fallback — all rotations included) is removed. The host clears
    // every stem position afterwards.
    if file_path.is_dir() {
        let sources = discover_log_sources(file_path)?.sources;
        let mut bytes_deleted = 0u64;
        let mut files_deleted = 0usize;
        for log_file in sources.iter().flat_map(|s| &s.files) {
            if is_cancelled() {
                let progress = ProgressData::new(
                    false,
                    0.0,
                    "cancelled".to_string(),
                    format!(
                        "Log file deletion cancelled after {} file(s)",
                        files_deleted
                    ),
                    0,
                    0,
                    files_deleted,
                    None,
                    datasource_name.map(str::to_string),
                )
                .with_bytes_deleted(bytes_deleted)
                .with_stage_key("signalr.logService.cancelled");
                write_progress(progress_path, reporter, &progress)?;
                return Ok(DeleteFileOutcome {
                    bytes_deleted,
                    cancelled: true,
                });
            }

            let size = fs::metadata(&log_file.path).map(|m| m.len()).unwrap_or(0);
            fs::remove_file(&log_file.path).with_context(|| {
                format!("Failed to delete log file: {}", log_file.path.display())
            })?;
            bytes_deleted += size;
            files_deleted += 1;
        }

        let progress = ProgressData::new(
            false,
            100.0,
            "completed".to_string(),
            format!(
                "Deleted {} log file(s) ({} bytes)",
                files_deleted, bytes_deleted
            ),
            0,
            0,
            files_deleted,
            None,
            datasource_name.map(str::to_string),
        )
        .with_bytes_deleted(bytes_deleted)
        .with_stage_key("signalr.logService.complete");
        write_progress(progress_path, reporter, &progress)?;

        return Ok(DeleteFileOutcome {
            bytes_deleted,
            cancelled: false,
        });
    }

    let bytes_deleted = fs::metadata(file_path)
        .with_context(|| format!("Failed to inspect log file: {}", file_path.display()))?
        .len();

    if is_cancelled() {
        let progress = ProgressData::new(
            false,
            0.0,
            "cancelled".to_string(),
            "Log file deletion cancelled".to_string(),
            0,
            0,
            0,
            None,
            datasource_name.map(str::to_string),
        )
        .with_stage_key("signalr.logService.cancelled");
        write_progress(progress_path, reporter, &progress)?;
        return Ok(DeleteFileOutcome {
            bytes_deleted: 0,
            cancelled: true,
        });
    }

    fs::remove_file(file_path)
        .with_context(|| format!("Failed to delete log file: {}", file_path.display()))?;

    let progress = ProgressData::new(
        false,
        100.0,
        "completed".to_string(),
        format!("Deleted log file ({} bytes)", bytes_deleted),
        0,
        0,
        1,
        None,
        datasource_name.map(str::to_string),
    )
    .with_bytes_deleted(bytes_deleted)
    .with_stage_key("signalr.logService.complete");
    write_progress(progress_path, reporter, &progress)?;

    Ok(DeleteFileOutcome {
        bytes_deleted,
        cancelled: false,
    })
}

fn count_services(
    log_path: &str,
    progress_path: &Path,
    reporter: &ProgressReporter,
    datasource_name: Option<&str>,
) -> Result<HashMap<String, u64>> {
    let start_time = Instant::now();
    let ds_name = datasource_name.map(|s| s.to_string());
    if let Some(ds) = &ds_name {
        eprintln!("Counting services in log files for datasource: {}", ds);
    } else {
        eprintln!("Counting services in log files...");
    }

    let sources = resolve_sources(log_path)?;

    if sources.is_empty() {
        eprintln!("No log files found in: {}", log_path);

        // Write progress with empty service counts so UI updates correctly
        let progress = ProgressData::new(
            false,
            100.0,
            "completed".to_string(),
            "No log files found".to_string(),
            0,
            0,
            0,
            Some(HashMap::new()),
            ds_name.clone(),
        );
        write_progress(progress_path, reporter, &progress)?;

        return Ok(HashMap::new());
    }

    let total_files: usize = sources.iter().map(|s| s.files.len()).sum();
    eprintln!(
        "Found {} log file(s) across {} source(s):",
        total_files,
        sources.len()
    );
    for source in &sources {
        for log_file in &source.files {
            eprintln!("  - {}", log_file.path.display());
        }
    }

    // Calculate total size across all files for progress tracking
    let mut total_size = 0u64;
    for log_file in sources.iter().flat_map(|s| &s.files) {
        if let Ok(metadata) = std::fs::metadata(&log_file.path) {
            total_size += metadata.len();
        }
    }

    let mut service_counts: HashMap<String, u64> = HashMap::new();
    let mut fallback_lines: Option<u64> = None;
    let mut lines_processed: u64 = 0;
    let mut bytes_processed: u64 = 0;
    let mut files_done: usize = 0;
    let mut last_progress_update = Instant::now();

    for source in &sources {
        for log_file in &source.files {
            files_done += 1;
            eprintln!(
                "\nProcessing file {}/{}: {}",
                files_done,
                total_files,
                log_file.path.display()
            );

            let file_result = (|| -> Result<()> {
                match &source.kind {
                    SourceKind::Monolithic => {
                        // Lines self-identify with a [service] tag; parse each one.
                        let mut log_reader = LogFileReader::open(&log_file.path)?;
                        let mut line = String::new();

                        loop {
                            line.clear();
                            let bytes_read = log_reader.read_line(&mut line)?;
                            if bytes_read == 0 {
                                break; // EOF
                            }

                            bytes_processed += line.len() as u64;
                            lines_processed += 1;

                            if let Some(service) = extract_service_from_line(line.trim()) {
                                // Use the service name directly - auto-discover all services
                                *service_counts.entry(service).or_insert(0) += 1;
                            }

                            // Update progress every 500ms
                            if last_progress_update.elapsed().as_millis() > 500 {
                                let percent = if total_size > 0 {
                                    ((bytes_processed as f64 / total_size as f64) * 100.0)
                                        .min(100.0)
                                } else {
                                    0.0
                                };

                                let progress = ProgressData::new(
                                    true,
                                    percent,
                                    "counting".to_string(),
                                    format!(
                                        "Counting services... {} lines processed across {} files",
                                        lines_processed, files_done
                                    ),
                                    lines_processed,
                                    0,
                                    files_done,
                                    None,
                                    ds_name.clone(),
                                );
                                write_progress(progress_path, reporter, &progress)?;
                                last_progress_update = Instant::now();
                            }
                        }
                        Ok(())
                    }
                    SourceKind::Service(_) | SourceKind::Fallback => {
                        // The filename IS the service scope: a complete-record count is the
                        // per-service line count, no parsing needed.
                        let mut tick = |_file_lines: u64, bytes_so_far: u64| -> Result<()> {
                            if last_progress_update.elapsed().as_millis() > 500 {
                                let percent = if total_size > 0 {
                                    ((bytes_so_far as f64 / total_size as f64) * 100.0).min(100.0)
                                } else {
                                    0.0
                                };
                                let progress = ProgressData::new(
                                    true,
                                    percent,
                                    "counting".to_string(),
                                    format!(
                                        "Counting services... {} lines processed across {} files",
                                        lines_processed, files_done
                                    ),
                                    lines_processed,
                                    0,
                                    files_done,
                                    None,
                                    ds_name.clone(),
                                );
                                write_progress(progress_path, reporter, &progress)?;
                                last_progress_update = Instant::now();
                            }
                            Ok(())
                        };
                        let file_lines = count_complete_records_in_file(
                            &log_file.path,
                            &mut bytes_processed,
                            &|| false,
                            &mut tick,
                        )?
                        .lines;
                        lines_processed += file_lines;
                        match &source.kind {
                            SourceKind::Service(service) => {
                                *service_counts.entry(service.clone()).or_insert(0) += file_lines;
                            }
                            _ => {
                                // Fallback series: reported separately, never as a service.
                                *fallback_lines.get_or_insert(0) += file_lines;
                            }
                        }
                        Ok(())
                    }
                }
            })();

            // If this file failed (e.g., corrupted gzip), log warning and skip it
            if let Err(e) = file_result {
                eprintln!(
                    "WARNING: Skipping corrupted file {}: {}",
                    log_file.path.display(),
                    e
                );
                eprintln!("  Continuing with remaining files...");
                continue;
            }
        }
    }

    let elapsed = start_time.elapsed();
    eprintln!("\nService counting completed!");
    eprintln!("  Files processed: {}", total_files);
    eprintln!("  Lines processed: {}", lines_processed);
    eprintln!("  Services found: {}", service_counts.len());
    eprintln!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    for (service, count) in &service_counts {
        eprintln!("  {}: {}", service, count);
    }
    if let Some(fallback) = fallback_lines {
        eprintln!("  (fallback series: {} lines)", fallback);
    }

    // Final progress with service counts
    let progress = ProgressData::new(
        false,
        100.0,
        "completed".to_string(),
        format!(
            "Service counting completed. Found {} services in {} lines across {} files.",
            service_counts.len(),
            lines_processed,
            total_files
        ),
        lines_processed,
        0,
        total_files,
        Some(service_counts.clone()),
        ds_name,
    )
    .with_fallback_lines(fallback_lines);
    write_progress(progress_path, reporter, &progress)?;

    Ok(service_counts)
}

fn remove_service_from_logs(
    log_path: &str,
    service_to_remove: &str,
    progress_path: &Path,
    reporter: &ProgressReporter,
    datasource_name: Option<&str>,
) -> Result<()> {
    let start_time = Instant::now();
    let ds_name = datasource_name.map(|s| s.to_string());
    if let Some(ds) = &ds_name {
        eprintln!(
            "Removing {} entries from log files for datasource: {}",
            service_to_remove, ds
        );
    } else {
        eprintln!("Removing {} entries from log files...", service_to_remove);
    }

    let sources = resolve_sources(log_path)?;

    if sources.is_empty() {
        eprintln!("No log files found in: {}", log_path);

        let progress = ProgressData::new(
            false,
            100.0,
            "completed".to_string(),
            "No log files found".to_string(),
            0,
            0,
            0,
            None,
            ds_name.clone(),
        )
        .with_stage_key("signalr.logRemoval.completeNoFiles");
        write_progress(progress_path, reporter, &progress)?;

        return Ok(());
    }

    let service_lower = service_utils::normalize_service_name(service_to_remove);

    // Per-service sources owned by this service are removed as whole file series (the
    // filename IS the service scope) — strictly simpler and safer than rewriting.
    // Monolithic (tagged) sources still need the line-level rewrite below. Per-service
    // sources of OTHER services and the fallback series are never touched.
    let delete_sources: Vec<&LogSource> = sources
        .iter()
        .filter(|s| matches!(&s.kind, SourceKind::Service(svc) if *svc == service_lower))
        .collect();
    let log_files: Vec<LogFile> = sources
        .iter()
        .filter(|s| s.kind == SourceKind::Monolithic)
        .flat_map(|s| s.files.clone())
        .collect();

    eprintln!(
        "Found {} tagged log file(s) to rewrite and {} per-service source(s) to delete:",
        log_files.len(),
        delete_sources.len()
    );
    for log_file in &log_files {
        eprintln!("  - {}", log_file.path.display());
    }
    for source in &delete_sources {
        for log_file in &source.files {
            eprintln!("  - {} (delete)", log_file.path.display());
        }
    }

    let mut total_lines_processed: u64 = 0;
    let mut total_lines_removed: u64 = 0;
    let mut permission_errors: usize = 0;
    let mut deleted_files: usize = 0;
    let mut deletion_failures: usize = 0;

    for source in &delete_sources {
        for log_file in &source.files {
            if cancel::is_cancelled() {
                let progress = ProgressData::new(
                    false,
                    0.0,
                    "cancelled".to_string(),
                    format!(
                        "Cancelled after deleting {} file(s). {} lines removed.",
                        deleted_files, total_lines_removed
                    ),
                    total_lines_processed,
                    total_lines_removed,
                    deleted_files,
                    None,
                    ds_name.clone(),
                );
                write_progress(progress_path, reporter, &progress)?;
                std::process::exit(0);
            }

            // Count complete records first so the removal report matches the counts the
            // service-count UI showed for this source.
            let mut bytes_sink = 0u64;
            let lines_in_file = count_complete_records_in_file(
                &log_file.path,
                &mut bytes_sink,
                &|| false,
                &mut |_, _| Ok(()),
            )
            .ok()
            .map(|outcome| outcome.lines)
            .unwrap_or(0);

            match fs::remove_file(&log_file.path) {
                Ok(()) => {
                    deleted_files += 1;
                    total_lines_processed += lines_in_file;
                    total_lines_removed += lines_in_file;
                    eprintln!(
                        "  Deleted {} ({} lines)",
                        log_file.path.display(),
                        lines_in_file
                    );
                }
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                    // Already gone (raced rotation/cleanup): the goal state is reached.
                    eprintln!("  {} was already gone", log_file.path.display());
                }
                Err(e) => {
                    // ANY surviving file fails the operation: reporting success here would
                    // let the host clear this stem's checkpoint while a series member still
                    // exists, and its content would replay from zero on the next run.
                    if e.kind() == std::io::ErrorKind::PermissionDenied {
                        permission_errors += 1;
                        eprintln!(
                            "ERROR: Permission denied deleting {}: {}",
                            log_file.path.display(),
                            e
                        );
                    } else {
                        deletion_failures += 1;
                        eprintln!("ERROR: Failed to delete {}: {}", log_file.path.display(), e);
                    }
                }
            }
        }
    }

    // Process each log file
    for (file_index, log_file) in log_files.iter().enumerate() {
        // Cooperative cancel: check between file iterations (each file uses NamedTempFile+rename, so
        // stopping between files is safe — completed files are already atomically rewritten)
        if cancel::is_cancelled() {
            eprintln!(
                "Cancel requested — stopping before file {}/{}",
                file_index + 1,
                log_files.len()
            );
            let elapsed = start_time.elapsed();
            let progress = ProgressData::new(
                false,
                if log_files.len() > 0 {
                    (file_index as f64 / log_files.len() as f64) * 100.0
                } else {
                    0.0
                },
                "cancelled".to_string(),
                format!(
                    "Cancelled after {} files. {} lines processed, {} removed in {:.2}s.",
                    file_index,
                    total_lines_processed,
                    total_lines_removed,
                    elapsed.as_secs_f64()
                ),
                total_lines_processed,
                total_lines_removed,
                file_index,
                None,
                ds_name.clone(),
            );
            write_progress(progress_path, reporter, &progress)?;
            std::process::exit(0);
        }

        eprintln!(
            "\nProcessing file {}/{}: {}",
            file_index + 1,
            log_files.len(),
            log_file.path.display()
        );

        // Try to process the file, but skip if it's corrupted (e.g., invalid gzip header)
        let file_result = (|| -> Result<(u64, u64)> {
            let file_size = std::fs::metadata(&log_file.path)?.len();

            // Progress update for this file
            let progress = ProgressData::new(
                true,
                0.0,
                "removing".to_string(),
                format!(
                    "Processing file {}/{}: removing {} entries...",
                    file_index + 1,
                    log_files.len(),
                    service_to_remove
                ),
                total_lines_processed,
                total_lines_removed,
                file_index + 1,
                None,
                ds_name.clone(),
            )
            .with_stage_key("signalr.logRemoval.removing");
            write_progress(progress_path, reporter, &progress)?;

            // Builds the 500ms-throttled progress payload shared by both passes.
            let build_progress = |percent: f64, current_processed: u64, current_removed: u64| {
                let message = if current_removed > 0 {
                    format!(
                        "File {}/{}: {} lines processed, {} removed",
                        file_index + 1,
                        log_files.len(),
                        current_processed,
                        current_removed
                    )
                } else {
                    format!(
                        "File {}/{}: {} lines processed",
                        file_index + 1,
                        log_files.len(),
                        current_processed
                    )
                };
                ProgressData::new(
                    true,
                    percent,
                    "removing".to_string(),
                    message,
                    current_processed,
                    current_removed,
                    file_index + 1,
                    None,
                    ds_name.clone(),
                )
                .with_stage_key("signalr.logRemoval.removing")
            };

            // PASS 1: read-only scan. If the file contains no entries for this
            // service it is left completely untouched (no temp file, no recompression).
            let mut scan_lines: u64 = 0;
            let mut scan_matches: u64 = 0;
            {
                let mut log_reader = LogFileReader::open(&log_file.path)?;
                let mut bytes_scanned: u64 = 0;
                let mut last_progress_update = Instant::now();
                let mut line: Vec<u8> = Vec::with_capacity(1024);

                loop {
                    line.clear();
                    let bytes_read = log_reader.read_until_newline(&mut line)?;
                    if bytes_read == 0 {
                        break; // EOF
                    }

                    bytes_scanned += line.len() as u64;
                    scan_lines += 1;
                    if line_matches_service(&line, &service_lower) {
                        scan_matches += 1;
                    }

                    // Update progress every 500ms
                    if last_progress_update.elapsed().as_millis() > 500 {
                        let percent = if file_size > 0 {
                            ((bytes_scanned as f64 / file_size as f64) * 100.0).min(100.0)
                        } else {
                            0.0
                        };
                        let progress = build_progress(
                            percent,
                            total_lines_processed + scan_lines,
                            total_lines_removed + scan_matches,
                        );
                        write_progress(progress_path, reporter, &progress)?;
                        last_progress_update = Instant::now();
                    }
                }
            }

            if scan_matches == 0 {
                eprintln!(
                    "  No {} entries in this file - leaving it untouched",
                    service_to_remove
                );
                return Ok((scan_lines, 0));
            }

            // Allow removing all lines - user may want to clear all entries for a service
            // If file would be empty, just delete it instead of leaving an empty file
            if scan_lines > 0 && scan_matches == scan_lines {
                eprintln!(
                    "  INFO: All {} lines from this file will be removed",
                    scan_lines
                );
                eprintln!("    Deleting the log file entirely");
                remove_log_file_if_present(&log_file.path)?;
                return Ok((scan_lines, scan_matches));
            }

            // PASS 2: rewrite the file without this service's lines.
            // Create temp file for filtered output with automatic cleanup
            // Try the log directory first (enables atomic rename), fall back to system temp
            // if the directory doesn't allow file creation (common in Docker volume mounts)
            let file_dir = log_file
                .path
                .parent()
                .context("Failed to get file directory")?;
            let temp_file = NamedTempFile::new_in(file_dir).or_else(|_| {
                eprintln!("  Cannot create temp file in log directory, using system temp dir");
                NamedTempFile::new()
            })?;

            let mut lines_processed: u64 = 0;
            let mut lines_removed: u64 = 0;

            // Scope the file operations so handles are closed before deletion
            {
                // Use LogFileReader for automatic compression support (.gz, .zst)
                let mut log_reader = LogFileReader::open(&log_file.path)?;

                // Create writer that matches the compression of the original file
                let mut writer: Box<dyn std::io::Write> = if log_file.is_compressed {
                    let path_str = log_file.path.to_string_lossy();
                    if path_str.ends_with(".gz") {
                        Box::new(BufWriter::with_capacity(
                            1024 * 1024,
                            GzEncoder::new(temp_file.as_file().try_clone()?, Compression::fast()),
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

                let mut bytes_processed: u64 = 0;
                let mut last_progress_update = Instant::now();
                let mut line: Vec<u8> = Vec::with_capacity(1024);

                loop {
                    line.clear();
                    let bytes_read = log_reader.read_until_newline(&mut line)?;
                    if bytes_read == 0 {
                        break; // EOF
                    }

                    bytes_processed += line.len() as u64;
                    lines_processed += 1;

                    if line_matches_service(&line, &service_lower) {
                        lines_removed += 1;
                        if lines_removed.is_multiple_of(10000) {
                            eprintln!(
                                "Removed {} {} entries from this file",
                                lines_removed, service_to_remove
                            );
                        }
                    } else {
                        writer.write_all(&line)?;
                    }

                    // Update progress every 500ms
                    if last_progress_update.elapsed().as_millis() > 500 {
                        let percent = if file_size > 0 {
                            ((bytes_processed as f64 / file_size as f64) * 100.0).min(100.0)
                        } else {
                            0.0
                        };
                        let progress = build_progress(
                            percent,
                            total_lines_processed + lines_processed,
                            total_lines_removed + lines_removed,
                        );
                        write_progress(progress_path, reporter, &progress)?;
                        last_progress_update = Instant::now();
                    }
                }

                // Flush and close writer
                writer.flush()?;
                drop(writer);

                // reader and file are automatically dropped here when scope ends
            }

            // Safety net: the live access.log may have changed between the scan and
            // rewrite passes — if the rewrite saw only matching lines, delete the file
            // instead of persisting an empty one (same semantics as before).
            if lines_processed > 0 && lines_removed == lines_processed {
                eprintln!(
                    "  INFO: All {} lines from this file will be removed",
                    lines_processed
                );
                eprintln!("    Deleting the log file entirely");
                // temp_file automatically deleted when it goes out of scope
                // Delete the original log file
                remove_log_file_if_present(&log_file.path)?;
                return Ok((lines_processed, lines_removed));
            }

            // Atomically replace original with filtered version
            // persist() uses rename which can fail on Windows if file is locked
            let temp_path = temp_file.into_temp_path();

            if let Err(persist_err) = temp_path.persist(&log_file.path) {
                // Fallback: copy + delete (works even if target is locked by file watcher)
                eprintln!(
                    "    persist() failed ({}), using copy fallback...",
                    persist_err
                );
                fs::copy(&persist_err.path, &log_file.path)?;
                fs::remove_file(&persist_err.path).ok();
            }

            Ok((lines_processed, lines_removed))
        })();

        // If this file failed, check if it's a permission error
        match file_result {
            Ok((lines_processed, lines_removed)) => {
                eprintln!("  Lines processed: {}", lines_processed);
                eprintln!("  Lines removed: {}", lines_removed);

                total_lines_processed += lines_processed;
                total_lines_removed += lines_removed;
            }
            Err(e) => {
                // Check if this is a permission error
                let error_str = e.to_string();
                if error_str.contains("Permission denied") || error_str.contains("os error 13") {
                    permission_errors += 1;
                    eprintln!(
                        "ERROR: Permission denied for file {}: {}",
                        log_file.path.display(),
                        e
                    );
                } else {
                    eprintln!(
                        "WARNING: Skipping corrupted file {}: {}",
                        log_file.path.display(),
                        e
                    );
                }
                eprintln!("  Continuing with remaining files...");
                continue;
            }
        }
    }

    let elapsed = start_time.elapsed();

    // CRITICAL: Check for permission errors and fail if any occurred
    // This prevents the UI from showing success when files couldn't be modified
    if permission_errors > 0 {
        let puid = std::env::var("PUID").unwrap_or_else(|_| "1000".to_string());
        let pgid = std::env::var("PGID").unwrap_or_else(|_| "1000".to_string());
        let error_msg = format!(
            "FAILED: {} log file(s) could not be modified due to permission errors. \
            This is likely caused by incorrect PUID/PGID settings. The lancache container is configured to run as UID/GID {}:{}. \
            Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership.",
            permission_errors, puid, pgid
        );
        eprintln!("\n{}", error_msg);

        let progress = ProgressData::new(
            false,
            0.0,
            "failed".to_string(),
            error_msg.clone(),
            total_lines_processed,
            total_lines_removed,
            log_files.len() + deleted_files,
            None,
            ds_name,
        );
        write_progress(progress_path, reporter, &progress)?;

        anyhow::bail!("{}", error_msg);
    }

    if deletion_failures > 0 {
        let error_msg = format!(
            "FAILED: {} log file(s) for {} could not be deleted, so the removal is incomplete. \
            The remaining files were left in place and their positions were not cleared.",
            deletion_failures, service_to_remove
        );
        eprintln!("\n{}", error_msg);

        let progress = ProgressData::new(
            false,
            0.0,
            "failed".to_string(),
            error_msg.clone(),
            total_lines_processed,
            total_lines_removed,
            log_files.len() + deleted_files,
            None,
            ds_name,
        );
        write_progress(progress_path, reporter, &progress)?;

        anyhow::bail!("{}", error_msg);
    }

    let files_touched = log_files.len() + deleted_files;
    eprintln!("\nLog filtering completed!");
    eprintln!("  Files processed: {}", files_touched);
    eprintln!("  Lines processed: {}", total_lines_processed);
    eprintln!("  Lines removed: {}", total_lines_removed);
    eprintln!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    // Final progress
    let progress = ProgressData::new(
        false,
        100.0,
        "completed".to_string(),
        format!(
            "Removed {} {} entries from {} total lines across {} files in {:.2}s",
            total_lines_removed,
            service_to_remove,
            total_lines_processed,
            files_touched,
            elapsed.as_secs_f64()
        ),
        total_lines_processed,
        total_lines_removed,
        files_touched,
        None,
        ds_name,
    )
    .with_stage_key("signalr.logRemoval.complete");
    write_progress(progress_path, reporter, &progress)?;

    Ok(())
}

/// Check if cached progress file is still valid (newer than all log files)
/// Returns Ok with counts if cache is valid, Err if cache should be regenerated
fn check_cache_validity(log_path: &str, progress_path: &Path) -> Result<HashMap<String, u64>> {
    // Check if progress file exists
    if !progress_path.exists() {
        return Err(anyhow::anyhow!("Progress file doesn't exist"));
    }

    // Get progress file modification time
    let progress_metadata = fs::metadata(progress_path)?;
    let progress_modified = progress_metadata.modified()?;

    // Discover every source's files (per-service series included) so a growing
    // bare-metal file invalidates the cache exactly like a growing access.log.
    let log_files: Vec<LogFile> = resolve_sources(log_path)?
        .into_iter()
        .flat_map(|s| s.files)
        .collect();

    if log_files.is_empty() {
        return Err(anyhow::anyhow!("No log files found"));
    }

    // Check if any log file is newer than progress file
    for log_file in &log_files {
        if let Ok(log_metadata) = fs::metadata(&log_file.path) {
            if let Ok(log_modified) = log_metadata.modified() {
                if log_modified > progress_modified {
                    return Err(anyhow::anyhow!(
                        "Log file {} is newer than progress file",
                        log_file.path.display()
                    ));
                }
            }
        }
    }

    // Cache is valid, read and return the service counts
    let json = fs::read_to_string(progress_path)?;

    #[derive(serde::Deserialize)]
    struct CachedProgress {
        service_counts: Option<HashMap<String, u64>>,
    }

    let cached: CachedProgress = serde_json::from_str(&json)?;

    if let Some(counts) = cached.service_counts {
        Ok(counts)
    } else {
        Err(anyhow::anyhow!(
            "Progress file doesn't contain service counts"
        ))
    }
}

fn write_error_progress(progress_path: &Path, message: String, datasource_name: Option<&str>) {
    let error_progress = ProgressData::new(
        false,
        0.0,
        "error".to_string(),
        message,
        0,
        0,
        0,
        None,
        datasource_name.map(str::to_string),
    );

    if let Err(progress_error) = progress_utils::write_progress_json(progress_path, &error_progress)
    {
        eprintln!(
            "Warning: failed to write error progress to {}: {progress_error:#}",
            progress_path.display()
        );
    }
}

fn run(args: &[String], reporter: &ProgressReporter) -> Result<()> {
    if args.len() < 4 {
        eprintln!("Usage:");
        eprintln!(
            "  log_manager count <log_path_or_directory> <progress_json_path> [datasource_name]"
        );
        eprintln!(
            "  log_manager count-lines <log_path_or_directory> <progress_json_path> [datasource_name]"
        );
        eprintln!(
            "  log_manager remove <log_path_or_directory> <service_name> <progress_json_path> [datasource_name]"
        );
        eprintln!("  log_manager delete-file <file_path> <progress_json_path> [datasource_name]");
        eprintln!(
            "\nNote: count-lines counts complete records only and reports per-source counts."
        );
        anyhow::bail!("invalid arguments");
    }

    let command = &args[1];
    let log_path = &args[2];

    match command.as_str() {
        "count" => {
            if args.len() < 4 || args.len() > 5 {
                eprintln!(
                    "Usage: log_manager count <log_path_or_directory> <progress_json_path> [datasource_name]"
                );
                anyhow::bail!("invalid arguments for count");
            }
            let progress_path = Path::new(&args[3]);
            let datasource_name = args.get(4).map(String::as_str);

            if let Some(datasource) = datasource_name {
                eprintln!("Processing for datasource: {datasource}");
            }

            if check_cache_validity(log_path, progress_path).is_ok() {
                eprintln!(
                    "Using cached service counts (progress file is newer than all log files)"
                );
                return Ok(());
            }

            let starting = ProgressData::new(
                true,
                0.0,
                "starting".to_string(),
                "Starting service count".to_string(),
                0,
                0,
                0,
                None,
                datasource_name.map(str::to_string),
            )
            .with_stage_key("signalr.logService.count.starting");
            if let Err(error) = progress_utils::write_progress_json(progress_path, &starting) {
                eprintln!("Warning: failed to seed progress file: {error:#}");
            }

            reporter.emit_started("signalr.logService.count.starting", serde_json::json!({}));

            if let Err(error) = count_services(log_path, progress_path, reporter, datasource_name) {
                let error = error.context("Service counting failed");
                write_error_progress(progress_path, format!("{error:#}"), datasource_name);
                return Err(error);
            }

            Ok(())
        }
        "count-lines" => {
            if args.len() < 4 || args.len() > 5 {
                eprintln!(
                    "Usage: log_manager count-lines <log_path_or_directory> <progress_json_path> [datasource_name]"
                );
                anyhow::bail!("invalid arguments for count-lines");
            }
            let progress_path = Path::new(&args[3]);
            let datasource_name = args.get(4).map(String::as_str);
            let starting = ProgressData::new(
                true,
                0.0,
                "starting".to_string(),
                "Starting line count".to_string(),
                0,
                0,
                0,
                None,
                datasource_name.map(str::to_string),
            )
            .with_stage_key("signalr.logService.count.starting");
            progress_utils::write_progress_json(progress_path, &starting)
                .context("Failed to seed line-count progress file")?;
            reporter.emit_started(
                "signalr.logService.count.starting",
                serde_json::json!({ "datasourceName": datasource_name }),
            );

            if let Err(error) = count_log_lines(
                log_path,
                progress_path,
                reporter,
                datasource_name,
                cancel::is_cancelled,
            ) {
                let error = error.context("Line counting failed");
                write_error_progress(progress_path, format!("{error:#}"), datasource_name);
                return Err(error);
            }

            Ok(())
        }
        "remove" => {
            if args.len() < 5 || args.len() > 6 {
                eprintln!(
                    "Usage: log_manager remove <log_path_or_directory> <service_name> <progress_json_path> [datasource_name]"
                );
                anyhow::bail!("invalid arguments for remove");
            }
            let service_name = &args[3];
            let progress_path = Path::new(&args[4]);
            let datasource_name = args.get(5).map(String::as_str);

            if let Some(datasource) = datasource_name {
                eprintln!("Processing for datasource: {datasource}");
            }

            let starting = ProgressData::new(
                true,
                0.0,
                "starting".to_string(),
                format!("Starting removal of {service_name} entries"),
                0,
                0,
                0,
                None,
                datasource_name.map(str::to_string),
            )
            .with_stage_key("signalr.logRemoval.starting.single");
            if let Err(error) = progress_utils::write_progress_json(progress_path, &starting) {
                eprintln!("Warning: failed to seed progress file: {error:#}");
            }

            reporter.emit_started(
                "signalr.logRemoval.starting.single",
                serde_json::json!({ "service": service_name }),
            );

            if let Err(error) = remove_service_from_logs(
                log_path,
                service_name,
                progress_path,
                reporter,
                datasource_name,
            ) {
                let error = error.context("Service removal failed");
                write_error_progress(progress_path, format!("{error:#}"), datasource_name);
                return Err(error);
            }

            Ok(())
        }
        "delete-file" => {
            if args.len() < 4 || args.len() > 5 {
                eprintln!(
                    "Usage: log_manager delete-file <file_path> <progress_json_path> [datasource_name]"
                );
                anyhow::bail!("invalid arguments for delete-file");
            }
            let file_path = Path::new(log_path);
            let progress_path = Path::new(&args[3]);
            let datasource_name = args.get(4).map(String::as_str);
            let starting = ProgressData::new(
                true,
                0.0,
                "deleting".to_string(),
                "Starting log file deletion".to_string(),
                0,
                0,
                0,
                None,
                datasource_name.map(str::to_string),
            )
            .with_stage_key("signalr.logService.delete.deleting");
            progress_utils::write_progress_json(progress_path, &starting)
                .context("Failed to seed delete-file progress file")?;
            reporter.emit_started(
                "signalr.logService.delete.deleting",
                serde_json::json!({ "datasourceName": datasource_name }),
            );

            if let Err(error) = delete_log_file(
                file_path,
                progress_path,
                reporter,
                datasource_name,
                cancel::is_cancelled,
            ) {
                let error = error.context("Log file deletion failed");
                write_error_progress(progress_path, format!("{error:#}"), datasource_name);
                return Err(error);
            }

            Ok(())
        }
        _ => {
            eprintln!("Unknown command: {command}");
            eprintln!("Valid commands: count, count-lines, remove, delete-file");
            anyhow::bail!("unknown command: {command}");
        }
    }
}

fn main() -> anyhow::Result<()> {
    cancel::install();

    let mut args: Vec<String> = env::args().collect();
    let progress_enabled = if let Some(position) = args
        .iter()
        .position(|arg| arg == "--progress" || arg == "-p")
    {
        args.remove(position);
        true
    } else {
        false
    };
    let reporter = ProgressReporter::new(progress_enabled);
    let failure_stage_key = match args.get(1).map(String::as_str) {
        Some("remove") => "signalr.logRemoval.error.fatal",
        Some("delete-file") => "signalr.logService.delete.failed",
        Some("count") | Some("count-lines") => "signalr.logService.error.fatal",
        _ => "signalr.logService.error.fatal",
    };

    progress_events::run_or_exit(&reporter, failure_stage_key, || run(&args, &reporter));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    fn write_gzip(path: &Path, contents: &[u8]) {
        let file = fs::File::create(path).expect("create gzip fixture");
        let mut encoder = GzEncoder::new(file, Compression::default());
        encoder
            .write_all(contents)
            .expect("write gzip fixture contents");
        encoder.finish().expect("finish gzip fixture");
    }

    fn write_zstd(path: &Path, contents: &[u8]) {
        let file = fs::File::create(path).expect("create zstd fixture");
        let mut encoder = zstd::Encoder::new(file, 3).expect("build zstd encoder");
        encoder
            .write_all(contents)
            .expect("write zstd fixture contents");
        encoder.finish().expect("finish zstd fixture");
    }

    fn read_progress(path: &Path) -> serde_json::Value {
        let contents = fs::read_to_string(path).expect("read progress JSON");
        serde_json::from_str(&contents).expect("parse progress JSON")
    }

    #[test]
    fn line_matches_service_agrees_with_string_extraction() {
        let line = b"[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /depot/1/chunk/a HTTP/1.1\" 200 10 \"-\" \"agent\" \"HIT\" \"-\" \"-\"\n";
        assert!(line_matches_service(line, "steam"));
        assert!(!line_matches_service(line, "epic"));

        // Tag normalization must match the String-based path (uppercase tag, IP grouping)
        assert!(line_matches_service(b"[Steam] 1.2.3.4 / - ...", "steam"));
        assert!(line_matches_service(
            b"[127.0.0.1] 1.2.3.4 / - ...",
            "localhost"
        ));

        // No tag / unterminated tag -> never matches
        assert!(!line_matches_service(b"no tag here", "steam"));
        assert!(!line_matches_service(b"[unterminated", "steam"));
    }

    #[test]
    fn count_log_lines_counts_complete_records_across_the_series() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        // "two" has no trailing newline: an unterminated final record is NOT complete and
        // must not be counted (a position seeded past it would skip the finished line).
        fs::write(directory.path().join("access.log"), b"one\ntwo").expect("write current log");
        fs::write(directory.path().join("access.log.1"), b"three\n").expect("write rotated log");
        write_gzip(&directory.path().join("access.log.2.gz"), b"four\nfive\n");
        // .zst rotations are part of the processed series now (the old reset count
        // silently excluded them, desyncing the seeded position from the processor).
        write_zstd(&directory.path().join("access.log.3.zst"), b"zero\n");
        fs::write(directory.path().join("access.log.bak"), b"ignored\n")
            .expect("write ignored backup");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            Some("primary"),
            || false,
        )
        .expect("count lines");

        assert_eq!(result.lines_processed, 5);
        assert_eq!(result.files_processed, 4);
        assert!(!result.cancelled);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&5));
        let progress = read_progress(&progress_path);
        assert_eq!(progress["status"], "completed");
        assert_eq!(progress["lines_processed"], 5);
        assert_eq!(progress["files_processed"], 4);
        assert_eq!(progress["source_line_counts"]["access.log"], 5);
        assert!(progress.get("service_counts").is_none());
    }

    #[test]
    fn count_log_lines_reports_per_source_counts_for_bare_metal() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("access.log"), b"[steam] tagged\n").expect("write");
        fs::write(directory.path().join("steam-access.log"), b"a\nb\n").expect("write");
        fs::write(directory.path().join("steam-access.log.1"), b"c\n").expect("write");
        fs::write(directory.path().join("blizzard-access.log"), b"d\n").expect("write");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
            || false,
        )
        .expect("count lines");

        assert_eq!(result.lines_processed, 5);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&1));
        assert_eq!(result.source_line_counts.get("steam-access.log"), Some(&3));
        assert_eq!(
            result.source_line_counts.get("blizzard-access.log"),
            Some(&1)
        );
    }

    #[test]
    fn count_services_bare_metal_counts_by_filename_and_reports_fallback_separately() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(
            directory.path().join("steam-access.log"),
            b"line1\nline2\nline3\n",
        )
        .expect("write");
        fs::write(
            directory.path().join("windows-update-access.log"),
            b"w1\nw2\n",
        )
        .expect("write");
        fs::write(directory.path().join("fallback-access.log"), b"f1\n").expect("write");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let counts = count_services(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
        )
        .expect("count services");

        assert_eq!(counts.get("steam"), Some(&3));
        // The windows-update vhost maps to the manager's wsus service key.
        assert_eq!(counts.get("wsus"), Some(&2));
        assert!(!counts.contains_key("fallback"));
        assert!(!counts.contains_key("windows-update"));

        let progress = read_progress(&progress_path);
        assert_eq!(progress["fallback_lines"], 1);
        assert_eq!(progress["service_counts"]["wsus"], 2);
    }

    #[test]
    fn remove_service_deletes_per_service_series_and_rewrites_tagged_logs() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("steam-access.log"), b"s1\ns2\n").expect("write");
        fs::write(directory.path().join("steam-access.log.1"), b"s3\n").expect("write");
        fs::write(directory.path().join("blizzard-access.log"), b"b1\n").expect("write");
        fs::write(
            directory.path().join("access.log"),
            b"[steam] tagged steam line\n[blizzard] tagged blizzard line\n",
        )
        .expect("write");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        remove_service_from_logs(
            directory.path().to_str().expect("UTF-8 fixture path"),
            "steam",
            &progress_path,
            &reporter,
            None,
        )
        .expect("remove steam");

        assert!(!directory.path().join("steam-access.log").exists());
        assert!(!directory.path().join("steam-access.log.1").exists());
        assert!(directory.path().join("blizzard-access.log").exists());
        let rewritten =
            fs::read_to_string(directory.path().join("access.log")).expect("read access.log");
        assert!(!rewritten.contains("[steam]"));
        assert!(rewritten.contains("[blizzard]"));

        let progress = read_progress(&progress_path);
        assert_eq!(progress["status"], "completed");
        // 3 lines from the deleted steam series + 1 tagged line from access.log
        assert_eq!(progress["lines_removed"], 4);
    }

    #[test]
    fn delete_log_file_directory_deletes_every_source_series() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("access.log"), b"123\n").expect("write");
        fs::write(directory.path().join("steam-access.log"), b"4567\n").expect("write");
        fs::write(directory.path().join("steam-access.log.1"), b"8\n").expect("write");
        fs::write(directory.path().join("nginx-error.log"), b"keep me\n").expect("write");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = delete_log_file(directory.path(), &progress_path, &reporter, None, || false)
            .expect("delete all log files");

        assert!(!result.cancelled);
        assert_eq!(result.bytes_deleted, 4 + 5 + 2);
        assert!(!directory.path().join("access.log").exists());
        assert!(!directory.path().join("steam-access.log").exists());
        assert!(!directory.path().join("steam-access.log.1").exists());
        assert!(directory.path().join("nginx-error.log").exists());
        assert_eq!(read_progress(&progress_path)["status"], "completed");
    }

    #[test]
    fn count_log_lines_returns_zero_for_missing_and_empty_inputs() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let reporter = ProgressReporter::new(false);

        let missing_progress = directory.path().join("missing-progress.json");
        let missing = count_log_lines(
            directory
                .path()
                .join("not-created")
                .to_str()
                .expect("UTF-8 fixture path"),
            &missing_progress,
            &reporter,
            None,
            || false,
        )
        .expect("count missing directory");
        assert_eq!(missing.lines_processed, 0);
        assert_eq!(missing.files_processed, 0);

        let empty_directory = directory.path().join("empty");
        fs::create_dir(&empty_directory).expect("create empty log directory");
        let empty_progress = directory.path().join("empty-progress.json");
        let empty = count_log_lines(
            empty_directory.to_str().expect("UTF-8 fixture path"),
            &empty_progress,
            &reporter,
            None,
            || false,
        )
        .expect("count empty directory");
        assert_eq!(empty.lines_processed, 0);
        assert_eq!(empty.files_processed, 0);
    }

    #[test]
    fn explicit_missing_current_file_still_discovers_its_rotations() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("access.log.1"), b"rotated\n").expect("write rotated log");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = count_log_lines(
            directory
                .path()
                .join("access.log")
                .to_str()
                .expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
            || false,
        )
        .expect("count surviving rotation");

        assert_eq!(result.lines_processed, 1);
        assert_eq!(result.files_processed, 1);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&1));
    }

    #[test]
    fn count_log_lines_stops_source_at_corrupt_member_to_keep_prefix() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        // The corrupt member is OLDER than the current file, so the seeded position must
        // stop at the prefix BEFORE it — counting the newer file would seed a position
        // that describes a series with a hole and later skips real records.
        fs::write(directory.path().join("access.log"), b"good\n").expect("write current log");
        fs::write(
            directory.path().join("access.log.1.gz"),
            b"not a gzip stream",
        )
        .expect("write corrupt gzip");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
            || false,
        )
        .expect("count around corrupt rotation");

        assert_eq!(result.lines_processed, 0);
        assert_eq!(result.files_processed, 1);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&0));
        assert_eq!(read_progress(&progress_path)["status"], "completed");
    }

    #[test]
    fn count_log_lines_reports_cancellation_without_failure() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("access.log"), b"one\n").expect("write current log");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
            || true,
        )
        .expect("cancel line count");

        assert!(result.cancelled);
        assert_eq!(read_progress(&progress_path)["status"], "cancelled");
    }

    #[test]
    fn cancelled_line_count_keeps_finished_stem_counts() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        std::fs::write(directory.path().join("access.log"), b"one\n").expect("write first source");
        std::fs::write(directory.path().join("steam-access.log"), b"two\n")
            .expect("write second source");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);
        let cancellation_checks = Cell::new(0usize);

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
            || {
                let next = cancellation_checks.get() + 1;
                cancellation_checks.set(next);
                // Initial check, first-file check, line read, EOF check, second-file check.
                next >= 5
            },
        )
        .expect("cancel after first source");

        assert!(result.cancelled);
        assert_eq!(result.lines_processed, 1);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&1));
        let progress = read_progress(&progress_path);
        assert_eq!(progress["source_line_counts"]["access.log"], 1);
    }

    #[test]
    fn count_log_lines_reports_records_consumed_before_mid_file_cancellation() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("access.log"), b"one\ntwo\nthree\n")
            .expect("write current log");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);
        let cancellation_checks = Cell::new(0usize);

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
            || {
                let next = cancellation_checks.get() + 1;
                cancellation_checks.set(next);
                // Initial command check, pre-file check, then one successful read.
                next >= 4
            },
        )
        .expect("cancel line count after one record");

        assert!(result.cancelled);
        assert_eq!(result.lines_processed, 1);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&1));
        assert_eq!(read_progress(&progress_path)["lines_processed"], 1);
        assert_eq!(
            read_progress(&progress_path)["source_line_counts"]["access.log"],
            1
        );
    }

    #[test]
    fn checked_log_unlink_propagates_non_not_found_errors() {
        let directory = tempfile::tempdir().expect("create fixture directory");

        let error = remove_log_file_if_present(directory.path())
            .expect_err("unlinking a directory as a log file must fail");

        assert!(format!("{error:#}").contains("Failed to delete log file"));
        assert!(directory.path().exists());
    }

    #[test]
    fn delete_log_file_reports_pre_delete_bytes() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let log_path = directory.path().join("access.log");
        fs::write(&log_path, b"123456").expect("write active log");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = delete_log_file(&log_path, &progress_path, &reporter, None, || false)
            .expect("delete active log");

        assert_eq!(
            result,
            DeleteFileOutcome {
                bytes_deleted: 6,
                cancelled: false,
            }
        );
        assert!(!log_path.exists());
        let progress = read_progress(&progress_path);
        assert_eq!(progress["status"], "completed");
        assert_eq!(progress["bytes_deleted"], 6);
    }

    #[test]
    fn delete_log_file_missing_or_cancelled_never_claims_success() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let reporter = ProgressReporter::new(false);
        let missing_path = directory.path().join("missing.log");
        let missing_progress = directory.path().join("missing-progress.json");

        let error = delete_log_file(&missing_path, &missing_progress, &reporter, None, || false)
            .expect_err("missing file must fail");
        assert!(format!("{error:#}").contains("Failed to inspect log file"));
        assert!(!missing_progress.exists());

        let active_path = directory.path().join("access.log");
        fs::write(&active_path, b"keep").expect("write active log");
        let cancelled_progress = directory.path().join("cancelled-progress.json");
        let cancelled =
            delete_log_file(&active_path, &cancelled_progress, &reporter, None, || true)
                .expect("cancel deletion");
        assert!(cancelled.cancelled);
        assert!(active_path.exists());
        assert_eq!(read_progress(&cancelled_progress)["status"], "cancelled");
    }
}
