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

pub const CORRUPTION_CONTRACT_VERSION: u32 = 3;
pub const DEFAULT_LOOKBACK_DAYS: u32 = 30;
pub const MIN_LOOKBACK_DAYS: u32 = 1;
pub const MAX_LOOKBACK_DAYS: u32 = 365;
const EVIDENCE_WINDOW_SECONDS: i64 = 60;

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

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CandidateObservation {
    pub timestamp: String,
    pub client_ip: String,
    pub raw_url: String,
    pub method: String,
    pub http_status: i32,
    pub bytes_served: i64,
    pub cache_status: String,
    pub raw_range: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CorruptionCandidate {
    pub candidate_id: String,
    pub service: String,
    pub raw_url: String,
    pub normalized_uri: String,
    pub observed_range: ObservedByteRange,
    pub cache_slice: CacheSliceKind,
    pub exact_paths: Vec<String>,
    pub evidence_count: usize,
    pub first_seen: String,
    pub last_seen: String,
    pub observations: Vec<CandidateObservation>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CorruptionReport {
    pub contract_version: u32,
    pub threshold: usize,
    pub lookback_days: u32,
    pub scan_started_utc: String,
    pub service_counts: BTreeMap<String, usize>,
    pub total: usize,
    pub candidates: Vec<CorruptionCandidate>,
}

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
        }
    }

    pub fn generate_report<P: AsRef<Path>>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
    ) -> Result<CorruptionReport> {
        let cache_dir = self.cache_dir.clone();
        let canonical_root = canonical_cache_root(&cache_dir);
        self.generate_report_with_path_inspector(
            log_dir,
            log_base_name,
            timezone,
            progress_path,
            move |path| mapped_path_is_safe_regular(&cache_dir, canonical_root.as_deref(), path),
        )
    }

    fn generate_report_with_path_inspector<P, F>(
        &self,
        log_dir: P,
        log_base_name: &str,
        timezone: chrono_tz::Tz,
        progress_path: Option<&Path>,
        mut path_is_safe_regular: F,
    ) -> Result<CorruptionReport>
    where
        P: AsRef<Path>,
        F: FnMut(&Path) -> bool,
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
            return Ok(self.build_report(Vec::new()));
        }

        eprintln!("Scanning {total_files} log files for repeated MISS evidence...");
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
            let file_size = std::fs::metadata(&log_file.path)
                .map(|metadata| metadata.len())
                .unwrap_or(0);
            if let Some(progress_file) = progress_path {
                self.write_detection_progress(
                    progress_file,
                    "scanning",
                    &format!("Scanning file {}/{}...", file_index + 1, total_files),
                    file_index,
                    total_files,
                    (file_index as f64 / total_files as f64) * 90.0,
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
                    || entry.cache_status != "MISS"
                    || entry.timestamp < cutoff
                    || entry.timestamp > scan_started_naive
                {
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
                let observation = InternalObservation {
                    timestamp: entry.timestamp,
                    client_id,
                    raw_url_id,
                    http_status: entry.status_code,
                    bytes_served: entry.bytes_served,
                    raw_range_id,
                    observed_range: mapping.observed_range.clone(),
                };
                let slice_kinds: Vec<CacheSliceKind> = match observation.observed_range {
                    ObservedByteRange::NoRange => vec![CacheSliceKind::NoRange],
                    ObservedByteRange::Inclusive { .. } => mapping
                        .slices
                        .iter()
                        .map(|slice| slice.kind.clone())
                        .collect(),
                };
                for cache_slice in slice_kinds {
                    trackers
                        .entry(EvidenceKey {
                            service_id,
                            normalized_uri_id,
                            cache_slice,
                        })
                        .or_default()
                        .record(observation.clone(), self.miss_threshold);
                }
                eligible_entries += 1;
                if eligible_entries.is_multiple_of(500_000) {
                    eprintln!(
                        "  Processed {eligible_entries} eligible entries across {} physical evidence keys",
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
            if let Some(candidate) =
                self.build_candidate(&interner, key, winning, &mut path_is_safe_regular)?
            {
                candidates.push(candidate);
            }
        }
        candidates.sort_by(|left, right| {
            (
                &left.service,
                &left.normalized_uri,
                &left.cache_slice,
                &left.candidate_id,
            )
                .cmp(&(
                    &right.service,
                    &right.normalized_uri,
                    &right.cache_slice,
                    &right.candidate_id,
                ))
        });

        let report = self.build_report(candidates);
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

    fn build_candidate<F>(
        &self,
        interner: &StringInterner,
        key: EvidenceKey,
        winning: Vec<InternalObservation>,
        path_is_safe_regular: &mut F,
    ) -> Result<Option<CorruptionCandidate>>
    where
        F: FnMut(&Path) -> bool,
    {
        let service = interner.get(key.service_id)?.to_string();
        let normalized_uri = interner.get(key.normalized_uri_id)?.to_string();
        let first_internal = winning
            .first()
            .context("qualified corruption evidence had no observations")?;
        debug_assert_eq!(
            compare_observation_sequences(interner, &winning, &winning)?,
            Ordering::Equal
        );
        let raw_url = interner.get(first_internal.raw_url_id)?.to_string();
        let observed_range = first_internal.observed_range.clone();

        let present = self
            .paths_for_slice(&service, &normalized_uri, &key.cache_slice)
            .into_iter()
            .filter(|(_, path)| path_is_safe_regular(path))
            .collect::<Vec<_>>();
        if present.is_empty() {
            return Ok(None);
        }
        let cache_slice = if present.len() == 1 {
            present[0].0.clone()
        } else {
            key.cache_slice
        };
        let exact_paths = present
            .into_iter()
            .map(|(_, path)| path.display().to_string())
            .collect::<Vec<_>>();

        let observations = winning
            .iter()
            .map(|observation| {
                Ok(CandidateObservation {
                    timestamp: format_timestamp(observation.timestamp),
                    client_ip: interner.get(observation.client_id)?.to_string(),
                    raw_url: interner.get(observation.raw_url_id)?.to_string(),
                    method: "GET".to_string(),
                    http_status: observation.http_status,
                    bytes_served: observation.bytes_served,
                    cache_status: "MISS".to_string(),
                    raw_range: observation
                        .raw_range_id
                        .map(|id| interner.get(id).map(str::to_string))
                        .transpose()?,
                })
            })
            .collect::<Result<Vec<_>>>()?;
        let first_seen = observations
            .first()
            .context("qualified corruption evidence had no first observation")?
            .timestamp
            .clone();
        let last_seen = observations
            .last()
            .context("qualified corruption evidence had no last observation")?
            .timestamp
            .clone();
        let candidate_id = stable_candidate_id(
            self.miss_threshold,
            &service,
            &normalized_uri,
            &cache_slice,
            &observations,
        );

        Ok(Some(CorruptionCandidate {
            candidate_id,
            service,
            raw_url,
            normalized_uri,
            observed_range,
            cache_slice,
            exact_paths,
            evidence_count: observations.len(),
            first_seen,
            last_seen,
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

    fn build_report(&self, candidates: Vec<CorruptionCandidate>) -> CorruptionReport {
        let mut service_counts = BTreeMap::new();
        for candidate in &candidates {
            *service_counts.entry(candidate.service.clone()).or_insert(0) += 1;
        }
        CorruptionReport {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            threshold: self.miss_threshold,
            lookback_days: self.lookback_days,
            scan_started_utc: self
                .scan_started_utc
                .to_rfc3339_opts(SecondsFormat::AutoSi, true),
            service_counts,
            total: candidates.len(),
            candidates,
        }
    }

    #[allow(clippy::too_many_arguments)]
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
        progress_utils::write_progress_json(
            progress_path,
            &CorruptionDetectionProgress {
                status: status.to_string(),
                message: message.to_string(),
                files_processed,
                total_files,
                percent_complete,
                current_file,
                timestamp: progress_utils::current_timestamp(),
            },
        )
    }
}

fn canonical_cache_root(cache_dir: &Path) -> Option<PathBuf> {
    let metadata = std::fs::metadata(cache_dir).ok()?;
    if !metadata.is_dir() {
        return None;
    }
    std::fs::canonicalize(cache_dir).ok()
}

fn mapped_path_is_safe_regular(
    cache_dir: &Path,
    canonical_root: Option<&Path>,
    mapped_path: &Path,
) -> bool {
    let Some(canonical_root) = canonical_root else {
        return false;
    };
    match (
        std::fs::metadata(cache_dir),
        std::fs::canonicalize(cache_dir),
    ) {
        (Ok(metadata), Ok(current_root)) if metadata.is_dir() && current_root == canonical_root => {
        }
        _ => return false,
    }
    let Ok(relative) = mapped_path.strip_prefix(cache_dir) else {
        return false;
    };
    let mut components = relative.components().peekable();
    if components.peek().is_none() {
        return false;
    }
    let mut current = cache_dir.to_path_buf();
    while let Some(component) = components.next() {
        let Component::Normal(part) = component else {
            return false;
        };
        current.push(part);
        let is_leaf = components.peek().is_none();
        let Ok(metadata) = std::fs::symlink_metadata(&current) else {
            return false;
        };
        if metadata.file_type().is_symlink() {
            return false;
        }
        if is_leaf {
            if !metadata.is_file() {
                return false;
            }
            return matches!(
                cache_utils::safe_path_under_root(cache_dir, &current),
                Ok(canonical) if canonical.starts_with(canonical_root)
            );
        }
        if !metadata.is_dir() {
            return false;
        }
    }
    false
}

fn format_timestamp(timestamp: NaiveDateTime) -> String {
    timestamp.format("%Y-%m-%dT%H:%M:%SZ").to_string()
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
    threshold: usize,
    service: &str,
    normalized_uri: &str,
    cache_slice: &CacheSliceKind,
    observations: &[CandidateObservation],
) -> String {
    fn append_field(buffer: &mut String, value: &str) {
        use std::fmt::Write;
        let _ = write!(buffer, "{}:{}|", value.len(), value);
    }

    let mut identity = String::new();
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
    use chrono::Duration;
    use std::fs;

    const BASE: &str = "2024-01-31T00:00:00Z";

    fn scan_start() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(BASE)
            .unwrap()
            .with_timezone(&Utc)
    }

    fn log_line(
        at: DateTime<Utc>,
        method: &str,
        status: i32,
        cache: &str,
        url: &str,
        range: Option<&str>,
    ) -> String {
        format!(
            "[steam] 192.0.2.10 / - - - [{} +0000] \"{method} {url} HTTP/1.1\" {status} 1024 \"-\" \"Test\" \"{cache}\" \"cdn.test\" \"{}\"",
            at.format("%d/%b/%Y:%H:%M:%S"),
            range.unwrap_or("-")
        )
    }

    fn write_log(log_dir: &Path, lines: &[String]) {
        fs::create_dir_all(log_dir).unwrap();
        fs::write(log_dir.join("access.log"), lines.join("\n") + "\n").unwrap();
    }

    fn write_slice(path: &Path) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, b"slice").unwrap();
    }

    fn ranged_path(cache_dir: &Path, url: &str) -> PathBuf {
        cache_utils::calculate_cache_path(cache_dir, "steam", url, 0, 1_048_575)
    }

    fn report(
        cache_dir: &Path,
        log_dir: &Path,
        threshold: usize,
        lookback: u32,
    ) -> CorruptionReport {
        CorruptionDetector::new(cache_dir, threshold, lookback, scan_start())
            .generate_report(log_dir, "access.log", chrono_tz::UTC, None)
            .unwrap()
    }

    #[test]
    fn same_slice_qualifies_at_each_threshold_and_retains_bounded_evidence() {
        for threshold in [3, 5, 10] {
            let temp = tempfile::tempdir().unwrap();
            let cache_dir = temp.path().join("cache");
            let log_dir = temp.path().join("logs");
            let url = "/depot/file.bin";
            write_slice(&ranged_path(&cache_dir, url));
            let lines = (0..threshold + 2)
                .map(|i| {
                    log_line(
                        scan_start() - Duration::seconds(30 - i as i64),
                        "GET",
                        206,
                        "MISS",
                        url,
                        Some("bytes=0-1048575"),
                    )
                })
                .collect::<Vec<_>>();
            write_log(&log_dir, &lines);
            let result = report(&cache_dir, &log_dir, threshold, 1);
            assert_eq!(result.total, 1);
            assert_eq!(result.candidates[0].evidence_count, threshold);
            assert_eq!(result.candidates[0].observations.len(), threshold);
        }
    }

    #[test]
    fn repeated_miss_respects_closed_caller_supplied_lookback_window() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        for url in ["/old.bin", "/cutoff.bin", "/start.bin", "/future.bin"] {
            write_slice(&ranged_path(&cache_dir, url));
        }
        let cutoff = scan_start() - Duration::days(1);
        let mut lines = Vec::new();
        for i in 0..3 {
            lines.push(log_line(
                cutoff - Duration::seconds(61 - i),
                "GET",
                206,
                "MISS",
                "/old.bin",
                Some("bytes=0-1048575"),
            ));
            lines.push(log_line(
                cutoff + Duration::seconds(i),
                "GET",
                206,
                "MISS",
                "/cutoff.bin",
                Some("bytes=0-1048575"),
            ));
            lines.push(log_line(
                scan_start() - Duration::seconds(2 - i),
                "GET",
                206,
                "MISS",
                "/start.bin",
                Some("bytes=0-1048575"),
            ));
            lines.push(log_line(
                scan_start() + Duration::seconds(i as i64 + 1),
                "GET",
                206,
                "MISS",
                "/future.bin",
                Some("bytes=0-1048575"),
            ));
        }
        write_log(&log_dir, &lines);
        let result = report(&cache_dir, &log_dir, 3, 1);
        assert_eq!(result.total, 2);
        assert_eq!(
            result
                .candidates
                .iter()
                .map(|candidate| candidate.raw_url.as_str())
                .collect::<Vec<_>>(),
            ["/cutoff.bin", "/start.bin"]
        );
    }

    #[test]
    fn normalization_equivalent_spellings_retain_each_observation_raw_url() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        write_slice(&ranged_path(&cache_dir, "/depot/file.bin"));
        write_log(
            &log_dir,
            &[
                log_line(
                    scan_start() - Duration::seconds(2),
                    "GET",
                    206,
                    "MISS",
                    "/depot/file.bin",
                    Some("bytes=0-1048575"),
                ),
                log_line(
                    scan_start() - Duration::seconds(1),
                    "GET",
                    206,
                    "MISS",
                    "//depot//file.bin",
                    Some("bytes=0-1048575"),
                ),
                log_line(
                    scan_start(),
                    "GET",
                    206,
                    "MISS",
                    "/depot/file.bin",
                    Some("bytes=0-1048575"),
                ),
            ],
        );
        let result = report(&cache_dir, &log_dir, 3, 1);
        assert_eq!(result.total, 1);
        assert_eq!(result.candidates[0].normalized_uri, "/depot/file.bin");
        assert_eq!(
            result.candidates[0]
                .observations
                .iter()
                .map(|observation| observation.raw_url.as_str())
                .collect::<Vec<_>>(),
            ["/depot/file.bin", "//depot//file.bin", "/depot/file.bin"]
        );
    }

    #[test]
    fn no_range_evidence_resolves_only_present_exact_alternative() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        let url = "/no-range.bin";
        let noslice = cache_utils::calculate_cache_path_noslice(&cache_dir, "steam", url);
        write_slice(&noslice);
        write_log(
            &log_dir,
            &(0..3)
                .map(|second| {
                    log_line(
                        scan_start() - Duration::seconds(2 - second),
                        "GET",
                        200,
                        "MISS",
                        url,
                        None,
                    )
                })
                .collect::<Vec<_>>(),
        );
        let result = report(&cache_dir, &log_dir, 3, 1);
        assert_eq!(result.total, 1);
        assert_eq!(result.candidates[0].cache_slice, CacheSliceKind::Noslice);
        assert_eq!(
            result.candidates[0].exact_paths,
            [noslice.display().to_string()]
        );
        assert_eq!(
            result.candidates[0].observed_range,
            ObservedByteRange::NoRange
        );
    }

    #[test]
    fn sixty_second_boundary_is_inclusive_but_sixty_one_is_not() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        for url in ["/inclusive.bin", "/outside.bin"] {
            write_slice(&ranged_path(&cache_dir, url));
        }
        let base = scan_start() - Duration::hours(1);
        let mut lines = Vec::new();
        for (url, offsets) in [
            ("/inclusive.bin", [0, 30, 60]),
            ("/outside.bin", [0, 30, 61]),
        ] {
            for offset in offsets {
                lines.push(log_line(
                    base + Duration::seconds(offset),
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=0-1048575"),
                ));
            }
        }
        write_log(&log_dir, &lines);
        let result = report(&cache_dir, &log_dir, 3, 1);
        assert_eq!(result.total, 1);
        assert_eq!(result.candidates[0].raw_url, "/inclusive.bin");
    }

    #[test]
    fn ineligible_method_http_and_cache_statuses_never_count() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        let url = "/ineligible.bin";
        write_slice(&ranged_path(&cache_dir, url));
        write_log(
            &log_dir,
            &[
                log_line(
                    scan_start(),
                    "POST",
                    206,
                    "MISS",
                    url,
                    Some("bytes=0-1048575"),
                ),
                log_line(
                    scan_start(),
                    "GET",
                    404,
                    "MISS",
                    url,
                    Some("bytes=0-1048575"),
                ),
                log_line(
                    scan_start(),
                    "GET",
                    206,
                    "HIT",
                    url,
                    Some("bytes=0-1048575"),
                ),
            ],
        );
        assert_eq!(report(&cache_dir, &log_dir, 3, 1).total, 0);
    }

    #[test]
    fn distinct_physical_ranges_do_not_aggregate() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        let url = "/range.bin";
        write_slice(&cache_utils::calculate_cache_path(
            &cache_dir, "steam", url, 0, 1_048_575,
        ));
        write_slice(&cache_utils::calculate_cache_path(
            &cache_dir, "steam", url, 1_048_576, 2_097_151,
        ));
        write_log(
            &log_dir,
            &[
                log_line(
                    scan_start() - Duration::seconds(2),
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=0-1048575"),
                ),
                log_line(
                    scan_start() - Duration::seconds(1),
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=1048576-2097151"),
                ),
                log_line(
                    scan_start(),
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=0-1048575"),
                ),
            ],
        );
        assert_eq!(report(&cache_dir, &log_dir, 3, 1).total, 0);
    }

    #[test]
    fn absent_and_non_regular_exact_paths_are_not_emitted() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        let absent = "/absent.bin";
        let directory = "/directory.bin";
        fs::create_dir_all(ranged_path(&cache_dir, directory)).unwrap();
        let mut lines = Vec::new();
        for i in 0..3 {
            for url in [absent, directory] {
                lines.push(log_line(
                    scan_start() - Duration::seconds(i),
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=0-1048575"),
                ));
            }
        }
        write_log(&log_dir, &lines);
        assert_eq!(report(&cache_dir, &log_dir, 3, 1).total, 0);
    }

    #[cfg(unix)]
    #[test]
    fn symlink_exact_path_is_not_emitted() {
        use std::os::unix::fs::symlink;
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        let path = ranged_path(&cache_dir, "/link.bin");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let target = temp.path().join("target");
        fs::write(&target, b"target").unwrap();
        symlink(&target, &path).unwrap();
        write_log(
            &log_dir,
            &(0..3)
                .map(|i| {
                    log_line(
                        scan_start() - Duration::seconds(i),
                        "GET",
                        206,
                        "MISS",
                        "/link.bin",
                        Some("bytes=0-1048575"),
                    )
                })
                .collect::<Vec<_>>(),
        );
        assert_eq!(report(&cache_dir, &log_dir, 3, 1).total, 0);
    }

    #[test]
    fn report_ids_order_and_wire_shape_are_deterministic_v3() {
        let temp = tempfile::tempdir().unwrap();
        let cache_dir = temp.path().join("cache");
        let log_dir = temp.path().join("logs");
        for url in ["/b.bin", "/a.bin"] {
            write_slice(&ranged_path(&cache_dir, url));
        }
        let mut lines = Vec::new();
        for i in 0..3 {
            for url in ["/b.bin", "/a.bin"] {
                lines.push(log_line(
                    scan_start() - Duration::seconds(2 - i),
                    "GET",
                    206,
                    "MISS",
                    url,
                    Some("bytes=0-1048575"),
                ));
            }
        }
        write_log(&log_dir, &lines);
        let first = report(&cache_dir, &log_dir, 3, 1);
        let second = report(&cache_dir, &log_dir, 3, 1);
        assert_eq!(first, second);
        assert_eq!(first.contract_version, 3);
        assert_eq!(first.candidates[0].raw_url, "/a.bin");
        let value = serde_json::to_value(first).unwrap();
        assert_eq!(
            value
                .as_object()
                .unwrap()
                .keys()
                .cloned()
                .collect::<Vec<_>>(),
            [
                "candidates",
                "contract_version",
                "lookback_days",
                "scan_started_utc",
                "service_counts",
                "threshold",
                "total"
            ]
        );
        let candidate = value["candidates"][0].as_object().unwrap();
        for removed in [
            "mode",
            "reason",
            "retry_client",
            "validation_state",
            "removal_allowed",
            "supporting_sibling",
        ] {
            assert!(!candidate.contains_key(removed));
        }
    }

    #[test]
    fn detector_rejects_invalid_threshold_and_lookback() {
        let temp = tempfile::tempdir().unwrap();
        let log_dir = temp.path().join("logs");
        fs::create_dir_all(&log_dir).unwrap();
        for (threshold, lookback) in [(4, 1), (3, 0), (3, 366)] {
            assert!(
                CorruptionDetector::new(temp.path(), threshold, lookback, scan_start())
                    .generate_report(&log_dir, "access.log", chrono_tz::UTC, None)
                    .is_err()
            );
        }
    }

    #[test]
    fn observation_sequence_comparison_is_stable() {
        let mut interner = StringInterner::default();
        let client_id = interner.intern("client").unwrap();
        let raw_url_id = interner.intern("/a").unwrap();
        let observation = InternalObservation {
            timestamp: scan_start().naive_utc(),
            client_id,
            raw_url_id,
            http_status: 206,
            bytes_served: 1,
            raw_range_id: None,
            observed_range: ObservedByteRange::NoRange,
        };
        assert_eq!(
            compare_observation_sequences(&interner, &[observation.clone()], &[observation])
                .unwrap(),
            Ordering::Equal
        );
    }
}
