// Bulk-purge nginx access.log entries for a collection of evicted games.
//
// This binary is called by C#'s `CacheReconciliationService.RemoveEvictedRecordsAsync`
// after the eviction scan flips `Downloads.IsEvicted = true`. It reads a JSON
// file containing the union of all evicted URLs and depot IDs, then invokes the
// shared `remove_log_entries_for_game` helper ONCE to rewrite all access.log
// files under the supplied log directory. The rewritten logs no longer contain
// entries for the evicted games, which prevents a subsequent `ResetLogPosition`
// from resurrecting them on log re-parse.
//
// Mirrors the single-game flow in `cache_steam_remove`, but batches N games into
// a single pass for drastically better performance when the user bulk-removes
// evicted data.

use anyhow::{Context, Result};
use clap::Parser;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::collections::HashSet;
use std::fs;
use std::path::Path;

use lancache_processor::cancel;
use lancache_processor::log_purge;
use lancache_processor::progress_events;
use lancache_processor::progress_utils;
use log_purge::remove_log_entries_for_game;
use progress_events::ProgressReporter;

/// Bulk purge log entries for a set of evicted games, given their URL + depot_id union.
#[derive(clap::Parser, Debug)]
#[command(name = "cache_purge_log_entries")]
#[command(about = "Rewrites all access.log files to remove entries for a batch of evicted games")]
struct Args {
    /// Directory containing log files (e.g. /logs or H:/logs)
    log_dir: String,

    /// Path to input JSON file: { "urls": [...], "depot_ids": [...] }
    input_json: String,

    /// Path to output JSON file: { "lines_removed": u64, "permission_errors": usize }
    output_json: String,

    /// Path to a progress JSON file the host polls (optional). Same mechanism and
    /// camelCase schema as every other cache_* binary; omit for silent runs.
    #[arg(long)]
    progress_json: Option<String>,

    /// Per-stem saved ingestion positions (JSON object of stem name to line index). Lets the
    /// log purge split removed lines at the read position so the position adjustment is exact.
    #[arg(long = "stem-positions")]
    stem_positions: Option<String>,

    /// Emit JSON progress events to stdout
    #[arg(short, long)]
    progress: bool,
}

#[derive(Debug, Deserialize)]
struct PurgeRequest {
    #[serde(default)]
    urls: Vec<String>,
    #[serde(default)]
    depot_ids: Vec<u32>,
}

#[derive(Debug, Serialize)]
struct PurgeReport {
    success: bool,
    lines_removed: u64,
    /// Removed-line count per log-source stem, series-wide - the caller subtracts these
    /// from the saved ingestion positions so a purge cannot shift them past unread lines.
    log_lines_removed_by_source: std::collections::HashMap<String, u64>,
    /// The already-read subset of the map above (series index below the saved position);
    /// the amount the position itself comes back by.
    log_lines_removed_before_position_by_source: std::collections::HashMap<String, u64>,
    permission_errors: usize,
    url_count: usize,
    depot_count: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

/// Writes the (optional) progress file exactly as before, THEN emits the matching stdout event
/// via `reporter` (file write always happens first when a path is supplied; the reporter itself
/// is independently gated by the `--progress` flag). No-ops the file write when no progress path
/// was supplied (silent runs) but still emits stdout when `--progress` is set. The struct and the
/// file-write-then-emit body live once in `progress_utils`; this wrapper only adapts the `Option`
/// this binary's silent-run mode needs.
fn write_progress(
    progress_path: Option<&Path>,
    reporter: &ProgressReporter,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
) -> Result<()> {
    match progress_path {
        Some(path) => progress_utils::write_progress(
            path,
            reporter,
            status,
            stage_key,
            context,
            percent_complete,
            files_processed,
            total_files,
        ),
        None => {
            progress_utils::emit_progress_event(reporter, status, stage_key, context, percent_complete);
            Ok(())
        }
    }
}

fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();
    let reporter = ProgressReporter::new(args.progress);

    let progress_path_buf = args.progress_json.clone().map(std::path::PathBuf::from);
    let progress_path = progress_path_buf.as_deref();
    let _ = write_progress(progress_path, &reporter, "starting", "signalr.logPurge.reading", json!({}), 0.0, 0, 0);

    // Single failure funnel: run_or_exit emits the structured `failed` event (with
    // errorDetail) exactly once, whether run_purge itself failed or a later step
    // (serialize/write output) did - no more hand-written write_progress("failed", ...)
    // racing a second emission.
    progress_events::run_or_exit(&reporter, "signalr.logPurge.error.fatal", || {
        match run_purge(&args, progress_path, &reporter) {
            Ok(report) => {
                let payload = serde_json::to_string_pretty(&report)
                    .context("Failed to serialize purge report")?;
                fs::write(&args.output_json, payload)
                    .with_context(|| format!("Failed to write output JSON to {}", args.output_json))?;

                let _ = write_progress(
                    progress_path,
                    &reporter,
                    "completed",
                    "signalr.logPurge.complete",
                    json!({ "linesRemoved": report.lines_removed, "permissionErrors": report.permission_errors }),
                    100.0,
                    0,
                    0,
                );
                eprintln!("Purged {} log lines across access.log files ({} permission errors)", report.lines_removed, report.permission_errors);
                Ok(())
            }
            Err(e) => {
                let err_msg = format!("cache_purge_log_entries failed: {:#}", e);
                eprintln!("{}", err_msg);

                // Still try to write a failure report so the C# caller can parse it -
                // this is the data-file contract, independent of the stdout event stream.
                let failure = PurgeReport {
                    success: false,
                    lines_removed: 0,
                    log_lines_removed_by_source: Default::default(),
                    log_lines_removed_before_position_by_source: Default::default(),
                    permission_errors: 0,
                    url_count: 0,
                    depot_count: 0,
                    error: Some(err_msg.clone()),
                };
                let _ = fs::write(
                    &args.output_json,
                    serde_json::to_string_pretty(&failure).unwrap_or_else(|_| "{}".to_string()),
                );

                Err(e)
            }
        }
    });

