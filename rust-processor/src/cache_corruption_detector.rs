use crate::cache_utils::{self, CacheSliceKind, ObservedByteRange};
use crate::log_reader::LogFileReader;
use crate::parser::LogParser;
use crate::progress_utils;
use crate::service_utils;
use anyhow::{bail, Context, Result};
use chrono::NaiveDateTime;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::Arc;

pub const CORRUPTION_CONTRACT_VERSION: u32 = 1;
const EVIDENCE_WINDOW_SECONDS: i64 = 60;

/// String interner for the streaming evidence maps. Large access logs repeat the same service,
/// URL, client, and range values millions of times; compact ids keep the per-slice map bounded.
#[derive(Default)]
struct StringInterner {
    ids: HashMap<Arc<str>, u32>,
    values: Vec<Arc<str>>,
}

impl StringInterner {
    fn intern(&mut self, value: &str) -> Result<u32> {
        if let Some(&id) = self.ids.get(value) {
            return Ok(id);
        }

        let id = u32::try_from(self.values.len())
            .context("too many unique strings while scanning corruption evidence")?;
        let value: Arc<str> = Arc::from(value);
        self.values.push(value.clone());
        self.ids.insert(value, id);
        Ok(id)
    }

    fn get(&self, id: u32) -> Result<&str> {
        self.values
            .get(id as usize)
            .map(AsRef::as_ref)
            .context("corruption evidence referenced an unknown interned string")
    }
}

#[derive(
    Debug, Default, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize,
)]
#[serde(rename_all = "snake_case")]
pub enum DetectionMode {
    #[default]
    LogsOnly,
    CacheAndLogs,
    Redownload,
}

