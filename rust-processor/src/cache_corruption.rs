use anyhow::{bail, Context, Result};
use chrono::{DateTime, Utc};
use clap::{Parser, Subcommand};
use serde::{Deserialize, Serialize};
use serde_json::json;
use sqlx::{PgPool, Row};
use std::collections::{BTreeMap, HashSet};
use std::fs::File;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use uuid::Uuid;

mod cache_corruption_detector;
mod cache_structural_scanner;
mod cache_structural_state;
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
    CorruptionCandidate, CorruptionDetector, CorruptionEvidence, DetectionMethod,
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
    /// Cache-key recipe of the target datasource: "monolithic" (default) | "bare_metal"
    #[arg(long = "key-scheme", default_value = "monolithic", global = true)]
    key_scheme: String,
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
        #[arg(short, long)]
        progress: bool,
    },
    /// Scan exact nginx cache files for proven structural framing/length corruption.
    StructuralSummary {
        cache_dir: String,
        #[arg(default_value = "none")]
        progress_json: String,
        /// Shared UTC scan anchor. Standalone callers may omit it to capture UTC now once.
        #[arg(long)]
        scan_started_utc: Option<String>,
        /// Durable scan mode. Omitted preserves the historical stateless full scan.
        #[arg(long, value_enum, requires_all = ["state_db", "state_scope"])]
        scan_mode: Option<cache_structural_state::StructuralScanMode>,
        /// Absolute SQLite state path. Required whenever --scan-mode is supplied.
        #[arg(long, requires = "scan_mode")]
        state_db: Option<PathBuf>,
        /// Stable datasource identity. Required whenever --scan-mode is supplied.
        #[arg(long, requires = "scan_mode")]
        state_scope: Option<String>,
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
    /// Remove only persisted structural candidates after complete preflight and revalidation.
    RemoveStructural {
        cache_dir: String,
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
#[serde(deny_unknown_fields)]
struct RemovalEvidenceEnvelope {
    contract_version: u32,
    scan_id: String,
    detection_method: DetectionMethod,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    threshold: Option<usize>,
    datasource: String,
    candidates: Vec<RemovalCandidate>,
}

/// C# attaches datasource to the canonical candidate. `flatten` consumes that one extra
/// field while preserving the shared candidate type unchanged.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct RemovalCandidate {
    datasource: String,
    #[serde(flatten)]
    candidate: CorruptionCandidate,
}

#[derive(Debug)]
struct ValidatedRemovalEvidence {
    scan_id: Uuid,
    threshold: usize,
    datasource: String,
    candidates: Vec<CorruptionCandidate>,
    exact_paths: Vec<ExactRemovalPath>,
    observations: Vec<ExactLogObservation>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct ExactRemovalPath {
    path: PathBuf,
    expected_key: Option<String>,
}

#[derive(Debug)]
struct ValidatedStructuralEvidence {
    scan_id: Uuid,
    datasource: String,
    candidates: Vec<CorruptionCandidate>,
}

#[derive(Debug, Default, PartialEq, Eq)]
struct StructuralRemovalOutcome {
    deleted_files: usize,
    already_missing: usize,
    healed: usize,
    key_verification_skipped: usize,
    bytes_freed: u64,
}

#[derive(Debug, Default, PartialEq, Eq)]
struct ExactPathRemovalOutcome {
    deleted_files: usize,
    already_missing: usize,
    key_verification_skipped: usize,
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

fn write_pretty_json<T: Serialize + ?Sized>(output_path: &Path, value: &T) -> Result<()> {
    let output_file = File::create(output_path)
        .with_context(|| format!("failed to create report {}", output_path.display()))?;
    let mut output_writer = BufWriter::new(output_file);
    serde_json::to_writer_pretty(&mut output_writer, value)
        .context("failed to serialize corruption report")?;
    output_writer.flush()?;
    Ok(())
}

#[allow(clippy::too_many_arguments)]
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
        _ => reporter.emit_progress(percent_complete, stage_key, context),
    }
    Ok(())
}

fn parse_evidence_timestamp(value: &str) -> Result<DateTime<Utc>> {
    Ok(DateTime::parse_from_rfc3339(value)
        .with_context(|| format!("invalid evidence timestamp '{value}'"))?
        .with_timezone(&Utc))
}

fn structural_expected_key(
    evidence: &cache_corruption_detector::StructuralEvidence,
) -> Option<String> {
    if evidence.cache_key_encoding != "hex" || !evidence.cache_key.len().is_multiple_of(2) {
        return None;
    }
    let bytes = evidence
        .cache_key
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            let pair = std::str::from_utf8(pair).ok()?;
            u8::from_str_radix(pair, 16).ok()
        })
        .collect::<Option<Vec<_>>>()?;
    String::from_utf8(bytes).ok()
}

fn observed_range_header(range: &ObservedByteRange) -> String {
    match range {
        ObservedByteRange::NoRange => String::new(),
        ObservedByteRange::Inclusive { start, end } => format!("bytes={start}-{end}"),
    }
}

fn slice_is_mapped(
    service: &str,
    key_scheme: cache_utils::CacheKeyScheme,
    candidate_slice: &CacheSliceKind,
    mapped_slice: &CacheSliceKind,
) -> bool {
    if key_scheme == cache_utils::CacheKeyScheme::BareMetal
        && !cache_utils::bare_metal_service_slices(service)
    {
        return *candidate_slice == CacheSliceKind::Noslice;
    }
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
) -> Result<Vec<cache_corruption_detector::RepeatedMissCachePath>> {
    let CorruptionEvidence::RepeatedMiss {
        raw_url,
        normalized_uri,
        observed_range,
        cache_slice,
        ..
    } = &candidate.evidence
    else {
        bail!("repeated-MISS removal received non-repeated evidence");
    };
    let mapping = cache_utils::physical_slices_for_request(
        cache_dir,
        &candidate.service,
        raw_url,
        &observed_range_header(observed_range),
    )
    .context("candidate observed range cannot be mapped to physical cache slices")?;

    if mapping.normalized_uri != *normalized_uri {
        bail!("candidate normalized URI does not match its raw URL");
    }
    let key_scheme = cache_utils::active_key_scheme();
    if !mapping
        .slices
        .iter()
        .any(|slice| slice_is_mapped(&candidate.service, key_scheme, cache_slice, &slice.kind))
    {
        bail!("candidate cache slice is not covered by its observed range");
    }

    let paths = cache_corruption_detector::repeated_miss_cache_paths(
        cache_dir,
        &candidate.service,
        raw_url,
        normalized_uri,
        cache_slice,
        key_scheme,
    );
    if paths.is_empty() {
        bail!("candidate service has no cache-key mapping under the active key scheme");
    }
    Ok(paths)
}