    Ok(())
}

fn run_purge(args: &Args, progress_path: Option<&Path>, reporter: &ProgressReporter) -> Result<PurgeReport> {
    // Read input JSON
    let _ = write_progress(progress_path, reporter, "purging", "signalr.logPurge.reading", json!({}), 5.0, 0, 0);
    let input_bytes = fs::read(&args.input_json)
        .with_context(|| format!("Failed to read input JSON {}", args.input_json))?;
    let request: PurgeRequest = serde_json::from_slice(&input_bytes)
        .with_context(|| format!("Failed to parse input JSON {}", args.input_json))?;

    let url_count = request.urls.len();
    let depot_count = request.depot_ids.len();

    if url_count == 0 && depot_count == 0 {
        eprintln!("No URLs or depot IDs supplied - nothing to purge");
        return Ok(PurgeReport {
            success: true,
            lines_removed: 0,
            log_lines_removed_by_source: Default::default(),
            log_lines_removed_before_position_by_source: Default::default(),
            permission_errors: 0,
            url_count: 0,
            depot_count: 0,
            error: None,
        });
    }

    let urls: HashSet<String> = request.urls.into_iter().collect();
    let depot_ids: HashSet<u32> = request.depot_ids.into_iter().collect();

    eprintln!(
        "Starting bulk log purge: {} URLs, {} depot IDs, log_dir={}",
        urls.len(),
        depot_ids.len(),
        args.log_dir
    );
    let _ = write_progress(
        progress_path,
        reporter,
        "purging",
        "signalr.logPurge.purging",
        json!({ "urlCount": urls.len(), "depotCount": depot_ids.len() }),
        15.0,
        0,
        0,
    );

    // Validate log_dir exists
    let log_dir_path = Path::new(&args.log_dir);
    if !log_dir_path.exists() {
        anyhow::bail!("Log directory does not exist: {}", args.log_dir);
    }

    // Call the shared helper (same function used by cache_steam_remove).
    // Pass a progress callback that maps per-file completion into the 15%-95% range
    // so the host's progress-file poller sees granular progress between the existing ticks.
    let progress_cb = |files_done: usize, total_files: usize| {
        if total_files > 0 {
            let fraction = files_done as f64 / total_files as f64;
            // Map into [15, 95) - the gap between the "purging" and "rewrote" ticks
            let mapped = 15.0 + fraction * 80.0;
            let _ = write_progress(
                progress_path,
                reporter,
                "purging",
                "signalr.logPurge.purging",
                json!({ "urlCount": url_count, "depotCount": depot_count, "filesProcessed": files_done, "totalFiles": total_files }),
                mapped,
                files_done,
                total_files,
            );
        }
    };
    let stem_positions = args
        .stem_positions
        .as_deref()
        .and_then(log_purge::read_stem_positions);
    let purge_outcome = remove_log_entries_for_game(
        log_dir_path,
        &urls,
        &depot_ids,
        Some(&progress_cb),
        stem_positions.as_ref(),
    )
    .context("remove_log_entries_for_game failed")?;
    let lines_removed = purge_outcome.lines_removed;
    let permission_errors = purge_outcome.permission_errors;
    let log_lines_removed_by_source = purge_outcome.lines_removed_by_stem;
    let log_lines_removed_before_position_by_source =
        purge_outcome.lines_removed_before_position_by_stem;

    // Cooperative cancellation: if cancel arrived during the purge, flush partial progress
    // with real counts and return Ok so main() exits 0 without writing a failed status.
    if cancel::is_cancelled() {
        eprintln!("Cancellation confirmed — flushing partial progress ({} lines removed so far).", lines_removed);
        let _ = write_progress(
            progress_path,
            reporter,
            "purging",
            "signalr.logPurge.purging",
            json!({ "urlCount": url_count, "depotCount": depot_count, "linesRemoved": lines_removed }),
            95.0,
            0,
            0,
        );
        return Ok(PurgeReport {
            success: true,
            lines_removed,
            log_lines_removed_by_source: log_lines_removed_by_source.clone(),
            log_lines_removed_before_position_by_source:
                log_lines_removed_before_position_by_source.clone(),
            permission_errors,
            url_count,
            depot_count,
            error: None,
        });
    }

    let _ = write_progress(
        progress_path,
        reporter,
        "purging",
        "signalr.logPurge.rewrote",
        json!({ "linesRemoved": lines_removed }),
        95.0,
        0,
        0,
    );

    Ok(PurgeReport {
        success: true,
        lines_removed,
        log_lines_removed_by_source,
        log_lines_removed_before_position_by_source,
        permission_errors,
        url_count,
        depot_count,
        error: None,
    })
}
