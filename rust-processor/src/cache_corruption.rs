use anyhow::{bail, Context, Result};
use chrono::{DateTime, Utc};
use clap::{Parser, Subcommand};
use serde::{Deserialize, Serialize};
use serde_json::json;
use sqlx::{PgPool, Row};
use std::collections::{BTreeSet, HashSet};
use std::fs::File;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use uuid::Uuid;

mod cache_corruption_detector;
mod cache_utils;
mod cancel;
mod db;
mod log_discovery;
mod log_purge;
mod log_reader;
mod models;
mod parser;
mod progress_events;
mod progress_utils;
mod service_utils;
mod tact_products;

use cache_corruption_detector::{
    CorruptionCandidate, CorruptionDetector, DetectionMode, DetectionReason, ValidationState,
    CORRUPTION_CONTRACT_VERSION, DEFAULT_LOOKBACK_DAYS,
};
use cache_utils::{CacheSliceKind, ObservedByteRange};
use log_purge::{ExactLogMatcher, ExactLogObservation};
use progress_events::ProgressReporter;

const EXACT_DB_DELETE_SQL: &str = r#"
DELETE FROM "LogEntries"
WHERE LOWER("Service") = LOWER($1)
  AND "Datasource" = $2
  AND "ClientIp" = $3
  AND "Timestamp" = $4
  AND "Method" = $5
  AND "Url" = $6
  AND "StatusCode" = $7
  AND "CacheStatus" = $8
  AND "HttpRange" = $9
  AND "BytesServed" = $10
RETURNING "DownloadId"
"#;

#[derive(Parser, Debug)]
#[command(name = "cache_corruption")]
#[command(about = "Detects and removes corrupted cache chunks")]
struct Args {
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand, Debug)]
enum Commands {
    /// Find exact physical-slice candidates and write a detailed JSON report.
    Detect {
        log_dir: String,
        cache_dir: String,
        output_json: String,
        #[arg(default_value = "UTC")]
        timezone: String,
        #[arg(default_value_t = 3)]
        threshold: usize,
        #[arg(
            long,
            default_value_t = DEFAULT_LOOKBACK_DAYS,
            value_parser = clap::value_parser!(u32).range(1..=365)
        )]
        lookback_days: u32,
        /// Shared UTC scan anchor. Standalone callers may omit it to capture UTC now once.
        #[arg(long)]
        scan_started_utc: Option<String>,
        /// Canonical mode: logs_only, cache_and_logs, or redownload.
        #[arg(long)]
        mode: Option<String>,
        /// Safe legacy scan-input alias for logs_only.
        #[arg(long, default_value_t = false)]
        no_cache_check: bool,
        /// Safe legacy scan-input alias for redownload.
        #[arg(long, default_value_t = false)]
        detect_redownloads: bool,
        #[arg(long)]
        progress_json: Option<String>,
        #[arg(short, long)]
        progress: bool,
    },
    /// Emit the canonical full report as compact JSON on stdout.
    Summary {
        log_dir: String,
        cache_dir: String,
        #[arg(default_value = "none")]
        progress_json: String,
        #[arg(default_value = "UTC")]
        timezone: String,
        #[arg(default_value_t = 3)]
        threshold: usize,
        #[arg(
            long,
            default_value_t = DEFAULT_LOOKBACK_DAYS,
            value_parser = clap::value_parser!(u32).range(1..=365)
        )]
        lookback_days: u32,
        /// Shared UTC scan anchor. Standalone callers may omit it to capture UTC now once.
        #[arg(long)]
        scan_started_utc: Option<String>,
        #[arg(long)]
        mode: Option<String>,
        #[arg(long, default_value_t = false)]
        no_cache_check: bool,
        #[arg(long, default_value_t = false)]
        detect_redownloads: bool,
        #[arg(short, long)]
        progress: bool,
    },
    /// Remove only exact paths and observations supplied by the persisted evidence file.
    Remove {
        log_dir: String,
        cache_dir: String,
        service: String,
        progress_json: String,
        #[arg(long)]
        evidence_file: String,
        #[arg(short, long)]
        progress: bool,
    },
    /// Permanently purge exact review-only access-log/database evidence without touching cache.
    PurgeHistory {
        log_dir: String,
        /// Exact service name, or the literal `all` for a server-resolved all-services envelope.
        scope: String,
        progress_json: String,
        #[arg(long)]
        evidence_file: String,
        #[arg(short, long)]
        progress: bool,
    },
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ProgressData {
    status: String,
    stage_key: String,
    context: serde_json::Value,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    #[serde(rename = "filesProcessed")]
    files_processed: usize,
    #[serde(rename = "totalFiles")]
    total_files: usize,
    timestamp: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct RemovalEvidenceEnvelope {
    contract_version: u32,
    scan_id: String,
    mode: DetectionMode,
    threshold: usize,
    datasource: String,
    candidates: Vec<RemovalCandidate>,
}

/// C# attaches datasource to Worker 1's canonical candidate. `flatten` consumes that one extra
/// field while preserving the shared candidate type unchanged.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct RemovalCandidate {
    datasource: String,
    #[serde(flatten)]
    candidate: CorruptionCandidate,
}

#[derive(Debug)]
struct ValidatedRemovalEvidence {
    scan_id: Uuid,
    mode: DetectionMode,
    threshold: usize,
    datasource: String,
    candidates: Vec<CorruptionCandidate>,
    exact_paths: Vec<PathBuf>,
    observations: Vec<ExactLogObservation>,
}

#[derive(Debug)]
struct ValidatedHistoryEvidence {
    scan_id: Uuid,
    mode: DetectionMode,
    threshold: usize,
    datasource: String,
    candidate_count: usize,
    observations: Vec<ExactLogObservation>,
}

#[derive(Debug, Default, PartialEq, Eq)]
struct ExactPathRemovalOutcome {
    deleted_files: usize,
    already_missing: usize,
    bytes_freed: u64,
}

fn parse_timezone(value: &str) -> Result<chrono_tz::Tz> {
    value
        .parse()
        .with_context(|| format!("invalid timezone '{value}'"))
}

fn parse_scan_started_utc(value: Option<&str>) -> Result<DateTime<Utc>> {
    let Some(value) = value else {
        return Ok(Utc::now());
    };
    let parsed = DateTime::parse_from_rfc3339(value)
        .with_context(|| format!("invalid scan_started_utc '{value}'"))?;
    if parsed.offset().local_minus_utc() != 0 {
        bail!("scan_started_utc must use the UTC offset");
    }
    Ok(parsed.with_timezone(&Utc))
}

fn parse_detection_mode(value: &str) -> Result<DetectionMode> {
    match value.trim().to_ascii_lowercase().as_str() {
        "logs_only" | "logsonly" => Ok(DetectionMode::LogsOnly),
        "cache_and_logs" | "cacheandlogs" | "miss_count" | "misscount" => {
            Ok(DetectionMode::CacheAndLogs)
        }
        "redownload" => Ok(DetectionMode::Redownload),
        _ => bail!(
            "invalid corruption mode '{value}'; expected logs_only, cache_and_logs, or redownload"
        ),
    }
}

fn resolve_scan_mode(
    canonical: Option<&str>,
    no_cache_check: bool,
    detect_redownloads: bool,
) -> Result<DetectionMode> {
    if no_cache_check && detect_redownloads {
        bail!("--no-cache-check and --detect-redownloads are inconsistent legacy aliases");
    }

    let legacy = if detect_redownloads {
        Some(DetectionMode::Redownload)
    } else if no_cache_check {
        Some(DetectionMode::LogsOnly)
    } else {
        None
    };

    match (canonical, legacy) {
        (Some(value), Some(alias)) => {
            let mode = parse_detection_mode(value)?;
            if mode != alias {
                bail!("canonical --mode conflicts with a legacy scan alias");
            }
            Ok(mode)
        }
        (Some(value), None) => parse_detection_mode(value),
        (None, Some(alias)) => Ok(alias),
        (None, None) => Ok(DetectionMode::CacheAndLogs),
    }
}

fn write_pretty_json<T: Serialize + ?Sized>(output_path: &Path, value: &T) -> Result<()> {
    let output_file = File::create(output_path)
        .with_context(|| format!("failed to create report {}", output_path.display()))?;
    let mut output_writer = BufWriter::new(output_file);
    serde_json::to_writer_pretty(&mut output_writer, value)
        .context("failed to serialize corruption report")?;
    output_writer.flush()?;
    Ok(())
}

fn write_progress(
    progress_path: &Path,
    reporter: &ProgressReporter,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
) -> Result<()> {
    let progress = ProgressData {
        status: status.to_string(),
        stage_key: stage_key.to_string(),
        context: context.clone(),
        percent_complete,
        files_processed,
        total_files,
        timestamp: progress_utils::current_timestamp(),
    };
    progress_utils::write_progress_json(progress_path, &progress)?;

    match status {
        "starting" => reporter.emit_started(stage_key, context),
        "completed" => reporter.emit_complete(stage_key, context),
        "failed" => {
            let detail = context
                .get("errorDetail")
                .and_then(|value| value.as_str())
                .map(str::to_string);
            reporter.emit_failed(stage_key, context, detail);
        }
        "cancelled" => reporter.emit_cancelled(stage_key, context),
        _ => reporter.emit_progress(percent_complete, stage_key, context),
    }
    Ok(())
}

fn parse_evidence_timestamp(value: &str) -> Result<DateTime<Utc>> {
    Ok(DateTime::parse_from_rfc3339(value)
        .with_context(|| format!("invalid evidence timestamp '{value}'"))?
        .with_timezone(&Utc))
}

fn observed_range_header(range: &ObservedByteRange) -> String {
    match range {
        ObservedByteRange::NoRange => String::new(),
        ObservedByteRange::Inclusive { start, end } => format!("bytes={start}-{end}"),
    }
}

fn slice_is_mapped(candidate_slice: &CacheSliceKind, mapped_slice: &CacheSliceKind) -> bool {
    match candidate_slice {
        // A no-range observation can retain both no-range and ::noslice exact alternatives.
        CacheSliceKind::NoRange => {
            matches!(
                mapped_slice,
                CacheSliceKind::NoRange | CacheSliceKind::Noslice
            )
        }
        other => other == mapped_slice,
    }
}

