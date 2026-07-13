use crate::cache_corruption_detector::{
    CorruptionCandidate, CorruptionEvidence, FileFingerprint, CORRUPTION_CONTRACT_VERSION,
};
use anyhow::{bail, Context, Result};
use clap::ValueEnum;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use sqlx::sqlite::{SqliteConnectOptions, SqliteJournalMode, SqliteSynchronous};
use sqlx::{Connection, QueryBuilder, Row, Sqlite, SqliteConnection};
use std::collections::HashMap;
use std::path::Path;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tokio::runtime::{Builder, Runtime};
use uuid::Uuid;

pub const STRUCTURAL_STATE_FORMAT_VERSION: u32 = 1;
pub const STATE_BATCH_SIZE: usize = 1_024;
const LOOKUP_BATCH_SIZE: usize = 500;
const LEASE_TIMEOUT_SECONDS: i64 = 300;
const HEARTBEAT_INTERVAL_SECONDS: i64 = 30;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, ValueEnum)]
#[serde(rename_all = "snake_case")]
pub enum StructuralScanMode {
    Full,
    Incremental,
}

impl StructuralScanMode {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Full => "full",
            Self::Incremental => "incremental",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EffectiveScanMode {
    Full,
    Incremental,
    Baseline,
}

impl EffectiveScanMode {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Full => "full",
            Self::Incremental => "incremental",
            Self::Baseline => "baseline",
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StructuralScanSummary {
    pub scan_mode: String,
    pub effective_scan_mode: String,
    pub baseline_status: String,
    pub resumed: bool,
    pub files_discovered: usize,
    pub files_processed: usize,
    pub files_reused: usize,
    pub files_inspected: usize,
    pub files_revalidated: usize,
    pub invalid_files: usize,
    pub files_pending_retry: usize,
    pub files_pruned: usize,
    pub state_entries: usize,
    pub state_committed: bool,
}

impl StructuralScanSummary {
    pub fn stateless_full() -> Self {
        Self {
            scan_mode: StructuralScanMode::Full.as_str().to_string(),
            effective_scan_mode: EffectiveScanMode::Full.as_str().to_string(),
            baseline_status: "stateless".to_string(),
            resumed: false,
            files_discovered: 0,
            files_processed: 0,
            files_reused: 0,
            files_inspected: 0,
            files_revalidated: 0,
            invalid_files: 0,
            files_pending_retry: 0,
            files_pruned: 0,
            state_entries: 0,
            state_committed: false,
        }
    }
}

#[derive(Debug, Clone)]
pub struct StateNamespace {
    pub canonical_root_identity: String,
    pub root_fingerprint: FileFingerprint,
    pub scope: String,
    pub layout_signature: String,
    pub scanner_policy_version: u32,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct NamespaceDocument<'a> {
    state_format_version: u32,
    report_contract_version: u32,
    scanner_policy_version: u32,
    canonical_root_identity: &'a str,
    root_fingerprint: &'a FileFingerprint,
    scope: &'a str,
    layout_signature: &'a str,
    os: &'static str,
    architecture: &'static str,
}

#[derive(Debug, Clone)]
pub struct LookupInput {
    pub digest: u128,
    pub fingerprint: Option<FileFingerprint>,
}

#[derive(Debug)]
pub enum ReuseDecision {
    Inspect,
    ReuseConsistent,
    Revalidate(Box<CorruptionCandidate>),
}

#[derive(Debug, Clone, Copy)]
pub enum SuccessfulOutcome<'a> {
    Consistent,
    Proven(&'a CorruptionCandidate),
}

#[derive(Debug)]
struct StoredRow {
    generation: String,
    fingerprint: FileFingerprint,
    outcome: i64,
    candidate: Option<CorruptionCandidate>,
}

#[derive(Debug)]
struct PendingWrite {
    digest: u128,
    fingerprint: FileFingerprint,
    outcome: i64,
    candidate_json: Option<String>,
}

pub struct StructuralState {
    runtime: Runtime,
    connection: SqliteConnection,
    namespace_hash: String,
    namespace_json: String,
    active_generation: Option<String>,
    staging_generation: String,
    scan_epoch: String,
    requested_mode: StructuralScanMode,
    effective_mode: EffectiveScanMode,
    resumed: bool,
    last_heartbeat_at: i64,
    pending: Vec<PendingWrite>,
    pending_deletes: Vec<u128>,
    finished: bool,
}

impl StructuralState {
    pub fn open(path: &Path, namespace: StateNamespace, mode: StructuralScanMode) -> Result<Self> {
        validate_state_path(path)?;
        let namespace_json = serde_json::to_string(&NamespaceDocument {
            state_format_version: STRUCTURAL_STATE_FORMAT_VERSION,
            report_contract_version: CORRUPTION_CONTRACT_VERSION,
            scanner_policy_version: namespace.scanner_policy_version,
            canonical_root_identity: &namespace.canonical_root_identity,
            root_fingerprint: &namespace.root_fingerprint,
            scope: &namespace.scope,
            layout_signature: &namespace.layout_signature,
            os: std::env::consts::OS,
            architecture: std::env::consts::ARCH,
        })
        .context("failed to serialize structural state namespace")?;
        let namespace_hash = format!("{:x}", Sha256::digest(namespace_json.as_bytes()));
        let runtime = Builder::new_current_thread()
            .enable_all()
            .build()
            .context("failed to create structural state runtime")?;
        let options = SqliteConnectOptions::new()
            .filename(path)
            .create_if_missing(true)
            .foreign_keys(true)
            .journal_mode(SqliteJournalMode::Wal)
            .synchronous(SqliteSynchronous::Full)
            .busy_timeout(Duration::from_secs(30));
        let mut connection = runtime
            .block_on(SqliteConnection::connect_with(&options))
            .with_context(|| {
                format!(
                    "failed to open structural state database {}",
                    path.display()
                )
            })?;
        runtime.block_on(initialize_schema(&mut connection))?;

        let now = unix_timestamp()?;
        let mut transaction = runtime
            .block_on(connection.begin())
            .context("failed to begin structural state setup transaction")?;
        let existing_descriptor = runtime.block_on(
            sqlx::query(
                "SELECT namespace_json FROM structural_namespaces WHERE namespace_hash = ?",
            )
            .bind(&namespace_hash)
            .fetch_optional(&mut *transaction),
        )?;
        if let Some(row) = existing_descriptor {
            let stored: String = row.try_get("namespace_json")?;
            if stored != namespace_json {
                bail!("structural state namespace hash collision");
            }
        } else {
            runtime.block_on(
                sqlx::query(
                    "INSERT INTO structural_namespaces(namespace_hash, namespace_json) VALUES(?, ?)",
                )
                .bind(&namespace_hash)
                .bind(&namespace_json)
                .execute(&mut *transaction),
            )?;
        }
        let active_generation = runtime
            .block_on(
                sqlx::query(
                    "SELECT active_generation FROM structural_namespaces WHERE namespace_hash = ?",
                )
                .bind(&namespace_hash)
                .fetch_one(&mut *transaction),
            )?
            .try_get::<Option<String>, _>("active_generation")?;

        let fresh_lease = runtime.block_on(
            sqlx::query(
                "SELECT generation FROM structural_runs \
                 WHERE status = 'running' AND heartbeat_at >= ? LIMIT 1",
            )
            .bind(now.saturating_sub(LEASE_TIMEOUT_SECONDS))
            .fetch_optional(&mut *transaction),
        )?;
        if fresh_lease.is_some() {
            bail!("another structural state scan holds a live lease for this namespace");
        }
        runtime.block_on(
            sqlx::query(
                "UPDATE structural_runs SET status = 'interrupted' WHERE status = 'running'",
            )
            .execute(&mut *transaction),
        )?;

        let resumable = if mode == StructuralScanMode::Incremental {
            runtime.block_on(
                sqlx::query(
                    "SELECT generation, effective_mode FROM structural_runs \
                     WHERE namespace_hash = ? AND requested_mode = 'incremental' \
                       AND status = 'interrupted' ORDER BY started_at DESC LIMIT 1",
                )
                .bind(&namespace_hash)
                .fetch_optional(&mut *transaction),
            )?
        } else {
            None
        };
        let resumed = resumable.is_some();
        let (staging_generation, effective_mode) = if let Some(row) = resumable {
            let effective: String = row.try_get("effective_mode")?;
            (
                row.try_get("generation")?,
                parse_effective_mode(&effective)?,
            )
        } else {
            let effective = match mode {
                StructuralScanMode::Full => EffectiveScanMode::Full,
                StructuralScanMode::Incremental if active_generation.is_some() => {
                    EffectiveScanMode::Incremental
                }
                StructuralScanMode::Incremental => EffectiveScanMode::Baseline,
            };
            let generation = Uuid::new_v4().to_string();
            runtime.block_on(
                sqlx::query(
                    "INSERT INTO structural_runs( \
                        generation, namespace_hash, requested_mode, effective_mode, status, \
                        started_at, heartbeat_at, enumeration_complete \
                     ) VALUES(?, ?, ?, ?, 'running', ?, ?, 0)",
                )
                .bind(&generation)
                .bind(&namespace_hash)
                .bind(mode.as_str())
                .bind(effective.as_str())
                .bind(now)
                .bind(now)
                .execute(&mut *transaction),
            )?;
            (generation, effective)
        };
        if resumed {
            runtime.block_on(
                sqlx::query(
                    "UPDATE structural_runs SET status = 'running', heartbeat_at = ? WHERE generation = ?",
                )
                .bind(now)
                .bind(&staging_generation)
                .execute(&mut *transaction),
            )?;
        }
        runtime.block_on(transaction.commit())?;

        let mut state = Self {
            runtime,
            connection,
            namespace_hash,
            namespace_json,
            active_generation,
            staging_generation,
            scan_epoch: Uuid::new_v4().to_string(),
            requested_mode: mode,
            effective_mode,
            resumed,
            last_heartbeat_at: now,
            pending: Vec::with_capacity(STATE_BATCH_SIZE),
            pending_deletes: Vec::with_capacity(STATE_BATCH_SIZE),
            finished: false,
        };
        if !resumed {
            state.cleanup_abandoned_staging()?;
        }
        Ok(state)
    }

