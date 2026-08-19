use crate::cache_corruption_detector::{
    CorruptionCandidate, CorruptionEvidence, FileFingerprint, CORRUPTION_CONTRACT_VERSION,
};
use crate::db;
use anyhow::{bail, Context, Result};
use clap::ValueEnum;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use sqlx::postgres::PgRow;
use sqlx::{Connection, PgConnection, Postgres, QueryBuilder, Row};
use std::collections::HashMap;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::runtime::{Builder, Runtime};
use uuid::Uuid;

pub const STRUCTURAL_STATE_FORMAT_VERSION: u32 = 1;
pub const STATE_BATCH_SIZE: usize = 1_024;
const LOOKUP_BATCH_SIZE: usize = 500;
const LEASE_TIMEOUT_SECONDS: i64 = 300;
const HEARTBEAT_INTERVAL_SECONDS: i64 = 30;

/// Serializes first-time table creation. Concurrent first initializers (parallel test threads
/// against a fresh database) would otherwise race CREATE TABLE IF NOT EXISTS, which PostgreSQL
/// reports as a duplicate-key error on its catalog instead of treating as a no-op. Namespace
/// lock keys are uniform SHA-256 prefixes, so a fixed constant collides with one namespace in
/// 2^64.
const SCHEMA_SETUP_LOCK_KEY: i64 = i64::from_be_bytes(*b"structur");

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

/// Identifies which stored baseline a scan may reuse.
///
/// The root is identified by its canonical path and nothing else. An earlier version also
/// carried the root's device+inode, which looked like a stronger check but threw the whole
/// baseline away after every NFS/SMB remount, because those clients hand out a fresh
/// anonymous device number each time they mount. Whether the files themselves are still the
/// files we recorded is settled per file by `FileFingerprint::same_file`, so pointing the
/// scanner at a different filesystem under the same path costs a fresh inspection, not a
/// wrong answer.
#[derive(Debug, Clone)]
pub struct StateNamespace {
    pub canonical_root_identity: String,
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
    outcome: bool,
    candidate: Option<CorruptionCandidate>,
}

#[derive(Debug)]
struct PendingWrite {
    digest: u128,
    fingerprint: FileFingerprint,
    outcome: bool,
    candidate_json: Option<String>,
}

/// Whether a live scanner process already owns this namespace.
///
/// A heartbeat column cannot answer that question. It records when a scan was last *seen* alive,
/// so a scan killed with SIGKILL (a redeploy, `docker stop`, the OOM killer) leaves a row that
/// still looks fresh, and every later scan is refused until the lease times out. A session
/// advisory lock is held by the PostgreSQL server on behalf of the connection and is released
/// the instant that connection dies, however its process dies, so it answers "is anyone actually
/// scanning this namespace right now" exactly.
enum StateLock {
    /// We own the lock. No other scanner is alive in this namespace, whatever the rows claim.
    Acquired,
    /// Another connection holds it and is genuinely running.
    Busy,
    /// Never produced against PostgreSQL, which can always take an advisory lock; kept so the
    /// heartbeat-lease fallback below stays wired for a store that cannot answer the question.
    #[allow(dead_code)]
    Unsupported,
}

/// First 64 bits of the namespace SHA-256, so each namespace gets its own advisory lock and two
/// scopes can scan concurrently.
fn advisory_lock_key(namespace_hash: &str) -> Result<i64> {
    let key = u64::from_str_radix(&namespace_hash[..16], 16)
        .context("structural namespace hash is not hexadecimal")?;
    Ok(key as i64)
}

async fn acquire_state_lock(
    connection: &mut PgConnection,
    namespace_hash: &str,
) -> Result<StateLock> {
    let acquired: bool = sqlx::query_scalar("SELECT pg_try_advisory_lock($1)")
        .bind(advisory_lock_key(namespace_hash)?)
        .fetch_one(connection)
        .await
        .context("failed to take structural state advisory lock")?;
    Ok(if acquired {
        StateLock::Acquired
    } else {
        StateLock::Busy
    })
}

