use crate::cache_corruption_detector::{
    CorruptionCandidate, CorruptionEvidence, CorruptionReport, CorruptionSettings, DetectionMethod,
    FileFingerprint, StructuralCoverage, StructuralEvidence, StructuralIssue,
    CORRUPTION_CONTRACT_VERSION,
};
use crate::cache_structural_state::{
    LookupInput, ReuseDecision, StateNamespace, StructuralScanMode, StructuralScanSummary,
    StructuralState, SuccessfulOutcome,
};
use crate::{cache_utils, cancel, progress_utils};
use anyhow::{bail, Context, Result};
use chrono::{DateTime, SecondsFormat, Utc};
use crossbeam_channel::{bounded, Receiver, RecvTimeoutError, Sender, TrySendError};
use jwalk::WalkDir;
use serde::Serialize;
use std::collections::{BTreeMap, VecDeque};
#[cfg(target_os = "linux")]
use std::ffi::{CString, OsString};
use std::fs::{File, Metadata, OpenOptions};
use std::io::{Read, Seek, SeekFrom};
#[cfg(target_os = "linux")]
use std::os::fd::{AsRawFd, FromRawFd};
#[cfg(unix)]
use std::os::unix::ffi::OsStrExt;
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex, RwLock};
use std::time::{Duration, Instant, SystemTime};

pub const MAX_PREFIX_BYTES: u64 = u16::MAX as u64;
pub const MIN_STABLE_AGE_SECONDS: u64 = 600;
pub const STRUCTURAL_SCANNER_POLICY_VERSION: u32 = 1;
const STATE_CLASSIFICATION_BATCH: usize = 500;
const KEY_MARKER: &[u8] = b"\nKEY: ";

#[allow(dead_code)]
#[repr(C)]
struct NginxCacheHeaderV5Layout {
    version: usize,
    valid_sec: libc::time_t,
    updating_sec: libc::time_t,
    error_sec: libc::time_t,
    last_modified: libc::time_t,
    date: libc::time_t,
    crc32: u32,
    valid_msec: u16,
    header_start: u16,
    body_start: u16,
    etag_len: u8,
    etag: [u8; 128],
    vary_len: u8,
    vary: [u8; 128],
    variant: [u8; 16],
}

#[derive(Clone, Copy)]
struct Layout {
    size: usize,
    version: usize,
    crc32: usize,
    header_start: usize,
    body_start: usize,
    etag_len: usize,
    vary_len: usize,
    variant: usize,
}

impl Layout {
    fn native() -> Option<Self> {
        #[cfg(unix)]
        {
            Some(Self {
                size: std::mem::size_of::<NginxCacheHeaderV5Layout>(),
                version: std::mem::offset_of!(NginxCacheHeaderV5Layout, version),
                crc32: std::mem::offset_of!(NginxCacheHeaderV5Layout, crc32),
                header_start: std::mem::offset_of!(NginxCacheHeaderV5Layout, header_start),
                body_start: std::mem::offset_of!(NginxCacheHeaderV5Layout, body_start),
                etag_len: std::mem::offset_of!(NginxCacheHeaderV5Layout, etag_len),
                vary_len: std::mem::offset_of!(NginxCacheHeaderV5Layout, vary_len),
                variant: std::mem::offset_of!(NginxCacheHeaderV5Layout, variant),
            })
        }
        #[cfg(not(unix))]
        {
            None
        }
    }

    fn signature(self) -> String {
        format!(
            "nginx-v5;levels=2:2;size={};version={};crc32={};header_start={};body_start={};etag_len={};vary_len={};variant={};max_prefix={};stable_age={}",
            self.size,
            self.version,
            self.crc32,
            self.header_start,
            self.body_start,
            self.etag_len,
            self.vary_len,
            self.variant,
            MAX_PREFIX_BYTES,
            MIN_STABLE_AGE_SECONDS,
        )
    }

    #[cfg(test)]
    fn linux_x86_64() -> Self {
        Self {
            size: 336,
            version: 0,
            crc32: 48,
            header_start: 54,
            body_start: 56,
            etag_len: 58,
            vary_len: 187,
            variant: 316,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum SkipReason {
    UnsupportedPlatform,
    UnsupportedCacheVersion,
    UnsupportedLayout,
    UnsupportedStatus,
    UnsupportedRangeShape,
    NoAuthoritativeLength,
    AmbiguousHttpHeaders,
    Recent,
    Changed,
    Replaced,
    Symlink,
    SpecialFile,
    ForeignPath,
    IoError,
    Cancelled,
}

impl SkipReason {
    fn as_str(self) -> &'static str {
        match self {
            Self::UnsupportedPlatform => "unsupported_platform",
            Self::UnsupportedCacheVersion => "unsupported_cache_version",
            Self::UnsupportedLayout => "unsupported_layout",
            Self::UnsupportedStatus => "unsupported_status",
            Self::UnsupportedRangeShape => "unsupported_range_shape",
            Self::NoAuthoritativeLength => "no_authoritative_length",
            Self::AmbiguousHttpHeaders => "ambiguous_http_headers",
            Self::Recent => "recent",
            Self::Changed => "changed",
            Self::Replaced => "replaced",
            Self::Symlink => "symlink",
            Self::SpecialFile => "special_file",
            Self::ForeignPath => "foreign_path",
            Self::IoError => "io_error",
            Self::Cancelled => "cancelled",
        }
    }
}

#[derive(Debug, Clone)]
struct ParsedStructural {
    issues: Vec<StructuralIssue>,
    cache_key: Vec<u8>,
    http_status: Option<u16>,
    header_start: Option<u16>,
    body_start: Option<u16>,
    actual_payload_length: Option<u64>,
    expected_payload_length: Option<u64>,
    content_length: Option<u64>,
    content_range: Option<String>,
}

#[derive(Debug, Clone)]
enum ParseOutcome {
    Consistent,
    Proven(ParsedStructural),
    Skip(SkipReason),
}

#[derive(Debug, Clone)]
pub struct Inspection {
    pub outcome: InspectionOutcome,
    pub bytes_read: u64,
    pub sparse: bool,
    verified_fingerprint: Option<FileFingerprint>,
}

#[derive(Debug, Clone)]
pub enum InspectionOutcome {
    Consistent,
    Proven(StructuralEvidence),
    Skip(&'static str),
}

#[derive(Debug)]
pub struct StructuralScanResult {
    pub report: CorruptionReport,
    pub cancelled: bool,
    pub scan_summary: StructuralScanSummary,
    #[allow(dead_code)]
    pipeline: PipelineTelemetry,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ScanProgress {
    status: String,
    stage_key: String,
    context: serde_json::Value,
    percent_complete: f64,
    files_processed: usize,
    total_files: usize,
    timestamp: String,
}

#[derive(Clone, Copy)]
struct StructuralScanConfig {
    inspection_parallelism: usize,
    initial_parallelism: usize,
    adaptive_concurrency: bool,
    task_queue_capacity: usize,
    result_queue_capacity: usize,
    progress_interval: Duration,
    layout: Option<Layout>,
}

/// Upper bound on inspection threads. Not a tuning knob: the controller only climbs this high
/// if the storage keeps paying for more requests in flight, which in practice only NVMe and
/// wide SAS arrays do. It exists so a pathological device cannot spawn threads without end.
const MAX_INSPECTION_PARALLELISM: usize = 64;

/// Where the climb starts. Deliberately low, so a single spinning disk is never hammered with
/// a deep queue before the controller has measured anything.
const INITIAL_INSPECTION_PARALLELISM: usize = 4;

impl StructuralScanConfig {
    fn production(_root: &Path) -> Self {
        // Concurrency is measured, never guessed from the filesystem type. A fixed number is
        // wrong for every backend at once: a single spinning disk saturates around a queue
        // depth of a handful, a NAS is bound by its own spindles, and NVMe or a wide SAS array
        // wants dozens of requests in flight. The controller climbs while throughput improves
        // and settles where the device stops rewarding more concurrency, so the same build
        // adapts to whatever the cache actually lives on.
        let inspection_parallelism = MAX_INSPECTION_PARALLELISM;
        Self {
            inspection_parallelism,
            initial_parallelism: INITIAL_INSPECTION_PARALLELISM,
            adaptive_concurrency: true,
            task_queue_capacity: inspection_parallelism,
            result_queue_capacity: inspection_parallelism,
            progress_interval: Duration::from_secs(1),
            layout: Layout::native(),
        }
    }

    fn normalized(mut self) -> Self {
        self.inspection_parallelism = self.inspection_parallelism.max(1);
        self.initial_parallelism = self
            .initial_parallelism
            .clamp(1, self.inspection_parallelism);
        self.task_queue_capacity = self.task_queue_capacity.max(1);
        self.result_queue_capacity = self.result_queue_capacity.max(1);
        if self.progress_interval.is_zero() {
            self.progress_interval = Duration::from_millis(1);
        }
        self
    }
}

#[derive(Debug)]
struct InspectionTask {
    path: PathBuf,
    path_digest: u128,
    revalidation: bool,
}

#[derive(Debug)]
struct InspectionResult {
    path: PathBuf,
    path_digest: u128,
    revalidation: bool,
    inspection: Result<Inspection>,
}

trait ScanClock: Sync {
    fn elapsed(&self) -> Duration;
}

struct RealScanClock {
    started_at: Instant,
}

impl RealScanClock {
    fn new() -> Self {
        Self {
            started_at: Instant::now(),
        }
    }
}

impl ScanClock for RealScanClock {
    fn elapsed(&self) -> Duration {
        self.started_at.elapsed()
    }
}

trait InspectionObserver: Sync {
    fn task_scheduled(&self, _path: &Path) {}
    fn inspection_started(&self, _path: &Path) {}
    fn inspection_wait(&self, _path: &Path) {}
    fn inspection_after_read(&self, _path: &Path) {}
    fn inspection_completed(&self, _path: &Path, _inspection: &Result<Inspection>) {}
    fn result_merged(&self) {}
    fn cancellation_observed(&self) {}
}

#[derive(Debug, Clone, Copy, Default)]
#[allow(dead_code)]
struct PipelineTelemetry {
    worker_count: usize,
    /// Highest concurrency the controller ever ran at. `worker_count` is where it ended up;
    /// this is how far it explored, which is the only way to tell a scan that adapted from one
    /// that sat at its starting value the whole time.
    peak_worker_count: usize,
    task_queue_high_water: usize,
    result_queue_high_water: usize,
    outstanding_high_water: usize,
    scheduled: usize,
    completed: usize,
    merged: usize,
    inspection_elapsed_seconds: f64,
    final_files_per_second: Option<f64>,
}

#[derive(Default)]
struct PipelineCounters {
    task_queue_high_water: AtomicUsize,
    result_queue_high_water: AtomicUsize,
    outstanding_high_water: AtomicUsize,
    concurrency_high_water: AtomicUsize,
    completed: AtomicUsize,
}

fn record_high_water(counter: &AtomicUsize, value: usize) {
    let mut current = counter.load(Ordering::Relaxed);
    while value > current {
        match counter.compare_exchange_weak(current, value, Ordering::Relaxed, Ordering::Relaxed) {
            Ok(_) => break,
            Err(actual) => current = actual,
        }
    }
}

/// Caps how many inspections may be in flight at once. Worker threads are cheap and mostly
/// parked on I/O, so the pool is sized once at the maximum and the *permitted* concurrency is
/// what moves. Growing the limit wakes parked workers; shrinking it simply stops handing out
/// permits until enough in-flight work drains, so a shrink never interrupts a read midway.
struct ConcurrencyLimiter {
    state: Mutex<LimiterState>,
    available: Condvar,
    current_limit: AtomicUsize,
}

struct LimiterState {
    limit: usize,
    in_flight: usize,
}

impl ConcurrencyLimiter {
    fn new(limit: usize) -> Self {
        let limit = limit.max(1);
        Self {
            state: Mutex::new(LimiterState {
                limit,
                in_flight: 0,
            }),
            available: Condvar::new(),
            current_limit: AtomicUsize::new(limit),
        }
    }

    fn limit(&self) -> usize {
        self.current_limit.load(Ordering::Relaxed)
    }

    fn set_limit(&self, limit: usize) {
        let limit = limit.max(1);
        if let Ok(mut state) = self.state.lock() {
            state.limit = limit;
            self.current_limit.store(limit, Ordering::Relaxed);
        }
        self.available.notify_all();
    }

    /// Blocks until a slot frees up. Returns `None` once the pipeline is stopping, so a worker
    /// parked here during cancellation wakes and exits instead of stranding the scan.
    fn acquire(&self, stop: &AtomicBool) -> Option<ConcurrencyPermit<'_>> {
        let mut state = self.state.lock().ok()?;
        loop {
            if stop.load(Ordering::Acquire) || cancel::is_cancelled() {
                return None;
            }
            if state.in_flight < state.limit {
                state.in_flight += 1;
                return Some(ConcurrencyPermit { limiter: self });
            }
            let (next, _) = self
                .available
                .wait_timeout(state, Duration::from_millis(50))
                .ok()?;
            state = next;
        }
    }
}

struct ConcurrencyPermit<'a> {
    limiter: &'a ConcurrencyLimiter,
}

impl Drop for ConcurrencyPermit<'_> {
    fn drop(&mut self) {
        if let Ok(mut state) = self.limiter.state.lock() {
            state.in_flight = state.in_flight.saturating_sub(1);
        }
        self.limiter.available.notify_one();
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ConcurrencyPhase {
    Climbing,
    Settled,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ProbeDirection {
    Up,
    Down,
}

/// Finds the concurrency the storage actually wants, by trying a step and keeping what pays.
///
/// Each window it measures completed-files-per-second. While stepping in the current direction
/// keeps buying throughput it steps again; when the gain stops it falls back to the best limit,
/// reverses direction, and settles. A single spinning disk settles at a shallow queue, a NAS at
/// whatever its spindles sustain, NVMe near the ceiling.
///
/// Two things matter for a scan that runs for hours. The baseline it compares against is the
/// rate re-measured at the settled limit, never an all-time high: a high-water mark taken when
/// the NAS was idle could never be beaten again once the NAS got busy, and the limit would be
/// frozen for the rest of the scan. And because direction reverses on a failed step, a settled
/// scan probes *down* as readily as up, so storage that gets busier mid-scan is met by backing
/// off rather than by hammering it with a queue depth it can no longer absorb.
struct ConcurrencyController {
    max: usize,
    limit: usize,
    phase: ConcurrencyPhase,
    /// Throughput at `best_limit`, re-measured under current conditions while settled.
    baseline_rate: f64,
    best_limit: usize,
    direction: ProbeDirection,
    window_started_at: Duration,
    window_start_completed: usize,
    next_probe_at: Duration,
}

/// A window must cover at least this much wall-clock before its rate means anything.
const CONCURRENCY_WINDOW: Duration = Duration::from_secs(5);
/// ...and this many files, unless the storage is so slow that waiting longer is pointless.
const CONCURRENCY_WINDOW_MIN_FILES: usize = 64;
/// Never wait longer than this for a window, or a very slow device would never adapt at all.
const CONCURRENCY_WINDOW_MAX: Duration = Duration::from_secs(30);
/// A step must buy more than this to be worth keeping. Below it, the extra queue depth is noise.
const CONCURRENCY_IMPROVEMENT: f64 = 1.05;
/// How often a settled scan re-checks whether the device has freed up.
const CONCURRENCY_REPROBE: Duration = Duration::from_secs(120);

impl ConcurrencyController {
    fn new(initial: usize, max: usize, now: Duration) -> Self {
        let max = max.max(1);
        let limit = initial.clamp(1, max);
        Self {
            max,
            limit,
            phase: ConcurrencyPhase::Climbing,
            baseline_rate: 0.0,
            best_limit: limit,
            direction: ProbeDirection::Up,
            window_started_at: now,
            window_start_completed: 0,
            next_probe_at: now.saturating_add(CONCURRENCY_REPROBE),
        }
    }

    /// Moves the limit one step in the current direction. `false` when the ladder has no more
    /// rungs that way (already at the ceiling, or already down to a single request in flight).
    fn step(&mut self) -> bool {
        let next = match self.direction {
            ProbeDirection::Up => self.limit.saturating_mul(2).min(self.max),
            ProbeDirection::Down => (self.limit / 2).max(1),
        };
        if next == self.limit {
            return false;
        }
        self.limit = next;
        true
    }

    fn reverse(&mut self) {
        self.direction = match self.direction {
            ProbeDirection::Up => ProbeDirection::Down,
            ProbeDirection::Down => ProbeDirection::Up,
        };
    }

    /// Keeps the step that just paid off, then immediately tries another one the same way. This
    /// is what makes a re-probe resume a full climb instead of inching one rung every probe
    /// interval.
    fn keep_and_continue(&mut self, rate: f64, now: Duration) {
        self.baseline_rate = rate;
        self.best_limit = self.limit;
        if !self.step() {
            self.settle(now);
        }
    }

    /// The step did not pay. Fall back to the best limit and reverse, so the next probe explores
    /// the other direction rather than retrying the one that just failed.
    fn revert_and_settle(&mut self, now: Duration) {
        self.limit = self.best_limit;
        self.reverse();
        self.settle(now);
    }

    fn window_ready(&self, now: Duration, completed: usize) -> bool {
        let elapsed = now.saturating_sub(self.window_started_at);
        if elapsed < CONCURRENCY_WINDOW {
            return false;
        }
        let files = completed.saturating_sub(self.window_start_completed);
        if files == 0 {
            return false;
        }
        files >= CONCURRENCY_WINDOW_MIN_FILES || elapsed >= CONCURRENCY_WINDOW_MAX
    }

    /// Feeds one observation in. Returns the new limit when it changed, so the caller can push
    /// it to the limiter.
    fn observe(&mut self, now: Duration, completed: usize) -> Option<usize> {
        if !self.window_ready(now, completed) {
            return None;
        }
        let elapsed = now
            .saturating_sub(self.window_started_at)
            .as_secs_f64()
            .max(f64::MIN_POSITIVE);
        let files = completed.saturating_sub(self.window_start_completed) as f64;
        let rate = files / elapsed;
        let previous = self.limit;

        match self.phase {
            // A probe is just a climb step, so both are the same rule: keep what pays and push
            // further the same way, otherwise fall back and turn around.
            ConcurrencyPhase::Climbing => {
                if rate > self.baseline_rate * CONCURRENCY_IMPROVEMENT {
                    self.keep_and_continue(rate, now);
                } else {
                    self.revert_and_settle(now);
                }
            }
            ConcurrencyPhase::Settled => {
                // Re-measure the yardstick at the limit we settled on. Comparing a probe
                // against a stale high-water rate from a quieter moment would make every probe
                // look like a failure, and the limit would never move again.
                self.baseline_rate = rate;
                if now >= self.next_probe_at {
                    if self.step() {
                        self.phase = ConcurrencyPhase::Climbing;
                    } else {
                        // No rung available this way. Turn around and wait for the next probe.
                        self.reverse();
                        self.next_probe_at = now.saturating_add(CONCURRENCY_REPROBE);
                    }
                }
            }
        }

        self.window_started_at = now;
        self.window_start_completed = completed;
        (self.limit != previous).then_some(self.limit)
    }

    fn settle(&mut self, now: Duration) {
        self.phase = ConcurrencyPhase::Settled;
        self.next_probe_at = now.saturating_add(CONCURRENCY_REPROBE);
    }
}

/// Feeds the completed-file count to the controller and applies any new limit. A no-op when
/// adaptation is off, which keeps the fixed-parallelism tests and the serial reference exact.
fn retune_concurrency(
    controller: &mut Option<ConcurrencyController>,
    limiter: &ConcurrencyLimiter,
    counters: &PipelineCounters,
    now: Duration,
) {
    let Some(controller) = controller.as_mut() else {
        return;
    };
    let completed = counters.completed.load(Ordering::Relaxed);
    if let Some(limit) = controller.observe(now, completed) {
        limiter.set_limit(limit);
        record_high_water(&counters.concurrency_high_water, limit);
    }
}

struct InspectionTelemetry {
    phase_started_at: Duration,
    last_progress_at: Duration,
    last_sample_at: Duration,
    last_sample_count: usize,
    ewma_files_per_second: Option<f64>,
}

impl InspectionTelemetry {
    fn new(now: Duration) -> Self {
        Self {
            phase_started_at: now,
            last_progress_at: now,
            last_sample_at: now,
            last_sample_count: 0,
            ewma_files_per_second: None,
        }
    }

    fn progress_due(&self, now: Duration, interval: Duration) -> bool {
        now.saturating_sub(self.last_progress_at) >= interval
    }

    fn sample(&mut self, now: Duration, count: usize) -> ProgressRate {
        let sample_elapsed = now.saturating_sub(self.last_sample_at).as_secs_f64();
        if sample_elapsed > 0.0 {
            let completed = count.saturating_sub(self.last_sample_count) as f64;
            let sample_rate = completed / sample_elapsed;
            if sample_rate.is_finite() && sample_rate >= 0.0 {
                self.ewma_files_per_second = Some(match self.ewma_files_per_second {
                    Some(previous) => previous * 0.75 + sample_rate * 0.25,
                    None => sample_rate,
                });
            }
            self.last_sample_at = now;
            self.last_sample_count = count;
        }
        self.last_progress_at = now;
        ProgressRate {
            elapsed_seconds: now.saturating_sub(self.phase_started_at).as_secs_f64(),
            files_per_second: self.ewma_files_per_second,
        }
    }
}

#[derive(Clone, Copy)]
struct ProgressRate {
    elapsed_seconds: f64,
    files_per_second: Option<f64>,
}

#[derive(Clone, Copy)]
struct ProgressDetails {
    rate: ProgressRate,
    eta_seconds: Option<u64>,
    worker_count: usize,
    task_queue_capacity: usize,
    result_queue_capacity: usize,
}

fn read_usize(bytes: &[u8], offset: usize) -> Option<usize> {
    let raw: [u8; std::mem::size_of::<usize>()] = bytes
        .get(offset..offset.checked_add(std::mem::size_of::<usize>())?)?
        .try_into()
        .ok()?;
    Some(usize::from_ne_bytes(raw))
}

fn read_u32(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_ne_bytes(
        bytes.get(offset..offset.checked_add(4)?)?.try_into().ok()?,
    ))
}

fn read_u16(bytes: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_ne_bytes(
        bytes.get(offset..offset.checked_add(2)?)?.try_into().ok()?,
    ))
}

fn hex_bytes(bytes: &[u8]) -> String {
    use std::fmt::Write;
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(result, "{byte:02x}");
    }
    result
}

#[derive(Debug)]
struct HttpMetadata {
    status: u16,
    content_length: Option<u64>,
    content_range: Option<(u64, String)>,
    chunked: bool,
    multipart: bool,
}

fn parse_decimal(value: &[u8]) -> Option<u64> {
    if value.is_empty() || !value.iter().all(u8::is_ascii_digit) {
        return None;
    }
    std::str::from_utf8(value).ok()?.parse().ok()
}

fn trim_ascii(mut value: &[u8]) -> &[u8] {
    while value.first().is_some_and(u8::is_ascii_whitespace) {
        value = &value[1..];
    }
    while value.last().is_some_and(u8::is_ascii_whitespace) {
        value = &value[..value.len() - 1];
    }
    value
}

fn parse_content_range(value: &[u8]) -> Option<(u64, String)> {
    let text = std::str::from_utf8(trim_ascii(value)).ok()?;
    let rest = text.strip_prefix("bytes ")?;
    if rest.contains(',') {
        return None;
    }
    let (range, total) = rest.split_once('/')?;
    let (start, end) = range.split_once('-')?;
    let start = start.parse::<u64>().ok()?;
    let end = end.parse::<u64>().ok()?;
    if start > end {
        return None;
    }
    if total != "*" {
        let total = total.parse::<u64>().ok()?;
        if end >= total {
            return None;
        }
    }
    let span = end.checked_sub(start)?.checked_add(1)?;
    Some((span, text.to_string()))
}

fn parse_http_headers(bytes: &[u8]) -> std::result::Result<HttpMetadata, SkipReason> {
    if !bytes.is_ascii() {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    let normalized = bytes
        .split(|byte| *byte == b'\n')
        .map(|line| line.strip_suffix(b"\r").unwrap_or(line))
        .collect::<Vec<_>>();
    let trailing_empty = normalized
        .iter()
        .rev()
        .take_while(|line| line.is_empty())
        .count();
    if trailing_empty < 2 {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    let mut lines = normalized.as_slice();
    while lines.last().is_some_and(|line| line.is_empty()) {
        lines = &lines[..lines.len() - 1];
    }
    let Some(status_line) = lines.first().copied() else {
        return Err(SkipReason::AmbiguousHttpHeaders);
    };
    if lines.iter().any(|line| line.is_empty()) {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    let status_text =
        std::str::from_utf8(status_line).map_err(|_| SkipReason::AmbiguousHttpHeaders)?;
    let mut status_parts = status_text.split_ascii_whitespace();
    if !matches!(status_parts.next(), Some("HTTP/1.0" | "HTTP/1.1")) {
        return Err(SkipReason::UnsupportedStatus);
    }
    let status = status_parts
        .next()
        .and_then(|value| value.parse::<u16>().ok())
        .ok_or(SkipReason::AmbiguousHttpHeaders)?;
    if !matches!(status, 200 | 206) {
        return Err(SkipReason::UnsupportedStatus);
    }

    let mut content_length = None;
    let mut content_range = None;
    let mut transfer_encoding = None::<Vec<u8>>;
    let mut multipart = false;
    for line in &lines[1..] {
        if line.is_empty() {
            continue;
        }
        if line
            .first()
            .is_some_and(|byte| matches!(byte, b' ' | b'\t'))
        {
            return Err(SkipReason::AmbiguousHttpHeaders);
        }
        let Some(separator) = line.iter().position(|byte| *byte == b':') else {
            return Err(SkipReason::AmbiguousHttpHeaders);
        };
        let (name, with_separator) = line.split_at(separator);
        let raw_value = &with_separator[1..];
        if name.is_empty()
            || !name
                .iter()
                .all(|byte| byte.is_ascii_alphanumeric() || *byte == b'-')
        {
            return Err(SkipReason::AmbiguousHttpHeaders);
        }
        let name = name.iter().map(u8::to_ascii_lowercase).collect::<Vec<_>>();
        let value = trim_ascii(raw_value);
        match name.as_slice() {
            b"content-length" => {
                if content_length.is_some() {
                    return Err(SkipReason::AmbiguousHttpHeaders);
                }
                content_length =
                    Some(parse_decimal(value).ok_or(SkipReason::AmbiguousHttpHeaders)?);
            }
            b"content-range" => {
                if content_range.is_some() {
                    return Err(SkipReason::AmbiguousHttpHeaders);
                }
                content_range =
                    Some(parse_content_range(value).ok_or(SkipReason::AmbiguousHttpHeaders)?);
            }
            b"transfer-encoding" => {
                if transfer_encoding.is_some() {
                    return Err(SkipReason::AmbiguousHttpHeaders);
                }
                transfer_encoding = Some(value.iter().map(u8::to_ascii_lowercase).collect());
            }
            b"content-type" => {
                let lower = value.iter().map(u8::to_ascii_lowercase).collect::<Vec<_>>();
                multipart = lower.starts_with(b"multipart/byteranges");
            }
            _ => {}
        }
    }
    let chunked = transfer_encoding.as_deref().is_some_and(|value| {
        value
            .split(|byte| *byte == b',')
            .last()
            .is_some_and(|coding| trim_ascii(coding) == b"chunked")
    });
    if transfer_encoding.is_some() && !chunked {
        return Err(SkipReason::AmbiguousHttpHeaders);
    }
    Ok(HttpMetadata {
        status,
        content_length,
        content_range,
        chunked,
        multipart,
    })
}

fn parse_prefix(prefix: &[u8], file_len: u64, path_digest: u128, layout: Layout) -> ParseOutcome {
    if file_len == 0 {
        return ParseOutcome::Proven(ParsedStructural {
            issues: vec![StructuralIssue::EmptyCacheFile],
            cache_key: Vec::new(),
            http_status: None,
            header_start: None,
            body_start: None,
            actual_payload_length: None,
            expected_payload_length: None,
            content_length: None,
            content_range: None,
        });
    }
    let Some(version) = read_usize(prefix, layout.version) else {
        return ParseOutcome::Skip(SkipReason::UnsupportedLayout);
    };
    if version != 5 {
        return ParseOutcome::Skip(SkipReason::UnsupportedCacheVersion);
    }
    let fixed_end = match layout.size.checked_add(KEY_MARKER.len()) {
        Some(value) => value,
        None => return ParseOutcome::Skip(SkipReason::UnsupportedLayout),
    };
    if file_len < fixed_end as u64 || prefix.len() < fixed_end {
        return ParseOutcome::Proven(ParsedStructural {
            issues: vec![StructuralIssue::TruncatedCacheHeader],
            cache_key: Vec::new(),
            http_status: None,
            header_start: None,
            body_start: None,
            actual_payload_length: None,
            expected_payload_length: None,
            content_length: None,
            content_range: None,
        });
    }
    let Some(header_start) = read_u16(prefix, layout.header_start) else {
        return ParseOutcome::Skip(SkipReason::UnsupportedLayout);
    };
    let Some(body_start) = read_u16(prefix, layout.body_start) else {
        return ParseOutcome::Skip(SkipReason::UnsupportedLayout);
    };
    if prefix.get(layout.size..fixed_end) != Some(KEY_MARKER) {
        return proven_envelope(
            StructuralIssue::MalformedCacheHeader,
            header_start,
            body_start,
        );
    }
    if header_start as usize <= fixed_end {
        return proven_envelope(
            StructuralIssue::MalformedCacheHeader,
            header_start,
            body_start,
        );
    }
    if header_start > body_start {
        return proven_envelope(
            StructuralIssue::InvalidPayloadOffset,
            header_start,
            body_start,
        );
    }
    if u64::from(header_start) > file_len {
        return proven_envelope(
            StructuralIssue::TruncatedCacheHeader,
            header_start,
            body_start,
        );
    }
    if u64::from(body_start) > file_len {
        return proven_envelope(
            StructuralIssue::TruncatedBeforePayload,
            header_start,
            body_start,
        );
    }
    let header_start_usize = usize::from(header_start);
    let body_start_usize = usize::from(body_start);
    if prefix.len() < body_start_usize || prefix.get(header_start_usize - 1) != Some(&b'\n') {
        return proven_envelope(
            StructuralIssue::MalformedCacheHeader,
            header_start,
            body_start,
        );
    }
    let cache_key = prefix[fixed_end..header_start_usize - 1].to_vec();
    let stored_crc = read_u32(prefix, layout.crc32).unwrap_or_default();
    let etag_len = prefix.get(layout.etag_len).copied().unwrap_or(u8::MAX);
    let vary_len = prefix.get(layout.vary_len).copied().unwrap_or(u8::MAX);
    if cache_key.is_empty()
        || stored_crc != crc32fast::hash(&cache_key)
        || etag_len > 128
        || vary_len > 128
    {
        return proven_with_key(
            StructuralIssue::MalformedCacheHeader,
            cache_key,
            header_start,
            body_start,
        );
    }

    let main_digest = u128::from_be_bytes(md5::compute(&cache_key).0);
    let variant_digest = prefix
        .get(layout.variant..layout.variant + 16)
        .and_then(|bytes| <[u8; 16]>::try_from(bytes).ok())
        .map(u128::from_be_bytes);
    if path_digest != main_digest && !(vary_len > 0 && variant_digest == Some(path_digest)) {
        return proven_with_key(
            StructuralIssue::CacheKeyPathMismatch,
            cache_key,
            header_start,
            body_start,
        );
    }

    let actual = file_len - u64::from(body_start);
    let http = match parse_http_headers(&prefix[header_start_usize..body_start_usize]) {
        Ok(value) => value,
        Err(reason) => return ParseOutcome::Skip(reason),
    };
    let mut issues = Vec::new();
    let expected = match http.status {
        200 if http.chunked => return ParseOutcome::Skip(SkipReason::NoAuthoritativeLength),
        200 => {
            let Some(length) = http.content_length else {
                return ParseOutcome::Skip(SkipReason::NoAuthoritativeLength);
            };
            if actual != length {
                issues.push(StructuralIssue::PayloadLengthMismatch);
            }
            Some(length)
        }
        206 => {
            if http.multipart {
                return ParseOutcome::Skip(SkipReason::UnsupportedRangeShape);
            }
            let Some((span, _)) = http.content_range.as_ref() else {
                return ParseOutcome::Skip(SkipReason::UnsupportedRangeShape);
            };
            if !http.chunked && http.content_length.is_some_and(|length| length != *span) {
                issues.push(StructuralIssue::ContentLengthRangeConflict);
            }
            if actual != *span {
                issues.push(StructuralIssue::ContentRangeLengthMismatch);
            }
            Some(*span)
        }
        _ => return ParseOutcome::Skip(SkipReason::UnsupportedStatus),
    };
    if issues.is_empty() {
        ParseOutcome::Consistent
    } else {
        ParseOutcome::Proven(ParsedStructural {
            issues,
            cache_key,
            http_status: Some(http.status),
            header_start: Some(header_start),
            body_start: Some(body_start),
            actual_payload_length: Some(actual),
            expected_payload_length: expected,
            content_length: http.content_length,
            content_range: http.content_range.map(|(_, raw)| raw),
        })
    }
}

fn proven_envelope(issue: StructuralIssue, header_start: u16, body_start: u16) -> ParseOutcome {
    proven_with_key(issue, Vec::new(), header_start, body_start)
}

fn proven_with_key(
    issue: StructuralIssue,
    cache_key: Vec<u8>,
    header_start: u16,
    body_start: u16,
) -> ParseOutcome {
    ParseOutcome::Proven(ParsedStructural {
        issues: vec![issue],
        cache_key,
        http_status: None,
        header_start: Some(header_start),
        body_start: Some(body_start),
        actual_payload_length: None,
        expected_payload_length: None,
        content_length: None,
        content_range: None,
    })
}

#[cfg(unix)]
fn fingerprint(metadata: &Metadata) -> FileFingerprint {
    use std::os::unix::fs::MetadataExt;
    FileFingerprint {
        dev: metadata.dev(),
        ino: metadata.ino(),
        len: metadata.len(),
        mtime_ns: metadata
            .mtime()
            .saturating_mul(1_000_000_000)
            .saturating_add(metadata.mtime_nsec()),
        ctime_ns: metadata
            .ctime()
            .saturating_mul(1_000_000_000)
            .saturating_add(metadata.ctime_nsec()),
    }
}

#[cfg(unix)]
fn canonical_root_identity(path: &Path) -> String {
    hex_bytes(path.as_os_str().as_bytes())
}

fn root_namespace_fingerprint(metadata: &Metadata) -> FileFingerprint {
    let identity = fingerprint(metadata);
    FileFingerprint {
        dev: identity.dev,
        ino: identity.ino,
        len: 0,
        mtime_ns: 0,
        ctime_ns: 0,
    }
}

#[cfg(unix)]
fn strong_verified_fingerprint(metadata: &Metadata) -> Option<FileFingerprint> {
    Some(fingerprint(metadata))
}

#[cfg(not(unix))]
fn strong_verified_fingerprint(_metadata: &Metadata) -> Option<FileFingerprint> {
    None
}

#[cfg(not(unix))]
fn canonical_root_identity(path: &Path) -> String {
    path.to_string_lossy().to_lowercase()
}

#[cfg(unix)]
fn reusable_path_fingerprint(root: &Path, path: &Path, now: SystemTime) -> Option<FileFingerprint> {
    let metadata = std::fs::symlink_metadata(path).ok()?;
    if metadata.file_type().is_symlink()
        || !metadata.file_type().is_file()
        || !stable_age(&metadata, now)
        || has_symlink_component(root, path).ok()?
    {
        return None;
    }
    Some(fingerprint(&metadata))
}

#[cfg(not(unix))]
fn reusable_path_fingerprint(
    _root: &Path,
    _path: &Path,
    _now: SystemTime,
) -> Option<FileFingerprint> {
    // The current non-Unix fingerprint lacks volume/file identity. Inspect rather than
    // trusting length+mtime, which can collide after replacement.
    None
}

pub fn path_fingerprint_matches(path: &Path, expected: &FileFingerprint) -> Result<bool> {
    let metadata = match std::fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(error) => {
            return Err(error).with_context(|| format!("failed to restat {}", path.display()))
        }
    };
    Ok(metadata.file_type().is_file()
        && !metadata.file_type().is_symlink()
        && fingerprint(&metadata) == *expected)
}

#[cfg(not(unix))]
fn fingerprint(metadata: &Metadata) -> FileFingerprint {
    let modified = metadata
        .modified()
        .ok()
        .and_then(|value| value.duration_since(SystemTime::UNIX_EPOCH).ok())
        .map(|value| i64::try_from(value.as_nanos()).unwrap_or(i64::MAX))
        .unwrap_or_default();
    FileFingerprint {
        dev: 0,
        ino: 0,
        len: metadata.len(),
        mtime_ns: modified,
        ctime_ns: modified,
    }
}

#[cfg(unix)]
fn sparse(metadata: &Metadata) -> bool {
    use std::os::unix::fs::MetadataExt;
    metadata.blocks().saturating_mul(512) < metadata.len()
}

#[cfg(not(unix))]
fn sparse(_metadata: &Metadata) -> bool {
    false
}

fn has_symlink_component(root: &Path, path: &Path) -> Result<bool> {
    let relative = path
        .strip_prefix(root)
        .with_context(|| format!("path is outside cache root: {}", path.display()))?;
    let mut current = root.to_path_buf();
    for component in relative.components() {
        let Component::Normal(name) = component else {
            return Ok(true);
        };
        current.push(name);
        match std::fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() => return Ok(true),
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
            Err(error) => {
                return Err(error).with_context(|| format!("failed to lstat {}", current.display()))
            }
        }
    }
    Ok(false)
}

fn open_nofollow(path: &Path) -> Result<File> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NOFOLLOW | libc::O_CLOEXEC);
    }
    options
        .open(path)
        .with_context(|| format!("failed to open cache file {}", path.display()))
}