    pub fn effective_mode(&self) -> EffectiveScanMode {
        self.effective_mode
    }

    pub fn resumed(&self) -> bool {
        self.resumed
    }

    /// Keeps the single-writer lease fresh during traversal phases that do not otherwise touch
    /// SQLite. Without this, a long enumeration can look stale while the owning scan is healthy.
    pub fn maintain_lease(&mut self) -> Result<()> {
        self.heartbeat_if_due()
    }

    /// A fresh Full or first-time baseline has no row that can be reused. Skipping classification
    /// avoids an extra metadata walk for every file before the mandatory header inspection.
    pub fn can_reuse_existing(&self) -> bool {
        self.requested_mode == StructuralScanMode::Incremental
            && (self.active_generation.is_some() || self.resumed)
    }

    pub fn lookup_batch(&mut self, inputs: &[LookupInput]) -> Result<Vec<ReuseDecision>> {
        if self.requested_mode != StructuralScanMode::Incremental {
            self.heartbeat_if_due()?;
            return Ok(inputs.iter().map(|_| ReuseDecision::Inspect).collect());
        }
        let mut rows = HashMap::<(String, u128), StoredRow>::new();
        for chunk in inputs.chunks(LOOKUP_BATCH_SIZE) {
            let mut query = QueryBuilder::<Sqlite>::new(
                "SELECT generation, digest, dev, ino, len, mtime_ns, ctime_ns, outcome, candidate_json \
                 FROM structural_file_state WHERE namespace_hash = ",
            );
            query.push_bind(&self.namespace_hash);
            query.push(" AND generation IN (");
            {
                let mut separated = query.separated(", ");
                separated.push_bind(&self.staging_generation);
                if let Some(active) = &self.active_generation {
                    separated.push_bind(active);
                }
            }
            query.push(") AND digest IN (");
            {
                let mut separated = query.separated(", ");
                for input in chunk {
                    separated.push_bind(input.digest.to_be_bytes().to_vec());
                }
            }
            query.push(")");
            let runtime = &self.runtime;
            let result = runtime.block_on(query.build().fetch_all(&mut self.connection))?;
            for row in result {
                let generation: String = row.try_get("generation")?;
                let digest_bytes: Vec<u8> = row.try_get("digest")?;
                let digest = decode_digest(&digest_bytes)?;
                let candidate_json: Option<String> = row.try_get("candidate_json")?;
                let candidate = candidate_json
                    .as_deref()
                    .map(serde_json::from_str)
                    .transpose()
                    .context("failed to deserialize persisted structural candidate")?;
                rows.insert(
                    (generation.clone(), digest),
                    StoredRow {
                        generation,
                        fingerprint: fingerprint_from_row(&row)?,
                        outcome: row.try_get("outcome")?,
                        candidate,
                    },
                );
            }
        }

        let mut decisions = Vec::with_capacity(inputs.len());
        for input in inputs {
            let staging_key = (self.staging_generation.clone(), input.digest);
            let staging_row = rows.get(&staging_key);
            let Some(current) = &input.fingerprint else {
                if staging_row.is_some() {
                    self.queue_delete(input.digest);
                }
                decisions.push(ReuseDecision::Inspect);
                continue;
            };
            let active_key = self
                .active_generation
                .as_ref()
                .map(|generation| (generation.clone(), input.digest));
            let stored = if let Some(staging) = staging_row {
                if staging.fingerprint != *current {
                    self.queue_delete(input.digest);
                    None
                } else {
                    Some(staging)
                }
            } else {
                active_key.as_ref().and_then(|key| rows.get(key))
            };
            let Some(stored) = stored.filter(|row| row.fingerprint == *current) else {
                decisions.push(ReuseDecision::Inspect);
                continue;
            };
            match stored.outcome {
                0 => {
                    // Rewrite even a staging hit so this resume epoch proves the file was seen.
                    self.queue_write(input.digest, current.clone(), 0, None)?;
                    decisions.push(ReuseDecision::ReuseConsistent);
                }
                1 => {
                    let candidate = stored
                        .candidate
                        .clone()
                        .context("persisted proven outcome omitted its candidate")?;
                    validate_candidate_fingerprint(&candidate, current)?;
                    if stored.generation == self.staging_generation {
                        self.queue_delete(input.digest);
                    }
                    decisions.push(ReuseDecision::Revalidate(Box::new(candidate)));
                }
                other => bail!("persisted structural state used unknown outcome {other}"),
            }
        }
        self.flush_if_full()?;
        self.heartbeat_if_due()?;
        Ok(decisions)
    }

