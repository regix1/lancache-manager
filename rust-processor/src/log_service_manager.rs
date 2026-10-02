use anyhow::{Context, Result};
use serde::Serialize;
use std::collections::HashMap;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use lancache_processor::cancel;
use lancache_processor::content_scan;
use lancache_processor::log_discovery;
use lancache_processor::log_layout;
use lancache_processor::log_purge;
use lancache_processor::log_reader;
use lancache_processor::log_resume;
use lancache_processor::progress_events;
use lancache_processor::progress_utils;
// The production binary does not use riot_hosts; it is compiled only for the test build, where the
// shared parser_http_detailed test suite resolves a Riot CDN host through `crate::riot_hosts`.
use lancache_processor::service_utils;
use log_discovery::{discover_log_files, LogFile};
use log_layout::{discover_log_sources, kind_for_stem, LogSource, SourceKind};
use log_reader::LogFileReader;
use progress_events::ProgressReporter;

#[derive(Serialize, Clone)]
struct StaleReadRecords {
    position: u64,
    records: u64,
}

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
    /// Complete-record counts per file name (count-lines only). A log delete keeps the read lines of
    /// the files it leaves, so it needs them per file, not per series.
    #[serde(skip_serializing_if = "Option::is_none")]
    file_line_counts: Option<HashMap<String, u64>>,
    /// Per stem with an importer resume record (count-lines with --resume only): the saved position
    /// that record belongs to and the read records in it whose files logrotate removed since.
    #[serde(skip_serializing_if = "Option::is_none")]
    stale_read_records: Option<HashMap<String, StaleReadRecords>>,
    /// Lines in the fallback-access.log series. Reported separately, never as a service.
    #[serde(skip_serializing_if = "Option::is_none")]
    fallback_lines: Option<u64>,
    /// Count of members with open/read trouble or an unterminated rotated record. When present,
    /// the line and source counts are the best available complete-record totals rather than an
    /// authoritative total. Omitted (and treated as zero) when every member read cleanly.
    #[serde(skip_serializing_if = "Option::is_none")]
    files_with_errors: Option<u64>,
    /// Removed-line counts per stem for the REWRITTEN (monolithic) sources only; deleted
    /// per-service series report nothing here because the host clears those stems outright.
    /// The host subtracts the on-disk total-line count by this map.
    #[serde(skip_serializing_if = "Option::is_none")]
    lines_removed_by_stem: Option<HashMap<String, u64>>,
    /// The already-read subset of `lines_removed_by_stem` (series index below the saved
    /// ingestion position); the amount the host pulls each stem's position back by.
    #[serde(skip_serializing_if = "Option::is_none")]
    lines_removed_before_position_by_stem: Option<HashMap<String, u64>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    datasource_name: Option<String>,
    // i18n stage key consumed by C# RustLogRemovalService.ProgressData.StageKey
    // (read via [JsonPropertyName("stage_key")]). Empty unless explicitly set so the
    // frontend progress card always has a non-blank stage to render. See arch-rust-progress.md.
    stage_key: String,
    timestamp: String,
}

