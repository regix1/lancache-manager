use crate::log_reader::LogFileReader;
use crate::parser::LogParser;
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

    /// Calculate MD5 hash for a given cache key
    fn calculate_md5(cache_key: &str) -> String {
        format!("{:x}", md5::compute(cache_key.as_bytes()))
    }

    /// Calculate cache file path using lancache's MD5 structure:
    /// /cache/{last_2_chars}/{2_chars_before_that}/{full_hash}
    fn calculate_cache_path(&self, service: &str, url: &str, start: u64, end: u64) -> String {
        // Cache key format: "{service}{url}bytes={start}-{end}"
        let cache_key = format!("{}{}bytes={}-{}", service, url, start, end);
        let hash = Self::calculate_md5(&cache_key);

        // Extract characters for path structure
        let len = hash.len();
        if len < 4 {
            // Should never happen with MD5, but handle gracefully
            return self.cache_dir.join(&hash).display().to_string();
        }

        let last_2 = &hash[len - 2..];
        let middle_2 = &hash[len - 4..len - 2];

        self.cache_dir
            .join(last_2)
            .join(middle_2)
            .join(&hash)
            .display()
            .to_string()
    }

    /// Detect corrupted chunks by analyzing log files
    /// Returns a map of (service, url) -> miss_count for chunks with 3+ misses
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

        // Filter to only chunks with miss_threshold or more MISS/UNKNOWN requests
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

    /// Generate quick summary (just counts per service)
    pub fn generate_summary<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
    ) -> Result<CorruptionSummary> {
        let corrupted_map = self.detect_corrupted_chunks(log_dir, log_base_name, timezone)?;

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
}