    pub fn record_success(
        &mut self,
        digest: u128,
        fingerprint: FileFingerprint,
        outcome: SuccessfulOutcome<'_>,
    ) -> Result<()> {
        let (outcome, candidate_json) = match outcome {
            SuccessfulOutcome::Consistent => (0, None),
            SuccessfulOutcome::Proven(candidate) => {
                validate_candidate_fingerprint(candidate, &fingerprint)?;
                (
                    1,
                    Some(
                        serde_json::to_string(candidate)
                            .context("failed to serialize structural candidate for state")?,
                    ),
                )
            }
        };
        self.queue_write(digest, fingerprint, outcome, candidate_json)?;
        self.flush_if_full()?;
        self.heartbeat_if_due()
    }

    pub fn interrupt(&mut self) -> Result<()> {
        self.flush()?;
        let now = unix_timestamp()?;
        let affected = self.runtime.block_on(
            sqlx::query(
                "UPDATE structural_runs SET status = 'interrupted', heartbeat_at = ? WHERE generation = ?",
            )
            .bind(now)
            .bind(&self.staging_generation)
            .execute(&mut self.connection),
        )?;
        if affected.rows_affected() != 1 {
            bail!("structural state interruption lost its staging run");
        }
        self.finished = true;
        self.checkpoint(false)
    }