fn expected_paths_for_candidate(
    cache_dir: &Path,
    candidate: &CorruptionCandidate,
) -> Result<HashSet<PathBuf>> {
    let mapping = cache_utils::physical_slices_for_request(
        cache_dir,
        &candidate.service,
        &candidate.raw_url,
        &observed_range_header(&candidate.observed_range),
    )
    .context("candidate observed range cannot be mapped to physical cache slices")?;

    if mapping.normalized_uri != candidate.normalized_uri {
        bail!("candidate normalized URI does not match its raw URL");
    }

    let paths: HashSet<PathBuf> = mapping
        .slices
        .into_iter()
        .filter(|slice| slice_is_mapped(&candidate.cache_slice, &slice.kind))
        .map(|slice| slice.exact_path)
        .collect();
    if paths.is_empty() {
        bail!("candidate cache slice is not covered by its observed range");
    }
    Ok(paths)
}

fn validate_observation_identity(
    candidate: &CorruptionCandidate,
    observation: &cache_corruption_detector::CandidateObservation,
    expected_status: &str,
) -> Result<ExactLogObservation> {
    if observation.raw_url.trim().is_empty() {
        bail!("candidate observation raw_url is missing; legacy evidence cannot be removed safely");
    }
    if observation.client_ip.trim().is_empty() {
        bail!("candidate observation client_ip is missing");
    }
    if observation.method != "GET" {
        bail!("candidate observation method must be GET");
    }
    if !matches!(observation.http_status, 200 | 206) {
        bail!("candidate observation HTTP status must be 200 or 206");
    }
    if observation.cache_status != expected_status {
        bail!("candidate observation cache status does not match removal mode");
    }
    if observation.raw_range.as_deref() == Some("") {
        bail!("empty ranges must be represented as null in candidate observations");
    }

    Ok(ExactLogObservation {
        service: candidate.service.clone(),
        raw_url: observation.raw_url.clone(),
        timestamp: parse_evidence_timestamp(&observation.timestamp)?,
        client_ip: observation.client_ip.clone(),
        method: observation.method.clone(),
        http_status: observation.http_status,
        bytes_served: observation.bytes_served,
        cache_status: observation.cache_status.clone(),
        raw_range: observation.raw_range.clone(),
    })
}

fn validate_observation(
    cache_dir: &Path,
    candidate: &CorruptionCandidate,
    observation: &cache_corruption_detector::CandidateObservation,
    expected_status: &str,
) -> Result<ExactLogObservation> {
    let exact = validate_observation_identity(candidate, observation, expected_status)?;

    let raw_range = observation.raw_range.as_deref().unwrap_or("");
    let mapping = cache_utils::physical_slices_for_request(
        cache_dir,
        &candidate.service,
        &observation.raw_url,
        raw_range,
    )
    .context("candidate observation contains an unsupported range")?;
    if mapping.normalized_uri != candidate.normalized_uri
        || !mapping
            .slices
            .iter()
            .any(|slice| slice_is_mapped(&candidate.cache_slice, &slice.kind))
    {
        bail!("candidate observation does not map to the stored physical slice");
    }

    Ok(exact)
}

fn load_and_validate_removal_evidence(
    evidence_path: &Path,
    cache_dir: &Path,
    requested_service: &str,
) -> Result<ValidatedRemovalEvidence> {
    let file = File::open(evidence_path)
        .with_context(|| format!("failed to open evidence file {}", evidence_path.display()))?;
    let envelope: RemovalEvidenceEnvelope =
        serde_json::from_reader(file).context("failed to deserialize removal evidence")?;

    if envelope.contract_version != CORRUPTION_CONTRACT_VERSION {
        bail!(
            "unsupported corruption evidence contract version {}",
            envelope.contract_version
        );
    }
    let scan_id = Uuid::parse_str(&envelope.scan_id).context("invalid evidence scan_id")?;
    if scan_id.is_nil() {
        bail!("evidence scan_id must not be nil");
    }
    if envelope.mode == DetectionMode::LogsOnly {
        bail!("logs_only evidence is review-only and cannot be removed");
    }
    if !matches!(envelope.threshold, 3 | 5 | 10) {
        bail!("evidence threshold must be 3, 5, or 10");
    }
    if envelope.datasource.trim().is_empty() {
        bail!("evidence datasource is required");
    }
    if envelope.candidates.is_empty() {
        bail!("evidence file contains no removal candidates");
    }

    std::fs::canonicalize(cache_dir)
        .with_context(|| format!("cache root is not accessible: {}", cache_dir.display()))?;

    let expected_status = match envelope.mode {
        DetectionMode::CacheAndLogs => "MISS",
        DetectionMode::Redownload => "HIT",
        DetectionMode::LogsOnly => unreachable!("logs_only rejected above"),
    };
    let expected_reason = match envelope.mode {
        DetectionMode::CacheAndLogs => DetectionReason::RepeatedMissBurst,
        DetectionMode::Redownload => DetectionReason::SameClientHitRetryBurst,
        DetectionMode::LogsOnly => unreachable!("logs_only rejected above"),
    };

    let mut ids = HashSet::new();
    let mut paths = BTreeSet::new();
    let mut observations = Vec::new();
    let mut candidates = Vec::with_capacity(envelope.candidates.len());

    for wrapped in envelope.candidates {
        let candidate = wrapped.candidate;
        if candidate.reason == DetectionReason::MissingCachedSlice {
            bail!("missing_cached_slice evidence is review-only and cannot be removed");
        }
        if !wrapped
            .datasource
            .eq_ignore_ascii_case(&envelope.datasource)
        {
            bail!("candidate datasource does not match evidence envelope");
        }
        if candidate.mode != envelope.mode || candidate.threshold != envelope.threshold {
            bail!("candidate mode/threshold does not match evidence envelope");
        }
        if !candidate.service.eq_ignore_ascii_case(requested_service) {
            bail!("candidate service is outside the requested service scope");
        }
        if !candidate.removal_allowed
            || candidate.validation_state != ValidationState::ExactPathPresent
        {
            bail!("candidate is not exact-path-present removable evidence");
        }
        if candidate.reason != expected_reason {
            bail!("candidate reason does not match evidence mode");
        }
        if candidate.candidate_id.trim().is_empty() || !ids.insert(candidate.candidate_id.clone()) {
            bail!("candidate IDs must be non-empty and unique");
        }
        if candidate.exact_paths.is_empty() {
            bail!("removable candidate has no exact paths");
        }
        if candidate.evidence_count != candidate.observations.len()
            || candidate.evidence_count != envelope.threshold
        {
            bail!("candidate evidence count does not match its threshold/observations");
        }

        let first_seen = parse_evidence_timestamp(&candidate.first_seen)?;
        let last_seen = parse_evidence_timestamp(&candidate.last_seen)?;
        if last_seen < first_seen || (last_seen - first_seen).num_seconds() > 60 {
            bail!("candidate evidence window must be ordered and no longer than 60 seconds");
        }

        let expected_paths = expected_paths_for_candidate(cache_dir, &candidate)?;
        for exact_path in &candidate.exact_paths {
            let exact_path = PathBuf::from(exact_path);
            if !expected_paths.contains(&exact_path) {
                bail!("stored exact path does not match the candidate slice under this cache root");
            }
            if !paths.insert(exact_path.clone()) {
                bail!("duplicate exact path appears in removal evidence");
            }

            match std::fs::symlink_metadata(&exact_path) {
                Ok(metadata) => {
                    cache_utils::safe_path_under_root(cache_dir, &exact_path).with_context(
                        || format!("unsafe stored exact path {}", exact_path.display()),
                    )?;
                    if !metadata.is_file() {
                        bail!(
                            "stored exact path is not a regular file: {}",
                            exact_path.display()
                        );
                    }
                }
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                    // The exact path may have been evicted after the scan. Its canonical mapping
                    // was validated above; treating it as already absent is narrowing-only.
                }
                Err(error) => {
                    return Err(error).with_context(|| {
                        format!("failed to inspect exact path {}", exact_path.display())
                    });
                }
            }
        }

        let mut candidate_observations = Vec::with_capacity(candidate.observations.len());
        for observation in &candidate.observations {
            candidate_observations.push(validate_observation(
                cache_dir,
                &candidate,
                observation,
                expected_status,
            )?);
        }
        candidate_observations.sort_by_key(|observation| observation.timestamp);
        if candidate_observations
            .first()
            .map(|observation| observation.timestamp)
            != Some(first_seen)
            || candidate_observations
                .last()
                .map(|observation| observation.timestamp)
                != Some(last_seen)
        {
            bail!("candidate first/last timestamps do not match its observations");
        }
        if candidate.observed_range
            != cache_utils::parse_http_byte_range(
                candidate
                    .observations
                    .first()
                    .and_then(|observation| observation.raw_range.as_deref())
                    .unwrap_or(""),
            )
            .context("candidate first observation has an invalid range")?
        {
            bail!("candidate observed range does not match its first observation");
        }
        match envelope.mode {
            DetectionMode::Redownload => {
                let retry_client = candidate
                    .retry_client
                    .as_deref()
                    .context("redownload candidate is missing retry_client")?;
                if candidate_observations
                    .iter()
                    .any(|observation| observation.client_ip != retry_client)
                {
                    bail!("redownload observations do not match retry_client");
                }
            }
            DetectionMode::CacheAndLogs if candidate.retry_client.is_some() => {
                bail!("cache_and_logs candidate must not carry retry_client");
            }
            _ => {}
        }

        observations.extend(candidate_observations);
        candidates.push(candidate);
    }

    observations.sort_by(|left, right| {
        (
            left.timestamp,
            &left.service,
            &left.raw_url,
            &left.client_ip,
            &left.raw_range,
        )
            .cmp(&(
                right.timestamp,
                &right.service,
                &right.raw_url,
                &right.client_ip,
                &right.raw_range,
            ))
    });
    observations.dedup();

    Ok(ValidatedRemovalEvidence {
        scan_id,
        mode: envelope.mode,
        threshold: envelope.threshold,
        datasource: envelope.datasource,
        candidates,
        exact_paths: paths.into_iter().collect(),
        observations,
    })
}