pub struct StructuralState {
    runtime: Runtime,
    /// Carries the session advisory lock for the whole scan. Dropping the connection (including
    /// by dying) releases the lock on the server.
    connection: PgConnection,
    namespace_hash: String,
    namespace_json: String,
    scope: String,
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
    pub fn open(namespace: StateNamespace, mode: StructuralScanMode) -> Result<Self> {
        let namespace_json = serde_json::to_string(&NamespaceDocument {
            state_format_version: STRUCTURAL_STATE_FORMAT_VERSION,
            report_contract_version: CORRUPTION_CONTRACT_VERSION,
            scanner_policy_version: namespace.scanner_policy_version,
            canonical_root_identity: &namespace.canonical_root_identity,
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
        // A dedicated connection rather than the shared pool: the advisory lock below is
        // session-scoped, so it must live and die with exactly this scan's connection.
        let options = db::build_connect_options()?;
        let mut connection = runtime
            .block_on(PgConnection::connect_with(&options))
            .context("failed to connect to the structural state database")?;
        // Ask the server who is actually running, before trusting anything the rows say about
        // it. A scan killed mid-flight leaves a `running` row with a fresh heartbeat, and
        // reading that row as "someone else is scanning" is what locked users out for five
        // minutes after every restart.
        let lock_proves_no_other_scanner =
            match runtime.block_on(acquire_state_lock(&mut connection, &namespace_hash))? {
                StateLock::Acquired => true,
                StateLock::Busy => bail!("another structural cache scan is already running"),
                StateLock::Unsupported => false,
            };
        runtime.block_on(initialize_schema(&mut connection))?;

        let now = unix_timestamp()?;
        let mut transaction = runtime
            .block_on(connection.begin())
            .context("failed to begin structural state setup transaction")?;
        let existing_descriptor = runtime.block_on(
            sqlx::query(
                "SELECT namespace_json FROM structural_namespaces WHERE namespace_hash = $1",
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
                    "INSERT INTO structural_namespaces(namespace_hash, namespace_json, scope) \
                     VALUES($1, $2, $3)",
                )
                .bind(&namespace_hash)
                .bind(&namespace_json)
                .bind(&namespace.scope)
                .execute(&mut *transaction),
            )?;
        }
        let active_generation = runtime
            .block_on(
                sqlx::query(
                    "SELECT active_generation FROM structural_namespaces WHERE namespace_hash = $1",
                )
                .bind(&namespace_hash)
                .fetch_one(&mut *transaction),
            )?
            .try_get::<Option<String>, _>("active_generation")?;

        // Only consult the heartbeat where we could not take an advisory lock. Where we could,
        // holding it already proves no other scanner is alive, and a stale `running` row from a
        // killed process must not be mistaken for one.
        if !lock_proves_no_other_scanner {
            let fresh_lease = runtime.block_on(
                sqlx::query(
                    "SELECT generation FROM structural_runs \
                     WHERE namespace_hash = $1 AND status = 'running' AND heartbeat_at >= $2 \
                     LIMIT 1",
                )
                .bind(&namespace_hash)
                .bind(now.saturating_sub(LEASE_TIMEOUT_SECONDS))
                .fetch_optional(&mut *transaction),
            )?;
            if fresh_lease.is_some() {
                bail!("another structural cache scan is already running");
            }
        }
        // Namespace-scoped because every scope shares these tables: another scope's scan may be
        // genuinely running right now under its own advisory lock, and its row is not ours to
        // interrupt.
        runtime.block_on(
            sqlx::query(
                "UPDATE structural_runs SET status = 'interrupted' \
                 WHERE namespace_hash = $1 AND status = 'running'",
            )
            .bind(&namespace_hash)
            .execute(&mut *transaction),
        )?;

        let resumable = if mode == StructuralScanMode::Incremental {
            runtime.block_on(
                sqlx::query(
                    "SELECT generation, effective_mode FROM structural_runs \
                     WHERE namespace_hash = $1 AND requested_mode = 'incremental' \
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
                     ) VALUES($1, $2, $3, $4, 'running', $5, $6, FALSE)",
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
                    "UPDATE structural_runs SET status = 'running', heartbeat_at = $1 WHERE generation = $2",
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
            scope: namespace.scope,
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
    /// the database. Without this, a long enumeration can look stale while the owning scan is
    /// healthy.
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
            let mut query = QueryBuilder::<Postgres>::new(
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
                if !staging.fingerprint.same_file(current) {
                    self.queue_delete(input.digest);
                    None
                } else {
                    Some(staging)
                }
            } else {
                active_key.as_ref().and_then(|key| rows.get(key))
            };
            let Some(stored) = stored.filter(|row| row.fingerprint.same_file(current)) else {
                decisions.push(ReuseDecision::Inspect);
                continue;
            };
            if stored.outcome {
                let candidate = stored
                    .candidate
                    .clone()
                    .context("persisted proven outcome omitted its candidate")?;
                validate_candidate_fingerprint(&candidate, current)?;
                if stored.generation == self.staging_generation {
                    self.queue_delete(input.digest);
                }
                decisions.push(ReuseDecision::Revalidate(Box::new(candidate)));
            } else {
                // Rewrite even a staging hit so this resume epoch proves the file was seen.
                self.queue_write(input.digest, current.clone(), false, None)?;
                decisions.push(ReuseDecision::ReuseConsistent);
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
            SuccessfulOutcome::Consistent => (false, None),
            SuccessfulOutcome::Proven(candidate) => {
                validate_candidate_fingerprint(candidate, &fingerprint)?;
                (
                    true,
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
                "UPDATE structural_runs SET status = 'interrupted', heartbeat_at = $1 WHERE generation = $2",
            )
            .bind(now)
            .bind(&self.staging_generation)
            .execute(&mut self.connection),
        )?;
        if affected.rows_affected() != 1 {
            bail!("structural state interruption lost its staging run");
        }
        self.finished = true;
        Ok(())
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
                "UPDATE structural_runs SET status = 'complete', enumeration_complete = TRUE, heartbeat_at = $1 \
                 WHERE generation = $2",
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
                "UPDATE structural_namespaces SET active_generation = $1 WHERE namespace_hash = $2 AND namespace_json = $3",
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
        if let Some(old) = old_active {
            if let Err(error) = self.delete_generation_bounded(&old) {
                eprintln!("WARNING: structural state old-generation cleanup failed: {error:#}");
            }
        }
        if let Err(error) = self.cleanup_incompatible_namespaces_bounded() {
            eprintln!("WARNING: structural state incompatible-namespace cleanup failed: {error:#}");
        }
        Ok((pruned, new_count))
    }

    fn queue_write(
        &mut self,
        digest: u128,
        fingerprint: FileFingerprint,
        outcome: bool,
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
                     WHERE namespace_hash = $1 AND generation = $2 AND digest = $3",
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
                     ) VALUES($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) \
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
            sqlx::query("UPDATE structural_runs SET heartbeat_at = $1 WHERE generation = $2")
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
            sqlx::query("UPDATE structural_runs SET heartbeat_at = $1 WHERE generation = $2")
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
                "SELECT COUNT(*) FROM structural_file_state WHERE namespace_hash = $1 AND generation = $2",
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
                 WHERE old.namespace_hash = $1 AND old.generation = $2 \
                   AND NOT EXISTS(SELECT 1 FROM structural_file_state new \
                     WHERE new.namespace_hash = old.namespace_hash AND new.generation = $3 AND new.digest = old.digest)",
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
                       WHERE namespace_hash = $1 AND generation != $2 AND ($3 IS NULL OR generation != $3) LIMIT $4 \
                     )",
                )
                .bind(&self.namespace_hash)
                .bind(&self.staging_generation)
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
                "DELETE FROM structural_runs WHERE namespace_hash = $1 AND generation != $2 \
                 AND ($3 IS NULL OR generation != $3)",
            )
            .bind(&self.namespace_hash)
            .bind(&self.staging_generation)
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
                       WHERE namespace_hash = $1 AND generation = $2 AND seen_epoch != $3 LIMIT $4 \
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
                       WHERE namespace_hash = $1 AND generation = $2 LIMIT $3 \
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
            sqlx::query("DELETE FROM structural_runs WHERE generation = $1")
                .bind(generation)
                .execute(&mut self.connection),
        )?;
        Ok(())
    }

    /// Collects predecessors of this scope whose namespace no longer matches: an older layout
    /// signature, scanner policy version, OS or architecture leaves rows nothing will read
    /// again. In the per-file store this could sweep every foreign namespace, because the file
    /// belonged to one scope; these tables are shared by all scopes now, so the sweep stays
    /// inside this scope and never touches another scope's live baseline.
    fn cleanup_incompatible_namespaces_bounded(&mut self) -> Result<()> {
        loop {
            let affected = self.runtime.block_on(
                sqlx::query(
                    "DELETE FROM structural_file_state WHERE (namespace_hash, generation, digest) IN ( \
                       SELECT f.namespace_hash, f.generation, f.digest FROM structural_file_state f \
                       JOIN structural_namespaces n ON n.namespace_hash = f.namespace_hash \
                       WHERE n.scope = $1 AND f.namespace_hash != $2 LIMIT $3 \
                     )",
                )
                .bind(&self.scope)
                .bind(&self.namespace_hash)
                .bind(STATE_BATCH_SIZE as i64)
                .execute(&mut self.connection),
            )?;
            if affected.rows_affected() < STATE_BATCH_SIZE as u64 {
                break;
            }
        }
        self.runtime.block_on(
            sqlx::query(
                "DELETE FROM structural_runs WHERE namespace_hash != $1 AND namespace_hash IN ( \
                   SELECT namespace_hash FROM structural_namespaces WHERE scope = $2 \
                 )",
            )
            .bind(&self.namespace_hash)
            .bind(&self.scope)
            .execute(&mut self.connection),
        )?;
        self.runtime.block_on(
            sqlx::query("DELETE FROM structural_namespaces WHERE scope = $1 AND namespace_hash != $2")
                .bind(&self.scope)
                .bind(&self.namespace_hash)
                .execute(&mut self.connection),
        )?;
        Ok(())
    }
}