    pub fn publish(&mut self) -> Result<(usize, usize)> {
        self.flush()?;
        self.delete_unseen_staging_bounded()?;
        let staging_generation = self.staging_generation.clone();
        let new_count = self.count_generation(&staging_generation)?;
        let pruned = match self.active_generation.clone() {
            Some(active) => self.count_pruned(&active, &staging_generation)?,
            None => 0,
        };
        let now = unix_timestamp()?;
        let mut transaction = self.runtime.block_on(self.connection.begin())?;
        let run_update = self.runtime.block_on(
            sqlx::query(
                "UPDATE structural_runs SET status = 'complete', enumeration_complete = 1, heartbeat_at = ? \
                 WHERE generation = ?",
            )
            .bind(now)
            .bind(&self.staging_generation)
            .execute(&mut *transaction),
        )?;
        if run_update.rows_affected() != 1 {
            bail!("structural state publication lost its staging run");
        }
        let namespace_update = self.runtime.block_on(
            sqlx::query(
                "UPDATE structural_namespaces SET active_generation = ? WHERE namespace_hash = ? AND namespace_json = ?",
            )
            .bind(&self.staging_generation)
            .bind(&self.namespace_hash)
            .bind(&self.namespace_json)
            .execute(&mut *transaction),
        )?;
        if namespace_update.rows_affected() != 1 {
            bail!("structural state publication lost its namespace");
        }
        self.runtime.block_on(transaction.commit())?;
        let old_active = self
            .active_generation
            .replace(self.staging_generation.clone());
        self.finished = true;
        if let Err(error) = self.checkpoint(true) {
            eprintln!(
                "WARNING: structural state WAL checkpoint failed after publication: {error:#}"
            );
        }
        if let Some(old) = old_active {
            if let Err(error) = self.delete_generation_bounded(&old) {
                eprintln!("WARNING: structural state old-generation cleanup failed: {error:#}");
            }
        }
        if let Err(error) = self.cleanup_other_namespaces_bounded() {
            eprintln!("WARNING: structural state incompatible-namespace cleanup failed: {error:#}");
        }
        Ok((pruned, new_count))
    }

    fn queue_write(
        &mut self,
        digest: u128,
        fingerprint: FileFingerprint,
        outcome: i64,
        candidate_json: Option<String>,
    ) -> Result<()> {
        if fingerprint.len > i64::MAX as u64 {
            bail!("structural state cannot represent a file length above i64::MAX");
        }
        self.pending.push(PendingWrite {
            digest,
            fingerprint,
            outcome,
            candidate_json,
        });
        Ok(())
    }

    fn queue_delete(&mut self, digest: u128) {
        self.pending_deletes.push(digest);
    }

    fn flush_if_full(&mut self) -> Result<()> {
        if self
            .pending
            .len()
            .saturating_add(self.pending_deletes.len())
            >= STATE_BATCH_SIZE
        {
            self.flush()
        } else {
            Ok(())
        }
    }

    fn flush(&mut self) -> Result<()> {
        if self.pending.is_empty() && self.pending_deletes.is_empty() {
            return Ok(());
        }
        let pending = std::mem::take(&mut self.pending);
        let pending_deletes = std::mem::take(&mut self.pending_deletes);
        let now = unix_timestamp()?;
        let mut transaction = self.runtime.block_on(self.connection.begin())?;
        for digest in pending_deletes {
            self.runtime.block_on(
                sqlx::query(
                    "DELETE FROM structural_file_state \
                     WHERE namespace_hash = ? AND generation = ? AND digest = ?",
                )
                .bind(&self.namespace_hash)
                .bind(&self.staging_generation)
                .bind(digest.to_be_bytes().to_vec())
                .execute(&mut *transaction),
            )?;
        }
        for write in pending {
            self.runtime.block_on(
                sqlx::query(
                    "INSERT INTO structural_file_state( \
                        namespace_hash, generation, digest, dev, ino, len, mtime_ns, ctime_ns, outcome, candidate_json, seen_epoch \
                     ) VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) \
                     ON CONFLICT(namespace_hash, generation, digest) DO UPDATE SET \
                        dev=excluded.dev, ino=excluded.ino, len=excluded.len, \
                        mtime_ns=excluded.mtime_ns, ctime_ns=excluded.ctime_ns, \
                        outcome=excluded.outcome, candidate_json=excluded.candidate_json, \
                        seen_epoch=excluded.seen_epoch",
                )
                .bind(&self.namespace_hash)
                .bind(&self.staging_generation)
                .bind(write.digest.to_be_bytes().to_vec())
                .bind(write.fingerprint.dev as i64)
                .bind(write.fingerprint.ino as i64)
                .bind(write.fingerprint.len as i64)
                .bind(write.fingerprint.mtime_ns)
                .bind(write.fingerprint.ctime_ns)
                .bind(write.outcome)
                .bind(write.candidate_json)
                .bind(&self.scan_epoch)
                .execute(&mut *transaction),
            )?;
        }
        self.runtime.block_on(
            sqlx::query("UPDATE structural_runs SET heartbeat_at = ? WHERE generation = ?")
                .bind(now)
                .bind(&self.staging_generation)
                .execute(&mut *transaction),
        )?;
        self.runtime.block_on(transaction.commit())?;
        self.last_heartbeat_at = now;
        Ok(())
    }

