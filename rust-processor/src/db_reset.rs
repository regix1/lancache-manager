use anyhow::{Context, Result};
use serde::Serialize;
use serde_json::{json, Map, Value};
use std::env;
use std::fs;
use std::path::Path;
use std::time::Instant;

mod cancel;
mod db;
mod progress_events;
mod progress_utils;

use progress_events::ProgressReporter;

#[derive(Serialize)]
struct ProgressData {
    #[serde(rename = "isProcessing")]
    is_processing: bool,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: String,
    message: String,
    #[serde(rename = "stageKey")]
    stage_key: String,
    context: Value,
    #[serde(rename = "tablesCleared")]
    tables_cleared: usize,
    #[serde(rename = "totalTables")]
    total_tables: usize,
    #[serde(rename = "filesDeleted")]
    files_deleted: usize,
    timestamp: String,
}

impl ProgressData {
    fn new(
        is_processing: bool,
        percent_complete: f64,
        status: String,
        message: String,
        tables_cleared: usize,
        total_tables: usize,
        files_deleted: usize,
    ) -> Self {
        Self {
            is_processing,
            percent_complete,
            status,
            message,
            stage_key: String::new(),
            context: Value::Object(Map::new()),
            tables_cleared,
            total_tables,
            files_deleted,
            timestamp: progress_utils::current_timestamp(),
        }
    }
}

/// Builds one complete stage context, persists it, then emits that exact same context on stdout.
/// The progress file is always written first so an event-driven host can safely use stdout as a
/// wake-up for the durable checkpoint.
fn write_progress(
    progress_path: &Path,
    reporter: &ProgressReporter,
    stage_key: &str,
    progress: &mut ProgressData,
    stage_context: Value,
) -> Result<()> {
    let mut context = Map::from_iter([
        ("tablesCleared".to_string(), json!(progress.tables_cleared)),
        ("totalTables".to_string(), json!(progress.total_tables)),
        ("filesDeleted".to_string(), json!(progress.files_deleted)),
        ("message".to_string(), json!(progress.message)),
    ]);
    if let Value::Object(additions) = stage_context {
        context.extend(additions);
    }

    progress.stage_key = stage_key.to_string();
    progress.context = Value::Object(context);
    progress_utils::write_progress_json(progress_path, progress)?;

    let context = progress.context.clone();

    match progress.status.as_str() {
        "starting" => reporter.emit_started(stage_key, context),
        "completed" => reporter.emit_complete(stage_key, context),
        "cancelled" => reporter.emit_cancelled(stage_key, context),
        "error" => reporter.emit_failed(stage_key, context, Some(progress.message.clone())),
        _ => reporter.emit_progress(progress.percent_complete, stage_key, context),
    }

    Ok(())
}

