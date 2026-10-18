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
// Batches N games into a single pass for drastically better performance when the
// user bulk-removes evicted data. Game and service removals run it as their own
// log step too, with the purge targets their removal binary reported.

use anyhow::{Context, Result};
use clap::Parser;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use lancache_processor::cancel;
use lancache_processor::log_layout::discover_log_sources;
use lancache_processor::log_purge;
use lancache_processor::log_resume;
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

    /// Path to input JSON file: { "urls": [...], "depot_ids": [...], "service": optional }
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

    /// The importer's resume record. It says which logs logrotate deleted since the last pass and
    /// which rotated logs the importer could not open, neither of which the purge can count.
    #[arg(long)]
    resume: Option<String>,

    /// Removes only lines the importer has read, below each stem's saved position; a line past
    /// it stays and is imported as usual.
    #[arg(long = "keep-unread-lines")]
    keep_unread_lines: bool,

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
    /// Set by a service removal: only that service's lines with a listed URL go.
    #[serde(default)]
    service: Option<String>,
}

#[derive(Debug, Serialize)]
struct PurgeReport {
    success: bool,
    lines_removed: u64,
    /// Removed-line count per log-source stem, series-wide - the caller subtracts these
    /// from the saved ingestion positions so a purge cannot shift them past unread lines.
    log_lines_removed_by_source: std::collections::HashMap<String, u64>,
    /// The already-read subset of the map above (series index below the saved position), plus,
    /// when that is above zero, the records of logs logrotate deleted that the position still
    /// counted; the amount the position itself comes back by.
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
#[allow(clippy::too_many_arguments)]
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
            progress_utils::emit_progress_event(
                reporter,
                status,
                stage_key,
                context,
                percent_complete,
            );
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
    let _ = write_progress(
        progress_path,
        &reporter,
        "starting",
        "signalr.logPurge.reading",
        json!({}),
        0.0,
        0,
        0,
    );

