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
// Mirrors the single-game flow in `cache_game_remove`, but batches N games into
// a single pass for drastically better performance when the user bulk-removes
// evicted data.

use anyhow::{Context, Result};
use clap::Parser;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::collections::HashSet;
use std::fs;
use std::path::Path;

mod log_discovery;
mod log_purge;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod service_utils;

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

    /// Emit ProgressReporter events to stdout (optional)
    #[arg(long)]
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
    permission_errors: usize,
    url_count: usize,
    depot_count: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

fn main() -> Result<()> {
    let args = Args::parse();

    let reporter = ProgressReporter::new(args.progress);
    reporter.emit_started("signalr.logPurge.reading", json!({}));

    let result = run_purge(&args, &reporter);

    match &result {
        Ok(report) => {
            let payload = serde_json::to_string_pretty(report)
                .context("Failed to serialize purge report")?;
            fs::write(&args.output_json, payload)
                .with_context(|| format!("Failed to write output JSON to {}", args.output_json))?;

            reporter.emit_complete("signalr.logPurge.complete", json!({ "linesRemoved": report.lines_removed, "permissionErrors": report.permission_errors }));
            eprintln!("Purged {} log lines across access.log files ({} permission errors)", report.lines_removed, report.permission_errors);
        }
        Err(e) => {
            let err_msg = format!("cache_purge_log_entries failed: {:#}", e);
            eprintln!("{}", err_msg);

            // Still try to write a failure report so the C# caller can parse it
            let failure = PurgeReport {
                success: false,
                lines_removed: 0,
                permission_errors: 0,
                url_count: 0,
                depot_count: 0,
                error: Some(err_msg.clone()),
            };
            let _ = fs::write(
                &args.output_json,
                serde_json::to_string_pretty(&failure).unwrap_or_else(|_| "{}".to_string()),
            );

            reporter.emit_failed("signalr.logPurge.error.fatal", json!({ "errorDetail": err_msg }));
            std::process::exit(1);
        }
    }

    Ok(())
}

fn run_purge(args: &Args, reporter: &ProgressReporter) -> Result<PurgeReport> {
    // Read input JSON
    reporter.emit_progress(5.0, "signalr.logPurge.reading", json!({}));
    let input_bytes = fs::read(&args.input_json)
        .with_context(|| format!("Failed to read input JSON {}", args.input_json))?;
    let request: PurgeRequest = serde_json::from_slice(&input_bytes)
        .with_context(|| format!("Failed to parse input JSON {}", args.input_json))?;

    let url_count = request.urls.len();
    let depot_count = request.depot_ids.len();

    if url_count == 0 && depot_count == 0 {
        eprintln!("No URLs or depot IDs supplied — nothing to purge");
        return Ok(PurgeReport {
            success: true,
            lines_removed: 0,
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
    reporter.emit_progress(
        15.0,
        "signalr.logPurge.purging",
        json!({ "urlCount": urls.len(), "depotCount": depot_ids.len() }),
    );

    // Validate log_dir exists
    let log_dir_path = Path::new(&args.log_dir);
    if !log_dir_path.exists() {
        anyhow::bail!("Log directory does not exist: {}", args.log_dir);
    }

    // Call the shared helper (same function used by cache_game_remove).
    // Pass a progress callback that maps per-file completion into the 15%-95% range
    // so the C# stdout reader sees granular progress between the existing ticks.
    let progress_cb = |files_done: usize, total_files: usize| {
        if total_files > 0 {
            let fraction = files_done as f64 / total_files as f64;
            // Map into [15, 95) — the gap between the "purging" and "rewrote" ticks
            let mapped = 15.0 + fraction * 80.0;
            reporter.emit_progress(
                mapped,
                "signalr.logPurge.purging",
                json!({ "urlCount": url_count, "depotCount": depot_count, "filesProcessed": files_done, "totalFiles": total_files }),
            );
        }
    };
    let (lines_removed, permission_errors) =
        remove_log_entries_for_game(log_dir_path, &urls, &depot_ids, Some(&progress_cb))
            .context("remove_log_entries_for_game failed")?;

    reporter.emit_progress(
        95.0,
        "signalr.logPurge.rewrote",
        json!({ "linesRemoved": lines_removed }),
    );

    Ok(PurgeReport {
        success: true,
        lines_removed,
        permission_errors,
        url_count,
        depot_count,
        error: None,
    })
}