    fn heartbeat(&mut self) -> Result<()> {
        let now = unix_timestamp()?;
        let affected = self.runtime.block_on(
            sqlx::query("UPDATE structural_runs SET heartbeat_at = ? WHERE generation = ?")
                .bind(now)
                .bind(&self.staging_generation)
                .execute(&mut self.connection),
        )?;
        if affected.rows_affected() != 1 {
            bail!("structural state heartbeat lost its staging run");
        }
        self.last_heartbeat_at = now;
        Ok(())
    }

    fn count_generation(&mut self, generation: &str) -> Result<usize> {
        let count: i64 = self.runtime.block_on(
            sqlx::query_scalar(
                "SELECT COUNT(*) FROM structural_file_state WHERE namespace_hash = ? AND generation = ?",
            )
            .bind(&self.namespace_hash)
            .bind(generation)
            .fetch_one(&mut self.connection),
        )?;
        usize::try_from(count).context("structural state entry count overflowed usize")
    }

    fn count_pruned(&mut self, old: &str, new: &str) -> Result<usize> {
        let count: i64 = self.runtime.block_on(
            sqlx::query_scalar(
                "SELECT COUNT(*) FROM structural_file_state old \
                 WHERE old.namespace_hash = ? AND old.generation = ? \
                   AND NOT EXISTS(SELECT 1 FROM structural_file_state new \
                     WHERE new.namespace_hash = old.namespace_hash AND new.generation = ? AND new.digest = old.digest)",
            )
            .bind(&self.namespace_hash)
            .bind(old)
            .bind(new)
            .fetch_one(&mut self.connection),
        )?;
        usize::try_from(count).context("structural pruned count overflowed usize")
    }

    fn cleanup_abandoned_staging(&mut self) -> Result<()> {
        loop {
            let affected = self.runtime.block_on(
                sqlx::query(
                    "DELETE FROM structural_file_state WHERE (namespace_hash, generation, digest) IN ( \
                       SELECT namespace_hash, generation, digest FROM structural_file_state \
                       WHERE namespace_hash = ? AND generation != ? AND (? IS NULL OR generation != ?) LIMIT ? \
                     )",
                )
                .bind(&self.namespace_hash)
                .bind(&self.staging_generation)
                .bind(&self.active_generation)
                .bind(&self.active_generation)
                .bind(STATE_BATCH_SIZE as i64)
                .execute(&mut self.connection),
            )?;
            self.heartbeat_if_due()?;
            if affected.rows_affected() < STATE_BATCH_SIZE as u64 {
                break;
            }
        }
        self.runtime.block_on(
            sqlx::query(
                "DELETE FROM structural_runs WHERE namespace_hash = ? AND generation != ? \
                 AND (? IS NULL OR generation != ?)",
            )
            .bind(&self.namespace_hash)
            .bind(&self.staging_generation)
            .bind(&self.active_generation)
            .bind(&self.active_generation)
            .execute(&mut self.connection),
        )?;
        Ok(())
    }

    fn delete_unseen_staging_bounded(&mut self) -> Result<()> {
        loop {
            let affected = self.runtime.block_on(
                sqlx::query(
                    "DELETE FROM structural_file_state WHERE (namespace_hash, generation, digest) IN ( \
                       SELECT namespace_hash, generation, digest FROM structural_file_state \
                       WHERE namespace_hash = ? AND generation = ? AND seen_epoch != ? LIMIT ? \
                     )",
                )
                .bind(&self.namespace_hash)
                .bind(&self.staging_generation)
                .bind(&self.scan_epoch)
                .bind(STATE_BATCH_SIZE as i64)
                .execute(&mut self.connection),
            )?;
            self.heartbeat_if_due()?;
            if affected.rows_affected() < STATE_BATCH_SIZE as u64 {
                break;
            }
        }
        Ok(())
    }

    fn heartbeat_if_due(&mut self) -> Result<()> {
        let now = unix_timestamp()?;
        if now.saturating_sub(self.last_heartbeat_at) >= HEARTBEAT_INTERVAL_SECONDS {
            self.heartbeat()?;
        }
        Ok(())
    }

    fn delete_generation_bounded(&mut self, generation: &str) -> Result<()> {
        loop {
            let affected = self.runtime.block_on(
                sqlx::query(
                    "DELETE FROM structural_file_state WHERE (namespace_hash, generation, digest) IN ( \
                       SELECT namespace_hash, generation, digest FROM structural_file_state \
                       WHERE namespace_hash = ? AND generation = ? LIMIT ? \
                     )",
                )
                .bind(&self.namespace_hash)
                .bind(generation)
                .bind(STATE_BATCH_SIZE as i64)
                .execute(&mut self.connection),
            )?;
            if affected.rows_affected() < STATE_BATCH_SIZE as u64 {
                break;
            }
        }
        self.runtime.block_on(
            sqlx::query("DELETE FROM structural_runs WHERE generation = ?")
                .bind(generation)
                .execute(&mut self.connection),
        )?;
        Ok(())
    }

