use anyhow::{Context, Result};
use clap::Parser;
use serde::{Deserialize, Serialize};
use sqlx::Row;
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

mod cache_utils;
mod db;
mod progress_utils;

/// Eviction scanner - checks which downloads have been evicted from the nginx cache
#[derive(Parser, Debug)]
#[command(name = "cache_eviction_scan")]
#[command(about = "Scans cache directories and marks evicted downloads in the database")]
struct Args {
    /// Path to JSON file containing datasource configuration
    datasource_config: String,

    /// Path to progress JSON file (use "none" to skip)
    #[arg(default_value = "none")]
    progress_json: Option<String>,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
struct DatasourceConfig {
    name: String,
    cache_path: String,
    is_default: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ProgressData {
    status: String,
    message: String,
    percent_complete: f64,
    processed: usize,
    total_estimate: usize,
    evicted: usize,
    un_evicted: usize,
    timestamp: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ScanResult {
    success: bool,
    processed: usize,
    evicted: usize,
    un_evicted: usize,
    files_on_disk: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

/// Represents a download's log entry data needed for cache path computation
struct DownloadEntry {
    service: String,
    url: String,
    datasource: Option<String>,
}

const SLICE_SIZE: u64 = 1_048_576; // 1 MiB - standard nginx slice size

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();

    let progress_path = match args.progress_json.as_deref() {
        Some("none") | None => None,
        Some(p) => Some(PathBuf::from(p)),
    };

    match run_scan(&args.datasource_config, progress_path.as_deref()).await {
        Ok(result) => {
            let json = serde_json::to_string(&result)?;
            println!("{}", json);
            if result.success {
                Ok(())
            } else {
                std::process::exit(1);
            }
        }
        Err(e) => {
            let result = ScanResult {
                success: false,
                processed: 0,
                evicted: 0,
                un_evicted: 0,
                files_on_disk: 0,
                error: Some(format!("{:#}", e)),
            };
            let json = serde_json::to_string(&result)?;
            println!("{}", json);
            std::process::exit(1);
        }
    }
}

async fn run_scan(datasource_config_path: &str, progress_path: Option<&Path>) -> Result<ScanResult> {
    // Step 1: Read datasource configuration
    let config_content = std::fs::read_to_string(datasource_config_path)
        .with_context(|| format!("Failed to read datasource config: {}", datasource_config_path))?;
    let datasources: Vec<DatasourceConfig> = serde_json::from_str(&config_content)
        .with_context(|| "Failed to parse datasource config JSON")?;

    if datasources.is_empty() {
        return Ok(ScanResult {
            success: true,
            processed: 0,
            evicted: 0,
            un_evicted: 0,
            files_on_disk: 0,
            error: None,
        });
    }

    // Build datasource name -> cache path lookup, and find default
    let mut datasource_map: HashMap<String, PathBuf> = HashMap::new();
    let mut default_cache_path: Option<PathBuf> = None;
    for ds in &datasources {
        let path = PathBuf::from(&ds.cache_path);
        datasource_map.insert(ds.name.clone(), path.clone());
        if ds.is_default {
            default_cache_path = Some(path);
        }
    }
    // If no explicit default, use the first datasource
    let default_cache_path = default_cache_path
        .unwrap_or_else(|| PathBuf::from(&datasources[0].cache_path));

    write_progress(progress_path, "running", "Scanning cache directories...", 0.0, 0, 0, 0, 0)?;

    // Step 2: Build HashSet of all files on disk across all cache directories
    let mut files_on_disk: HashSet<PathBuf> = HashSet::new();
    for ds in &datasources {
        let cache_dir = Path::new(&ds.cache_path);
        if !cache_dir.exists() {
            eprintln!("[EvictionScan] Cache directory does not exist for datasource '{}': {}", ds.name, ds.cache_path);
            continue;
        }
        for entry in jwalk::WalkDir::new(cache_dir)
            .min_depth(1)
            .into_iter()
            .filter_map(|e| e.ok())
        {
            if entry.file_type().is_file() {
                files_on_disk.insert(entry.path());
            }
        }
    }

    if files_on_disk.is_empty() {
        eprintln!("[EvictionScan] No cache files found on disk - skipping to prevent false eviction flags");
        return Ok(ScanResult {
            success: true,
            processed: 0,
            evicted: 0,
            un_evicted: 0,
            files_on_disk: 0,
            error: None,
        });
    }

    let total_files = files_on_disk.len();
    eprintln!("[EvictionScan] Found {} cache files on disk across {} datasource(s)", total_files, datasources.len());

    // Step 3: Connect to database
    let pool = db::create_pool().await;

    // Count total inactive downloads for progress estimation
    let total_estimate: i64 = sqlx::query_scalar(
        r#"SELECT COUNT(*) FROM "Downloads" WHERE "IsActive" = false"#
    )
    .fetch_one(&pool)
    .await
    .unwrap_or(0);

    let total_estimate = total_estimate as usize;

    // Step 4: Process downloads in batches
    let batch_size: i64 = 500;
    let mut total_evicted: usize = 0;
    let mut total_un_evicted: usize = 0;
    let mut total_processed: usize = 0;
    let mut last_processed_id: i64 = 0;

    loop {
        // Step A: Fetch a batch of distinct inactive download IDs
        let download_rows = sqlx::query(
            r#"
            SELECT "Id" as download_id, "IsEvicted" as is_evicted
            FROM "Downloads"
            WHERE "IsActive" = false AND "Id" > $1
            ORDER BY "Id"
            LIMIT $2
            "#
        )
        .bind(last_processed_id)
        .bind(batch_size)
        .fetch_all(&pool)
        .await
        .with_context(|| "Failed to fetch downloads")?;

        if download_rows.is_empty() {
            break;
        }

        let download_ids: Vec<i64> = download_rows.iter().map(|r| r.get("download_id")).collect();
        let download_evicted: HashMap<i64, bool> = download_rows.iter()
            .map(|r| (r.get("download_id"), r.get("is_evicted")))
            .collect();

        last_processed_id = *download_ids.last().unwrap();
        let batch_count = download_ids.len();

        // Step B: Fetch log entries for these downloads
        // Build parameterized IN clause
        let placeholders: Vec<String> = download_ids.iter()
            .enumerate()
            .map(|(i, _)| format!("${}", i + 1))
            .collect();
        let log_query = format!(
            r#"
            SELECT "DownloadId" as download_id, "Service" as service, "Url" as url, "Datasource" as datasource
            FROM "LogEntries"
            WHERE "DownloadId" IN ({})
            AND "Service" IS NOT NULL AND "Url" IS NOT NULL
            "#,
            placeholders.join(", ")
        );
        let mut query = sqlx::query(&log_query);
        for id in &download_ids {
            query = query.bind(id);
        }
        let log_rows = query.fetch_all(&pool).await
            .with_context(|| "Failed to fetch log entries")?;

        // Group log entries by download_id
        let mut download_entries: HashMap<i64, Vec<DownloadEntry>> = HashMap::new();
        for row in &log_rows {
            let download_id: i64 = row.get("download_id");
            let service: String = row.get("service");
            let url: String = row.get("url");
            let datasource: Option<String> = row.get("datasource");

            download_entries
                .entry(download_id)
                .or_insert_with(Vec::new)
                .push(DownloadEntry {
                    service,
                    url,
                    datasource,
                });
        }

        // Check each download's cache files against disk
        let mut ids_to_evict: Vec<i64> = Vec::new();
        let mut ids_to_unevict: Vec<i64> = Vec::new();

        // Downloads without any log entries can't be verified — mark them as evicted
        for download_id in &download_ids {
            let is_evicted = download_evicted.get(download_id).copied().unwrap_or(false);
            if !download_entries.contains_key(download_id) && !is_evicted {
                ids_to_evict.push(*download_id);
            }
        }

        for (download_id, entries) in &download_entries {
            let is_evicted = download_evicted.get(download_id).copied().unwrap_or(false);
            let has_cache_file = entries.iter().any(|entry| {
                // Resolve cache directory for this entry's datasource
                let cache_dir = entry.datasource.as_ref()
                    .and_then(|ds_name| datasource_map.get(ds_name))
                    .unwrap_or(&default_cache_path);

                // Check without byte range (slice disabled)
                let path_no_range = cache_utils::calculate_cache_path_no_range(
                    cache_dir, &entry.service, &entry.url
                );
                if files_on_disk.contains(&path_no_range) {
                    return true;
                }

                // Check with first 1MB slice (slice enabled)
                let path_first_slice = cache_utils::calculate_cache_path(
                    cache_dir, &entry.service, &entry.url, 0, SLICE_SIZE - 1
                );
                files_on_disk.contains(&path_first_slice)
            });

            if !has_cache_file && !is_evicted {
                ids_to_evict.push(*download_id);
            } else if has_cache_file && is_evicted {
                ids_to_unevict.push(*download_id);
            }
        }

        // Batch UPDATE evicted downloads
        if !ids_to_evict.is_empty() {
            for chunk in ids_to_evict.chunks(400) {
                let placeholders: Vec<String> = chunk.iter()
                    .enumerate()
                    .map(|(i, _)| format!("${}", i + 1))
                    .collect();
                let query_str = format!(
                    r#"UPDATE "Downloads" SET "IsEvicted" = true WHERE "Id" IN ({})"#,
                    placeholders.join(", ")
                );
                let mut query = sqlx::query(&query_str);
                for id in chunk {
                    query = query.bind(id);
                }
                query.execute(&pool).await
                    .with_context(|| "Failed to update evicted downloads")?;
            }
            total_evicted += ids_to_evict.len();
        }

        // Batch UPDATE un-evicted downloads
        if !ids_to_unevict.is_empty() {
            for chunk in ids_to_unevict.chunks(400) {
                let placeholders: Vec<String> = chunk.iter()
                    .enumerate()
                    .map(|(i, _)| format!("${}", i + 1))
                    .collect();
                let query_str = format!(
                    r#"UPDATE "Downloads" SET "IsEvicted" = false WHERE "Id" IN ({})"#,
                    placeholders.join(", ")
                );
                let mut query = sqlx::query(&query_str);
                for id in chunk {
                    query = query.bind(id);
                }
                query.execute(&pool).await
                    .with_context(|| "Failed to update un-evicted downloads")?;
            }
            total_un_evicted += ids_to_unevict.len();
        }

        total_processed += batch_count;

        // Write progress
        let percent = if total_estimate > 0 {
            (total_processed as f64 / total_estimate as f64 * 100.0).min(100.0)
        } else {
            0.0
        };

        write_progress(
            progress_path,
            "running",
            &format!("Processed {} of ~{} downloads...", total_processed, total_estimate),
            percent,
            total_processed,
            total_estimate,
            total_evicted,
            total_un_evicted,
        )?;

        // If we got fewer downloads than batch_size, we've reached the end
        if (batch_count as i64) < batch_size {
            break;
        }
    }

    eprintln!(
        "[EvictionScan] Scan complete: processed {} downloads, {} newly evicted, {} un-evicted (re-cached)",
        total_processed, total_evicted, total_un_evicted
    );

    write_progress(
        progress_path,
        "completed",
        &format!("Scan complete: {} processed, {} evicted, {} un-evicted", total_processed, total_evicted, total_un_evicted),
        100.0,
        total_processed,
        total_estimate,
        total_evicted,
        total_un_evicted,
    )?;

    Ok(ScanResult {
        success: true,
        processed: total_processed,
        evicted: total_evicted,
        un_evicted: total_un_evicted,
        files_on_disk: total_files,
        error: None,
    })
}

fn write_progress(
    progress_path: Option<&Path>,
    status: &str,
    message: &str,
    percent_complete: f64,
    processed: usize,
    total_estimate: usize,
    evicted: usize,
    un_evicted: usize,
) -> Result<()> {
    let Some(path) = progress_path else {
        return Ok(());
    };

    let progress = ProgressData {
        status: status.to_string(),
        message: message.to_string(),
        percent_complete,
        processed,
        total_estimate,
        evicted,
        un_evicted,
        timestamp: progress_utils::current_timestamp(),
    };

    progress_utils::write_progress_json(path, &progress)
}