fn valid_ranged_slice(slice: &CacheSliceKind) -> bool {
    let CacheSliceKind::Ranged { start, end } = slice else {
        return false;
    };
    start % cache_utils::DEFAULT_SLICE_SIZE == 0
        && end
            .checked_sub(*start)
            .and_then(|length| length.checked_add(1))
            == Some(cache_utils::DEFAULT_SLICE_SIZE)
}

fn validate_history_observation_mapping(
    candidate: &CorruptionCandidate,
    observation: &cache_corruption_detector::CandidateObservation,
) -> Result<()> {
    // The synthetic root is used only to reuse the pure request-to-slice calculation. History
    // purge has no cache-root input and this function performs no filesystem access.
    let mapping = cache_utils::physical_slices_for_request(
        Path::new("history-validation-only"),
        &candidate.service,
        &observation.raw_url,
        observation.raw_range.as_deref().unwrap_or(""),
    )
    .context("candidate observation contains an unsupported range")?;
    if mapping.normalized_uri != candidate.normalized_uri
        || !mapping
            .slices
            .iter()
            .any(|slice| slice_is_mapped(&candidate.cache_slice, &slice.kind))
    {
        bail!("candidate observation does not map to the stored logical slice");
    }
    Ok(())
}

fn load_and_validate_history_evidence(
    evidence_path: &Path,
    requested_scope: &str,
) -> Result<ValidatedHistoryEvidence> {
    let file = File::open(evidence_path)
        .with_context(|| format!("failed to open evidence file {}", evidence_path.display()))?;
    let envelope: RemovalEvidenceEnvelope =
        serde_json::from_reader(file).context("failed to deserialize history-purge evidence")?;

    if envelope.contract_version != CORRUPTION_CONTRACT_VERSION {
        bail!(
            "unsupported corruption evidence contract version {}",
            envelope.contract_version
        );
    }
    let scan_id = Uuid::parse_str(&envelope.scan_id).context("invalid evidence scan_id")?;
    if scan_id.is_nil() {
        bail!("evidence scan_id must not be nil");
    }
    if !matches!(envelope.threshold, 3 | 5 | 10) {
        bail!("evidence threshold must be 3, 5, or 10");
    }
    if envelope.datasource.trim().is_empty() {
        bail!("evidence datasource is required");
    }
    if envelope.candidates.is_empty() {
        bail!("evidence file contains no review-only candidates");
    }
    let requested_scope = requested_scope.trim();
    if requested_scope.is_empty() {
        bail!("history-purge scope is required");
    }
    let all_services = requested_scope.eq_ignore_ascii_case("all");

    let mut ids = HashSet::new();
    let mut observations = Vec::new();
    let candidate_count = envelope.candidates.len();

    for wrapped in envelope.candidates {
        if !wrapped
            .datasource
            .eq_ignore_ascii_case(&envelope.datasource)
        {
            bail!("candidate datasource does not match evidence envelope");
        }
        let candidate = wrapped.candidate;
        if candidate.mode != envelope.mode || candidate.threshold != envelope.threshold {
            bail!("candidate mode/threshold does not match evidence envelope");
        }
        if candidate.service.trim().is_empty()
            || (!all_services && !candidate.service.eq_ignore_ascii_case(requested_scope))
        {
            bail!("candidate service is outside the requested history-purge scope");
        }
        if candidate.removal_allowed {
            bail!("history purge accepts review-only evidence only");
        }
        if candidate.candidate_id.trim().is_empty() || !ids.insert(candidate.candidate_id.clone()) {
            bail!("candidate IDs must be non-empty and unique");
        }
        if candidate.normalized_uri.trim().is_empty()
            || candidate.raw_url.trim().is_empty()
            || candidate.exact_paths.is_empty()
            || candidate
                .exact_paths
                .iter()
                .any(|path| path.trim().is_empty())
            || candidate.exact_paths.iter().collect::<HashSet<_>>().len()
                != candidate.exact_paths.len()
        {
            bail!("review-only candidate contains malformed logical/path evidence");
        }
        if candidate.observations.is_empty()
            || candidate.evidence_count != candidate.observations.len()
        {
            bail!("candidate evidence count does not match its observations");
        }

        let expected_status = match (envelope.mode, candidate.reason, candidate.validation_state) {
            (
                DetectionMode::LogsOnly,
                DetectionReason::RepeatedMissBurst,
                ValidationState::LogSuspect,
            ) if candidate.evidence_count == envelope.threshold
                && candidate.retry_client.is_none()
                && candidate.supporting_sibling.is_none() =>
            {
                "MISS"
            }
            (
                DetectionMode::CacheAndLogs,
                DetectionReason::MissingCachedSlice,
                ValidationState::ExactPathMissing,
            ) if candidate.evidence_count == 1
                && candidate.retry_client.is_none()
                && candidate.exact_paths.len() == 1 =>
            {
                let sibling = candidate
                    .supporting_sibling
                    .as_ref()
                    .context("missing-slice candidate lacks supporting sibling evidence")?;
                if !valid_ranged_slice(&candidate.cache_slice)
                    || !valid_ranged_slice(&sibling.cache_slice)
                    || sibling.cache_slice == candidate.cache_slice
                    || sibling.exact_path.trim().is_empty()
                    || sibling.exact_path == candidate.exact_paths[0]
                {
                    bail!("missing-slice candidate has invalid supporting sibling evidence");
                }
                "HIT"
            }
            (
                DetectionMode::Redownload,
                DetectionReason::SameClientHitRetryBurst,
                ValidationState::ExactPathMissing,
            ) if candidate.evidence_count == envelope.threshold
                && candidate.supporting_sibling.is_none()
                && candidate
                    .retry_client
                    .as_deref()
                    .is_some_and(|client| !client.trim().is_empty()) =>
            {
                "HIT"
            }
            _ => bail!("candidate is not one of the closed review-only evidence shapes"),
        };

        let first_seen = parse_evidence_timestamp(&candidate.first_seen)?;
        let last_seen = parse_evidence_timestamp(&candidate.last_seen)?;
        if last_seen < first_seen || (last_seen - first_seen).num_seconds() > 60 {
            bail!("candidate evidence window must be ordered and no longer than 60 seconds");
        }

        let mut candidate_observations = Vec::with_capacity(candidate.observations.len());
        for observation in &candidate.observations {
            let exact = validate_observation_identity(&candidate, observation, expected_status)?;
            validate_history_observation_mapping(&candidate, observation)?;
            if envelope.mode == DetectionMode::Redownload
                && candidate.retry_client.as_deref() != Some(observation.client_ip.as_str())
            {
                bail!("redownload observations do not match retry_client");
            }
            candidate_observations.push(exact);
        }
        if candidate_observations
            .windows(2)
            .any(|window| window[1].timestamp < window[0].timestamp)
            || candidate_observations.first().map(|item| item.timestamp) != Some(first_seen)
            || candidate_observations.last().map(|item| item.timestamp) != Some(last_seen)
        {
            bail!("candidate first/last timestamps do not match ordered observations");
        }
        if candidate.raw_url != candidate.observations[0].raw_url
            || candidate.observed_range
                != cache_utils::parse_http_byte_range(
                    candidate.observations[0].raw_range.as_deref().unwrap_or(""),
                )
                .context("candidate first observation has an invalid range")?
        {
            bail!("candidate identity/range does not match its first observation");
        }
        if candidate.reason == DetectionReason::MissingCachedSlice {
            let observation = &candidate.observations[0];
            let ObservedByteRange::Inclusive { start, end } = candidate.observed_range else {
                bail!("missing-slice evidence must use an inclusive range");
            };
            let served_length = end
                .checked_sub(start)
                .and_then(|value| value.checked_add(1))
                .and_then(|value| i64::try_from(value).ok());
            if observation.http_status != 206
                || observation.bytes_served <= 0
                || served_length != Some(observation.bytes_served)
            {
                bail!("missing-slice proof is not one fully served ranged observation");
            }
        }

        observations.extend(candidate_observations);
    }

    observations.sort_by(|left, right| {
        (
            left.timestamp,
            &left.service,
            &left.raw_url,
            &left.client_ip,
            &left.method,
            left.http_status,
            left.bytes_served,
            &left.cache_status,
            &left.raw_range,
        )
            .cmp(&(
                right.timestamp,
                &right.service,
                &right.raw_url,
                &right.client_ip,
                &right.method,
                right.http_status,
                right.bytes_served,
                &right.cache_status,
                &right.raw_range,
            ))
    });
    observations.dedup();

    Ok(ValidatedHistoryEvidence {
        scan_id,
        mode: envelope.mode,
        threshold: envelope.threshold,
        datasource: envelope.datasource,
        candidate_count,
        observations,
    })
}

fn delete_exact_paths_with<F>(
    cache_dir: &Path,
    paths: &[PathBuf],
    progress_path: &Path,
    reporter: &ProgressReporter,
    mut remove_file: F,
) -> Result<ExactPathRemovalOutcome>
where
    F: FnMut(&Path) -> std::io::Result<()>,
{
    let mut outcome = ExactPathRemovalOutcome::default();
    let mut parent_dirs = HashSet::new();

    for (index, path) in paths.iter().enumerate() {
        if cancel::is_cancelled() {
            bail!("corruption removal cancelled before all exact paths were processed");
        }
        let percent = 10.0 + (index as f64 / paths.len().max(1) as f64) * 40.0;
        write_progress(
            progress_path,
            reporter,
            "removing_cache",
            "signalr.corruptionRemove.removingCacheFile",
            json!({ "fileIndex": index + 1, "totalFiles": paths.len() }),
            percent,
            index,
            paths.len(),
        )?;

        let metadata = match std::fs::symlink_metadata(path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                outcome.already_missing += 1;
                continue;
            }
            Err(error) => {
                return Err(error)
                    .with_context(|| format!("failed to inspect exact path {}", path.display()));
            }
        };
        cache_utils::safe_path_under_root(cache_dir, path)
            .with_context(|| format!("unsafe exact path {}", path.display()))?;
        if !metadata.is_file() {
            bail!("exact path is not a regular file: {}", path.display());
        }

        remove_file(path)
            .with_context(|| format!("failed to delete exact cache path {}", path.display()))?;
        outcome.deleted_files += 1;
        outcome.bytes_freed += metadata.len();
        if let Some(parent) = path.parent() {
            parent_dirs.insert(parent.to_path_buf());
        }
    }

    cache_utils::cleanup_empty_directories(cache_dir, parent_dirs);
    Ok(outcome)
}

