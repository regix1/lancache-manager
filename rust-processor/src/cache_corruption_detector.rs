use crate::cache_utils::{self, CacheSliceKind, ObservedByteRange};
use crate::log_reader::LogFileReader;
use crate::parser::LogParser;
use crate::progress_utils;
use crate::service_utils;
use anyhow::{bail, Context, Result};
use chrono::{DateTime, Duration, NaiveDateTime, SecondsFormat, Utc};
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;
use std::collections::{BTreeMap, HashMap, VecDeque};
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;

pub const CORRUPTION_CONTRACT_VERSION: u32 = 2;
pub const DEFAULT_LOOKBACK_DAYS: u32 = 30;
pub const MIN_LOOKBACK_DAYS: u32 = 1;
pub const MAX_LOOKBACK_DAYS: u32 = 365;
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
    MissingCachedSlice,
}

impl DetectionReason {
    fn as_str(self) -> &'static str {
        match self {
            Self::RepeatedMissBurst => "repeated_miss_burst",
            Self::SameClientHitRetryBurst => "same_client_hit_retry_burst",
            Self::MissingCachedSlice => "missing_cached_slice",
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
    pub bytes_served: i64,
    pub cache_status: String,
    pub raw_range: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SupportingSiblingEvidence {
    pub cache_slice: CacheSliceKind,
    pub exact_path: String,
}

fn deserialize_required_option<'de, D, T>(
    deserializer: D,
) -> std::result::Result<Option<T>, D::Error>
where
    D: serde::Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::<T>::deserialize(deserializer)
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
    #[serde(deserialize_with = "deserialize_required_option")]
    pub supporting_sibling: Option<SupportingSiblingEvidence>,
    pub observations: Vec<CandidateObservation>,
}

