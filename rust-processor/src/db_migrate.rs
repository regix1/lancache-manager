use anyhow::{Context, Result};
use chrono::{NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use rusqlite::{params, Connection, OpenFlags};
use serde::Serialize;
use std::collections::HashSet;
use std::env;
use std::fs;
use std::path::Path;
use std::time::Instant;

mod progress_utils;

#[derive(Serialize, Clone)]
struct ProgressData {
    is_processing: bool,
    percent_complete: f64,
    status: String,
    message: String,
    records_processed: u64,
    records_imported: u64,
    records_skipped: u64,
    records_errors: u64,
    backup_path: Option<String>,
    timestamp: String,
}

impl ProgressData {
    fn new(
        is_processing: bool,
        percent_complete: f64,
        status: String,
        message: String,
        records_processed: u64,
        records_imported: u64,
        records_skipped: u64,
        records_errors: u64,
        backup_path: Option<String>,
    ) -> Self {
        Self {
            is_processing,
            percent_complete,
            status,
            message,
            records_processed,
            records_imported,
            records_skipped,
            records_errors,
            backup_path,
            timestamp: progress_utils::current_timestamp(),
        }
    }
}

fn write_progress(progress_path: &Path, progress: &ProgressData) -> Result<()> {
    progress_utils::write_progress_json(progress_path, progress)
}

/// Convert UTC NaiveDateTime to local timezone NaiveDateTime
fn utc_to_local(utc_dt: NaiveDateTime, local_tz: Tz) -> NaiveDateTime {
    let utc_datetime = Utc.from_utc_datetime(&utc_dt);
    let local_datetime = utc_datetime.with_timezone(&local_tz);
    NaiveDateTime::new(local_datetime.date_naive(), local_datetime.time())
}

/// Create a timestamped backup of the target database
fn create_database_backup(db_path: &str) -> Result<String> {
    let db_path = Path::new(db_path);

    // Check if database exists
    if !db_path.exists() {
        eprintln!("Target database does not exist yet, skipping backup");
        return Ok(String::from("(no backup - new database)"));
    }

    // Generate backup filename with timestamp
    let timestamp = chrono::Utc::now().format("%Y%m%d_%H%M%S");
    let backup_filename = format!(
        "{}.backup.{}{}",
        db_path.file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("database"),
        timestamp,
        db_path.extension()
            .and_then(|s| s.to_str())
            .map(|ext| format!(".{}", ext))
            .unwrap_or_default()
    );

    let backup_path = db_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join(&backup_filename);

    eprintln!("Creating backup: {} -> {}", db_path.display(), backup_path.display());

    // Copy the database file
    fs::copy(db_path, &backup_path)
        .context("Failed to create database backup")?;

    let backup_path_str = backup_path
        .to_str()
        .ok_or_else(|| anyhow::anyhow!("Invalid backup path"))?
        .to_string();

    eprintln!("Backup created successfully: {}", backup_path_str);
    Ok(backup_path_str)
}

/// Import data from DeveLanCacheUI database to LancacheManager database
fn import_develancache_data(
    source_db_path: &str,
    target_db_path: &str,
    progress_path: &Path,
    overwrite_existing: bool,
    batch_size: usize,
) -> Result<()> {
    let start_time = Instant::now();
    eprintln!("Starting import from DeveLanCacheUI database...");

    // Get timezone from environment variable (same as log processor)
    let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
    let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);
    eprintln!("Using timezone: {} (from TZ env var)", local_tz);

    // Write initial progress
    write_progress(
        progress_path,
        &ProgressData::new(
            true,
            0.0,
            "starting".to_string(),
            "Creating database backup...".to_string(),
            0,
            0,
            0,
            0,
            None,
        ),
    )?;

    // Create backup of target database before importing
    let backup_path = match create_database_backup(target_db_path) {
        Ok(path) => {
            eprintln!("Backup created: {}", path);
            Some(path)
        }
        Err(e) => {
            eprintln!("Warning: Failed to create backup: {}", e);
            eprintln!("Continuing with import...");
            None
        }
    };

    // Write progress with backup path
    write_progress(
        progress_path,
        &ProgressData::new(
            true,
            0.5,
            "starting".to_string(),
            "Connecting to databases...".to_string(),
            0,
            0,
            0,
            0,
            backup_path.clone(),
        ),
    )?;

    // Open source database (DeveLanCacheUI)
    let source_conn = Connection::open_with_flags(
        source_db_path,
        OpenFlags::SQLITE_OPEN_READ_ONLY,
    )
    .context("Failed to open source database")?;

    // Verify DownloadEvents table exists
    let table_exists: bool = source_conn
        .query_row(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='DownloadEvents'",
            [],
            |row| row.get::<_, i32>(0),
        )
        .context("Failed to check for DownloadEvents table")?
        > 0;

    if !table_exists {
        return Err(anyhow::anyhow!(
            "DownloadEvents table not found in source database"
        ));
    }

    // Get total record count
    let total_records: u64 = source_conn
        .query_row("SELECT COUNT(*) FROM DownloadEvents", [], |row| {
            row.get(0)
        })
        .context("Failed to get record count")?;

    eprintln!("Found {} records in DeveLanCacheUI database", total_records);

    write_progress(
        progress_path,
        &ProgressData::new(
            true,
            1.0,
            "processing".to_string(),
            format!("Found {} records to import", total_records),
            0,
            0,
            0,
            0,
            backup_path.clone(),
        ),
    )?;

    // Open target database (LancacheManager)
    let mut target_conn = Connection::open(target_db_path)
        .context("Failed to open target database")?;

    // Track statistics
    let mut records_processed = 0u64;
    let mut records_imported = 0u64;
    let mut records_skipped = 0u64;
    let mut records_errors = 0u64;

    // Read all download events from source
    let mut stmt = source_conn
        .prepare(
            "SELECT
                Id,
                CacheIdentifier,
                DownloadIdentifier,
                DownloadIdentifierString,
                ClientIp,
                CreatedAt,
                LastUpdatedAt,
                CacheHitBytes,
                CacheMissBytes
            FROM DownloadEvents
            ORDER BY CreatedAt",
        )
        .context("Failed to prepare SELECT statement")?;

    let mut rows = stmt.query([]).context("Failed to query DownloadEvents")?;

    // Collect records in batches
    let mut batch = Vec::new();

    while let Some(row) = rows.next().context("Failed to read row")? {
        let service: String = row.get(1)?; // CacheIdentifier
        let download_identifier: Option<u32> = row.get(2)?; // DownloadIdentifier (nullable uint)
        let client_ip: String = row.get(4)?; // ClientIp
        let created_at_str: String = row.get(5)?; // CreatedAt (UTC timestamp as string)
        let last_updated_at_str: String = row.get(6)?; // LastUpdatedAt (UTC timestamp as string)
        let cache_hit_bytes: i64 = row.get(7)?; // CacheHitBytes
        let cache_miss_bytes: i64 = row.get(8)?; // CacheMissBytes

        // Parse timestamps as UTC
        let created_at_utc = NaiveDateTime::parse_from_str(&created_at_str, "%Y-%m-%d %H:%M:%S%.f")
            .context("Failed to parse CreatedAt timestamp")?;
        let last_updated_at_utc =
            NaiveDateTime::parse_from_str(&last_updated_at_str, "%Y-%m-%d %H:%M:%S%.f")
                .context("Failed to parse LastUpdatedAt timestamp")?;

        // Convert UTC to local timezone
        let created_at_local = utc_to_local(created_at_utc, local_tz);
        let last_updated_at_local = utc_to_local(last_updated_at_utc, local_tz);

        // Determine DepotId or GameAppId based on service
        let (depot_id, game_app_id) = if let Some(id) = download_identifier {
            if service.eq_ignore_ascii_case("steam") {
                (Some(id), None)
            } else {
                (None, Some(id))
            }
        } else {
            (None, None)
        };

        batch.push((
            service,
            client_ip,
            created_at_utc.format("%Y-%m-%d %H:%M:%S").to_string(),
            last_updated_at_utc.format("%Y-%m-%d %H:%M:%S").to_string(),
            created_at_local.format("%Y-%m-%d %H:%M:%S").to_string(),
            last_updated_at_local.format("%Y-%m-%d %H:%M:%S").to_string(),
            cache_hit_bytes,
            cache_miss_bytes,
            depot_id,
            game_app_id,
        ));

        records_processed += 1;

        // Process batch when it reaches the specified size
        if batch.len() >= batch_size {
            let (imported, skipped, errors) = process_batch(
                &mut target_conn,
                &batch,
                overwrite_existing,
            )?;

            records_imported += imported;
            records_skipped += skipped;
            records_errors += errors;
            batch.clear();

            // Update progress
            let percent = (records_processed as f64 / total_records as f64) * 100.0;
            write_progress(
                progress_path,
                &ProgressData::new(
                    true,
                    percent,
                    "processing".to_string(),
                    format!(
                        "Processed {}/{} records ({} imported, {} skipped, {} errors)",
                        records_processed, total_records, records_imported, records_skipped, records_errors
                    ),
                    records_processed,
                    records_imported,
                    records_skipped,
                    records_errors,
                    backup_path.clone(),
                ),
            )?;
        }
    }

    // Process remaining records
    if !batch.is_empty() {
        let (imported, skipped, errors) = process_batch(
            &mut target_conn,
            &batch,
            overwrite_existing,
        )?;

        records_imported += imported;
        records_skipped += skipped;
        records_errors += errors;
    }

    let elapsed = start_time.elapsed();
    eprintln!(
        "Import completed in {:.2}s: {} imported, {} skipped, {} errors",
        elapsed.as_secs_f64(),
        records_imported,
        records_skipped,
        records_errors
    );

    // Write final progress
    write_progress(
        progress_path,
        &ProgressData::new(
            false,
            100.0,
            "completed".to_string(),
            format!(
                "Import completed: {} imported, {} skipped, {} errors",
                records_imported, records_skipped, records_errors
            ),
            records_processed,
            records_imported,
            records_skipped,
            records_errors,
            backup_path,
        ),
    )?;

    Ok(())
}