    fn cleanup_other_namespaces_bounded(&mut self) -> Result<()> {
        loop {
            let affected = self.runtime.block_on(
                sqlx::query(
                    "DELETE FROM structural_file_state WHERE (namespace_hash, generation, digest) IN ( \
                       SELECT namespace_hash, generation, digest FROM structural_file_state \
                       WHERE namespace_hash != ? LIMIT ? \
                     )",
                )
                .bind(&self.namespace_hash)
                .bind(STATE_BATCH_SIZE as i64)
                .execute(&mut self.connection),
            )?;
            if affected.rows_affected() < STATE_BATCH_SIZE as u64 {
                break;
            }
        }
        self.runtime.block_on(
            sqlx::query("DELETE FROM structural_runs WHERE namespace_hash != ?")
                .bind(&self.namespace_hash)
                .execute(&mut self.connection),
        )?;
        self.runtime.block_on(
            sqlx::query("DELETE FROM structural_namespaces WHERE namespace_hash != ?")
                .bind(&self.namespace_hash)
                .execute(&mut self.connection),
        )?;
        Ok(())
    }

    fn checkpoint(&mut self, truncate: bool) -> Result<()> {
        let command = if truncate {
            "PRAGMA wal_checkpoint(TRUNCATE)"
        } else {
            "PRAGMA wal_checkpoint(PASSIVE)"
        };
        self.runtime
            .block_on(sqlx::query(command).execute(&mut self.connection))?;
        Ok(())
    }
}

impl Drop for StructuralState {
    fn drop(&mut self) {
        if !self.finished {
            let _ = self.flush();
            let _ = self.runtime.block_on(
                sqlx::query(
                    "UPDATE structural_runs SET status = 'interrupted' WHERE generation = ?",
                )
                .bind(&self.staging_generation)
                .execute(&mut self.connection),
            );
        }
    }
}

async fn initialize_schema(connection: &mut SqliteConnection) -> Result<()> {
    let user_version: i64 = sqlx::query_scalar("PRAGMA user_version")
        .fetch_one(&mut *connection)
        .await?;
    if user_version != 0 && user_version != i64::from(STRUCTURAL_STATE_FORMAT_VERSION) {
        bail!(
            "unsupported structural state schema version {user_version}; expected {}",
            STRUCTURAL_STATE_FORMAT_VERSION
        );
    }
    sqlx::query("PRAGMA wal_autocheckpoint = 1000")
        .execute(&mut *connection)
        .await?;
    sqlx::query("PRAGMA temp_store = MEMORY")
        .execute(&mut *connection)
        .await?;
    sqlx::query(
        "CREATE TABLE IF NOT EXISTS structural_namespaces( \
            namespace_hash TEXT PRIMARY KEY, \
            namespace_json TEXT NOT NULL, \
            active_generation TEXT NULL \
         ) STRICT",
    )
    .execute(&mut *connection)
    .await?;
    sqlx::query(
        "CREATE TABLE IF NOT EXISTS structural_runs( \
            generation TEXT PRIMARY KEY, \
            namespace_hash TEXT NOT NULL, \
            requested_mode TEXT NOT NULL CHECK(requested_mode IN ('full','incremental')), \
            effective_mode TEXT NOT NULL CHECK(effective_mode IN ('full','incremental','baseline')), \
            status TEXT NOT NULL CHECK(status IN ('running','interrupted','complete')), \
            started_at INTEGER NOT NULL, \
            heartbeat_at INTEGER NOT NULL, \
            enumeration_complete INTEGER NOT NULL CHECK(enumeration_complete IN (0,1)), \
            FOREIGN KEY(namespace_hash) REFERENCES structural_namespaces(namespace_hash) ON DELETE CASCADE \
         ) STRICT",
    )
    .execute(&mut *connection)
    .await?;
    sqlx::query(
        "CREATE TABLE IF NOT EXISTS structural_file_state( \
            namespace_hash TEXT NOT NULL, \
            generation TEXT NOT NULL, \
            digest BLOB NOT NULL CHECK(length(digest) = 16), \
            dev INTEGER NOT NULL, ino INTEGER NOT NULL, len INTEGER NOT NULL, \
            mtime_ns INTEGER NOT NULL, ctime_ns INTEGER NOT NULL, \
            outcome INTEGER NOT NULL CHECK(outcome IN (0,1)), \
            candidate_json TEXT NULL, \
            seen_epoch TEXT NOT NULL, \
            PRIMARY KEY(namespace_hash, generation, digest), \
            FOREIGN KEY(generation) REFERENCES structural_runs(generation) ON DELETE CASCADE \
         ) WITHOUT ROWID, STRICT",
    )
    .execute(&mut *connection)
    .await?;
    sqlx::query(&format!(
        "PRAGMA user_version = {}",
        STRUCTURAL_STATE_FORMAT_VERSION
    ))
    .execute(&mut *connection)
    .await?;
    Ok(())
}

fn validate_state_path(path: &Path) -> Result<()> {
    if !path.is_absolute() {
        bail!("structural state database path must be absolute");
    }
    let parent = path
        .parent()
        .context("structural state database path has no parent")?;
    let parent_metadata = std::fs::symlink_metadata(parent).with_context(|| {
        format!(
            "failed to inspect structural state directory {}",
            parent.display()
        )
    })?;
    if parent_metadata.file_type().is_symlink() || !parent_metadata.is_dir() {
        bail!("structural state parent must be a non-symlink directory");
    }
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_file() => {
            bail!("structural state path must be a regular non-symlink file")
        }
        Ok(_) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(error).context("failed to inspect structural state path"),
    }
    Ok(())
}

fn unix_timestamp() -> Result<i64> {
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .context("system clock is before the Unix epoch")?
        .as_secs();
    i64::try_from(seconds).context("system clock overflowed SQLite timestamp")
}

fn decode_digest(bytes: &[u8]) -> Result<u128> {
    let value: [u8; 16] = bytes
        .try_into()
        .map_err(|_| anyhow::anyhow!("persisted structural digest was not 16 bytes"))?;
    Ok(u128::from_be_bytes(value))
}

fn fingerprint_from_row(row: &sqlx::sqlite::SqliteRow) -> Result<FileFingerprint> {
    Ok(FileFingerprint {
        dev: row.try_get::<i64, _>("dev")? as u64,
        ino: row.try_get::<i64, _>("ino")? as u64,
        len: row.try_get::<i64, _>("len")? as u64,
        mtime_ns: row.try_get("mtime_ns")?,
        ctime_ns: row.try_get("ctime_ns")?,
    })
}

fn parse_effective_mode(value: &str) -> Result<EffectiveScanMode> {
    match value {
        "full" => Ok(EffectiveScanMode::Full),
        "incremental" => Ok(EffectiveScanMode::Incremental),
        "baseline" => Ok(EffectiveScanMode::Baseline),
        _ => bail!("persisted structural run used unknown effective mode"),
    }
}

fn validate_candidate_fingerprint(
    candidate: &CorruptionCandidate,
    fingerprint: &FileFingerprint,
) -> Result<()> {
    let CorruptionEvidence::Structural { structural } = &candidate.evidence else {
        bail!("persisted structural state contained non-structural evidence");
    };
    if candidate.exact_paths.len() != 1 || structural.fingerprint != *fingerprint {
        bail!("persisted structural candidate identity did not match its state row");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cache_corruption_detector::{StructuralEvidence, StructuralIssue};
    use std::path::PathBuf;
    use tempfile::TempDir;

    fn fingerprint(value: u64) -> FileFingerprint {
        FileFingerprint {
            dev: value,
            ino: value + 1,
            len: value + 2,
            mtime_ns: value as i64 + 3,
            ctime_ns: value as i64 + 4,
        }
    }

    fn namespace(scope: &str) -> StateNamespace {
        StateNamespace {
            canonical_root_identity: "root".to_string(),
            root_fingerprint: fingerprint(1),
            scope: scope.to_string(),
            layout_signature: "layout".to_string(),
            scanner_policy_version: 1,
        }
    }

    fn database(temp: &TempDir) -> PathBuf {
        temp.path().join("state.sqlite3")
    }

    #[test]
    fn first_incremental_builds_then_reuses_consistent_state() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        assert_eq!(first.effective_mode(), EffectiveScanMode::Baseline);
        assert!(!first.can_reuse_existing());
        first
            .record_success(7, fingerprint(7), SuccessfulOutcome::Consistent)
            .unwrap();
        assert_eq!(first.publish().unwrap(), (0, 1));
        drop(first);

        let mut second =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        assert!(second.can_reuse_existing());
        assert_eq!(second.effective_mode(), EffectiveScanMode::Incremental);
        assert!(matches!(
            second
                .lookup_batch(&[LookupInput {
                    digest: 7,
                    fingerprint: Some(fingerprint(7)),
                }])
                .unwrap()
                .as_slice(),
            [ReuseDecision::ReuseConsistent]
        ));
        assert_eq!(second.publish().unwrap(), (0, 1));
    }

    #[test]
    fn changed_fingerprint_is_inspected_and_deleted_rows_are_pruned() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first
            .record_success(2, fingerprint(2), SuccessfulOutcome::Consistent)
            .unwrap();
        first.publish().unwrap();
        drop(first);

        let mut second =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        assert!(matches!(
            second
                .lookup_batch(&[LookupInput {
                    digest: 1,
                    fingerprint: Some(fingerprint(99)),
                }])
                .unwrap()
                .as_slice(),
            [ReuseDecision::Inspect]
        ));
        second
            .record_success(1, fingerprint(99), SuccessfulOutcome::Consistent)
            .unwrap();
        assert_eq!(second.publish().unwrap(), (1, 1));
    }