impl Drop for StructuralState {
    fn drop(&mut self) {
        if !self.finished {
            let _ = self.flush();
            let _ = self.runtime.block_on(
                sqlx::query(
                    "UPDATE structural_runs SET status = 'interrupted' WHERE generation = $1",
                )
                .bind(&self.staging_generation)
                .execute(&mut self.connection),
            );
        }
    }
}

async fn initialize_schema(connection: &mut PgConnection) -> Result<()> {
    let mut transaction = connection
        .begin()
        .await
        .context("failed to begin structural state schema setup")?;
    sqlx::query("SELECT pg_advisory_xact_lock($1)")
        .bind(SCHEMA_SETUP_LOCK_KEY)
        .execute(&mut *transaction)
        .await?;
    // SQLite kept the schema generation in `PRAGMA user_version`, one per state file, so a
    // version this binary did not understand was one file an admin could delete. Here it is a
    // single row covering the whole database, and refusing on a mismatch would stop every scan
    // on every cache root with nothing in the app able to clear it. All of this is derived
    // state that the next scan rebuilds from the cache itself, so a version we do not recognise
    // is discarded rather than refused.
    sqlx::query("CREATE TABLE IF NOT EXISTS structural_state_version(version BIGINT PRIMARY KEY)")
        .execute(&mut *transaction)
        .await?;
    let stored_version: Option<i64> =
        sqlx::query_scalar("SELECT version FROM structural_state_version LIMIT 1")
            .fetch_optional(&mut *transaction)
            .await?;
    let stale_version = stored_version
        .filter(|version| *version != i64::from(STRUCTURAL_STATE_FORMAT_VERSION));
    if let Some(version) = stale_version {
        eprintln!(
            "WARNING: discarding structural scan state written for schema version {version}; \
             this build expects {}. The next scan rebuilds it.",
            STRUCTURAL_STATE_FORMAT_VERSION
        );
        // Dropped rather than migrated: the tables the old version left behind may not have the
        // columns this one reads. Order matters only for readability, CASCADE handles the rest.
        for table in [
            "structural_file_state",
            "structural_runs",
            "structural_namespaces",
        ] {
            sqlx::query(&format!("DROP TABLE IF EXISTS {table} CASCADE"))
                .execute(&mut *transaction)
                .await?;
        }
        sqlx::query("DELETE FROM structural_state_version")
            .execute(&mut *transaction)
            .await?;
    }
    // `scope` mirrors the namespace document's scope field as a real column so state for one
    // cache root can be invalidated from outside this process with a plain
    // `DELETE FROM structural_namespaces WHERE scope = ...`. External callers cannot compute
    // `namespace_hash` (it also covers policy version, OS and architecture), and both cascades
    // below carry that delete through structural_runs into structural_file_state.
    sqlx::query(
        "CREATE TABLE IF NOT EXISTS structural_namespaces( \
            namespace_hash TEXT PRIMARY KEY, \
            namespace_json TEXT NOT NULL, \
            scope TEXT NOT NULL, \
            active_generation TEXT NULL \
         )",
    )
    .execute(&mut *transaction)
    .await?;
    sqlx::query(
        "CREATE INDEX IF NOT EXISTS idx_structural_namespaces_scope \
         ON structural_namespaces(scope)",
    )
    .execute(&mut *transaction)
    .await?;
    sqlx::query(
        "CREATE TABLE IF NOT EXISTS structural_runs( \
            generation TEXT PRIMARY KEY, \
            namespace_hash TEXT NOT NULL, \
            requested_mode TEXT NOT NULL CHECK(requested_mode IN ('full','incremental')), \
            effective_mode TEXT NOT NULL CHECK(effective_mode IN ('full','incremental','baseline')), \
            status TEXT NOT NULL CHECK(status IN ('running','interrupted','complete')), \
            started_at BIGINT NOT NULL, \
            heartbeat_at BIGINT NOT NULL, \
            enumeration_complete BOOLEAN NOT NULL, \
            FOREIGN KEY(namespace_hash) REFERENCES structural_namespaces(namespace_hash) ON DELETE CASCADE \
         )",
    )
    .execute(&mut *transaction)
    .await?;
    sqlx::query(
        "CREATE TABLE IF NOT EXISTS structural_file_state( \
            namespace_hash TEXT NOT NULL, \
            generation TEXT NOT NULL, \
            digest BYTEA NOT NULL CHECK(octet_length(digest) = 16), \
            dev BIGINT NOT NULL, ino BIGINT NOT NULL, len BIGINT NOT NULL, \
            mtime_ns BIGINT NOT NULL, ctime_ns BIGINT NOT NULL, \
            outcome BOOLEAN NOT NULL, \
            candidate_json TEXT NULL, \
            seen_epoch TEXT NOT NULL, \
            PRIMARY KEY(namespace_hash, generation, digest), \
            FOREIGN KEY(generation) REFERENCES structural_runs(generation) ON DELETE CASCADE \
         )",
    )
    .execute(&mut *transaction)
    .await?;
    // Either there was never a version row, or the stale one was just deleted above.
    if stored_version.is_none() || stale_version.is_some() {
        sqlx::query("INSERT INTO structural_state_version(version) VALUES($1)")
            .bind(i64::from(STRUCTURAL_STATE_FORMAT_VERSION))
            .execute(&mut *transaction)
            .await?;
    }
    transaction.commit().await?;
    Ok(())
}