impl DetectionMode {
    fn as_str(self) -> &'static str {
        match self {
            Self::LogsOnly => "logs_only",
            Self::CacheAndLogs => "cache_and_logs",
            Self::Redownload => "redownload",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DetectionReason {
    RepeatedMissBurst,
    SameClientHitRetryBurst,
}

impl DetectionReason {
    fn as_str(self) -> &'static str {
        match self {
            Self::RepeatedMissBurst => "repeated_miss_burst",
            Self::SameClientHitRetryBurst => "same_client_hit_retry_burst",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ValidationState {
    LogSuspect,
    ExactPathMissing,
    ExactPathPresent,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CandidateObservation {
    pub timestamp: String,
    pub client_ip: String,
    /// Exact request-target spelling from the qualifying access-log line. Missing legacy values
    /// deserialize as empty only so removal can reject them with a controlled fail-closed error.
    #[serde(default)]
    pub raw_url: String,
    pub method: String,
    pub http_status: i32,
    pub cache_status: String,
    pub raw_range: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CorruptionCandidate {
    pub candidate_id: String,
    pub mode: DetectionMode,
    pub threshold: usize,
    pub service: String,
    pub raw_url: String,
    pub normalized_uri: String,
    pub observed_range: ObservedByteRange,
    pub cache_slice: CacheSliceKind,
    pub exact_paths: Vec<String>,
    pub evidence_count: usize,
    pub first_seen: String,
    pub last_seen: String,
    pub retry_client: Option<String>,
    pub reason: DetectionReason,
    pub validation_state: ValidationState,
    pub removal_allowed: bool,
    pub observations: Vec<CandidateObservation>,
}

#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CorruptionSummary {
    pub contract_version: u32,
    pub mode: DetectionMode,
    pub threshold: usize,
    pub service_counts: BTreeMap<String, usize>,
    #[serde(rename = "total")]
    pub total_corrupted: usize,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CorruptionReport {
    pub contract_version: u32,
    pub mode: DetectionMode,
    pub threshold: usize,
    pub service_counts: BTreeMap<String, usize>,
    pub total: usize,
    pub candidates: Vec<CorruptionCandidate>,
    /// Runtime compatibility for the current CLI. It is a projection of `candidates` and is not
    /// serialized, so the wire report remains the canonical six-field contract.
    #[serde(skip, default)]
    pub summary: CorruptionSummary,
}

/// Progress data for corruption detection scan.
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

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct EvidenceKey {
    service_id: u32,
    normalized_uri_id: u32,
    cache_slice: CacheSliceKind,
    retry_client_id: Option<u32>,
}

#[derive(Debug, Clone)]
struct InternalObservation {
    timestamp: NaiveDateTime,
    client_id: u32,
    raw_url_id: u32,
    http_status: i32,
    raw_range_id: Option<u32>,
    observed_range: ObservedByteRange,
}

#[derive(Debug, Default)]
struct EvidenceAccumulator {
    window: VecDeque<InternalObservation>,
    winning: Option<Vec<InternalObservation>>,
}

impl EvidenceAccumulator {
    fn record(&mut self, observation: InternalObservation, threshold: usize) {
        if self.winning.is_some() {
            return;
        }

        // Log discovery supplies oldest-to-newest files. If a malformed rotation contains an
        // out-of-order line, ignore it for this key rather than manufacture a false time window.
        if self
            .window
            .back()
            .is_some_and(|last| observation.timestamp < last.timestamp)
        {
            return;
        }

        while self.window.front().is_some_and(|first| {
            observation
                .timestamp
                .signed_duration_since(first.timestamp)
                .num_seconds()
                > EVIDENCE_WINDOW_SECONDS
        }) {
            self.window.pop_front();
        }

        self.window.push_back(observation);
        if self.window.len() >= threshold {
            // Store exactly the selected threshold's earliest qualifying evidence. Thresholds are
            // 3/5/10, so persisted evidence is deterministic and bounded even for retry storms.
            self.winning = Some(self.window.iter().take(threshold).cloned().collect());
            self.window.clear();
        }
    }
}

pub struct CorruptionDetector {
    miss_threshold: usize,
    cache_dir: PathBuf,
    /// Legacy caller compatibility: true selects Logs Only for MISS report/summary entry points.
    skip_cache_check: bool,
}

impl CorruptionDetector {
    pub fn new<P: AsRef<Path>>(cache_dir: P, miss_threshold: usize) -> Self {
        Self {
            miss_threshold,
            cache_dir: cache_dir.as_ref().to_path_buf(),
            skip_cache_check: false,
        }
    }

    pub fn with_skip_cache_check(mut self, skip: bool) -> Self {
        self.skip_cache_check = skip;
        self
    }

    /// Canonical typed report entry point. Every mode uses the same parser, exact-slice mapper,
    /// bounded evidence window, stable candidate identity, and deterministic ordering.
    pub fn generate_report_for_mode<P: AsRef<Path>>(
        &self,
        mode: DetectionMode,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<CorruptionReport> {
        if !matches!(self.miss_threshold, 3 | 5 | 10) {
            bail!(
                "corruption threshold must be one of 3, 5, or 10 (received {})",
                self.miss_threshold
            );
        }

        let log_dir = log_dir.as_ref();
        let log_files = crate::log_discovery::discover_log_files(log_dir, log_base_name)?;
        let total_files = log_files.len();

        if log_files.is_empty() {
            if let Some(progress_file) = progress_path {
                self.write_detection_progress(
                    progress_file,
                    "completed",
                    "No log files found",
                    0,
                    0,
                    100.0,
                    None,
                )?;
            }
            return Ok(self.build_report(mode, Vec::new()));
        }

        eprintln!(
            "Scanning {} log files for {} corruption evidence...",
            total_files,
            mode.as_str()
        );
        if let Some(progress_file) = progress_path {
            self.write_detection_progress(
                progress_file,
                "scanning",
                &format!("Scanning {total_files} log files for corruption evidence..."),
                0,
                total_files,
                0.0,
                None,
            )?;
        }

        let parser = LogParser::new(timezone);
        let mut interner = StringInterner::default();
        let mut trackers: HashMap<EvidenceKey, EvidenceAccumulator> = HashMap::new();
        let mut eligible_entries = 0usize;

        for (file_index, log_file) in log_files.iter().enumerate() {
            let file_name = log_file
                .path
                .file_name()
                .map(|name| name.to_string_lossy().to_string())
                .unwrap_or_else(|| "unknown".to_string());
            eprintln!(
                "Processing ({}/{}): {}",
                file_index + 1,
                total_files,
                log_file.path.display()
            );

            let file_size = std::fs::metadata(&log_file.path)
                .map(|metadata| metadata.len())
                .unwrap_or(0);
            if let Some(progress_file) = progress_path {
                let percent = (file_index as f64 / total_files as f64) * 90.0;
                self.write_detection_progress(
                    progress_file,
                    "scanning",
                    &format!("Scanning file {}/{}...", file_index + 1, total_files),
                    file_index,
                    total_files,
                    percent,
                    Some(file_name.clone()),
                )?;
            }

            let mut reader = match LogFileReader::open(&log_file.path) {
                Ok(reader) => reader,
                Err(error) => {
                    eprintln!(
                        "WARNING: Skipping unreadable log file {}: {}",
                        log_file.path.display(),
                        error
                    );
                    continue;
                }
            };

            let mut line = String::new();
            let mut bytes_read_total = 0u64;
            let mut last_file_percent = 0.0f64;
            loop {
                line.clear();
                let bytes_read = match reader.read_line(&mut line) {
                    Ok(bytes_read) => bytes_read,
                    Err(error) => {
                        eprintln!(
                            "WARNING: Stopping at unreadable data in {}: {}",
                            log_file.path.display(),
                            error
                        );
                        break;
                    }
                };
                if bytes_read == 0 {
                    break;
                }
                bytes_read_total += bytes_read as u64;

                if file_size > 0 {
                    let file_percent = bytes_read_total as f64 / file_size as f64 * 100.0;
                    if file_percent - last_file_percent >= 5.0 {
                        last_file_percent = file_percent;
                        if let Some(progress_file) = progress_path {
                            let overall = ((file_index as f64 + file_percent / 100.0)
                                / total_files as f64)
                                * 90.0;
                            let _ = self.write_detection_progress(
                                progress_file,
                                "scanning",
                                &format!("Scanning file {}/{}...", file_index + 1, total_files),
                                file_index,
                                total_files,
                                overall,
                                Some(file_name.clone()),
                            );
                        }
                    }
                }

                let Some(entry) = parser.parse_line(line.trim()) else {
                    continue;
                };
                if service_utils::should_skip_url(&entry.url)
                    || entry.method != "GET"
                    || !matches!(entry.status_code, 200 | 206)
                {
                    continue;
                }

                let required_cache_status = match mode {
                    DetectionMode::LogsOnly | DetectionMode::CacheAndLogs => "MISS",
                    DetectionMode::Redownload => "HIT",
                };
                if entry.cache_status != required_cache_status {
                    continue;
                }

                let Some(mapping) = cache_utils::physical_slices_for_request(
                    &self.cache_dir,
                    &entry.service,
                    &entry.raw_url,
                    &entry.http_range,
                ) else {
                    continue;
                };
                if mode == DetectionMode::Redownload
                    && mapping.observed_range == (ObservedByteRange::Inclusive { start: 0, end: 0 })
                {
                    continue;
                }

                let service = cache_utils::service_name_lowercase(&entry.service);
                let service_id = interner.intern(&service)?;
                let normalized_uri_id = interner.intern(&mapping.normalized_uri)?;
                let client_id = interner.intern(&entry.client_ip)?;
                let raw_url_id = interner.intern(&entry.raw_url)?;
                let raw_range_id = if entry.http_range.is_empty() {
                    None
                } else {
                    Some(interner.intern(&entry.http_range)?)
                };
                let retry_client_id = (mode == DetectionMode::Redownload).then_some(client_id);
                let observation = InternalObservation {
                    timestamp: entry.timestamp,
                    client_id,
                    raw_url_id,
                    http_status: entry.status_code,
                    raw_range_id,
                    observed_range: mapping.observed_range,
                };

                // A no-range log line is one logical observation with two possible cache-key
                // locations. Ranged requests are counted once for every physical slice they cover.
                let slice_kinds: Vec<CacheSliceKind> = match observation.observed_range {
                    ObservedByteRange::NoRange => vec![CacheSliceKind::NoRange],
                    ObservedByteRange::Inclusive { .. } => {
                        mapping.slices.into_iter().map(|slice| slice.kind).collect()
                    }
                };
                for cache_slice in slice_kinds {
                    let key = EvidenceKey {
                        service_id,
                        normalized_uri_id,
                        cache_slice,
                        retry_client_id,
                    };
                    trackers
                        .entry(key)
                        .or_default()
                        .record(observation.clone(), self.miss_threshold);
                }

                eligible_entries += 1;
                if eligible_entries % 500_000 == 0 {
                    eprintln!(
                        "  Processed {} eligible entries across {} physical evidence keys",
                        eligible_entries,
                        trackers.len()
                    );
                }
            }
        }

        if let Some(progress_file) = progress_path {
            let _ = self.write_detection_progress(
                progress_file,
                "analyzing",
                "Resolving exact corruption candidates...",
                total_files,
                total_files,
                95.0,
                None,
            );
        }

        let mut candidates = Vec::new();
        for (key, accumulator) in trackers {
            let Some(winning) = accumulator.winning else {
                continue;
            };
            if let Some(candidate) = self.build_candidate(mode, &interner, key, winning)? {
                candidates.push(candidate);
            }
        }
        candidates.sort_by(|left, right| {
            (
                &left.service,
                &left.normalized_uri,
                &left.cache_slice,
                &left.retry_client,
                &left.candidate_id,
            )
                .cmp(&(
                    &right.service,
                    &right.normalized_uri,
                    &right.cache_slice,
                    &right.retry_client,
                    &right.candidate_id,
                ))
        });

        let report = self.build_report(mode, candidates);
        if let Some(progress_file) = progress_path {
            self.write_detection_progress(
                progress_file,
                "completed",
                &format!("Scan complete. Found {} candidate slices.", report.total),
                total_files,
                total_files,
                100.0,
                None,
            )?;
        }
        Ok(report)
    }

    fn build_candidate(
        &self,
        mode: DetectionMode,
        interner: &StringInterner,
        key: EvidenceKey,
        winning: Vec<InternalObservation>,
    ) -> Result<Option<CorruptionCandidate>> {
        let service = interner.get(key.service_id)?.to_string();
        let normalized_uri = interner.get(key.normalized_uri_id)?.to_string();
        let first_internal = winning
            .first()
            .context("qualified corruption evidence had no observations")?;
        let raw_url = interner.get(first_internal.raw_url_id)?.to_string();
        let observed_range = first_internal.observed_range.clone();
        let retry_client = key
            .retry_client_id
            .map(|id| interner.get(id).map(str::to_string))
            .transpose()?;

        let mapped = self.paths_for_slice(&service, &normalized_uri, &key.cache_slice);
        let mut present = Vec::new();
        for (kind, path) in &mapped {
            if !path.exists() {
                continue;
            }
            match cache_utils::safe_path_under_root(&self.cache_dir, path) {
                // Keep the deterministic mapped path on the wire. `canonicalize` on Windows
                // adds a `\\?\` prefix; the guard's successful result is the validation, not a
                // replacement for the nginx-derived identity that removal will revalidate.
                Ok(_) => present.push((kind.clone(), path.clone())),
                Err(error) => eprintln!(
                    "WARNING: Ignoring unsafe corruption candidate path {}: {}",
                    path.display(),
                    error
                ),
            }
        }

        if mode == DetectionMode::CacheAndLogs && present.is_empty() {
            return Ok(None);
        }

        let (cache_slice, exact_paths, validation_state, removal_allowed) = match mode {
            DetectionMode::LogsOnly => (
                key.cache_slice,
                mapped
                    .into_iter()
                    .map(|(_, path)| path.display().to_string())
                    .collect(),
                ValidationState::LogSuspect,
                false,
            ),
            DetectionMode::CacheAndLogs | DetectionMode::Redownload if !present.is_empty() => {
                let resolved_kind = if present.len() == 1 {
                    present[0].0.clone()
                } else {
                    key.cache_slice
                };
                (
                    resolved_kind,
                    present
                        .into_iter()
                        .map(|(_, path)| path.display().to_string())
                        .collect(),
                    ValidationState::ExactPathPresent,
                    true,
                )
            }
            DetectionMode::Redownload => (
                key.cache_slice,
                mapped
                    .into_iter()
                    .map(|(_, path)| path.display().to_string())
                    .collect(),
                ValidationState::ExactPathMissing,
                false,
            ),
            DetectionMode::CacheAndLogs => unreachable!("missing cache candidates returned above"),
        };

        let reason = match mode {
            DetectionMode::LogsOnly | DetectionMode::CacheAndLogs => {
                DetectionReason::RepeatedMissBurst
            }
            DetectionMode::Redownload => DetectionReason::SameClientHitRetryBurst,
        };
        let cache_status = match mode {
            DetectionMode::LogsOnly | DetectionMode::CacheAndLogs => "MISS",
            DetectionMode::Redownload => "HIT",
        };
        let observations: Vec<CandidateObservation> = winning
            .iter()
            .map(|observation| {
                Ok(CandidateObservation {
                    timestamp: format_timestamp(observation.timestamp),
                    client_ip: interner.get(observation.client_id)?.to_string(),
                    raw_url: interner.get(observation.raw_url_id)?.to_string(),
                    method: "GET".to_string(),
                    http_status: observation.http_status,
                    cache_status: cache_status.to_string(),
                    raw_range: observation
                        .raw_range_id
                        .map(|id| interner.get(id).map(str::to_string))
                        .transpose()?,
                })
            })
            .collect::<Result<_>>()?;
        let first_seen = observations
            .first()
            .map(|observation| observation.timestamp.clone())
            .context("qualified corruption evidence had no first observation")?;
        let last_seen = observations
            .last()
            .map(|observation| observation.timestamp.clone())
            .context("qualified corruption evidence had no last observation")?;
        let candidate_id = stable_candidate_id(
            mode,
            self.miss_threshold,
            &service,
            &normalized_uri,
            &cache_slice,
            retry_client.as_deref(),
            reason,
            &observations,
        );

        Ok(Some(CorruptionCandidate {
            candidate_id,
            mode,
            threshold: self.miss_threshold,
            service,
            raw_url,
            normalized_uri,
            observed_range,
            cache_slice,
            exact_paths,
            evidence_count: observations.len(),
            first_seen,
            last_seen,
            retry_client,
            reason,
            validation_state,
            removal_allowed,
            observations,
        }))
    }

    fn paths_for_slice(
        &self,
        service: &str,
        normalized_uri: &str,
        cache_slice: &CacheSliceKind,
    ) -> Vec<(CacheSliceKind, PathBuf)> {
        match cache_slice {
            CacheSliceKind::NoRange => vec![
                (
                    CacheSliceKind::NoRange,
                    cache_utils::calculate_cache_path_no_range(
                        &self.cache_dir,
                        service,
                        normalized_uri,
                    ),
                ),
                (
                    CacheSliceKind::Noslice,
                    cache_utils::calculate_cache_path_noslice(
                        &self.cache_dir,
                        service,
                        normalized_uri,
                    ),
                ),
            ],
            CacheSliceKind::Noslice => vec![(
                CacheSliceKind::Noslice,
                cache_utils::calculate_cache_path_noslice(&self.cache_dir, service, normalized_uri),
            )],
            CacheSliceKind::Ranged { start, end } => vec![(
                cache_slice.clone(),
                cache_utils::calculate_cache_path(
                    &self.cache_dir,
                    service,
                    normalized_uri,
                    *start,
                    *end,
                ),
            )],
        }
    }

    fn build_report(
        &self,
        mode: DetectionMode,
        candidates: Vec<CorruptionCandidate>,
    ) -> CorruptionReport {
        let mut service_counts = BTreeMap::new();
        for candidate in &candidates {
            *service_counts.entry(candidate.service.clone()).or_insert(0) += 1;
        }
        let total = candidates.len();
        let summary = CorruptionSummary {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            mode,
            threshold: self.miss_threshold,
            service_counts: service_counts.clone(),
            total_corrupted: total,
        };
        CorruptionReport {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            mode,
            threshold: self.miss_threshold,
            service_counts,
            total,
            candidates,
            summary,
        }
    }

    /// Legacy MISS report entry point retained until the exact-removal worker migrates the CLI.
    pub fn generate_report<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<CorruptionReport> {
        let mode = if self.skip_cache_check {
            DetectionMode::LogsOnly
        } else {
            DetectionMode::CacheAndLogs
        };
        self.generate_report_for_mode(mode, log_dir, log_base_name, timezone, progress_path)
    }

    pub fn generate_summary_with_progress<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<CorruptionSummary> {
        Ok(self
            .generate_report(log_dir, log_base_name, timezone, progress_path)?
            .summary)
    }

    pub fn generate_redownload_report<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<CorruptionReport> {
        self.generate_report_for_mode(
            DetectionMode::Redownload,
            log_dir,
            log_base_name,
            timezone,
            progress_path,
        )
    }

    pub fn generate_redownload_summary_with_progress<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<CorruptionSummary> {
        Ok(self
            .generate_redownload_report(log_dir, log_base_name, timezone, progress_path)?
            .summary)
    }

    /// Compatibility projection for the legacy removal path. New detail/removal code must consume
    /// `CorruptionReport.candidates` instead of this URL-level map.
    #[allow(dead_code)]
    pub fn detect_corrupted_chunks_with_progress<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<HashMap<(String, String), usize>> {
        let report = self.generate_report(log_dir, log_base_name, timezone, progress_path)?;
        let mut projected = HashMap::new();
        for candidate in report.candidates {
            projected
                .entry((candidate.service, candidate.raw_url))
                .and_modify(|count: &mut usize| *count = (*count).max(candidate.evidence_count))
                .or_insert(candidate.evidence_count);
        }
        Ok(projected)
    }

    /// Compatibility projection for the legacy removal path. Response-size reconstruction is no
    /// longer evidence, so the deprecated size value is conservatively zero.
    pub fn detect_redownloaded_chunks_with_progress<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<HashMap<(String, String), (usize, i64)>> {
        let report =
            self.generate_redownload_report(log_dir, log_base_name, timezone, progress_path)?;
        let mut projected = HashMap::new();
        for candidate in report.candidates {
            projected
                .entry((candidate.service, candidate.raw_url))
                .and_modify(|value: &mut (usize, i64)| {
                    value.0 = value.0.max(candidate.evidence_count)
                })
                .or_insert((candidate.evidence_count, 0));
        }
        Ok(projected)
    }

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

fn format_timestamp(timestamp: NaiveDateTime) -> String {
    timestamp.format("%Y-%m-%dT%H:%M:%SZ").to_string()
}

fn stable_candidate_id(
    mode: DetectionMode,
    threshold: usize,
    service: &str,
    normalized_uri: &str,
    cache_slice: &CacheSliceKind,
    retry_client: Option<&str>,
    reason: DetectionReason,
    observations: &[CandidateObservation],
) -> String {
    fn append_field(buffer: &mut String, value: &str) {
        use std::fmt::Write;
        let _ = write!(buffer, "{}:{}|", value.len(), value);
    }

    let mut identity = String::new();
    append_field(&mut identity, mode.as_str());
    append_field(&mut identity, &threshold.to_string());
    append_field(&mut identity, service);
    append_field(&mut identity, normalized_uri);
    match cache_slice {
        CacheSliceKind::NoRange => append_field(&mut identity, "no_range"),
        CacheSliceKind::Noslice => append_field(&mut identity, "noslice"),
        CacheSliceKind::Ranged { start, end } => {
            append_field(&mut identity, &format!("ranged:{start}-{end}"))
        }
    }
    append_field(&mut identity, retry_client.unwrap_or(""));
    append_field(&mut identity, reason.as_str());
    for observation in observations {
        append_field(&mut identity, &observation.timestamp);
        append_field(&mut identity, &observation.client_ip);
        append_field(&mut identity, &observation.raw_url);
        append_field(&mut identity, &observation.http_status.to_string());
        append_field(&mut identity, &observation.cache_status);
        append_field(
            &mut identity,
            observation.raw_range.as_deref().unwrap_or(""),
        );
    }
    cache_utils::calculate_md5(&identity)
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::{Duration, NaiveDateTime};
    use std::fs;

    fn log_line(
        second: i64,
        client: &str,
        method: &str,
        status: i32,
        cache_status: &str,
        url: &str,
        range: Option<&str>,
    ) -> String {
        let base = NaiveDateTime::parse_from_str("2024-01-01 00:00:00", "%Y-%m-%d %H:%M:%S")
            .expect("valid base time");
        let timestamp = (base + Duration::seconds(second)).format("%d/%b/%Y:%H:%M:%S");
        format!(
            "[steam] {client} / - - - [{timestamp} +0000] \"{method} {url} HTTP/1.1\" {status} 1024 \"-\" \"Test\" \"{cache_status}\" \"cdn.test\" \"{}\"",
            range.unwrap_or("-")
        )
    }

    fn write_log(log_dir: &Path, lines: &[String]) {
        fs::create_dir_all(log_dir).expect("create log dir");
        fs::write(log_dir.join("access.log"), lines.join("\n") + "\n").expect("write log");
    }

    fn report(
        cache_dir: &Path,
        log_dir: &Path,
        threshold: usize,
        mode: DetectionMode,
    ) -> CorruptionReport {
        fs::create_dir_all(cache_dir).expect("create cache dir");
        CorruptionDetector::new(cache_dir, threshold)
            .generate_report_for_mode(mode, log_dir, "access.log", chrono_tz::UTC, None)
            .expect("generate report")
    }

    #[test]
    fn distinct_physical_ranges_do_not_aggregate_at_supported_thresholds() {
        for threshold in [3usize, 5, 10] {
            let fixture = tempfile::tempdir().expect("fixture");
            let log_dir = fixture.path().join("logs");
            let cache_dir = fixture.path().join("cache");
            let lines: Vec<String> = (0..threshold)
                .map(|index| {
                    let start = index as u64 * cache_utils::DEFAULT_SLICE_SIZE;
                    let end = start + 100;
                    log_line(
                        index as i64,
                        "10.0.0.1",
                        "GET",
                        206,
                        "MISS",
                        "/same-url.bin",
                        Some(&format!("bytes={start}-{end}")),
                    )
                })
                .collect();
            write_log(&log_dir, &lines);

            let result = report(&cache_dir, &log_dir, threshold, DetectionMode::LogsOnly);
            assert!(result.candidates.is_empty(), "threshold {threshold}");
        }
    }

    #[test]
    fn same_slice_qualifies_at_each_threshold_and_retains_bounded_evidence() {
        for threshold in [3usize, 5, 10] {
            let fixture = tempfile::tempdir().expect("fixture");
            let log_dir = fixture.path().join("logs");
            let cache_dir = fixture.path().join("cache");
            let lines: Vec<String> = (0..threshold)
                .map(|index| {
                    log_line(
                        index as i64,
                        "10.0.0.1",
                        "GET",
                        206,
                        "MISS",
                        "/same-slice.bin",
                        Some("bytes=1048600-1048700"),
                    )
                })
                .collect();
            write_log(&log_dir, &lines);

            let result = report(&cache_dir, &log_dir, threshold, DetectionMode::LogsOnly);
            assert_eq!(result.total, 1, "threshold {threshold}");
            assert_eq!(result.candidates[0].evidence_count, threshold);
            assert_eq!(result.candidates[0].observations.len(), threshold);
            assert!(!result.candidates[0].removal_allowed);
        }
    }

    #[test]
    fn normalization_equivalent_spellings_retain_each_observation_raw_url() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let first = "/content//file.bin?token=one";
        let second = "/content/file.bin?token=two";
        let lines = vec![
            log_line(
                0,
                "10.0.0.1",
                "GET",
                206,
                "MISS",
                first,
                Some("bytes=1048600-1048700"),
            ),
            log_line(
                1,
                "10.0.0.1",
                "GET",
                206,
                "MISS",
                second,
                Some("bytes=1048600-1048700"),
            ),
            log_line(
                2,
                "10.0.0.1",
                "GET",
                206,
                "MISS",
                first,
                Some("bytes=1048600-1048700"),
            ),
        ];
        write_log(&log_dir, &lines);

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::LogsOnly);
        assert_eq!(result.total, 1);
        let candidate = &result.candidates[0];
        assert_eq!(candidate.raw_url, first);
        assert_eq!(candidate.normalized_uri, "/content/file.bin");
        assert_eq!(
            candidate
                .observations
                .iter()
                .map(|observation| observation.raw_url.as_str())
                .collect::<Vec<_>>(),
            vec![first, second, first]
        );
        let serialized = serde_json::to_value(candidate).expect("serialize candidate");
        assert_eq!(serialized["observations"][1]["raw_url"], second);
    }

    #[test]
    fn sixty_second_boundary_is_inclusive_but_sixty_one_is_not() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let mut lines = Vec::new();
        for second in [0, 30, 60] {
            lines.push(log_line(
                second,
                "10.0.0.1",
                "GET",
                206,
                "MISS",
                "/inclusive.bin",
                Some("bytes=0-10"),
            ));
        }
        for second in [0, 30, 61] {
            lines.push(log_line(
                second + 120,
                "10.0.0.1",
                "GET",
                206,
                "MISS",
                "/outside.bin",
                Some("bytes=0-10"),
            ));
        }
        write_log(&log_dir, &lines);

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::LogsOnly);
        assert_eq!(result.total, 1);
        assert_eq!(result.candidates[0].raw_url, "/inclusive.bin");
    }

    #[test]
    fn ineligible_method_http_and_cache_statuses_never_count() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let lines = vec![
            log_line(0, "a", "GET", 206, "MISS", "/x", Some("bytes=0-10")),
            log_line(1, "a", "GET", 206, "MISS", "/x", Some("bytes=0-10")),
            log_line(2, "a", "GET", 206, "BYPASS", "/x", Some("bytes=0-10")),
            log_line(3, "a", "GET", 206, "UNKNOWN", "/x", Some("bytes=0-10")),
            log_line(4, "a", "HEAD", 206, "MISS", "/x", Some("bytes=0-10")),
            log_line(5, "a", "GET", 404, "MISS", "/x", Some("bytes=0-10")),
            log_line(6, "a", "GET", 504, "MISS", "/x", Some("bytes=0-10")),
        ];
        write_log(&log_dir, &lines);

        assert!(report(&cache_dir, &log_dir, 3, DetectionMode::LogsOnly)
            .candidates
            .is_empty());
    }

    #[test]
    fn redownload_requires_same_client_and_slice_and_excludes_zero_probe() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let mut lines = Vec::new();
        for second in 0..3 {
            lines.push(log_line(
                second,
                "10.0.0.1",
                "GET",
                206,
                "HIT",
                "/retry.bin",
                Some("bytes=2097200-2097300"),
            ));
            lines.push(log_line(
                second + 10,
                &format!("10.0.1.{second}"),
                "GET",
                206,
                "HIT",
                "/different-clients.bin",
                Some("bytes=0-10"),
            ));
            lines.push(log_line(
                second + 20,
                "10.0.0.2",
                "GET",
                206,
                "HIT",
                "/different-slices.bin",
                Some(&format!(
                    "bytes={}-{}",
                    second * 1_048_576,
                    second * 1_048_576 + 10
                )),
            ));
            lines.push(log_line(
                second + 30,
                "10.0.0.3",
                "GET",
                206,
                "HIT",
                "/probe.bin",
                Some("bytes=0-0"),
            ));
        }
        write_log(&log_dir, &lines);
        let exact_path = cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/retry.bin",
            2_097_152,
            3_145_727,
        );
        fs::create_dir_all(exact_path.parent().expect("path parent")).expect("mkdir");
        fs::write(&exact_path, b"slice").expect("cache slice");

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::Redownload);
        assert_eq!(result.total, 1);
        let candidate = &result.candidates[0];
        assert_eq!(candidate.raw_url, "/retry.bin");
        assert_eq!(candidate.retry_client.as_deref(), Some("10.0.0.1"));
        assert_eq!(
            candidate.validation_state,
            ValidationState::ExactPathPresent
        );
        assert!(candidate.removal_allowed);
    }

