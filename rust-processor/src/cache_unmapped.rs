//! Finds cache files on disk that no detection row claims, names them from the nginx `KEY:`
//! header nginx stored beside the body, and deletes them on request.
//!
//! The claimed set is a plain list of md5 digests the host streams out of its detection tables,
//! never a list of path strings: a stored path carries whichever separator the host that ran
//! detection used, while the digest is the same 16 bytes everywhere.

use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use std::io::{BufRead, BufReader, ErrorKind};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use crate::cache_utils;
use crate::cancel;
use crate::progress_utils;

/// Bumped whenever the report shape changes. The host refuses a report on any other version
/// instead of migrating it.
pub const UNMAPPED_CONTRACT_VERSION: u32 = 1;

/// Grouping label for a file whose `KEY:` header is unreadable or does not start with a
/// service prefix.
const UNKNOWN_SERVICE: &str = "unknown";

/// The host polls the progress file every 500 ms, so writing faster than that only costs I/O.
const PROGRESS_INTERVAL: Duration = Duration::from_millis(500);

const ENUMERATING_STAGE: &str = "signalr.unmappedScan.enumerating";
const READING_KEYS_STAGE: &str = "signalr.unmappedScan.readingKeys";
const SCAN_COMPLETE_STAGE: &str = "signalr.unmappedScan.complete";
const REMOVING_STAGE: &str = "signalr.unmappedRemove.removingCacheFiles";
const REMOVE_COMPLETE_STAGE: &str = "signalr.unmappedRemove.complete";

#[derive(Debug, Serialize)]
pub struct UnmappedReport {
    pub contract_version: u32,
    pub cancelled: bool,
    pub scan_started_utc: String,
    pub cache_root: String,
    pub files_on_disk: usize,
    pub claimed_digests: usize,
    pub skipped_non_hash_names: usize,
    pub orphan_count: usize,
    pub orphan_bytes: u64,
    pub unreadable_keys: usize,
    pub services: Vec<UnmappedService>,
}

#[derive(Debug, Serialize)]
pub struct UnmappedService {
    pub service: String,
    pub file_count: usize,
    pub total_bytes: u64,
    pub files: Vec<UnmappedFile>,
}

#[derive(Debug, Serialize)]
pub struct UnmappedFile {
    pub digest: String,
    pub path: String,
    pub url: Option<String>,
    pub size_bytes: u64,
}

#[derive(Debug, Serialize)]
pub struct UnmappedRemovalReport {
    pub contract_version: u32,
    pub cancelled: bool,
    pub deleted_files: usize,
    pub already_missing: usize,
    pub claimed_since_scan: usize,
    pub bytes_freed: u64,
}

