use crate::cache_utils;
use crate::log_reader::LogFileReader;
use crate::parser::LogParser;
use crate::progress_utils;
use crate::service_utils;
use anyhow::Result;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CorruptedChunk {
    pub service: String,
    pub url: String,
    pub miss_count: usize,
    pub cache_file_path: String,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct CorruptionSummary {
    pub service_counts: HashMap<String, usize>,
    pub total_corrupted: usize,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct CorruptionReport {
    pub corrupted_chunks: Vec<CorruptedChunk>,
    pub summary: CorruptionSummary,
}

/// Progress data for corruption detection scan
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CorruptionDetectionProgress {
    pub status: String,
    pub message: String,
    pub files_processed: usize,
    pub total_files: usize,
    pub percent_complete: f64,
    pub current_file: Option<String>,
    pub timestamp: String,
}

pub struct CorruptionDetector {
    /// Minimum number of MISS/UNKNOWN requests to consider a chunk corrupted
    miss_threshold: usize,
    /// Cache directory root path
    cache_dir: PathBuf,
}

impl CorruptionDetector {
    pub fn new<P: AsRef<Path>>(cache_dir: P, miss_threshold: usize) -> Self {
        Self {
            miss_threshold,
            cache_dir: cache_dir.as_ref().to_path_buf(),
        }
    }

    /// Calculate cache file path using lancache's MD5 structure:
    /// /cache/{last_2_chars}/{2_chars_before_that}/{full_hash}
    fn calculate_cache_path(&self, service: &str, url: &str, start: u64, end: u64) -> String {
        cache_utils::calculate_cache_path(&self.cache_dir, service, url, start, end)
            .display()
            .to_string()
    }

    /// Detect corrupted chunks by analyzing log files
    /// Returns a map of (service, url) -> miss_count for chunks with 3+ misses
    /// Memory-optimized: Periodically filters out entries below threshold to prevent unbounded growth
    pub fn detect_corrupted_chunks<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
    ) -> Result<HashMap<(String, String), usize>> {
        let log_dir = log_dir.as_ref();

        // Discover all log files
        let log_files = crate::log_discovery::discover_log_files(log_dir, log_base_name)?;

        if log_files.is_empty() {
            return Ok(HashMap::new());
        }

        eprintln!("Scanning {} log file(s) for corrupted chunks...", log_files.len());

        let parser = LogParser::new(timezone);
        let mut miss_tracker: HashMap<(String, String), usize> = HashMap::new();
        let mut entries_processed = 0usize;

        // Process each log file
        for log_file in &log_files {
            eprintln!("Processing: {}", log_file.path.display());

            // Try to process the file, but skip if corrupted
            let file_result = (|| -> Result<()> {
                let mut reader = LogFileReader::open(&log_file.path)?;
                let mut line = String::new();

                loop {
                    line.clear();
                    let bytes_read = reader.read_line(&mut line)?;
                    if bytes_read == 0 {
                        break; // EOF
                    }

                    // Parse log entry
                    if let Some(entry) = parser.parse_line(line.trim()) {
                        // Skip health check/heartbeat endpoints
                        if service_utils::should_skip_url(&entry.url) {
                            continue;
                        }

                        // Only track MISS and UNKNOWN status
                        if entry.cache_status == "MISS" || entry.cache_status == "UNKNOWN" {
                            let key = (entry.service.clone(), entry.url.clone());
                            *miss_tracker.entry(key).or_insert(0) += 1;
                            entries_processed += 1;

                            // MEMORY OPTIMIZATION: Periodically clean up entries that won't reach threshold
                            // This prevents unbounded HashMap growth with millions of single-miss URLs
                            if entries_processed % 100_000 == 0 {
                                let before_size = miss_tracker.len();
                                miss_tracker.retain(|_, count| *count >= self.miss_threshold - 1);
                                // Actually release memory back to the system
                                miss_tracker.shrink_to_fit();
                                let after_size = miss_tracker.len();
                                if before_size > after_size {
                                    eprintln!("  Memory cleanup: Removed {} low-count entries (kept {})",
                                        before_size - after_size, after_size);
                                }
                            }
                        }
                        // Skip HIT entries - they're working fine
                    }
                }
                Ok(())
            })();

            // If this file failed (e.g., corrupted gzip), log warning and skip it
            if let Err(e) = file_result {
                eprintln!("WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                eprintln!("  Continuing with remaining files...");
                continue;
            }
        }

        // Final filter to only chunks with miss_threshold or more MISS/UNKNOWN requests
        let corrupted: HashMap<(String, String), usize> = miss_tracker
            .into_iter()
            .filter(|(_, count)| *count >= self.miss_threshold)
            .collect();

        eprintln!("Found {} corrupted chunks ({}+ MISS/UNKNOWN)", corrupted.len(), self.miss_threshold);

        Ok(corrupted)
    }

    /// Generate full corruption report with cache file paths
    pub fn generate_report<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
    ) -> Result<CorruptionReport> {
        let corrupted_map = self.detect_corrupted_chunks(log_dir, log_base_name, timezone)?;

        let mut corrupted_chunks = Vec::new();
        let mut service_counts: HashMap<String, usize> = HashMap::new();

        for ((service, url), miss_count) in corrupted_map {
            // Calculate cache paths for 1MB slices
            // Note: We don't know exact byte ranges from logs alone, so we calculate for common patterns
            // For a more accurate implementation, you'd track actual byte ranges from logs
            // For now, we'll generate path for the first 1MB slice as an example
            let cache_file_path = self.calculate_cache_path(&service, &url, 0, 1_048_575);

            corrupted_chunks.push(CorruptedChunk {
                service: service.clone(),
                url,
                miss_count,
                cache_file_path,
            });

            *service_counts.entry(service).or_insert(0) += 1;
        }

        let total_corrupted = corrupted_chunks.len();

        Ok(CorruptionReport {
            corrupted_chunks,
            summary: CorruptionSummary {
                service_counts,
                total_corrupted,
            },
        })
    }

    /// Detect corrupted chunks with progress reporting
    /// Returns a map of (service, url) -> miss_count for chunks with 3+ misses
    pub fn detect_corrupted_chunks_with_progress<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<HashMap<(String, String), usize>> {
        let log_dir = log_dir.as_ref();

        // Discover all log files
        let log_files = crate::log_discovery::discover_log_files(log_dir, log_base_name)?;

        if log_files.is_empty() {
            if let Some(progress_file) = progress_path {
                self.write_detection_progress(progress_file, "complete", "No log files found", 0, 0, 100.0, None)?;
            }
            return Ok(HashMap::new());
        }

        let total_files = log_files.len();
        eprintln!("Scanning {} log file(s) for corrupted chunks...", total_files);

        // Write initial progress
        if let Some(progress_file) = progress_path {
            self.write_detection_progress(
                progress_file,
                "scanning",
                &format!("Scanning {} log files for corrupted chunks...", total_files),
                0,
                total_files,
                0.0,
                None,
            )?;
        }

        let parser = LogParser::new(timezone);
        let mut miss_tracker: HashMap<(String, String), usize> = HashMap::new();
        let mut entries_processed = 0usize;

        // Process each log file
        for (file_index, log_file) in log_files.iter().enumerate() {
            let file_name = log_file.path.file_name()
                .map(|n| n.to_string_lossy().to_string())
                .unwrap_or_else(|| "unknown".to_string());

            eprintln!("Processing ({}/{}): {}", file_index + 1, total_files, log_file.path.display());

            // Get file size for progress calculation within the file
            let file_size = std::fs::metadata(&log_file.path)
                .map(|m| m.len())
                .unwrap_or(0);

            // Update progress for current file
            if let Some(progress_file) = progress_path {
                let percent = (file_index as f64 / total_files as f64) * 100.0;
                self.write_detection_progress(
                    progress_file,
                    "scanning",
                    &format!("Scanning file {}/{}: {}", file_index + 1, total_files, file_name),
                    file_index,
                    total_files,
                    percent,
                    Some(file_name.clone()),
                )?;
            }

            // Process the file with progress tracking
            let mut reader = match LogFileReader::open(&log_file.path) {
                Ok(r) => r,
                Err(e) => {
                    eprintln!("WARNING: Skipping corrupted file {}: {}", log_file.path.display(), e);
                    eprintln!("  Continuing with remaining files...");
                    continue;
                }
            };

            let mut line = String::new();
            let mut bytes_read_total = 0u64;
            let mut last_progress_percent = 0.0f64;
            let progress_update_threshold = 5.0; // Update every 5% within the file

            loop {
                line.clear();
                let bytes_read = match reader.read_line(&mut line) {
                    Ok(b) => b,
                    Err(e) => {
                        eprintln!("WARNING: Error reading file {}: {}", log_file.path.display(), e);
                        break;
                    }
                };

                if bytes_read == 0 {
                    break; // EOF
                }

                bytes_read_total += bytes_read as u64;

                // Update progress within the file (every ~5%)
                if file_size > 0 {
                    let file_progress = (bytes_read_total as f64 / file_size as f64) * 100.0;
                    if file_progress - last_progress_percent >= progress_update_threshold {
                        last_progress_percent = file_progress;

                        if let Some(progress_file) = progress_path {
                            // Calculate overall progress: file_index/total_files + (file_progress/100) * (1/total_files)
                            let overall_percent = ((file_index as f64 + file_progress / 100.0) / total_files as f64) * 100.0;
                            let _ = self.write_detection_progress(
                                progress_file,
                                "scanning",
                                &format!("Scanning {}: {:.0}%", file_name, file_progress),
                                file_index,
                                total_files,
                                overall_percent,
                                Some(file_name.clone()),
                            );
                        }
                    }
                }

                // Parse log entry
                if let Some(entry) = parser.parse_line(line.trim()) {
                    // Skip health check/heartbeat endpoints
                    if service_utils::should_skip_url(&entry.url) {
                        continue;
                    }

                    // Only track MISS and UNKNOWN status
                    if entry.cache_status == "MISS" || entry.cache_status == "UNKNOWN" {
                        let key = (entry.service.clone(), entry.url.clone());
                        *miss_tracker.entry(key).or_insert(0) += 1;
                        entries_processed += 1;

                        // MEMORY OPTIMIZATION: Periodically clean up entries that won't reach threshold
                        if entries_processed % 100_000 == 0 {
                            let before_size = miss_tracker.len();
                            miss_tracker.retain(|_, count| *count >= self.miss_threshold - 1);
                            miss_tracker.shrink_to_fit();
                            let after_size = miss_tracker.len();
                            if before_size > after_size {
                                eprintln!("  Memory cleanup: Removed {} low-count entries (kept {})",
                                    before_size - after_size, after_size);
                            }
                        }
                    }
                }
            }
        }

        // Final filter to only chunks with miss_threshold or more MISS/UNKNOWN requests
        let corrupted: HashMap<(String, String), usize> = miss_tracker
            .into_iter()
            .filter(|(_, count)| *count >= self.miss_threshold)
            .collect();

        eprintln!("Found {} corrupted chunks ({}+ MISS/UNKNOWN)", corrupted.len(), self.miss_threshold);

        // Write completion progress
        if let Some(progress_file) = progress_path {
            self.write_detection_progress(
                progress_file,
                "complete",
                &format!("Scan complete. Found {} corrupted chunks.", corrupted.len()),
                total_files,
                total_files,
                100.0,
                None,
            )?;
        }

        Ok(corrupted)
    }

    /// Generate quick summary with progress reporting
    pub fn generate_summary_with_progress<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<CorruptionSummary> {
        let corrupted_map = self.detect_corrupted_chunks_with_progress(
            log_dir, log_base_name, timezone, progress_path
        )?;

        let mut service_counts: HashMap<String, usize> = HashMap::new();

        for ((service, _url), _count) in corrupted_map.iter() {
            *service_counts.entry(service.clone()).or_insert(0) += 1;
        }

        let total_corrupted = corrupted_map.len();

        Ok(CorruptionSummary {
            service_counts,
            total_corrupted,
        })
    }

    /// Helper to write detection progress to file
    fn write_detection_progress(
        &self,
        progress_path: &Path,
        status: &str,
        message: &str,
        files_processed: usize,
        total_files: usize,
        percent_complete: f64,
        current_file: Option<String>,
    ) -> Result<()> {
        let progress = CorruptionDetectionProgress {
            status: status.to_string(),
            message: message.to_string(),
            files_processed,
            total_files,
            percent_complete,
            current_file,
            timestamp: progress_utils::current_timestamp(),
        };
        progress_utils::write_progress_json(progress_path, &progress)
    }
}