async fn reset_database(
    data_directory: &str,
    progress_path: &Path,
    reporter: &ProgressReporter,
) -> Result<()> {
    let start_time = Instant::now();

    println!("Starting database reset...");
    println!("Data directory: {}", data_directory);
    println!("Progress file: {}", progress_path.display());

    // Initial progress
    let mut progress = ProgressData::new(
        true,
        0.0,
        "starting".to_string(),
        "Starting database reset...".to_string(),
        0,
        4,
        0,
    );
    write_progress(
        progress_path,
        reporter,
        "signalr.dbReset.starting",
        &mut progress,
        json!({}),
    )?;

    // Create PostgreSQL connection pool
    let pool = db::create_pool().await?;

    let tables = vec!["LogEntries", "Downloads", "ClientStats", "ServiceStats"];

    // Disable foreign key constraints using session replication role
    // (equivalent to PostgreSQL's way to bypass FK checks during bulk delete)
    sqlx::query("SET session_replication_role = 'replica'")
        .execute(&pool)
        .await
        .context("Failed to disable foreign key checks")?;

    let mut tables_cleared = 0;
    let batch_size = 5000i64;

    // Count total rows across all tables for accurate progress
    let mut total_rows = 0i64;
    for table_name in &tables {
        let count_sql = format!("SELECT COUNT(*) FROM \"{}\"", table_name);
        match sqlx::query_scalar::<_, i64>(&count_sql)
            .fetch_one(&pool)
            .await
        {
            Ok(count) => total_rows += count,
            Err(e) => eprintln!("Warning: Failed to count {}: {}", table_name, e),
        }
    }

    println!("Total rows to delete: {}", total_rows);

    let mut deleted_rows = 0i64;

    for table_name in &tables {
        // Cooperative cancel: check between table-level iterations
        if cancel::is_cancelled() {
            println!(
                "Cancel requested — stopping at table boundary ({} of {} tables cleared)",
                tables_cleared,
                tables.len()
            );
            progress.is_processing = false;
            progress.status = "cancelled".to_string();
            progress.message = format!(
                "Database reset cancelled. Cleared {} of {} tables, deleted {} files.",
                tables_cleared,
                tables.len(),
                0usize
            );
            write_progress(
                progress_path,
                reporter,
                "signalr.dbReset.cancelled",
                &mut progress,
                json!({}),
            )?;
            return Ok(());
        }

        println!("Clearing table: {}", table_name);

        // Count rows in this table
        let count_sql = format!("SELECT COUNT(*) FROM \"{}\"", table_name);
        let table_row_count = match sqlx::query_scalar::<_, i64>(&count_sql)
            .fetch_one(&pool)
            .await
        {
            Ok(count) => count,
            Err(e) => {
                eprintln!("  Warning: Failed to count {}: {}", table_name, e);
                0
            }
        };

        println!("  Rows in {}: {}", table_name, table_row_count);

        // Delete in batches with progress reporting
        let mut batch_num = 0;
        let mut table_had_error = false;
        loop {
            // PostgreSQL uses ctid for row-level deletion in batches (equivalent to SQLite's rowid)
            let delete_sql = format!(
                "DELETE FROM \"{}\" WHERE ctid IN (SELECT ctid FROM \"{}\" LIMIT {})",
                table_name, table_name, batch_size
            );

            match sqlx::query(&delete_sql).execute(&pool).await {
                Ok(result) => {
                    let deleted = result.rows_affected();
                    if deleted == 0 {
                        break; // No more rows to delete
                    }

                    deleted_rows += deleted as i64;
                    batch_num += 1;

                    // Calculate progress: 0% to 85% based on rows deleted
                    // Reserve 85-100% for cleanup
                    let overall_progress = if total_rows > 0 {
                        (deleted_rows as f64 / total_rows as f64) * 85.0
                    } else {
                        0.0
                    };

                    progress.message = format!(
                        "Clearing {}... ({} / {} rows)",
                        table_name, deleted_rows, total_rows
                    );
                    progress.percent_complete = overall_progress.min(85.0);
                    progress.status = "deleting".to_string();
                    write_progress(
                        progress_path,
                        reporter,
                        "signalr.dbReset.deleting",
                        &mut progress,
                        json!({
                            "tableName": table_name,
                            "deletedRows": deleted_rows,
                            "totalRows": total_rows,
                        }),
                    )?;

                    if batch_num % 10 == 0 {
                        println!(
                            "  Batch {}: Deleted {} rows (total: {} / {})",
                            batch_num, deleted, deleted_rows, total_rows
                        );
                    }

                    // Cooperative cancel: check after each batch completes (at a clean batch boundary)
                    if cancel::is_cancelled() {
                        println!(
                            "Cancel requested — stopping after batch {} ({} rows deleted)",
                            batch_num, deleted_rows
                        );
                        progress.is_processing = false;
                        progress.tables_cleared = tables_cleared;
                        progress.status = "cancelled".to_string();
                        progress.message = format!(
                            "Database reset cancelled after {} rows deleted ({} of {} tables fully cleared).",
                            deleted_rows, tables_cleared, tables.len()
                        );
                        write_progress(
                            progress_path,
                            reporter,
                            "signalr.dbReset.cancelled",
                            &mut progress,
                            json!({}),
                        )?;
                        return Ok(());
                    }
                }
                Err(e) => {
                    eprintln!(
                        "  Warning: Failed to delete batch from {}: {}",
                        table_name, e
                    );
                    table_had_error = true;
                    break;
                }
            }
        }

        if !table_had_error {
            tables_cleared += 1;
        }
        progress.tables_cleared = tables_cleared;
        println!(
            "  Completed: {} (total deleted: {})",
            table_name, deleted_rows
        );
    }

    // Re-enable foreign key constraints
    sqlx::query("SET session_replication_role = 'DEFAULT'")
        .execute(&pool)
        .await
        .context("Failed to re-enable foreign key checks")?;

    // PostgreSQL doesn't need VACUUM to reclaim space (autovacuum handles it)
    // But we can run ANALYZE to update statistics
    progress.message = "Optimizing database...".to_string();
    progress.percent_complete = 88.0;
    progress.status = "optimizing".to_string();
    write_progress(
        progress_path,
        reporter,
        "signalr.dbReset.optimizing",
        &mut progress,
        json!({}),
    )?;

    println!("Running ANALYZE to update statistics...");
    sqlx::query("ANALYZE")
        .execute(&pool)
        .await
        .context("Failed to analyze database")?;

    println!("Database cleared successfully");

    // Clean up files
    progress.message = "Cleaning up files...".to_string();
    progress.percent_complete = 90.0;
    progress.status = "cleanup".to_string();
    write_progress(
        progress_path,
        reporter,
        "signalr.dbReset.cleanup",
        &mut progress,
        json!({}),
    )?;

    // Cooperative cancel: check before file deletion loop
    if cancel::is_cancelled() {
        println!(
            "Cancel requested — stopping before file cleanup ({} tables cleared)",
            tables_cleared
        );
        progress.is_processing = false;
        progress.tables_cleared = tables_cleared;
        progress.status = "cancelled".to_string();
        progress.message = format!(
            "Database reset cancelled. Cleared {} of {} tables (all rows deleted), no data files removed.",
            tables_cleared, tables.len()
        );
        write_progress(
            progress_path,
            reporter,
            "signalr.dbReset.cancelled",
            &mut progress,
            json!({}),
        )?;
        return Ok(());
    }

    let mut files_deleted = 0;
    let files_to_delete = vec![
        "position.txt",
        "performance_data.json",
        "processing.marker",
        "rust_progress.json",
    ];

    for file_name in files_to_delete {
        let file_path = Path::new(data_directory).join(file_name);
        if file_path.exists() {
            match fs::remove_file(&file_path) {
                Ok(_) => {
                    println!("Deleted file: {}", file_path.display());
                    files_deleted += 1;
                    progress.files_deleted = files_deleted;
                }
                Err(e) => {
                    eprintln!("Warning: Failed to delete {}: {}", file_path.display(), e);
                }
            }
        }
    }

    let elapsed = start_time.elapsed();

    // Final progress
    progress.is_processing = false;
    progress.percent_complete = 100.0;
    progress.status = "completed".to_string();
    progress.message = format!(
        "Database reset completed in {:.2}s. Cleared {} tables, deleted {} files.",
        elapsed.as_secs_f64(),
        tables_cleared,
        files_deleted
    );
    write_progress(
        progress_path,
        reporter,
        "signalr.dbReset.complete",
        &mut progress,
        json!({}),
    )?;

    println!("\nDatabase reset completed successfully!");
    println!("  Tables cleared: {}", tables_cleared);
    println!("  Files deleted: {}", files_deleted);
    println!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    Ok(())
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let mut args: Vec<String> = env::args().collect();

    // Emit JSON progress events to stdout (mirrors cache_clear.rs/cache_game_detect.rs's
    // `-p`/`--progress` flag). Stripped before the existing positional-argument checks below so
    // it can be appended anywhere without disturbing the required <data_directory>
    // <progress_json_path> positions.
    let progress_enabled =
        if let Some(pos) = args.iter().position(|a| a == "--progress" || a == "-p") {
            args.remove(pos);
            true
        } else {
            false
        };

    if args.len() != 3 {
        eprintln!("Usage: database_reset <data_directory> <progress_json_path> [--progress]");
        eprintln!("\nExample:");
        eprintln!("  database_reset ./data ./data/reset_progress.json");
        eprintln!(
            "\nNote: Database connection is configured via DATABASE_URL environment variable."
        );
        anyhow::bail!("invalid arguments");
    }

    cancel::install();

    let data_directory = &args[1];
    let progress_path = Path::new(&args[2]);
    let reporter = ProgressReporter::new(progress_enabled);

    // Create data directory if it doesn't exist
    if let Err(e) = fs::create_dir_all(data_directory) {
        eprintln!("Failed to create data directory: {}", e);
        anyhow::bail!("failed to create data directory: {e}");
    }

    match reset_database(data_directory, progress_path, &reporter).await {
        Ok(_) => Ok(()),
        Err(e) => {
            eprintln!("Error: {:?}", e);

            // Write error progress
            let mut error_progress = ProgressData::new(
                false,
                0.0,
                "error".to_string(),
                format!("Database reset failed: {}", e),
                0,
                4,
                0,
            );
            let error_detail = format!("Database reset failed: {e}");
            let _ = write_progress(
                progress_path,
                &reporter,
                "signalr.dbReset.error.fatal",
                &mut error_progress,
                json!({ "errorDetail": error_detail }),
            );

            Err(e)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn progress_file_persists_complete_deleting_stage_context() {
        let directory = tempfile::tempdir().expect("temp directory");
        let progress_path = directory.path().join("reset-progress.json");
        let reporter = ProgressReporter::new(false);
        let mut progress = ProgressData::new(
            true,
            42.0,
            "deleting".to_string(),
            "Clearing Downloads".to_string(),
            1,
            4,
            2,
        );

        write_progress(
            &progress_path,
            &reporter,
            "signalr.dbReset.deleting",
            &mut progress,
            json!({
                "tableName": "Downloads",
                "deletedRows": 12,
                "totalRows": 30,
            }),
        )
        .expect("write progress");

        let saved: Value = serde_json::from_slice(&fs::read(progress_path).expect("read progress"))
            .expect("deserialize progress");
        assert_eq!(saved["stageKey"], "signalr.dbReset.deleting");
        assert_eq!(saved["context"], progress.context);
        assert_eq!(saved["context"]["tableName"], "Downloads");
        assert_eq!(saved["context"]["deletedRows"], 12);
        assert_eq!(saved["context"]["totalRows"], 30);
        assert_eq!(saved["context"]["tablesCleared"], 1);
        assert_eq!(saved["context"]["filesDeleted"], 2);
    }

    #[test]
    fn fatal_progress_contains_error_detail_placeholder() {
        let directory = tempfile::tempdir().expect("temp directory");
        let progress_path = directory.path().join("reset-progress.json");
        let reporter = ProgressReporter::new(false);
        let mut progress = ProgressData::new(
            false,
            0.0,
            "error".to_string(),
            "reset failed".to_string(),
            0,
            4,
            0,
        );

        write_progress(
            &progress_path,
            &reporter,
            "signalr.dbReset.error.fatal",
            &mut progress,
            json!({ "errorDetail": "reset failed" }),
        )
        .expect("write progress");

        let saved: Value = serde_json::from_slice(&fs::read(progress_path).expect("read progress"))
            .expect("deserialize progress");
        assert_eq!(saved["context"]["errorDetail"], "reset failed");
    }
}