enum PreparedInspectionFile {
    Ready { file: File, metadata: Metadata },
    Skip { reason: SkipReason, sparse: bool },
}

#[allow(dead_code)] // `Legacy` is the non-Linux safety fallback; production runs the rooted variant.
enum WorkerPathAccess {
    #[cfg(target_os = "linux")]
    Rooted(LinuxRootedAccess),
    Legacy,
}

impl WorkerPathAccess {
    fn new(root: &Path) -> Result<Self> {
        #[cfg(target_os = "linux")]
        {
            return LinuxRootedAccess::new(root).map(Self::Rooted);
        }
        #[cfg(not(target_os = "linux"))]
        {
            let _ = root;
            Ok(Self::Legacy)
        }
    }

    fn prepare(
        &mut self,
        root: &Path,
        path: &Path,
        now: SystemTime,
    ) -> Result<PreparedInspectionFile> {
        #[cfg(target_os = "linux")]
        if let Self::Rooted(access) = self {
            return access.prepare(root, path, now);
        }

        if has_symlink_component(root, path)? {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::Symlink,
                sparse: false,
            });
        }
        let path_before = match std::fs::symlink_metadata(path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(PreparedInspectionFile::Skip {
                    reason: SkipReason::Changed,
                    sparse: false,
                });
            }
            Err(error) => {
                return Err(error).with_context(|| format!("failed to inspect {}", path.display()));
            }
        };
        if !path_before.file_type().is_file() {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::SpecialFile,
                sparse: false,
            });
        }
        if !stable_age(&path_before, now) {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::Recent,
                sparse: sparse(&path_before),
            });
        }
        cache_utils::safe_path_under_root(root, path)
            .with_context(|| format!("unsafe structural cache path {}", path.display()))?;
        let file = open_nofollow(path)?;
        let before = file.metadata()?;
        if fingerprint(&before) != fingerprint(&path_before) {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::Replaced,
                sparse: sparse(&before),
            });
        }
        Ok(PreparedInspectionFile::Ready {
            file,
            metadata: before,
        })
    }

    fn uses_rooted_access(&self) -> bool {
        #[cfg(target_os = "linux")]
        {
            matches!(self, Self::Rooted(_))
        }
        #[cfg(not(target_os = "linux"))]
        {
            false
        }
    }

    fn current_path_matches(
        &mut self,
        root: &Path,
        path: &Path,
        expected: &FileFingerprint,
    ) -> Result<bool> {
        #[cfg(target_os = "linux")]
        if let Self::Rooted(access) = self {
            return access.current_path_matches(root, path, expected);
        }
        #[cfg(not(target_os = "linux"))]
        let _ = root;
        path_fingerprint_matches(path, expected)
    }
}

#[cfg(target_os = "linux")]
struct LinuxRootedAccess {
    root: File,
    root_dev: u64,
    root_ino: u64,
    cached_parent: Option<(PathBuf, File)>,
}