fn delete_exact_paths(
    cache_dir: &Path,
    paths: &[PathBuf],
    progress_path: &Path,
    reporter: &ProgressReporter,
) -> Result<ExactPathRemovalOutcome> {
    delete_exact_paths_with(cache_dir, paths, progress_path, reporter, |path| {
        std::fs::remove_file(path)
    })
}

fn stored_log_url(raw_url: &str) -> String {
    if !raw_url.as_bytes().windows(2).any(|pair| pair == b"//") {
        return raw_url.to_string();
    }
    let mut normalized = String::with_capacity(raw_url.len());
    let mut previous_slash = false;
    for character in raw_url.chars() {
        if character == '/' {
            if !previous_slash {
                normalized.push(character);
            }
            previous_slash = true;
        } else {
            normalized.push(character);
            previous_slash = false;
        }
    }
    normalized
}

fn database_http_range(observation: &ExactLogObservation) -> &str {
    observation.raw_range.as_deref().unwrap_or("")
}

fn affected_download_delete_sql(placeholders: &str) -> String {
    format!(
        "DELETE FROM \"Downloads\" WHERE \"Id\" IN ({placeholders}) AND NOT \"IsActive\" AND NOT EXISTS (SELECT 1 FROM \"LogEntries\" WHERE \"LogEntries\".\"DownloadId\" = \"Downloads\".\"Id\")"
    )
}

fn affected_survivor_recompute_sql(placeholders: &str) -> String {
    format!(
        "UPDATE \"Downloads\" SET \"CacheHitBytes\" = COALESCE((SELECT SUM(\"BytesServed\") FROM \"LogEntries\" WHERE \"LogEntries\".\"DownloadId\" = \"Downloads\".\"Id\" AND \"CacheStatus\" = 'HIT'), 0), \"CacheMissBytes\" = COALESCE((SELECT SUM(\"BytesServed\") FROM \"LogEntries\" WHERE \"LogEntries\".\"DownloadId\" = \"Downloads\".\"Id\" AND \"CacheStatus\" IN ('MISS', 'UNKNOWN')), 0) WHERE \"Id\" IN ({placeholders})"
    )
}

async fn delete_database_observations(
    pool: &PgPool,
    datasource: &str,
    observations: &[ExactLogObservation],
) -> Result<(usize, usize)> {
    let mut transaction = pool
        .begin()
        .await
        .context("failed to begin exact corruption database cleanup")?;
    let mut affected_download_ids = HashSet::new();
    let mut log_entries_deleted = 0usize;

    for observation in observations {
        let rows = sqlx::query(EXACT_DB_DELETE_SQL)
            .bind(&observation.service)
            .bind(datasource)
            .bind(&observation.client_ip)
            .bind(observation.timestamp)
            .bind(&observation.method)
            .bind(stored_log_url(&observation.raw_url))
            .bind(observation.http_status)
            .bind(&observation.cache_status)
            // New no-range rows persist ""; historical nullable rows never match this predicate.
            .bind(database_http_range(observation))
            .bind(observation.bytes_served)
            .fetch_all(&mut *transaction)
            .await
            .context("failed to delete exact corruption LogEntries")?;
        log_entries_deleted += rows.len();
        for row in rows {
            if let Some(download_id) = row.try_get::<Option<i64>, _>("DownloadId")? {
                affected_download_ids.insert(download_id);
            }
        }
    }

    let mut downloads_deleted = 0usize;
    let download_ids: Vec<i64> = affected_download_ids.into_iter().collect();
    for chunk in download_ids.chunks(400) {
        let placeholders = (1..=chunk.len())
            .map(|index| format!("${index}"))
            .collect::<Vec<_>>()
            .join(", ");
        let delete_downloads = affected_download_delete_sql(&placeholders);
        let mut query = sqlx::query(&delete_downloads);
        for id in chunk {
            query = query.bind(*id);
        }
        downloads_deleted += query
            .execute(&mut *transaction)
            .await
            .context("failed to delete affected inactive zero-row Downloads")?
            .rows_affected() as usize;

        let update_downloads = affected_survivor_recompute_sql(&placeholders);
        let mut query = sqlx::query(&update_downloads);
        for id in chunk {
            query = query.bind(*id);
        }
        query
            .execute(&mut *transaction)
            .await
            .context("failed to recompute affected survivor byte totals")?;
    }

    transaction
        .commit()
        .await
        .context("failed to commit exact corruption database cleanup")?;
    Ok((downloads_deleted, log_entries_deleted))
}

fn seed_scan_progress(progress_path: Option<&Path>) {
    if let Some(path) = progress_path {
        let starting = ProgressData {
            status: "starting".to_string(),
            stage_key: "signalr.corruptionRemove.scanningFiles".to_string(),
            context: json!({}),
            percent_complete: 0.0,
            files_processed: 0,
            total_files: 0,
            timestamp: progress_utils::current_timestamp(),
        };
        if let Err(error) = progress_utils::write_progress_json(path, &starting) {
            eprintln!("Warning: failed to seed progress file: {error:#}");
        }
    }
}

fn generate_report(
    log_dir: &Path,
    cache_dir: &Path,
    timezone: chrono_tz::Tz,
    threshold: usize,
    lookback_days: u32,
    scan_started_utc: DateTime<Utc>,
    mode: DetectionMode,
    progress_path: Option<&Path>,
) -> Result<cache_corruption_detector::CorruptionReport> {
    CorruptionDetector::new(cache_dir, threshold, lookback_days, scan_started_utc)
        .generate_report_for_mode(mode, log_dir, "access.log", timezone, progress_path)
        .with_context(|| format!("failed to generate {mode:?} corruption report"))
}

async fn run_remove(
    log_dir: &Path,
    cache_dir: &Path,
    service: &str,
    progress_path: &Path,
    evidence_path: &Path,
    reporter: &ProgressReporter,
) -> Result<()> {
    write_progress(
        progress_path,
        reporter,
        "starting",
        "signalr.corruptionRemove.starting",
        json!({ "service": service }),
        0.0,
        0,
        0,
    )?;

    let evidence = load_and_validate_removal_evidence(evidence_path, cache_dir, service)?;
    eprintln!(
        "Validated scan {}: {} {:?} candidates for datasource '{}' at threshold {}",
        evidence.scan_id,
        evidence.candidates.len(),
        evidence.mode,
        evidence.datasource,
        evidence.threshold
    );

    // Preflight every non-filesystem dependency before the first unlink. A later mutation error
    // still retains persisted evidence, but avoid preventable partial work (bad log root/DB config).
    crate::log_discovery::discover_log_files(log_dir, "access.log")
        .context("failed to discover access logs before removal")?;
    let matcher = ExactLogMatcher::new(evidence.observations.clone());
    let prefilter = matcher.prefilter()?;
    let pool = db::create_pool()
        .await
        .context("failed to connect to the database before corruption removal")?;

    write_progress(
        progress_path,
        reporter,
        "removing_cache",
        "signalr.corruptionRemove.removingCacheFiles",
        json!({ "totalFiles": evidence.exact_paths.len() }),
        10.0,
        0,
        evidence.exact_paths.len(),
    )?;
    let cache_outcome =
        delete_exact_paths(cache_dir, &evidence.exact_paths, progress_path, reporter)?;

    write_progress(
        progress_path,
        reporter,
        "filtering",
        "signalr.corruptionRemove.filteringLogs",
        json!({}),
        55.0,
        0,
        0,
    )?;
    let filter_callback = |files_done: usize, total: usize| {
        let percent = 55.0 + (files_done as f64 / total.max(1) as f64) * 25.0;
        let _ = write_progress(
            progress_path,
            reporter,
            "filtering",
            "signalr.corruptionRemove.filteringFile",
            json!({ "fileIndex": files_done, "totalFiles": total }),
            percent,
            files_done,
            total,
        );
    };
    let log_outcome = log_purge::rewrite_matching_log_entries_strict(
        log_dir,
        "exact corruption evidence",
        &prefilter,
        |entry| matcher.matches(entry),
        Some(&filter_callback),
    )?;
    if log_outcome.permission_errors > 0 || log_outcome.other_errors > 0 {
        bail!(
            "access-log cleanup was partial ({} permission errors, {} other errors); database evidence was retained",
            log_outcome.permission_errors,
            log_outcome.other_errors
        );
    }

    write_progress(
        progress_path,
        reporter,
        "removing_database",
        "signalr.corruptionRemove.deletingDb",
        json!({}),
        85.0,
        0,
        0,
    )?;
    let (downloads_deleted, log_entries_deleted) =
        delete_database_observations(&pool, &evidence.datasource, &evidence.observations).await?;

    write_progress(
        progress_path,
        reporter,
        "completed",
        "signalr.corruptionRemove.complete",
        json!({
            "count": evidence.candidates.len(),
            "service": service,
            "files": cache_outcome.deleted_files,
            "alreadyMissing": cache_outcome.already_missing,
            "bytesFreed": cache_outcome.bytes_freed,
            "logLines": log_outcome.lines_removed,
            "downloads": downloads_deleted,
            "logEntries": log_entries_deleted
        }),
        100.0,
        evidence.exact_paths.len(),
        evidence.exact_paths.len(),
    )?;
    Ok(())
}

fn complete_history_cancellation(
    progress_path: &Path,
    reporter: &ProgressReporter,
    scope: &str,
    context: serde_json::Value,
) -> Result<()> {
    let mut terminal = json!({ "scope": scope });
    if let (Some(target), Some(source)) = (terminal.as_object_mut(), context.as_object()) {
        target.extend(source.clone());
    }
    write_progress(
        progress_path,
        reporter,
        "cancelled",
        "signalr.historicalEvidencePurge.cancelled",
        terminal,
        100.0,
        0,
        0,
    )
}