/// The exact path list the host selected. Anything else in the file is a mismatch between the
/// two sides and is refused rather than ignored.
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct RemovalEvidence {
    contract_version: u32,
    paths: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct UnmappedProgress {
    status: String,
    stage_key: String,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
    timestamp: String,
}

fn write_progress(
    progress_path: Option<&Path>,
    status: &str,
    stage_key: &str,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
) -> Result<()> {
    let Some(progress_path) = progress_path else {
        return Ok(());
    };
    progress_utils::write_progress_json(
        progress_path,
        &UnmappedProgress {
            status: status.to_string(),
            stage_key: stage_key.to_string(),
            context,
            percent_complete,
            files_processed,
            total_files,
            timestamp: progress_utils::current_timestamp(),
        },
    )
    .context("failed to write unmapped scan progress")
}

/// Reads the newline-delimited digests the host streamed out of its detection tables.
fn load_claimed_digests(path: &Path) -> Result<HashSet<u128>> {
    let file = std::fs::File::open(path)
        .with_context(|| format!("failed to open claimed digest file {}", path.display()))?;
    let mut claimed = HashSet::new();
    for (index, line) in BufReader::new(file).lines().enumerate() {
        let line =
            line.with_context(|| format!("failed to read claimed digest file {}", path.display()))?;
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        let lowercase_hex = line.len() == 32
            && line
                .bytes()
                .all(|byte| byte.is_ascii_digit() || matches!(byte, b'a'..=b'f'));
        let digest = lowercase_hex
            .then(|| u128::from_str_radix(line, 16).ok())
            .flatten()
            .with_context(|| {
                format!(
                    "claimed digest line {} is not a 32-character lowercase md5 digest",
                    index + 1
                )
            })?;
        claimed.insert(digest);
    }
    Ok(claimed)
}

/// The service a cache key belongs to: the segment before its first `/`.
fn service_from_key(key: Option<&str>) -> String {
    let Some(key) = key else {
        return UNKNOWN_SERVICE.to_string();
    };
    let prefix = key.split('/').next().unwrap_or_default();
    if prefix.is_empty()
        || !prefix
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
    {
        return UNKNOWN_SERVICE.to_string();
    }
    let prefix = prefix.to_ascii_lowercase();
    if cache_utils::active_key_scheme() != cache_utils::CacheKeyScheme::BareMetal {
        return prefix;
    }
    // Bare metal keys carry the nginx vhost, not the name this app files the traffic under.
    let Some(vhost) = prefix.strip_prefix("lancache-") else {
        return prefix;
    };
    if vhost == "windows-update" {
        return "wsus".to_string();
    }
    vhost.to_string()
}

/// Walks `cache_root`, keeps every file the claimed set does not account for, then reads each
/// survivor's `KEY:` header to recover the URL it was cached from.
pub fn scan(
    cache_root: &Path,
    claimed_digests_path: &Path,
    progress_path: Option<&Path>,
    scan_started_utc: &str,
) -> Result<UnmappedReport> {
    let claimed = load_claimed_digests(claimed_digests_path)?;

    let mut orphans: Vec<(u128, u64)> = Vec::new();
    let mut files_on_disk = 0usize;
    let mut skipped_non_hash_names = 0usize;
    let mut cancelled = false;

    // The tree size is unknown until the walk ends, so this stage carries a live count and a
    // hard zero percent instead of a division that has no denominator yet.
    write_progress(
        progress_path,
        "starting",
        ENUMERATING_STAGE,
        serde_json::json!({ "count": 0 }),
        0.0,
        0,
        0,
    )?;
    let mut last_emit = Instant::now();

    for entry in cache_utils::walk_cache_root(cache_root) {
        if cancel::is_cancelled() {
            cancelled = true;
            break;
        }
        let path = entry.path();
        let Some(digest) = cache_utils::strict_cache_file_digest(cache_root, &path) else {
            skipped_non_hash_names += 1;
            continue;
        };
        files_on_disk += 1;
        if !claimed.contains(&digest) {
            let size_bytes = entry.metadata().map(|metadata| metadata.len()).unwrap_or(0);
            orphans.push((digest, size_bytes));
        }
        if last_emit.elapsed() >= PROGRESS_INTERVAL {
            last_emit = Instant::now();
            write_progress(
                progress_path,
                "processing",
                ENUMERATING_STAGE,
                serde_json::json!({ "count": files_on_disk }),
                0.0,
                files_on_disk,
                0,
            )?;
        }
    }

    let orphan_count = orphans.len();
    let mut unreadable_keys = 0usize;
    let mut orphan_bytes = 0u64;
    let mut by_service: BTreeMap<String, Vec<UnmappedFile>> = BTreeMap::new();

    for (index, (digest, size_bytes)) in orphans.into_iter().enumerate() {
        if cancel::is_cancelled() {
            cancelled = true;
            break;
        }
        let path = cache_utils::cache_path_for_digest(cache_root, digest);
        let url = cache_utils::read_cache_file_key(&path);
        if url.is_none() {
            unreadable_keys += 1;
        }
        let service = service_from_key(url.as_deref());
        orphan_bytes = orphan_bytes.saturating_add(size_bytes);
        by_service.entry(service).or_default().push(UnmappedFile {
            digest: format!("{digest:032x}"),
            path: path.display().to_string(),
            url,
            size_bytes,
        });
        if last_emit.elapsed() >= PROGRESS_INTERVAL {
            last_emit = Instant::now();
            let processed = index + 1;
            write_progress(
                progress_path,
                "processing",
                READING_KEYS_STAGE,
                serde_json::json!({ "count": processed, "total": orphan_count }),
                (processed as f64 / orphan_count.max(1) as f64) * 100.0,
                processed,
                orphan_count,
            )?;
        }
    }

    let services = by_service
        .into_iter()
        .map(|(service, files)| UnmappedService {
            service,
            file_count: files.len(),
            total_bytes: files.iter().map(|file| file.size_bytes).sum(),
            files,
        })
        .collect::<Vec<_>>();
    let reported_orphans = services.iter().map(|group| group.file_count).sum();

    write_progress(
        progress_path,
        "completed",
        SCAN_COMPLETE_STAGE,
        serde_json::json!({ "count": reported_orphans, "bytes": orphan_bytes }),
        100.0,
        reported_orphans,
        reported_orphans,
    )?;

    Ok(UnmappedReport {
        contract_version: UNMAPPED_CONTRACT_VERSION,
        cancelled,
        scan_started_utc: scan_started_utc.to_string(),
        cache_root: cache_root.display().to_string(),
        files_on_disk,
        claimed_digests: claimed.len(),
        skipped_non_hash_names,
        orphan_count: reported_orphans,
        orphan_bytes,
        unreadable_keys,
        services,
    })
}

fn load_removal_evidence(evidence_path: &Path) -> Result<Vec<PathBuf>> {
    let raw = std::fs::read_to_string(evidence_path)
        .with_context(|| format!("failed to read evidence file {}", evidence_path.display()))?;
    let evidence: RemovalEvidence = serde_json::from_str(&raw)
        .with_context(|| format!("failed to parse evidence file {}", evidence_path.display()))?;
    if evidence.contract_version != UNMAPPED_CONTRACT_VERSION {
        bail!(
            "evidence contract version {} is not the supported {}",
            evidence.contract_version,
            UNMAPPED_CONTRACT_VERSION
        );
    }
    if evidence.paths.is_empty() {
        bail!("evidence file selected no paths");
    }
    let paths = evidence.paths.iter().map(PathBuf::from).collect::<Vec<_>>();
    if paths.iter().collect::<HashSet<_>>().len() != paths.len() {
        bail!("evidence file lists the same path more than once");
    }
    Ok(paths)
}

/// Deletes the selected paths, refusing anything the freshly re-read claimed set now accounts
/// for. `claimed_digests_path` must be the list the host regenerated for THIS removal: that
/// re-read is what stops a file attributed by a detection run since the scan from being lost.
pub fn remove(
    cache_root: &Path,
    claimed_digests_path: &Path,
    progress_path: Option<&Path>,
    evidence_path: &Path,
) -> Result<UnmappedRemovalReport> {
    let paths = load_removal_evidence(evidence_path)?;
    let claimed = load_claimed_digests(claimed_digests_path)?;

    // Nothing is unlinked until every selected path has cleared this pass, so a bad list fails
    // the whole removal rather than half of it.
    let mut claimed_since_scan = 0usize;
    let mut ready = Vec::with_capacity(paths.len());
    for path in paths {
        let Some(digest) = cache_utils::strict_cache_file_digest(cache_root, &path) else {
            bail!(
                "selected path is not a cache file under {}: {}",
                cache_root.display(),
                path.display()
            );
        };
        if claimed.contains(&digest) {
            claimed_since_scan += 1;
            continue;
        }
        ready.push(path);
    }

    let total_files = ready.len();
    let mut report = UnmappedRemovalReport {
        contract_version: UNMAPPED_CONTRACT_VERSION,
        cancelled: false,
        deleted_files: 0,
        already_missing: 0,
        claimed_since_scan,
        bytes_freed: 0,
    };
    let mut parent_dirs = HashSet::new();

    write_progress(
        progress_path,
        "starting",
        REMOVING_STAGE,
        serde_json::json!({ "count": 0, "total": total_files }),
        0.0,
        0,
        total_files,
    )?;
    let mut last_emit = Instant::now();

    for (index, path) in ready.iter().enumerate() {
        if cancel::is_cancelled() {
            report.cancelled = true;
            break;
        }
        let size_bytes = match std::fs::metadata(path) {
            Ok(metadata) => metadata.len(),
            Err(error) if error.kind() == ErrorKind::NotFound => {
                report.already_missing += 1;
                continue;
            }
            Err(error) => {
                return Err(error)
                    .with_context(|| format!("failed to stat cache file {}", path.display()))
            }
        };
        cache_utils::safe_path_under_root(cache_root, path)
            .with_context(|| format!("unsafe unmapped cache path {}", path.display()))?;
        std::fs::remove_file(path)
            .with_context(|| format!("failed to delete cache file {}", path.display()))?;
        report.deleted_files += 1;
        report.bytes_freed = report.bytes_freed.saturating_add(size_bytes);
        if let Some(parent) = path.parent() {
            parent_dirs.insert(parent.to_path_buf());
        }
        if last_emit.elapsed() >= PROGRESS_INTERVAL {
            last_emit = Instant::now();
            let processed = index + 1;
            write_progress(
                progress_path,
                "processing",
                REMOVING_STAGE,
                serde_json::json!({ "count": processed, "total": total_files }),
                (processed as f64 / total_files.max(1) as f64) * 100.0,
                processed,
                total_files,
            )?;
        }
    }

    cache_utils::cleanup_empty_directories(cache_root, parent_dirs);
    write_progress(
        progress_path,
        "completed",
        REMOVE_COMPLETE_STAGE,
        serde_json::json!({ "count": report.deleted_files, "bytes": report.bytes_freed }),
        100.0,
        report.deleted_files,
        total_files,
    )?;
    Ok(report)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    /// The host writes its digest list and evidence file into its operations directory, so the
    /// tree these tests walk holds cache files and nothing else.
    fn cache_root(temp: &tempfile::TempDir) -> PathBuf {
        let root = temp.path().join("cache");
        std::fs::create_dir_all(&root).unwrap();
        root
    }

    fn write_cache_file(root: &Path, digest: u128, key: &str, body: &str) -> PathBuf {
        let path = cache_utils::cache_path_for_digest(root, digest);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut file = std::fs::File::create(&path).unwrap();
        write!(file, "KEY: {key}\n{body}").unwrap();
        path
    }

    fn write_claimed(dir: &Path, digests: &[u128]) -> PathBuf {
        let path = dir.join("claimed.txt");
        let body = digests
            .iter()
            .map(|digest| format!("{digest:032x}"))
            .collect::<Vec<_>>()
            .join("\n");
        std::fs::write(&path, body).unwrap();
        path
    }

    fn write_evidence(dir: &Path, contract_version: u32, paths: &[&Path]) -> PathBuf {
        let path = dir.join("evidence.json");
        let quoted = paths
            .iter()
            .map(|entry| serde_json::to_string(&entry.display().to_string()).unwrap())
            .collect::<Vec<_>>()
            .join(",");
        std::fs::write(
            &path,
            format!("{{\"contract_version\":{contract_version},\"paths\":[{quoted}]}}"),
        )
        .unwrap();
        path
    }

    #[test]
    fn scan_reports_only_the_digests_the_claimed_set_is_missing() {
        let temp = tempfile::tempdir().unwrap();
        let root = cache_root(&temp);
        write_cache_file(&root, 1, "steam/depot/1/chunk/a", "body-a");
        write_cache_file(&root, 2, "steam/depot/2/chunk/b", "body-bb");
        write_cache_file(&root, 3, "epicgames/Builds/c", "body-ccc");
        let claimed = write_claimed(temp.path(), &[2]);

        let report = scan(&root, &claimed, None, "2026-08-27T11:00:00+00:00").unwrap();

        assert_eq!(report.contract_version, UNMAPPED_CONTRACT_VERSION);
        assert_eq!(report.scan_started_utc, "2026-08-27T11:00:00+00:00");
        assert_eq!(report.files_on_disk, 3);
        assert_eq!(report.claimed_digests, 1);
        assert_eq!(report.orphan_count, 2);
        assert_eq!(report.unreadable_keys, 0);
        let services = report
            .services
            .iter()
            .map(|group| group.service.as_str())
            .collect::<Vec<_>>();
        assert_eq!(services, vec!["epicgames", "steam"]);
        let steam = report
            .services
            .iter()
            .find(|group| group.service == "steam")
            .unwrap();
        assert_eq!(steam.file_count, 1);
        assert_eq!(steam.files[0].digest, "00000000000000000000000000000001");
        assert_eq!(steam.files[0].url.as_deref(), Some("steam/depot/1/chunk/a"));
        assert_eq!(steam.files[0].size_bytes, steam.total_bytes);
        assert_eq!(
            report.orphan_bytes,
            report
                .services
                .iter()
                .map(|group| group.total_bytes)
                .sum::<u64>()
        );
    }

    #[test]
    fn scan_skips_names_that_are_not_the_strict_cache_layout() {
        let temp = tempfile::tempdir().unwrap();
        let root = cache_root(&temp);
        write_cache_file(&root, 1, "steam/depot/1", "body");
        std::fs::write(root.join("01").join("00").join("nginx-temp"), "x").unwrap();
        let claimed = write_claimed(temp.path(), &[]);

        let report = scan(&root, &claimed, None, "anchor").unwrap();

        assert_eq!(report.files_on_disk, 1);
        assert_eq!(report.skipped_non_hash_names, 1);
        assert_eq!(report.orphan_count, 1);
    }

    #[test]
    fn scan_groups_a_file_with_no_readable_key_under_unknown() {
        let temp = tempfile::tempdir().unwrap();
        let root = cache_root(&temp);
        let path = cache_utils::cache_path_for_digest(&root, 1);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, "no key header here").unwrap();
        let claimed = write_claimed(temp.path(), &[]);

        let report = scan(&root, &claimed, None, "anchor").unwrap();

        assert_eq!(report.unreadable_keys, 1);
        assert_eq!(report.services[0].service, UNKNOWN_SERVICE);
        assert_eq!(report.services[0].files[0].url, None);
    }

    #[test]
    fn load_claimed_digests_refuses_a_line_that_is_not_lowercase_hex() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("claimed.txt");
        std::fs::write(&path, "0000000000000000000000000000000A\n").unwrap();

        let error = load_claimed_digests(&path).unwrap_err();

        assert!(error.to_string().contains("line 1"), "{error:#}");
    }

    #[test]
    fn remove_refuses_a_file_that_became_claimed_since_the_scan() {
        let temp = tempfile::tempdir().unwrap();
        let root = cache_root(&temp);
        let kept = write_cache_file(&root, 1, "steam/depot/1", "body-a");
        let removed = write_cache_file(&root, 2, "steam/depot/2", "body-bb");
        // The host regenerates this list at delete time, and digest 1 was attributed since.
        let claimed = write_claimed(temp.path(), &[1]);
        let evidence = write_evidence(
            temp.path(),
            UNMAPPED_CONTRACT_VERSION,
            &[kept.as_path(), removed.as_path()],
        );

        let report = remove(&root, &claimed, None, &evidence).unwrap();

        assert_eq!(report.claimed_since_scan, 1);
        assert_eq!(report.deleted_files, 1);
        assert!(kept.exists(), "a re-claimed file must survive the removal");
        assert!(!removed.exists());
    }

    #[test]
    fn remove_refuses_a_path_outside_the_cache_root() {
        let temp = tempfile::tempdir().unwrap();
        let root = cache_root(&temp);
        let inside = write_cache_file(&root, 1, "steam/depot/1", "body-a");
        let outside = temp.path().join("secrets.log");
        std::fs::write(&outside, "keep me").unwrap();
        let claimed = write_claimed(temp.path(), &[]);
        let evidence = write_evidence(
            temp.path(),
            UNMAPPED_CONTRACT_VERSION,
            &[inside.as_path(), outside.as_path()],
        );

        let error = remove(&root, &claimed, None, &evidence).unwrap_err();

        assert!(error.to_string().contains("not a cache file"), "{error:#}");
        assert!(outside.exists());
        assert!(
            inside.exists(),
            "no file may be unlinked before the whole list has cleared preflight"
        );
    }

    #[test]
    fn remove_counts_a_file_that_vanished_before_the_unlink() {
        let temp = tempfile::tempdir().unwrap();
        let root = cache_root(&temp);
        let gone = cache_utils::cache_path_for_digest(&root, 1);
        let present = write_cache_file(&root, 2, "steam/depot/2", "body-bb");
        let claimed = write_claimed(temp.path(), &[]);
        let evidence = write_evidence(
            temp.path(),
            UNMAPPED_CONTRACT_VERSION,
            &[gone.as_path(), present.as_path()],
        );

        let report = remove(&root, &claimed, None, &evidence).unwrap();

        assert_eq!(report.already_missing, 1);
        assert_eq!(report.deleted_files, 1);
        assert_eq!(report.bytes_freed, 7 + "KEY: steam/depot/2\n".len() as u64);
    }

    #[test]
    fn remove_refuses_an_evidence_file_on_another_contract_version() {
        let temp = tempfile::tempdir().unwrap();
        let root = cache_root(&temp);
        let path = write_cache_file(&root, 1, "steam/depot/1", "body");
        let claimed = write_claimed(temp.path(), &[]);
        let evidence = write_evidence(
            temp.path(),
            UNMAPPED_CONTRACT_VERSION + 1,
            &[path.as_path()],
        );

        let error = remove(&root, &claimed, None, &evidence).unwrap_err();

        assert!(error.to_string().contains("contract version"), "{error:#}");
        assert!(path.exists());
    }

    #[test]
    fn service_from_key_falls_back_to_unknown_for_an_unusable_prefix() {
        assert_eq!(service_from_key(None), UNKNOWN_SERVICE);
        assert_eq!(service_from_key(Some("/leading-slash")), UNKNOWN_SERVICE);
        assert_eq!(service_from_key(Some("bad prefix/x")), UNKNOWN_SERVICE);
        assert_eq!(service_from_key(Some("STEAM/depot/1")), "steam");
    }
}
