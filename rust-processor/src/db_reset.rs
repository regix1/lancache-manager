use anyhow::{Context, Result};
use serde::Serialize;
use std::env;
use std::fs;
use std::path::Path;
use std::time::Instant;

mod db;
mod progress_utils;

#[derive(Serialize)]
struct ProgressData {
    #[serde(rename = "isProcessing")]
    is_processing: bool,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: String,
    message: String,
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
            tables_cleared,
            total_tables,
            files_deleted,
            timestamp: progress_utils::current_timestamp(),
        }
    }
}

fn write_progress(progress_path: &Path, progress: &ProgressData) -> Result<()> {
    progress_utils::write_progress_json(progress_path, progress)
}

async fn reset_database(
    data_directory: &str,
    progress_path: &Path,
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
    write_progress(progress_path, &progress)?;

    // Create PostgreSQL connection pool
    let pool = db::create_pool().await;

    let tables = vec![
        "LogEntries",
        "Downloads",
        "ClientStats",
        "ServiceStats",
    ];

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
        match sqlx::query_scalar::<_, i64>(&count_sql).fetch_one(&pool).await {
            Ok(count) => total_rows += count,
            Err(e) => eprintln!("Warning: Failed to count {}: {}", table_name, e),
        }
    }

    println!("Total rows to delete: {}", total_rows);

    let mut deleted_rows = 0i64;

    for table_name in &tables {
        println!("Clearing table: {}", table_name);

        // Count rows in this table
        let count_sql = format!("SELECT COUNT(*) FROM \"{}\"", table_name);
        let table_row_count = match sqlx::query_scalar::<_, i64>(&count_sql).fetch_one(&pool).await {
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

                    progress.message = format!("Clearing {}... ({} / {} rows)", table_name, deleted_rows, total_rows);
                    progress.percent_complete = overall_progress.min(85.0);
                    progress.status = "deleting".to_string();
                    write_progress(progress_path, &progress)?;

                    if batch_num % 10 == 0 {
                        println!("  Batch {}: Deleted {} rows (total: {} / {})", batch_num, deleted, deleted_rows, total_rows);
                    }
                }
                Err(e) => {
                    eprintln!("  Warning: Failed to delete batch from {}: {}", table_name, e);
                    table_had_error = true;
                    break;
                }
            }
        }

        if !table_had_error {
            tables_cleared += 1;
        }
        progress.tables_cleared = tables_cleared;
        println!("  Completed: {} (total deleted: {})", table_name, deleted_rows);
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
    write_progress(progress_path, &progress)?;

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
    write_progress(progress_path, &progress)?;

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
    write_progress(progress_path, &progress)?;

    println!("\nDatabase reset completed successfully!");
    println!("  Tables cleared: {}", tables_cleared);
    println!("  Files deleted: {}", files_deleted);
    println!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    Ok(())
}

#[tokio::main]
async fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() != 3 {
        eprintln!("Usage: database_reset <data_directory> <progress_json_path>");
        eprintln!("\nExample:");
        eprintln!("  database_reset ./data ./data/reset_progress.json");
        eprintln!("\nNote: Database connection is configured via DATABASE_URL environment variable.");
        std::process::exit(1);
    }

    let data_directory = &args[1];
    let progress_path = Path::new(&args[2]);

    // Create data directory if it doesn't exist
    if let Err(e) = fs::create_dir_all(data_directory) {
        eprintln!("Failed to create data directory: {}", e);
        std::process::exit(1);
    }

    match reset_database(data_directory, progress_path).await {
        Ok(_) => {
            std::process::exit(0);
        }
        Err(e) => {
            eprintln!("Error: {:?}", e);

            // Write error progress
            let error_progress = ProgressData::new(
                false,
                0.0,
                "error".to_string(),
                format!("Database reset failed: {}", e),
                0,
                4,
                0,
            );
            let _ = write_progress(progress_path, &error_progress);

            std::process::exit(1);
        }
    }
}