impl ProgressData {
    #[allow(clippy::too_many_arguments)]
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
            file_line_counts: None,
            stale_read_records: None,
            lines_removed_by_stem: None,
            lines_removed_before_position_by_stem: None,
            fallback_lines: None,
            files_with_errors: None,
            datasource_name,
            stage_key: String::new(),
            timestamp: progress_utils::current_timestamp(),
        }
    }

    fn with_lines_removed_by_stem(
        mut self,
        removed: HashMap<String, u64>,
        removed_before_position: HashMap<String, u64>,
    ) -> Self {
        self.lines_removed_by_stem = Some(removed);
        self.lines_removed_before_position_by_stem = Some(removed_before_position);
        self
    }

    fn with_source_line_counts(mut self, counts: HashMap<String, u64>) -> Self {
        self.source_line_counts = Some(counts);
        self
    }

    fn with_file_line_counts(mut self, counts: HashMap<String, u64>) -> Self {
        self.file_line_counts = Some(counts);
        self
    }

    fn with_stale_read_records(mut self, stale: HashMap<String, StaleReadRecords>) -> Self {
        self.stale_read_records = Some(stale);
        self
    }

    fn with_fallback_lines(mut self, fallback_lines: Option<u64>) -> Self {
        self.fallback_lines = fallback_lines;
        self
    }

    /// Marks the count partial when at least one member had a reportable problem. A zero count is
    /// left unset so a clean run keeps the original progress shape.
    fn with_files_with_errors(mut self, files_with_errors: u64) -> Self {
        self.files_with_errors = if files_with_errors > 0 {
            Some(files_with_errors)
        } else {
            None
        };
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

#[derive(Debug, PartialEq, Eq)]
struct LineCountOutcome {
    lines_processed: u64,
    files_processed: usize,
    cancelled: bool,
    /// Count of members with a reportable problem; when greater than zero the returned complete
    /// record count is the best available total rather than an authoritative total.
    files_with_errors: u64,
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
    read_error: Option<String>,
}

impl CompleteRecordCount {
    fn into_lines(self) -> Result<u64> {
        match self.read_error {
            Some(error) => Err(anyhow::Error::msg(error)),
            None => Ok(self.lines),
        }
    }
}

/// What one member adds to its source's line count, the problem to report for it, and whether
/// the source stops after it.
#[derive(Debug, PartialEq, Eq)]
struct MemberCount {
    lines: u64,
    problem: Option<String>,
    stop_source: bool,
    cancelled: bool,
}

fn member_count(rotated: bool, result: Result<CompleteRecordCount>) -> MemberCount {
    match result {
        Ok(outcome) if outcome.cancelled => MemberCount {
            lines: outcome.lines,
            problem: None,
            stop_source: false,
            cancelled: true,
        },
        Ok(outcome) if outcome.read_error.is_some() => MemberCount {
            lines: if rotated { outcome.lines } else { 0 },
            problem: outcome.read_error,
            stop_source: !rotated,
            cancelled: false,
        },
        Ok(outcome) if outcome.ended_incomplete => MemberCount {
            lines: outcome.lines,
            problem: if rotated {
                Some("ends with an unterminated record".to_string())
            } else {
                None
            },
            stop_source: !rotated,
            cancelled: false,
        },
        Ok(outcome) => MemberCount {
            lines: outcome.lines,
            problem: None,
            stop_source: false,
            cancelled: false,
        },
        Err(error) => MemberCount {
            lines: 0,
            problem: Some(format!("{error:#}")),
            stop_source: !rotated,
            cancelled: false,
        },
    }
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
fn count_complete_records<R>(
    bytes_processed: &mut u64,
    is_cancelled: &dyn Fn() -> bool,
    tick: &mut dyn FnMut(u64, u64) -> Result<()>,
    mut read_until_newline: R,
) -> Result<CompleteRecordCount>
where
    R: FnMut(&mut Vec<u8>) -> Result<usize>,
{
    let mut line: Vec<u8> = Vec::with_capacity(1024);
    let mut file_lines = 0u64;
    let mut ended_incomplete = false;
    loop {
        if is_cancelled() {
            return Ok(CompleteRecordCount {
                lines: file_lines,
                ended_incomplete,
                cancelled: true,
                read_error: None,
            });
        }
        line.clear();
        let bytes_read = match read_until_newline(&mut line) {
            Ok(bytes_read) => bytes_read,
            Err(error) => {
                return Ok(CompleteRecordCount {
                    lines: file_lines,
                    ended_incomplete: false,
                    cancelled: false,
                    read_error: Some(format!("{error:#}")),
                });
            }
        };
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
        read_error: None,
    })
}

fn count_complete_records_in_file(
    path: &Path,
    bytes_processed: &mut u64,
    is_cancelled: &dyn Fn() -> bool,
    tick: &mut dyn FnMut(u64, u64) -> Result<()>,
) -> Result<CompleteRecordCount> {
    let mut reader = LogFileReader::open(path)?;
    count_complete_records(bytes_processed, is_cancelled, tick, |line| {
        reader.read_until_newline(line)
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
    files_with_errors: u64,
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
    .with_files_with_errors(files_with_errors)
    .with_stage_key("signalr.logService.cancelled");
    write_progress(progress_path, reporter, &progress)?;

    Ok(LineCountOutcome {
        lines_processed,
        files_processed,
        cancelled: true,
        files_with_errors,
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

#[cfg(test)]
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
    count_log_lines_with(
        log_path,
        progress_path,
        reporter,
        datasource_name,
        is_cancelled,
        count_complete_records_in_file,
        None,
    )
}

fn count_log_lines_with<F, C>(
    log_path: &str,
    progress_path: &Path,
    reporter: &ProgressReporter,
    datasource_name: Option<&str>,
    is_cancelled: F,
    count_file: C,
    resume_path: Option<&Path>,
) -> Result<LineCountOutcome>
where
    F: Fn() -> bool,
    C: Fn(
        &Path,
        &mut u64,
        &dyn Fn() -> bool,
        &mut dyn FnMut(u64, u64) -> Result<()>,
    ) -> Result<CompleteRecordCount>,
{
    if is_cancelled() {
        return finish_cancelled_line_count(
            progress_path,
            reporter,
            datasource_name,
            0,
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
        .with_file_line_counts(HashMap::new())
        .with_stale_read_records(HashMap::new())
        .with_stage_key("signalr.logService.complete");
        write_progress(progress_path, reporter, &progress)?;

        return Ok(LineCountOutcome {
            lines_processed: 0,
            files_processed: 0,
            cancelled: false,
            files_with_errors: 0,
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
    let mut files_with_errors = 0u64;
    let mut bytes_processed = 0u64;
    let mut last_progress_update = Instant::now();
    let mut source_line_counts: HashMap<String, u64> = HashMap::new();
    let mut file_line_counts: HashMap<String, u64> = HashMap::new();
    let mut stale_read_records: HashMap<String, StaleReadRecords> = HashMap::new();
    if let Some(resume_path) = resume_path {
        let resume = log_resume::load(resume_path);
        for source in &sources {
            if let Some(entry) = resume.stems.get(&source.stem) {
                let paths: Vec<PathBuf> = source
                    .files
                    .iter()
                    .map(|log_file| log_file.path.clone())
                    .collect();
                stale_read_records.insert(
                    source.stem.clone(),
                    StaleReadRecords {
                        position: entry.position,
                        records: log_resume::deleted_prefix_records(entry, &paths),
                    },
                );
            }
        }
    }

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
                    files_with_errors,
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

            // Counting keeps complete readable records from a rotated member and continues through
            // newer members. Current-file read errors keep the clean-prefix boundary. A scalar count
            // has no saved per-member proof, so a later processor pass can conservatively replay
            // newer members after an unreadable rotation.
            let count = member_count(
                log_file.rotation_number.is_some(),
                count_file(
                    &log_file.path,
                    &mut bytes_processed,
                    &is_cancelled,
                    &mut tick,
                ),
            );
            if count.cancelled {
                return finish_cancelled_line_count(
                    progress_path,
                    reporter,
                    datasource_name,
                    lines_processed + source_lines + count.lines,
                    files_processed,
                    files_with_errors,
                    source_counts_through_current(
                        &source_line_counts,
                        &source.stem,
                        source_lines + count.lines,
                    ),
                );
            }

            files_processed += 1;
            file_line_counts.insert(
                log_file
                    .path
                    .file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .into_owned(),
                count.lines,
            );
            if let Some(problem) = count.problem {
                files_with_errors += 1;
                eprintln!(
                    "WARNING: Log file {} could not deliver every complete record: {problem}",
                    log_file.path.display()
                );
            }
            source_lines += count.lines;
            if count.stop_source {
                break;
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
    .with_file_line_counts(file_line_counts)
    .with_stale_read_records(stale_read_records)
    .with_files_with_errors(files_with_errors)
    .with_stage_key("signalr.logService.complete");
    write_progress(progress_path, reporter, &progress)?;

    Ok(LineCountOutcome {
        lines_processed,
        files_processed,
        cancelled: false,
        files_with_errors,
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
        // Newest file of each series first, so a delete that stops partway leaves only older files
        // of a series, and the host can count the read lines still in them.
        for log_file in sources.iter().flat_map(|s| s.files.iter().rev()) {
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

            // logrotate's half-written compressed copy of a plain rotation is hidden by discovery
            // while the plain file exists, and would outlive the delete with the same lines.
            if log_file.rotation_number.is_some() && !log_file.is_compressed {
                for extension in ["gz", "zst"] {
                    let mut twin = log_file.path.clone().into_os_string();
                    twin.push(format!(".{extension}"));
                    let twin = PathBuf::from(twin);
                    let size = fs::metadata(&twin).map(|m| m.len()).unwrap_or(0);
                    match fs::remove_file(&twin) {
                        Ok(()) => {
                            bytes_deleted += size;
                            files_deleted += 1;
                        }
                        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                        Err(error) => {
                            return Err(error).with_context(|| {
                                format!("Failed to delete log file: {}", twin.display())
                            })
                        }
                    }
                }
            }
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
        match std::fs::metadata(&log_file.path) {
            Ok(metadata) => total_size += metadata.len(),
            // The file is still processed; only the progress denominator is short by
            // its size, which makes the bar reach 100% before the work is done.
            Err(e) => eprintln!(
                "  Warning: could not read the size of {}, progress will run ahead: {}",
                log_file.path.display(),
                e
            ),
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
                        .into_lines()?;
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
    stem_positions: Option<&HashMap<String, u64>>,
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
    // Monolithic (tagged) files, counted here and rewritten by remove_all_log_entries_for_service below.
    let log_files: Vec<(LogFile, String)> = sources
        .iter()
        .filter(|s| s.kind == SourceKind::Monolithic)
        .flat_map(|s| {
            let stem = s.stem.clone();
            s.files.iter().cloned().map(move |f| (f, stem.clone()))
        })
        .collect();
    let mut removed_by_stem: HashMap<String, u64> = HashMap::new();
    let mut removed_before_by_stem: HashMap<String, u64> = HashMap::new();

    eprintln!(
        "Found {} tagged log file(s) to rewrite and {} per-service source(s) to delete:",
        log_files.len(),
        delete_sources.len()
    );
    for (log_file, _stem) in &log_files {
        eprintln!("  - {}", log_file.path.display());
    }
    for source in &delete_sources {
        for log_file in &source.files {
            eprintln!("  - {} (delete)", log_file.path.display());
        }
    }

    // Every per-service file must be one the host's check bound (a check made on an empty folder
    // binds none): nginx keeps writing into a file it created after the check, so deleting one would
    // lose its lines until the next reopen. The removal stops before it deletes anything.
    for source in &delete_sources {
        for log_file in &source.files {
            log_purge::publication_expectation(&log_file.path)?;
        }
    }

    let mut total_lines_processed: u64 = 0;
    let mut total_lines_removed: u64 = 0;
    let mut permission_errors: usize = 0;
    let mut deleted_files: usize = 0;
    let mut deletion_failures: usize = 0;
    let mut deleted_identities: Vec<log_purge::FileIdentity> = Vec::new();
    let mut rewrite_failures: usize = 0;
    let tagged_files = log_files.len();

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
            let lines_in_file = match count_complete_records_in_file(
                &log_file.path,
                &mut bytes_sink,
                &|| false,
                &mut |_, _| Ok(()),
            )
            .and_then(CompleteRecordCount::into_lines)
            {
                Ok(lines) => lines,
                Err(_) => 0,
            };

            // Read before the delete: the host's check matches a deleted file by this identity.
            let identity = log_purge::file_identity(&log_file.path);
            match fs::remove_file(&log_file.path) {
                Ok(()) => {
                    deleted_files += 1;
                    deleted_identities.extend(identity.ok());
                    total_lines_processed += lines_in_file;
                    total_lines_removed += lines_in_file;
                    eprintln!(
                        "  Deleted {} ({} lines)",
                        log_file.path.display(),
                        lines_in_file
                    );
                }
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                    // Already gone: the publication counts it as deleted only when no file of its series is left.
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

    let deletes_succeeded = permission_errors == 0 && deletion_failures == 0;

    if !log_files.is_empty() {
        total_lines_processed += log_files
            .iter()
            .filter_map(|(log_file, _)| {
                let mut bytes = 0;
                count_complete_records_in_file(
                    &log_file.path,
                    &mut bytes,
                    &|| false,
                    &mut |_, _| Ok(()),
                )
                .and_then(CompleteRecordCount::into_lines)
                .ok()
            })
            .sum::<u64>();
        let outcome = log_purge::remove_all_log_entries_for_service(
            Path::new(log_path),
            &service_lower,
            stem_positions,
        )?;
        total_lines_removed += outcome.lines_removed;
        permission_errors += outcome.permission_errors;
        rewrite_failures += outcome.other_errors;
        removed_by_stem.extend(outcome.lines_removed_by_stem);
        removed_before_by_stem.extend(outcome.lines_removed_before_position_by_stem);
    }

    // The host's check bound every log file, the deleted series included, so the publication covers
    // them all; a rewrite above published only the files it walked.
    log_purge::publish_deleted_files(&deleted_identities, deletes_succeeded, &service_lower)?;

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

        // Files that completed before the failing one already lost their lines; the counts
        // must still reach the host so the saved positions come back for them.
        let progress = ProgressData::new(
            false,
            0.0,
            "failed".to_string(),
            error_msg.clone(),
            total_lines_processed,
            total_lines_removed,
            tagged_files + deleted_files,
            None,
            ds_name,
        )
        .with_lines_removed_by_stem(removed_by_stem.clone(), removed_before_by_stem.clone());
        write_progress(progress_path, reporter, &progress)?;

        anyhow::bail!("{}", error_msg);
    }

    if deletion_failures + rewrite_failures > 0 {
        let error_msg = format!(
            "FAILED: {} log file(s) for {} could not be deleted, so the removal is incomplete. \
            The remaining files were left in place and their positions were not cleared.",
            deletion_failures + rewrite_failures,
            service_to_remove
        );
        eprintln!("\n{}", error_msg);

        let progress = ProgressData::new(
            false,
            0.0,
            "failed".to_string(),
            error_msg.clone(),
            total_lines_processed,
            total_lines_removed,
            tagged_files + deleted_files,
            None,
            ds_name,
        );
        write_progress(progress_path, reporter, &progress)?;

        anyhow::bail!("{}", error_msg);
    }

    let files_touched = tagged_files + deleted_files;
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
    .with_lines_removed_by_stem(removed_by_stem, removed_before_by_stem)
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
        // A log whose timestamp cannot be read is no proof the cache is still fresh,
        // so treat it like a newer log and regenerate instead of serving stale counts.
        let log_metadata = fs::metadata(&log_file.path).with_context(|| {
            format!(
                "Cannot check {} against the progress file",
                log_file.path.display()
            )
        })?;
        let log_modified = log_metadata.modified().with_context(|| {
            format!(
                "Cannot read the modified time of {}",
                log_file.path.display()
            )
        })?;
        if log_modified > progress_modified {
            return Err(anyhow::anyhow!(
                "Log file {} is newer than progress file",
                log_file.path.display()
            ));
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

fn run(
    args: &[String],
    reporter: &ProgressReporter,
    stem_positions: Option<&HashMap<String, u64>>,
    resume_path: Option<&Path>,
) -> Result<()> {
    if args.len() < 4 {
        eprintln!("Usage:");
        eprintln!(
            "  log_manager count <log_path_or_directory> <progress_json_path> [datasource_name]"
        );
        eprintln!(
            "  log_manager count-lines <log_path_or_directory> <progress_json_path> [datasource_name] [--resume <resume_json_path>]"
        );
        eprintln!(
            "  log_manager remove <log_path_or_directory> <service_name> <progress_json_path> [datasource_name]"
        );
        eprintln!("  log_manager delete-file <file_path> <progress_json_path> [datasource_name]");
        eprintln!(
            "  log_manager scan-content <log_directory> <output_json_path> [max_tail_bytes] [max_samples]"
        );
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
                    "Usage: log_manager count-lines <log_path_or_directory> <progress_json_path> [datasource_name] [--resume <resume_json_path>]"
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

            if let Err(error) = count_log_lines_with(
                log_path,
                progress_path,
                reporter,
                datasource_name,
                cancel::is_cancelled,
                count_complete_records_in_file,
                resume_path,
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
                stem_positions,
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
        "scan-content" => {
            if args.len() < 4 || args.len() > 6 {
                eprintln!(
                    "Usage: log_manager scan-content <log_directory> <output_json_path> [max_tail_bytes] [max_samples]"
                );
                anyhow::bail!("invalid arguments for scan-content");
            }
            let output_path = Path::new(&args[3]);
            // Clamp both budgets to hard backstop ceilings so an oversized argument can never
            // demand a multi-gigabyte tail allocation or unbounded candidate retention.
            let max_tail_bytes = match args.get(4) {
                Some(value) => value
                    .parse::<u64>()
                    .context("invalid max_tail_bytes for scan-content")?
                    .min(content_scan::MAX_TAIL_BYTES_LIMIT),
                None => content_scan::DEFAULT_MAX_TAIL_BYTES,
            };
            let max_samples = match args.get(5) {
                Some(value) => value
                    .parse::<usize>()
                    .context("invalid max_samples for scan-content")?
                    .min(content_scan::MAX_SAMPLES_LIMIT),
                None => content_scan::DEFAULT_MAX_SAMPLES,
            };

            // Read-only: this never writes progress events or touches live monitor offsets. Its
            // one output is the candidate JSON the C# host reads back and re-validates.
            let output =
                content_scan::scan_directory(Path::new(log_path), max_tail_bytes, max_samples)
                    .context("Content scan failed")?;
            let json = serde_json::to_string(&output)
                .context("Failed to serialize content scan output")?;
            std::fs::write(output_path, json).with_context(|| {
                format!(
                    "Failed to write content scan output: {}",
                    output_path.display()
                )
            })?;

            Ok(())
        }
        _ => {
            eprintln!("Unknown command: {command}");
            eprintln!("Valid commands: count, count-lines, remove, delete-file, scan-content");
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
    let stem_positions: Option<HashMap<String, u64>> =
        if let Some(position) = args.iter().position(|arg| arg == "--stem-positions") {
            args.remove(position);
            if position < args.len() {
                let path = args.remove(position);
                lancache_processor::log_purge::read_stem_positions(&path)
            } else {
                eprintln!("Warning: --stem-positions given without a path; ignoring");
                None
            }
        } else {
            None
        };
    let resume_path: Option<PathBuf> =
        if let Some(position) = args.iter().position(|arg| arg == "--resume") {
            args.remove(position);
            if position < args.len() {
                Some(PathBuf::from(args.remove(position)))
            } else {
                eprintln!("Warning: --resume given without a path; ignoring");
                None
            }
        } else {
            None
        };
    let reporter = ProgressReporter::new(progress_enabled);
    let failure_stage_key = match args.get(1).map(String::as_str) {
        Some("remove") => "signalr.logRemoval.error.fatal",
        Some("delete-file") => "signalr.logService.delete.failed",
        Some("count") | Some("count-lines") => "signalr.logService.error.fatal",
        _ => "signalr.logService.error.fatal",
    };

    progress_events::run_or_exit(&reporter, failure_stage_key, || {
        run(
            &args,
            &reporter,
            stem_positions.as_ref(),
            resume_path.as_deref(),
        )
    });
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use flate2::write::GzEncoder;
    use flate2::Compression;
    use lancache_processor::service_utils::line_matches_service;
    use std::cell::Cell;
    use std::io::Write;

    // The publication variables are process-wide, so the tests that run a service removal take turns.
    static PUBLICATION_ENV: std::sync::Mutex<()> = std::sync::Mutex::new(());

    fn json_line(service: &str) -> String {
        serde_json::json!({
            "cache_identifier": service,
            "remote_addr": "192.0.2.40",
            "time_local": "12/Sep/2026:18:22:34 +1000",
            "method": "GET",
            "path": "/content/file",
            "status": "200",
            "bytes_sent": 1,
            "user_agent": "Fixture/1.0",
            "upstream_cache_status": "HIT",
            "host": "cdn.example.test",
            "http_range": "-"
        })
        .to_string()
    }

    fn write_gzip(path: &Path, contents: &[u8], compression: Compression) {
        let file = fs::File::create(path).expect("create gzip fixture");
        let mut encoder = GzEncoder::new(file, compression);
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

    fn count_with_read_failure(
        path: &Path,
        bytes_processed: &mut u64,
        is_cancelled: &dyn Fn() -> bool,
        tick: &mut dyn FnMut(u64, u64) -> Result<()>,
        after_lines: u64,
    ) -> Result<CompleteRecordCount> {
        let mut reader = LogFileReader::open(path)?;
        let mut lines_read = 0u64;
        count_complete_records(bytes_processed, is_cancelled, tick, |line| {
            if lines_read == after_lines {
                anyhow::bail!("injected read failure");
            }
            let bytes_read = reader.read_until_newline(line)?;
            if line.ends_with(b"\n") {
                lines_read += 1;
            }
            Ok(bytes_read)
        })
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

        let json = json_line("Steam");
        assert!(line_matches_service(json.as_bytes(), "steam"));
        assert!(!line_matches_service(json.as_bytes(), "epicgames"));
        assert!(!line_matches_service(b"{malformed JSON}\n", "steam"));
    }

    #[test]
    fn count_log_lines_counts_complete_records_across_the_series() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        // "two" has no trailing newline: an unterminated final record is NOT complete and
        // must not be counted (a position seeded past it would skip the finished line).
        fs::write(directory.path().join("access.log"), b"one\ntwo").expect("write current log");
        fs::write(directory.path().join("access.log.1"), b"three\n").expect("write rotated log");
        write_gzip(
            &directory.path().join("access.log.2.gz"),
            b"four\nfive\n",
            Compression::default(),
        );
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
    fn count_log_lines_reports_lines_per_file() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("access.log.1"), b"a\nb\n").expect("write rotated log");
        fs::write(directory.path().join("access.log"), b"c\n").expect("write current log");
        let progress_path = directory.path().join("progress.json");

        count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
        )
        .expect("count lines");

        let progress = read_progress(&progress_path);
        assert_eq!(progress["file_line_counts"]["access.log.1"], 2);
        assert_eq!(progress["file_line_counts"]["access.log"], 1);
        assert_eq!(
            progress["file_line_counts"].as_object().map(|m| m.len()),
            Some(2)
        );
    }

    #[test]
    fn count_log_lines_reports_read_records_of_removed_files() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let oldest = directory.path().join("access.log.2");
        let rotated = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        fs::write(&oldest, b"1\n2\n3\n").expect("write oldest rotation");
        fs::write(&rotated, b"4\n5\n").expect("write rotation");
        fs::write(&current, b"6\n").expect("write current log");
        let mark = |path: &Path, records: u64| log_resume::FileMark {
            identity: log_purge::file_identity(path).expect("read identity"),
            len: fs::metadata(path).expect("read length").len(),
            records,
            offset: None,
            tail_crc: None,
        };
        let current_length = fs::metadata(&current).expect("read length").len();
        let mut resume = log_resume::ResumeFile::default();
        resume.stems.insert(
            "access.log".to_string(),
            log_resume::StemResume {
                position: 6,
                older_files: vec![mark(&oldest, 3), mark(&rotated, 2)],
                file_identity: log_purge::file_identity(&current).expect("read identity"),
                offset: current_length,
                file_records: 1,
                tail_crc: log_resume::tail_crc(&current, current_length).expect("hash tail"),
            },
        );
        let resume_path = directory.path().join("resume.json");
        log_resume::save(&resume_path, &resume).expect("save resume record");
        fs::remove_file(&oldest).expect("remove the oldest rotation");
        let progress_path = directory.path().join("progress.json");

        count_log_lines_with(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
            count_complete_records_in_file,
            Some(&resume_path),
        )
        .expect("count lines");

        let stale = &read_progress(&progress_path)["stale_read_records"]["access.log"];
        assert_eq!(stale["position"], 6);
        assert_eq!(stale["records"], 3);
    }

    #[test]
    fn count_log_lines_keeps_plain_rotation_when_compressed_twin_exists() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("access.log.1"), b"one\ntwo\nthree\n")
            .expect("write plain rotation");
        write_gzip(
            &directory.path().join("access.log.1.gz"),
            b"duplicate-one\nduplicate-two\nduplicate-three\nduplicate-four\nduplicate-five\n",
            Compression::default(),
        );
        fs::write(directory.path().join("access.log"), b"four\nfive\n").expect("write current log");

        let discovered =
            discover_log_files(directory.path(), "access.log").expect("discover rotation series");
        let names: Vec<_> = discovered
            .iter()
            .map(|log_file| {
                log_file
                    .path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .expect("UTF-8 fixture name")
            })
            .collect();
        assert_eq!(names, vec!["access.log.1", "access.log"]);

        let progress_path = directory.path().join("progress.json");
        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
        )
        .expect("count de-duplicated series");

        assert_eq!(result.lines_processed, 5);
        assert_eq!(result.files_processed, 2);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&5));
        assert_eq!(result.files_with_errors, 0);
    }

    #[test]
    fn member_count_applies_the_rotated_and_current_rules() {
        let complete = |lines| CompleteRecordCount {
            lines,
            ended_incomplete: false,
            cancelled: false,
            read_error: None,
        };
        assert_eq!(
            member_count(true, Ok(complete(3))),
            MemberCount {
                lines: 3,
                problem: None,
                stop_source: false,
                cancelled: false,
            }
        );

        let cancelled = CompleteRecordCount {
            lines: 4,
            ended_incomplete: false,
            cancelled: true,
            read_error: None,
        };
        assert_eq!(
            member_count(false, Ok(cancelled)),
            MemberCount {
                lines: 4,
                problem: None,
                stop_source: false,
                cancelled: true,
            }
        );

        for rotated in [true, false] {
            let open = member_count(rotated, Err(anyhow::anyhow!("open failed")));
            assert_eq!(open.lines, 0);
            assert_eq!(open.problem.as_deref(), Some("open failed"));
            assert_eq!(open.stop_source, !rotated);

            let read = member_count(
                rotated,
                Ok(CompleteRecordCount {
                    lines: 7,
                    ended_incomplete: false,
                    cancelled: false,
                    read_error: Some("read failed".to_string()),
                }),
            );
            assert_eq!(read.lines, if rotated { 7 } else { 0 });
            assert_eq!(read.problem.as_deref(), Some("read failed"));
            assert_eq!(read.stop_source, !rotated);

            let incomplete = member_count(
                rotated,
                Ok(CompleteRecordCount {
                    lines: 2,
                    ended_incomplete: true,
                    cancelled: false,
                    read_error: None,
                }),
            );
            assert_eq!(incomplete.lines, 2);
            assert_eq!(
                incomplete.problem.as_deref(),
                rotated.then_some("ends with an unterminated record")
            );
            assert_eq!(incomplete.stop_source, !rotated);
        }
    }

    #[test]
    fn count_log_lines_continues_after_unterminated_rotated_member() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(
            directory.path().join("access.log.1"),
            b"a1\na2\na3-cut-mid-lin",
        )
        .expect("write unterminated rotation");
        fs::write(directory.path().join("access.log"), b"a3\na4\n").expect("write current log");
        let progress_path = directory.path().join("progress.json");

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
        )
        .expect("count through unterminated rotation");

        assert_eq!(result.lines_processed, 4);
        assert_eq!(result.files_processed, 2);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&4));
        assert_eq!(result.files_with_errors, 1);

        fs::write(directory.path().join("access.log.1"), b"a1\na2\na3\n")
            .expect("complete rotated tail");
        fs::write(directory.path().join("access.log"), b"a3\na4\na5\n")
            .expect("append current record");

        let completed = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
        )
        .expect("count completed rotation");

        assert_eq!(completed.lines_processed, 6);
        assert_eq!(completed.files_processed, 2);
        assert_eq!(completed.source_line_counts.get("access.log"), Some(&6));
        assert_eq!(completed.files_with_errors, 0);
    }

    #[test]
    fn count_log_lines_continues_after_injected_rotated_read_failure() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let rotated = directory.path().join("access.log.1");
        fs::write(&rotated, b"old-one\nold-two\n").expect("write rotated log");
        fs::write(directory.path().join("access.log"), b"new-one\nnew-two\n")
            .expect("write current log");
        let progress_path = directory.path().join("progress.json");

        let result = count_log_lines_with(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
            |path, bytes_processed, is_cancelled, tick| {
                if path == rotated {
                    count_with_read_failure(path, bytes_processed, is_cancelled, tick, 0)
                } else {
                    count_complete_records_in_file(path, bytes_processed, is_cancelled, tick)
                }
            },
            None,
        )
        .expect("count after rotated read failure");

        assert_eq!(result.lines_processed, 2);
        assert_eq!(result.files_processed, 2);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&2));
        assert_eq!(result.files_with_errors, 1);
    }

    #[test]
    fn count_log_lines_stops_at_injected_current_read_failure() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let current = directory.path().join("access.log");
        fs::write(&current, b"one\ntwo\n").expect("write current log");
        let progress_path = directory.path().join("progress.json");

        let result = count_log_lines_with(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
            |path, bytes_processed, is_cancelled, tick| {
                if path == current {
                    count_with_read_failure(path, bytes_processed, is_cancelled, tick, 1)
                } else {
                    count_complete_records_in_file(path, bytes_processed, is_cancelled, tick)
                }
            },
            None,
        )
        .expect("count current read failure");

        assert_eq!(result.lines_processed, 0);
        assert_eq!(result.files_processed, 1);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&0));
        assert_eq!(result.files_with_errors, 1);
    }

    #[test]
    fn count_log_lines_keeps_records_before_truncated_rotation_error() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let compressed = directory.path().join("access.log.2.gz");
        let contents = (0..200)
            .map(|number| format!("record-{number:03}\n"))
            .collect::<String>();
        write_gzip(&compressed, contents.as_bytes(), Compression::none());
        let compressed_length = fs::metadata(&compressed)
            .expect("read compressed fixture length")
            .len();
        fs::OpenOptions::new()
            .write(true)
            .open(&compressed)
            .expect("open compressed fixture for truncation")
            .set_len(compressed_length * 3 / 5)
            .expect("truncate compressed fixture");
        fs::write(directory.path().join("access.log.1"), b"one\ntwo\nthree\n")
            .expect("write newer rotation");
        fs::write(directory.path().join("access.log"), b"four\nfive\n").expect("write current log");

        let mut reader = LogFileReader::open(&compressed).expect("open truncated rotation");
        let mut line = Vec::new();
        let mut readable_records = 0u64;
        let mut read_failed = false;
        loop {
            line.clear();
            match reader.read_until_newline(&mut line) {
                Ok(0) => break,
                Ok(_) if line.ends_with(b"\n") => readable_records += 1,
                Ok(_) => break,
                Err(_) => {
                    read_failed = true;
                    break;
                }
            }
        }
        assert!(read_failed, "truncated gzip must report a read error");

        let progress_path = directory.path().join("progress.json");
        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &ProgressReporter::new(false),
            None,
            || false,
        )
        .expect("count through truncated rotation");

        assert_eq!(result.lines_processed, readable_records + 5);
        assert_eq!(
            result.source_line_counts.get("access.log"),
            Some(&(readable_records + 5))
        );
        assert_eq!(result.files_with_errors, 1);
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
    fn count_services_includes_typed_json_identity() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        let contents = format!(
            "[steam] tagged text\n{}\n{}\n{{malformed JSON}}\n",
            json_line("Steam"),
            json_line("XboxLive")
        );
        fs::write(directory.path().join("access.log"), contents).expect("write access log");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let counts = count_services(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
        )
        .expect("count services");

        assert_eq!(counts.get("steam"), Some(&2));
        assert_eq!(counts.get("xboxlive"), Some(&1));
        assert_eq!(counts.len(), 2);
    }

    #[test]
    fn remove_service_deletes_per_service_series_and_rewrites_tagged_logs() {
        let _env = PUBLICATION_ENV
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
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
    fn remove_service_publishes_every_checked_file_when_it_deletes_a_per_service_series() {
        let _env = PUBLICATION_ENV
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        // Per-service logs only (bare metal), then per-service logs beside access.log (mixed).
        for mixed in [false, true] {
            let directory = tempfile::tempdir().expect("create fixture directory");
            let logs = directory.path().join("logs");
            fs::create_dir(&logs).expect("create log folder");
            fs::write(logs.join("steam-access.log"), b"s1\n").expect("write");
            fs::write(logs.join("blizzard-access.log"), b"b1\n").expect("write");
            if mixed {
                fs::write(
                    logs.join("access.log"),
                    b"[blizzard] tagged blizzard line\n",
                )
                .expect("write");
            }
            // The check the host writes: every log file it bound, with its identity.
            let files: Vec<serde_json::Value> = fs::read_dir(&logs)
                .expect("list log folder")
                .map(|entry| {
                    let path = entry.expect("log folder entry").path();
                    let identity = log_purge::file_identity(&path).expect("read identity");
                    serde_json::json!({ "targetPath": path, "originalIdentity": identity })
                })
                .collect();
            let check_path = directory.path().join("check.json");
            fs::write(
                &check_path,
                serde_json::to_vec(&serde_json::json!({ "valid": true, "files": files }))
                    .expect("serialize check"),
            )
            .expect("write check");
            let result_path = directory.path().join("result.json");
            std::env::set_var("LANCACHE_LOG_CHECK", &check_path);
            std::env::set_var("LANCACHE_LOG_RESULT", &result_path);
            let removal = remove_service_from_logs(
                logs.to_str().expect("UTF-8 fixture path"),
                "steam",
                &directory.path().join("progress.json"),
                &ProgressReporter::new(false),
                None,
                None,
            );
            std::env::remove_var("LANCACHE_LOG_CHECK");
            std::env::remove_var("LANCACHE_LOG_RESULT");
            removal.expect("remove steam");

            let published: serde_json::Value =
                serde_json::from_slice(&fs::read(&result_path).expect("read published result"))
                    .expect("parse published result");
            assert_eq!(published["success"], true);
            let records = published["files"].as_array().expect("published records");
            assert_eq!(records.len(), files.len());
            let record = |name: &str| {
                records
                    .iter()
                    .find(|record| {
                        record["targetPath"]
                            .as_str()
                            .expect("record path")
                            .ends_with(name)
                    })
                    .expect("a record for every checked file")
            };
            assert_eq!(record("steam-access.log")["deleted"], true);
            assert_eq!(record("steam-access.log")["changed"], true);
            assert_eq!(record("blizzard-access.log")["changed"], false);
            assert_eq!(
                record("blizzard-access.log")["publishedIdentity"],
                record("blizzard-access.log")["originalIdentity"]
            );
        }
    }

    #[test]
    fn remove_service_records_a_checked_file_deleted_outside_the_app_as_deleted() {
        let _env = PUBLICATION_ENV
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let directory = tempfile::tempdir().expect("create fixture directory");
        let logs = directory.path().join("logs");
        fs::create_dir(&logs).expect("create log folder");
        fs::write(logs.join("steam-access.log"), b"s1\n").expect("write");
        fs::write(logs.join("steam-access.log.1"), b"s0\n").expect("write");
        fs::write(logs.join("blizzard-access.log"), b"b1\n").expect("write");
        // The check the host writes: every log file it bound, with its identity.
        let files: Vec<serde_json::Value> = fs::read_dir(&logs)
            .expect("list log folder")
            .map(|entry| {
                let path = entry.expect("log folder entry").path();
                let identity = log_purge::file_identity(&path).expect("read identity");
                serde_json::json!({ "targetPath": path, "originalIdentity": identity })
            })
            .collect();
        let check_path = directory.path().join("check.json");
        fs::write(
            &check_path,
            serde_json::to_vec(&serde_json::json!({ "valid": true, "files": files }))
                .expect("serialize check"),
        )
        .expect("write check");
        // A retention cleanup outside the app deletes a bound rotation before the child lists the folder.
        fs::remove_file(logs.join("steam-access.log.1")).expect("delete rotation");
        let result_path = directory.path().join("result.json");
        std::env::set_var("LANCACHE_LOG_CHECK", &check_path);
        std::env::set_var("LANCACHE_LOG_RESULT", &result_path);
        let removal = remove_service_from_logs(
            logs.to_str().expect("UTF-8 fixture path"),
            "steam",
            &directory.path().join("progress.json"),
            &ProgressReporter::new(false),
            None,
            None,
        );
        std::env::remove_var("LANCACHE_LOG_CHECK");
        std::env::remove_var("LANCACHE_LOG_RESULT");
        removal.expect("remove steam");

        let published: serde_json::Value =
            serde_json::from_slice(&fs::read(&result_path).expect("read published result"))
                .expect("parse published result");
        assert_eq!(published["success"], true);
        let records = published["files"].as_array().expect("published records");
        assert_eq!(records.len(), 3);
        let record = |name: &str| {
            records
                .iter()
                .find(|record| {
                    record["targetPath"]
                        .as_str()
                        .expect("record path")
                        .ends_with(name)
                })
                .expect("a record for every checked file")
        };
        assert_eq!(record("steam-access.log.1")["deleted"], true);
        assert_eq!(record("steam-access.log")["deleted"], true);
        assert_eq!(record("blizzard-access.log")["changed"], false);
    }

    #[test]
    fn a_checked_file_renamed_within_its_series_is_not_recorded_as_deleted() {
        let _env = PUBLICATION_ENV
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let directory = tempfile::tempdir().expect("create fixture directory");
        let logs = directory.path().join("logs");
        fs::create_dir(&logs).expect("create log folder");
        let rotation = logs.join("steam-access.log.1");
        fs::write(&rotation, b"s0\n").expect("write");
        let identity = log_purge::file_identity(&rotation).expect("read identity");
        let check_path = directory.path().join("check.json");
        fs::write(
            &check_path,
            serde_json::to_vec(&serde_json::json!({
                "valid": true,
                "files": [{ "targetPath": rotation, "originalIdentity": identity }]
            }))
            .expect("serialize check"),
        )
        .expect("write check");
        // logrotate moves the bound rotation on after the child listed the folder; its lines survive
        // under the new name.
        fs::rename(&rotation, logs.join("steam-access.log.2")).expect("rotate");
        let result_path = directory.path().join("result.json");
        std::env::set_var("LANCACHE_LOG_CHECK", &check_path);
        std::env::set_var("LANCACHE_LOG_RESULT", &result_path);
        let publication = log_purge::publish_deleted_files(&[], true, "steam");
        std::env::remove_var("LANCACHE_LOG_CHECK");
        std::env::remove_var("LANCACHE_LOG_RESULT");
        publication.expect("publish");

        let published: serde_json::Value =
            serde_json::from_slice(&fs::read(&result_path).expect("read published result"))
                .expect("parse published result");
        assert_eq!(published["success"], false);
        assert_eq!(published["files"][0]["deleted"], false);
    }

    #[test]
    fn remove_service_keeps_a_per_service_log_the_check_did_not_bind() {
        let _env = PUBLICATION_ENV
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let directory = tempfile::tempdir().expect("create fixture directory");
        let logs = directory.path().join("logs");
        fs::create_dir(&logs).expect("create log folder");
        // The host found no log file and bound none; nginx created this one before the child listed
        // the folder.
        fs::write(logs.join("steam-access.log"), b"s1\n").expect("write");
        let check_path = directory.path().join("check.json");
        fs::write(&check_path, br#"{"valid":true,"files":[]}"#).expect("write check");
        let result_path = directory.path().join("result.json");
        std::env::set_var("LANCACHE_LOG_CHECK", &check_path);
        std::env::set_var("LANCACHE_LOG_RESULT", &result_path);
        let removal = remove_service_from_logs(
            logs.to_str().expect("UTF-8 fixture path"),
            "steam",
            &directory.path().join("progress.json"),
            &ProgressReporter::new(false),
            None,
            None,
        );
        std::env::remove_var("LANCACHE_LOG_CHECK");
        std::env::remove_var("LANCACHE_LOG_RESULT");

        let error = removal.expect_err("an unchecked log file stops the removal");
        assert!(format!("{error:#}").contains("log publication check did not include"));
        assert!(logs.join("steam-access.log").exists());
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
    fn delete_log_file_directory_removes_each_series_newest_first() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("steam-access.log.1"), b"1\n").expect("write");
        fs::write(directory.path().join("steam-access.log"), b"2\n").expect("write");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);
        // One check before the walk and one before each file, so the third stops it after one file.
        let checks = std::cell::Cell::new(0u32);

        let result = delete_log_file(directory.path(), &progress_path, &reporter, None, || {
            checks.set(checks.get() + 1);
            checks.get() == 3
        })
        .expect("stop after one file");

        assert!(result.cancelled);
        assert!(!directory.path().join("steam-access.log").exists());
        assert!(directory.path().join("steam-access.log.1").exists());
    }

    #[test]
    fn delete_log_file_directory_removes_a_compressed_twin() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        fs::write(directory.path().join("steam-access.log"), b"3\n").expect("write");
        fs::write(directory.path().join("steam-access.log.1"), b"2\n").expect("write");
        fs::write(
            directory.path().join("steam-access.log.1.gz"),
            b"half-written",
        )
        .expect("write");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = delete_log_file(directory.path(), &progress_path, &reporter, None, || false)
            .expect("delete all log files");

        assert!(!result.cancelled);
        assert!(!directory.path().join("steam-access.log").exists());
        assert!(!directory.path().join("steam-access.log.1").exists());
        assert!(!directory.path().join("steam-access.log.1.gz").exists());
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
        // A rotated member cannot recover, so its unreadable suffix is reported and newer
        // members continue. This matches the processor's position accounting.
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

        assert_eq!(result.lines_processed, 1);
        assert_eq!(result.files_processed, 2);
        assert_eq!(result.source_line_counts.get("access.log"), Some(&1));
        assert_eq!(result.files_with_errors, 1);
        assert_eq!(read_progress(&progress_path)["status"], "completed");
        assert_eq!(read_progress(&progress_path)["files_with_errors"], 1);
    }

    #[test]
    fn count_log_lines_preserves_readable_sources_and_flags_partial_on_unreadable() {
        let directory = tempfile::tempdir().expect("create fixture directory");
        // One per-service source is fully readable and must keep its exact count.
        fs::write(directory.path().join("steam-access.log"), b"a\nb\n").expect("write readable");
        // Another source's older rotation is corrupt. Its readable prefix is retained, the
        // problem is reported, and the current member still contributes its record.
        fs::write(directory.path().join("blizzard-access.log"), b"newer\n").expect("write current");
        fs::write(
            directory.path().join("blizzard-access.log.1.gz"),
            b"not a gzip stream",
        )
        .expect("write corrupt rotation");
        let progress_path = directory.path().join("progress.json");
        let reporter = ProgressReporter::new(false);

        let result = count_log_lines(
            directory.path().to_str().expect("UTF-8 fixture path"),
            &progress_path,
            &reporter,
            None,
            || false,
        )
        .expect("count with one unreadable source");

        // The readable source's count survives and the unreadable source's current file counts.
        assert_eq!(result.source_line_counts.get("steam-access.log"), Some(&2));
        assert_eq!(
            result.source_line_counts.get("blizzard-access.log"),
            Some(&1)
        );
        assert_eq!(result.lines_processed, 3);
        assert_eq!(result.files_processed, 3);
        assert!(!result.cancelled);
        // Exactly one source hit an unreadable member, so the total is partial.
        assert_eq!(result.files_with_errors, 1);

        let progress = read_progress(&progress_path);
        assert_eq!(progress["status"], "completed");
        assert_eq!(progress["lines_processed"], 3);
        assert_eq!(progress["files_with_errors"], 1);
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