    #[test]
    fn interrupted_incremental_resumes_committed_staging_rows() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first.interrupt().unwrap();
        drop(first);

        let mut resumed =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        assert!(resumed.resumed());
        assert!(matches!(
            resumed
                .lookup_batch(&[LookupInput {
                    digest: 1,
                    fingerprint: Some(fingerprint(1)),
                }])
                .unwrap()
                .as_slice(),
            [ReuseDecision::ReuseConsistent]
        ));
    }

    #[test]
    fn resumed_publication_drops_staging_rows_not_seen_again() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first
            .record_success(2, fingerprint(2), SuccessfulOutcome::Consistent)
            .unwrap();
        first.interrupt().unwrap();
        drop(first);

        let mut resumed =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        assert!(matches!(
            resumed
                .lookup_batch(&[LookupInput {
                    digest: 1,
                    fingerprint: Some(fingerprint(1)),
                }])
                .unwrap()
                .as_slice(),
            [ReuseDecision::ReuseConsistent]
        ));
        assert_eq!(resumed.publish().unwrap(), (0, 1));
    }

    #[test]
    fn live_lease_blocks_a_second_writer() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        let error =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .err()
                .expect("a live lease must reject a second writer");
        assert!(format!("{error:#}").contains("live lease"));
        first.interrupt().unwrap();
    }

    #[test]
    fn full_never_resumes_abandoned_full_staging() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Full).unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first.interrupt().unwrap();
        drop(first);
        let second =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Full).unwrap();
        assert!(!second.resumed());
        assert_eq!(second.effective_mode(), EffectiveScanMode::Full);
    }

    #[test]
    fn namespace_change_builds_a_new_baseline() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("one"), StructuralScanMode::Incremental)
                .unwrap();
        first.publish().unwrap();
        drop(first);
        let second =
            StructuralState::open(&path, namespace("two"), StructuralScanMode::Incremental)
                .unwrap();
        assert_eq!(second.effective_mode(), EffectiveScanMode::Baseline);
    }

    #[test]
    fn proven_rows_require_revalidation() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let fp = fingerprint(4);
        let candidate = CorruptionCandidate {
            candidate_id: "candidate".to_string(),
            service: "unknown".to_string(),
            exact_paths: vec!["/cache/00/00/00000000000000000000000000000000".to_string()],
            evidence: CorruptionEvidence::Structural {
                structural: StructuralEvidence {
                    issues: vec![StructuralIssue::MalformedCacheHeader],
                    cache_key_encoding: "hex".to_string(),
                    cache_key: String::new(),
                    cache_key_md5: "00000000000000000000000000000000".to_string(),
                    cache_version: 5,
                    http_status: None,
                    header_start: None,
                    body_start: None,
                    file_length: fp.len,
                    actual_payload_length: None,
                    expected_payload_length: None,
                    content_length: None,
                    content_range: None,
                    fingerprint: fp.clone(),
                    detected_at_utc: "2026-01-01T00:00:00Z".to_string(),
                },
            },
        };
        let mut first =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        first
            .record_success(0, fp.clone(), SuccessfulOutcome::Proven(&candidate))
            .unwrap();
        first.publish().unwrap();
        drop(first);
        let mut second =
            StructuralState::open(&path, namespace("default"), StructuralScanMode::Incremental)
                .unwrap();
        assert!(matches!(
            second
                .lookup_batch(&[LookupInput {
                    digest: 0,
                    fingerprint: Some(fp),
                }])
                .unwrap()
                .as_slice(),
            [ReuseDecision::Revalidate(_)]
        ));
    }

    #[test]
    fn lease_maintenance_refreshes_a_scan_during_long_traversal() {
        let temp = TempDir::new().unwrap();
        let mut state = StructuralState::open(
            &database(&temp),
            namespace("lease-refresh"),
            StructuralScanMode::Incremental,
        )
        .unwrap();
        let stale = unix_timestamp().unwrap() - LEASE_TIMEOUT_SECONDS - 1;
        state.last_heartbeat_at = stale;
        state
            .runtime
            .block_on(
                sqlx::query("UPDATE structural_runs SET heartbeat_at = ? WHERE generation = ?")
                    .bind(stale)
                    .bind(&state.staging_generation)
                    .execute(&mut state.connection),
            )
            .unwrap();

        state.maintain_lease().unwrap();
        let refreshed: i64 = state
            .runtime
            .block_on(
                sqlx::query_scalar("SELECT heartbeat_at FROM structural_runs WHERE generation = ?")
                    .bind(&state.staging_generation)
                    .fetch_one(&mut state.connection),
            )
            .unwrap();
        assert!(refreshed > stale);
    }

    #[test]
    fn publication_failure_does_not_flip_the_active_generation() {
        let temp = TempDir::new().unwrap();
        let path = database(&temp);
        let mut first =
            StructuralState::open(&path, namespace("atomic"), StructuralScanMode::Incremental)
                .unwrap();
        first.publish().unwrap();
        let previous_active = first.active_generation.clone().unwrap();
        drop(first);

        let mut second =
            StructuralState::open(&path, namespace("atomic"), StructuralScanMode::Incremental)
                .unwrap();
        second
            .runtime
            .block_on(
                sqlx::query(
                    "UPDATE structural_namespaces SET namespace_json = 'tampered' WHERE namespace_hash = ?",
                )
                .bind(&second.namespace_hash)
                .execute(&mut second.connection),
            )
            .unwrap();

        assert!(second.publish().is_err());
        let active: Option<String> = second
            .runtime
            .block_on(
                sqlx::query_scalar(
                    "SELECT active_generation FROM structural_namespaces WHERE namespace_hash = ?",
                )
                .bind(&second.namespace_hash)
                .fetch_one(&mut second.connection),
            )
            .unwrap();
        assert_eq!(active.as_deref(), Some(previous_active.as_str()));
    }
}