type BatchRecord = (
    String, // service
    String, // client_ip
    String, // start_time_utc
    String, // end_time_utc
    String, // start_time_local
    String, // end_time_local
    i64,    // cache_hit_bytes
    i64,    // cache_miss_bytes
    Option<u32>, // depot_id
    Option<u32>, // game_app_id
);

fn process_batch(
    conn: &mut Connection,
    batch: &[BatchRecord],
    overwrite_existing: bool,
) -> Result<(u64, u64, u64)> {
    let mut imported = 0u64;
    let mut skipped = 0u64;
    let mut errors = 0u64;

    // Start transaction
    let tx = conn.transaction()?;

    // Build set of existing records to avoid duplicates
    let mut existing_keys = HashSet::new();

    for record in batch {
        let (_, client_ip, start_time_utc, _, _, _, _, _, _, _) = record;

        // Check if record already exists
        let exists: bool = tx
            .query_row(
                "SELECT COUNT(*) FROM Downloads WHERE ClientIp = ? AND StartTimeUtc = ?",
                params![client_ip, start_time_utc],
                |row| row.get::<_, i32>(0),
            )
            .unwrap_or(0)
            > 0;

        if exists {
            existing_keys.insert(format!("{}_{}", client_ip, start_time_utc));
        }
    }

    // Process each record in the batch
    for record in batch {
        let (
            service,
            client_ip,
            start_time_utc,
            end_time_utc,
            start_time_local,
            end_time_local,
            cache_hit_bytes,
            cache_miss_bytes,
            depot_id,
            game_app_id,
        ) = record;

        let key = format!("{}_{}", client_ip, start_time_utc);

        if existing_keys.contains(&key) {
            if overwrite_existing {
                // Update existing record
                match tx.execute(
                    "UPDATE Downloads
                     SET Service = ?, EndTimeUtc = ?, EndTimeLocal = ?,
                         StartTimeLocal = ?,
                         CacheHitBytes = ?, CacheMissBytes = ?,
                         DepotId = ?, GameAppId = ?
                     WHERE ClientIp = ? AND StartTimeUtc = ?",
                    params![
                        service,
                        end_time_utc,
                        end_time_local,
                        start_time_local,
                        cache_hit_bytes,
                        cache_miss_bytes,
                        depot_id,
                        game_app_id,
                        client_ip,
                        start_time_utc,
                    ],
                ) {
                    Ok(_) => imported += 1,
                    Err(e) => {
                        eprintln!("Error updating record: {}", e);
                        errors += 1;
                    }
                }
            } else {
                skipped += 1;
            }
        } else {
            // Insert new record (include Datasource column with "default" value)
            match tx.execute(
                "INSERT INTO Downloads
                 (Service, ClientIp, StartTimeUtc, EndTimeUtc, StartTimeLocal, EndTimeLocal,
                  CacheHitBytes, CacheMissBytes, IsActive, DepotId, GameAppId, Datasource)
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, 'default')",
                params![
                    service,
                    client_ip,
                    start_time_utc,
                    end_time_utc,
                    start_time_local,
                    end_time_local,
                    cache_hit_bytes,
                    cache_miss_bytes,
                    depot_id,
                    game_app_id,
                ],
            ) {
                Ok(_) => imported += 1,
                Err(e) => {
                    eprintln!("Error inserting record: {}", e);
                    errors += 1;
                }
            }
        }
    }

    // Commit transaction
    tx.commit()?;

    Ok((imported, skipped, errors))
}

