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
    reporter.emit_started();

    let result = run_purge(&args, &reporter);

    match &result {
        Ok(report) => {
            let payload = serde_json::to_string_pretty(report)
                .context("Failed to serialize purge report")?;
            fs::write(&args.output_json, payload)
                .with_context(|| format!("Failed to write output JSON to {}", args.output_json))?;

            let msg = format!(
                "Purged {} log lines across access.log files ({} permission errors)",
                report.lines_removed, report.permission_errors
            );
            reporter.emit_complete(&msg);
            eprintln!("{}", msg);
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

            reporter.emit_failed(&err_msg);
            std::process::exit(1);
        }
    }

    Ok(())
}

fn run_purge(args: &Args, reporter: &ProgressReporter) -> Result<PurgeReport> {
    // Read input JSON
    reporter.emit_progress(5.0, "Reading purge request JSON...");
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
        &format!(
            "Purging logs for {} URLs / {} depot IDs",
            urls.len(),
            depot_ids.len()
        ),
    );

    // Validate log_dir exists
    let log_dir_path = Path::new(&args.log_dir);
    if !log_dir_path.exists() {
        anyhow::bail!("Log directory does not exist: {}", args.log_dir);
    }

    // Call the shared helper (same function used by cache_game_remove)
    let (lines_removed, permission_errors) =
        remove_log_entries_for_game(log_dir_path, &urls, &depot_ids)
            .context("remove_log_entries_for_game failed")?;

    reporter.emit_progress(
        95.0,
        &format!("Rewrote logs — {} lines removed", lines_removed),
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