#[cfg(target_os = "linux")]
impl LinuxRootedAccess {
    fn open_root(root: &Path) -> std::io::Result<File> {
        use std::os::unix::fs::OpenOptionsExt;

        let mut options = OpenOptions::new();
        options
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC);
        options.open(root)
    }

    fn new(root: &Path) -> Result<Self> {
        use std::os::unix::fs::MetadataExt;

        let root = Self::open_root(root)
            .with_context(|| "failed to open structural cache root for rooted inspection")?;
        let metadata = root.metadata()?;
        Ok(Self {
            root,
            root_dev: metadata.dev(),
            root_ino: metadata.ino(),
            cached_parent: None,
        })
    }

    fn relative_parts(root: &Path, path: &Path) -> Result<(PathBuf, OsString)> {
        let relative = path
            .strip_prefix(root)
            .with_context(|| format!("path is outside cache root: {}", path.display()))?;
        let leaf = relative
            .file_name()
            .context("structural cache path has no file name")?
            .to_os_string();
        let parent = relative.parent().unwrap_or_else(|| Path::new(""));
        if parent
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
        {
            bail!("structural cache path contains a non-normal parent component");
        }
        Ok((parent.to_path_buf(), leaf))
    }

    fn open_at(directory: &File, name: &std::ffi::OsStr, flags: i32) -> std::io::Result<File> {
        let name = CString::new(name.as_bytes()).map_err(|_| {
            std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "structural cache path contains an embedded NUL",
            )
        })?;
        let descriptor = unsafe { libc::openat(directory.as_raw_fd(), name.as_ptr(), flags) };
        if descriptor < 0 {
            return Err(std::io::Error::last_os_error());
        }
        Ok(unsafe { File::from_raw_fd(descriptor) })
    }

    fn open_parent_from(root: &File, relative: &Path) -> std::io::Result<File> {
        let mut current = root.try_clone()?;
        for component in relative.components() {
            let Component::Normal(name) = component else {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidInput,
                    "structural cache parent contains a non-normal component",
                ));
            };
            current = Self::open_at(
                &current,
                name,
                libc::O_PATH | libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC,
            )?;
        }
        Ok(current)
    }

    fn open_parent(&self, relative: &Path) -> std::io::Result<File> {
        Self::open_parent_from(&self.root, relative)
    }

    fn open_leaf_cached(
        &mut self,
        parent: &Path,
        leaf: &std::ffi::OsStr,
        flags: i32,
    ) -> std::io::Result<File> {
        let reuse = self
            .cached_parent
            .as_ref()
            .is_some_and(|(cached, _)| cached == parent);
        if !reuse {
            let directory = self.open_parent(parent)?;
            self.cached_parent = Some((parent.to_path_buf(), directory));
        }
        let Some((_, directory)) = self.cached_parent.as_ref() else {
            return Err(std::io::Error::other(
                "structural cache parent cache was not initialized",
            ));
        };
        Self::open_at(directory, leaf, flags)
    }

    fn unsafe_component(error: &std::io::Error) -> bool {
        matches!(error.raw_os_error(), Some(code) if code == libc::ELOOP || code == libc::ENOTDIR)
    }

    fn prepare(
        &mut self,
        root: &Path,
        path: &Path,
        now: SystemTime,
    ) -> Result<PreparedInspectionFile> {
        let (parent, leaf) = Self::relative_parts(root, path)?;
        let path_handle = match self.open_leaf_cached(
            &parent,
            &leaf,
            libc::O_PATH | libc::O_NOFOLLOW | libc::O_CLOEXEC,
        ) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(PreparedInspectionFile::Skip {
                    reason: SkipReason::Changed,
                    sparse: false,
                });
            }
            Err(error) if Self::unsafe_component(&error) => {
                return Ok(PreparedInspectionFile::Skip {
                    reason: SkipReason::Symlink,
                    sparse: false,
                });
            }
            Err(error) => {
                return Err(error).with_context(|| format!("failed to inspect {}", path.display()));
            }
        };
        let path_before = path_handle.metadata()?;
        if path_before.file_type().is_symlink() {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::Symlink,
                sparse: false,
            });
        }
        if !path_before.file_type().is_file() {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::SpecialFile,
                sparse: false,
            });
        }
        if !stable_age(&path_before, now) {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::Recent,
                sparse: sparse(&path_before),
            });
        }

        let file = match self.open_leaf_cached(
            &parent,
            &leaf,
            libc::O_RDONLY | libc::O_NONBLOCK | libc::O_NOFOLLOW | libc::O_CLOEXEC,
        ) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(PreparedInspectionFile::Skip {
                    reason: SkipReason::Changed,
                    sparse: false,
                });
            }
            Err(error) if Self::unsafe_component(&error) => {
                return Ok(PreparedInspectionFile::Skip {
                    reason: SkipReason::Replaced,
                    sparse: false,
                });
            }
            Err(error) => {
                return Err(error)
                    .with_context(|| format!("failed to open cache file {}", path.display()));
            }
        };
        let before = file.metadata()?;
        if !before.file_type().is_file() || fingerprint(&before) != fingerprint(&path_before) {
            return Ok(PreparedInspectionFile::Skip {
                reason: SkipReason::Replaced,
                sparse: sparse(&before),
            });
        }
        Ok(PreparedInspectionFile::Ready {
            file,
            metadata: before,
        })
    }

    fn current_path_matches(
        &self,
        root: &Path,
        path: &Path,
        expected: &FileFingerprint,
    ) -> Result<bool> {
        use std::os::unix::fs::MetadataExt;

        let (parent, leaf) = Self::relative_parts(root, path)?;
        let current_root = match Self::open_root(root) {
            Ok(current_root) => current_root,
            Err(error)
                if error.kind() == std::io::ErrorKind::NotFound
                    || Self::unsafe_component(&error) =>
            {
                return Ok(false);
            }
            Err(error) => return Err(error).context("failed to reopen structural cache root"),
        };
        let root_metadata = current_root.metadata()?;
        if root_metadata.dev() != self.root_dev || root_metadata.ino() != self.root_ino {
            return Ok(false);
        }
        let directory = match Self::open_parent_from(&current_root, &parent) {
            Ok(directory) => directory,
            Err(error)
                if error.kind() == std::io::ErrorKind::NotFound
                    || Self::unsafe_component(&error) =>
            {
                return Ok(false);
            }
            Err(error) => return Err(error).context("failed to reopen cache parent"),
        };
        let current = match Self::open_at(
            &directory,
            &leaf,
            libc::O_PATH | libc::O_NOFOLLOW | libc::O_CLOEXEC,
        ) {
            Ok(file) => file,
            Err(error)
                if error.kind() == std::io::ErrorKind::NotFound
                    || Self::unsafe_component(&error) =>
            {
                return Ok(false);
            }
            Err(error) => return Err(error).context("failed to reopen cache file"),
        };
        let metadata = current.metadata()?;
        Ok(metadata.file_type().is_file() && fingerprint(&metadata) == *expected)
    }
}

fn stable_age(metadata: &Metadata, now: SystemTime) -> bool {
    metadata
        .modified()
        .ok()
        .and_then(|modified| now.duration_since(modified).ok())
        .is_some_and(|age| age >= Duration::from_secs(MIN_STABLE_AGE_SECONDS))
}

fn read_prefix(file: &mut File, file_len: u64, layout: Layout) -> Result<(Vec<u8>, u64)> {
    // Read only far enough to discover the serialized body offset first. A corrupt body_start
    // must not make the scanner eagerly consume the rest of the fixed header (or payload bytes).
    let offset_probe_end = layout
        .body_start
        .checked_add(std::mem::size_of::<u16>())
        .context("nginx body_start offset overflowed")?;
    let first_len = usize::try_from(file_len.min(offset_probe_end as u64))?;
    let mut prefix = vec![0; first_len];
    file.read_exact(&mut prefix)
        .with_context(|| format!("failed to read {first_len}-byte nginx cache prefix"))?;
    let mut bytes_read = first_len as u64;
    if file_len >= layout.size as u64 {
        if let Some(body_start) = read_u16(&prefix, layout.body_start) {
            let target = u64::from(body_start).min(file_len).min(MAX_PREFIX_BYTES);
            if target > bytes_read {
                let target = usize::try_from(target)?;
                prefix.resize(target, 0);
                file.seek(SeekFrom::Start(bytes_read))?;
                file.read_exact(&mut prefix[bytes_read as usize..])
                    .context("failed to read through nginx payload offset")?;
                bytes_read = target as u64;
            }
        }
    }
    Ok((prefix, bytes_read))
}

// Retained for the structural-scanner test harness (see cache_corruption.rs); the scan hot
// path now calls inspect_path_with_layout directly with a pre-computed digest.
#[allow(dead_code)]
pub fn inspect_path(
    root: &Path,
    path: &Path,
    now: SystemTime,
    detected_at: DateTime<Utc>,
) -> Result<Inspection> {
    let Some(path_digest) = cache_utils::strict_cache_file_digest(root, path) else {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::ForeignPath.as_str()),
            bytes_read: 0,
            sparse: false,
            verified_fingerprint: None,
        });
    };
    let Some(layout) = Layout::native() else {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::UnsupportedPlatform.as_str()),
            bytes_read: 0,
            sparse: false,
            verified_fingerprint: None,
        });
    };
    inspect_path_with_layout(root, path, path_digest, now, detected_at, layout)
}

fn inspect_path_with_layout(
    root: &Path,
    path: &Path,
    path_digest: u128,
    now: SystemTime,
    detected_at: DateTime<Utc>,
    layout: Layout,
) -> Result<Inspection> {
    inspect_path_with_layout_and_hook(root, path, path_digest, now, detected_at, layout, || {})
}

fn inspect_path_with_layout_and_hook<F: FnOnce()>(
    root: &Path,
    path: &Path,
    path_digest: u128,
    now: SystemTime,
    detected_at: DateTime<Utc>,
    layout: Layout,
    after_read: F,
) -> Result<Inspection> {
    let mut access = WorkerPathAccess::new(root)?;
    inspect_path_with_layout_and_access(
        root,
        path,
        path_digest,
        now,
        detected_at,
        layout,
        &mut access,
        after_read,
    )
}

#[allow(clippy::too_many_arguments)]
fn inspect_path_with_layout_and_access<F: FnOnce()>(
    root: &Path,
    path: &Path,
    path_digest: u128,
    now: SystemTime,
    detected_at: DateTime<Utc>,
    layout: Layout,
    access: &mut WorkerPathAccess,
    after_read: F,
) -> Result<Inspection> {
    let (mut file, before) = match access.prepare(root, path, now)? {
        PreparedInspectionFile::Ready { file, metadata } => (file, metadata),
        PreparedInspectionFile::Skip { reason, sparse } => {
            return Ok(Inspection {
                outcome: InspectionOutcome::Skip(reason.as_str()),
                bytes_read: 0,
                sparse,
                verified_fingerprint: None,
            });
        }
    };
    let (prefix, bytes_read) = match read_prefix(&mut file, before.len(), layout) {
        Ok(value) => value,
        Err(error) => {
            let after = file.metadata()?;
            if fingerprint(&before) != fingerprint(&after) {
                return Ok(Inspection {
                    outcome: InspectionOutcome::Skip(SkipReason::Changed.as_str()),
                    bytes_read: 0,
                    sparse: sparse(&after),
                    verified_fingerprint: None,
                });
            }
            return Err(error)
                .with_context(|| format!("failed to inspect prefix for {}", path.display()));
        }
    };
    after_read();
    if cancel::is_cancelled() {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Cancelled.as_str()),
            bytes_read,
            sparse: sparse(&before),
            verified_fingerprint: None,
        });
    }
    let after = file.metadata()?;
    if fingerprint(&before) != fingerprint(&after) {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Changed.as_str()),
            bytes_read,
            sparse: sparse(&after),
            verified_fingerprint: None,
        });
    }

    // The legacy fallback preserves its historical all-file path restat. The Linux rooted-open
    // path already holds a symlink-safe directory/file descriptor; it only rebuilds the current
    // root-relative path for proven candidates, where an exact removable path will be persisted.
    if !access.uses_rooted_access()
        && !access.current_path_matches(root, path, &fingerprint(&before))?
    {
        return Ok(Inspection {
            outcome: InspectionOutcome::Skip(SkipReason::Changed.as_str()),
            bytes_read,
            sparse: sparse(&after),
            verified_fingerprint: None,
        });
    }
    let outcome = match parse_prefix(&prefix, before.len(), path_digest, layout) {
        ParseOutcome::Consistent => InspectionOutcome::Consistent,
        ParseOutcome::Skip(reason) => InspectionOutcome::Skip(reason.as_str()),
        ParseOutcome::Proven(parsed) => {
            if access.uses_rooted_access()
                && !access.current_path_matches(root, path, &fingerprint(&before))?
            {
                return Ok(Inspection {
                    outcome: InspectionOutcome::Skip(SkipReason::Changed.as_str()),
                    bytes_read,
                    sparse: sparse(&after),
                    verified_fingerprint: None,
                });
            }
            let key_md5 = format!(
                "{:032x}",
                u128::from_be_bytes(md5::compute(&parsed.cache_key).0)
            );
            InspectionOutcome::Proven(StructuralEvidence {
                issues: parsed.issues,
                cache_key_encoding: "hex".to_string(),
                cache_key: hex_bytes(&parsed.cache_key),
                cache_key_md5: key_md5,
                cache_version: 5,
                http_status: parsed.http_status,
                header_start: parsed.header_start,
                body_start: parsed.body_start,
                file_length: before.len(),
                actual_payload_length: parsed.actual_payload_length,
                expected_payload_length: parsed.expected_payload_length,
                content_length: parsed.content_length,
                content_range: parsed.content_range,
                fingerprint: fingerprint(&before),
                detected_at_utc: detected_at.to_rfc3339_opts(SecondsFormat::AutoSi, true),
            })
        }
    };
    Ok(Inspection {
        outcome,
        bytes_read,
        sparse: sparse(&before),
        verified_fingerprint: strong_verified_fingerprint(&before),
    })
}

fn service_from_key_hex(evidence: &StructuralEvidence) -> String {
    let bytes = evidence
        .cache_key
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            let text = std::str::from_utf8(pair).ok()?;
            u8::from_str_radix(text, 16).ok()
        })
        .collect::<Option<Vec<_>>>();
    let Some(bytes) = bytes else {
        return "unknown".to_string();
    };
    let prefix = bytes.split(|byte| *byte == b'/').next().unwrap_or_default();
    if prefix.is_empty()
        || !prefix
            .iter()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
    {
        return "unknown".to_string();
    }
    String::from_utf8(prefix.to_ascii_lowercase()).unwrap_or_else(|_| "unknown".to_string())
}

fn structural_candidate(path: &Path, evidence: StructuralEvidence) -> CorruptionCandidate {
    let service = service_from_key_hex(&evidence);
    let identity = format!(
        "{}|{}|{:?}|{}|{}|{}",
        path.display(),
        evidence.cache_key_md5,
        evidence.issues,
        evidence.fingerprint.dev,
        evidence.fingerprint.ino,
        evidence.fingerprint.ctime_ns
    );
    CorruptionCandidate {
        candidate_id: cache_utils::calculate_md5(&identity),
        service,
        exact_paths: vec![path.display().to_string()],
        evidence: CorruptionEvidence::Structural {
            structural: evidence,
        },
    }
}