fn unix_timestamp() -> Result<i64> {
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .context("system clock is before the Unix epoch")?
        .as_secs();
    i64::try_from(seconds).context("system clock overflowed the state timestamp")
}

fn decode_digest(bytes: &[u8]) -> Result<u128> {
    let value: [u8; 16] = bytes
        .try_into()
        .map_err(|_| anyhow::anyhow!("persisted structural digest was not 16 bytes"))?;
    Ok(u128::from_be_bytes(value))
}

fn fingerprint_from_row(row: &PgRow) -> Result<FileFingerprint> {
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
    if candidate.exact_paths.len() != 1 || !structural.fingerprint.same_file(fingerprint) {
        bail!("persisted structural candidate identity did not match its state row");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cache_corruption_detector::{StructuralEvidence, StructuralIssue};

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
            scope: scope.to_string(),
            layout_signature: "layout".to_string(),
            scanner_policy_version: 1,
        }
    }

    /// The store is one shared database, so tests isolate through the namespace instead of a
    /// temp file: a scope no other test can collide with keeps rows and advisory locks apart.
    fn unique_scope(prefix: &str) -> String {
        format!("{prefix}-{}", Uuid::new_v4())
    }

    #[test]
    fn first_incremental_builds_then_reuses_consistent_state() {
        let _env = db::lock_test_env();
        let scope = unique_scope("first-incremental");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        assert_eq!(first.effective_mode(), EffectiveScanMode::Baseline);
        assert!(!first.can_reuse_existing());
        first
            .record_success(7, fingerprint(7), SuccessfulOutcome::Consistent)
            .unwrap();
        assert_eq!(first.publish().unwrap(), (0, 1));
        drop(first);

        let mut second =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
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

    /// The version row covers the whole database rather than one file per cache root, so
    /// refusing to open on a version this build does not know would stop every scan on every
    /// root at once, with nothing in the app able to clear it. Opening has to discard the old
    /// state and carry on instead. The baseline is derived from the cache, so the only cost is
    /// one full rescan.
    #[test]
    fn state_written_by_another_schema_version_is_discarded_not_refused() {
        let _env = db::lock_test_env();
        let scope = unique_scope("stale-version");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        first
            .record_success(11, fingerprint(11), SuccessfulOutcome::Consistent)
            .unwrap();
        first.publish().unwrap();
        drop(first);

        // Stand in for a future build having written the state.
        let runtime = Builder::new_current_thread().enable_all().build().unwrap();
        runtime.block_on(async {
            let mut connection = PgConnection::connect_with(&db::build_connect_options().unwrap())
                .await
                .unwrap();
            sqlx::query("UPDATE structural_state_version SET version = $1")
                .bind(i64::from(STRUCTURAL_STATE_FORMAT_VERSION) + 1)
                .execute(&mut connection)
                .await
                .unwrap();
        });

        // Opening must succeed rather than bail, and must not offer the discarded baseline.
        let mut reopened =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        assert!(!reopened.can_reuse_existing());
        assert_eq!(reopened.effective_mode(), EffectiveScanMode::Baseline);
        assert_eq!(reopened.publish().unwrap(), (0, 0));
    }

    /// A remount hands out a new device number for files that never changed. NFS and SMB do
    /// this on every mount, so a host reboot must not cost the user a full rescan.
    #[test]
    fn a_new_device_number_alone_still_reuses_the_baseline() {
        let _env = db::lock_test_env();
        let scope = unique_scope("new-device");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        first
            .record_success(7, fingerprint(7), SuccessfulOutcome::Consistent)
            .unwrap();
        first.publish().unwrap();
        drop(first);

        let mut remounted = fingerprint(7);
        remounted.dev += 4_000;
        let mut second =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        assert_eq!(second.effective_mode(), EffectiveScanMode::Incremental);
        assert!(matches!(
            second
                .lookup_batch(&[LookupInput {
                    digest: 7,
                    fingerprint: Some(remounted),
                }])
                .unwrap()
                .as_slice(),
            [ReuseDecision::ReuseConsistent]
        ));
    }

    #[test]
    fn changed_fingerprint_is_inspected_and_deleted_rows_are_pruned() {
        let _env = db::lock_test_env();
        let scope = unique_scope("changed-fingerprint");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first
            .record_success(2, fingerprint(2), SuccessfulOutcome::Consistent)
            .unwrap();
        first.publish().unwrap();
        drop(first);

        let mut second =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
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
        let _env = db::lock_test_env();
        let scope = unique_scope("interrupted-resume");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first.interrupt().unwrap();
        drop(first);

        let mut resumed =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
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
        let _env = db::lock_test_env();
        let scope = unique_scope("resumed-publication");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first
            .record_success(2, fingerprint(2), SuccessfulOutcome::Consistent)
            .unwrap();
        first.interrupt().unwrap();
        drop(first);

        let mut resumed =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
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
        let _env = db::lock_test_env();
        let scope = unique_scope("live-lease");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        let error = StructuralState::open(namespace(&scope), StructuralScanMode::Incremental)
            .err()
            .expect("a live scan must reject a second writer");
        assert!(format!("{error:#}").contains("already running"));
        first.interrupt().unwrap();
    }

    #[test]
    fn full_never_resumes_abandoned_full_staging() {
        let _env = db::lock_test_env();
        let scope = unique_scope("full-no-resume");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Full).unwrap();
        first
            .record_success(1, fingerprint(1), SuccessfulOutcome::Consistent)
            .unwrap();
        first.interrupt().unwrap();
        drop(first);
        let second = StructuralState::open(namespace(&scope), StructuralScanMode::Full).unwrap();
        assert!(!second.resumed());
        assert_eq!(second.effective_mode(), EffectiveScanMode::Full);
    }

    #[test]
    fn namespace_change_builds_a_new_baseline() {
        let _env = db::lock_test_env();
        let base = unique_scope("namespace-change");
        let mut first = StructuralState::open(
            namespace(&format!("{base}-one")),
            StructuralScanMode::Incremental,
        )
        .unwrap();
        first.publish().unwrap();
        drop(first);
        let second = StructuralState::open(
            namespace(&format!("{base}-two")),
            StructuralScanMode::Incremental,
        )
        .unwrap();
        assert_eq!(second.effective_mode(), EffectiveScanMode::Baseline);
    }

    #[test]
    fn proven_rows_require_revalidation() {
        let _env = db::lock_test_env();
        let scope = unique_scope("proven-revalidate");
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
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        first
            .record_success(0, fp.clone(), SuccessfulOutcome::Proven(&candidate))
            .unwrap();
        first.publish().unwrap();
        drop(first);
        let mut second =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
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
        let _env = db::lock_test_env();
        let mut state = StructuralState::open(
            namespace(&unique_scope("lease-refresh")),
            StructuralScanMode::Incremental,
        )
        .unwrap();
        let stale = unix_timestamp().unwrap() - LEASE_TIMEOUT_SECONDS - 1;
        state.last_heartbeat_at = stale;
        state
            .runtime
            .block_on(
                sqlx::query("UPDATE structural_runs SET heartbeat_at = $1 WHERE generation = $2")
                    .bind(stale)
                    .bind(&state.staging_generation)
                    .execute(&mut state.connection),
            )
            .unwrap();

        state.maintain_lease().unwrap();
        let refreshed: i64 = state
            .runtime
            .block_on(
                sqlx::query_scalar(
                    "SELECT heartbeat_at FROM structural_runs WHERE generation = $1",
                )
                .bind(&state.staging_generation)
                .fetch_one(&mut state.connection),
            )
            .unwrap();
        assert!(refreshed > stale);
    }

    /// The failure users actually hit: restart the container mid-scan (SIGKILL, so nothing gets
    /// to tidy up), press Scan, and the run row still says `running` with a heartbeat that looks
    /// fresh. Reading that as "another scan is live" refused every scan for the next five
    /// minutes. No connection holds the advisory lock, so the next scan must simply start.
    #[test]
    fn a_hard_killed_scan_does_not_lock_out_the_next_one() {
        let _env = db::lock_test_env();
        let scope = unique_scope("hard-kill");

        let state =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        let generation = state.staging_generation.clone();
        drop(state);

        // Forge exactly what a SIGKILL leaves behind: still `running`, heartbeat still fresh.
        let runtime = Builder::new_current_thread().enable_all().build().unwrap();
        let mut connection = runtime
            .block_on(PgConnection::connect_with(
                &db::build_connect_options().unwrap(),
            ))
            .unwrap();
        runtime
            .block_on(
                sqlx::query(
                    "UPDATE structural_runs SET status = 'running', heartbeat_at = $1 \
                     WHERE generation = $2",
                )
                .bind(unix_timestamp().unwrap())
                .bind(&generation)
                .execute(&mut connection),
            )
            .unwrap();
        drop(connection);
        drop(runtime);

        let next = StructuralState::open(namespace(&scope), StructuralScanMode::Incremental);
        assert!(
            next.is_ok(),
            "a hard-killed scan locked out the next one: {:?}",
            next.err()
        );
    }

    #[test]
    fn publication_failure_does_not_flip_the_active_generation() {
        let _env = db::lock_test_env();
        let scope = unique_scope("atomic");
        let mut first =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        first.publish().unwrap();
        let previous_active = first.active_generation.clone().unwrap();
        drop(first);

        let mut second =
            StructuralState::open(namespace(&scope), StructuralScanMode::Incremental).unwrap();
        second
            .runtime
            .block_on(
                sqlx::query(
                    "UPDATE structural_namespaces SET namespace_json = 'tampered' WHERE namespace_hash = $1",
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
                    "SELECT active_generation FROM structural_namespaces WHERE namespace_hash = $1",
                )
                .bind(&second.namespace_hash)
                .fetch_one(&mut second.connection),
            )
            .unwrap();
        assert_eq!(active.as_deref(), Some(previous_active.as_str()));
    }
}