#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CorruptionSummary {
    pub contract_version: u32,
    pub mode: DetectionMode,
    pub threshold: usize,
    pub lookback_days: u32,
    pub scan_started_utc: String,
    pub service_counts: BTreeMap<String, usize>,
    pub removable_service_counts: BTreeMap<String, usize>,
    pub review_only_service_counts: BTreeMap<String, usize>,
    #[serde(rename = "total")]
    pub total_corrupted: usize,
    pub removable_total: usize,
    pub review_only_total: usize,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CorruptionReport {
    pub contract_version: u32,
    pub mode: DetectionMode,
    pub threshold: usize,
    pub lookback_days: u32,
    pub scan_started_utc: String,
    pub service_counts: BTreeMap<String, usize>,
    pub removable_service_counts: BTreeMap<String, usize>,
    pub review_only_service_counts: BTreeMap<String, usize>,
    pub total: usize,
    pub removable_total: usize,
    pub review_only_total: usize,
    pub candidates: Vec<CorruptionCandidate>,
    /// Runtime compatibility for the current CLI. It is a projection of `candidates` and is not
    /// serialized, so the wire report remains the canonical v2 contract.
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
    bytes_served: i64,
    raw_range_id: Option<u32>,
    observed_range: ObservedByteRange,
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct MissingProofKey {
    service_id: u32,
    normalized_uri_id: u32,
    cache_slice: CacheSliceKind,
}

#[derive(Debug, Default)]
struct LatestProofAccumulator {
    latest: Option<InternalObservation>,
}

impl LatestProofAccumulator {
    fn record(&mut self, observation: InternalObservation) {
        if self
            .latest
            .as_ref()
            .is_none_or(|latest| observation.timestamp >= latest.timestamp)
        {
            self.latest = Some(observation);
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum MappedPathState {
    PresentSafeRegular,
    MissingSafe,
    Unverifiable,
}

#[derive(Debug)]
struct ResolvedMissingProof {
    cache_slice: CacheSliceKind,
    observation: InternalObservation,
    exact_path: PathBuf,
    initial_path_state: MappedPathState,
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
    lookback_days: u32,
    scan_started_utc: DateTime<Utc>,
    /// Legacy caller compatibility: true selects Logs Only for MISS report/summary entry points.
    skip_cache_check: bool,
}

impl CorruptionDetector {
    pub fn new<P: AsRef<Path>>(
        cache_dir: P,
        miss_threshold: usize,
        lookback_days: u32,
        scan_started_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            miss_threshold,
            cache_dir: cache_dir.as_ref().to_path_buf(),
            lookback_days,
            scan_started_utc,
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
        let cache_dir = self.cache_dir.clone();
        let canonical_root = canonical_cache_root(&cache_dir);
        self.generate_report_for_mode_with_path_inspector(
            mode,
            log_dir,
            log_base_name,
            timezone,
            progress_path,
            move |path| inspect_mapped_path(&cache_dir, canonical_root.as_deref(), path),
        )
    }

    fn generate_report_for_mode_with_path_inspector<P, F>(
        &self,
        mode: DetectionMode,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
        mut inspect_path: F,
    ) -> Result<CorruptionReport>
    where
        P: AsRef<Path>,
        F: FnMut(&Path) -> MappedPathState,
    {
        if !matches!(self.miss_threshold, 3 | 5 | 10) {
            bail!(
                "corruption threshold must be one of 3, 5, or 10 (received {})",
                self.miss_threshold
            );
        }
        if !(MIN_LOOKBACK_DAYS..=MAX_LOOKBACK_DAYS).contains(&self.lookback_days) {
            bail!(
                "corruption lookback days must be between {} and {} (received {})",
                MIN_LOOKBACK_DAYS,
                MAX_LOOKBACK_DAYS,
                self.lookback_days
            );
        }
        let scan_started_naive = self.scan_started_utc.naive_utc();
        let cutoff = scan_started_naive
            .checked_sub_signed(Duration::days(i64::from(self.lookback_days)))
            .context("corruption lookback cutoff is outside the supported timestamp range")?;

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
        let mut missing_proofs: HashMap<MissingProofKey, LatestProofAccumulator> = HashMap::new();
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
                let inside_evidence_window =
                    entry.timestamp >= cutoff && entry.timestamp <= scan_started_naive;
                let qualifies_for_burst = entry.cache_status == required_cache_status
                    && (mode != DetectionMode::Redownload || inside_evidence_window);
                let may_qualify_for_missing = mode == DetectionMode::CacheAndLogs
                    && inside_evidence_window
                    && entry.status_code == 206
                    && entry.cache_status == "HIT";
                if !qualifies_for_burst && !may_qualify_for_missing {
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
                let qualifies_for_burst = qualifies_for_burst
                    && !(mode == DetectionMode::Redownload
                        && mapping.observed_range
                            == (ObservedByteRange::Inclusive { start: 0, end: 0 }));
                let missing_slice = if may_qualify_for_missing && mapping.slices.len() == 1 {
                    match &mapping.observed_range {
                        ObservedByteRange::Inclusive { start, end } => end
                            .checked_sub(*start)
                            .and_then(|length_minus_one| length_minus_one.checked_add(1))
                            .and_then(|length| i64::try_from(length).ok())
                            .filter(|length| *length > 0 && *length == entry.bytes_served)
                            .and_then(|_| match &mapping.slices[0].kind {
                                ranged @ CacheSliceKind::Ranged { .. } => Some(ranged.clone()),
                                CacheSliceKind::NoRange | CacheSliceKind::Noslice => None,
                            }),
                        ObservedByteRange::NoRange => None,
                    }
                } else {
                    None
                };
                if !qualifies_for_burst && missing_slice.is_none() {
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
                    bytes_served: entry.bytes_served,
                    raw_range_id,
                    observed_range: mapping.observed_range.clone(),
                };

                if let Some(cache_slice) = missing_slice {
                    let key = MissingProofKey {
                        service_id,
                        normalized_uri_id,
                        cache_slice,
                    };
                    missing_proofs
                        .entry(key)
                        .or_default()
                        .record(observation.clone());
                }

                if qualifies_for_burst {
                    // A no-range log line is one logical observation with two possible cache-key
                    // locations. Ranged requests are counted once for every physical slice they cover.
                    let slice_kinds: Vec<CacheSliceKind> = match observation.observed_range {
                        ObservedByteRange::NoRange => vec![CacheSliceKind::NoRange],
                        ObservedByteRange::Inclusive { .. } => mapping
                            .slices
                            .iter()
                            .map(|slice| slice.kind.clone())
                            .collect(),
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
                }

                eligible_entries += 1;
                if eligible_entries % 500_000 == 0 {
                    eprintln!(
                        "  Processed {} eligible entries across {} physical evidence keys",
                        eligible_entries,
                        trackers.len() + missing_proofs.len()
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

        let mut qualified_winners = trackers
            .into_iter()
            .filter_map(|(key, accumulator)| accumulator.winning.map(|winning| (key, winning)))
            .collect::<Vec<_>>();
        if mode == DetectionMode::Redownload {
            // Client remains part of the accumulator key so retry bursts qualify independently.
            // Once qualified, collapse by the immutable physical slice before constructing
            // candidates so report counts and destructive evidence cannot duplicate a path.
            let mut by_physical_slice: BTreeMap<
                (u32, u32, CacheSliceKind),
                (EvidenceKey, Vec<InternalObservation>),
            > = BTreeMap::new();
            for (key, winning) in qualified_winners {
                let physical_identity = (
                    key.service_id,
                    key.normalized_uri_id,
                    key.cache_slice.clone(),
                );
                if let Some((selected_key, selected_winning)) =
                    by_physical_slice.get_mut(&physical_identity)
                {
                    if compare_qualified_winners(
                        &interner,
                        &key,
                        &winning,
                        selected_key,
                        selected_winning,
                    )? == Ordering::Less
                    {
                        *selected_key = key;
                        *selected_winning = winning;
                    }
                } else {
                    by_physical_slice.insert(physical_identity, (key, winning));
                }
            }
            qualified_winners = by_physical_slice.into_values().collect();
        }

        let mut candidates = Vec::new();
        for (key, winning) in qualified_winners {
            if let Some(candidate) = self.build_candidate(mode, &interner, key, winning)? {
                candidates.push(candidate);
            }
        }
        if mode == DetectionMode::CacheAndLogs {
            candidates.extend(self.build_missing_slice_candidates(
                &interner,
                missing_proofs,
                &mut inspect_path,
            )?);
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
                    bytes_served: observation.bytes_served,
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
            supporting_sibling: None,
            observations,
        }))
    }

    fn build_missing_slice_candidates<F>(
        &self,
        interner: &StringInterner,
        missing_proofs: HashMap<MissingProofKey, LatestProofAccumulator>,
        inspect_path: &mut F,
    ) -> Result<Vec<CorruptionCandidate>>
    where
        F: FnMut(&Path) -> MappedPathState,
    {
        let mut groups: BTreeMap<(u32, u32), Vec<(CacheSliceKind, InternalObservation)>> =
            BTreeMap::new();
        for (key, accumulator) in missing_proofs {
            let Some(observation) = accumulator.latest else {
                continue;
            };
            groups
                .entry((key.service_id, key.normalized_uri_id))
                .or_default()
                .push((key.cache_slice, observation));
        }

        let mut candidates = Vec::new();
        for ((service_id, normalized_uri_id), mut proofs) in groups {
            proofs.sort_by(|left, right| left.0.cmp(&right.0));
            let service = interner.get(service_id)?.to_string();
            let normalized_uri = interner.get(normalized_uri_id)?.to_string();

            // Resolve and inspect each proven slice once up front. Besides keeping sibling
            // selection deterministic, this avoids repeatedly walking every sibling when a URI
            // has many missing slices and no present support.
            let mut resolved_proofs = Vec::with_capacity(proofs.len());
            for (cache_slice, observation) in proofs {
                let paths = self.paths_for_slice(&service, &normalized_uri, &cache_slice);
                let [(resolved_slice, exact_path)] = paths.as_slice() else {
                    continue;
                };
                if resolved_slice != &cache_slice {
                    continue;
                }
                resolved_proofs.push(ResolvedMissingProof {
                    cache_slice,
                    observation,
                    exact_path: exact_path.clone(),
                    initial_path_state: inspect_path(exact_path),
                });
            }
            let present_support_indices = resolved_proofs
                .iter()
                .enumerate()
                .filter_map(|(index, proof)| {
                    (proof.initial_path_state == MappedPathState::PresentSafeRegular)
                        .then_some(index)
                })
                .collect::<Vec<_>>();

            for target in resolved_proofs
                .iter()
                .filter(|proof| proof.initial_path_state == MappedPathState::MissingSafe)
            {
                let Some(sibling) = present_support_indices.iter().find_map(|index| {
                    let sibling = &resolved_proofs[*index];
                    (sibling.cache_slice != target.cache_slice
                        && sibling.exact_path != target.exact_path)
                        .then_some(sibling)
                }) else {
                    continue;
                };

                // Revalidate the selected support immediately before the target absence check.
                // Both sides can change while a large scan is resolving candidates.
                if inspect_path(&sibling.exact_path) != MappedPathState::PresentSafeRegular {
                    continue;
                }
                let supporting_sibling = SupportingSiblingEvidence {
                    cache_slice: sibling.cache_slice.clone(),
                    exact_path: sibling.exact_path.display().to_string(),
                };

                // The filesystem can change while the sibling is inspected. Absence is evidence
                // only if the exact mapped target is still safely NotFound afterwards.
                if inspect_path(&target.exact_path) != MappedPathState::MissingSafe {
                    continue;
                }

                let observation = CandidateObservation {
                    timestamp: format_timestamp(target.observation.timestamp),
                    client_ip: interner.get(target.observation.client_id)?.to_string(),
                    raw_url: interner.get(target.observation.raw_url_id)?.to_string(),
                    method: "GET".to_string(),
                    http_status: target.observation.http_status,
                    bytes_served: target.observation.bytes_served,
                    cache_status: "HIT".to_string(),
                    raw_range: target
                        .observation
                        .raw_range_id
                        .map(|id| interner.get(id).map(str::to_string))
                        .transpose()?,
                };
                let observations = vec![observation];
                let first_seen = observations[0].timestamp.clone();
                let last_seen = first_seen.clone();
                let raw_url = observations[0].raw_url.clone();
                let reason = DetectionReason::MissingCachedSlice;
                let candidate_id = stable_candidate_id(
                    DetectionMode::CacheAndLogs,
                    self.miss_threshold,
                    &service,
                    &normalized_uri,
                    &target.cache_slice,
                    None,
                    reason,
                    &observations,
                );

                candidates.push(CorruptionCandidate {
                    candidate_id,
                    mode: DetectionMode::CacheAndLogs,
                    threshold: self.miss_threshold,
                    service: service.clone(),
                    raw_url,
                    normalized_uri: normalized_uri.clone(),
                    observed_range: target.observation.observed_range.clone(),
                    cache_slice: target.cache_slice.clone(),
                    exact_paths: vec![target.exact_path.display().to_string()],
                    evidence_count: 1,
                    first_seen,
                    last_seen,
                    retry_client: None,
                    reason,
                    validation_state: ValidationState::ExactPathMissing,
                    removal_allowed: false,
                    supporting_sibling: Some(supporting_sibling),
                    observations,
                });
            }
        }

        Ok(candidates)
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
        let mut removable_service_counts = BTreeMap::new();
        let mut review_only_service_counts = BTreeMap::new();
        let mut removable_total = 0usize;
        let mut review_only_total = 0usize;
        for candidate in &candidates {
            *service_counts.entry(candidate.service.clone()).or_insert(0) += 1;
            if candidate.removal_allowed {
                *removable_service_counts
                    .entry(candidate.service.clone())
                    .or_insert(0) += 1;
                removable_total += 1;
            } else {
                *review_only_service_counts
                    .entry(candidate.service.clone())
                    .or_insert(0) += 1;
                review_only_total += 1;
            }
        }
        let total = candidates.len();
        debug_assert_eq!(total, removable_total + review_only_total);
        let scan_started_utc = self
            .scan_started_utc
            .to_rfc3339_opts(SecondsFormat::AutoSi, true);
        let summary = CorruptionSummary {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            mode,
            threshold: self.miss_threshold,
            lookback_days: self.lookback_days,
            scan_started_utc: scan_started_utc.clone(),
            service_counts: service_counts.clone(),
            removable_service_counts: removable_service_counts.clone(),
            review_only_service_counts: review_only_service_counts.clone(),
            total_corrupted: total,
            removable_total,
            review_only_total,
        };
        CorruptionReport {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            mode,
            threshold: self.miss_threshold,
            lookback_days: self.lookback_days,
            scan_started_utc,
            service_counts,
            removable_service_counts,
            review_only_service_counts,
            total,
            removable_total,
            review_only_total,
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

fn canonical_cache_root(cache_dir: &Path) -> Option<PathBuf> {
    let metadata = std::fs::metadata(cache_dir).ok()?;
    if !metadata.is_dir() {
        return None;
    }
    std::fs::canonicalize(cache_dir).ok()
}

fn inspect_mapped_path(
    cache_dir: &Path,
    canonical_root: Option<&Path>,
    mapped_path: &Path,
) -> MappedPathState {
    let Some(canonical_root) = canonical_root else {
        return MappedPathState::Unverifiable;
    };
    match (
        std::fs::metadata(cache_dir),
        std::fs::canonicalize(cache_dir),
    ) {
        (Ok(metadata), Ok(current_root)) if metadata.is_dir() && current_root == canonical_root => {
        }
        _ => return MappedPathState::Unverifiable,
    }
    let Ok(relative) = mapped_path.strip_prefix(cache_dir) else {
        return MappedPathState::Unverifiable;
    };
    let mut components = relative.components().peekable();
    if components.peek().is_none() {
        return MappedPathState::Unverifiable;
    }

    let mut current = cache_dir.to_path_buf();
    while let Some(component) = components.next() {
        let Component::Normal(part) = component else {
            return MappedPathState::Unverifiable;
        };
        current.push(part);
        let is_leaf = components.peek().is_none();
        match std::fs::symlink_metadata(&current) {
            Ok(metadata) => {
                if metadata.file_type().is_symlink() {
                    return MappedPathState::Unverifiable;
                }
                if is_leaf {
                    if !metadata.is_file() {
                        return MappedPathState::Unverifiable;
                    }
                    return match cache_utils::safe_path_under_root(cache_dir, &current) {
                        Ok(canonical) if canonical.starts_with(canonical_root) => {
                            MappedPathState::PresentSafeRegular
                        }
                        Ok(_) | Err(_) => MappedPathState::Unverifiable,
                    };
                }
                if !metadata.is_dir() {
                    return MappedPathState::Unverifiable;
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return MappedPathState::MissingSafe;
            }
            Err(_) => return MappedPathState::Unverifiable,
        }
    }

    MappedPathState::Unverifiable
}

fn format_timestamp(timestamp: NaiveDateTime) -> String {
    timestamp.format("%Y-%m-%dT%H:%M:%SZ").to_string()
}

fn compare_qualified_winners(
    interner: &StringInterner,
    left_key: &EvidenceKey,
    left: &[InternalObservation],
    right_key: &EvidenceKey,
    right: &[InternalObservation],
) -> Result<Ordering> {
    let left_first = left
        .first()
        .context("qualified Re-download winner had no first observation")?;
    let right_first = right
        .first()
        .context("qualified Re-download winner had no first observation")?;
    let mut order = left_first.timestamp.cmp(&right_first.timestamp);
    if order != Ordering::Equal {
        return Ok(order);
    }

    let left_last = left
        .last()
        .context("qualified Re-download winner had no last observation")?;
    let right_last = right
        .last()
        .context("qualified Re-download winner had no last observation")?;
    order = left_last.timestamp.cmp(&right_last.timestamp);
    if order != Ordering::Equal {
        return Ok(order);
    }

    let left_client = left_key
        .retry_client_id
        .context("qualified Re-download winner had no client")?;
    let right_client = right_key
        .retry_client_id
        .context("qualified Re-download winner had no client")?;
    order = interner.get(left_client)?.cmp(interner.get(right_client)?);
    if order != Ordering::Equal {
        return Ok(order);
    }

    compare_observation_sequences(interner, left, right)
}

fn compare_observation_sequences(
    interner: &StringInterner,
    left: &[InternalObservation],
    right: &[InternalObservation],
) -> Result<Ordering> {
    for (left, right) in left.iter().zip(right) {
        let mut order = left.timestamp.cmp(&right.timestamp);
        if order != Ordering::Equal {
            return Ok(order);
        }
        order = interner
            .get(left.client_id)?
            .cmp(interner.get(right.client_id)?);
        if order != Ordering::Equal {
            return Ok(order);
        }
        order = interner
            .get(left.raw_url_id)?
            .cmp(interner.get(right.raw_url_id)?);
        if order != Ordering::Equal {
            return Ok(order);
        }
        order = left.http_status.cmp(&right.http_status);
        if order != Ordering::Equal {
            return Ok(order);
        }
        order = left.bytes_served.cmp(&right.bytes_served);
        if order != Ordering::Equal {
            return Ok(order);
        }
        let left_raw_range = left.raw_range_id.map(|id| interner.get(id)).transpose()?;
        let right_raw_range = right.raw_range_id.map(|id| interner.get(id)).transpose()?;
        order = left_raw_range.cmp(&right_raw_range);
        if order != Ordering::Equal {
            return Ok(order);
        }
        order = observed_range_order_key(&left.observed_range)
            .cmp(&observed_range_order_key(&right.observed_range));
        if order != Ordering::Equal {
            return Ok(order);
        }
    }

    Ok(left.len().cmp(&right.len()))
}

fn observed_range_order_key(range: &ObservedByteRange) -> (u8, u64, u64) {
    match range {
        ObservedByteRange::NoRange => (0, 0, 0),
        ObservedByteRange::Inclusive { start, end } => (1, *start, *end),
    }
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
        append_field(&mut identity, &observation.bytes_served.to_string());
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
    use chrono::{DateTime, Duration, NaiveDateTime, Utc};
    use std::fs;

    const FIXED_SCAN_SECOND: i64 = 30 * 24 * 60 * 60;

    fn log_line(
        second: i64,
        client: &str,
        method: &str,
        status: i32,
        cache_status: &str,
        url: &str,
        range: Option<&str>,
    ) -> String {
        log_line_with_bytes(
            second,
            client,
            method,
            status,
            "1024",
            cache_status,
            url,
            range,
        )
    }

    fn log_line_with_bytes(
        second: i64,
        client: &str,
        method: &str,
        status: i32,
        bytes_served: &str,
        cache_status: &str,
        url: &str,
        range: Option<&str>,
    ) -> String {
        let base = NaiveDateTime::parse_from_str("2024-01-01 00:00:00", "%Y-%m-%d %H:%M:%S")
            .expect("valid base time");
        let timestamp = (base + Duration::seconds(second)).format("%d/%b/%Y:%H:%M:%S");
        format!(
            "[steam] {client} / - - - [{timestamp} +0000] \"{method} {url} HTTP/1.1\" {status} {bytes_served} \"-\" \"Test\" \"{cache_status}\" \"cdn.test\" \"{}\"",
            range.unwrap_or("-")
        )
    }

    fn fixed_scan_start() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2024-01-31T00:00:00Z")
            .expect("valid fixed scan start")
            .with_timezone(&Utc)
    }

    fn write_log(log_dir: &Path, lines: &[String]) {
        fs::create_dir_all(log_dir).expect("create log dir");
        fs::write(log_dir.join("access.log"), lines.join("\n") + "\n").expect("write log");
    }

    fn write_slice(path: &Path) {
        fs::create_dir_all(path.parent().expect("cache path parent")).expect("create cache path");
        fs::write(path, b"slice").expect("write cache slice");
    }

    fn report(
        cache_dir: &Path,
        log_dir: &Path,
        threshold: usize,
        mode: DetectionMode,
    ) -> CorruptionReport {
        report_at(
            cache_dir,
            log_dir,
            threshold,
            mode,
            DEFAULT_LOOKBACK_DAYS,
            fixed_scan_start(),
        )
    }

    fn report_at(
        cache_dir: &Path,
        log_dir: &Path,
        threshold: usize,
        mode: DetectionMode,
        lookback_days: u32,
        scan_started_utc: DateTime<Utc>,
    ) -> CorruptionReport {
        fs::create_dir_all(cache_dir).expect("create cache dir");
        CorruptionDetector::new(cache_dir, threshold, lookback_days, scan_started_utc)
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
    fn redownload_collapses_qualified_clients_by_physical_slice() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let mut lines = Vec::new();
        for second in [10, 20, 30] {
            // Both clients independently reach threshold for the same physical slice. Their
            // first/last timestamps tie, so lexical client order must select 10.0.0.1 even though
            // the other client is encountered first in the log.
            lines.push(log_line(
                second,
                "10.0.0.9",
                "GET",
                206,
                "HIT",
                "/dedupe.bin?winner=z",
                Some("bytes=100-200"),
            ));
            lines.push(log_line(
                second,
                "10.0.0.1",
                "GET",
                206,
                "HIT",
                "/dedupe.bin?winner=a",
                Some("bytes=100-200"),
            ));
            // A distinct physical slice for the same normalized URI remains independent.
            lines.push(log_line(
                second + 1,
                "10.0.0.5",
                "GET",
                206,
                "HIT",
                "/dedupe.bin?winner=second-slice",
                Some("bytes=1048600-1048700"),
            ));
        }
        write_log(&log_dir, &lines);

        let first_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", "/dedupe.bin", 0, 1_048_575);
        let second_path = cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/dedupe.bin",
            1_048_576,
            2_097_151,
        );
        write_slice(&first_path);
        write_slice(&second_path);

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::Redownload);
        assert_eq!(result.total, 2);
        assert_eq!(result.removable_total, 2);
        assert_eq!(result.review_only_total, 0);
        assert_eq!(result.service_counts.get("steam"), Some(&2));
        assert_eq!(result.removable_service_counts.get("steam"), Some(&2));
        assert_eq!(result.candidates.len(), 2);

        let first = &result.candidates[0];
        assert_eq!(
            first.cache_slice,
            CacheSliceKind::Ranged {
                start: 0,
                end: 1_048_575
            }
        );
        assert_eq!(first.retry_client.as_deref(), Some("10.0.0.1"));
        assert_eq!(first.raw_url, "/dedupe.bin?winner=a");
        assert!(first
            .observations
            .iter()
            .all(|observation| observation.client_ip == "10.0.0.1"));

        assert_eq!(
            result.candidates[1].cache_slice,
            CacheSliceKind::Ranged {
                start: 1_048_576,
                end: 2_097_151
            }
        );
        assert_eq!(
            result.candidates[1].retry_client.as_deref(),
            Some("10.0.0.5")
        );

        let removal_paths = result
            .candidates
            .iter()
            .filter(|candidate| candidate.removal_allowed)
            .flat_map(|candidate| candidate.exact_paths.iter().cloned())
            .collect::<Vec<_>>();
        let unique_removal_paths = removal_paths
            .iter()
            .collect::<std::collections::BTreeSet<_>>();
        assert_eq!(removal_paths.len(), 2);
        assert_eq!(unique_removal_paths.len(), removal_paths.len());
        assert!(removal_paths.contains(&first_path.display().to_string()));
        assert!(removal_paths.contains(&second_path.display().to_string()));
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
    fn redownload_uses_closed_caller_supplied_lookback_window() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let mut lines = Vec::new();

        for second in [-1, 0, 1] {
            lines.push(log_line(
                second,
                "old",
                "GET",
                206,
                "HIT",
                "/before-cutoff.bin",
                Some("bytes=10-20"),
            ));
        }
        for second in [0, 1, 2] {
            lines.push(log_line(
                second,
                "cutoff",
                "GET",
                206,
                "HIT",
                "/at-cutoff.bin",
                Some("bytes=10-20"),
            ));
        }
        for second in [
            FIXED_SCAN_SECOND - 2,
            FIXED_SCAN_SECOND - 1,
            FIXED_SCAN_SECOND,
        ] {
            lines.push(log_line(
                second,
                "start",
                "GET",
                206,
                "HIT",
                "/at-scan-start.bin",
                Some("bytes=10-20"),
            ));
        }
        for second in [
            FIXED_SCAN_SECOND,
            FIXED_SCAN_SECOND + 1,
            FIXED_SCAN_SECOND + 2,
        ] {
            lines.push(log_line(
                second,
                "future",
                "GET",
                206,
                "HIT",
                "/future.bin",
                Some("bytes=10-20"),
            ));
        }
        write_log(&log_dir, &lines);

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::Redownload);
        assert_eq!(result.lookback_days, 30);
        assert_eq!(result.scan_started_utc, "2024-01-31T00:00:00Z");
        assert_eq!(result.total, 2);
        let urls: Vec<&str> = result
            .candidates
            .iter()
            .map(|candidate| candidate.raw_url.as_str())
            .collect();
        assert_eq!(urls, vec!["/at-cutoff.bin", "/at-scan-start.bin"]);
    }

    #[test]
    fn missing_cached_slice_requires_full_single_slice_hit_and_safe_proven_sibling() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let url = "/partial.bin";
        let target_range = "bytes=1048600-1048700";
        let sibling_range = "bytes=2097200-2097300";
        write_log(
            &log_dir,
            &[
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 20,
                    "target",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some(target_range),
                ),
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 19,
                    "sibling",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some(sibling_range),
                ),
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 18,
                    "earlier-sibling",
                    "GET",
                    206,
                    "11",
                    "HIT",
                    url,
                    Some("bytes=10-20"),
                ),
            ],
        );
        let sibling_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", url, 2_097_152, 3_145_727);
        write_slice(&sibling_path);
        let earlier_sibling_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", url, 0, 1_048_575);
        write_slice(&earlier_sibling_path);
        let target_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", url, 1_048_576, 2_097_151);

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs);
        assert_eq!(result.total, 1);
        assert_eq!(result.removable_total, 0);
        assert_eq!(result.review_only_total, 1);
        assert_eq!(result.service_counts.get("steam"), Some(&1));
        assert_eq!(result.review_only_service_counts.get("steam"), Some(&1));
        assert!(result.removable_service_counts.is_empty());

        let candidate = &result.candidates[0];
        assert_eq!(candidate.mode, DetectionMode::CacheAndLogs);
        assert_eq!(candidate.reason, DetectionReason::MissingCachedSlice);
        assert_eq!(
            candidate.validation_state,
            ValidationState::ExactPathMissing
        );
        assert!(!candidate.removal_allowed);
        assert_eq!(candidate.evidence_count, 1);
        assert_eq!(
            candidate.exact_paths,
            vec![target_path.display().to_string()]
        );
        assert_eq!(candidate.observations.len(), 1);
        assert_eq!(candidate.observations[0].bytes_served, 101);
        assert_eq!(candidate.observations[0].cache_status, "HIT");
        let support = candidate
            .supporting_sibling
            .as_ref()
            .expect("missing-slice support");
        assert_eq!(
            support.cache_slice,
            CacheSliceKind::Ranged {
                start: 0,
                end: 1_048_575
            }
        );
        assert_eq!(
            support.exact_path,
            earlier_sibling_path.display().to_string()
        );
        let wire = serde_json::to_value(candidate).expect("serialize missing candidate");
        assert_eq!(wire["reason"], "missing_cached_slice");
        assert_eq!(wire["supporting_sibling"]["cache_slice"]["kind"], "ranged");
        assert_eq!(wire["supporting_sibling"]["exact_path"], support.exact_path);
        assert_eq!(
            serde_json::from_value::<CorruptionCandidate>(wire.clone())
                .expect("round-trip candidate"),
            *candidate
        );
        let mut missing_sibling_path = wire;
        missing_sibling_path["supporting_sibling"]
            .as_object_mut()
            .expect("sibling object")
            .remove("exact_path");
        assert!(serde_json::from_value::<CorruptionCandidate>(missing_sibling_path).is_err());
    }

    #[test]
    fn missing_cached_slice_rejects_ineligible_target_proof() {
        struct Case {
            name: &'static str,
            second: i64,
            method: &'static str,
            status: i32,
            bytes: &'static str,
            cache_status: &'static str,
            range: Option<&'static str>,
        }

        let cases = [
            Case {
                name: "miss",
                second: FIXED_SCAN_SECOND - 100,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "MISS",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "status-200",
                second: FIXED_SCAN_SECOND - 99,
                method: "GET",
                status: 200,
                bytes: "101",
                cache_status: "HIT",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "partial",
                second: FIXED_SCAN_SECOND - 98,
                method: "GET",
                status: 206,
                bytes: "100",
                cache_status: "HIT",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "zero",
                second: FIXED_SCAN_SECOND - 97,
                method: "GET",
                status: 206,
                bytes: "0",
                cache_status: "HIT",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "dash",
                second: FIXED_SCAN_SECOND - 96,
                method: "GET",
                status: 206,
                bytes: "-",
                cache_status: "HIT",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "multi-slice",
                second: FIXED_SCAN_SECOND - 95,
                method: "GET",
                status: 206,
                bytes: "2",
                cache_status: "HIT",
                range: Some("bytes=1048575-1048576"),
            },
            Case {
                name: "no-range",
                second: FIXED_SCAN_SECOND - 94,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "HIT",
                range: None,
            },
            Case {
                name: "invalid-range",
                second: FIXED_SCAN_SECOND - 93,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "HIT",
                range: Some("bytes=10-"),
            },
            Case {
                name: "old",
                second: -1,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "HIT",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "future",
                second: FIXED_SCAN_SECOND + 1,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "HIT",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "head",
                second: FIXED_SCAN_SECOND - 92,
                method: "HEAD",
                status: 206,
                bytes: "101",
                cache_status: "HIT",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "bypass",
                second: FIXED_SCAN_SECOND - 91,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "BYPASS",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "stale",
                second: FIXED_SCAN_SECOND - 90,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "STALE",
                range: Some("bytes=1048600-1048700"),
            },
            Case {
                name: "unknown",
                second: FIXED_SCAN_SECOND - 89,
                method: "GET",
                status: 206,
                bytes: "101",
                cache_status: "UNKNOWN",
                range: Some("bytes=1048600-1048700"),
            },
        ];

        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let mut lines = Vec::new();
        for case in &cases {
            let url = format!("/invalid-{}.bin", case.name);
            lines.push(log_line_with_bytes(
                case.second,
                "target",
                case.method,
                case.status,
                case.bytes,
                case.cache_status,
                &url,
                case.range,
            ));
            lines.push(log_line_with_bytes(
                FIXED_SCAN_SECOND - 10,
                "sibling",
                "GET",
                206,
                "101",
                "HIT",
                &url,
                Some("bytes=2097200-2097300"),
            ));
            write_slice(&cache_utils::calculate_cache_path(
                &cache_dir, "steam", &url, 2_097_152, 3_145_727,
            ));
        }
        write_log(&log_dir, &lines);

        let result = report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs);
        assert!(result.candidates.is_empty());
    }

    #[test]
    fn unproven_and_no_range_forms_do_not_support_a_missing_ranged_slice() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let url = "/range-with-whole-object.bin";
        write_log(
            &log_dir,
            &[log_line_with_bytes(
                FIXED_SCAN_SECOND - 1,
                "target",
                "GET",
                206,
                "101",
                "HIT",
                url,
                Some("bytes=1048600-1048700"),
            )],
        );
        write_slice(&cache_utils::calculate_cache_path_no_range(
            &cache_dir, "steam", url,
        ));
        write_slice(&cache_utils::calculate_cache_path_noslice(
            &cache_dir, "steam", url,
        ));
        write_slice(&cache_utils::calculate_cache_path(
            &cache_dir, "steam", url, 2_097_152, 3_145_727,
        ));

        assert!(report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs)
            .candidates
            .is_empty());
    }

    #[test]
    fn missing_cached_slice_rechecks_absence_after_sibling_validation() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        fs::create_dir_all(&cache_dir).expect("cache root");
        let url = "/racy.bin";
        write_log(
            &log_dir,
            &[
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 2,
                    "target",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some("bytes=1048600-1048700"),
                ),
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 1,
                    "sibling",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some("bytes=2097200-2097300"),
                ),
            ],
        );
        let target_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", url, 1_048_576, 2_097_151);
        let sibling_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", url, 2_097_152, 3_145_727);
        let detector =
            CorruptionDetector::new(&cache_dir, 3, DEFAULT_LOOKBACK_DAYS, fixed_scan_start());
        let mut target_checks = 0usize;
        let result = detector
            .generate_report_for_mode_with_path_inspector(
                DetectionMode::CacheAndLogs,
                &log_dir,
                "access.log",
                chrono_tz::UTC,
                None,
                |path| {
                    if path == target_path {
                        target_checks += 1;
                        if target_checks == 1 {
                            MappedPathState::MissingSafe
                        } else {
                            MappedPathState::PresentSafeRegular
                        }
                    } else if path == sibling_path {
                        MappedPathState::PresentSafeRegular
                    } else {
                        MappedPathState::Unverifiable
                    }
                },
            )
            .expect("report");
        assert!(result.candidates.is_empty());
        assert_eq!(target_checks, 2);
    }

    #[test]
    fn missing_cached_slice_abstains_for_unavailable_or_unverifiable_paths() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let missing_root = fixture.path().join("offline-cache");
        let url = "/offline.bin";
        write_log(
            &log_dir,
            &[
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 2,
                    "target",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some("bytes=1048600-1048700"),
                ),
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 1,
                    "sibling",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some("bytes=2097200-2097300"),
                ),
            ],
        );
        let detector =
            CorruptionDetector::new(&missing_root, 3, DEFAULT_LOOKBACK_DAYS, fixed_scan_start());
        let unavailable = detector
            .generate_report_for_mode(
                DetectionMode::CacheAndLogs,
                &log_dir,
                "access.log",
                chrono_tz::UTC,
                None,
            )
            .expect("unavailable root report");
        assert!(unavailable.candidates.is_empty());

        fs::create_dir_all(&missing_root).expect("cache root");
        let unknown = detector
            .generate_report_for_mode_with_path_inspector(
                DetectionMode::CacheAndLogs,
                &log_dir,
                "access.log",
                chrono_tz::UTC,
                None,
                |_| MappedPathState::Unverifiable,
            )
            .expect("unverifiable path report");
        assert!(unknown.candidates.is_empty());
    }

    #[cfg(unix)]
    #[test]
    fn symlink_sibling_cannot_support_a_missing_cached_slice() {
        use std::os::unix::fs::symlink;

        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let url = "/symlink.bin";
        write_log(
            &log_dir,
            &[
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 2,
                    "target",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some("bytes=1048600-1048700"),
                ),
                log_line_with_bytes(
                    FIXED_SCAN_SECOND - 1,
                    "sibling",
                    "GET",
                    206,
                    "101",
                    "HIT",
                    url,
                    Some("bytes=2097200-2097300"),
                ),
            ],
        );
        let sibling_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", url, 2_097_152, 3_145_727);
        fs::create_dir_all(sibling_path.parent().expect("sibling parent")).expect("mkdir");
        let outside = fixture.path().join("outside");
        fs::write(&outside, b"outside").expect("outside file");
        symlink(&outside, &sibling_path).expect("symlink sibling");

        assert!(report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs)
            .candidates
            .is_empty());
        assert_eq!(fs::read(outside).expect("outside remains"), b"outside");
    }

    #[test]
    fn repeated_miss_modes_are_not_filtered_by_the_new_lookback() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let url = "/old-repeated-miss.bin";
        let lines: Vec<String> = [-100, -99, -98]
            .into_iter()
            .map(|second| {
                log_line(
                    second,
                    "client",
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=1048600-1048700"),
                )
            })
            .collect();
        write_log(&log_dir, &lines);
        write_slice(&cache_utils::calculate_cache_path(
            &cache_dir, "steam", url, 1_048_576, 2_097_151,
        ));

        assert_eq!(
            report(&cache_dir, &log_dir, 3, DetectionMode::LogsOnly).total,
            1
        );
        assert_eq!(
            report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs).total,
            1
        );
    }

    #[test]
    fn mixed_service_report_partitions_removable_and_review_counts() {
        let fixture = tempfile::tempdir().expect("fixture");
        let log_dir = fixture.path().join("logs");
        let cache_dir = fixture.path().join("cache");
        let mut lines: Vec<String> = (0..3)
            .map(|second| {
                log_line(
                    second,
                    "miss",
                    "GET",
                    206,
                    "MISS",
                    "/removable.bin",
                    Some("bytes=1048600-1048700"),
                )
            })
            .collect();
        lines.extend([
            log_line_with_bytes(
                FIXED_SCAN_SECOND - 2,
                "target",
                "GET",
                206,
                "101",
                "HIT",
                "/review.bin",
                Some("bytes=1048600-1048700"),
            ),
            log_line_with_bytes(
                FIXED_SCAN_SECOND - 1,
                "sibling",
                "GET",
                206,
                "101",
                "HIT",
                "/review.bin",
                Some("bytes=2097200-2097300"),
            ),
        ]);
        write_log(&log_dir, &lines);
        write_slice(&cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/removable.bin",
            1_048_576,
            2_097_151,
        ));
        write_slice(&cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/review.bin",
            2_097_152,
            3_145_727,
        ));

        let report = report(&cache_dir, &log_dir, 3, DetectionMode::CacheAndLogs);
        assert_eq!(report.total, 2);
        assert_eq!(report.removable_total, 1);
        assert_eq!(report.review_only_total, 1);
        assert_eq!(report.service_counts.get("steam"), Some(&2));
        assert_eq!(report.removable_service_counts.get("steam"), Some(&1));
        assert_eq!(report.review_only_service_counts.get("steam"), Some(&1));
        assert_eq!(
            report.removable_total + report.review_only_total,
            report.total
        );
    }

    #[test]
    fn empty_report_preserves_v2_scan_identity_and_zero_projections() {
        let fixture = tempfile::tempdir().expect("fixture");
        let report = report_at(
            &fixture.path().join("cache"),
            &fixture.path().join("logs"),
            3,
            DetectionMode::Redownload,
            365,
            fixed_scan_start(),
        );
        assert_eq!(report.contract_version, 2);
        assert_eq!(report.lookback_days, 365);
        assert_eq!(report.scan_started_utc, "2024-01-31T00:00:00Z");
        assert!(report.service_counts.is_empty());
        assert!(report.removable_service_counts.is_empty());
        assert!(report.review_only_service_counts.is_empty());
        assert_eq!(report.total, 0);
        assert_eq!(report.removable_total, 0);
        assert_eq!(report.review_only_total, 0);
    }

    #[test]
    fn detector_rejects_out_of_range_lookback() {
        let fixture = tempfile::tempdir().expect("fixture");
        for lookback_days in [0, 366] {
            let detector = CorruptionDetector::new(
                fixture.path().join("cache"),
                3,
                lookback_days,
                fixed_scan_start(),
            );
            assert!(detector
                .generate_report_for_mode(
                    DetectionMode::Redownload,
                    fixture.path().join("logs"),
                    "access.log",
                    chrono_tz::UTC,
                    None,
                )
                .is_err());
        }
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
        assert_eq!(object.len(), 12);
        for field in [
            "contract_version",
            "mode",
            "threshold",
            "lookback_days",
            "scan_started_utc",
            "service_counts",
            "removable_service_counts",
            "review_only_service_counts",
            "total",
            "removable_total",
            "review_only_total",
            "candidates",
        ] {
            assert!(object.contains_key(field), "missing {field}");
        }
        assert_eq!(value["contract_version"], 2);
        assert_eq!(value["lookback_days"], 30);
        assert_eq!(value["scan_started_utc"], "2024-01-31T00:00:00Z");
        assert_eq!(value["removable_total"], 0);
        assert_eq!(value["review_only_total"], 2);
        assert_eq!(value["mode"], "logs_only");
        assert_eq!(value["candidates"][0]["validation_state"], "log_suspect");
        assert_eq!(
            value["candidates"][0]["supporting_sibling"],
            serde_json::Value::Null
        );
        assert_eq!(
            value["candidates"][0]["observations"][0]["bytes_served"],
            1024
        );
        assert_eq!(
            value["candidates"][0]["observed_range"]["kind"],
            "inclusive"
        );
        assert_eq!(value["candidates"][0]["cache_slice"]["kind"], "ranged");

        for required in [
            "lookback_days",
            "scan_started_utc",
            "removable_service_counts",
            "review_only_service_counts",
            "removable_total",
            "review_only_total",
        ] {
            let mut missing = value.clone();
            missing
                .as_object_mut()
                .expect("report object")
                .remove(required);
            assert!(
                serde_json::from_value::<CorruptionReport>(missing).is_err(),
                "missing {required} must fail"
            );
        }
        let mut missing_supporting_sibling = value.clone();
        missing_supporting_sibling["candidates"][0]
            .as_object_mut()
            .expect("candidate object")
            .remove("supporting_sibling");
        assert!(serde_json::from_value::<CorruptionReport>(missing_supporting_sibling).is_err());
        let mut missing_bytes = value;
        missing_bytes["candidates"][0]["observations"][0]
            .as_object_mut()
            .expect("observation object")
            .remove("bytes_served");
        assert!(serde_json::from_value::<CorruptionReport>(missing_bytes).is_err());
    }
}