fn print_usage() {
    eprintln!("Usage: data_migrator <source_db_path> <target_db_path> <progress_path> [overwrite] [batch_size]");
    eprintln!();
    eprintln!("Arguments:");
    eprintln!("  source_db_path  - Path to DeveLanCacheUI_Backend database (source)");
    eprintln!("  target_db_path  - Path to LancacheManager database (target)");
    eprintln!("  progress_path   - Path to write progress JSON file");
    eprintln!("  overwrite       - (optional) 1 to overwrite existing records, 0 to skip (default: 0)");
    eprintln!("  batch_size      - (optional) Number of records per batch (default: 1000)");
    eprintln!();
    eprintln!("Example:");
    eprintln!("  data_migrator source.db target.db progress.json 0 1000");
    eprintln!();
    eprintln!("Environment Variables:");
    eprintln!("  TZ              - Timezone for local timestamp conversion (default: UTC)");
    eprintln!("                    Examples: UTC, America/Chicago, Europe/London");
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 4 {
        print_usage();
        std::process::exit(1);
    }

    let source_db_path = &args[1];
    let target_db_path = &args[2];
    let progress_path = Path::new(&args[3]);
    let overwrite_existing = args.get(4)
        .and_then(|s| s.parse::<u8>().ok())
        .unwrap_or(0) == 1;
    let batch_size = args.get(5)
        .and_then(|s| s.parse::<usize>().ok())
        .unwrap_or(1000);

    eprintln!("Source database: {}", source_db_path);
    eprintln!("Target database: {}", target_db_path);
    eprintln!("Progress file: {}", progress_path.display());
    eprintln!("Overwrite existing: {}", overwrite_existing);
    eprintln!("Batch size: {}", batch_size);

    import_develancache_data(
        source_db_path,
        target_db_path,
        progress_path,
        overwrite_existing,
        batch_size,
    )
}