fn validate_observation(
    cache_dir: &Path,
    candidate: &CorruptionCandidate,
    observation: &cache_corruption_detector::CandidateObservation,
) -> Result<ExactLogObservation> {
    let CorruptionEvidence::RepeatedMiss {
        raw_url,
        normalized_uri,
        cache_slice,
        ..
    } = &candidate.evidence
    else {
        bail!("repeated-MISS removal received non-repeated evidence");
    };
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
    if observation.cache_status != "MISS" {
        bail!("candidate observation cache status must be MISS");
    }
    if observation.raw_range.as_deref() == Some("") {
        bail!("empty ranges must be represented as null in candidate observations");
    }

    let exact = ExactLogObservation {
        service: candidate.service.clone(),
        raw_url: observation.raw_url.clone(),
        timestamp: parse_evidence_timestamp(&observation.timestamp)?,
        client_ip: observation.client_ip.clone(),
        method: observation.method.clone(),
        http_status: observation.http_status,
        bytes_served: observation.bytes_served,
        cache_status: observation.cache_status.clone(),
        raw_range: observation.raw_range.clone(),
    };

    let raw_range = observation.raw_range.as_deref().unwrap_or("");
    let mapping = cache_utils::physical_slices_for_request(
        cache_dir,
        &candidate.service,
        &observation.raw_url,
        raw_range,
    )
    .context("candidate observation contains an unsupported range")?;
    let key_scheme = cache_utils::active_key_scheme();
    if mapping.normalized_uri != *normalized_uri
        || !mapping
            .slices
            .iter()
            .any(|slice| slice_is_mapped(&candidate.service, key_scheme, cache_slice, &slice.kind))
    {
        bail!("candidate observation does not map to the stored physical slice");
    }
    let candidate_paths = cache_corruption_detector::repeated_miss_cache_paths(
        cache_dir,
        &candidate.service,
        raw_url,
        normalized_uri,
        cache_slice,
        key_scheme,
    );
    let observation_paths = cache_corruption_detector::repeated_miss_cache_paths(
        cache_dir,
        &candidate.service,
        &observation.raw_url,
        &mapping.normalized_uri,
        cache_slice,
        key_scheme,
    );
    if !candidate_paths.iter().any(|candidate_path| {
        observation_paths.iter().any(|observation_path| {
            candidate_path.path == observation_path.path
                && candidate_path.expected_key == observation_path.expected_key
        })
    }) {
        bail!("candidate observation does not map to the stored cache key");
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
    if envelope.detection_method != DetectionMethod::RepeatedMiss {
        bail!("repeated-MISS removal evidence has the wrong detection method");
    }
    let threshold = envelope
        .threshold
        .context("repeated-MISS evidence threshold is required")?;
    if !matches!(threshold, 3 | 5 | 10) {
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

    let mut ids = HashSet::new();
    let mut paths = BTreeMap::new();
    let mut observations = Vec::new();
    let mut candidates = Vec::with_capacity(envelope.candidates.len());

    for wrapped in envelope.candidates {
        let candidate = wrapped.candidate;
        if !wrapped
            .datasource
            .eq_ignore_ascii_case(&envelope.datasource)
        {
            bail!("candidate datasource does not match evidence envelope");
        }
        if !candidate.service.eq_ignore_ascii_case(requested_service) {
            bail!("candidate service is outside the requested service scope");
        }
        if candidate.candidate_id.trim().is_empty() || !ids.insert(candidate.candidate_id.clone()) {
            bail!("candidate IDs must be non-empty and unique");
        }
        if candidate.exact_paths.is_empty() {
            bail!("removable candidate has no exact paths");
        }
        if candidate.exact_paths.len() != 1 {
            bail!("repeated-MISS candidates must contain exactly one exact path");
        }
        let CorruptionEvidence::RepeatedMiss {
            raw_url: _,
            normalized_uri: _,
            observed_range,
            cache_slice: _,
            evidence_count,
            first_seen,
            last_seen,
            observations: candidate_observations_wire,
        } = &candidate.evidence
        else {
            bail!("repeated-MISS envelope contains structural evidence");
        };
        if *evidence_count != candidate_observations_wire.len() || *evidence_count != threshold {
            bail!("candidate evidence count does not match its threshold/observations");
        }

        let first_seen = parse_evidence_timestamp(first_seen)?;
        let last_seen = parse_evidence_timestamp(last_seen)?;
        if last_seen < first_seen || (last_seen - first_seen).num_seconds() > 60 {
            bail!("candidate evidence window must be ordered and no longer than 60 seconds");
        }

        let expected_paths = expected_paths_for_candidate(cache_dir, &candidate)?;
        for exact_path in &candidate.exact_paths {
            let exact_path = PathBuf::from(exact_path);
            let Some(expected_path) = expected_paths
                .iter()
                .find(|expected| expected.path == exact_path)
            else {
                bail!("stored exact path does not match the candidate slice under this cache root");
            };
            if paths
                .insert(exact_path.clone(), expected_path.expected_key.clone())
                .is_some()
            {
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

        let mut candidate_observations = Vec::with_capacity(candidate_observations_wire.len());
        for observation in candidate_observations_wire {
            candidate_observations.push(validate_observation(cache_dir, &candidate, observation)?);
        }
        if candidate_observations
            .windows(2)
            .any(|pair| pair[1].timestamp < pair[0].timestamp)
        {
            bail!("candidate observations must be ordered by timestamp");
        }
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
        if *observed_range
            != cache_utils::parse_http_byte_range(
                candidate_observations_wire
                    .first()
                    .and_then(|observation| observation.raw_range.as_deref())
                    .unwrap_or(""),
            )
            .context("candidate first observation has an invalid range")?
        {
            bail!("candidate observed range does not match its first observation");
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
        threshold,
        datasource: envelope.datasource,
        candidates,
        exact_paths: paths
            .into_iter()
            .map(|(path, expected_key)| ExactRemovalPath { path, expected_key })
            .collect(),
        observations,
    })
}

fn delete_exact_paths_with<F>(
    cache_dir: &Path,
    paths: &[ExactRemovalPath],
    progress_path: &Path,
    reporter: &ProgressReporter,
    key_scheme: cache_utils::CacheKeyScheme,
    mut remove_file: F,
) -> Result<ExactPathRemovalOutcome>
where
    F: FnMut(&Path) -> std::io::Result<()>,
{
    let mut outcome = ExactPathRemovalOutcome::default();
    let mut parent_dirs = HashSet::new();

    for (index, candidate) in paths.iter().enumerate() {
        let path = &candidate.path;
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
        let key_matches = candidate
            .expected_key
            .as_deref()
            .and_then(|expected_key| cache_utils::cache_file_key_matches(path, expected_key));
        if (key_scheme == cache_utils::CacheKeyScheme::BareMetal
            || candidate.expected_key.is_some())
            && key_matches != Some(true)
        {
            outcome.key_verification_skipped += 1;
            continue;
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
    paths: &[ExactRemovalPath],
    progress_path: &Path,
    reporter: &ProgressReporter,
) -> Result<ExactPathRemovalOutcome> {
    delete_exact_paths_with(
        cache_dir,
        paths,
        progress_path,
        reporter,
        cache_utils::active_key_scheme(),
        |path| std::fs::remove_file(path),
    )
}

fn completed_exact_removal_count(outcome: &ExactPathRemovalOutcome) -> Result<usize> {
    if outcome.key_verification_skipped > 0 {
        bail!(
            "{} exact cache path(s) failed key verification; access-log and database evidence were retained",
            outcome.key_verification_skipped
        );
    }
    Ok(outcome.deleted_files)
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

fn seed_scan_progress(
    progress_path: Option<&Path>,
    stage_key: &str,
    detection_method: DetectionMethod,
) {
    if let Some(path) = progress_path {
        let starting = ProgressData {
            status: "starting".to_string(),
            stage_key: stage_key.to_string(),
            context: json!({ "detectionMethod": detection_method }),
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
    progress_path: Option<&Path>,
) -> Result<cache_corruption_detector::CorruptionReport> {
    CorruptionDetector::new(cache_dir, threshold, lookback_days, scan_started_utc)
        .generate_report(log_dir, "access.log", timezone, progress_path)
        .context("failed to generate corruption report")
}

fn load_and_validate_structural_evidence(
    evidence_path: &Path,
    cache_dir: &Path,
) -> Result<ValidatedStructuralEvidence> {
    let file = File::open(evidence_path)
        .with_context(|| format!("failed to open evidence file {}", evidence_path.display()))?;
    let envelope: RemovalEvidenceEnvelope = serde_json::from_reader(file)
        .context("failed to deserialize structural removal evidence")?;
    if envelope.contract_version != CORRUPTION_CONTRACT_VERSION {
        bail!(
            "unsupported corruption evidence contract version {}",
            envelope.contract_version
        );
    }
    if envelope.detection_method != DetectionMethod::Structural {
        bail!("remove-structural requires structural detection evidence");
    }
    if envelope.threshold.is_some() {
        bail!("structural removal evidence must not contain a threshold");
    }
    let scan_id = Uuid::parse_str(&envelope.scan_id).context("invalid evidence scan_id")?;
    if scan_id.is_nil() {
        bail!("evidence scan_id must not be nil");
    }
    if envelope.datasource.trim().is_empty() {
        bail!("evidence datasource is required");
    }
    if envelope.candidates.is_empty() {
        bail!("evidence file contains no removal candidates");
    }
    std::fs::canonicalize(cache_dir)
        .with_context(|| format!("cache root is not accessible: {}", cache_dir.display()))?;

    let mut ids = HashSet::new();
    let mut paths = HashSet::new();
    let mut candidates = Vec::with_capacity(envelope.candidates.len());
    for wrapped in envelope.candidates {
        if !wrapped
            .datasource
            .eq_ignore_ascii_case(&envelope.datasource)
        {
            bail!("candidate datasource does not match evidence envelope");
        }
        let candidate = wrapped.candidate;
        if candidate.candidate_id.trim().is_empty() || !ids.insert(candidate.candidate_id.clone()) {
            bail!("candidate IDs must be non-empty and unique");
        }
        if candidate.service.trim().is_empty() || candidate.exact_paths.len() != 1 {
            bail!("structural candidates require a service and exactly one exact path");
        }
        let CorruptionEvidence::Structural { structural } = &candidate.evidence else {
            bail!("structural envelope contains repeated-MISS evidence");
        };
        if structural.issues.is_empty()
            || structural.cache_key_encoding != "hex"
            || structural.cache_version != 5
            || structural.fingerprint.len != structural.file_length
        {
            bail!("structural candidate evidence is incomplete or inconsistent");
        }
        let exact_path = PathBuf::from(&candidate.exact_paths[0]);
        if cache_utils::strict_cache_file_digest(cache_dir, &exact_path).is_none() {
            bail!("structural candidate path is not an exact nginx cache path");
        }
        if !paths.insert(exact_path) {
            bail!("duplicate exact path appears in structural removal evidence");
        }
        candidates.push(candidate);
    }
    Ok(ValidatedStructuralEvidence {
        scan_id,
        datasource: envelope.datasource,
        candidates,
    })
}

fn run_structural_remove(
    cache_dir: &Path,
    progress_path: &Path,
    evidence_path: &Path,
    reporter: &ProgressReporter,
) -> Result<()> {
    write_progress(
        progress_path,
        reporter,
        "starting",
        "signalr.corruptionRemove.startingStructural",
        json!({ "detectionMethod": "structural" }),
        0.0,
        0,
        0,
    )?;
    let evidence = load_and_validate_structural_evidence(evidence_path, cache_dir)?;
    eprintln!(
        "Validated structural scan {}: {} candidates for datasource '{}'",
        evidence.scan_id,
        evidence.candidates.len(),
        evidence.datasource
    );

    // No mutation is allowed until every selected candidate has passed this preflight.
    let now = std::time::SystemTime::now();
    let mut preflight = Vec::with_capacity(evidence.candidates.len());
    let mut outcome = StructuralRemovalOutcome::default();
    for candidate in &evidence.candidates {
        if cancel::is_cancelled() {
            reporter.emit_cancelled(
                "signalr.corruptionRemove.complete",
                json!({
                    "detectionMethod": "structural",
                    "files": 0,
                    "alreadyMissing": outcome.already_missing,
                    "healed": outcome.healed,
                    "keyVerificationSkipped": outcome.key_verification_skipped,
                    "bytesFreed": 0,
                }),
            );
            return Ok(());
        }
        match cache_structural_scanner::revalidate_for_removal(cache_dir, candidate, now)? {
            cache_structural_scanner::RemovalDisposition::Missing => {
                outcome.already_missing += 1;
            }
            cache_structural_scanner::RemovalDisposition::Healed => outcome.healed += 1,
            cache_structural_scanner::RemovalDisposition::Cancelled => {
                reporter.emit_cancelled(
                    "signalr.corruptionRemove.complete",
                    json!({
                        "detectionMethod": "structural",
                        "files": 0,
                        "alreadyMissing": outcome.already_missing,
                        "healed": outcome.healed,
                        "keyVerificationSkipped": outcome.key_verification_skipped,
                        "bytesFreed": 0,
                    }),
                );
                return Ok(());
            }
            ready @ cache_structural_scanner::RemovalDisposition::Ready { .. } => {
                preflight.push((candidate, ready));
            }
        }
    }

    let mut parent_dirs = HashSet::new();
    for (index, (candidate, _)) in preflight.iter().enumerate() {
        if cancel::is_cancelled() {
            reporter.emit_cancelled(
                "signalr.corruptionRemove.complete",
                json!({
                    "detectionMethod": "structural",
                    "files": outcome.deleted_files,
                    "alreadyMissing": outcome.already_missing,
                    "healed": outcome.healed,
                    "keyVerificationSkipped": outcome.key_verification_skipped,
                    "bytesFreed": outcome.bytes_freed,
                }),
            );
            return Ok(());
        }
        write_progress(
            progress_path,
            reporter,
            "removing_cache",
            "signalr.corruptionRemove.removingCacheFile",
            json!({
                "detectionMethod": "structural",
                "fileIndex": index + 1,
                "totalFiles": preflight.len()
            }),
            10.0 + (index as f64 / preflight.len().max(1) as f64) * 85.0,
            index,
            preflight.len(),
        )?;
        match cache_structural_scanner::revalidate_for_removal(
            cache_dir,
            candidate,
            std::time::SystemTime::now(),
        )? {
            cache_structural_scanner::RemovalDisposition::Missing => {
                outcome.already_missing += 1;
            }
            cache_structural_scanner::RemovalDisposition::Healed => outcome.healed += 1,
            cache_structural_scanner::RemovalDisposition::Cancelled => {
                reporter.emit_cancelled(
                    "signalr.corruptionRemove.complete",
                    json!({
                        "detectionMethod": "structural",
                        "files": outcome.deleted_files,
                        "alreadyMissing": outcome.already_missing,
                        "healed": outcome.healed,
                        "keyVerificationSkipped": outcome.key_verification_skipped,
                        "bytesFreed": outcome.bytes_freed,
                    }),
                );
                return Ok(());
            }
            cache_structural_scanner::RemovalDisposition::Ready { path, evidence } => {
                cache_utils::safe_path_under_root(cache_dir, &path)
                    .with_context(|| format!("unsafe structural exact path {}", path.display()))?;
                if !cache_structural_scanner::path_fingerprint_matches(
                    &path,
                    &evidence.fingerprint,
                )? {
                    bail!(
                        "structural candidate changed immediately before unlink: {}",
                        path.display()
                    );
                }
                if cache_utils::active_key_scheme() == cache_utils::CacheKeyScheme::BareMetal {
                    let matches_expected_key = structural_expected_key(&evidence)
                        .as_deref()
                        .and_then(|expected_key| {
                            cache_utils::cache_file_key_matches(&path, expected_key)
                        });
                    if matches_expected_key != Some(true) {
                        outcome.key_verification_skipped += 1;
                        continue;
                    }
                }
                std::fs::remove_file(&path).with_context(|| {
                    format!("failed to delete structural cache path {}", path.display())
                })?;
                outcome.deleted_files += 1;
                outcome.bytes_freed = outcome.bytes_freed.saturating_add(evidence.file_length);
                if let Some(parent) = path.parent() {
                    parent_dirs.insert(parent.to_path_buf());
                }
            }
        }
    }
    cache_utils::cleanup_empty_directories(cache_dir, parent_dirs);
    write_progress(
        progress_path,
        reporter,
        "completed",
        "signalr.corruptionRemove.complete",
        json!({
            "detectionMethod": "structural",
            "count": evidence.candidates.len(),
            "files": outcome.deleted_files,
            "alreadyMissing": outcome.already_missing,
            "healed": outcome.healed,
            "keyVerificationSkipped": outcome.key_verification_skipped,
            "bytesFreed": outcome.bytes_freed
        }),
        100.0,
        evidence.candidates.len(),
        evidence.candidates.len(),
    )?;
    Ok(())
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
        "Validated scan {}: {} candidates for datasource '{}' at threshold {}",
        evidence.scan_id,
        evidence.candidates.len(),
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
    let removed_count = completed_exact_removal_count(&cache_outcome)?;

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
            "count": removed_count,
            "service": service,
            "files": cache_outcome.deleted_files,
            "alreadyMissing": cache_outcome.already_missing,
            "keyVerificationSkipped": cache_outcome.key_verification_skipped,
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

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    let args = Args::parse();
    cache_utils::set_active_key_scheme(cache_utils::CacheKeyScheme::from_config_str(
        &args.key_scheme,
    ));

    match args.command {
        Commands::Detect {
            log_dir,
            cache_dir,
            output_json,
            timezone,
            threshold,
            lookback_days,
            scan_started_utc,
            progress_json,
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result: Result<()> = (|| {
                let timezone = parse_timezone(&timezone)?;
                let scan_started_utc = parse_scan_started_utc(scan_started_utc.as_deref())?;
                let progress_path = progress_json.as_deref().map(Path::new);
                seed_scan_progress(
                    progress_path,
                    "signalr.corruptionDetect.scanningLogs",
                    DetectionMethod::RepeatedMiss,
                );
                reporter.emit_started(
                    "signalr.corruptionDetect.scanningLogs",
                    json!({ "detectionMethod": "repeated_miss" }),
                );
                let report = generate_report(
                    Path::new(&log_dir),
                    Path::new(&cache_dir),
                    timezone,
                    threshold,
                    lookback_days,
                    scan_started_utc,
                    progress_path,
                )?;
                write_pretty_json(Path::new(&output_json), &report)?;
                reporter.emit_complete(
                    "signalr.corruptionRemove.complete",
                    json!({
                        "totalCorrupted": report.total,
                        "serviceCounts": report.service_counts
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
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result: Result<()> = (|| {
                let timezone = parse_timezone(&timezone)?;
                let scan_started_utc = parse_scan_started_utc(scan_started_utc.as_deref())?;
                let progress_path = (progress_json != "none" && !progress_json.is_empty())
                    .then(|| Path::new(&progress_json));
                seed_scan_progress(
                    progress_path,
                    "signalr.corruptionDetect.scanningLogs",
                    DetectionMethod::RepeatedMiss,
                );
                reporter.emit_started(
                    "signalr.corruptionDetect.scanningLogs",
                    json!({ "detectionMethod": "repeated_miss" }),
                );
                let report = generate_report(
                    Path::new(&log_dir),
                    Path::new(&cache_dir),
                    timezone,
                    threshold,
                    lookback_days,
                    scan_started_utc,
                    progress_path,
                )?;
                reporter.emit_complete(
                    "signalr.corruptionRemove.complete",
                    json!({
                        "totalCorrupted": report.total,
                        "serviceCounts": report.service_counts
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
        Commands::StructuralSummary {
            cache_dir,
            progress_json,
            scan_started_utc,
            scan_mode,
            state_db,
            state_scope,
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result: Result<()> = async {
                let scan_started_utc = parse_scan_started_utc(scan_started_utc.as_deref())?;
                let progress_path = (progress_json != "none" && !progress_json.is_empty())
                    .then(|| PathBuf::from(&progress_json));
                seed_scan_progress(
                    progress_path.as_deref(),
                    "signalr.corruptionDetect.scanningHeaders",
                    DetectionMethod::Structural,
                );
                reporter.emit_started(
                    "signalr.corruptionDetect.scanningHeaders",
                    json!({
                        "detectionMethod": "structural",
                        "scanMode": scan_mode.unwrap_or(cache_structural_state::StructuralScanMode::Full).as_str(),
                    }),
                );
                if scan_mode.is_none() && (state_db.is_some() || state_scope.is_some()) {
                    bail!("--state-db and --state-scope require --scan-mode");
                }
                let result = tokio::task::spawn_blocking(move || match scan_mode {
                    None => cache_structural_scanner::scan(
                        Path::new(&cache_dir),
                        scan_started_utc,
                        progress_path.as_deref(),
                    ),
                    Some(mode) => {
                        let state_db = state_db
                            .as_deref()
                            .context("--state-db is required when --scan-mode is supplied")?;
                        let state_scope = state_scope
                            .as_deref()
                            .context("--state-scope is required when --scan-mode is supplied")?;
                        cache_structural_scanner::scan_with_state(
                            Path::new(&cache_dir),
                            scan_started_utc,
                            progress_path.as_deref(),
                            mode,
                            state_db,
                            state_scope,
                        )
                    }
                })
                .await
                .context("structural scanner blocking task failed")??;
                if result.cancelled {
                    reporter.emit_cancelled(
                        "signalr.corruptionDetect.complete",
                        json!({
                            "detectionMethod": "structural",
                            "totalCorrupted": result.report.total,
                            "serviceCounts": result.report.service_counts,
                            "coverage": result.report.coverage,
                            "scanSummary": result.scan_summary,
                        }),
                    );
                } else {
                    reporter.emit_complete(
                        "signalr.corruptionDetect.complete",
                        json!({
                            "detectionMethod": "structural",
                            "totalCorrupted": result.report.total,
                            "serviceCounts": result.report.service_counts,
                            "coverage": result.report.coverage,
                            "scanSummary": result.scan_summary,
                        }),
                    );
                }
                println!("{}", serde_json::to_string(&result.report)?);
                Ok(())
            }
            .await;
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
        Commands::RemoveStructural {
            cache_dir,
            progress_json,
            evidence_file,
            progress,
        } => {
            let reporter = ProgressReporter::new(progress);
            let result = run_structural_remove(
                Path::new(&cache_dir),
                Path::new(&progress_json),
                Path::new(&evidence_file),
                &reporter,
            );
            progress_events::finish_or_exit(
                &reporter,
                "signalr.corruptionRemove.error.fatal",
                result,
            );
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use cache_corruption_detector::CandidateObservation;
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn make_candidate(cache_dir: &Path, datasource: &str, threshold: usize) -> RemovalCandidate {
        let raw_url = "/depot/file.bin".to_string();
        let end = cache_utils::DEFAULT_SLICE_SIZE - 1;
        let exact_path = cache_utils::calculate_cache_path(cache_dir, "steam", &raw_url, 0, end);
        let observations = (0..threshold)
            .map(|second| CandidateObservation {
                timestamp: format!("2024-01-01T00:00:{second:02}Z"),
                client_ip: "192.0.2.10".to_string(),
                raw_url: raw_url.clone(),
                method: "GET".to_string(),
                http_status: 206,
                bytes_served: 1024,
                cache_status: "MISS".to_string(),
                raw_range: Some(format!("bytes=0-{end}")),
            })
            .collect::<Vec<_>>();
        RemovalCandidate {
            datasource: datasource.to_string(),
            candidate: CorruptionCandidate {
                candidate_id: "candidate-1".to_string(),
                service: "steam".to_string(),
                exact_paths: vec![exact_path.display().to_string()],
                evidence: CorruptionEvidence::RepeatedMiss {
                    raw_url,
                    normalized_uri: "/depot/file.bin".to_string(),
                    observed_range: ObservedByteRange::Inclusive { start: 0, end },
                    cache_slice: CacheSliceKind::Ranged { start: 0, end },
                    evidence_count: threshold,
                    first_seen: observations.first().unwrap().timestamp.clone(),
                    last_seen: observations.last().unwrap().timestamp.clone(),
                    observations,
                },
            },
        }
    }

    fn materialize_candidate_path(candidate: &RemovalCandidate) -> PathBuf {
        let path = PathBuf::from(&candidate.candidate.exact_paths[0]);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, b"slice").unwrap();
        path
    }

    fn write_evidence(
        root: &Path,
        datasource: &str,
        threshold: usize,
        candidates: Vec<RemovalCandidate>,
    ) -> PathBuf {
        let path = root.join("evidence.json");
        let envelope = RemovalEvidenceEnvelope {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            scan_id: Uuid::new_v4().to_string(),
            detection_method: DetectionMethod::RepeatedMiss,
            threshold: Some(threshold),
            datasource: datasource.to_string(),
            candidates,
        };
        std::fs::write(&path, serde_json::to_vec_pretty(&envelope).unwrap()).unwrap();
        path
    }

    fn make_structural_candidate(cache_dir: &Path, datasource: &str) -> RemovalCandidate {
        let hash = cache_utils::calculate_md5("steam/file");
        let digest = cache_utils::parse_cache_file_digest(&hash).unwrap();
        let exact_path = cache_utils::cache_path_for_digest(cache_dir, digest);
        RemovalCandidate {
            datasource: datasource.to_string(),
            candidate: CorruptionCandidate {
                candidate_id: "structural-1".to_string(),
                service: "steam".to_string(),
                exact_paths: vec![exact_path.display().to_string()],
                evidence: CorruptionEvidence::Structural {
                    structural: cache_corruption_detector::StructuralEvidence {
                        issues: vec![cache_corruption_detector::StructuralIssue::EmptyCacheFile],
                        cache_key_encoding: "hex".to_string(),
                        cache_key: String::new(),
                        cache_key_md5: "d41d8cd98f00b204e9800998ecf8427e".to_string(),
                        cache_version: 5,
                        http_status: None,
                        header_start: None,
                        body_start: None,
                        file_length: 0,
                        actual_payload_length: None,
                        expected_payload_length: None,
                        content_length: None,
                        content_range: None,
                        fingerprint: cache_corruption_detector::FileFingerprint {
                            dev: 1,
                            ino: 2,
                            len: 0,
                            mtime_ns: 3,
                            ctime_ns: 4,
                        },
                        detected_at_utc: "2024-01-01T00:00:00Z".to_string(),
                    },
                },
            },
        }
    }

    fn write_structural_evidence(
        root: &Path,
        datasource: &str,
        candidates: Vec<RemovalCandidate>,
    ) -> PathBuf {
        let path = root.join("structural-evidence.json");
        let envelope = RemovalEvidenceEnvelope {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            scan_id: Uuid::new_v4().to_string(),
            detection_method: DetectionMethod::Structural,
            threshold: None,
            datasource: datasource.to_string(),
            candidates,
        };
        std::fs::write(&path, serde_json::to_vec_pretty(&envelope).unwrap()).unwrap();
        path
    }

    #[cfg(all(unix, target_pointer_width = "64"))]
    fn age_file(path: &Path) {
        use std::ffi::CString;
        use std::os::unix::ffi::OsStrExt;
        let path = CString::new(path.as_os_str().as_bytes()).unwrap();
        let seconds = std::time::SystemTime::now()
            .duration_since(std::time::SystemTime::UNIX_EPOCH)
            .unwrap()
            .as_secs() as libc::time_t
            - (cache_structural_scanner::MIN_STABLE_AGE_SECONDS as libc::time_t + 5);
        let times = [
            libc::timespec {
                tv_sec: seconds,
                tv_nsec: 0,
            },
            libc::timespec {
                tv_sec: seconds,
                tv_nsec: 0,
            },
        ];
        assert_eq!(
            unsafe { libc::utimensat(libc::AT_FDCWD, path.as_ptr(), times.as_ptr(), 0) },
            0
        );
    }

    #[cfg(all(unix, target_pointer_width = "64"))]
    fn scanned_structural_candidate(
        cache_dir: &Path,
        key: &[u8],
        payload: &[u8],
        id: &str,
    ) -> RemovalCandidate {
        let path = cache_structural_scanner::write_linux_v5_test_fixture(
            cache_dir,
            key,
            b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n",
            payload,
        );
        age_file(&path);
        let inspection = cache_structural_scanner::inspect_path(
            cache_dir,
            &path,
            std::time::SystemTime::now(),
            Utc::now(),
        )
        .unwrap();
        let cache_structural_scanner::InspectionOutcome::Proven(structural) = inspection.outcome
        else {
            panic!("fixture must be proven structurally invalid");
        };
        RemovalCandidate {
            datasource: "default".into(),
            candidate: CorruptionCandidate {
                candidate_id: id.into(),
                service: "steam".into(),
                exact_paths: vec![path.display().to_string()],
                evidence: CorruptionEvidence::Structural { structural },
            },
        }
    }

    fn fixed_cli_prefix(command: &str) -> Vec<&str> {
        match command {
            "detect" => vec![
                "cache_corruption",
                "detect",
                "/logs",
                "/cache",
                "/tmp/report.json",
                "UTC",
                "3",
            ],
            "summary" => vec![
                "cache_corruption",
                "summary",
                "/logs",
                "/cache",
                "none",
                "UTC",
                "3",
            ],
            _ => unreachable!(),
        }
    }

    #[test]
    fn fixed_cli_matches_csharp_calls_and_rejects_removed_inputs() {
        for command in ["detect", "summary"] {
            let mut args = fixed_cli_prefix(command);
            args.extend([
                "--lookback-days",
                "30",
                "--scan-started-utc",
                "2024-01-01T00:00:00Z",
                "--key-scheme",
                "bare_metal",
                "--progress",
            ]);
            assert!(Args::try_parse_from(args).is_ok());

            for flag in ["--mode", "--no-cache-check", "--detect-redownloads"] {
                let mut args = fixed_cli_prefix(command);
                args.push(flag);
                if flag == "--mode" {
                    args.push("cache_and_logs");
                }
                assert!(
                    Args::try_parse_from(args).is_err(),
                    "{flag} must be rejected"
                );
            }
        }
        assert!(Args::try_parse_from([
            "cache_corruption",
            "purge-history",
            "/logs",
            "all",
            "/tmp/progress.json",
            "--evidence-file",
            "/tmp/evidence.json",
        ])
        .is_err());
        assert!(Args::try_parse_from([
            "cache_corruption",
            "remove",
            "/logs",
            "/cache",
            "steam",
            "/tmp/progress.json",
            "--evidence-file",
            "/server/evidence.json",
            "--key-scheme",
            "bare_metal",
        ])
        .is_ok());
        assert!(Args::try_parse_from([
            "cache_corruption",
            "structural-summary",
            "/cache",
            "none",
            "--scan-started-utc",
            "2024-01-01T00:00:00Z",
            "--progress",
        ])
        .is_ok());
        assert!(Args::try_parse_from([
            "cache_corruption",
            "structural-summary",
            "/cache",
            "none",
            "--scan-mode",
            "incremental",
            "--state-db",
            "/state/structural.sqlite3",
            "--state-scope",
            "default",
        ])
        .is_ok());
        assert!(Args::try_parse_from([
            "cache_corruption",
            "structural-summary",
            "/cache",
            "none",
            "--scan-mode",
            "incremental",
        ])
        .is_err());
        assert!(Args::try_parse_from([
            "cache_corruption",
            "structural-summary",
            "/cache",
            "none",
            "--state-db",
            "/state/structural.sqlite3",
        ])
        .is_err());
        assert!(Args::try_parse_from([
            "cache_corruption",
            "remove-structural",
            "/cache",
            "/tmp/progress.json",
            "--evidence-file",
            "/server/evidence.json",
        ])
        .is_ok());
    }

    #[test]
    fn lookback_days_cli_accepts_bounds_defaults_and_rejects_out_of_range() {
        for value in ["1", "365"] {
            let mut args = fixed_cli_prefix("summary");
            args.extend(["--lookback-days", value]);
            assert!(Args::try_parse_from(args).is_ok());
        }
        for value in ["0", "366"] {
            let mut args = fixed_cli_prefix("summary");
            args.extend(["--lookback-days", value]);
            assert!(Args::try_parse_from(args).is_err());
        }
        let args = Args::try_parse_from(fixed_cli_prefix("summary")).unwrap();
        let Commands::Summary { lookback_days, .. } = args.command else {
            panic!("expected summary");
        };
        assert_eq!(lookback_days, DEFAULT_LOOKBACK_DAYS);
    }

    #[test]
    fn scan_started_utc_parser_requires_utc_and_preserves_the_instant() {
        let parsed = parse_scan_started_utc(Some("2024-05-01T12:30:00Z")).unwrap();
        assert_eq!(parsed.to_rfc3339(), "2024-05-01T12:30:00+00:00");
        assert!(parse_scan_started_utc(Some("2024-05-01T12:30:00-05:00")).is_err());
        assert!(parse_scan_started_utc(Some("not-a-timestamp")).is_err());
    }

    #[test]
    fn v4_repeated_miss_actionable_evidence_validates() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let candidate = make_candidate(&cache_dir, "default", 3);
        let exact_path = materialize_candidate_path(&candidate);
        let evidence_path = write_evidence(fixture.path(), "default", 3, vec![candidate]);

        let evidence =
            load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam").unwrap();
        assert_eq!(evidence.threshold, 3);
        assert_eq!(
            evidence.exact_paths,
            vec![ExactRemovalPath {
                path: exact_path,
                expected_key: None,
            }]
        );
        assert_eq!(evidence.observations.len(), 3);
        assert!(evidence
            .observations
            .iter()
            .all(|observation| observation.cache_status == "MISS"));
    }

    #[test]
    fn v3_and_old_mode_evidence_fail_closed_without_mutation() {
        for old_mode in ["logs_only", "redownload"] {
            let fixture = tempfile::tempdir().unwrap();
            let cache_dir = fixture.path().join("cache");
            std::fs::create_dir_all(&cache_dir).unwrap();
            let candidate = make_candidate(&cache_dir, "default", 3);
            let sentinel = materialize_candidate_path(&candidate);
            let evidence_path = write_evidence(fixture.path(), "default", 3, vec![candidate]);

            let mut value: serde_json::Value =
                serde_json::from_slice(&std::fs::read(&evidence_path).unwrap()).unwrap();
            value["contract_version"] = json!(3);
            value["mode"] = json!(old_mode);
            std::fs::write(&evidence_path, serde_json::to_vec_pretty(&value).unwrap()).unwrap();

            let error = load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam")
                .expect_err("v3 evidence must be rejected");
            assert!(
                error.to_string().contains("deserialize")
                    || error.to_string().contains("contract version")
            );
            assert_eq!(std::fs::read(&sentinel).unwrap(), b"slice");
        }
    }

    #[test]
    fn v4_unknown_or_review_fields_fail_closed_without_mutation() {
        for (field, field_value) in [
            ("mode", json!("cache_and_logs")),
            ("reason", json!("repeated_miss_burst")),
            ("removal_allowed", json!(true)),
            ("supporting_sibling", json!(null)),
        ] {
            let fixture = tempfile::tempdir().unwrap();
            let cache_dir = fixture.path().join("cache");
            std::fs::create_dir_all(&cache_dir).unwrap();
            let candidate = make_candidate(&cache_dir, "default", 3);
            let sentinel = materialize_candidate_path(&candidate);
            let evidence_path = write_evidence(fixture.path(), "default", 3, vec![candidate]);

            let mut envelope_value: serde_json::Value =
                serde_json::from_slice(&std::fs::read(&evidence_path).unwrap()).unwrap();
            envelope_value["candidates"][0][field] = field_value;
            std::fs::write(
                &evidence_path,
                serde_json::to_vec_pretty(&envelope_value).unwrap(),
            )
            .unwrap();
            assert!(
                load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam").is_err()
            );
            assert!(sentinel.is_file());
        }
    }

    #[test]
    fn malformed_actionable_evidence_is_rejected() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();

        for mutation in 0..5 {
            let mut candidate = make_candidate(&cache_dir, "default", 3);
            materialize_candidate_path(&candidate);
            let CorruptionEvidence::RepeatedMiss {
                evidence_count,
                last_seen,
                observations,
                ..
            } = &mut candidate.candidate.evidence
            else {
                unreachable!();
            };
            match mutation {
                0 => observations[0].cache_status = "HIT".to_string(),
                1 => observations[0].method = "POST".to_string(),
                2 => *evidence_count = 2,
                3 => candidate.candidate.exact_paths.clear(),
                4 => *last_seen = "2024-01-01T00:02:00Z".to_string(),
                _ => unreachable!(),
            }
            let evidence_path = write_evidence(fixture.path(), "default", 3, vec![candidate]);
            assert!(
                load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam").is_err()
            );
        }
    }

    #[test]
    fn structural_evidence_is_strict_singular_and_method_tagged() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let candidate = make_structural_candidate(&cache_dir, "default");
        let path = write_structural_evidence(fixture.path(), "default", vec![candidate.clone()]);
        let evidence = load_and_validate_structural_evidence(&path, &cache_dir).unwrap();
        assert_eq!(evidence.candidates.len(), 1);

        let mut duplicate_path = candidate.clone();
        duplicate_path.candidate.candidate_id = "structural-2".into();
        let path = write_structural_evidence(
            fixture.path(),
            "default",
            vec![candidate.clone(), duplicate_path],
        );
        assert!(load_and_validate_structural_evidence(&path, &cache_dir).is_err());

        let mut multiple = candidate.clone();
        multiple
            .candidate
            .exact_paths
            .push(multiple.candidate.exact_paths[0].clone());
        let path = write_structural_evidence(fixture.path(), "default", vec![multiple]);
        assert!(load_and_validate_structural_evidence(&path, &cache_dir).is_err());

        let path = write_structural_evidence(fixture.path(), "default", vec![candidate]);
        let mut value: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        value["candidates"][0]["evidence"]["observations"] = json!([]);
        std::fs::write(&path, serde_json::to_vec_pretty(&value).unwrap()).unwrap();
        assert!(load_and_validate_structural_evidence(&path, &cache_dir).is_err());
    }

    #[cfg(all(unix, target_pointer_width = "64"))]
    #[test]
    fn structural_removal_unlinks_only_revalidated_exact_file_without_logs_or_database() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let candidate = scanned_structural_candidate(&cache_dir, b"steam/file", b"1234", "one");
        let exact = PathBuf::from(&candidate.candidate.exact_paths[0]);
        let sibling = fixture.path().join("access.log");
        std::fs::write(&sibling, b"must remain untouched").unwrap();
        let evidence = write_structural_evidence(fixture.path(), "default", vec![candidate]);
        run_structural_remove(
            &cache_dir,
            &fixture.path().join("progress.json"),
            &evidence,
            &ProgressReporter::new(false),
        )
        .unwrap();
        assert!(!exact.exists());
        assert_eq!(std::fs::read(sibling).unwrap(), b"must remain untouched");
    }

    #[cfg(all(unix, target_pointer_width = "64"))]
    #[test]
    fn structural_removal_preflights_every_candidate_before_first_unlink() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let first = scanned_structural_candidate(&cache_dir, b"steam/first", b"1234", "one");
        let second = scanned_structural_candidate(&cache_dir, b"steam/second", b"1234", "two");
        let first_path = PathBuf::from(&first.candidate.exact_paths[0]);
        let second_path = PathBuf::from(&second.candidate.exact_paths[0]);
        std::fs::write(&second_path, b"changed corrupt replacement").unwrap();
        age_file(&second_path);
        let evidence = write_structural_evidence(fixture.path(), "default", vec![first, second]);
        assert!(run_structural_remove(
            &cache_dir,
            &fixture.path().join("progress.json"),
            &evidence,
            &ProgressReporter::new(false),
        )
        .is_err());
        assert!(
            first_path.exists(),
            "preflight failure must precede every unlink"
        );
    }

    #[test]
    fn per_observation_raw_urls_scope_exact_log_and_database_cleanup() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let mut candidate = make_candidate(&cache_dir, "default", 3);
        materialize_candidate_path(&candidate);
        let CorruptionEvidence::RepeatedMiss { observations, .. } =
            &mut candidate.candidate.evidence
        else {
            unreachable!();
        };
        observations[1].raw_url = "//depot//file.bin".to_string();
        let evidence_path = write_evidence(fixture.path(), "default", 3, vec![candidate]);
        let evidence =
            load_and_validate_removal_evidence(&evidence_path, &cache_dir, "steam").unwrap();

        assert_eq!(evidence.observations[0].raw_url, "/depot/file.bin");
        assert_eq!(evidence.observations[1].raw_url, "//depot//file.bin");
        let matcher = ExactLogMatcher::new(evidence.observations.clone());
        assert!(matcher.prefilter().is_ok());
        assert!(EXACT_DB_DELETE_SQL.contains("\"Url\" = $6"));
        assert!(EXACT_DB_DELETE_SQL.contains("\"BytesServed\" = $10"));
    }

    #[test]
    fn evidence_rejects_datasource_threshold_and_root_mismatches() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_one = fixture.path().join("one");
        let cache_two = fixture.path().join("two");
        std::fs::create_dir_all(&cache_one).unwrap();
        std::fs::create_dir_all(&cache_two).unwrap();

        let candidate = make_candidate(&cache_one, "wrong", 3);
        materialize_candidate_path(&candidate);
        let path = write_evidence(fixture.path(), "default", 3, vec![candidate]);
        assert!(load_and_validate_removal_evidence(&path, &cache_one, "steam").is_err());

        let candidate = make_candidate(&cache_one, "default", 3);
        materialize_candidate_path(&candidate);
        let path = write_evidence(fixture.path(), "default", 5, vec![candidate]);
        assert!(load_and_validate_removal_evidence(&path, &cache_one, "steam").is_err());

        let candidate = make_candidate(&cache_one, "default", 3);
        materialize_candidate_path(&candidate);
        let path = write_evidence(fixture.path(), "default", 3, vec![candidate]);
        assert!(load_and_validate_removal_evidence(&path, &cache_two, "steam").is_err());
        assert!(load_and_validate_removal_evidence(&path, &cache_one, "epic").is_err());
    }

    #[test]
    fn evidence_rejects_root_escape_and_non_regular_leaf_without_touching_them() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let outside = fixture.path().join("outside");
        std::fs::write(&outside, b"sentinel").unwrap();

        let mut escaped = make_candidate(&cache_dir, "default", 3);
        escaped.candidate.exact_paths = vec![outside.display().to_string()];
        let path = write_evidence(fixture.path(), "default", 3, vec![escaped]);
        assert!(load_and_validate_removal_evidence(&path, &cache_dir, "steam").is_err());
        assert_eq!(std::fs::read(&outside).unwrap(), b"sentinel");

        let directory_candidate = make_candidate(&cache_dir, "default", 3);
        let leaf = PathBuf::from(&directory_candidate.candidate.exact_paths[0]);
        std::fs::create_dir_all(&leaf).unwrap();
        let path = write_evidence(fixture.path(), "default", 3, vec![directory_candidate]);
        assert!(load_and_validate_removal_evidence(&path, &cache_dir, "steam").is_err());
        assert!(leaf.is_dir());
    }

    #[test]
    fn exact_mid_object_delete_leaves_siblings_and_unrelated_paths() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let candidate = make_candidate(&cache_dir, "default", 3);
        let exact = materialize_candidate_path(&candidate);
        let sibling = cache_utils::calculate_cache_path(
            &cache_dir,
            "steam",
            "/depot/file.bin",
            cache_utils::DEFAULT_SLICE_SIZE,
            cache_utils::DEFAULT_SLICE_SIZE * 2 - 1,
        );
        std::fs::create_dir_all(sibling.parent().unwrap()).unwrap();
        std::fs::write(&sibling, b"sibling").unwrap();
        let unrelated = cache_dir.join("unrelated");
        std::fs::write(&unrelated, b"unrelated").unwrap();

        let progress = fixture.path().join("progress.json");
        let exact_candidate = ExactRemovalPath {
            path: exact.clone(),
            expected_key: None,
        };
        let outcome = delete_exact_paths_with(
            &cache_dir,
            std::slice::from_ref(&exact_candidate),
            &progress,
            &ProgressReporter::new(false),
            cache_utils::CacheKeyScheme::Monolithic,
            |path| std::fs::remove_file(path),
        )
        .unwrap();
        assert_eq!(outcome.deleted_files, 1);
        assert!(!exact.exists());
        assert_eq!(std::fs::read(&sibling).unwrap(), b"sibling");
        assert_eq!(std::fs::read(&unrelated).unwrap(), b"unrelated");
    }

    #[test]
    fn bare_metal_key_header_mismatch_is_counted_and_not_deleted() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let expected_key = "lancache-steam/depot/1/chunk/abcdef";
        let mapped = cache_corruption_detector::repeated_miss_cache_paths(
            &cache_dir,
            "steam",
            "/depot/1/chunk/abcdef",
            "/depot/1/chunk/abcdef",
            &CacheSliceKind::Ranged {
                start: 0,
                end: cache_utils::DEFAULT_SLICE_SIZE - 1,
            },
            cache_utils::CacheKeyScheme::BareMetal,
        );
        let path = mapped[0].path.clone();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut contents = vec![0x03u8, 0x00, 0x00, 0x00, b'\n'];
        contents.extend_from_slice(b"KEY: lancache-steam/different-object\n");
        contents.extend_from_slice(b"HTTP/1.1 200 OK\r\nbody");
        std::fs::write(&path, contents).unwrap();

        let outcome = delete_exact_paths_with(
            &cache_dir,
            &[ExactRemovalPath {
                path: path.clone(),
                expected_key: Some(expected_key.to_string()),
            }],
            &fixture.path().join("progress.json"),
            &ProgressReporter::new(false),
            cache_utils::CacheKeyScheme::BareMetal,
            |candidate_path| std::fs::remove_file(candidate_path),
        )
        .unwrap();

        assert_eq!(outcome.deleted_files, 0);
        assert_eq!(outcome.key_verification_skipped, 1);
        assert!(path.exists());
    }

    #[test]
    fn bare_metal_missing_expected_key_is_counted_and_not_deleted() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let path = cache_dir.join("candidate");
        std::fs::write(&path, b"cache file without trusted expected key").unwrap();
        let remove_calls = AtomicUsize::new(0);

        let outcome = delete_exact_paths_with(
            &cache_dir,
            &[ExactRemovalPath {
                path: path.clone(),
                expected_key: None,
            }],
            &fixture.path().join("progress.json"),
            &ProgressReporter::new(false),
            cache_utils::CacheKeyScheme::BareMetal,
            |candidate_path| {
                remove_calls.fetch_add(1, Ordering::SeqCst);
                std::fs::remove_file(candidate_path)
            },
        )
        .unwrap();

        assert_eq!(outcome.deleted_files, 0);
        assert_eq!(outcome.key_verification_skipped, 1);
        assert_eq!(remove_calls.load(Ordering::SeqCst), 0);
        assert!(path.exists());
    }

    #[test]
    fn skipped_key_verification_blocks_history_cleanup_and_count_is_deleted_files() {
        let skipped = ExactPathRemovalOutcome {
            deleted_files: 1,
            key_verification_skipped: 1,
            ..ExactPathRemovalOutcome::default()
        };
        let error = completed_exact_removal_count(&skipped).unwrap_err();
        assert!(error
            .to_string()
            .contains("access-log and database evidence were retained"));

        let completed = ExactPathRemovalOutcome {
            deleted_files: 2,
            already_missing: 3,
            ..ExactPathRemovalOutcome::default()
        };
        assert_eq!(completed_exact_removal_count(&completed).unwrap(), 2);
    }

    #[test]
    fn bare_metal_unsliced_identity_accepts_ranged_request_mapping() {
        let ranged = CacheSliceKind::Ranged {
            start: 0,
            end: cache_utils::DEFAULT_SLICE_SIZE - 1,
        };
        assert!(slice_is_mapped(
            "steam",
            cache_utils::CacheKeyScheme::BareMetal,
            &CacheSliceKind::Noslice,
            &ranged,
        ));
        assert!(!slice_is_mapped(
            "steam",
            cache_utils::CacheKeyScheme::BareMetal,
            &ranged,
            &ranged,
        ));
        assert!(!slice_is_mapped(
            "blizzard",
            cache_utils::CacheKeyScheme::BareMetal,
            &CacheSliceKind::Noslice,
            &ranged,
        ));
    }

    #[test]
    fn partial_filesystem_failure_returns_error_before_later_cleanup() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        std::fs::create_dir_all(&cache_dir).unwrap();
        let first = make_candidate(&cache_dir, "default", 3);
        let first_path = materialize_candidate_path(&first);
        let mut second = make_candidate(&cache_dir, "default", 3);
        let CorruptionEvidence::RepeatedMiss {
            raw_url,
            normalized_uri,
            ..
        } = &mut second.candidate.evidence
        else {
            unreachable!();
        };
        *raw_url = "/depot/second.bin".to_string();
        *normalized_uri = "/depot/second.bin".to_string();
        let end = cache_utils::DEFAULT_SLICE_SIZE - 1;
        let second_path =
            cache_utils::calculate_cache_path(&cache_dir, "steam", "/depot/second.bin", 0, end);
        std::fs::create_dir_all(second_path.parent().unwrap()).unwrap();
        std::fs::write(&second_path, b"second").unwrap();

        let calls = AtomicUsize::new(0);
        let result = delete_exact_paths_with(
            &cache_dir,
            &[
                ExactRemovalPath {
                    path: first_path.clone(),
                    expected_key: None,
                },
                ExactRemovalPath {
                    path: second_path.clone(),
                    expected_key: None,
                },
            ],
            &fixture.path().join("progress.json"),
            &ProgressReporter::new(false),
            cache_utils::CacheKeyScheme::Monolithic,
            |path| {
                if calls.fetch_add(1, Ordering::SeqCst) == 0 {
                    std::fs::remove_file(path)
                } else {
                    Err(std::io::Error::new(
                        std::io::ErrorKind::PermissionDenied,
                        "injected failure",
                    ))
                }
            },
        );
        assert!(result.is_err());
        assert!(!first_path.exists());
        assert!(second_path.exists());
        assert_eq!(calls.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn database_cleanup_sql_preserves_exact_scope_and_survivor_recompute() {
        assert!(EXACT_DB_DELETE_SQL.contains("\"Datasource\" = $2"));
        assert!(EXACT_DB_DELETE_SQL.contains("\"ClientIp\" = $3"));
        assert!(EXACT_DB_DELETE_SQL.contains("\"Timestamp\" = $4"));
        assert!(EXACT_DB_DELETE_SQL.contains("\"BytesServed\" = $10"));
        assert_eq!(
            database_http_range(&ExactLogObservation {
                service: "steam".into(),
                raw_url: "/a".into(),
                timestamp: Utc::now(),
                client_ip: "client".into(),
                method: "GET".into(),
                http_status: 200,
                bytes_served: 1,
                cache_status: "MISS".into(),
                raw_range: None,
            }),
            ""
        );
        let delete = affected_download_delete_sql("$1");
        assert!(delete.contains("NOT \"IsActive\""));
        assert!(delete.contains("NOT EXISTS"));
        let update = affected_survivor_recompute_sql("$1");
        assert!(update.contains("COALESCE((SELECT SUM(\"BytesServed\")"));
    }

    #[test]
    fn streamed_pretty_json_matches_serde_serialization() {
        let fixture = tempfile::tempdir().unwrap();
        let cache_dir = fixture.path().join("cache");
        let candidate = make_candidate(&cache_dir, "default", 3);
        let envelope = RemovalEvidenceEnvelope {
            contract_version: CORRUPTION_CONTRACT_VERSION,
            scan_id: Uuid::new_v4().to_string(),
            detection_method: DetectionMethod::RepeatedMiss,
            threshold: Some(3),
            datasource: "default".to_string(),
            candidates: vec![candidate],
        };
        let path = fixture.path().join("pretty.json");
        write_pretty_json(&path, &envelope).unwrap();
        let from_file: serde_json::Value =
            serde_json::from_slice(&std::fs::read(path).unwrap()).unwrap();
        assert_eq!(from_file, serde_json::to_value(envelope).unwrap());
    }
}