    #[test]
    fn redownload_excludes_ineligible_observations_and_keeps_missing_path_review_only() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let lines = vec![
            // Two eligible observations are deliberately below threshold.
            log_line(0, "a", "GET", 206, "HIT", "/invalid", Some("bytes=10-20")),
            log_line(1, "a", "GET", 206, "HIT", "/invalid", Some("bytes=10-20")),
            log_line(2, "a", "HEAD", 206, "HIT", "/invalid", Some("bytes=10-20")),
            log_line(3, "a", "GET", 404, "HIT", "/invalid", Some("bytes=10-20")),
            log_line(4, "a", "POST", 200, "HIT", "/invalid", Some("bytes=10-20")),
            log_line(5, "a", "GET", 206, "MISS", "/invalid", Some("bytes=10-20")),
            log_line(
                6,
                "a",
                "GET",
                206,
                "BYPASS",
                "/invalid",
                Some("bytes=10-20"),
            ),
            // A real same-client retry burst remains visible even after the exact path is gone,
            // but cannot be removed.
            log_line(10, "b", "GET", 206, "HIT", "/missing", Some("bytes=10-20")),
            log_line(11, "b", "GET", 206, "HIT", "/missing", Some("bytes=10-20")),
            log_line(12, "b", "GET", 206, "HIT", "/missing", Some("bytes=10-20")),
        ];
        write_log(&log_dir, &lines);

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::Redownload);
        assert_eq!(result.total, 1);
        assert_eq!(result.candidates[0].raw_url, "/missing");
        assert_eq!(
            result.candidates[0].validation_state,
            ValidationState::ExactPathMissing
        );
        assert!(!result.candidates[0].removal_allowed);
    }

    #[test]
    fn cache_and_logs_accepts_only_the_exact_mid_object_path() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let lines: Vec<String> = (0..3)
            .map(|second| {
                log_line(
                    second,
                    "10.0.0.1",
                    "GET",
                    206,
                    "MISS",
                    "/middle.bin",
                    Some("bytes=2097200-2097300"),
                )
            })
            .collect();
        write_log(&log_dir, &lines);
        fs::create_dir_all(&cache_dir).expect("cache root");

        let slice_zero =
            cache_utils::calculate_cache_path(&cache_dir, "steam", "/middle.bin", 0, 1_048_575);
        fs::create_dir_all(slice_zero.parent().expect("path parent")).expect("mkdir");
        fs::write(&slice_zero, b"unrelated").expect("slice zero");
        assert!(report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs)
            .candidates
            .is_empty());

        let exact = cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/middle.bin",
            2_097_152,
            3_145_727,
        );
        fs::create_dir_all(exact.parent().expect("path parent")).expect("mkdir");
        fs::write(&exact, b"exact").expect("exact slice");
        let result = report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs);
        assert_eq!(result.total, 1);
        assert_eq!(
            result.candidates[0].exact_paths,
            vec![exact.display().to_string()]
        );
        assert!(result.candidates[0].removal_allowed);
    }

    #[test]
    fn cache_and_logs_resolves_noslice_for_no_range_evidence() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let lines: Vec<String> = (0..3)
            .map(|second| log_line(second, "10.0.0.1", "GET", 200, "MISS", "/whole", None))
            .collect();
        write_log(&log_dir, &lines);
        let noslice = cache_utils::calculate_cache_path_noslice(&cache_dir, "steam", "/whole");
        fs::create_dir_all(noslice.parent().expect("path parent")).expect("mkdir");
        fs::write(&noslice, b"whole").expect("noslice");

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs);
        assert_eq!(result.total, 1);
        assert_eq!(result.candidates[0].cache_slice, CacheSliceKind::Noslice);
        assert_eq!(
            result.candidates[0].exact_paths,
            vec![noslice.display().to_string()]
        );
    }

    #[test]
    fn report_ids_order_and_wire_shape_are_deterministic() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let mut lines = Vec::new();
        for url in ["/z.bin", "/a.bin"] {
            for second in 0..3 {
                lines.push(log_line(
                    second,
                    "10.0.0.1",
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=1048600-1048700"),
                ));
            }
        }
        write_log(&log_dir, &lines);

        let first = report(&cache_dir, &log_dir, 3, DetectionMode::LogsOnly);
        let second = report(&cache_dir, &log_dir, 3, DetectionMode::LogsOnly);
        assert_eq!(first, second);
        assert_eq!(first.candidates[0].raw_url, "/a.bin");
        assert_eq!(first.candidates[1].raw_url, "/z.bin");

        let value = serde_json::to_value(&first).expect("serialize report");
        let object = value.as_object().expect("report object");
        assert_eq!(object.len(), 6);
        for field in [
            "contract_version",
            "mode",
            "threshold",
            "service_counts",
            "total",
            "candidates",
        ] {
            assert!(object.contains_key(field), "missing {field}");
        }
        assert_eq!(value["mode"], "logs_only");
        assert_eq!(value["candidates"][0]["validation_state"], "log_suspect");
        assert_eq!(
            value["candidates"][0]["observed_range"]["kind"],
            "inclusive"
        );
        assert_eq!(value["candidates"][0]["cache_slice"]["kind"], "ranged");
    }
}
