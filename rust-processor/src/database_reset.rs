use anyhow::{Context, Result};
use chrono::Utc;
use rusqlite::Connection;
use serde::Serialize;
use std::env;
use std::fs;
use std::path::Path;
use std::time::Instant;

#[derive(Serialize, Clone)]
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
            timestamp: Utc::now().to_rfc3339(),
        }
    }
}

fn write_progress(progress_path: &Path, progress: &ProgressData) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;
    fs::write(progress_path, json)?;
    Ok(())
}

fn reset_database(
    db_path: &str,
    data_directory: &str,
    progress_path: &Path,
) -> Result<()> {
    let start_time = Instant::now();

    println!("Starting database reset...");
    println!("Database: {}", db_path);
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

    // Open database connection
    let conn = Connection::open(db_path)
        .context("Failed to open database")?;

    // Disable foreign key constraints for faster deletion
    conn.execute("PRAGMA foreign_keys = OFF", [])
        .context("Failed to disable foreign keys")?;

    let tables = vec![
        ("LogEntries", 10.0, 30.0),
        ("Downloads", 30.0, 50.0),
        ("ClientStats", 50.0, 70.0),
        ("ServiceStats", 70.0, 85.0),
    ];

    let mut tables_cleared = 0;

    for (table_name, _start_percent, end_percent) in &tables {
        progress.message = format!("Clearing {} table...", table_name);
        progress.percent_complete = *end_percent;
        progress.status = "deleting".to_string();
        write_progress(progress_path, &progress)?;

        println!("Clearing table: {}", table_name);

        // Use DELETE for clean removal
        let sql = format!("DELETE FROM {}", table_name);
        match conn.execute(&sql, []) {
            Ok(deleted) => {
                println!("  Deleted {} rows from {}", deleted, table_name);
                tables_cleared += 1;
                progress.tables_cleared = tables_cleared;
            }
            Err(e) => {
                eprintln!("  Warning: Failed to clear {}: {}", table_name, e);
                // Continue with other tables even if one fails
            }
        }
    }

    // Re-enable foreign key constraints
    conn.execute("PRAGMA foreign_keys = ON", [])
        .context("Failed to re-enable foreign keys")?;

    // Run VACUUM to reclaim space
    progress.message = "Optimizing database...".to_string();
    progress.percent_complete = 88.0;
    progress.status = "optimizing".to_string();
    write_progress(progress_path, &progress)?;

    println!("Running VACUUM to optimize database...");
    conn.execute("VACUUM", [])
        .context("Failed to vacuum database")?;

    // Close database connection
    drop(conn);
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
    progress.status = "complete".to_string();
    progress.message = format!(
        "Database reset completed in {:.2}s. Cleared {} tables, deleted {} files.",
        elapsed.as_secs_f64(),
        tables_cleared,
        files_deleted
    );
    write_progress(progress_path, &progress)?;

    println!("\n✓ Database reset completed successfully!");
    println!("  Tables cleared: {}", tables_cleared);
    println!("  Files deleted: {}", files_deleted);
    println!("  Time elapsed: {:.2}s", elapsed.as_secs_f64());

    Ok(())
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() != 4 {
        eprintln!("Usage: database_reset <db_path> <data_directory> <progress_json_path>");
        eprintln!("\nExample:");
        eprintln!("  database_reset ./data/LancacheManager.db ./data ./data/reset_progress.json");
        std::process::exit(1);
    }

    let db_path = &args[1];
    let data_directory = &args[2];
    let progress_path = Path::new(&args[3]);

    // Create data directory if it doesn't exist
    if let Err(e) = fs::create_dir_all(data_directory) {
        eprintln!("Failed to create data directory: {}", e);
        std::process::exit(1);
    }

    match reset_database(db_path, data_directory, progress_path) {
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