fn require_complete_history_log_rewrite(outcome: &log_purge::LogRewriteOutcome) -> Result<()> {
    if outcome.permission_errors > 0 || outcome.other_errors > 0 {
        bail!(
            "access-log cleanup was partial ({} permission errors, {} other errors); database evidence was retained",
            outcome.permission_errors,
            outcome.other_errors
        );
    }
    Ok(())
}

async fn run_purge_history(
    log_dir: &Path,
    scope: &str,
    progress_path: &Path,
    evidence_path: &Path,
    reporter: &ProgressReporter,
) -> Result<()> {
    write_progress(
        progress_path,
        reporter,
        "starting",
        "signalr.historicalEvidencePurge.starting",
        json!({ "scope": scope }),
        0.0,
        0,
        0,
    )?;
    if cancel::is_cancelled() {
        return complete_history_cancellation(progress_path, reporter, scope, json!({}));
    }

    let evidence = load_and_validate_history_evidence(evidence_path, scope)?;
    eprintln!(
        "Validated scan {}: {} review-only {:?} candidates for datasource '{}' at threshold {}",
        evidence.scan_id,
        evidence.candidate_count,
        evidence.mode,
        evidence.datasource,
        evidence.threshold
    );

    // Preflight log discovery, exact matcher construction, and the database connection before the
    // first rewrite. The history command has no cache root and cannot reach cache deletion code.
    crate::log_discovery::discover_log_files(log_dir, "access.log")
        .context("failed to discover access logs before historical evidence purge")?;
    let matcher = ExactLogMatcher::new(evidence.observations.clone());
    let prefilter = matcher.prefilter()?;
    let pool = db::create_pool()
        .await
        .context("failed to connect to the database before historical evidence purge")?;
    if cancel::is_cancelled() {
        return complete_history_cancellation(progress_path, reporter, scope, json!({}));
    }

    write_progress(
        progress_path,
        reporter,
        "filtering",
        "signalr.historicalEvidencePurge.filteringLogs",
        json!({ "count": evidence.candidate_count, "scope": scope }),
        15.0,
        0,
        0,
    )?;
    let filter_callback = |files_done: usize, total: usize| {
        let percent = 15.0 + (files_done as f64 / total.max(1) as f64) * 60.0;
        if let Err(error) = write_progress(
            progress_path,
            reporter,
            "filtering",
            "signalr.historicalEvidencePurge.filteringFile",
            json!({ "fileIndex": files_done, "totalFiles": total, "scope": scope }),
            percent,
            files_done,
            total,
        ) {
            eprintln!("Warning: failed to publish historical evidence purge progress: {error:#}");
        }
    };
    let log_outcome = log_purge::rewrite_matching_log_entries_strict_cancellable(
        log_dir,
        "exact historical evidence",
        &prefilter,
        |entry| matcher.matches(entry),
        Some(&filter_callback),
        cancel::is_cancelled,
    )?;
    require_complete_history_log_rewrite(&log_outcome)?;
    if log_outcome.cancelled || cancel::is_cancelled() {
        return complete_history_cancellation(
            progress_path,
            reporter,
            scope,
            json!({ "logLines": log_outcome.lines_removed }),
        );
    }

    write_progress(
        progress_path,
        reporter,
        "removing_database",
        "signalr.historicalEvidencePurge.deletingDb",
        json!({ "count": evidence.candidate_count, "scope": scope }),
        80.0,
        0,
        0,
    )?;
    if cancel::is_cancelled() {
        return complete_history_cancellation(
            progress_path,
            reporter,
            scope,
            json!({ "logLines": log_outcome.lines_removed }),
        );
    }
    let (downloads_deleted, log_entries_deleted) =
        delete_database_observations(&pool, &evidence.datasource, &evidence.observations).await?;
    if cancel::is_cancelled() {
        // The transaction completed atomically. Retaining the server-side candidate makes a retry
        // safe: exact log/DB deletion is idempotent and the next run can cross the success boundary.
        return complete_history_cancellation(
            progress_path,
            reporter,
            scope,
            json!({
                "logLines": log_outcome.lines_removed,
                "downloads": downloads_deleted,
                "logEntries": log_entries_deleted
            }),
        );
    }

    write_progress(
        progress_path,
        reporter,
        "completed",
        "signalr.historicalEvidencePurge.complete",
        json!({
            "count": evidence.candidate_count,
            "scope": scope,
            "files": 0,
            "alreadyMissing": 0,
            "bytesFreed": 0,
            "logLines": log_outcome.lines_removed,
            "downloads": downloads_deleted,
            "logEntries": log_entries_deleted
        }),
        100.0,
        0,
        0,
    )?;
    Ok(())
}

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();

    match args.command {
        Commands::Detect {
            log_dir,
            cache_dir,
            output_json,
            timezone,
            threshold,
            lookback_days,
            scan_started_utc,
            mode,
            no_cache_check,
            detect_redownloads,
            progress_json,
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result: Result<()> = (|| {
                let mode = resolve_scan_mode(mode.as_deref(), no_cache_check, detect_redownloads)?;
                let timezone = parse_timezone(&timezone)?;
                let scan_started_utc = parse_scan_started_utc(scan_started_utc.as_deref())?;
                let progress_path = progress_json.as_deref().map(Path::new);
                seed_scan_progress(progress_path);
                reporter.emit_started("signalr.corruptionRemove.scanningFiles", json!({}));
                let report = generate_report(
                    Path::new(&log_dir),
                    Path::new(&cache_dir),
                    timezone,
                    threshold,
                    lookback_days,
                    scan_started_utc,
                    mode,
                    progress_path,
                )?;
                write_pretty_json(Path::new(&output_json), &report)?;
                reporter.emit_complete(
                    "signalr.corruptionRemove.complete",
                    json!({
                        "totalCorrupted": report.total,
                        "serviceCounts": report.service_counts,
                        "removableTotal": report.removable_total,
                        "reviewOnlyTotal": report.review_only_total,
                        "removableServiceCounts": report.removable_service_counts,
                        "reviewOnlyServiceCounts": report.review_only_service_counts
                    }),
                );
                Ok(())
            })();
            progress_events::finish_or_exit(
                &reporter,
                "signalr.corruptionRemove.error.fatal",
                result,
            );
        }
        Commands::Summary {
            log_dir,
            cache_dir,
            progress_json,
            timezone,
            threshold,
            lookback_days,
            scan_started_utc,
            mode,
            no_cache_check,
            detect_redownloads,
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result: Result<()> = (|| {
                let mode = resolve_scan_mode(mode.as_deref(), no_cache_check, detect_redownloads)?;
                let timezone = parse_timezone(&timezone)?;
                let scan_started_utc = parse_scan_started_utc(scan_started_utc.as_deref())?;
                let progress_path = (progress_json != "none" && !progress_json.is_empty())
                    .then(|| Path::new(&progress_json));
                seed_scan_progress(progress_path);
                reporter.emit_started("signalr.corruptionRemove.scanningFiles", json!({}));
                let report = generate_report(
                    Path::new(&log_dir),
                    Path::new(&cache_dir),
                    timezone,
                    threshold,
                    lookback_days,
                    scan_started_utc,
                    mode,
                    progress_path,
                )?;
                reporter.emit_complete(
                    "signalr.corruptionRemove.complete",
                    json!({
                        "totalCorrupted": report.total,
                        "serviceCounts": report.service_counts,
                        "removableTotal": report.removable_total,
                        "reviewOnlyTotal": report.review_only_total,
                        "removableServiceCounts": report.removable_service_counts,
                        "reviewOnlyServiceCounts": report.review_only_service_counts
                    }),
                );
                println!("{}", serde_json::to_string(&report)?);
                Ok(())
            })();
            progress_events::finish_or_exit(
                &reporter,
                "signalr.corruptionRemove.error.fatal",
                result,
            );
        }
        Commands::Remove {
            log_dir,
            cache_dir,
            service,
            progress_json,
            evidence_file,
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result = run_remove(
                Path::new(&log_dir),
                Path::new(&cache_dir),
                &service,
                Path::new(&progress_json),
                Path::new(&evidence_file),
                &reporter,
            )
            .await;
            progress_events::finish_or_exit(
                &reporter,
                "signalr.corruptionRemove.error.fatal",
                result,
            );
        }
        Commands::PurgeHistory {
            log_dir,
            scope,
            progress_json,
            evidence_file,
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result = run_purge_history(
                Path::new(&log_dir),
                &scope,
                Path::new(&progress_json),
                Path::new(&evidence_file),
                &reporter,
            )
            .await;
            progress_events::finish_or_exit(
                &reporter,
                "signalr.historicalEvidencePurge.error.fatal",
                result,
            );
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use cache_corruption_detector::{CandidateObservation, SupportingSiblingEvidence};
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[derive(Clone, Copy)]
    enum HistoryFixtureShape {
        LogsOnly,
        MissingSlice,
        RedownloadMissing,
    }

    fn make_candidate(
        cache_dir: &Path,
        datasource: &str,
        mode: DetectionMode,
        threshold: usize,
    ) -> RemovalCandidate {
        let raw_url = "/middle.bin";
        let observed_range = ObservedByteRange::Inclusive {
            start: 2_097_200,
            end: 2_097_300,
        };
        let cache_slice = CacheSliceKind::Ranged {
            start: 2_097_152,
            end: 3_145_727,
        };
        let exact_path =
            cache_utils::calculate_cache_path(cache_dir, "steam", raw_url, 2_097_152, 3_145_727);
        let cache_status = if mode == DetectionMode::Redownload {
            "HIT"
        } else {
            "MISS"
        };
        let observations: Vec<CandidateObservation> = (0..threshold)
            .map(|second| CandidateObservation {
                timestamp: format!("2024-01-01T00:00:{second:02}Z"),
                client_ip: "192.0.2.10".to_string(),
                raw_url: raw_url.to_string(),
                method: "GET".to_string(),
                http_status: 206,
                bytes_served: 1024,
                cache_status: cache_status.to_string(),
                raw_range: Some("bytes=2097200-2097300".to_string()),
            })
            .collect();
        RemovalCandidate {
            datasource: datasource.to_string(),
            candidate: CorruptionCandidate {
                candidate_id: format!("{datasource}:candidate"),
                mode,
                threshold,
                service: "steam".to_string(),
                raw_url: raw_url.to_string(),
                normalized_uri: raw_url.to_string(),
                observed_range,
                cache_slice,
                exact_paths: vec![exact_path.display().to_string()],
                evidence_count: threshold,
                first_seen: "2024-01-01T00:00:00Z".to_string(),
                last_seen: format!("2024-01-01T00:00:{:02}Z", threshold - 1),
                retry_client: (mode == DetectionMode::Redownload).then(|| "192.0.2.10".to_string()),
                reason: if mode == DetectionMode::Redownload {
                    DetectionReason::SameClientHitRetryBurst
                } else {
                    DetectionReason::RepeatedMissBurst
                },
                validation_state: ValidationState::ExactPathPresent,
                removal_allowed: mode != DetectionMode::LogsOnly,
                supporting_sibling: None,
                observations,
            },
        }
    }

    fn write_evidence(
        directory: &Path,
        datasource: &str,
        mode: DetectionMode,
        threshold: usize,
        candidates: Vec<RemovalCandidate>,
    ) -> PathBuf {
        let path = directory.join("evidence.json");
        let envelope = RemovalEvidenceEnvelope {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            scan_id: Uuid::new_v4().to_string(),
            mode,
            threshold,
            datasource: datasource.to_string(),
            candidates,
        };
        std::fs::write(&path, serde_json::to_vec(&envelope).unwrap()).unwrap();
        path
    }

    fn make_history_candidate(
        cache_dir: &Path,
        datasource: &str,
        threshold: usize,
        shape: HistoryFixtureShape,
    ) -> RemovalCandidate {
        let mode = match shape {
            HistoryFixtureShape::LogsOnly => DetectionMode::LogsOnly,
            HistoryFixtureShape::MissingSlice => DetectionMode::CacheAndLogs,
            HistoryFixtureShape::RedownloadMissing => DetectionMode::Redownload,
        };
        let mut wrapped = make_candidate(cache_dir, datasource, mode, threshold);
        wrapped.candidate.removal_allowed = false;
        match shape {
            HistoryFixtureShape::LogsOnly => {
                wrapped.candidate.validation_state = ValidationState::LogSuspect;
            }
            HistoryFixtureShape::RedownloadMissing => {
                wrapped.candidate.validation_state = ValidationState::ExactPathMissing;
            }
            HistoryFixtureShape::MissingSlice => {
                wrapped.candidate.reason = DetectionReason::MissingCachedSlice;
                wrapped.candidate.validation_state = ValidationState::ExactPathMissing;
                wrapped.candidate.evidence_count = 1;
                wrapped.candidate.first_seen = "2024-01-01T00:00:00Z".to_string();
                wrapped.candidate.last_seen = "2024-01-01T00:00:00Z".to_string();
                wrapped.candidate.retry_client = None;
                wrapped.candidate.observations.truncate(1);
                wrapped.candidate.observations[0].cache_status = "HIT".to_string();
                wrapped.candidate.observations[0].bytes_served = 101;
                wrapped.candidate.supporting_sibling = Some(SupportingSiblingEvidence {
                    cache_slice: CacheSliceKind::Ranged {
                        start: 3_145_728,
                        end: 4_194_303,
                    },
                    exact_path: cache_utils::calculate_cache_path(
                        cache_dir,
                        "steam",
                        "/middle.bin",
                        3_145_728,
                        4_194_303,
                    )
                    .display()
                    .to_string(),
                });
            }
        }
        wrapped
    }

    fn observation_log_line(second: usize, raw_url: &str, raw_range: &str) -> String {
        format!(
            "[steam] 192.0.2.10 / - - - [01/Jan/2024:00:00:{second:02} +0000] \"GET {raw_url} HTTP/1.1\" 206 1024 \"-\" \"Test\" \"MISS\" \"cdn.test\" \"{raw_range}\""
        )
    }

    #[test]
    fn canonical_cli_mode_and_evidence_file_match_csharp_calls() {
        let summary = Args::try_parse_from([
            "cache_corruption",
            "summary",
            "logs",
            "cache",
            "progress.json",
            "UTC",
            "5",
            "--mode",
            "redownload",
            "--lookback-days",
            "30",
            "--scan-started-utc",
            "2024-01-31T00:00:00Z",
        ])
        .expect("summary CLI");
        match summary.command {
            Commands::Summary {
                lookback_days,
                scan_started_utc,
                ..
            } => {
                assert_eq!(lookback_days, 30);
                assert_eq!(scan_started_utc.as_deref(), Some("2024-01-31T00:00:00Z"));
            }
            _ => panic!("expected summary"),
        }
        let detect = Args::try_parse_from([
            "cache_corruption",
            "detect",
            "logs",
            "cache",
            "report.json",
            "UTC",
            "3",
            "--lookback-days",
            "30",
            "--scan-started-utc",
            "2024-01-31T00:00:00Z",
        ])
        .expect("detect CLI");
        match detect.command {
            Commands::Detect {
                lookback_days,
                scan_started_utc,
                ..
            } => {
                assert_eq!(lookback_days, 30);
                assert_eq!(scan_started_utc.as_deref(), Some("2024-01-31T00:00:00Z"));
            }
            _ => panic!("expected detect"),
        }
        let removal = Args::try_parse_from([
            "cache_corruption",
            "remove",
            "logs",
            "cache",
            "steam",
            "progress.json",
            "--evidence-file",
            "evidence.json",
            "--progress",
        ]);
        assert!(removal.is_ok());

        let history = Args::try_parse_from([
            "cache_corruption",
            "purge-history",
            "logs",
            "all",
            "progress.json",
            "--evidence-file",
            "evidence.json",
            "--progress",
        ])
        .expect("history-purge CLI");
        match history.command {
            Commands::PurgeHistory {
                log_dir,
                scope,
                progress_json,
                evidence_file,
                progress,
            } => {
                assert_eq!(log_dir, "logs");
                assert_eq!(scope, "all");
                assert_eq!(progress_json, "progress.json");
                assert_eq!(evidence_file, "evidence.json");
                assert!(progress);
            }
            _ => panic!("expected purge-history"),
        }
        assert!(Args::try_parse_from([
            "cache_corruption",
            "purge-history",
            "logs",
            "unexpected-cache-root",
            "steam",
            "progress.json",
            "--evidence-file",
            "evidence.json",
        ])
        .is_err());
    }

    #[test]
    fn history_validation_accepts_only_the_three_closed_review_shapes() {
        for (shape, mode) in [
            (HistoryFixtureShape::LogsOnly, DetectionMode::LogsOnly),
            (
                HistoryFixtureShape::MissingSlice,
                DetectionMode::CacheAndLogs,
            ),
            (
                HistoryFixtureShape::RedownloadMissing,
                DetectionMode::Redownload,
            ),
        ] {
            let fixture = tempfile::tempdir().unwrap();
            let candidate = make_history_candidate(fixture.path(), "default", 10, shape);
            let evidence_path =
                write_evidence(fixture.path(), "default", mode, 10, vec![candidate]);
            let evidence = load_and_validate_history_evidence(&evidence_path, "steam").unwrap();
            assert_eq!(evidence.mode, mode);
            assert_eq!(evidence.candidate_count, 1);
            let expected_observations = if matches!(shape, HistoryFixtureShape::MissingSlice) {
                1
            } else {
                10
            };
            assert_eq!(evidence.observations.len(), expected_observations);
        }
    }

    #[test]
    fn history_validation_rejects_removable_mixed_empty_and_wrong_scope_evidence() {
        let fixture = tempfile::tempdir().unwrap();
        let mut candidate =
            make_history_candidate(fixture.path(), "default", 3, HistoryFixtureShape::LogsOnly);
        candidate.candidate.removal_allowed = true;
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::LogsOnly,
            3,
            vec![candidate],
        );
        assert!(format!(
            "{:#}",
            load_and_validate_history_evidence(&path, "steam").unwrap_err()
        )
        .contains("review-only"));

        let mut mixed = make_history_candidate(
            fixture.path(),
            "default",
            3,
            HistoryFixtureShape::RedownloadMissing,
        );
        mixed.candidate.validation_state = ValidationState::LogSuspect;
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::Redownload,
            3,
            vec![mixed],
        );
        assert!(load_and_validate_history_evidence(&path, "steam").is_err());

        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::LogsOnly,
            3,
            Vec::new(),
        );
        assert!(load_and_validate_history_evidence(&path, "steam").is_err());

        let candidate =
            make_history_candidate(fixture.path(), "default", 3, HistoryFixtureShape::LogsOnly);
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::LogsOnly,
            3,
            vec![candidate],
        );
        assert!(load_and_validate_history_evidence(&path, "epic").is_err());
        assert!(load_and_validate_history_evidence(&path, "").is_err());
        assert!(load_and_validate_history_evidence(&path, "all").is_ok());
    }

    #[test]
    fn missing_slice_history_count_is_one_independent_of_scan_threshold() {
        for threshold in [3, 5, 10] {
            let fixture = tempfile::tempdir().unwrap();
            let candidate = make_history_candidate(
                fixture.path(),
                "default",
                threshold,
                HistoryFixtureShape::MissingSlice,
            );
            let path = write_evidence(
                fixture.path(),
                "default",
                DetectionMode::CacheAndLogs,
                threshold,
                vec![candidate],
            );
            let evidence = load_and_validate_history_evidence(&path, "steam").unwrap();
            assert_eq!(evidence.observations.len(), 1);
        }
    }

    #[test]
    fn history_validation_and_log_rewrite_never_mutate_cache_sentinels() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache-sentinel");
        let log_dir = fixture.path().join("logs");
        std::fs::create_dir_all(&cache_dir).unwrap();
        std::fs::create_dir_all(&log_dir).unwrap();
        let sentinel = cache_dir.join("nested").join("must-remain.bin");
        std::fs::create_dir_all(sentinel.parent().unwrap()).unwrap();
        std::fs::write(&sentinel, b"cache bytes must remain unchanged").unwrap();

        let candidate =
            make_history_candidate(&cache_dir, "default", 3, HistoryFixtureShape::LogsOnly);
        std::fs::write(
            log_dir.join("access.log"),
            format!(
                "{}\n",
                observation_log_line(0, "/middle.bin", "bytes=2097200-2097300")
            ),
        )
        .unwrap();
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::LogsOnly,
            3,
            vec![candidate],
        );
        let evidence = load_and_validate_history_evidence(&path, "steam").unwrap();
        let matcher = ExactLogMatcher::new(evidence.observations);
        let prefilter = matcher.prefilter().unwrap();
        log_purge::rewrite_matching_log_entries_strict(
            &log_dir,
            "history cache non-mutation proof",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
        )
        .unwrap();

        assert_eq!(
            std::fs::read(&sentinel).unwrap(),
            b"cache bytes must remain unchanged"
        );
        assert!(cache_dir.join("nested").is_dir());

        let source = include_str!("cache_corruption.rs");
        let history_body = source
            .split("async fn run_purge_history")
            .nth(1)
            .unwrap()
            .split("#[tokio::main]")
            .next()
            .unwrap();
        for forbidden in [
            "delete_exact_paths(",
            "delete_exact_paths_with(",
            "remove_file(",
            "cleanup_empty_directories(",
        ] {
            assert!(!history_body.contains(forbidden), "found {forbidden}");
        }
    }

    #[test]
    fn lookback_days_cli_accepts_bounds_defaults_and_rejects_out_of_range() {
        for value in ["1", "30", "365"] {
            let detect = Args::try_parse_from([
                "cache_corruption",
                "detect",
                "logs",
                "cache",
                "report.json",
                "UTC",
                "3",
                "--lookback-days",
                value,
            ])
            .expect("valid detect lookback");
            let Commands::Detect { lookback_days, .. } = detect.command else {
                panic!("expected detect")
            };
            assert_eq!(lookback_days.to_string(), value);

            let summary = Args::try_parse_from([
                "cache_corruption",
                "summary",
                "logs",
                "cache",
                "none",
                "UTC",
                "3",
                "--lookback-days",
                value,
            ])
            .expect("valid summary lookback");
            let Commands::Summary { lookback_days, .. } = summary.command else {
                panic!("expected summary")
            };
            assert_eq!(lookback_days.to_string(), value);
        }

        for value in ["0", "366"] {
            assert!(Args::try_parse_from([
                "cache_corruption",
                "detect",
                "logs",
                "cache",
                "report.json",
                "UTC",
                "3",
                "--lookback-days",
                value,
            ])
            .is_err());
            assert!(Args::try_parse_from([
                "cache_corruption",
                "summary",
                "logs",
                "cache",
                "none",
                "UTC",
                "3",
                "--lookback-days",
                value,
            ])
            .is_err());
        }

        let detect_default =
            Args::try_parse_from(["cache_corruption", "detect", "logs", "cache", "report.json"])
                .expect("default detect");
        let Commands::Detect { lookback_days, .. } = detect_default.command else {
            panic!("expected detect")
        };
        assert_eq!(lookback_days, DEFAULT_LOOKBACK_DAYS);

        let summary_default =
            Args::try_parse_from(["cache_corruption", "summary", "logs", "cache"])
                .expect("default summary");
        let Commands::Summary { lookback_days, .. } = summary_default.command else {
            panic!("expected summary")
        };
        assert_eq!(lookback_days, DEFAULT_LOOKBACK_DAYS);
    }

    #[test]
    fn scan_started_utc_parser_requires_utc_and_preserves_the_instant() {
        let parsed = parse_scan_started_utc(Some("2024-01-31T00:00:00Z")).expect("UTC start");
        assert_eq!(
            parsed.to_rfc3339_opts(chrono::SecondsFormat::Secs, true),
            "2024-01-31T00:00:00Z"
        );
        assert!(parse_scan_started_utc(Some("2024-01-31T01:00:00+01:00")).is_err());
        assert!(parse_scan_started_utc(Some("not-a-time")).is_err());
    }

    #[test]
    fn canonical_mode_rejects_inconsistent_legacy_aliases() {
        assert_eq!(
            resolve_scan_mode(Some("redownload"), false, false).unwrap(),
            DetectionMode::Redownload
        );
        assert_eq!(
            resolve_scan_mode(Some("miss_count"), false, false).unwrap(),
            DetectionMode::CacheAndLogs
        );
        assert!(resolve_scan_mode(Some("logs_only"), false, true).is_err());
        assert!(resolve_scan_mode(None, true, true).is_err());
    }

    #[test]
    fn logs_only_and_non_removable_evidence_are_rejected() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let mut logs = make_candidate(&cache_dir, "default", DetectionMode::LogsOnly, 3);
        logs.candidate.validation_state = ValidationState::LogSuspect;
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::LogsOnly,
            3,
            vec![logs],
        );
        assert!(load_and_validate_removal_evidence(&path, &cache_dir, "steam").is_err());

        let mut candidate = make_candidate(&cache_dir, "default", DetectionMode::CacheAndLogs, 3);
        candidate.candidate.removal_allowed = false;
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate],
        );
        assert!(load_and_validate_removal_evidence(&path, &cache_dir, "steam").is_err());
    }

    #[test]
    fn forged_missing_cached_slice_is_rejected_before_removal() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let mut base = make_candidate(&cache_dir, "default", DetectionMode::CacheAndLogs, 3);
        base.candidate.reason = DetectionReason::MissingCachedSlice;
        base.candidate.supporting_sibling = Some(SupportingSiblingEvidence {
            cache_slice: CacheSliceKind::Ranged {
                start: 3_145_728,
                end: 4_194_303,
            },
            exact_path: cache_utils::calculate_cache_path(
                &cache_dir,
                "steam",
                "/middle.bin",
                3_145_728,
                4_194_303,
            )
            .display()
            .to_string(),
        });
        let exact_path = PathBuf::from(&base.candidate.exact_paths[0]);
        std::fs::create_dir_all(exact_path.parent().unwrap()).unwrap();
        std::fs::write(&exact_path, b"must remain").unwrap();

        for (removal_allowed, validation_state) in [
            (true, ValidationState::ExactPathPresent),
            (true, ValidationState::ExactPathMissing),
            (false, ValidationState::ExactPathPresent),
        ] {
            let mut candidate = base.clone();
            candidate.candidate.removal_allowed = removal_allowed;
            candidate.candidate.validation_state = validation_state;
            let evidence_path = write_evidence(
                fixture.path(),
                "default",
                DetectionMode::CacheAndLogs,
                3,
                vec![candidate],
            );
            let error = load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam")
                .expect_err("missing slice must never be removable");
            assert!(format!("{error:#}").contains("review-only"));
            assert_eq!(std::fs::read(&exact_path).unwrap(), b"must remain");
        }
    }

    #[test]
    fn missing_or_empty_legacy_observation_raw_url_fails_closed() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let candidate = make_candidate(&cache_dir, "default", DetectionMode::CacheAndLogs, 3);
        let exact_path = PathBuf::from(&candidate.candidate.exact_paths[0]);
        std::fs::create_dir_all(exact_path.parent().unwrap()).unwrap();
        std::fs::write(&exact_path, b"must remain").unwrap();
        let evidence_path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate],
        );

        let mut evidence_json: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&evidence_path).unwrap()).unwrap();
        evidence_json["candidates"][0]["observations"][0]
            .as_object_mut()
            .unwrap()
            .remove("raw_url");
        std::fs::write(&evidence_path, serde_json::to_vec(&evidence_json).unwrap()).unwrap();
        let missing =
            load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam").unwrap_err();
        assert!(format!("{missing:#}").contains("observation raw_url is missing"));
        assert!(exact_path.exists());

        evidence_json["candidates"][0]["observations"][0]["raw_url"] = json!("");
        std::fs::write(&evidence_path, serde_json::to_vec(&evidence_json).unwrap()).unwrap();
        let empty =
            load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam").unwrap_err();
        assert!(format!("{empty:#}").contains("observation raw_url is missing"));
        assert!(exact_path.exists());
    }

    #[test]
    fn per_observation_raw_urls_scope_exact_log_and_database_cleanup() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        let log_dir = fixture.path().join("logs");
        std::fs::create_dir_all(&cache_dir).unwrap();
        std::fs::create_dir_all(&log_dir).unwrap();

        let first_url = "/content//file.bin?token=one";
        let second_url = "/content/file.bin?token=two";
        let unrelated_url = "/content/file.bin?token=three";
        let evidence_range = "bytes=2097200-2097300";
        let unrelated_range = "bytes=2097301-2097400";
        let mut candidate = make_candidate(&cache_dir, "default", DetectionMode::CacheAndLogs, 3);
        candidate.candidate.raw_url = first_url.to_string();
        candidate.candidate.normalized_uri = "/content/file.bin".to_string();
        candidate.candidate.exact_paths = vec![cache_utils::calculate_cache_path(
            &cache_dir, "steam", first_url, 2_097_152, 3_145_727,
        )
        .display()
        .to_string()];
        for (observation, raw_url) in candidate
            .candidate
            .observations
            .iter_mut()
            .zip([first_url, second_url, first_url])
        {
            observation.raw_url = raw_url.to_string();
        }
        let exact_path = PathBuf::from(&candidate.candidate.exact_paths[0]);
        std::fs::create_dir_all(exact_path.parent().unwrap()).unwrap();
        std::fs::write(&exact_path, b"exact slice").unwrap();

        let target_lines = [
            observation_log_line(0, first_url, evidence_range),
            observation_log_line(1, second_url, evidence_range),
            observation_log_line(2, first_url, evidence_range),
        ];
        let unrelated_line = observation_log_line(3, unrelated_url, unrelated_range);
        std::fs::write(
            log_dir.join("access.log"),
            format!(
                "{}\n{}\n{}\n{}\n",
                target_lines[0], target_lines[1], target_lines[2], unrelated_line
            ),
        )
        .unwrap();
        let evidence_path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate],
        );
        let evidence =
            load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam").unwrap();
        assert_eq!(
            evidence
                .observations
                .iter()
                .map(|observation| observation.raw_url.as_str())
                .collect::<Vec<_>>(),
            vec![first_url, second_url, first_url]
        );

        let matcher = ExactLogMatcher::new(evidence.observations.clone());
        let prefilter = matcher.prefilter().unwrap();
        let outcome = log_purge::rewrite_matching_log_entries_strict(
            &log_dir,
            "per-observation raw URL evidence",
            &prefilter,
            |entry| matcher.matches(entry),
            None,
        )
        .unwrap();
        assert_eq!(outcome.lines_removed, 3);
        assert_eq!(
            std::fs::read_to_string(log_dir.join("access.log")).unwrap(),
            format!("{unrelated_line}\n")
        );

        // These are the exact URL values bound to EXACT_DB_DELETE_SQL after parser-compatible
        // slash normalization. The unreviewed spelling/range has no deletion key.
        let database_urls: HashSet<String> = evidence
            .observations
            .iter()
            .map(|observation| stored_log_url(&observation.raw_url))
            .collect();
        assert_eq!(database_urls.len(), 2);
        assert!(database_urls.contains("/content/file.bin?token=one"));
        assert!(database_urls.contains(second_url));
        assert!(!database_urls.contains(unrelated_url));
    }

    #[test]
    fn evidence_rejects_datasource_threshold_and_root_mismatches() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_one = fixture.path().join("cache-one");
        let cache_two = fixture.path().join("cache-two");
        std::fs::create_dir_all(&cache_one).unwrap();
        std::fs::create_dir_all(&cache_two).unwrap();
        let mut candidate = make_candidate(&cache_one, "wrong", DetectionMode::CacheAndLogs, 3);
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate.clone()],
        );
        assert!(load_and_validate_removal_evidence(&path, &cache_one, "steam").is_err());

        candidate.datasource = "default".to_string();
        candidate.candidate.threshold = 5;
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate.clone()],
        );
        assert!(load_and_validate_removal_evidence(&path, &cache_one, "steam").is_err());

        candidate.candidate.threshold = 3;
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate],
        );
        assert!(load_and_validate_removal_evidence(&path, &cache_two, "steam").is_err());
    }

    #[test]
    fn evidence_rejects_a_handcrafted_root_escape_without_touching_it() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let outside = fixture.path().join("outside-cache-file");
        std::fs::write(&outside, b"must remain").unwrap();

        let mut candidate = make_candidate(&cache_dir, "default", DetectionMode::CacheAndLogs, 3);
        candidate.candidate.exact_paths = vec![outside.display().to_string()];
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate],
        );

        assert!(load_and_validate_removal_evidence(&path, &cache_dir, "steam").is_err());
        assert_eq!(std::fs::read(outside).unwrap(), b"must remain");
    }

    #[test]
    fn both_destructive_modes_require_and_accept_exact_path_present_evidence() {
        for mode in [DetectionMode::CacheAndLogs, DetectionMode::Redownload] {
            let fixture = tempfile::tempdir().unwrap();
            let cache_dir = fixture.path().join("cache");
            std::fs::create_dir_all(&cache_dir).unwrap();
            let candidate = make_candidate(&cache_dir, "default", mode, 3);
            let exact_path = PathBuf::from(&candidate.candidate.exact_paths[0]);
            std::fs::create_dir_all(exact_path.parent().unwrap()).unwrap();
            std::fs::write(&exact_path, b"exact slice").unwrap();
            let path = write_evidence(fixture.path(), "default", mode, 3, vec![candidate]);

            let evidence = load_and_validate_removal_evidence(&path, &cache_dir, "steam").unwrap();
            assert_eq!(evidence.mode, mode);
            assert_eq!(evidence.exact_paths, vec![exact_path]);
        }
    }

    #[test]
    fn exact_mid_object_delete_leaves_zero_sibling_and_unrelated_paths() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let candidate = make_candidate(&cache_dir, "default", DetectionMode::CacheAndLogs, 3);
        let exact = PathBuf::from(&candidate.candidate.exact_paths[0]);
        let zero =
            cache_utils::calculate_cache_path(&cache_dir, "steam", "/middle.bin", 0, 1_048_575);
        let sibling = cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/middle.bin",
            3_145_728,
            4_194_303,
        );
        let unrelated = cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/other.bin",
            2_097_152,
            3_145_727,
        );
        for path in [&exact, &zero, &sibling, &unrelated] {
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, b"slice").unwrap();
        }
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate],
        );
        let evidence = load_and_validate_removal_evidence(&path, &cache_dir, "steam").unwrap();
        let progress = fixture.path().join("progress.json");
        let reporter = ProgressReporter::new(false);
        let outcome =
            delete_exact_paths(&cache_dir, &evidence.exact_paths, &progress, &reporter).unwrap();

        assert_eq!(outcome.deleted_files, 1);
        assert!(!exact.exists());
        assert!(zero.exists());
        assert!(sibling.exists());
        assert!(unrelated.exists());
    }

    #[cfg(unix)]
    #[test]
    fn evidence_rejects_symlink_at_the_expected_exact_path() {
        use std::os::unix::fs::symlink;

        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let candidate = make_candidate(&cache_dir, "default", DetectionMode::CacheAndLogs, 3);
        let exact = PathBuf::from(&candidate.candidate.exact_paths[0]);
        std::fs::create_dir_all(exact.parent().unwrap()).unwrap();
        let outside = fixture.path().join("outside");
        std::fs::write(&outside, b"outside").unwrap();
        symlink(&outside, &exact).unwrap();
        let path = write_evidence(
            fixture.path(),
            "default",
            DetectionMode::CacheAndLogs,
            3,
            vec![candidate],
        );
        assert!(load_and_validate_removal_evidence(&path, &cache_dir, "steam").is_err());
        assert_eq!(std::fs::read(outside).unwrap(), b"outside");
    }

    #[test]
    fn partial_filesystem_failure_returns_error_before_other_cleanup_can_run() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let no_range =
            cache_utils::calculate_cache_path_no_range(&cache_dir, "steam", "/whole.bin");
        let noslice = cache_utils::calculate_cache_path_noslice(&cache_dir, "steam", "/whole.bin");
        for path in [&no_range, &noslice] {
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, b"slice").unwrap();
        }
        let progress = fixture.path().join("progress.json");
        let reporter = ProgressReporter::new(false);
        let calls = AtomicUsize::new(0);
        let paths = vec![no_range.clone(), noslice.clone()];
        let result = delete_exact_paths_with(&cache_dir, &paths, &progress, &reporter, |path| {
            let call = calls.fetch_add(1, Ordering::SeqCst);
            if call == 0 {
                std::fs::remove_file(path)
            } else {
                Err(std::io::Error::new(
                    std::io::ErrorKind::PermissionDenied,
                    "fixture permission failure",
                ))
            }
        });
        assert!(result.is_err());
        assert_eq!(calls.load(Ordering::SeqCst), 2);
        assert_eq!(paths.iter().filter(|path| path.exists()).count(), 1);
    }

    #[test]
    fn database_scope_is_exact_and_preserves_historical_null_ranges() {
        for required in [
            "\"Datasource\" = $2",
            "\"ClientIp\" = $3",
            "\"Timestamp\" = $4",
            "\"Method\" = $5",
            "\"Url\" = $6",
            "\"StatusCode\" = $7",
            "\"CacheStatus\" = $8",
            "\"HttpRange\" = $9",
            "\"BytesServed\" = $10",
        ] {
            assert!(EXACT_DB_DELETE_SQL.contains(required), "missing {required}");
        }
        let mut observation = ExactLogObservation {
            service: "steam".to_string(),
            raw_url: "/whole.bin".to_string(),
            timestamp: DateTime::parse_from_rfc3339("2024-01-01T00:00:00Z")
                .unwrap()
                .with_timezone(&Utc),
            client_ip: "192.0.2.1".to_string(),
            method: "GET".to_string(),
            http_status: 200,
            bytes_served: 1024,
            cache_status: "MISS".to_string(),
            raw_range: None,
        };
        assert_eq!(database_http_range(&observation), "");
        observation.raw_range = Some("bytes=1-2".to_string());
        assert_eq!(database_http_range(&observation), "bytes=1-2");
        assert!(!EXACT_DB_DELETE_SQL.contains("IS NULL"));

        let delete_sql = affected_download_delete_sql("$1, $2");
        assert!(delete_sql.contains("\"Id\" IN ($1, $2)"));
        assert!(delete_sql.contains("AND NOT \"IsActive\""));
        assert!(delete_sql.contains("NOT EXISTS"));
        let recompute_sql = affected_survivor_recompute_sql("$1, $2");
        assert!(recompute_sql.contains("SUM(\"BytesServed\")"));
        assert!(recompute_sql.contains("\"CacheHitBytes\""));
        assert!(recompute_sql.contains("\"CacheMissBytes\""));
        assert!(recompute_sql.contains("'MISS', 'UNKNOWN'"));

        assert!(
            require_complete_history_log_rewrite(&log_purge::LogRewriteOutcome {
                lines_removed: 3,
                permission_errors: 0,
                other_errors: 0,
                cancelled: false,
            })
            .is_ok()
        );
        let partial = require_complete_history_log_rewrite(&log_purge::LogRewriteOutcome {
            lines_removed: 2,
            permission_errors: 0,
            other_errors: 1,
            cancelled: false,
        })
        .unwrap_err();
        assert!(format!("{partial:#}").contains("database evidence was retained"));
    }

    #[test]
    fn streamed_pretty_json_matches_serde_serialization() {
        let report = json!({ "contract_version": 1, "candidates": [] });
        let expected = serde_json::to_string_pretty(&report).unwrap().into_bytes();
        let temp_dir = tempfile::tempdir().unwrap();
        let output_path = temp_dir.path().join("report.json");
        write_pretty_json(&output_path, &report).unwrap();
        assert_eq!(std::fs::read(output_path).unwrap(), expected);
    }
}