    // Single failure funnel: run_or_exit emits the structured `failed` event (with
    // errorDetail) exactly once, whether run_purge itself failed or a later step
    // (serialize/write output) did - no more hand-written write_progress("failed", ...)
    // racing a second emission.
    progress_events::run_or_exit(&reporter, "signalr.logPurge.error.fatal", || {
        match run_purge(&args, progress_path, &reporter) {
            Ok(report) => {
                let payload = serde_json::to_string_pretty(&report)
                    .context("Failed to serialize purge report")?;
                fs::write(&args.output_json, payload).with_context(|| {
                    format!("Failed to write output JSON to {}", args.output_json)
                })?;

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
                eprintln!(
                    "Purged {} log lines across access.log files ({} permission errors)",
                    report.lines_removed, report.permission_errors
                );
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

fn run_purge(
    args: &Args,
    progress_path: Option<&Path>,
    reporter: &ProgressReporter,
) -> Result<PurgeReport> {
    // Read input JSON
    let _ = write_progress(
        progress_path,
        reporter,
        "purging",
        "signalr.logPurge.reading",
        json!({}),
        5.0,
        0,
        0,
    );
    let input_bytes = fs::read(&args.input_json)
        .with_context(|| format!("Failed to read input JSON {}", args.input_json))?;
    let request: PurgeRequest = serde_json::from_slice(&input_bytes)
        .with_context(|| format!("Failed to parse input JSON {}", args.input_json))?;
    // The service match takes URLs only; a depot match would reach other services' lines.
    if request.service.is_some() && !request.depot_ids.is_empty() {
        anyhow::bail!("A service purge does not accept depot IDs");
    }

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

    // Call the shared helper. The game purge takes a progress callback that maps per-file
    // completion into the 15%-95% range so the host's progress-file poller sees granular
    // progress between the existing ticks; the service purge has no callback.
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
    let resume = args
        .resume
        .as_deref()
        .map(|path| log_resume::load(Path::new(path)));
    // A rotation since the importer's last pass can leave no log that shows where it stopped. The
    // purge then cannot tell the lines it read from the ones it has not, and keeping them all brings
    // back the rows a removal deletes, so it refuses; the repair imports again and redoes it.
    if let (true, Some(positions), Some(resume)) =
        (args.keep_unread_lines, &stem_positions, &resume)
    {
        for source in discover_log_sources(log_dir_path)?.sources {
            let Some(entry) = resume.stems.get(&source.stem) else {
                continue;
            };
            let paths: Vec<PathBuf> = source.files.iter().map(|file| file.path.clone()).collect();
            if entry.position > 0
                && positions.get(&source.stem) == Some(&entry.position)
                && log_resume::proven_deleted_records(entry, &paths).is_none()
            {
                anyhow::bail!(
                    "No log of {} shows where the importer stopped; import it again before the purge",
                    source.stem
                );
            }
        }
    }
    let purge_outcome = match &request.service {
        Some(service) => log_purge::remove_log_entries_for_service(
            log_dir_path,
            service,
            &urls,
            stem_positions.as_ref(),
            resume.as_ref(),
            args.keep_unread_lines,
        )
        .context("remove_log_entries_for_service failed")?,
        None => remove_log_entries_for_game(
            log_dir_path,
            &urls,
            &depot_ids,
            Some(&progress_cb),
            stem_positions.as_ref(),
            resume.as_ref(),
            args.keep_unread_lines,
        )
        .context("remove_log_entries_for_game failed")?,
    };
    let lines_removed = purge_outcome.lines_removed;
    let permission_errors = purge_outcome.permission_errors;
    let log_lines_removed_by_source = purge_outcome.lines_removed_by_stem;
    let log_lines_removed_before_position_by_source =
        purge_outcome.lines_removed_before_position_by_stem;

    // Cooperative cancellation: if cancel arrived during the purge, flush partial progress
    // with real counts and return Ok so main() exits 0 without writing a failed status.
    if cancel::is_cancelled() {
        eprintln!(
            "Cancellation confirmed — flushing partial progress ({} lines removed so far).",
            lines_removed
        );
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

#[cfg(test)]
mod tests {
    use super::*;

    fn log_line(service: &str, url: &str) -> String {
        format!(
            "[{service}] 192.0.2.10 / - - - [01/Oct/2026:10:00:00 +0000] \"GET {url} HTTP/1.1\" 200 1024 \"-\" \"Test\" \"HIT\" \"cdn.test\" \"-\"\n"
        )
    }

    fn purge(dir: &Path, request: serde_json::Value) -> Result<PurgeReport> {
        purge_with(dir, request, &[])
    }

    fn purge_with(dir: &Path, request: serde_json::Value, extra: &[&str]) -> Result<PurgeReport> {
        let input = dir.join("request.json");
        fs::write(&input, request.to_string()).unwrap();
        let mut argv = vec![
            "cache_purge_log_entries".to_string(),
            dir.join("logs").to_str().unwrap().to_string(),
            input.to_str().unwrap().to_string(),
            dir.join("report.json").to_str().unwrap().to_string(),
        ];
        argv.extend(extra.iter().map(|arg| arg.to_string()));
        let args = Args::try_parse_from(argv).unwrap();
        run_purge(&args, None, &ProgressReporter::new(false))
    }

    /// The importer has read the first two lines of access.log; the third is still unread, so
    /// the purge must leave exactly that line.
    fn purge_with_two_lines_read(temp: &Path, request: serde_json::Value) -> PurgeReport {
        let log_path = temp.join("logs").join("access.log");
        fs::create_dir_all(log_path.parent().unwrap()).unwrap();
        let unread = log_line("steam", "/depot/1/chunk/c");
        fs::write(
            &log_path,
            format!(
                "{}{}{unread}",
                log_line("steam", "/depot/1/chunk/a"),
                log_line("steam", "/depot/1/chunk/b")
            ),
        )
        .unwrap();
        let positions = temp.join("positions.json");
        fs::write(&positions, r#"{"access.log":2}"#).unwrap();

        let report = purge_with(
            temp,
            request,
            &[
                "--stem-positions",
                positions.to_str().unwrap(),
                "--resume",
                temp.join("resume.json").to_str().unwrap(),
                "--keep-unread-lines",
            ],
        )
        .unwrap();
        assert_eq!(fs::read_to_string(&log_path).unwrap(), unread);
        report
    }

    #[test]
    fn game_purge_keeps_lines_the_importer_has_not_read() {
        let temp = tempfile::tempdir().unwrap();
        let report =
            purge_with_two_lines_read(temp.path(), json!({ "urls": [], "depot_ids": [1] }));

        assert_eq!(report.lines_removed, 2);
    }

    #[test]
    fn service_purge_keeps_lines_the_importer_has_not_read() {
        let temp = tempfile::tempdir().unwrap();
        let report = purge_with_two_lines_read(
            temp.path(),
            json!({
                "service": "steam",
                "urls": ["/depot/1/chunk/a", "/depot/1/chunk/b", "/depot/1/chunk/c"]
            }),
        );

        assert_eq!(report.lines_removed, 2);
    }

    /// Purges depot 1 with the given saved position and the resume record in `resume.json`.
    fn purge_depot_one(temp: &Path, position: u64, extra: &[&str]) -> PurgeReport {
        let positions = temp.join("positions.json");
        fs::write(&positions, format!(r#"{{"access.log":{position}}}"#)).unwrap();
        let resume = temp.join("resume.json");
        let mut args = vec![
            "--stem-positions",
            positions.to_str().unwrap(),
            "--resume",
            resume.to_str().unwrap(),
        ];
        args.extend_from_slice(extra);
        purge_with(temp, json!({ "urls": [], "depot_ids": [1] }), &args).unwrap()
    }

    /// Purges depot 1 keeping unread lines, with the given saved position, and returns the current
    /// log afterwards.
    fn purge_keeping_unread(temp: &Path, position: u64) -> String {
        purge_depot_one(temp, position, &["--keep-unread-lines"]);
        fs::read_to_string(temp.join("logs").join("access.log")).unwrap()
    }

    #[test]
    fn a_damaged_rotated_log_keeps_the_unread_lines_after_it() {
        use std::io::Write;

        let temp = tempfile::tempdir().unwrap();
        let logs = temp.path().join("logs");
        fs::create_dir_all(&logs).unwrap();
        // The importer read two lines of this log before the damage, so its position counts them;
        // the purge cannot read the log and so cannot count them.
        let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
        for _ in 0..200 {
            encoder
                .write_all(log_line("steam", "/depot/2/chunk/old").as_bytes())
                .unwrap();
        }
        let mut damaged = encoder.finish().unwrap();
        damaged.truncate(damaged.len() / 2);
        fs::write(logs.join("access.log.1.gz"), damaged).unwrap();
        let unread = log_line("steam", "/depot/1/chunk/c");
        fs::write(
            logs.join("access.log"),
            format!(
                "{}{}{unread}",
                log_line("steam", "/depot/1/chunk/a"),
                log_line("steam", "/depot/1/chunk/b")
            ),
        )
        .unwrap();

        let remaining = purge_keeping_unread(temp.path(), 4);

        assert!(remaining.ends_with(&unread), "{remaining}");
    }

    fn mark(path: &Path, records: u64, unread: bool) -> log_resume::FileMark {
        log_resume::FileMark {
            identity: log_purge::file_identity(path).unwrap(),
            len: fs::metadata(path).unwrap().len(),
            records,
            offset: None,
            tail_crc: None,
            unread,
        }
    }

    /// Saves the importer's record of having read `read` from the current log at `position`.
    fn save_resume(temp: &Path, position: u64, older_files: Vec<log_resume::FileMark>, read: &str) {
        let current = temp.join("logs").join("access.log");
        let mut resume = log_resume::ResumeFile::default();
        resume.stems.insert(
            "access.log".to_string(),
            log_resume::StemResume {
                position,
                older_files,
                file_identity: log_purge::file_identity(&current).unwrap(),
                offset: read.len() as u64,
                file_records: read.lines().count() as u64,
                tail_crc: log_resume::tail_crc(&current, read.len() as u64).unwrap(),
            },
        );
        log_resume::save(&temp.join("resume.json"), &resume).unwrap();
    }

    /// The importer read three lines of access.log.2, two of access.log.1 and the game's lines a
    /// and b, so its position is 7. logrotate then deletes access.log.2 and the game's line c
    /// arrives before the next pass. Returns line c.
    fn logrotate_deleted_the_oldest_log(temp: &Path) -> String {
        let logs = temp.join("logs");
        fs::create_dir_all(&logs).unwrap();
        let oldest = logs.join("access.log.2");
        let rotated = logs.join("access.log.1");
        let current = logs.join("access.log");
        let other = log_line("steam", "/depot/2/chunk/old");
        fs::write(&oldest, other.repeat(3)).unwrap();
        fs::write(&rotated, other.repeat(2)).unwrap();
        let read = format!(
            "{}{}",
            log_line("steam", "/depot/1/chunk/a"),
            log_line("steam", "/depot/1/chunk/b")
        );
        fs::write(&current, &read).unwrap();
        save_resume(
            temp,
            7,
            vec![mark(&oldest, 3, false), mark(&rotated, 2, false)],
            &read,
        );
        fs::remove_file(&oldest).unwrap();
        let unread = log_line("steam", "/depot/1/chunk/c");
        fs::write(&current, format!("{read}{unread}")).unwrap();
        unread
    }

    #[test]
    fn a_log_logrotate_deleted_does_not_make_unread_lines_look_read() {
        let temp = tempfile::tempdir().unwrap();
        let unread = logrotate_deleted_the_oldest_log(temp.path());

        let report = purge_depot_one(temp.path(), 7, &["--keep-unread-lines"]);

        assert_eq!(
            fs::read_to_string(temp.path().join("logs").join("access.log")).unwrap(),
            unread
        );
        // The position drops by a and b and by the deleted log's three records, so the next pass
        // skips the two lines of access.log.1 and imports c.
        assert_eq!(
            report.log_lines_removed_before_position_by_source["access.log"],
            5
        );
    }

    /// The importer read the old line of access.log.1 and lines a and b of access.log, so its
    /// position is 3. Line c arrives unread; then rotate 1 + compress without delaycompress deletes
    /// the read rotation, compresses the current log into a new file and starts a new current log
    /// with line d, so no log with a saved identity is left.
    fn rotate_without_anchor(temp: &Path) {
        use std::io::Write;

        let logs = temp.join("logs");
        fs::create_dir_all(&logs).unwrap();
        let rotated = logs.join("access.log.1");
        let current = logs.join("access.log");
        fs::write(&rotated, log_line("steam", "/depot/2/chunk/old")).unwrap();
        let read = format!(
            "{}{}",
            log_line("steam", "/depot/1/chunk/a"),
            log_line("steam", "/depot/1/chunk/b")
        );
        fs::write(&current, &read).unwrap();
        save_resume(temp, 3, vec![mark(&rotated, 1, false)], &read);
        let unread = log_line("steam", "/depot/1/chunk/c");
        fs::remove_file(&rotated).unwrap();
        let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
        encoder
            .write_all(format!("{read}{unread}").as_bytes())
            .unwrap();
        fs::write(logs.join("access.log.1.gz"), encoder.finish().unwrap()).unwrap();
        fs::remove_file(&current).unwrap();
        fs::write(&current, log_line("steam", "/depot/1/chunk/d")).unwrap();
    }

    fn log_files(temp: &Path) -> Vec<Vec<u8>> {
        ["access.log.1.gz", "access.log"]
            .iter()
            .map(|name| fs::read(temp.join("logs").join(name)).unwrap())
            .collect()
    }

    #[test]
    fn a_rotation_that_leaves_no_anchor_refuses_a_purge_that_keeps_unread_lines() {
        let temp = tempfile::tempdir().unwrap();
        rotate_without_anchor(temp.path());
        let before = log_files(temp.path());
        let positions = temp.path().join("positions.json");
        fs::write(&positions, r#"{"access.log":3}"#).unwrap();

        let error = purge_with(
            temp.path(),
            json!({ "urls": [], "depot_ids": [1] }),
            &[
                "--stem-positions",
                positions.to_str().unwrap(),
                "--resume",
                temp.path().join("resume.json").to_str().unwrap(),
                "--keep-unread-lines",
            ],
        )
        .expect_err("a purge that cannot place the read lines must refuse");

        assert!(error.to_string().contains("import it again"), "{error}");
        assert_eq!(log_files(temp.path()), before);
    }

    #[test]
    fn a_purge_with_no_anchor_still_runs_when_it_need_not_place_the_read_lines() {
        // An eviction purge counts every line as read.
        let eviction = tempfile::tempdir().unwrap();
        rotate_without_anchor(eviction.path());
        assert_eq!(purge_depot_one(eviction.path(), 3, &[]).lines_removed, 4);

        // A position the record does not match is used as it is, as before.
        let stale = tempfile::tempdir().unwrap();
        rotate_without_anchor(stale.path());
        assert_eq!(
            purge_depot_one(stale.path(), 2, &["--keep-unread-lines"]).lines_removed,
            2
        );

        // A position of 0 read nothing, so there is nothing to place.
        let unread = tempfile::tempdir().unwrap();
        let current = unread.path().join("logs").join("access.log");
        fs::create_dir_all(current.parent().unwrap()).unwrap();
        fs::write(&current, "").unwrap();
        save_resume(unread.path(), 0, Vec::new(), "");
        fs::remove_file(&current).unwrap();
        fs::write(&current, log_line("steam", "/depot/1/chunk/d")).unwrap();
        assert_eq!(
            purge_depot_one(unread.path(), 0, &["--keep-unread-lines"]).lines_removed,
            0
        );
    }

    #[test]
    fn an_eviction_purge_takes_a_log_logrotate_deleted_off_the_position() {
        let temp = tempfile::tempdir().unwrap();
        logrotate_deleted_the_oldest_log(temp.path());

        let report = purge_depot_one(temp.path(), 7, &[]);

        assert_eq!(report.lines_removed, 3);
        assert_eq!(
            report.log_lines_removed_before_position_by_source["access.log"],
            5
        );
    }

    #[test]
    fn a_rotated_log_the_importer_could_not_open_keeps_its_lines() {
        let temp = tempfile::tempdir().unwrap();
        let logs = temp.path().join("logs");
        fs::create_dir_all(&logs).unwrap();
        let rotated = logs.join("access.log.1");
        let never_read = format!(
            "{}{}",
            log_line("steam", "/depot/1/chunk/x"),
            log_line("steam", "/depot/1/chunk/y")
        );
        fs::write(&rotated, &never_read).unwrap();
        let other = log_line("steam", "/depot/2/chunk/other");
        let read = format!(
            "{}{}{other}",
            log_line("steam", "/depot/1/chunk/a"),
            log_line("steam", "/depot/1/chunk/b")
        );
        fs::write(logs.join("access.log"), &read).unwrap();
        // The importer could not open access.log.1, so its position 3 counts only access.log.
        save_resume(temp.path(), 3, vec![mark(&rotated, 0, true)], &read);

        let remaining = purge_keeping_unread(temp.path(), 3);

        assert_eq!(remaining, other);
        assert_eq!(fs::read_to_string(&rotated).unwrap(), never_read);
    }

    #[test]
    fn service_purge_removes_only_that_services_lines_with_a_listed_url() {
        let temp = tempfile::tempdir().unwrap();
        let log_path = temp.path().join("logs").join("access.log");
        fs::create_dir_all(log_path.parent().unwrap()).unwrap();
        let kept_other_service = log_line("epicgames", "/depot/1/chunk/a");
        let kept_other_url = log_line("steam", "/depot/1/chunk/b");
        fs::write(
            &log_path,
            format!(
                "{}{kept_other_service}{kept_other_url}",
                log_line("steam", "/depot/1/chunk/a")
            ),
        )
        .unwrap();

        let report = purge(
            temp.path(),
            json!({ "service": "steam", "urls": ["/depot/1/chunk/a"] }),
        )
        .unwrap();

        assert_eq!(report.lines_removed, 1);
        assert_eq!(
            fs::read_to_string(&log_path).unwrap(),
            format!("{kept_other_service}{kept_other_url}")
        );
    }

    #[test]
    fn service_purge_with_depot_ids_fails_and_leaves_the_log_alone() {
        let temp = tempfile::tempdir().unwrap();
        let log_path = temp.path().join("logs").join("access.log");
        fs::create_dir_all(log_path.parent().unwrap()).unwrap();
        let contents = log_line("steam", "/depot/1/chunk/a");
        fs::write(&log_path, &contents).unwrap();

        let error = purge(
            temp.path(),
            json!({ "service": "steam", "urls": ["/depot/1/chunk/a"], "depot_ids": [1] }),
        )
        .expect_err("a service purge with depot IDs must fail");

        assert!(error.to_string().contains("depot IDs"));
        assert_eq!(fs::read_to_string(&log_path).unwrap(), contents);
    }
}