fn validate_cached_candidate(
    root: &Path,
    current_path: &Path,
    expected_digest: u128,
    candidate: &CorruptionCandidate,
) -> Result<()> {
    if candidate.exact_paths.len() != 1 {
        bail!("persisted structural candidate must contain exactly one path");
    }
    let persisted_path = Path::new(&candidate.exact_paths[0]);
    let persisted_digest = persisted_path
        .file_name()
        .and_then(|name| name.to_str())
        .and_then(cache_utils::parse_cache_file_digest);
    if cache_utils::strict_cache_file_digest(root, current_path) != Some(expected_digest)
        || persisted_digest != Some(expected_digest)
    {
        bail!("persisted structural candidate path did not match current cache entry");
    }
    let CorruptionEvidence::Structural { structural } = &candidate.evidence else {
        bail!("persisted structural state contained non-structural evidence");
    };
    if structural.issues.is_empty() {
        bail!("persisted structural candidate contained no issues");
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn update_progress(
    progress_path: Option<&Path>,
    status: &str,
    pass: &str,
    processed: usize,
    total: usize,
    candidates: usize,
    coverage: &StructuralCoverage,
    details: ProgressDetails,
    scan_summary: &StructuralScanSummary,
) -> Result<()> {
    let Some(path) = progress_path else {
        return Ok(());
    };
    let counting = pass == "counting";
    // The counting pass does not yet know the total, so a percentage would be a misleading
    // frozen 0%. Emit a distinct, count-driven stage instead and leave the percent
    // indeterminate (zeroed) so the frontend can render the running eligible-file count.
    let stage_key = if counting {
        "signalr.corruptionDetect.enumerating"
    } else {
        "signalr.corruptionDetect.scanningHeaders"
    };
    let percent = if status == "completed" {
        100.0
    } else if counting {
        0.0
    } else if total == 0 {
        0.0
    } else {
        (processed as f64 / total as f64 * 100.0).min(99.9)
    };
    let mut context = serde_json::json!({
        "detectionMethod": "structural",
        "pass": pass,
        "filesProcessed": processed,
        "totalFiles": total,
        "totalCorrupted": candidates,
        "coverage": coverage,
        "elapsedSeconds": details.rate.elapsed_seconds,
        "workerCount": details.worker_count,
        "taskQueueCapacity": details.task_queue_capacity,
        "resultQueueCapacity": details.result_queue_capacity,
        "scanMode": scan_summary.scan_mode,
        "effectiveScanMode": scan_summary.effective_scan_mode,
        "baselineStatus": scan_summary.baseline_status,
        "resumed": scan_summary.resumed,
        "filesDiscovered": scan_summary.files_discovered,
        "filesReused": scan_summary.files_reused,
        "filesInspected": scan_summary.files_inspected,
        "filesRevalidated": scan_summary.files_revalidated,
        "invalidFiles": scan_summary.invalid_files,
        "filesPendingRetry": scan_summary.files_pending_retry,
        "filesPruned": scan_summary.files_pruned,
        "stateEntries": scan_summary.state_entries,
        "stateCommitted": scan_summary.state_committed,
    });
    if counting {
        // `processed` carries the running eligible-file count during the counting pass.
        context["count"] = serde_json::json!(processed);
    }
    if let Some(rate) = details
        .rate
        .files_per_second
        .filter(|rate| rate.is_finite() && *rate >= 0.0)
    {
        context["filesPerSecond"] = serde_json::json!(rate);
    }
    if !counting {
        if let Some(eta_seconds) = details.eta_seconds {
            context["etaSeconds"] = serde_json::json!(eta_seconds);
        }
    }
    progress_utils::write_progress_json(
        path,
        &ScanProgress {
            status: status.to_string(),
            stage_key: stage_key.to_string(),
            context,
            percent_complete: percent,
            files_processed: processed,
            total_files: total,
            timestamp: progress_utils::current_timestamp(),
        },
    )
    .context("failed to write structural scan progress")
}

fn count_walk(root: &Path) -> impl Iterator<Item = jwalk::Result<jwalk::DirEntry<((), ())>>> {
    let parallelism = cache_utils::detect_filesystem_type(root).recommended_parallelism();
    WalkDir::new(root)
        .follow_links(false)
        .parallelism(jwalk::Parallelism::RayonNewPool(parallelism.max(1)))
        .into_iter()
}

fn inspection_walk(root: &Path) -> impl Iterator<Item = jwalk::Result<jwalk::DirEntry<((), ())>>> {
    WalkDir::new(root)
        .follow_links(false)
        .parallelism(jwalk::Parallelism::Serial)
        .into_iter()
}

fn eta_seconds(total: usize, processed: usize, rate: ProgressRate) -> Option<u64> {
    let files_per_second = rate.files_per_second?;
    if rate.elapsed_seconds < 2.0 || !files_per_second.is_finite() || files_per_second <= 0.0 {
        return None;
    }
    let remaining = total.saturating_sub(processed) as f64;
    let eta = (remaining / files_per_second).ceil();
    if eta.is_finite() && eta >= 0.0 {
        Some(eta as u64)
    } else {
        None
    }
}

fn progress_details(
    telemetry: &mut InspectionTelemetry,
    now: Duration,
    processed: usize,
    total: usize,
    config: StructuralScanConfig,
    worker_count: usize,
    include_eta: bool,
) -> ProgressDetails {
    let rate = telemetry.sample(now, processed);
    ProgressDetails {
        rate,
        eta_seconds: include_eta
            .then(|| eta_seconds(total, processed, rate))
            .flatten(),
        // The concurrency the controller has settled on right now, not the size of the pool.
        // This is what surfaces in the progress file, so a scan can be watched converging.
        worker_count,
        task_queue_capacity: config.task_queue_capacity,
        result_queue_capacity: config.result_queue_capacity,
    }
}

fn merge_inspection_result(
    result: InspectionResult,
    coverage: &mut StructuralCoverage,
    candidates: &mut Vec<CorruptionCandidate>,
    warning_count: &mut usize,
) {
    coverage.files_seen = coverage.files_seen.saturating_add(1);
    match result.inspection {
        Ok(inspection) => {
            coverage.bytes_read = coverage.bytes_read.saturating_add(inspection.bytes_read);
            coverage.sparse_files = coverage
                .sparse_files
                .saturating_add(usize::from(inspection.sparse));
            match inspection.outcome {
                InspectionOutcome::Consistent => {
                    coverage.files_checked = coverage.files_checked.saturating_add(1);
                    coverage.consistent = coverage.consistent.saturating_add(1);
                }
                InspectionOutcome::Proven(evidence) => {
                    coverage.files_checked = coverage.files_checked.saturating_add(1);
                    candidates.push(structural_candidate(&result.path, evidence));
                }
                InspectionOutcome::Skip(reason) => {
                    let count = coverage
                        .skipped_by_reason
                        .entry(reason.to_string())
                        .or_default();
                    *count = count.saturating_add(1);
                }
            }
        }
        Err(_error) => {
            *warning_count = warning_count.saturating_add(1);
            if *warning_count <= 5 {
                eprintln!(
                    "WARNING: structural inspection I/O error (sample {}/5); path details suppressed",
                    *warning_count
                );
            }
            coverage.io_errors = coverage.io_errors.saturating_add(1);
            let count = coverage
                .skipped_by_reason
                .entry(SkipReason::IoError.as_str().to_string())
                .or_default();
            *count = count.saturating_add(1);
        }
    }
}

fn request_pipeline_stop(
    stop: &AtomicBool,
    start_gate: &RwLock<()>,
    observer: Option<&dyn InspectionObserver>,
) -> Result<()> {
    let _guard = start_gate
        .write()
        .map_err(|_| anyhow::anyhow!("structural inspection start gate was poisoned"))?;
    stop.store(true, Ordering::Release);
    if let Some(observer) = observer {
        observer.cancellation_observed();
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn inspection_worker(
    root: &Path,
    now: SystemTime,
    scan_started_utc: DateTime<Utc>,
    layout: Option<Layout>,
    task_receiver: Receiver<InspectionTask>,
    result_sender: Sender<InspectionResult>,
    stop: &AtomicBool,
    start_gate: &RwLock<()>,
    counters: &PipelineCounters,
    limiter: &ConcurrencyLimiter,
    observer: Option<&dyn InspectionObserver>,
) -> Result<()> {
    // Linux workers keep the current two-level cache directory open while consuming its files.
    // jwalk emits siblings together, so this replaces millions of repeated root canonicalizations
    // and parent-component stats with descriptor-relative leaf opens.
    let mut path_access = WorkerPathAccess::new(root)?;
    loop {
        // Take the permit BEFORE pulling a task, not after. Dequeuing first would let every
        // thread in the pool hoard a task while parked, spreading each hash directory's files
        // across the whole pool: a worker's cached parent handle would then point at whatever
        // directory it saw a pool-size ago, and the descriptor reuse this scanner depends on
        // would miss on nearly every file. Gating first keeps only `limit` workers pulling, so
        // each one walks a run of siblings and its cached handle keeps hitting.
        //
        // It also has to come before the task is announced as started and before the start
        // gate: announcing first lets a worker cancelled while parked exit with the task still
        // counted active, and taking the gate first lets a parked worker hold a read lock that
        // `request_pipeline_stop` needs for writing.
        let Some(permit) = limiter.acquire(stop) else {
            break;
        };
        let Ok(task) = task_receiver.recv() else {
            break;
        };
        let start_guard = start_gate
            .read()
            .map_err(|_| anyhow::anyhow!("structural inspection start gate was poisoned"))?;
        if stop.load(Ordering::Acquire) || cancel::is_cancelled() {
            break;
        }
        if let Some(observer) = observer {
            observer.inspection_started(&task.path);
        }
        drop(start_guard);
        if let Some(observer) = observer {
            observer.inspection_wait(&task.path);
        }

        let inspection = match layout {
            Some(layout) => inspect_path_with_layout_and_access(
                root,
                &task.path,
                task.path_digest,
                now,
                scan_started_utc,
                layout,
                &mut path_access,
                || {
                    if let Some(observer) = observer {
                        observer.inspection_after_read(&task.path);
                    }
                },
            ),
            None => Ok(Inspection {
                outcome: InspectionOutcome::Skip(SkipReason::UnsupportedPlatform.as_str()),
                bytes_read: 0,
                sparse: false,
                verified_fingerprint: None,
            }),
        };
        drop(permit);
        if let Some(observer) = observer {
            observer.inspection_completed(&task.path, &inspection);
        }
        counters.completed.fetch_add(1, Ordering::Relaxed);
        result_sender
            .send(InspectionResult {
                path: task.path,
                path_digest: task.path_digest,
                revalidation: task.revalidation,
                inspection,
            })
            .context("structural inspection result channel disconnected")?;
        record_high_water(&counters.result_queue_high_water, result_sender.len());
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn merge_pipeline_result(
    result: InspectionResult,
    _root: &Path,
    _scan_now: SystemTime,
    coverage: &mut StructuralCoverage,
    candidates: &mut Vec<CorruptionCandidate>,
    mut state: Option<&mut StructuralState>,
    scan_summary: &mut StructuralScanSummary,
    warning_count: &mut usize,
    merged: &mut usize,
    scheduled: usize,
    counters: &PipelineCounters,
    observer: Option<&dyn InspectionObserver>,
) -> Result<()> {
    scan_summary.files_inspected = scan_summary.files_inspected.saturating_add(1);
    if result.revalidation {
        scan_summary.files_revalidated = scan_summary.files_revalidated.saturating_add(1);
    }
    match &result.inspection {
        Ok(Inspection {
            outcome: InspectionOutcome::Consistent,
            verified_fingerprint,
            ..
        }) => {
            if let Some(state) = state.as_mut() {
                if let Some(current) = verified_fingerprint.clone() {
                    state.record_success(
                        result.path_digest,
                        current,
                        SuccessfulOutcome::Consistent,
                    )?;
                } else {
                    scan_summary.files_pending_retry =
                        scan_summary.files_pending_retry.saturating_add(1);
                }
            }
        }
        Ok(Inspection {
            outcome: InspectionOutcome::Proven(evidence),
            verified_fingerprint,
            ..
        }) => {
            if let Some(state) = state.as_mut() {
                if let Some(current) = verified_fingerprint
                    .clone()
                    .filter(|current| current == &evidence.fingerprint)
                {
                    let candidate = structural_candidate(&result.path, evidence.clone());
                    state.record_success(
                        result.path_digest,
                        current,
                        SuccessfulOutcome::Proven(&candidate),
                    )?;
                } else {
                    scan_summary.files_pending_retry =
                        scan_summary.files_pending_retry.saturating_add(1);
                }
            }
        }
        Ok(Inspection {
            outcome: InspectionOutcome::Skip(_),
            ..
        })
        | Err(_) => {
            scan_summary.files_pending_retry = scan_summary.files_pending_retry.saturating_add(1);
        }
    }
    merge_inspection_result(result, coverage, candidates, warning_count);
    scan_summary.files_processed = coverage.files_seen;
    scan_summary.invalid_files = candidates.len();
    *merged = merged.saturating_add(1);
    record_high_water(
        &counters.outstanding_high_water,
        scheduled.saturating_sub(*merged),
    );
    if let Some(observer) = observer {
        observer.result_merged();
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn emit_inspection_checkpoint(
    progress_path: Option<&Path>,
    status: &str,
    total: usize,
    coverage: &StructuralCoverage,
    candidates: usize,
    telemetry: &mut InspectionTelemetry,
    clock: &dyn ScanClock,
    config: StructuralScanConfig,
    worker_count: usize,
    scan_summary: &StructuralScanSummary,
) -> Result<ProgressRate> {
    let now = clock.elapsed();
    let inspected_total = total.saturating_sub(scan_summary.files_reused);
    let mut details = progress_details(
        telemetry,
        now,
        scan_summary.files_inspected,
        inspected_total,
        config,
        worker_count,
        status != "cancelled",
    );
    if status == "completed" {
        details.eta_seconds = Some(0);
    } else if status == "cancelled" {
        details.eta_seconds = None;
    }
    update_progress(
        progress_path,
        status,
        "scanning",
        coverage.files_seen,
        total,
        candidates,
        coverage,
        details,
        scan_summary,
    )?;
    Ok(details.rate)
}

#[allow(clippy::too_many_arguments)]
fn run_parallel_inspection<F: FnMut() -> bool>(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
    total: usize,
    coverage: &mut StructuralCoverage,
    candidates: &mut Vec<CorruptionCandidate>,
    config: StructuralScanConfig,
    clock: &dyn ScanClock,
    observer: Option<&dyn InspectionObserver>,
    mut state: Option<&mut StructuralState>,
    scan_summary: &mut StructuralScanSummary,
    traversal_errors: &mut usize,
    cancellation_requested: &mut F,
) -> Result<(bool, PipelineTelemetry)> {
    let config = config.normalized();
    let now = SystemTime::from(scan_started_utc);
    let phase_started_at = clock.elapsed();
    let mut progress_telemetry = InspectionTelemetry::new(phase_started_at);
    update_progress(
        progress_path,
        "scanning",
        "scanning",
        0,
        total,
        candidates.len(),
        coverage,
        ProgressDetails {
            rate: ProgressRate {
                elapsed_seconds: 0.0,
                files_per_second: Some(0.0),
            },
            eta_seconds: None,
            worker_count: config.initial_parallelism,
            task_queue_capacity: config.task_queue_capacity,
            result_queue_capacity: config.result_queue_capacity,
        },
        scan_summary,
    )?;
    let stop = Arc::new(AtomicBool::new(false));
    let start_gate = Arc::new(RwLock::new(()));
    let counters = Arc::new(PipelineCounters::default());
    // With adaptation off (tests, and the serial reference) the limiter is pinned wide open at
    // the pool size, so every worker runs unthrottled exactly as it did before.
    let limiter = Arc::new(ConcurrencyLimiter::new(if config.adaptive_concurrency {
        config.initial_parallelism
    } else {
        config.inspection_parallelism
    }));
    let mut controller = config.adaptive_concurrency.then(|| {
        ConcurrencyController::new(
            config.initial_parallelism,
            config.inspection_parallelism,
            phase_started_at,
        )
    });
    record_high_water(&counters.concurrency_high_water, limiter.limit());
    let (task_sender, task_receiver) = bounded(config.task_queue_capacity);
    let (result_sender, result_receiver) = bounded(config.result_queue_capacity);
    let mut scheduled = 0usize;
    let mut merged = 0usize;
    let mut warning_count = 0usize;
    let mut cancelled = false;

    std::thread::scope(|scope| -> Result<()> {
        let mut handles = Vec::with_capacity(config.inspection_parallelism);
        let mut spawn_error = None;
        for worker_index in 0..config.inspection_parallelism {
            let worker_receiver = task_receiver.clone();
            let worker_sender = result_sender.clone();
            let worker_stop = Arc::clone(&stop);
            let worker_start_gate = Arc::clone(&start_gate);
            let worker_counters = Arc::clone(&counters);
            let worker_limiter = Arc::clone(&limiter);
            // The pool is sized at the ceiling and most of it is parked, so keep the stacks
            // small: these threads block on I/O, they do not recurse.
            let builder = std::thread::Builder::new()
                .name(format!("structural-inspector-{worker_index}"))
                .stack_size(256 * 1024);
            match builder.spawn_scoped(scope, move || {
                inspection_worker(
                    root,
                    now,
                    scan_started_utc,
                    config.layout,
                    worker_receiver,
                    worker_sender,
                    &worker_stop,
                    &worker_start_gate,
                    &worker_counters,
                    &worker_limiter,
                    observer,
                )
            }) {
                Ok(handle) => handles.push(handle),
                Err(error) => {
                    spawn_error = Some(
                        anyhow::Error::new(error)
                            .context("failed to spawn structural inspection worker"),
                    );
                    break;
                }
            }
        }
        drop(task_receiver);
        drop(result_sender);

        let mut fatal_error = spawn_error;
        if fatal_error.is_some() {
            request_pipeline_stop(&stop, &start_gate, observer)?;
        }

        let mut walk = inspection_walk(root);
        let mut pending_tasks = VecDeque::<InspectionTask>::new();
        let mut traversal_complete = fatal_error.is_some();
        while !traversal_complete || !pending_tasks.is_empty() {
            if let Some(state) = state.as_deref_mut() {
                state.maintain_lease()?;
            }
            if fatal_error.is_none() && cancellation_requested() {
                cancelled = true;
                request_pipeline_stop(&stop, &start_gate, observer)?;
                pending_tasks.clear();
                traversal_complete = true;
            }

            while let Ok(result) = result_receiver.try_recv() {
                merge_pipeline_result(
                    result,
                    root,
                    now,
                    coverage,
                    candidates,
                    state.as_deref_mut(),
                    scan_summary,
                    &mut warning_count,
                    &mut merged,
                    scheduled,
                    &counters,
                    observer,
                )?;
            }

            let clock_now = clock.elapsed();
            retune_concurrency(&mut controller, &limiter, &counters, clock_now);
            if progress_telemetry.progress_due(clock_now, config.progress_interval) {
                if let Err(error) = emit_inspection_checkpoint(
                    progress_path,
                    "scanning",
                    total,
                    coverage,
                    candidates.len(),
                    &mut progress_telemetry,
                    clock,
                    config,
                    limiter.limit(),
                    scan_summary,
                ) {
                    fatal_error = Some(error);
                    request_pipeline_stop(&stop, &start_gate, observer)?;
                    pending_tasks.clear();
                    traversal_complete = true;
                }
            }
            if fatal_error.is_some() || cancelled {
                break;
            }

            if pending_tasks.is_empty() && !traversal_complete {
                let mut discovered = Vec::with_capacity(STATE_CLASSIFICATION_BATCH);
                while discovered.len() < STATE_CLASSIFICATION_BATCH && !traversal_complete {
                    let next_entry = walk.next();
                    if let Some(state) = state.as_deref_mut() {
                        state.maintain_lease()?;
                    }
                    match next_entry {
                        Some(Ok(entry)) => {
                            let path = entry.path();
                            if let Some(path_digest) =
                                cache_utils::strict_cache_file_digest(root, &path)
                            {
                                discovered.push((path, path_digest));
                            }
                        }
                        Some(Err(error)) => {
                            if error.depth() == 0 {
                                fatal_error = Some(
                                    anyhow::Error::new(error)
                                        .context("failed to enumerate structural cache root"),
                                );
                                request_pipeline_stop(&stop, &start_gate, observer)?;
                                traversal_complete = true;
                            } else {
                                *traversal_errors = (*traversal_errors).saturating_add(1);
                                coverage.io_errors = coverage.io_errors.saturating_add(1);
                                let count = coverage
                                    .skipped_by_reason
                                    .entry(SkipReason::IoError.as_str().to_string())
                                    .or_default();
                                *count = count.saturating_add(1);
                            }
                        }
                        None => traversal_complete = true,
                    }
                }
                let decisions = if let Some(state) = state.as_deref_mut() {
                    state.maintain_lease()?;
                    if state.can_reuse_existing() {
                        let inputs = discovered
                            .iter()
                            .map(|(path, digest)| LookupInput {
                                digest: *digest,
                                fingerprint: reusable_path_fingerprint(root, path, now),
                            })
                            .collect::<Vec<_>>();
                        state.lookup_batch(&inputs)?
                    } else {
                        discovered.iter().map(|_| ReuseDecision::Inspect).collect()
                    }
                } else {
                    discovered.iter().map(|_| ReuseDecision::Inspect).collect()
                };
                for ((path, path_digest), decision) in
                    discovered.into_iter().zip(decisions.into_iter())
                {
                    match decision {
                        ReuseDecision::Inspect => pending_tasks.push_back(InspectionTask {
                            path,
                            path_digest,
                            revalidation: false,
                        }),
                        ReuseDecision::ReuseConsistent => {
                            coverage.files_seen = coverage.files_seen.saturating_add(1);
                            coverage.consistent = coverage.consistent.saturating_add(1);
                            scan_summary.files_reused = scan_summary.files_reused.saturating_add(1);
                            scan_summary.files_processed = coverage.files_seen;
                        }
                        ReuseDecision::Revalidate(candidate) => {
                            validate_cached_candidate(root, &path, path_digest, &candidate)?;
                            pending_tasks.push_back(InspectionTask {
                                path,
                                path_digest,
                                revalidation: true,
                            });
                        }
                    }
                }
            }

            if let Some(task) = pending_tasks.pop_front() {
                let scheduled_path = task.path.clone();
                match task_sender.try_send(task) {
                    Ok(()) => {
                        scheduled = scheduled.saturating_add(1);
                        record_high_water(&counters.task_queue_high_water, task_sender.len());
                        record_high_water(
                            &counters.outstanding_high_water,
                            scheduled.saturating_sub(merged),
                        );
                        if let Some(observer) = observer {
                            observer.task_scheduled(&scheduled_path);
                        }
                    }
                    Err(TrySendError::Full(task)) => {
                        pending_tasks.push_front(task);
                        match result_receiver.recv_timeout(Duration::from_millis(25)) {
                            Ok(result) => merge_pipeline_result(
                                result,
                                root,
                                now,
                                coverage,
                                candidates,
                                state.as_deref_mut(),
                                scan_summary,
                                &mut warning_count,
                                &mut merged,
                                scheduled,
                                &counters,
                                observer,
                            )?,
                            Err(RecvTimeoutError::Timeout) => {}
                            Err(RecvTimeoutError::Disconnected) => {
                                fatal_error = Some(anyhow::anyhow!(
                                    "structural inspection result channel disconnected while scheduling"
                                ));
                                request_pipeline_stop(&stop, &start_gate, observer)?;
                                pending_tasks.clear();
                                traversal_complete = true;
                            }
                        }
                    }
                    Err(TrySendError::Disconnected(_task)) => {
                        fatal_error = Some(anyhow::anyhow!(
                            "structural inspection task channel disconnected while scheduling"
                        ));
                        request_pipeline_stop(&stop, &start_gate, observer)?;
                        traversal_complete = true;
                    }
                }
            }
        }

        drop(task_sender);
        loop {
            if let Some(state) = state.as_deref_mut() {
                state.maintain_lease()?;
            }
            match result_receiver.recv_timeout(Duration::from_millis(100)) {
                Ok(result) => merge_pipeline_result(
                    result,
                    root,
                    now,
                    coverage,
                    candidates,
                    state.as_deref_mut(),
                    scan_summary,
                    &mut warning_count,
                    &mut merged,
                    scheduled,
                    &counters,
                    observer,
                )?,
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => break,
            }
            let clock_now = clock.elapsed();
            retune_concurrency(&mut controller, &limiter, &counters, clock_now);
            if progress_telemetry.progress_due(clock_now, config.progress_interval) {
                if let Err(error) = emit_inspection_checkpoint(
                    progress_path,
                    "scanning",
                    total,
                    coverage,
                    candidates.len(),
                    &mut progress_telemetry,
                    clock,
                    config,
                    limiter.limit(),
                    scan_summary,
                ) {
                    fatal_error.get_or_insert(error);
                    request_pipeline_stop(&stop, &start_gate, observer)?;
                }
            }
        }

        for handle in handles {
            match handle.join() {
                Ok(Ok(())) => {}
                Ok(Err(error)) => {
                    fatal_error.get_or_insert(
                        error.context("structural inspection worker terminated with an error"),
                    );
                }
                Err(_) => {
                    fatal_error.get_or_insert_with(|| {
                        anyhow::anyhow!("structural inspection worker panicked")
                    });
                }
            }
        }

        let completed = counters.completed.load(Ordering::Relaxed);
        if merged != completed {
            fatal_error.get_or_insert_with(|| {
                anyhow::anyhow!(
                    "structural inspection result accounting mismatch: completed={completed} merged={merged}"
                )
            });
        }
        if !cancelled && fatal_error.is_none() && completed != scheduled {
            fatal_error.get_or_insert_with(|| {
                anyhow::anyhow!(
                    "structural inspection task accounting mismatch: scheduled={scheduled} completed={completed}"
                )
            });
        }
        if let Some(error) = fatal_error {
            return Err(error);
        }
        Ok(())
    })?;

    if warning_count > 5 {
        eprintln!(
            "WARNING: structural inspection I/O errors suppressed after 5 samples; total={warning_count}"
        );
    }
    let inspection_elapsed_seconds = clock
        .elapsed()
        .saturating_sub(phase_started_at)
        .as_secs_f64();
    let final_files_per_second = if inspection_elapsed_seconds > 0.0 {
        let rate = merged as f64 / inspection_elapsed_seconds;
        rate.is_finite().then_some(rate)
    } else {
        progress_telemetry.ewma_files_per_second
    };
    let telemetry = PipelineTelemetry {
        // The concurrency the scan converged on, not the size of the (mostly parked) pool.
        // Reporting the ceiling here would claim 64 workers on a NAS that only ever sustained
        // eight. Stays >= 1 for any pipeline that ran, so the "never started" check that keys
        // off a zero worker count still holds.
        worker_count: limiter.limit(),
        peak_worker_count: counters.concurrency_high_water.load(Ordering::Relaxed),
        task_queue_high_water: counters.task_queue_high_water.load(Ordering::Relaxed),
        result_queue_high_water: counters.result_queue_high_water.load(Ordering::Relaxed),
        outstanding_high_water: counters.outstanding_high_water.load(Ordering::Relaxed),
        scheduled,
        completed: counters.completed.load(Ordering::Relaxed),
        merged,
        inspection_elapsed_seconds,
        final_files_per_second,
    };
    Ok((cancelled, telemetry))
}

fn build_report(
    scan_started_utc: DateTime<Utc>,
    mut candidates: Vec<CorruptionCandidate>,
    coverage: StructuralCoverage,
    cancelled: bool,
) -> CorruptionReport {
    candidates.sort_by(|left, right| left.exact_paths.cmp(&right.exact_paths));
    let mut service_counts = BTreeMap::new();
    for candidate in &candidates {
        *service_counts.entry(candidate.service.clone()).or_default() += 1;
    }
    CorruptionReport {
        contract_version: CORRUPTION_CONTRACT_VERSION,
        cancelled,
        detection_method: DetectionMethod::Structural,
        scan_started_utc: scan_started_utc.to_rfc3339_opts(SecondsFormat::AutoSi, true),
        settings: CorruptionSettings {
            threshold: None,
            lookback_days: None,
            min_stable_age_seconds: Some(MIN_STABLE_AGE_SECONDS),
            max_prefix_bytes: Some(MAX_PREFIX_BYTES),
        },
        service_counts,
        detection_counts: BTreeMap::from([("structural".to_string(), candidates.len())]),
        total: candidates.len(),
        coverage: Some(coverage),
        candidates,
    }
}

fn require_complete_traversal(traversal_errors: usize, cancelled: bool) -> Result<()> {
    if traversal_errors > 0 && !cancelled {
        bail!("structural cache traversal was incomplete ({traversal_errors} enumeration errors)");
    }
    Ok(())
}

pub fn scan(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
) -> Result<StructuralScanResult> {
    scan_with_cancellation(root, scan_started_utc, progress_path, cancel::is_cancelled)
}

pub fn scan_with_state(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
    mode: StructuralScanMode,
    state_db: &Path,
    state_scope: &str,
) -> Result<StructuralScanResult> {
    let config = StructuralScanConfig::production(root);
    let clock = RealScanClock::new();
    let mut cancellation_requested = cancel::is_cancelled;
    scan_with_runtime_options(
        root,
        scan_started_utc,
        progress_path,
        config,
        &clock,
        None,
        Some((mode, state_db, state_scope)),
        &mut cancellation_requested,
    )
}

fn scan_with_cancellation<F: FnMut() -> bool>(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
    mut cancellation_requested: F,
) -> Result<StructuralScanResult> {
    let config = StructuralScanConfig::production(root);
    let clock = RealScanClock::new();
    scan_with_runtime(
        root,
        scan_started_utc,
        progress_path,
        config,
        &clock,
        None,
        &mut cancellation_requested,
    )
}

#[allow(clippy::too_many_arguments)]
fn scan_with_runtime<F: FnMut() -> bool>(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
    config: StructuralScanConfig,
    clock: &dyn ScanClock,
    observer: Option<&dyn InspectionObserver>,
    cancellation_requested: &mut F,
) -> Result<StructuralScanResult> {
    scan_with_runtime_options(
        root,
        scan_started_utc,
        progress_path,
        config,
        clock,
        observer,
        None,
        cancellation_requested,
    )
}

#[allow(clippy::too_many_arguments)]
fn scan_with_runtime_options<F: FnMut() -> bool>(
    root: &Path,
    scan_started_utc: DateTime<Utc>,
    progress_path: Option<&Path>,
    config: StructuralScanConfig,
    clock: &dyn ScanClock,
    observer: Option<&dyn InspectionObserver>,
    state_options: Option<(StructuralScanMode, &Path, &str)>,
    cancellation_requested: &mut F,
) -> Result<StructuralScanResult> {
    let config = config.normalized();
    let root_link_metadata = std::fs::symlink_metadata(root)
        .with_context(|| format!("failed to inspect structural cache root {}", root.display()))?;
    if root_link_metadata.file_type().is_symlink() || !root_link_metadata.is_dir() {
        bail!(
            "structural cache root must be a non-symlink directory: {}",
            root.display()
        );
    }
    let canonical_root = std::fs::canonicalize(root).with_context(|| {
        format!(
            "failed to canonicalize structural cache root {}",
            root.display()
        )
    })?;
    if canonical_root != root.canonicalize()? {
        bail!("structural cache root changed during setup");
    }

    let (mut state, mut scan_summary) = if let Some((mode, state_db, state_scope)) = state_options {
        if state_scope.trim().is_empty() {
            bail!("structural state scope must not be empty");
        }
        let state = StructuralState::open(
            state_db,
            StateNamespace {
                canonical_root_identity: canonical_root_identity(&canonical_root),
                root_fingerprint: root_namespace_fingerprint(&root_link_metadata),
                scope: state_scope.to_string(),
                layout_signature: config
                    .layout
                    .map(Layout::signature)
                    .unwrap_or_else(|| "unsupported-native-layout".to_string()),
                scanner_policy_version: STRUCTURAL_SCANNER_POLICY_VERSION,
            },
            mode,
        )?;
        let summary = StructuralScanSummary {
            scan_mode: mode.as_str().to_string(),
            effective_scan_mode: state.effective_mode().as_str().to_string(),
            baseline_status: "building".to_string(),
            resumed: state.resumed(),
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
        };
        (Some(state), summary)
    } else {
        (None, StructuralScanSummary::stateless_full())
    };

    let mut total = 0usize;
    let mut cancelled = false;
    let mut traversal_errors = 0usize;
    let mut coverage = StructuralCoverage::default();
    // Emit the enumerating stage immediately so the UI leaves any prior scanningHeaders/0%
    // state instead of sitting on a frozen 0% for the whole (potentially multi-minute) count.
    let mut count_telemetry = InspectionTelemetry::new(clock.elapsed());
    update_progress(
        progress_path,
        "scanning",
        "counting",
        0,
        0,
        0,
        &coverage,
        ProgressDetails {
            rate: ProgressRate {
                elapsed_seconds: 0.0,
                files_per_second: None,
            },
            eta_seconds: None,
            worker_count: 0,
            task_queue_capacity: 0,
            result_queue_capacity: 0,
        },
        &scan_summary,
    )?;
    for entry in count_walk(root) {
        if cancellation_requested() {
            cancelled = true;
            break;
        }
        if let Some(state) = state.as_mut() {
            state.maintain_lease()?;
        }
        let entry = match entry {
            Ok(entry) => entry,
            Err(error) => {
                if error.depth() == 0 {
                    return Err(error).context("failed to enumerate structural cache root");
                }
                coverage.io_errors = coverage.io_errors.saturating_add(1);
                traversal_errors = traversal_errors.saturating_add(1);
                let count = coverage
                    .skipped_by_reason
                    .entry("count_enumeration_io_error".to_string())
                    .or_default();
                *count = count.saturating_add(1);
                continue;
            }
        };
        if cache_utils::strict_cache_file_digest(root, &entry.path()).is_some() {
            total = total.saturating_add(1);
            scan_summary.files_discovered = total;
        }
        let now = clock.elapsed();
        if count_telemetry.progress_due(now, config.progress_interval) {
            // Enumeration runs before any inspector exists, so no concurrency to report yet.
            let details = progress_details(&mut count_telemetry, now, total, 0, config, 0, false);
            update_progress(
                progress_path,
                "scanning",
                "counting",
                total,
                0,
                0,
                &coverage,
                details,
                &scan_summary,
            )?;
        }
    }

    let mut candidates = Vec::new();
    // Final counting emit: report the full eligible-file count now that enumeration finished.
    let counting_details = progress_details(
        &mut count_telemetry,
        clock.elapsed(),
        total,
        0,
        config,
        0,
        false,
    );
    update_progress(
        progress_path,
        "scanning",
        "counting",
        total,
        total,
        0,
        &coverage,
        counting_details,
        &scan_summary,
    )?;
    let mut pipeline = PipelineTelemetry::default();
    if !cancelled {
        let result = run_parallel_inspection(
            root,
            scan_started_utc,
            progress_path,
            total,
            &mut coverage,
            &mut candidates,
            config,
            clock,
            observer,
            state.as_mut(),
            &mut scan_summary,
            &mut traversal_errors,
            cancellation_requested,
        )?;
        cancelled = result.0;
        pipeline = result.1;
    }
    // The count pass is an estimate on a live cache. Terminal discovery is the exact set the
    // inspection walk classified, which is also the report coverage denominator.
    scan_summary.files_discovered = coverage.files_seen;
    scan_summary.files_processed = coverage.files_seen;
    scan_summary.invalid_files = candidates.len();
    if let Some(state) = state.as_mut() {
        if cancelled || traversal_errors > 0 {
            state.interrupt()?;
            scan_summary.baseline_status = "incomplete".to_string();
        } else {
            let (pruned, entries) = state.publish()?;
            scan_summary.files_pruned = pruned;
            scan_summary.state_entries = entries;
            scan_summary.state_committed = true;
            scan_summary.baseline_status = "ready".to_string();
        }
    }
    require_complete_traversal(traversal_errors, cancelled)?;
    let report = build_report(scan_started_utc, candidates, coverage.clone(), cancelled);
    if cancelled && pipeline.worker_count == 0 {
        let details = progress_details(
            &mut count_telemetry,
            clock.elapsed(),
            total,
            0,
            config,
            0,
            false,
        );
        update_progress(
            progress_path,
            "cancelled",
            "counting",
            total,
            0,
            report.total,
            &coverage,
            details,
            &scan_summary,
        )?;
    } else {
        let details = ProgressDetails {
            rate: ProgressRate {
                elapsed_seconds: pipeline.inspection_elapsed_seconds,
                files_per_second: pipeline.final_files_per_second,
            },
            eta_seconds: if cancelled { None } else { Some(0) },
            worker_count: pipeline.worker_count,
            task_queue_capacity: config.task_queue_capacity,
            result_queue_capacity: config.result_queue_capacity,
        };
        update_progress(
            progress_path,
            if cancelled { "cancelled" } else { "completed" },
            "scanning",
            coverage.files_seen,
            total,
            report.total,
            &coverage,
            details,
            &scan_summary,
        )?;
    }
    Ok(StructuralScanResult {
        report,
        cancelled,
        scan_summary,
        pipeline,
    })
}

pub fn revalidate_for_removal(
    root: &Path,
    candidate: &CorruptionCandidate,
    now: SystemTime,
) -> Result<RemovalDisposition> {
    let layout = Layout::native().context("structural removal is unsupported on this platform")?;
    revalidate_for_removal_with_layout(root, candidate, now, layout)
}

fn revalidate_for_removal_with_layout(
    root: &Path,
    candidate: &CorruptionCandidate,
    now: SystemTime,
    layout: Layout,
) -> Result<RemovalDisposition> {
    if candidate.exact_paths.len() != 1 {
        bail!("structural candidate must contain exactly one exact path");
    }
    let CorruptionEvidence::Structural {
        structural: persisted,
    } = &candidate.evidence
    else {
        bail!("remove-structural received non-structural evidence");
    };
    if persisted.issues.is_empty() {
        bail!("structural candidate has no issues");
    }
    let path = PathBuf::from(&candidate.exact_paths[0]);
    let Some(path_digest) = cache_utils::strict_cache_file_digest(root, &path) else {
        bail!("structural candidate path has an invalid cache layout");
    };
    match std::fs::symlink_metadata(&path) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(RemovalDisposition::Missing)
        }
        Err(error) => {
            return Err(error).with_context(|| format!("failed to lstat {}", path.display()))
        }
        Ok(_) => {}
    }
    let inspection = inspect_path_with_layout(root, &path, path_digest, now, Utc::now(), layout)?;
    match inspection.outcome {
        InspectionOutcome::Consistent => Ok(RemovalDisposition::Healed),
        InspectionOutcome::Skip("cancelled") => Ok(RemovalDisposition::Cancelled),
        InspectionOutcome::Skip(reason) => bail!(
            "structural candidate {} is no longer safely actionable: {reason}",
            path.display()
        ),
        InspectionOutcome::Proven(current) => {
            if current.fingerprint != persisted.fingerprint {
                bail!(
                    "structural candidate changed since scan: {}",
                    path.display()
                );
            }
            if current.issues != persisted.issues
                || current.cache_key != persisted.cache_key
                || current.cache_key_md5 != persisted.cache_key_md5
            {
                bail!(
                    "structural candidate finding changed since scan: {}",
                    path.display()
                );
            }
            Ok(RemovalDisposition::Ready {
                path,
                evidence: persisted.clone(),
            })
        }
    }
}

#[derive(Debug, Clone)]
pub enum RemovalDisposition {
    Missing,
    Healed,
    Cancelled,
    Ready {
        path: PathBuf,
        evidence: StructuralEvidence,
    },
}

#[cfg(all(test, unix, target_pointer_width = "64"))]
pub(crate) fn write_linux_v5_test_fixture(
    root: &Path,
    key: &[u8],
    headers: &[u8],
    payload: &[u8],
) -> PathBuf {
    let layout = Layout::linux_x86_64();
    let header_start = layout.size + KEY_MARKER.len() + key.len() + 1;
    let body_start = header_start + headers.len();
    let mut bytes = vec![0; body_start];
    bytes[layout.version..layout.version + std::mem::size_of::<usize>()]
        .copy_from_slice(&5usize.to_ne_bytes());
    bytes[layout.crc32..layout.crc32 + 4].copy_from_slice(&crc32fast::hash(key).to_ne_bytes());
    bytes[layout.header_start..layout.header_start + 2]
        .copy_from_slice(&(header_start as u16).to_ne_bytes());
    bytes[layout.body_start..layout.body_start + 2]
        .copy_from_slice(&(body_start as u16).to_ne_bytes());
    bytes[layout.size..layout.size + KEY_MARKER.len()].copy_from_slice(KEY_MARKER);
    bytes[layout.size + KEY_MARKER.len()..header_start - 1].copy_from_slice(key);
    bytes[header_start - 1] = b'\n';
    bytes[header_start..body_start].copy_from_slice(headers);
    bytes.extend_from_slice(payload);
    let digest = u128::from_be_bytes(md5::compute(key).0);
    let path = cache_utils::cache_path_for_digest(root, digest);
    std::fs::create_dir_all(path.parent().expect("fixture cache path has parent"))
        .expect("create fixture parents");
    std::fs::write(&path, bytes).expect("write fixture");
    path
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicU64;
    use std::sync::{Barrier, Condvar, Mutex};

    fn put_usize(buffer: &mut [u8], offset: usize, value: usize) {
        buffer[offset..offset + std::mem::size_of::<usize>()].copy_from_slice(&value.to_ne_bytes());
    }

    fn put_u32(buffer: &mut [u8], offset: usize, value: u32) {
        buffer[offset..offset + 4].copy_from_slice(&value.to_ne_bytes());
    }

    fn put_u16(buffer: &mut [u8], offset: usize, value: u16) {
        buffer[offset..offset + 2].copy_from_slice(&value.to_ne_bytes());
    }

    fn fixture(key: &[u8], headers: &[u8], payload: &[u8]) -> (Vec<u8>, u128) {
        let layout = Layout::linux_x86_64();
        let header_start = layout.size + KEY_MARKER.len() + key.len() + 1;
        let body_start = header_start + headers.len();
        let mut bytes = vec![0; body_start];
        put_usize(&mut bytes, layout.version, 5);
        put_u32(&mut bytes, layout.crc32, crc32fast::hash(key));
        put_u16(&mut bytes, layout.header_start, header_start as u16);
        put_u16(&mut bytes, layout.body_start, body_start as u16);
        bytes[layout.size..layout.size + KEY_MARKER.len()].copy_from_slice(KEY_MARKER);
        bytes[layout.size + KEY_MARKER.len()..header_start - 1].copy_from_slice(key);
        bytes[header_start - 1] = b'\n';
        bytes[header_start..body_start].copy_from_slice(headers);
        bytes.extend_from_slice(payload);
        let digest = u128::from_be_bytes(md5::compute(key).0);
        (bytes, digest)
    }

    fn issues(outcome: ParseOutcome) -> Vec<StructuralIssue> {
        match outcome {
            ParseOutcome::Proven(parsed) => parsed.issues,
            other => panic!("expected proven corruption, got {other:?}"),
        }
    }

    fn materialize(root: &Path, key: &[u8], headers: &[u8], payload: &[u8]) -> (PathBuf, usize) {
        let (bytes, digest) = fixture(key, headers, payload);
        let body_start = read_u16(&bytes, Layout::linux_x86_64().body_start).unwrap() as usize;
        let path = cache_utils::cache_path_for_digest(root, digest);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, bytes).unwrap();
        (path, body_start)
    }

    fn old_now() -> SystemTime {
        SystemTime::now() + Duration::from_secs(MIN_STABLE_AGE_SECONDS + 5)
    }

    /// Writes a real cache tree for end-to-end runs of the built binary (Full then Incremental,
    /// kill-and-resume). Ignored by default; the scan itself is covered by the tests above.
    /// `STRUCTURAL_FIXTURE_DIR=/tmp/cache STRUCTURAL_FIXTURE_N=2000 cargo test emit_fixture_cache -- --ignored`
    #[test]
    #[ignore]
    fn emit_fixture_cache() {
        let Ok(dir) = std::env::var("STRUCTURAL_FIXTURE_DIR") else {
            panic!("set STRUCTURAL_FIXTURE_DIR to the cache root to populate");
        };
        let count: u32 = std::env::var("STRUCTURAL_FIXTURE_N")
            .unwrap_or_else(|_| "2000".to_string())
            .parse()
            .expect("STRUCTURAL_FIXTURE_N must be a number");
        let root = PathBuf::from(dir);
        for index in 0..count {
            materialize(
                &root,
                format!("steam/fixture-{index}").as_bytes(),
                b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
                b"1234",
            );
        }
        eprintln!("wrote {count} fixtures to {}", root.display());
    }

    /// Pins concurrency: the pipeline tests assert exact worker counts and queue high-water
    /// marks, which only hold if the limiter stays wide open. The controller is exercised
    /// separately, against synthetic throughput, where it can be checked deterministically.
    fn test_config(parallelism: usize, capacity: usize) -> StructuralScanConfig {
        StructuralScanConfig {
            inspection_parallelism: parallelism,
            initial_parallelism: parallelism,
            adaptive_concurrency: false,
            task_queue_capacity: capacity,
            result_queue_capacity: capacity,
            progress_interval: Duration::from_secs(1),
            layout: Some(Layout::linux_x86_64()),
        }
    }

    fn adaptive_test_config(initial: usize, max: usize, capacity: usize) -> StructuralScanConfig {
        StructuralScanConfig {
            inspection_parallelism: max,
            initial_parallelism: initial,
            adaptive_concurrency: true,
            task_queue_capacity: capacity,
            result_queue_capacity: capacity,
            progress_interval: Duration::from_secs(1),
            layout: Some(Layout::linux_x86_64()),
        }
    }

    #[cfg(unix)]
    fn durable_scan(
        root: &Path,
        state_db: &Path,
        mode: StructuralScanMode,
        scan_started: DateTime<Utc>,
    ) -> StructuralScanResult {
        let clock = RealScanClock::new();
        let mut never_cancel = || false;
        scan_with_runtime_options(
            root,
            scan_started,
            None,
            test_config(2, 2),
            &clock,
            None,
            Some((mode, state_db, "default")),
            &mut never_cancel,
        )
        .unwrap()
    }

    #[test]
    fn incomplete_traversal_fails_instead_of_publishing_a_partial_success() {
        assert!(require_complete_traversal(1, false).is_err());
        assert!(require_complete_traversal(0, false).is_ok());
        assert!(require_complete_traversal(1, true).is_ok());
    }

    #[cfg(unix)]
    #[test]
    fn durable_modes_build_reuse_refresh_change_and_prune() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let state_db = temp.path().join("structural.sqlite3");
        let (path, _) = materialize(
            &root,
            b"steam/durable",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"1234",
        );
        let started = fixed_scan_started();

        let baseline = durable_scan(&root, &state_db, StructuralScanMode::Incremental, started);
        assert_eq!(baseline.scan_summary.effective_scan_mode, "baseline");
        assert_eq!(baseline.scan_summary.files_inspected, 1);
        assert_eq!(baseline.scan_summary.files_reused, 0);
        assert!(baseline.scan_summary.state_committed);

        let unchanged = durable_scan(&root, &state_db, StructuralScanMode::Incremental, started);
        assert_eq!(unchanged.scan_summary.effective_scan_mode, "incremental");
        assert_eq!(unchanged.scan_summary.files_inspected, 0);
        assert_eq!(unchanged.scan_summary.files_reused, 1);
        assert_eq!(unchanged.report.coverage.unwrap().files_checked, 0);

        let full = durable_scan(&root, &state_db, StructuralScanMode::Full, started);
        assert_eq!(full.scan_summary.effective_scan_mode, "full");
        assert_eq!(full.scan_summary.files_inspected, 1);
        assert_eq!(full.scan_summary.files_reused, 0);

        let replacement = path.with_extension("replacement");
        std::fs::write(&replacement, std::fs::read(&path).unwrap()).unwrap();
        std::fs::rename(&replacement, &path).unwrap();
        let changed = durable_scan(&root, &state_db, StructuralScanMode::Incremental, started);
        assert_eq!(changed.scan_summary.files_inspected, 1);
        assert_eq!(changed.scan_summary.files_reused, 0);

        std::fs::remove_file(path).unwrap();
        let deleted = durable_scan(&root, &state_db, StructuralScanMode::Incremental, started);
        assert_eq!(deleted.scan_summary.files_pruned, 1);
        assert_eq!(deleted.scan_summary.state_entries, 0);
    }

    #[cfg(unix)]
    #[test]
    fn durable_incremental_revalidates_known_invalid_candidates() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let state_db = temp.path().join("structural.sqlite3");
        materialize(
            &root,
            b"steam/invalid-durable",
            b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n",
            b"1234",
        );
        let started = fixed_scan_started();
        let first = durable_scan(&root, &state_db, StructuralScanMode::Incremental, started);
        assert_eq!(first.report.total, 1);
        let second = durable_scan(&root, &state_db, StructuralScanMode::Incremental, started);
        assert_eq!(second.report.total, 1);
        assert_eq!(second.scan_summary.files_revalidated, 1);
        assert_eq!(second.scan_summary.files_inspected, 1);
        assert_eq!(second.scan_summary.files_reused, 0);
    }

    #[cfg(unix)]
    #[test]
    fn recent_skip_is_not_reused_as_success() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let state_db = temp.path().join("structural.sqlite3");
        materialize(
            &root,
            b"steam/recent-durable",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"1234",
        );
        let recent = durable_scan(
            &root,
            &state_db,
            StructuralScanMode::Incremental,
            Utc::now(),
        );
        assert_eq!(recent.scan_summary.files_pending_retry, 1);
        assert_eq!(recent.scan_summary.state_entries, 0);
        let retry = durable_scan(
            &root,
            &state_db,
            StructuralScanMode::Incremental,
            fixed_scan_started(),
        );
        assert_eq!(retry.scan_summary.files_inspected, 1);
        assert_eq!(retry.scan_summary.files_reused, 0);
        assert_eq!(retry.scan_summary.state_entries, 1);
    }

    #[cfg(not(unix))]
    #[test]
    fn weak_platform_identity_never_reuses_state() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let (path, _) = materialize(
            &root,
            b"steam/weak-identity",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"1234",
        );
        assert!(reusable_path_fingerprint(&root, &path, old_now()).is_none());
    }

    /// Drives the controller with a synthetic device: `rate_for` decides how many files a
    /// given concurrency sustains, so a spinning disk that saturates at 8 and an NVMe that
    /// keeps scaling can both be modelled exactly.
    fn drive_controller(
        controller: &mut ConcurrencyController,
        limiter: &ConcurrencyLimiter,
        windows: usize,
        rate_for: impl Fn(usize) -> f64,
    ) -> Vec<usize> {
        let mut now = Duration::ZERO;
        let mut completed = 0usize;
        let mut seen = Vec::new();
        for _ in 0..windows {
            let window = CONCURRENCY_WINDOW;
            now = now.saturating_add(window);
            completed += (rate_for(limiter.limit()) * window.as_secs_f64()) as usize;
            if let Some(limit) = controller.observe(now, completed) {
                limiter.set_limit(limit);
            }
            seen.push(limiter.limit());
        }
        seen
    }

    #[test]
    fn concurrency_controller_settles_where_the_device_stops_paying() {
        // A device that saturates hard at 8 in flight: more queue depth buys nothing.
        let limiter = ConcurrencyLimiter::new(4);
        let mut controller = ConcurrencyController::new(4, 64, Duration::ZERO);
        drive_controller(&mut controller, &limiter, 12, |limit| {
            (limit.min(8) * 50) as f64
        });
        assert_eq!(limiter.limit(), 8);
        assert_eq!(controller.best_limit, 8);
    }

    #[test]
    fn concurrency_controller_climbs_for_storage_that_keeps_scaling() {
        // NVMe-shaped: throughput keeps rising with queue depth, so it should reach the cap.
        let limiter = ConcurrencyLimiter::new(4);
        let mut controller = ConcurrencyController::new(4, 64, Duration::ZERO);
        drive_controller(&mut controller, &limiter, 12, |limit| (limit * 500) as f64);
        assert_eq!(limiter.limit(), 64);
    }

    #[test]
    fn concurrency_controller_backs_off_when_more_queue_depth_hurts() {
        // A single spinning disk: past a shallow queue, seek thrashing makes it worse.
        let limiter = ConcurrencyLimiter::new(4);
        let mut controller = ConcurrencyController::new(4, 64, Duration::ZERO);
        drive_controller(&mut controller, &limiter, 12, |limit| {
            if limit <= 4 {
                400.0
            } else {
                400.0 / (limit as f64 / 4.0)
            }
        });
        assert_eq!(limiter.limit(), 4);
    }

    #[test]
    fn concurrency_controller_ignores_windows_that_are_too_short_to_mean_anything() {
        let mut controller = ConcurrencyController::new(4, 64, Duration::ZERO);
        // Plenty of files, but well under the window: no signal, no change.
        assert_eq!(controller.observe(Duration::from_secs(1), 10_000), None);
        // A full window with nothing completed is equally meaningless.
        assert_eq!(controller.observe(CONCURRENCY_WINDOW, 0), None);
    }

    #[test]
    fn concurrency_controller_reprobes_after_settling() {
        let limiter = ConcurrencyLimiter::new(4);
        let mut controller = ConcurrencyController::new(4, 64, Duration::ZERO);
        drive_controller(&mut controller, &limiter, 6, |limit| {
            (limit.min(8) * 50) as f64
        });
        assert_eq!(limiter.limit(), 8);
        assert_eq!(controller.phase, ConcurrencyPhase::Settled);

        // The device frees up: a later probe must find the new headroom and resume climbing.
        let mut now = controller.window_started_at;
        let mut completed = controller.window_start_completed;
        let mut reached = limiter.limit();
        for _ in 0..12 {
            now = now.saturating_add(CONCURRENCY_WINDOW.max(Duration::from_secs(30)));
            completed += (limiter.limit() * 200) as usize;
            if let Some(limit) = controller.observe(now, completed) {
                limiter.set_limit(limit);
            }
            reached = reached.max(limiter.limit());
        }
        assert!(
            reached > 8,
            "a settled controller never re-probed for freed-up headroom (reached {reached})"
        );
    }

    /// A probe that pays off must resume a full climb. The earlier version flipped to Climbing
    /// but left the limit where it was, so the next window compared a limit against itself,
    /// settled again, and the scan crept up exactly one rung per probe interval.
    #[test]
    fn a_successful_probe_resumes_a_full_climb() {
        let limiter = ConcurrencyLimiter::new(4);
        let mut controller = ConcurrencyController::new(4, 64, Duration::ZERO);
        let mut now = Duration::ZERO;
        let mut completed = 0usize;
        let mut limits = Vec::new();

        for window in 0..80 {
            // The device saturates at 8 to begin with, so the controller settles there. Then
            // the competing load goes away and it scales freely.
            let limit = limiter.limit();
            let rate = if window < 12 {
                (limit.min(8) * 50) as f64
            } else {
                (limit * 50) as f64
            };
            now = now.saturating_add(CONCURRENCY_WINDOW);
            completed += (rate * CONCURRENCY_WINDOW.as_secs_f64()) as usize;
            if let Some(next) = controller.observe(now, completed) {
                limiter.set_limit(next);
            }
            limits.push(limiter.limit());
        }

        assert_eq!(limiter.limit(), 64);
        let mut longest = 1;
        let mut run = 1;
        for pair in limits.windows(2) {
            if pair[1] > pair[0] {
                run += 1;
                longest = longest.max(run);
            } else {
                run = 1;
            }
        }
        assert!(
            longest >= 3,
            "the climb advanced one rung per probe interval instead of resuming (longest back-to-back climb was {longest})"
        );
    }

    /// Storage that gets busier mid-scan must be met by backing off. This is the case a
    /// high-water baseline gets wrong: a rate recorded while the NAS was idle can never be
    /// beaten once it is loaded, so every probe "fails" and the limit freezes at the top.
    #[test]
    fn controller_backs_down_when_storage_gets_busier_after_settling() {
        let limiter = ConcurrencyLimiter::new(4);
        let mut controller = ConcurrencyController::new(4, 32, Duration::ZERO);
        let mut now = Duration::ZERO;
        let mut completed = 0usize;

        for _ in 0..12 {
            let rate = (limiter.limit() * 100) as f64;
            now = now.saturating_add(CONCURRENCY_WINDOW);
            completed += (rate * CONCURRENCY_WINDOW.as_secs_f64()) as usize;
            if let Some(next) = controller.observe(now, completed) {
                limiter.set_limit(next);
            }
        }
        assert_eq!(
            limiter.limit(),
            32,
            "should have climbed while it had headroom"
        );

        // The NAS is now busy: a deep queue thrashes it, a shallow one is faster.
        for _ in 0..120 {
            let limit = limiter.limit();
            let rate = if limit <= 4 {
                200.0
            } else {
                200.0 * 4.0 / limit as f64
            };
            now = now.saturating_add(CONCURRENCY_WINDOW);
            completed += (rate * CONCURRENCY_WINDOW.as_secs_f64()) as usize;
            if let Some(next) = controller.observe(now, completed) {
                limiter.set_limit(next);
            }
        }
        assert!(
            limiter.limit() < 32,
            "the controller stayed pinned at the ceiling while the device degraded (limit {})",
            limiter.limit()
        );
        assert_eq!(limiter.limit(), 4);
    }

    #[test]
    fn concurrency_limiter_never_exceeds_its_limit() {
        let limiter = Arc::new(ConcurrencyLimiter::new(3));
        let stop = Arc::new(AtomicBool::new(false));
        let in_flight = Arc::new(AtomicUsize::new(0));
        let peak = Arc::new(AtomicUsize::new(0));

        std::thread::scope(|scope| {
            for _ in 0..16 {
                let limiter = Arc::clone(&limiter);
                let stop = Arc::clone(&stop);
                let in_flight = Arc::clone(&in_flight);
                let peak = Arc::clone(&peak);
                scope.spawn(move || {
                    for _ in 0..40 {
                        let Some(permit) = limiter.acquire(&stop) else {
                            return;
                        };
                        let active = in_flight.fetch_add(1, Ordering::SeqCst) + 1;
                        record_high_water(&peak, active);
                        std::thread::yield_now();
                        in_flight.fetch_sub(1, Ordering::SeqCst);
                        drop(permit);
                    }
                });
            }
        });

        assert_eq!(in_flight.load(Ordering::SeqCst), 0);
        assert!(
            peak.load(Ordering::SeqCst) <= 3,
            "limiter allowed {} concurrent inspections past a limit of 3",
            peak.load(Ordering::SeqCst)
        );
    }

    #[test]
    fn concurrency_limiter_releases_workers_parked_during_cancellation() {
        let limiter = Arc::new(ConcurrencyLimiter::new(1));
        let stop = Arc::new(AtomicBool::new(false));
        // Take the only permit, so the spawned worker must park.
        let held = limiter.acquire(&stop).expect("first permit");

        std::thread::scope(|scope| {
            let worker_limiter = Arc::clone(&limiter);
            let worker_stop = Arc::clone(&stop);
            let parked = scope.spawn(move || worker_limiter.acquire(&worker_stop).is_none());
            stop.store(true, Ordering::Release);
            assert!(
                parked.join().expect("parked worker panicked"),
                "a worker parked on the limiter did not wake when the pipeline stopped"
            );
        });
        drop(held);
    }

    /// The limiter is resized underneath live workers, so this has to prove two things at once:
    /// that a real transition happens mid-scan, and that resizing does not cost or duplicate a
    /// single file. A never-advancing clock would silently skip the first half.
    #[test]
    fn adaptive_scan_retunes_mid_scan_and_still_matches_the_serial_reference() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        for index in 0..256u32 {
            materialize(
                &root,
                format!("steam/adaptive-{index}").as_bytes(),
                b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
                b"1234",
            );
        }
        let started = fixed_scan_started();
        let serial = serial_reference(&root, started, Layout::linux_x86_64());

        let clock = TickingClock::new(Duration::from_secs(1));
        let mut never_cancel = || false;
        let mut coverage = StructuralCoverage::default();
        let mut candidates = Vec::new();
        let mut scan_summary = StructuralScanSummary::stateless_full();
        let mut traversal_errors = 0;
        let (cancelled, telemetry) = run_parallel_inspection(
            &root,
            started,
            None,
            256,
            &mut coverage,
            &mut candidates,
            adaptive_test_config(1, 8, 8),
            &clock,
            None,
            None,
            &mut scan_summary,
            &mut traversal_errors,
            &mut never_cancel,
        )
        .unwrap();

        assert!(!cancelled);
        assert_eq!(coverage.files_seen, 256);
        // The limiter must actually have been resized under the running workers. Assert on the
        // peak, not the final limit: throughput here is bounded by the harness rather than by
        // any device, so the controller explores upward and then correctly backs all the way
        // down again, and the value it ends on proves nothing.
        assert!(
            telemetry.peak_worker_count > 1,
            "the limiter was never resized mid-scan (peak {})",
            telemetry.peak_worker_count
        );
        assert!(telemetry.peak_worker_count <= 8);
        assert!(telemetry.worker_count >= 1);
        // ...and resizing it under live workers must not lose, duplicate, or corrupt a file.
        let adaptive = build_report(started, candidates, coverage, false);
        assert_eq!(serial, adaptive);
    }

    struct ManualClock {
        millis: AtomicU64,
    }

    impl ManualClock {
        fn new() -> Self {
            Self {
                millis: AtomicU64::new(0),
            }
        }

        fn advance(&self, duration: Duration) {
            let millis = u64::try_from(duration.as_millis()).unwrap_or(u64::MAX);
            self.millis.fetch_add(millis, Ordering::Relaxed);
        }
    }

    impl ScanClock for ManualClock {
        fn elapsed(&self) -> Duration {
            Duration::from_millis(self.millis.load(Ordering::Relaxed))
        }
    }

    /// A clock that moves on its own, one tick per reading. `ManualClock` never advances unless
    /// a test advances it, which means the controller's windows never elapse and it never
    /// retunes: a scan driven by it proves nothing about adaptation. This one guarantees the
    /// windows close, so the pipeline really does resize its limiter mid-scan.
    struct TickingClock {
        millis: AtomicU64,
        tick_millis: u64,
    }

    impl TickingClock {
        fn new(tick: Duration) -> Self {
            Self {
                millis: AtomicU64::new(0),
                tick_millis: u64::try_from(tick.as_millis()).unwrap_or(u64::MAX),
            }
        }
    }

    impl ScanClock for TickingClock {
        fn elapsed(&self) -> Duration {
            let millis = self.millis.fetch_add(self.tick_millis, Ordering::Relaxed);
            Duration::from_millis(millis)
        }
    }

    fn fixed_scan_started() -> DateTime<Utc> {
        Utc::now() + chrono::Duration::seconds((MIN_STABLE_AGE_SECONDS + 30) as i64)
    }

    fn serial_reference(
        root: &Path,
        scan_started_utc: DateTime<Utc>,
        layout: Layout,
    ) -> CorruptionReport {
        let mut access = WorkerPathAccess::new(root).unwrap();
        serial_reference_with_access(root, scan_started_utc, layout, &mut access)
    }

    fn serial_reference_with_access(
        root: &Path,
        scan_started_utc: DateTime<Utc>,
        layout: Layout,
        access: &mut WorkerPathAccess,
    ) -> CorruptionReport {
        let mut coverage = StructuralCoverage::default();
        let mut total = 0usize;
        for entry in count_walk(root) {
            match entry {
                Ok(entry) => {
                    if cache_utils::strict_cache_file_digest(root, &entry.path()).is_some() {
                        total = total.saturating_add(1);
                    }
                }
                Err(error) if error.depth() > 0 => {
                    coverage.io_errors = coverage.io_errors.saturating_add(1);
                    let count = coverage
                        .skipped_by_reason
                        .entry("count_enumeration_io_error".to_string())
                        .or_default();
                    *count = count.saturating_add(1);
                }
                Err(error) => panic!("serial count root error: {error}"),
            }
        }

        let now = SystemTime::from(scan_started_utc);
        let mut candidates = Vec::new();
        let mut warning_count = 0usize;
        for entry in inspection_walk(root) {
            match entry {
                Ok(entry) => {
                    let path = entry.path();
                    let Some(path_digest) = cache_utils::strict_cache_file_digest(root, &path)
                    else {
                        continue;
                    };
                    let inspection = inspect_path_with_layout_and_access(
                        root,
                        &path,
                        path_digest,
                        now,
                        scan_started_utc,
                        layout,
                        access,
                        || {},
                    );
                    merge_inspection_result(
                        InspectionResult {
                            path,
                            path_digest,
                            revalidation: false,
                            inspection,
                        },
                        &mut coverage,
                        &mut candidates,
                        &mut warning_count,
                    );
                }
                Err(error) if error.depth() > 0 => {
                    coverage.io_errors = coverage.io_errors.saturating_add(1);
                    let count = coverage
                        .skipped_by_reason
                        .entry(SkipReason::IoError.as_str().to_string())
                        .or_default();
                    *count = count.saturating_add(1);
                }
                Err(error) => panic!("serial inspection root error: {error}"),
            }
        }
        assert_eq!(coverage.files_seen, total);
        build_report(scan_started_utc, candidates, coverage, false)
    }

    fn update_max(counter: &AtomicUsize, value: usize) {
        record_high_water(counter, value);
    }

    struct BarrierObserver {
        barrier: Barrier,
        active: AtomicUsize,
        max_active: AtomicUsize,
        max_bytes_read: AtomicUsize,
    }

    impl BarrierObserver {
        fn new(parties: usize) -> Self {
            Self {
                barrier: Barrier::new(parties),
                active: AtomicUsize::new(0),
                max_active: AtomicUsize::new(0),
                max_bytes_read: AtomicUsize::new(0),
            }
        }
    }

    impl InspectionObserver for BarrierObserver {
        fn inspection_started(&self, _path: &Path) {
            let active = self.active.fetch_add(1, Ordering::SeqCst) + 1;
            update_max(&self.max_active, active);
        }

        fn inspection_wait(&self, _path: &Path) {
            self.barrier.wait();
        }

        fn inspection_completed(&self, _path: &Path, inspection: &Result<Inspection>) {
            if let Ok(inspection) = inspection {
                update_max(
                    &self.max_bytes_read,
                    usize::try_from(inspection.bytes_read).unwrap_or(usize::MAX),
                );
            }
            self.active.fetch_sub(1, Ordering::SeqCst);
        }
    }

    #[test]
    fn parallel_inspection_is_concurrent_and_bounded() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path();
        let parallelism = 4;
        for index in 0..(parallelism * 4) {
            let key = format!("steam/concurrent-{index}");
            materialize(
                root,
                key.as_bytes(),
                b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
                b"data",
            );
        }
        let observer = BarrierObserver::new(parallelism);
        let clock = ManualClock::new();
        let started = fixed_scan_started();
        let mut never_cancel = || false;
        let result = scan_with_runtime(
            root,
            started,
            None,
            test_config(parallelism, parallelism),
            &clock,
            Some(&observer),
            &mut never_cancel,
        )
        .unwrap();

        assert!(!result.cancelled);
        assert!(!result.report.cancelled);
        assert!(observer.max_active.load(Ordering::SeqCst) > 1);
        assert!(observer.max_active.load(Ordering::SeqCst) <= parallelism);
        assert_eq!(result.report.coverage.as_ref().unwrap().files_seen, 16);
        assert_eq!(result.pipeline.scheduled, 16);
        assert_eq!(result.pipeline.completed, 16);
        assert_eq!(result.pipeline.merged, 16);
        assert!(result.pipeline.task_queue_high_water <= parallelism);
        assert!(result.pipeline.result_queue_high_water <= parallelism);
        assert!(result.pipeline.outstanding_high_water <= parallelism * 4 + 1);
    }

    #[test]
    fn parallel_scan_matches_serial_reference_for_mixed_fixtures() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path();
        let started = fixed_scan_started();
        materialize(
            root,
            b"steam/valid-200",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"data",
        );
        materialize(
            root,
            b"steam/valid-empty",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        materialize(
            root,
            b"steam/valid-206",
            b"HTTP/1.1 206 Partial\r\nContent-Range: bytes 0-3/10\r\nContent-Length: 4\r\n\r\n",
            b"data",
        );
        materialize(
            root,
            b"steam/short",
            b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n",
            b"data",
        );
        materialize(
            root,
            b"steam/overlong",
            b"HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\n",
            b"data",
        );
        materialize(
            root,
            b"steam/unsupported",
            b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        materialize(
            root,
            b"steam/ambiguous",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n",
            b"",
        );

        let zero_digest = u128::from_be_bytes(md5::compute(b"steam/zero").0);
        let zero_path = cache_utils::cache_path_for_digest(root, zero_digest);
        std::fs::create_dir_all(zero_path.parent().unwrap()).unwrap();
        std::fs::write(&zero_path, b"").unwrap();

        let truncated_digest = u128::from_be_bytes(md5::compute(b"steam/truncated").0);
        let truncated_path = cache_utils::cache_path_for_digest(root, truncated_digest);
        std::fs::create_dir_all(truncated_path.parent().unwrap()).unwrap();
        std::fs::write(&truncated_path, [0u8; 16]).unwrap();

        let (bad_crc_path, _) = materialize(
            root,
            b"steam/bad-crc",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let mut bad_crc = std::fs::read(&bad_crc_path).unwrap();
        put_u32(&mut bad_crc, Layout::linux_x86_64().crc32, 0);
        std::fs::write(&bad_crc_path, bad_crc).unwrap();

        let (mismatch_bytes, _) = fixture(
            b"steam/path-key-source",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let mismatch_digest = u128::from_be_bytes(md5::compute(b"steam/path-key-target").0);
        let mismatch_path = cache_utils::cache_path_for_digest(root, mismatch_digest);
        std::fs::create_dir_all(mismatch_path.parent().unwrap()).unwrap();
        std::fs::write(mismatch_path, mismatch_bytes).unwrap();
        let (recent_path, _) = materialize(
            root,
            b"steam/recent",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let recent_file = OpenOptions::new().write(true).open(recent_path).unwrap();
        recent_file
            .set_times(
                std::fs::FileTimes::new().set_modified(
                    SystemTime::from(started)
                        .checked_sub(Duration::from_secs(30))
                        .unwrap(),
                ),
            )
            .unwrap();
        std::fs::write(root.join("foreign-entry"), b"ignored").unwrap();

        let serial = serial_reference(root, started, Layout::linux_x86_64());
        let mut legacy_access = WorkerPathAccess::Legacy;
        let legacy =
            serial_reference_with_access(root, started, Layout::linux_x86_64(), &mut legacy_access);
        assert_eq!(serial, legacy);
        let clock = ManualClock::new();
        let mut never_cancel = || false;
        let parallel = scan_with_runtime(
            root,
            started,
            None,
            test_config(4, 4),
            &clock,
            None,
            &mut never_cancel,
        )
        .unwrap();
        assert_eq!(parallel.report, serial);
    }

    struct CancellationObserver {
        active: AtomicUsize,
        completed: AtomicUsize,
        cancellation_seen: AtomicBool,
        scheduled_after_cancellation: AtomicUsize,
        released: Mutex<bool>,
        release_signal: Condvar,
    }

    impl CancellationObserver {
        fn new() -> Self {
            Self {
                active: AtomicUsize::new(0),
                completed: AtomicUsize::new(0),
                cancellation_seen: AtomicBool::new(false),
                scheduled_after_cancellation: AtomicUsize::new(0),
                released: Mutex::new(false),
                release_signal: Condvar::new(),
            }
        }
    }

    impl InspectionObserver for CancellationObserver {
        fn task_scheduled(&self, _path: &Path) {
            if self.cancellation_seen.load(Ordering::SeqCst) {
                self.scheduled_after_cancellation
                    .fetch_add(1, Ordering::SeqCst);
            }
        }

        fn inspection_started(&self, _path: &Path) {
            self.active.fetch_add(1, Ordering::SeqCst);
        }

        fn inspection_wait(&self, _path: &Path) {
            let mut released = self.released.lock().unwrap();
            while !*released {
                released = self.release_signal.wait(released).unwrap();
            }
        }

        fn inspection_completed(&self, _path: &Path, _inspection: &Result<Inspection>) {
            self.completed.fetch_add(1, Ordering::SeqCst);
            self.active.fetch_sub(1, Ordering::SeqCst);
        }

        fn cancellation_observed(&self) {
            self.cancellation_seen.store(true, Ordering::SeqCst);
            let mut released = self.released.lock().unwrap();
            *released = true;
            self.release_signal.notify_all();
        }
    }

    #[test]
    fn parallel_scan_cancellation_stops_scheduling_and_drains_workers() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let progress_path = temp.path().join("progress.json");
        let parallelism = 4;
        for index in 0..64 {
            let key = format!("steam/cancel-{index}");
            materialize(
                &root,
                key.as_bytes(),
                b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
                b"data",
            );
        }
        let observer = CancellationObserver::new();
        let clock = ManualClock::new();
        let mut cancel_when_workers_are_held = || {
            observer.active.load(Ordering::SeqCst) == parallelism
                && !observer.cancellation_seen.load(Ordering::SeqCst)
        };
        let result = scan_with_runtime(
            &root,
            fixed_scan_started(),
            Some(&progress_path),
            test_config(parallelism, parallelism),
            &clock,
            Some(&observer),
            &mut cancel_when_workers_are_held,
        )
        .unwrap();

        assert!(result.cancelled);
        assert!(result.report.cancelled);
        assert!(observer.cancellation_seen.load(Ordering::SeqCst));
        assert_eq!(
            observer.scheduled_after_cancellation.load(Ordering::SeqCst),
            0
        );
        assert_eq!(observer.active.load(Ordering::SeqCst), 0);
        assert_eq!(result.pipeline.completed, result.pipeline.merged);
        assert_eq!(
            result.pipeline.completed,
            observer.completed.load(Ordering::SeqCst)
        );
        assert!(result.pipeline.scheduled >= result.pipeline.completed);
        assert!(result.pipeline.completed <= parallelism);
        let progress: serde_json::Value =
            serde_json::from_slice(&std::fs::read(progress_path).unwrap()).unwrap();
        assert_eq!(progress["status"], "cancelled");
        assert_eq!(
            progress["filesProcessed"].as_u64(),
            Some(result.pipeline.merged as u64)
        );
        assert!(progress["context"].get("etaSeconds").is_none());
    }

    #[cfg(unix)]
    #[test]
    fn cancelled_incremental_flushes_staging_and_resumes_without_promotion() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let state_db = temp.path().join("structural.sqlite3");
        let parallelism = 4;
        for index in 0..64 {
            let key = format!("steam/durable-cancel-{index}");
            materialize(
                &root,
                key.as_bytes(),
                b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
                b"data",
            );
        }
        let observer = CancellationObserver::new();
        let clock = ManualClock::new();
        let mut cancel_when_workers_are_held = || {
            observer.active.load(Ordering::SeqCst) == parallelism
                && !observer.cancellation_seen.load(Ordering::SeqCst)
        };
        let cancelled = scan_with_runtime_options(
            &root,
            fixed_scan_started(),
            None,
            test_config(parallelism, parallelism),
            &clock,
            Some(&observer),
            Some((StructuralScanMode::Incremental, &state_db, "default")),
            &mut cancel_when_workers_are_held,
        )
        .unwrap();
        assert!(cancelled.cancelled);
        assert!(!cancelled.scan_summary.state_committed);
        assert_eq!(cancelled.scan_summary.baseline_status, "incomplete");
        assert_eq!(cancelled.scan_summary.files_inspected, parallelism);

        let resumed = durable_scan(
            &root,
            &state_db,
            StructuralScanMode::Incremental,
            fixed_scan_started(),
        );
        assert!(resumed.scan_summary.resumed);
        assert_eq!(resumed.scan_summary.files_reused, parallelism);
        assert_eq!(resumed.scan_summary.files_inspected, 64 - parallelism);
        assert!(resumed.scan_summary.state_committed);
    }

    struct PathologicalObserver {
        mutate_after_read: PathBuf,
        disappear_before_open: PathBuf,
        mutated: AtomicBool,
        disappeared: AtomicBool,
        max_bytes_read: AtomicUsize,
    }

    impl InspectionObserver for PathologicalObserver {
        fn inspection_wait(&self, path: &Path) {
            if path == self.disappear_before_open && !self.disappeared.swap(true, Ordering::SeqCst)
            {
                std::fs::remove_file(path).unwrap();
            }
        }

        fn inspection_after_read(&self, path: &Path) {
            if path == self.mutate_after_read && !self.mutated.swap(true, Ordering::SeqCst) {
                let mut file = OpenOptions::new().append(true).open(path).unwrap();
                use std::io::Write;
                file.write_all(b"changed").unwrap();
                file.flush().unwrap();
            }
        }

        fn inspection_completed(&self, _path: &Path, inspection: &Result<Inspection>) {
            if let Ok(inspection) = inspection {
                update_max(
                    &self.max_bytes_read,
                    usize::try_from(inspection.bytes_read).unwrap_or(usize::MAX),
                );
            }
        }
    }

    #[test]
    fn parallel_scan_pathological_entries_remain_bounded_typed_outcomes() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let payload = vec![7u8; 1024 * 1024];
        let headers = format!(
            "HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n",
            payload.len()
        );
        materialize(&root, b"steam/million", headers.as_bytes(), &payload);

        let (tiny_offset, _) = materialize(
            &root,
            b"steam/tiny-offset",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"data",
        );
        let mut bytes = std::fs::read(&tiny_offset).unwrap();
        put_u16(&mut bytes, Layout::linux_x86_64().body_start, 1);
        std::fs::write(&tiny_offset, bytes).unwrap();

        let empty_digest = u128::from_be_bytes(md5::compute(b"steam/pathological-empty").0);
        let empty_path = cache_utils::cache_path_for_digest(&root, empty_digest);
        std::fs::create_dir_all(empty_path.parent().unwrap()).unwrap();
        std::fs::write(empty_path, b"").unwrap();

        let (ambiguous_path, _) = materialize(
            &root,
            b"steam/ambiguous-pathological",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\nContent-Length: 4\r\n\r\n",
            b"data",
        );
        let (mutate_path, _) = materialize(
            &root,
            b"steam/mutate",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"data",
        );
        let (disappear_path, _) = materialize(
            &root,
            b"steam/disappear",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"data",
        );
        std::fs::write(root.join("foreign-shape"), vec![1u8; 1024 * 1024]).unwrap();

        #[cfg(unix)]
        {
            use std::ffi::CString;
            use std::os::unix::ffi::OsStrExt;
            use std::os::unix::fs::symlink;
            let target = root.join("symlink-target");
            std::fs::write(&target, b"target").unwrap();
            let symlink_digest = u128::from_be_bytes(md5::compute(b"steam/symlink-pipeline").0);
            let symlink_path = cache_utils::cache_path_for_digest(&root, symlink_digest);
            std::fs::create_dir_all(symlink_path.parent().unwrap()).unwrap();
            symlink(target, symlink_path).unwrap();

            let fifo_digest = u128::from_be_bytes(md5::compute(b"steam/fifo-pipeline").0);
            let fifo_path = cache_utils::cache_path_for_digest(&root, fifo_digest);
            std::fs::create_dir_all(fifo_path.parent().unwrap()).unwrap();
            let fifo_c = CString::new(fifo_path.as_os_str().as_bytes()).unwrap();
            assert_eq!(unsafe { libc::mkfifo(fifo_c.as_ptr(), 0o600) }, 0);
        }

        let observer = PathologicalObserver {
            mutate_after_read: mutate_path,
            disappear_before_open: disappear_path,
            mutated: AtomicBool::new(false),
            disappeared: AtomicBool::new(false),
            max_bytes_read: AtomicUsize::new(0),
        };
        let clock = ManualClock::new();
        let mut never_cancel = || false;
        let result = scan_with_runtime(
            &root,
            fixed_scan_started(),
            None,
            test_config(4, 4),
            &clock,
            Some(&observer),
            &mut never_cancel,
        )
        .unwrap();
        let coverage = result.report.coverage.as_ref().unwrap();
        assert!(observer.max_bytes_read.load(Ordering::SeqCst) <= MAX_PREFIX_BYTES as usize);
        assert!(
            coverage
                .skipped_by_reason
                .get("changed")
                .copied()
                .unwrap_or(0)
                >= 2
        );
        assert_eq!(
            coverage
                .skipped_by_reason
                .get("ambiguous_http_headers")
                .copied(),
            Some(1)
        );
        #[cfg(unix)]
        {
            assert_eq!(coverage.skipped_by_reason.get("symlink").copied(), Some(1));
            assert_eq!(
                coverage.skipped_by_reason.get("special_file").copied(),
                Some(1)
            );
        }
        assert_eq!(result.report.total, 2);
        assert!(result.report.candidates.iter().all(|candidate| {
            candidate.exact_paths != [ambiguous_path.display().to_string()]
                && candidate.exact_paths != [observer.mutate_after_read.display().to_string()]
                && candidate.exact_paths != [observer.disappear_before_open.display().to_string()]
        }));
    }

    #[test]
    fn parallel_progress_is_time_based_and_rate_eta_are_sane() {
        let temp = tempfile::tempdir().unwrap();
        let progress_path = temp.path().join("progress.json");
        let clock = ManualClock::new();
        let config = test_config(4, 4);
        let mut telemetry = InspectionTelemetry::new(clock.elapsed());
        let mut coverage = StructuralCoverage::default();
        let scan_summary = StructuralScanSummary::stateless_full();

        update_progress(
            Some(&progress_path),
            "scanning",
            "scanning",
            0,
            10_000,
            0,
            &coverage,
            ProgressDetails {
                rate: ProgressRate {
                    elapsed_seconds: 0.0,
                    files_per_second: Some(0.0),
                },
                eta_seconds: None,
                worker_count: 4,
                task_queue_capacity: 4,
                result_queue_capacity: 4,
            },
            &scan_summary,
        )
        .unwrap();
        let immediate = std::fs::read_to_string(&progress_path).unwrap();
        let immediate_value: serde_json::Value = serde_json::from_str(&immediate).unwrap();
        assert_eq!(immediate_value["filesProcessed"], 0);
        assert_eq!(immediate_value["totalFiles"], 10_000);
        assert_eq!(immediate_value["percentComplete"], 0.0);
        assert_eq!(immediate_value["context"]["scanMode"], "full");
        assert_eq!(immediate_value["context"]["effectiveScanMode"], "full");
        assert_eq!(immediate_value["context"]["baselineStatus"], "stateless");
        assert_eq!(immediate_value["context"]["filesDiscovered"], 0);
        assert_eq!(immediate_value["context"]["filesReused"], 0);
        assert_eq!(immediate_value["context"]["filesInspected"], 0);
        assert_eq!(immediate_value["context"]["filesRevalidated"], 0);
        assert_eq!(immediate_value["context"]["invalidFiles"], 0);
        assert_eq!(immediate_value["context"]["filesPendingRetry"], 0);
        assert_eq!(immediate_value["context"]["stateCommitted"], false);
        assert!(immediate_value["context"].get("etaSeconds").is_none());

        clock.advance(Duration::from_millis(500));
        assert!(!telemetry.progress_due(clock.elapsed(), config.progress_interval));
        assert_eq!(std::fs::read_to_string(&progress_path).unwrap(), immediate);

        coverage.files_seen = 1;
        clock.advance(Duration::from_millis(600));
        assert!(telemetry.progress_due(clock.elapsed(), config.progress_interval));
        let details = progress_details(
            &mut telemetry,
            clock.elapsed(),
            coverage.files_seen,
            10_000,
            config,
            config.inspection_parallelism,
            true,
        );
        update_progress(
            Some(&progress_path),
            "scanning",
            "scanning",
            coverage.files_seen,
            10_000,
            0,
            &coverage,
            details,
            &scan_summary,
        )
        .unwrap();
        let below_one_percent: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&progress_path).unwrap()).unwrap();
        assert!(below_one_percent["percentComplete"].as_f64().unwrap() < 1.0);
        assert!(below_one_percent["context"]["filesPerSecond"]
            .as_f64()
            .unwrap()
            .is_finite());

        coverage.files_seen = 2;
        clock.advance(Duration::from_millis(1_100));
        let details = progress_details(
            &mut telemetry,
            clock.elapsed(),
            coverage.files_seen,
            10_000,
            config,
            config.inspection_parallelism,
            true,
        );
        assert!(details.rate.files_per_second.unwrap().is_finite());
        assert!(details.rate.files_per_second.unwrap() >= 0.0);
        assert!(details.eta_seconds.is_some());
        update_progress(
            Some(&progress_path),
            "completed",
            "scanning",
            10_000,
            10_000,
            0,
            &coverage,
            ProgressDetails {
                eta_seconds: Some(0),
                ..details
            },
            &scan_summary,
        )
        .unwrap();
        let terminal: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&progress_path).unwrap()).unwrap();
        assert_eq!(terminal["percentComplete"], 100.0);
        assert_eq!(terminal["context"]["etaSeconds"], 0);

        let enumeration_path = temp.path().join("enumeration.json");
        update_progress(
            Some(&enumeration_path),
            "scanning",
            "counting",
            7,
            0,
            0,
            &coverage,
            ProgressDetails {
                eta_seconds: Some(999),
                ..details
            },
            &scan_summary,
        )
        .unwrap();
        let enumeration: serde_json::Value =
            serde_json::from_slice(&std::fs::read(enumeration_path).unwrap()).unwrap();
        assert_eq!(enumeration["context"]["count"], 7);
        assert_eq!(enumeration["percentComplete"], 0.0);
        assert!(enumeration["context"].get("etaSeconds").is_none());

        let normalized = StructuralScanConfig {
            inspection_parallelism: 0,
            task_queue_capacity: 0,
            result_queue_capacity: 0,
            ..config
        }
        .normalized();
        assert_eq!(normalized.inspection_parallelism, 1);
        assert_eq!(normalized.task_queue_capacity, 1);
        assert_eq!(normalized.result_queue_capacity, 1);
    }

    struct FirstWaveObserver {
        parties: usize,
        arrivals: AtomicUsize,
        active: AtomicUsize,
        max_active: AtomicUsize,
        released: Mutex<bool>,
        release_signal: Condvar,
    }

    impl FirstWaveObserver {
        fn new(parties: usize) -> Self {
            Self {
                parties,
                arrivals: AtomicUsize::new(0),
                active: AtomicUsize::new(0),
                max_active: AtomicUsize::new(0),
                released: Mutex::new(false),
                release_signal: Condvar::new(),
            }
        }
    }

    impl InspectionObserver for FirstWaveObserver {
        fn inspection_started(&self, _path: &Path) {
            let active = self.active.fetch_add(1, Ordering::SeqCst) + 1;
            update_max(&self.max_active, active);
        }

        fn inspection_wait(&self, _path: &Path) {
            let ordinal = self.arrivals.fetch_add(1, Ordering::SeqCst);
            if ordinal >= self.parties {
                return;
            }
            let mut released = self.released.lock().unwrap();
            if ordinal + 1 == self.parties {
                *released = true;
                self.release_signal.notify_all();
            }
            while !*released {
                released = self.release_signal.wait(released).unwrap();
            }
        }

        fn inspection_completed(&self, _path: &Path, _inspection: &Result<Inspection>) {
            self.active.fetch_sub(1, Ordering::SeqCst);
        }
    }

    #[cfg(target_os = "linux")]
    fn linux_rss_kib() -> (Option<u64>, Option<u64>) {
        let Ok(status) = std::fs::read_to_string("/proc/self/status") else {
            return (None, None);
        };
        let parse = |name: &str| {
            status.lines().find_map(|line| {
                let value = line.strip_prefix(name)?.trim();
                value.split_ascii_whitespace().next()?.parse::<u64>().ok()
            })
        };
        (parse("VmRSS:"), parse("VmHWM:"))
    }

    #[cfg(target_os = "linux")]
    fn with_rss_monitor<T>(
        operation: impl FnOnce() -> T,
    ) -> (T, Option<u64>, Option<u64>, Option<u64>) {
        let (baseline, _) = linux_rss_kib();
        let peak = Arc::new(AtomicU64::new(baseline.unwrap_or(0)));
        let stop = Arc::new(AtomicBool::new(false));
        let monitor_peak = Arc::clone(&peak);
        let monitor_stop = Arc::clone(&stop);
        let monitor = std::thread::spawn(move || {
            while !monitor_stop.load(Ordering::Acquire) {
                if let (Some(rss), _) = linux_rss_kib() {
                    let mut current = monitor_peak.load(Ordering::Relaxed);
                    while rss > current {
                        match monitor_peak.compare_exchange_weak(
                            current,
                            rss,
                            Ordering::Relaxed,
                            Ordering::Relaxed,
                        ) {
                            Ok(_) => break,
                            Err(actual) => current = actual,
                        }
                    }
                }
                std::thread::sleep(Duration::from_millis(5));
            }
        });
        let result = operation();
        stop.store(true, Ordering::Release);
        monitor.join().unwrap();
        let (_, high_water) = linux_rss_kib();
        (
            result,
            baseline,
            Some(peak.load(Ordering::Relaxed)),
            high_water,
        )
    }

    #[cfg(not(target_os = "linux"))]
    fn with_rss_monitor<T>(
        operation: impl FnOnce() -> T,
    ) -> (T, Option<u64>, Option<u64>, Option<u64>) {
        (operation(), None, None, None)
    }

    #[test]
    #[ignore]
    fn parallel_structural_scan_throughput_and_rss() {
        let fixture_count = std::env::var("STRUCTURAL_BENCH_N")
            .ok()
            .and_then(|value| value.parse::<usize>().ok())
            .unwrap_or(20_000)
            .max(1);
        let requested_threads = std::env::var("STRUCTURAL_BENCH_THREADS")
            .ok()
            .and_then(|value| value.parse::<usize>().ok())
            .unwrap_or(8)
            .max(1);
        let worker_count = requested_threads.min(fixture_count);
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path();

        let fixture_started = Instant::now();
        for index in 0..fixture_count {
            let key = format!("steam/benchmark-{index:08}");
            materialize(
                root,
                key.as_bytes(),
                b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
                b"data",
            );
        }
        let fixture_elapsed = fixture_started.elapsed();

        let count_started = Instant::now();
        let counted = count_walk(root)
            .filter_map(std::result::Result::ok)
            .filter(|entry| cache_utils::strict_cache_file_digest(root, &entry.path()).is_some())
            .count();
        let count_elapsed = count_started.elapsed();
        assert_eq!(counted, fixture_count);

        let scan_started_utc = fixed_scan_started();
        let now = SystemTime::from(scan_started_utc);
        let legacy_started = Instant::now();
        let mut legacy_access = WorkerPathAccess::Legacy;
        let mut legacy_coverage = StructuralCoverage::default();
        let mut legacy_candidates = Vec::new();
        let mut legacy_warning_count = 0usize;
        for entry in inspection_walk(root) {
            let entry = entry.unwrap();
            let path = entry.path();
            let Some(path_digest) = cache_utils::strict_cache_file_digest(root, &path) else {
                continue;
            };
            let inspection = inspect_path_with_layout_and_access(
                root,
                &path,
                path_digest,
                now,
                scan_started_utc,
                Layout::linux_x86_64(),
                &mut legacy_access,
                || {},
            );
            merge_inspection_result(
                InspectionResult {
                    path,
                    path_digest,
                    revalidation: false,
                    inspection,
                },
                &mut legacy_coverage,
                &mut legacy_candidates,
                &mut legacy_warning_count,
            );
        }
        let legacy_elapsed = legacy_started.elapsed();
        assert_eq!(legacy_coverage.files_seen, fixture_count);
        assert_eq!(legacy_coverage.consistent, fixture_count);
        assert!(legacy_candidates.is_empty());

        let rooted_started = Instant::now();
        let mut rooted_access = WorkerPathAccess::new(root).unwrap();
        let mut serial_coverage = StructuralCoverage::default();
        let mut serial_candidates = Vec::new();
        let mut warning_count = 0usize;
        for entry in inspection_walk(root) {
            let entry = entry.unwrap();
            let path = entry.path();
            let Some(path_digest) = cache_utils::strict_cache_file_digest(root, &path) else {
                continue;
            };
            let inspection = inspect_path_with_layout_and_access(
                root,
                &path,
                path_digest,
                now,
                scan_started_utc,
                Layout::linux_x86_64(),
                &mut rooted_access,
                || {},
            );
            merge_inspection_result(
                InspectionResult {
                    path,
                    path_digest,
                    revalidation: false,
                    inspection,
                },
                &mut serial_coverage,
                &mut serial_candidates,
                &mut warning_count,
            );
        }
        let serial_elapsed = rooted_started.elapsed();
        assert_eq!(serial_coverage.files_seen, fixture_count);
        assert_eq!(serial_coverage.consistent, fixture_count);
        assert!(serial_candidates.is_empty());
        assert_eq!(serial_coverage, legacy_coverage);
        assert_eq!(serial_candidates, legacy_candidates);

        let observer = FirstWaveObserver::new(worker_count);
        let config = test_config(worker_count, worker_count);
        let clock = RealScanClock::new();
        let mut parallel_coverage = StructuralCoverage::default();
        let mut parallel_candidates = Vec::new();
        let mut never_cancel = || false;
        let mut scan_summary = StructuralScanSummary::stateless_full();
        let mut traversal_errors = 0;
        let parallel_started = Instant::now();
        let (parallel_result, rss_baseline, rss_peak, rss_high_water) = with_rss_monitor(|| {
            run_parallel_inspection(
                root,
                scan_started_utc,
                None,
                fixture_count,
                &mut parallel_coverage,
                &mut parallel_candidates,
                config,
                &clock,
                Some(&observer),
                None,
                &mut scan_summary,
                &mut traversal_errors,
                &mut never_cancel,
            )
        });
        let parallel_elapsed = parallel_started.elapsed();
        let (cancelled, telemetry) = parallel_result.unwrap();
        assert!(!cancelled);
        assert_eq!(parallel_coverage, serial_coverage);
        assert!(parallel_candidates.is_empty());
        assert!(telemetry.task_queue_high_water <= worker_count);
        assert!(telemetry.result_queue_high_water <= worker_count);
        assert!(telemetry.outstanding_high_water <= worker_count * 4 + 1);
        assert_eq!(telemetry.scheduled, fixture_count);
        assert_eq!(telemetry.completed, fixture_count);
        assert_eq!(telemetry.merged, fixture_count);
        assert!(observer.max_active.load(Ordering::SeqCst) <= worker_count);
        if worker_count > 1 {
            assert!(observer.max_active.load(Ordering::SeqCst) > 1);
        }

        let count_rate = fixture_count as f64 / count_elapsed.as_secs_f64();
        let legacy_rate = fixture_count as f64 / legacy_elapsed.as_secs_f64();
        let serial_rate = fixture_count as f64 / serial_elapsed.as_secs_f64();
        let parallel_rate = fixture_count as f64 / parallel_elapsed.as_secs_f64();
        let rooted_speedup = legacy_elapsed.as_secs_f64() / serial_elapsed.as_secs_f64();
        let parallel_speedup = legacy_elapsed.as_secs_f64() / parallel_elapsed.as_secs_f64();
        eprintln!(
            "[structural-bench] fixtures={} creation_seconds={:.3} count_seconds={:.3} count_files_per_second={:.1}",
            fixture_count,
            fixture_elapsed.as_secs_f64(),
            count_elapsed.as_secs_f64(),
            count_rate
        );
        eprintln!(
            "[structural-bench] legacy_seconds={:.3} legacy_files_per_second={:.1} rooted_serial_seconds={:.3} rooted_serial_files_per_second={:.1} rooted_speedup={:.2}",
            legacy_elapsed.as_secs_f64(),
            legacy_rate,
            serial_elapsed.as_secs_f64(),
            serial_rate,
            rooted_speedup
        );
        eprintln!(
            "[structural-bench] parallel_seconds={:.3} parallel_files_per_second={:.1} speedup_vs_legacy={:.2} workers={}",
            parallel_elapsed.as_secs_f64(),
            parallel_rate,
            parallel_speedup,
            worker_count
        );
        eprintln!(
            "[structural-bench] extrapolation_parallel_minutes_2m={:.1} extrapolation_parallel_minutes_4m={:.1} task_high_water={} result_high_water={} outstanding_high_water={}",
            2_000_000.0 / parallel_rate / 60.0,
            4_000_000.0 / parallel_rate / 60.0,
            telemetry.task_queue_high_water,
            telemetry.result_queue_high_water,
            telemetry.outstanding_high_water
        );
        eprintln!(
            "[structural-bench] rss_baseline_kib={} rss_peak_kib={} rss_delta_kib={} vmhwm_kib={}",
            rss_baseline.map_or_else(|| "unavailable".to_string(), |value| value.to_string()),
            rss_peak.map_or_else(|| "unavailable".to_string(), |value| value.to_string()),
            rss_baseline
                .zip(rss_peak)
                .map(|(baseline, peak)| peak.saturating_sub(baseline))
                .map_or_else(|| "unavailable".to_string(), |value| value.to_string()),
            rss_high_water.map_or_else(|| "unavailable".to_string(), |value| value.to_string())
        );
    }

    #[test]
    fn authentic_v5_200_and_valid_empty_are_consistent() {
        for payload in [b"hello".as_slice(), b"".as_slice()] {
            let headers = format!(
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n",
                payload.len()
            );
            let (bytes, digest) =
                fixture(b"steam/depot/filebytes=0-4", headers.as_bytes(), payload);
            assert!(matches!(
                parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()),
                ParseOutcome::Consistent
            ));
        }
    }

    #[cfg(all(target_os = "linux", target_arch = "x86_64"))]
    #[test]
    fn native_linux_x86_64_layout_matches_nginx_v5_abi() {
        let native = Layout::native().unwrap();
        let expected = Layout::linux_x86_64();
        assert_eq!(native.size, 336);
        assert_eq!(native.version, expected.version);
        assert_eq!(native.crc32, expected.crc32);
        assert_eq!(native.header_start, expected.header_start);
        assert_eq!(native.body_start, expected.body_start);
        assert_eq!(native.etag_len, expected.etag_len);
        assert_eq!(native.vary_len, expected.vary_len);
        assert_eq!(native.variant, expected.variant);
    }

    #[test]
    fn payload_short_overlong_and_offsets_are_proven() {
        for payload in [b"1234".as_slice(), b"123456".as_slice()] {
            let (bytes, digest) = fixture(
                b"steam/file",
                b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n",
                payload,
            );
            assert_eq!(
                issues(parse_prefix(
                    &bytes,
                    bytes.len() as u64,
                    digest,
                    Layout::linux_x86_64()
                )),
                [StructuralIssue::PayloadLengthMismatch]
            );
        }
        let (mut bytes, digest) = fixture(
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let layout = Layout::linux_x86_64();
        put_u16(&mut bytes, layout.body_start, 1);
        assert_eq!(
            issues(parse_prefix(&bytes, bytes.len() as u64, digest, layout)),
            [StructuralIssue::InvalidPayloadOffset]
        );
        put_u16(&mut bytes, layout.body_start, u16::MAX);
        assert_eq!(
            issues(parse_prefix(&bytes, bytes.len() as u64, digest, layout)),
            [StructuralIssue::TruncatedBeforePayload]
        );
    }

    #[test]
    fn envelope_corruptions_and_path_mismatch_are_proven() {
        let (base, digest) = fixture(
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let layout = Layout::linux_x86_64();
        let mut bad = base.clone();
        bad[layout.size + 1] ^= 1;
        assert_eq!(
            issues(parse_prefix(&bad, bad.len() as u64, digest, layout)),
            [StructuralIssue::MalformedCacheHeader]
        );
        let mut bad = base.clone();
        put_u32(&mut bad, layout.crc32, 0);
        assert_eq!(
            issues(parse_prefix(&bad, bad.len() as u64, digest, layout)),
            [StructuralIssue::MalformedCacheHeader]
        );
        for length_offset in [layout.etag_len, layout.vary_len] {
            let mut bad = base.clone();
            bad[length_offset] = 129;
            assert_eq!(
                issues(parse_prefix(&bad, bad.len() as u64, digest, layout)),
                [StructuralIssue::MalformedCacheHeader]
            );
        }
        assert_eq!(
            issues(parse_prefix(&base, base.len() as u64, digest ^ 1, layout)),
            [StructuralIssue::CacheKeyPathMismatch]
        );
        assert!(matches!(
            parse_prefix(&[], 0, digest, layout),
            ParseOutcome::Proven(_)
        ));
        let short = &base[..layout.size + 2];
        assert_eq!(
            issues(parse_prefix(short, short.len() as u64, digest, layout)),
            [StructuralIssue::TruncatedCacheHeader]
        );
    }

    #[test]
    fn range_chunked_encoding_and_skip_shapes_follow_closed_table() {
        let cases = [
            (
                b"HTTP/1.1 206 Partial\r\nContent-Range: bytes 0-4/10\r\nContent-Length: 5\r\n\r\n"
                    .as_slice(),
                b"12345".as_slice(),
                None,
            ),
            (
                b"HTTP/1.1 206 Partial\r\nContent-Range: bytes 5-9/10\r\nContent-Length: 5\r\n\r\n",
                b"1234",
                Some(StructuralIssue::ContentRangeLengthMismatch),
            ),
            (
                b"HTTP/1.1 206 Partial\r\nContent-Range: bytes 0-4/10\r\nContent-Length: 4\r\n\r\n",
                b"12345",
                Some(StructuralIssue::ContentLengthRangeConflict),
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 5\r\n\r\n",
                b"12345",
                None,
            ),
        ];
        for (headers, payload, expected_issue) in cases {
            let (bytes, digest) = fixture(b"steam/file", headers, payload);
            let outcome = parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64());
            match expected_issue {
                Some(issue) => assert!(issues(outcome).contains(&issue)),
                None => assert!(matches!(outcome, ParseOutcome::Consistent)),
            }
        }
        for headers in [
            b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n".as_slice(),
            b"HTTP/1.1 200 OK\r\n\r\n",
        ] {
            let (bytes, digest) = fixture(b"steam/file", headers, b"data");
            assert!(matches!(
                parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()),
                ParseOutcome::Skip(SkipReason::NoAuthoritativeLength)
            ));
        }
    }

    #[test]
    fn ambiguous_and_unsupported_http_never_become_candidates() {
        for (headers, reason) in [
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\n".as_slice(),
                SkipReason::AmbiguousHttpHeaders,
            ),
            (
                b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n",
                SkipReason::UnsupportedStatus,
            ),
            (
                b"HTTP/1.1 206 Partial\r\nContent-Type: multipart/byteranges\r\n\r\n",
                SkipReason::UnsupportedRangeShape,
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 18446744073709551616\r\n\r\n",
                SkipReason::AmbiguousHttpHeaders,
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n",
                SkipReason::AmbiguousHttpHeaders,
            ),
            (
                b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\nX-Late: value\r\n\r\n",
                SkipReason::AmbiguousHttpHeaders,
            ),
            (b"NOT HTTP\r\n\r\n", SkipReason::UnsupportedStatus),
        ] {
            let (bytes, digest) = fixture(b"steam/file", headers, b"");
            assert!(
                matches!(parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()), ParseOutcome::Skip(actual) if actual == reason)
            );
        }
    }

    #[test]
    fn valid_vary_variant_path_is_consistent() {
        let (mut bytes, _) = fixture(
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
            b"",
        );
        let layout = Layout::linux_x86_64();
        bytes[layout.vary_len] = 4;
        let variant = [7u8; 16];
        bytes[layout.variant..layout.variant + 16].copy_from_slice(&variant);
        assert!(matches!(
            parse_prefix(
                &bytes,
                bytes.len() as u64,
                u128::from_be_bytes(variant),
                layout
            ),
            ParseOutcome::Consistent
        ));
    }

    #[test]
    fn chunked_206_uses_content_range_and_ignores_content_length() {
        let (bytes, digest) = fixture(
            b"steam/file",
            b"HTTP/1.1 206 Partial\r\nTransfer-Encoding: chunked\r\nContent-Range: bytes 0-4/10\r\nContent-Length: 999\r\n\r\n",
            b"12345",
        );
        assert!(matches!(
            parse_prefix(&bytes, bytes.len() as u64, digest, Layout::linux_x86_64()),
            ParseOutcome::Consistent
        ));
    }

    #[test]
    fn filesystem_inspection_reads_only_through_payload_offset() {
        let temp = tempfile::tempdir().unwrap();
        let payload = vec![7u8; 1024 * 1024];
        let headers = format!(
            "HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n",
            payload.len()
        );
        let (path, body_start) =
            materialize(temp.path(), b"steam/file", headers.as_bytes(), &payload);
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(inspection.outcome, InspectionOutcome::Consistent));
        assert_eq!(inspection.bytes_read, body_start as u64);
        assert!(inspection.bytes_read <= MAX_PREFIX_BYTES);
        assert!(inspection.bytes_read < std::fs::metadata(path).unwrap().len());
    }

    #[test]
    fn malformed_small_payload_offset_reads_only_the_offset_probe() {
        let temp = tempfile::tempdir().unwrap();
        let (path, _) = materialize(
            temp.path(),
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 1024\r\n\r\n",
            &[7u8; 1024],
        );
        let layout = Layout::linux_x86_64();
        let mut bytes = std::fs::read(&path).unwrap();
        put_u16(&mut bytes, layout.body_start, 1);
        std::fs::write(&path, bytes).unwrap();

        let mut file = open_nofollow(&path).unwrap();
        let file_len = file.metadata().unwrap().len();
        let (prefix, bytes_read) = read_prefix(&mut file, file_len, layout).unwrap();
        let expected_probe = layout.body_start + std::mem::size_of::<u16>();
        assert_eq!(bytes_read, expected_probe as u64);
        assert_eq!(prefix.len(), expected_probe);
        assert!(bytes_read < (layout.size + KEY_MARKER.len()) as u64);
    }

    #[test]
    fn root_failure_is_fatal_and_count_pass_is_cancellable() {
        let temp = tempfile::tempdir().unwrap();
        assert!(
            scan_with_cancellation(&temp.path().join("missing"), Utc::now(), None, || false)
                .is_err()
        );

        let root = temp.path().join("cache");
        for digest in [1u128, 2, 3] {
            let path = cache_utils::cache_path_for_digest(&root, digest);
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, b"").unwrap();
        }
        let checks = std::cell::Cell::new(0usize);
        let result = scan_with_cancellation(&root, Utc::now(), None, || {
            let next = checks.get() + 1;
            checks.set(next);
            next >= 2
        })
        .unwrap();
        assert!(result.cancelled);
        assert!(result.report.cancelled);
        assert!(checks.get() >= 2);
    }

    #[test]
    fn recent_and_mid_read_mutation_are_typed_skips() {
        let temp = tempfile::tempdir().unwrap();
        let (path, _) = materialize(
            temp.path(),
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"1234",
        );
        let recent = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            SystemTime::now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(recent.outcome, InspectionOutcome::Skip("recent")));

        let replacement = std::fs::read(&path).unwrap();
        let changed = inspect_path_with_layout_and_hook(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
            || {
                let mut replacement = replacement.clone();
                replacement.push(0);
                std::fs::write(&path, replacement).unwrap();
            },
        )
        .unwrap();
        assert!(matches!(
            changed.outcome,
            InspectionOutcome::Skip("changed")
        ));
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn rooted_candidate_rechecks_the_current_parent_path_after_read() {
        use std::os::unix::fs::symlink;

        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("cache");
        let (path, _) = materialize(
            &root,
            b"steam/rooted-parent-swap",
            b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n",
            b"1234",
        );
        let parent = path.parent().unwrap().to_path_buf();
        let moved_parent = parent.with_extension("moved");
        let outside_parent = temp.path().join("outside");
        std::fs::create_dir_all(&outside_parent).unwrap();

        let inspection = inspect_path_with_layout_and_hook(
            &root,
            &path,
            cache_utils::strict_cache_file_digest(&root, &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
            || {
                std::fs::rename(&parent, &moved_parent).unwrap();
                symlink(&outside_parent, &parent).unwrap();
            },
        )
        .unwrap();

        assert!(matches!(
            inspection.outcome,
            InspectionOutcome::Skip("changed")
        ));
    }

    #[test]
    fn structural_revalidation_resolves_missing_healed_and_rejects_changed() {
        let temp = tempfile::tempdir().unwrap();
        let headers = b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n";
        let (path, _) = materialize(temp.path(), b"steam/file", headers, b"1234");
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        let InspectionOutcome::Proven(evidence) = inspection.outcome else {
            panic!("expected structural evidence");
        };
        let candidate = structural_candidate(&path, evidence);
        assert!(matches!(
            revalidate_for_removal_with_layout(
                temp.path(),
                &candidate,
                old_now(),
                Layout::linux_x86_64()
            )
            .unwrap(),
            RemovalDisposition::Ready { .. }
        ));

        std::fs::remove_file(&path).unwrap();
        assert!(matches!(
            revalidate_for_removal_with_layout(
                temp.path(),
                &candidate,
                old_now(),
                Layout::linux_x86_64()
            )
            .unwrap(),
            RemovalDisposition::Missing
        ));

        materialize(
            temp.path(),
            b"steam/file",
            b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n",
            b"1234",
        );
        assert!(matches!(
            revalidate_for_removal_with_layout(
                temp.path(),
                &candidate,
                old_now(),
                Layout::linux_x86_64()
            )
            .unwrap(),
            RemovalDisposition::Healed
        ));

        materialize(temp.path(), b"steam/file", headers, b"12");
        assert!(revalidate_for_removal_with_layout(
            temp.path(),
            &candidate,
            old_now(),
            Layout::linux_x86_64()
        )
        .is_err());
    }

    #[test]
    fn structural_wire_evidence_is_flat_tagged_and_has_no_observations() {
        let evidence = StructuralEvidence {
            issues: vec![StructuralIssue::EmptyCacheFile],
            cache_key_encoding: "hex".into(),
            cache_key: String::new(),
            cache_key_md5: "d41d8cd98f00b204e9800998ecf8427e".into(),
            cache_version: 5,
            http_status: None,
            header_start: None,
            body_start: None,
            file_length: 0,
            actual_payload_length: None,
            expected_payload_length: None,
            content_length: None,
            content_range: None,
            fingerprint: FileFingerprint {
                dev: 1,
                ino: 2,
                len: 0,
                mtime_ns: 3,
                ctime_ns: 4,
            },
            detected_at_utc: "2024-01-01T00:00:00Z".into(),
        };
        let value = serde_json::to_value(CorruptionEvidence::Structural {
            structural: evidence,
        })
        .unwrap();
        assert_eq!(value["kind"], "structural");
        assert_eq!(value["issues"][0], "empty_cache_file");
        assert!(value.get("structural").is_none());
        assert!(value.get("observations").is_none());
    }

    #[cfg(unix)]
    #[test]
    fn symlink_leaf_is_never_followed() {
        use std::os::unix::fs::symlink;
        let temp = tempfile::tempdir().unwrap();
        let target = temp.path().join("target");
        std::fs::write(&target, b"target").unwrap();
        let digest = u128::from_be_bytes(md5::compute(b"steam/file").0);
        let path = cache_utils::cache_path_for_digest(temp.path(), digest);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        symlink(&target, &path).unwrap();
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(
            inspection.outcome,
            InspectionOutcome::Skip("symlink")
        ));
        assert_eq!(std::fs::read(target).unwrap(), b"target");
    }

    #[cfg(unix)]
    #[test]
    fn symlinked_parent_and_special_leaf_are_typed_skips() {
        use std::ffi::CString;
        use std::os::unix::ffi::OsStrExt;
        use std::os::unix::fs::symlink;
        let temp = tempfile::tempdir().unwrap();
        let digest = u128::from_be_bytes(md5::compute(b"steam/file").0);
        let path = cache_utils::cache_path_for_digest(temp.path(), digest);
        let outside = temp.path().join("outside");
        std::fs::create_dir_all(outside.join(path.parent().unwrap().file_name().unwrap())).unwrap();
        symlink(
            &outside,
            temp.path()
                .join(path.components().nth_back(2).unwrap().as_os_str()),
        )
        .unwrap();
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(
            inspection.outcome,
            InspectionOutcome::Skip("symlink")
        ));

        let fifo_root = temp.path().join("fifo-root");
        let fifo = cache_utils::cache_path_for_digest(&fifo_root, digest);
        std::fs::create_dir_all(fifo.parent().unwrap()).unwrap();
        let fifo_c = CString::new(fifo.as_os_str().as_bytes()).unwrap();
        assert_eq!(unsafe { libc::mkfifo(fifo_c.as_ptr(), 0o600) }, 0);
        let inspection = inspect_path_with_layout(
            &fifo_root,
            &fifo,
            cache_utils::strict_cache_file_digest(&fifo_root, &fifo).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(
            inspection.outcome,
            InspectionOutcome::Skip("special_file")
        ));
    }

    #[cfg(unix)]
    #[test]
    fn sparse_allocation_is_not_itself_corruption() {
        let temp = tempfile::tempdir().unwrap();
        let payload_len = 1024 * 1024u64;
        let headers = format!("HTTP/1.1 200 OK\r\nContent-Length: {payload_len}\r\n\r\n");
        let (path, body_start) = materialize(temp.path(), b"steam/file", headers.as_bytes(), b"");
        let file = OpenOptions::new().write(true).open(&path).unwrap();
        file.set_len(body_start as u64 + payload_len).unwrap();
        let inspection = inspect_path_with_layout(
            temp.path(),
            &path,
            cache_utils::strict_cache_file_digest(temp.path(), &path).unwrap(),
            old_now(),
            Utc::now(),
            Layout::linux_x86_64(),
        )
        .unwrap();
        assert!(matches!(inspection.outcome, InspectionOutcome::Consistent));
        assert!(inspection.sparse);
    }
}
