use anyhow::Result;
use chrono::{DateTime, NaiveDateTime, Utc};
use chrono_tz::Tz;
use serde::Serialize;
use sqlx::PgPool;
use sqlx::Row;
use std::collections::{HashMap, VecDeque};
use std::env;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use lancache_processor::cache_utils;
use lancache_processor::db;
use lancache_processor::log_layout;
use lancache_processor::parser;
use lancache_processor::parser_http_detailed;
use lancache_processor::progress_events;
use lancache_processor::riot_hosts;
use lancache_processor::service_utils;
use lancache_processor::tact_products;
use log_layout::{discover_log_sources, SourceKind};
use parser::LogParser;
use parser_http_detailed::HttpDetailedParser;

// Configuration
const WINDOW_SECONDS: i64 = 2;
// The 15-second activity floor, also the ceiling of the throughput window. Per-flow cadence may
// extend the inferred session to twice this value; measured throughput uses WINDOW_SECONDS,
// widened by a buffered source's delivery cadence (see effective_window_secs).
const MAX_WINDOW_SECONDS: i64 = 15;
// Depth of the per-flow completion-gap ring and each source's transport-delay ring.
const CADENCE_SAMPLES: usize = 4;
const PROTOCOL_VERSION: u32 = 2;
const BROADCAST_INTERVAL_MS: u64 = 500;
const POLL_INTERVAL_MS: u64 = 100;
/// Upper bound on how many bytes a single source may drain in one poll. Each `read_new_entries`
/// call reads at most this many pending bytes (or the size snapshot captured at entry, whichever
/// is smaller) and then RETURNS, so a source appended to as fast as it is read cannot pin the loop
/// on a moving EOF: the 2s cleanup, the broadcast, and every other source are still serviced each
/// iteration, and the in-window `entries` deque stays bounded. The checkpoint resumes exactly where
/// the poll stopped, so nothing is skipped between polls.
const MAX_POLL_BYTES: u64 = 8 * 1024 * 1024;

fn replace_pattern_lookup_cache(cache: &mut HashMap<u128, Option<String>>) {
    *cache = HashMap::new();
}

#[derive(Debug, Clone)]
struct SpeedLogEntry {
    timestamp: NaiveDateTime,
    observed_at: NaiveDateTime,
    client_ip: String,
    service: String,
    depot_id: Option<u32>,
    bytes_sent: i64,
    is_cache_hit: bool,
    request_url: String,
    /// Riot CDN host (access.log `$host`, 4th quoted field), lowercased; only set
    /// for the riot service (None otherwise). Riot bundle URLs have no slug, so the
    /// host subdomain (lol/valorant/bacon) is the only live per-game discriminator.
    cdn_host: Option<String>,
    source_root: PathBuf,
    datasources: Vec<String>,
    transport_delay_secs: f64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct GameSpeedInfo {
    key: String,
    depot_id: u32,
    game_name: Option<String>,
    game_app_id: Option<u32>,
    service: String,
    client_ip: String,
    bytes_per_second: f64,
    total_bytes: i64,
    request_count: usize,
    cache_hit_bytes: i64,
    cache_miss_bytes: i64,
    cache_hit_percent: f64,
    first_seen_utc: String,
    last_seen_utc: String,
    active_until_utc: String,
    sources: Vec<DownloadSource>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DownloadSource {
    datasources: Vec<String>,
    depot_ids: Vec<u32>,
    first_seen_utc: String,
    last_seen_utc: String,
    active_until_utc: String,
    measured_until_utc: String,
    bytes_per_second: f64,
    total_bytes: i64,
    request_count: usize,
    cache_hit_bytes: i64,
    cache_miss_bytes: i64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ClientSpeedInfo {
    client_ip: String,
    bytes_per_second: f64,
    total_bytes: i64,
    active_games: usize,
    cache_hit_bytes: i64,
    cache_miss_bytes: i64,
    active_until_utc: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DownloadSpeedSnapshot {
    version: u32,
    stream_id: String,
    revision: i64,
    timestamp_utc: String,
    is_available: bool,
    total_bytes_per_second: f64,
    game_speeds: Vec<GameSpeedInfo>,
    client_speeds: Vec<ClientSpeedInfo>,
    window_seconds: i64,
    entries_in_window: usize,
    has_active_downloads: bool,
}

/// One discovered log source reduced to the single file the tracker tails: its CURRENT
/// unrotated, uncompressed member. `kind` carries the service attribution: `Monolithic`
/// lines self-identify with a `[service]` tag, `Service(name)` lines take the stem's service
/// hint for the http-detailed format. The fallback series is dropped at discovery and never
/// tracked.
#[derive(Debug, Clone)]
struct TrackedSource {
    path: PathBuf,
    kind: SourceKind,
    root: PathBuf,
    datasources: Vec<String>,
}

/// Resolve every datasource directory to the concrete files worth tailing. A directory may
/// expose 1..N sources (a monolithic `access.log`, and/or per-service bare-metal `*-access.log`
/// files); for each we tail only the CURRENT unrotated, uncompressed member. A source whose sole
/// surviving member is a rotated/compressed archive has no live file and is skipped, and the
/// fallback series is never ingested. Discovery reuses `log_layout::discover_log_sources`, so the
/// tracker carries no filename or layout grammar of its own.
fn discover_tracked_sources(configured: &[(String, PathBuf)]) -> Vec<TrackedSource> {
    let mut roots: Vec<(PathBuf, Vec<String>)> = Vec::new();
    for (name, dir) in configured {
        let root = match std::fs::canonicalize(dir) {
            Ok(root) => root,
            Err(e) => {
                eprintln!("Failed to resolve log source {}: {}", dir.display(), e);
                continue;
            }
        };
        if let Some((_, aliases)) = roots.iter_mut().find(|(known, _)| *known == root) {
            if !aliases.iter().any(|alias| alias.eq_ignore_ascii_case(name)) {
                aliases.push(name.clone());
            }
        } else {
            roots.push((root, vec![name.clone()]));
        }
    }
    roots.sort_by(|a, b| a.0.cmp(&b.0));

    let mut tracked = Vec::new();
    for (root, mut datasources) in roots {
        datasources.sort_by_key(|name| name.to_ascii_lowercase());
        let set = match discover_log_sources(&root) {
            Ok(set) => set,
            Err(e) => {
                eprintln!(
                    "Failed to discover log sources in {}: {}",
                    root.display(),
                    e
                );
                continue;
            }
        };
        for source in set.sources {
            if matches!(source.kind, SourceKind::Fallback) {
                continue;
            }
            let Some(current) = source
                .files
                .iter()
                .find(|file| file.rotation_number.is_none() && !file.is_compressed)
            else {
                continue;
            };
            tracked.push(TrackedSource {
                path: current.path.clone(),
                kind: source.kind.clone(),
                root: root.clone(),
                datasources: datasources.clone(),
            });
        }
    }
    tracked.sort_by(|a, b| a.path.cmp(&b.path));
    tracked
}

/// Parse one tailed line into a `SpeedLogEntry`, or None. Canonical order (identical to the
/// record processor and the content scan): the cachelog parser runs first everywhere so an
/// explicit `[service]` tag always wins; otherwise a per-service source parses with its stem's
/// service hint. The manager's own probe traffic is synthetic and never live activity, and only
/// requests that actually transferred bytes count toward live speed. Maps the canonical
/// `LogEntry` onto the tracker's `SpeedLogEntry` (riot `cdn_host` comes straight from the parsed
/// entry, whose parsers own that grammar).
fn parse_speed_entry(
    cachelog: &LogParser,
    detailed: &HttpDetailedParser,
    line: &str,
    kind: &SourceKind,
    source: &TrackedSource,
    observed_at: NaiveDateTime,
) -> Option<SpeedLogEntry> {
    if service_utils::is_manager_probe(line) {
        return None;
    }

    let entry = if let Some(entry) = cachelog.parse_line(line) {
        entry
    } else if let SourceKind::Service(service) = kind {
        detailed.parse_line(line, service)?
    } else {
        // Monolithic http-detailed content is hint-less; there is no service to attribute.
        return None;
    };

    if service_utils::should_skip_url(&entry.url) {
        return None;
    }
    // Live speed measures bytes actually served; `-`/zero/negative rows move no data.
    if entry.bytes_served <= 0 {
        return None;
    }

    Some(SpeedLogEntry {
        timestamp: entry.timestamp,
        observed_at,
        client_ip: entry.client_ip,
        service: entry.service,
        // Depot 0 names no real depot, and the server rejects a row that carries it.
        depot_id: entry.depot_id.filter(|id| *id > 0),
        bytes_sent: entry.bytes_served,
        is_cache_hit: entry.cache_status.eq_ignore_ascii_case("HIT"),
        request_url: entry.url,
        cdn_host: entry.cdn_host,
        source_root: source.root.clone(),
        datasources: source.datasources.clone(),
        transport_delay_secs: 0.0,
    })
}

/// Per-source tail state, kept distinct so an oversized record cannot stall a source. `checkpoint`
/// is the committed RECORD boundary: the byte offset of the start of the current incomplete record;
/// everything before it has been parsed and is never re-read. `scan` is where the next poll reads
/// from and it advances across polls even when a capped slice holds NO newline, so a record longer
/// than `MAX_POLL_BYTES` cannot pin the reader on the same prefix forever. `discarding` marks that
/// the current record already exceeded the poll budget with no terminator: bytes are dropped until
/// the next newline (a real access-log line is never that long) before normal parsing resumes.
#[derive(Debug, Clone, Copy)]
struct SourceState {
    checkpoint: u64,
    scan: u64,
    discarding: bool,
    // Recent transport-delay samples. Each entry-producing poll records only its own timestamp span
    // and observation delay, so silence between polls cannot be mistaken for buffering.
    cadence_ring: [f64; CADENCE_SAMPLES],
    cadence_head: usize,
}

impl SourceState {
    /// Anchor both cursors at `position` with no in-progress discard and an empty cadence history.
    /// This is both the seed-to-EOF state (first successful observation) and the rotation reset;
    /// the rotation path carries the learned cadence over afterwards, since a rotation resets file
    /// offsets but not the log's delivery cadence.
    fn anchored(position: u64) -> Self {
        Self {
            checkpoint: position,
            scan: position,
            discarding: false,
            cadence_ring: [0.0; CADENCE_SAMPLES],
            cadence_head: 0,
        }
    }

    /// Record transport evidence from one batch and return the source's recent maximum. The batch
    /// span captures buffered records, while observation delay captures a buffered single record.
    /// Both are bounded by the activity floor. Time between separate deliveries is not evidence.
    fn record_delivery(
        &mut self,
        min_ts: NaiveDateTime,
        max_ts: NaiveDateTime,
        observed_at: NaiveDateTime,
    ) -> f64 {
        let intra_batch_span = (max_ts - min_ts).num_milliseconds() as f64 / 1000.0;
        let observation_delay = (observed_at - max_ts).num_milliseconds() as f64 / 1000.0;
        let sample = intra_batch_span
            .max(observation_delay)
            .clamp(0.0, MAX_WINDOW_SECONDS as f64);
        self.cadence_ring[self.cadence_head] = sample;
        self.cadence_head = (self.cadence_head + 1) % CADENCE_SAMPLES;
        self.measured_cadence()
    }

    /// The source's largest recent transport-delay sample in seconds.
    fn measured_cadence(&self) -> f64 {
        self.cadence_ring.iter().copied().fold(0.0, f64::max)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
enum ContentKey {
    SteamApp(u32),
    SteamDepot(u32),
    Named(String),
    Service,
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct FlowKey {
    source_root: PathBuf,
    client_ip: String,
    service: String,
    content: ContentKey,
}

#[derive(Debug, Clone)]
struct MeasuredEntry {
    timestamp: NaiveDateTime,
    depot_id: Option<u32>,
    bytes_sent: i64,
    is_cache_hit: bool,
}

#[derive(Debug, Clone)]
struct FlowState {
    datasources: Vec<String>,
    depot_ids: Vec<u32>,
    game_name: Option<String>,
    game_app_id: Option<u32>,
    first_seen: NaiveDateTime,
    last_seen: NaiveDateTime,
    active_until: NaiveDateTime,
    measured_until: NaiveDateTime,
    cadence_ring: [f64; CADENCE_SAMPLES],
    cadence_head: usize,
    cadence_count: usize,
    measurements: VecDeque<MeasuredEntry>,
}

impl FlowState {
    fn new(
        entry: &SpeedLogEntry,
        game_name: Option<String>,
        game_app_id: Option<u32>,
        window_secs: i64,
    ) -> Option<Self> {
        let active_until = entry.timestamp + chrono::Duration::seconds(MAX_WINDOW_SECONDS);
        if active_until <= entry.observed_at {
            return None;
        }

        let measured_until = entry.timestamp + chrono::Duration::seconds(window_secs);
        let mut state = Self {
            datasources: entry.datasources.clone(),
            depot_ids: entry.depot_id.into_iter().collect(),
            game_name,
            game_app_id,
            first_seen: entry.timestamp,
            last_seen: entry.timestamp,
            active_until,
            measured_until,
            cadence_ring: [0.0; CADENCE_SAMPLES],
            cadence_head: 0,
            cadence_count: 0,
            measurements: VecDeque::new(),
        };
        state.record_measurement(entry, window_secs);
        Some(state)
    }

    fn record(
        &mut self,
        entry: &SpeedLogEntry,
        game_name: Option<String>,
        game_app_id: Option<u32>,
        window_secs: i64,
    ) {
        merge_aliases(&mut self.datasources, &entry.datasources);
        if let Some(depot_id) = entry.depot_id {
            if !self.depot_ids.contains(&depot_id) {
                self.depot_ids.push(depot_id);
                self.depot_ids.sort_unstable();
            }
        }
        if game_name.is_some() {
            self.game_name = game_name;
        }
        if game_app_id.is_some() {
            self.game_app_id = game_app_id;
        }

        if entry.timestamp > self.last_seen {
            if entry.timestamp >= self.active_until {
                self.first_seen = entry.timestamp;
                self.cadence_ring = [0.0; CADENCE_SAMPLES];
                self.cadence_head = 0;
                self.cadence_count = 0;
                self.measurements.clear();
            } else {
                let gap = (entry.timestamp - self.last_seen).num_milliseconds() as f64 / 1000.0;
                if gap > 0.0 {
                    self.cadence_ring[self.cadence_head] = gap;
                    self.cadence_head = (self.cadence_head + 1) % CADENCE_SAMPLES;
                    self.cadence_count = (self.cadence_count + 1).min(CADENCE_SAMPLES);
                }
            }

            self.last_seen = entry.timestamp;
            let horizon = self.horizon_secs(entry.transport_delay_secs);
            let candidate = entry.timestamp + chrono::Duration::seconds(horizon);
            if candidate > entry.observed_at {
                self.active_until = if self.active_until > entry.observed_at {
                    self.active_until.max(candidate)
                } else {
                    candidate
                };
            }
        }

        self.record_measurement(entry, window_secs);
    }

    fn horizon_secs(&self, transport_delay_secs: f64) -> i64 {
        if self.cadence_count < CADENCE_SAMPLES {
            return MAX_WINDOW_SECONDS;
        }
        let gap = self.cadence_ring.iter().copied().fold(0.0, f64::max);
        (2.0 * gap + transport_delay_secs + WINDOW_SECONDS as f64)
            .ceil()
            .clamp(MAX_WINDOW_SECONDS as f64, (2 * MAX_WINDOW_SECONDS) as f64) as i64
    }

    fn record_measurement(&mut self, entry: &SpeedLogEntry, window_secs: i64) {
        let measured_until = entry.timestamp + chrono::Duration::seconds(window_secs);
        self.measured_until = self.measured_until.max(measured_until);
        if measured_until > entry.observed_at {
            self.measurements.push_back(MeasuredEntry {
                timestamp: entry.timestamp,
                depot_id: entry.depot_id,
                bytes_sent: entry.bytes_sent,
                is_cache_hit: entry.is_cache_hit,
            });
        }
    }

    fn merge(&mut self, other: FlowState) {
        let use_other_cadence = other.last_seen > self.last_seen
            || (other.last_seen == self.last_seen && other.cadence_count > self.cadence_count);
        merge_aliases(&mut self.datasources, &other.datasources);
        for depot_id in other.depot_ids {
            if !self.depot_ids.contains(&depot_id) {
                self.depot_ids.push(depot_id);
            }
        }
        self.depot_ids.sort_unstable();
        if self.game_name.is_none() {
            self.game_name = other.game_name;
        }
        if self.game_app_id.is_none() {
            self.game_app_id = other.game_app_id;
        }
        self.first_seen = self.first_seen.min(other.first_seen);
        self.last_seen = self.last_seen.max(other.last_seen);
        self.active_until = self.active_until.max(other.active_until);
        self.measured_until = self.measured_until.max(other.measured_until);
        if use_other_cadence {
            self.cadence_ring = other.cadence_ring;
            self.cadence_head = other.cadence_head;
            self.cadence_count = other.cadence_count;
        }
        self.measurements.extend(other.measurements);
        let mut measurements: Vec<_> = self.measurements.drain(..).collect();
        measurements.sort_by_key(|entry| entry.timestamp);
        self.measurements.extend(measurements);
    }
}

fn merge_aliases(existing: &mut Vec<String>, incoming: &[String]) {
    for name in incoming {
        if !existing
            .iter()
            .any(|known| known.eq_ignore_ascii_case(name))
        {
            existing.push(name.clone());
        }
    }
    existing.sort_by_key(|name| name.to_ascii_lowercase());
}

fn format_utc(value: NaiveDateTime) -> String {
    DateTime::<Utc>::from_naive_utc_and_offset(value, Utc)
        .format("%Y-%m-%dT%H:%M:%S%.3fZ")
        .to_string()
}

struct GameTotals {
    game_name: Option<String>,
    game_app_id: Option<u32>,
    first_seen: NaiveDateTime,
    last_seen: NaiveDateTime,
    active_until: NaiveDateTime,
    sources: Vec<DownloadSource>,
    depot_ids: Vec<u32>,
    depot_bytes: HashMap<u32, i64>,
    total_bytes: i64,
    request_count: usize,
    cache_hit_bytes: i64,
}

struct SpeedTracker {
    pool: PgPool,
    sources: Vec<TrackedSource>,
    cachelog: LogParser,
    detailed: HttpDetailedParser,
    entries: VecDeque<SpeedLogEntry>,
    flows: HashMap<FlowKey, FlowState>,
    revision: i64,
    depot_cache: HashMap<u32, (Option<String>, Option<u32>)>, // depot_id -> (game_name, game_app_id)
    last_depot_refresh: Option<Instant>,
    // Committed record checkpoint + bounded scan cursor per source (see SourceState). An absent
    // key is the explicit UNINITIALIZED state, kept distinct from an anchored byte-0 checkpoint.
    file_positions: HashMap<PathBuf, SourceState>,
    // The CDN caches key on the URL's md5 digest instead of the URL string: this daemon lives
    // for months, and a heavy chunked download pushes tens of thousands of unique URLs into
    // these maps between the 60s pattern-reload clears - ~180 bytes each as Strings, ~46 as
    // digests. None (no match) is cached too.
    epic_cdn_cache: HashMap<u128, Option<String>>, // md5(url) -> game_name (None = no match)
    epic_patterns: Vec<(String, String)>,          // (ChunkBaseUrl trimmed, GameName)
    last_epic_pattern_load: Option<Instant>,
    xbox_cdn_cache: HashMap<u128, Option<String>>, // md5(url) -> game_name (None = no match)
    xbox_patterns: Vec<(String, String)>,          // (UrlFragment, Title), longest-first
    last_xbox_pattern_load: Option<Instant>,
}

impl SpeedTracker {
    fn new(pool: PgPool, sources: Vec<TrackedSource>) -> Self {
        let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
        let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);

        Self {
            pool,
            sources,
            cachelog: LogParser::new(local_tz),
            detailed: HttpDetailedParser::new(local_tz),
            entries: VecDeque::new(),
            flows: HashMap::new(),
            revision: 0,
            depot_cache: HashMap::new(),
            last_depot_refresh: None,
            file_positions: HashMap::new(),
            epic_cdn_cache: HashMap::new(),
            epic_patterns: Vec::new(),
            last_epic_pattern_load: None,
            xbox_cdn_cache: HashMap::new(),
            xbox_patterns: Vec::new(),
            last_xbox_pattern_load: None,
        }
    }

    async fn run(&mut self) -> Result<()> {
        eprintln!(
            "SpeedTracker started - monitoring {} log file(s)",
            self.sources.len()
        );

        // File positions seed lazily on each source's first readable poll (see read_new_entries):
        // every source anchors at its CURRENT EOF, so pre-existing history is never replayed, and a
        // source that is absent or unreadable at startup seeds correctly the first time it becomes
        // readable instead of defaulting to byte 0 and replaying its whole file.

        let mut last_broadcast = Instant::now();

        loop {
            // Read new entries from every tracked source's current file
            for source in self.sources.clone() {
                if let Err(e) = self.read_new_entries(&source) {
                    eprintln!("Error reading {}: {}", source.path.display(), e);
                }
            }

            // Broadcast if interval passed
            if last_broadcast.elapsed() >= Duration::from_millis(BROADCAST_INTERVAL_MS) {
                let snapshot = self.calculate_snapshot(Utc::now()).await;

                // Output JSON to stdout (C# will read this) via the shared emission
                // primitive in progress_events — same compact serialize + println +
                // flush guarantee, no envelope wrapping (see emit_json_line docs).
                progress_events::emit_json_line(&snapshot);

                last_broadcast = Instant::now();
            }

            tokio::time::sleep(Duration::from_millis(POLL_INTERVAL_MS)).await;
        }
    }

    fn read_new_entries(&mut self, source: &TrackedSource) -> Result<()> {
        let log_path = &source.path;

        // Size snapshot at entry. A source we cannot stat (absent or unreadable) stays in the
        // explicit UNINITIALIZED state: we neither seed nor read it, so it is never conflated with
        // a byte-0 checkpoint. It seeds on the first poll it is actually readable (below), so a
        // file missing at startup that later appears does not replay its whole history.
        let current_size = match std::fs::metadata(log_path) {
            Ok(metadata) => metadata.len(),
            Err(_) => return Ok(()),
        };

        // First successful observation: seed both cursors to the CURRENT EOF and read nothing this
        // poll. An absent map key is the uninitialized state, kept distinct from a real byte-0
        // checkpoint so a genuinely empty file is still read from its start.
        let state = match self.file_positions.get(log_path) {
            Some(&state) => state,
            None => {
                eprintln!(
                    "Initialized {} at position {}",
                    log_path.display(),
                    current_size
                );
                self.file_positions
                    .insert(log_path.clone(), SourceState::anchored(current_size));
                return Ok(());
            }
        };

        // Rotation: a file smaller than our committed checkpoint is a fresh log; read it from the
        // start and drop any in-progress over-limit discard state.
        let mut state = if current_size < state.checkpoint {
            eprintln!("Log file rotated: {}", log_path.display());
            // Reset only the file offsets. Rotation swaps the file, not nginx's flush
            // configuration, so the source's delivery cadence is unchanged; carrying the learned
            // ring over stops the first download after logrotate from re-learning (and re-dipping).
            let mut rotated = SourceState::anchored(0);
            rotated.cadence_ring = state.cadence_ring;
            rotated.cadence_head = state.cadence_head;
            rotated
        } else {
            state
        };

        // The scan cursor must never lead past EOF. If the file was truncated to somewhere inside
        // the current incomplete record (below the scan cursor but not below the committed
        // checkpoint), the scanned span no longer exists; fall back to the checkpoint and re-scan
        // from the last committed record boundary.
        if state.scan > current_size {
            state.scan = state.checkpoint;
            state.discarding = false;
        }

        // No unscanned bytes this poll: either no new data at all, or we already scanned every
        // available byte of an in-progress over-limit record and are waiting for its newline.
        if current_size <= state.scan {
            self.file_positions.insert(log_path.clone(), state);
            return Ok(());
        }

        // Bound this poll to the unscanned pending bytes AND the fixed budget, then return to the
        // outer loop so cleanup, broadcast, and every other source get serviced. The scan cursor
        // advances every poll (below), so the partial buffer never exceeds MAX_POLL_BYTES and an
        // oversized record cannot pin the reader on the same prefix.
        let pending = current_size - state.scan;
        let budget = pending.min(MAX_POLL_BYTES) as usize;

        let mut file = File::open(log_path)?;
        file.seek(SeekFrom::Start(state.scan))?;

        let mut buffer = vec![0u8; budget];
        let mut filled = 0usize;
        while filled < buffer.len() {
            let read = file.read(&mut buffer[filled..])?;
            if read == 0 {
                break;
            }
            filled += read;
        }
        buffer.truncate(filled);

        if filled == 0 {
            self.file_positions.insert(log_path.clone(), state);
            return Ok(());
        }

        // Entry count before parsing this poll's buffer, so the timestamp span of exactly the
        // records appended below can be measured as this source's latest delivery.
        let before = self.entries.len();
        let observed_at = Utc::now().naive_utc();

        match buffer.iter().rposition(|&b| b == b'\n') {
            Some(index) => {
                let complete_len = index + 1;
                if state.discarding {
                    // We were discarding a record that overran the budget with no newline. The
                    // FIRST newline in this buffer terminates that oversized record's tail; drop
                    // everything up to and including it, then parse any complete records that
                    // follow within this same buffer. A single access-log line is never 8 MiB, so
                    // discarding the oversized fragment is correct and the source recovers here.
                    let first_newline = buffer[..complete_len]
                        .iter()
                        .position(|&b| b == b'\n')
                        .unwrap_or(index);
                    let resume = first_newline + 1;
                    self.parse_records(&buffer[resume..complete_len], source, observed_at);
                    state.discarding = false;
                } else {
                    // Commit only PAST complete, newline-terminated records. Everything after the
                    // last newline is an incomplete final record read again next poll.
                    self.parse_records(&buffer[..complete_len], source, observed_at);
                }
                // Advance the committed checkpoint past the last complete record and resync the
                // scan cursor to it: no byte is skipped, and the last complete record is not re-read.
                let record_end = state.scan + complete_len as u64;
                state.checkpoint = record_end;
                state.scan = record_end;
            }
            None => {
                if state.discarding {
                    // Still discarding an over-limit record whose newline has not arrived: drop the
                    // scanned bytes and advance past them, retaining nothing.
                    state.scan += filled as u64;
                } else if filled as u64 >= MAX_POLL_BYTES {
                    // The current record is at least MAX_POLL_BYTES long with no terminator. Enter
                    // discard-until-newline mode and advance the scan cursor past the scanned bytes
                    // so they are never re-read and the partial buffer never exceeds the cap. The
                    // committed checkpoint stays at the record start until the newline is found, so
                    // nothing before it is ever replayed.
                    state.discarding = true;
                    state.scan += filled as u64;
                }
                // Otherwise this is an ordinary incomplete final record still being written: leave
                // the scan cursor at the checkpoint so the whole record is re-read once its
                // terminating newline arrives, never split across two polls and never lost.
            }
        }

        // A poll that appended live completions records source transport evidence from this batch
        // only. Silence between batches is client idleness, not buffering evidence.
        if self.entries.len() > before {
            let mut min_ts: Option<NaiveDateTime> = None;
            let mut max_ts: Option<NaiveDateTime> = None;
            for entry in self
                .entries
                .iter()
                .skip(before)
                .filter(|entry| entry.timestamp <= observed_at)
            {
                let ts = entry.timestamp;
                min_ts = Some(min_ts.map_or(ts, |current| current.min(ts)));
                max_ts = Some(max_ts.map_or(ts, |current| current.max(ts)));
            }
            if let (Some(min_ts), Some(max_ts)) = (min_ts, max_ts) {
                let delay = state.record_delivery(min_ts, max_ts, observed_at);
                for entry in self.entries.iter_mut().skip(before) {
                    if entry.timestamp <= observed_at {
                        entry.transport_delay_secs = delay;
                    }
                }
            }
        }

        self.file_positions.insert(log_path.clone(), state);
        Ok(())
    }

    /// Parse a newline-delimited slice of complete records into `entries`. Decode lossily and trim,
    /// exactly like canonical ingestion (`String::from_utf8_lossy(raw).trim()`): a record carrying
    /// invalid UTF-8 becomes a classified-invalid line that parse_speed_entry rejects and we advance
    /// past, instead of an error that would drop the batch's checkpoint and replay earlier valid
    /// records on every later poll.
    fn parse_records(&mut self, bytes: &[u8], source: &TrackedSource, observed_at: NaiveDateTime) {
        for record in bytes.split(|&b| b == b'\n') {
            if record.is_empty() {
                continue;
            }
            let decoded = String::from_utf8_lossy(record);
            let trimmed = decoded.trim();
            if trimmed.is_empty() {
                continue;
            }
            if let Some(entry) = parse_speed_entry(
                &self.cachelog,
                &self.detailed,
                trimmed,
                &source.kind,
                source,
                observed_at,
            ) {
                self.entries.push_back(entry);
            }
        }
    }

    /// The rolling window sized to the slowest source's measured delivery cadence. A source that
    /// delivers within the base window contributes nothing (gate `c > WINDOW_SECONDS`), so
    /// unbuffered/monolithic delivery keeps the exact 2s behavior; a source whose flush cadence
    /// exceeds the base window widens the window just enough that it never empties between bursts.
    /// The margin added to the measured cadence is WINDOW_SECONDS itself, so worst-cycle coverage
    /// (`w - c`) never falls below the speed divisor's floor, and the result is capped at the
    /// backstop horizon.
    fn effective_window_secs(&self) -> i64 {
        let mut eff = WINDOW_SECONDS as f64;
        for state in self.file_positions.values() {
            let cadence = state.measured_cadence();
            if cadence > WINDOW_SECONDS as f64 {
                eff = eff.max(cadence + WINDOW_SECONDS as f64);
            }
        }
        (eff.ceil() as i64).clamp(WINDOW_SECONDS, MAX_WINDOW_SECONDS)
    }

    fn clean_old_entries(&mut self, now: NaiveDateTime) {
        let window = chrono::Duration::seconds(self.effective_window_secs());
        for state in self.flows.values_mut() {
            state
                .measurements
                .retain(|entry| entry.timestamp + window > now);
        }
        self.flows.retain(|_, state| state.active_until > now);
    }

    fn move_depot_flow(
        &mut self,
        source_root: &Path,
        client_ip: &str,
        service: &str,
        depot_id: u32,
        game_app_id: u32,
        game_name: Option<String>,
    ) {
        let old_key = FlowKey {
            source_root: source_root.to_path_buf(),
            client_ip: client_ip.to_string(),
            service: service.to_string(),
            content: ContentKey::SteamDepot(depot_id),
        };
        let Some(mut state) = self.flows.remove(&old_key) else {
            return;
        };

        if game_name.is_some() {
            state.game_name = game_name;
        }
        state.game_app_id = Some(game_app_id);
        let new_key = FlowKey {
            source_root: source_root.to_path_buf(),
            client_ip: client_ip.to_string(),
            service: service.to_string(),
            content: ContentKey::SteamApp(game_app_id),
        };
        if let Some(existing) = self.flows.get_mut(&new_key) {
            existing.merge(state);
        } else {
            self.flows.insert(new_key, state);
        }
    }

    async fn refresh_depot_flows(&mut self) {
        if self
            .last_depot_refresh
            .is_some_and(|last| last.elapsed() < Duration::from_secs(60))
        {
            return;
        }
        self.last_depot_refresh = Some(Instant::now());

        let unresolved: Vec<(FlowKey, u32)> = self
            .flows
            .keys()
            .filter_map(|key| match key.content {
                ContentKey::SteamDepot(depot_id) => Some((key.clone(), depot_id)),
                _ => None,
            })
            .collect();

        for (key, depot_id) in unresolved {
            let (game_name, game_app_id) = self.lookup_depot(depot_id).await;
            if let Some(game_app_id) = game_app_id {
                self.move_depot_flow(
                    &key.source_root,
                    &key.client_ip,
                    &key.service,
                    depot_id,
                    game_app_id,
                    game_name,
                );
            }
        }
    }

    fn existing_named_game(
        &self,
        source_root: &PathBuf,
        client_ip: &str,
        service: &str,
    ) -> Option<String> {
        self.flows
            .iter()
            .filter(|(key, _)| {
                key.source_root == *source_root
                    && key.client_ip == client_ip
                    && key.service == service
                    && matches!(key.content, ContentKey::Named(_))
            })
            .max_by_key(|(_, state)| state.last_seen)
            .and_then(|(_, state)| state.game_name.clone())
    }

    fn record_flow(
        &mut self,
        key: FlowKey,
        entries: Vec<SpeedLogEntry>,
        game_name: Option<String>,
        game_app_id: Option<u32>,
    ) {
        let window_secs = self.effective_window_secs();
        if let ContentKey::SteamApp(app_id) = key.content {
            let mut depots: Vec<u32> = entries.iter().filter_map(|entry| entry.depot_id).collect();
            depots.sort_unstable();
            depots.dedup();
            for depot_id in depots {
                self.move_depot_flow(
                    &key.source_root,
                    &key.client_ip,
                    &key.service,
                    depot_id,
                    app_id,
                    game_name.clone(),
                );
            }
        }

        for entry in entries {
            if let Some(state) = self.flows.get_mut(&key) {
                state.record(&entry, game_name.clone(), game_app_id, window_secs);
            } else if let Some(state) =
                FlowState::new(&entry, game_name.clone(), game_app_id, window_secs)
            {
                self.flows.insert(key.clone(), state);
            }
        }
    }

    async fn accept_entries(&mut self, now: NaiveDateTime) {
        let pending = std::mem::take(&mut self.entries);
        let mut depot_entries = Vec::new();
        let mut service_groups: HashMap<(PathBuf, String, String), Vec<SpeedLogEntry>> =
            HashMap::new();

        for mut entry in pending {
            // A log host whose clock runs a little ahead still reports live downloads at this
            // tracker's time; a completion further ahead than the activity floor is dropped.
            if entry.timestamp > now + chrono::Duration::seconds(MAX_WINDOW_SECONDS) {
                continue;
            }
            entry.timestamp = entry.timestamp.min(now);
            entry.service = entry.service.trim().to_ascii_lowercase();
            if entry.depot_id.is_some() {
                depot_entries.push(entry);
            } else {
                service_groups
                    .entry((
                        entry.source_root.clone(),
                        entry.client_ip.clone(),
                        entry.service.clone(),
                    ))
                    .or_default()
                    .push(entry);
            }
        }

        let mut depot_ids: Vec<u32> = depot_entries
            .iter()
            .filter_map(|entry| entry.depot_id)
            .collect();
        depot_ids.sort_unstable();
        depot_ids.dedup();
        let mut depot_resolutions = HashMap::new();
        for depot_id in depot_ids {
            depot_resolutions.insert(depot_id, self.lookup_depot(depot_id).await);
        }

        for entry in depot_entries {
            let Some(depot_id) = entry.depot_id else {
                continue;
            };
            let (game_name, game_app_id) = depot_resolutions
                .get(&depot_id)
                .cloned()
                .unwrap_or((None, None));
            let content = game_app_id
                .map(ContentKey::SteamApp)
                .unwrap_or(ContentKey::SteamDepot(depot_id));
            let key = FlowKey {
                source_root: entry.source_root.clone(),
                client_ip: entry.client_ip.clone(),
                service: entry.service.clone(),
                content,
            };
            self.record_flow(key, vec![entry], game_name, game_app_id);
        }

        for ((source_root, client_ip, service), entries) in service_groups {
            if service.contains("epic") {
                let mut groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
                for entry in entries {
                    let game_name = self
                        .lookup_epic_game(&entry.request_url)
                        .await
                        .unwrap_or_else(|| get_service_display_name(&service));
                    groups.entry(game_name).or_default().push(entry);
                }
                for (game_name, entries) in groups {
                    let content = content_key(&service, &game_name);
                    self.record_flow(
                        FlowKey {
                            source_root: source_root.clone(),
                            client_ip: client_ip.clone(),
                            service: service.clone(),
                            content,
                        },
                        entries,
                        Some(game_name),
                        None,
                    );
                }
            } else if service.contains("blizzard") || service.contains("battle") {
                let mut game_counts: HashMap<String, usize> = HashMap::new();
                let mut shared_label = None;
                for entry in &entries {
                    if let Some(segment) = tact_products::extract_tact_product(&entry.request_url) {
                        match tact_products::resolve_tact_segment(&segment) {
                            tact_products::TactResolution::Game(name) => {
                                *game_counts.entry(name).or_insert(0) += 1;
                            }
                            tact_products::TactResolution::Shared(label) => {
                                shared_label = Some(label);
                            }
                            tact_products::TactResolution::Unknown => {}
                        }
                    }
                }
                let group_name = game_counts
                    .into_iter()
                    .max_by(|a, b| a.1.cmp(&b.1).then_with(|| a.0.cmp(&b.0)))
                    .map(|(name, _)| name)
                    .or_else(|| self.existing_named_game(&source_root, &client_ip, &service))
                    .or(shared_label)
                    .unwrap_or_else(|| get_service_display_name(&service));
                let content = content_key(&service, &group_name);
                self.record_flow(
                    FlowKey {
                        source_root,
                        client_ip,
                        service,
                        content,
                    },
                    entries,
                    Some(group_name),
                    None,
                );
            } else if service.contains("riot") {
                let mut groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
                for entry in entries {
                    let game_name = entry
                        .cdn_host
                        .as_deref()
                        .and_then(riot_hosts::resolve_riot_host)
                        .map(str::to_string)
                        .unwrap_or_else(|| get_service_display_name(&service));
                    groups.entry(game_name).or_default().push(entry);
                }
                for (game_name, entries) in groups {
                    let content = content_key(&service, &game_name);
                    self.record_flow(
                        FlowKey {
                            source_root: source_root.clone(),
                            client_ip: client_ip.clone(),
                            service: service.clone(),
                            content,
                        },
                        entries,
                        Some(game_name),
                        None,
                    );
                }
            } else if service.contains("wsus") || service.contains("xboxlive") {
                let mut groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
                for entry in entries {
                    let game_name = self
                        .lookup_xbox_game(&entry.request_url)
                        .await
                        .unwrap_or_else(|| get_service_display_name(&service));
                    groups.entry(game_name).or_default().push(entry);
                }
                for (game_name, entries) in groups {
                    let content = content_key(&service, &game_name);
                    self.record_flow(
                        FlowKey {
                            source_root: source_root.clone(),
                            client_ip: client_ip.clone(),
                            service: service.clone(),
                            content,
                        },
                        entries,
                        Some(game_name),
                        None,
                    );
                }
            } else {
                let display_name = get_service_display_name(&service);
                self.record_flow(
                    FlowKey {
                        source_root,
                        client_ip,
                        service,
                        content: ContentKey::Service,
                    },
                    entries,
                    Some(display_name),
                    None,
                );
            }
        }
    }

    async fn calculate_snapshot(&mut self, now: DateTime<Utc>) -> DownloadSpeedSnapshot {
        let now_naive = now.naive_utc();
        self.clean_old_entries(now_naive);
        self.refresh_depot_flows().await;
        self.accept_entries(now_naive).await;
        self.clean_old_entries(now_naive);
        self.revision = self.revision.saturating_add(1);
        let window_secs = self.effective_window_secs();
        let window_start = now_naive - chrono::Duration::seconds(window_secs);

        let mut groups: HashMap<(String, String, ContentKey), GameTotals> = HashMap::new();
        for (key, state) in &self.flows {
            let total_bytes: i64 = state
                .measurements
                .iter()
                .map(|entry| entry.bytes_sent)
                .sum();
            // Speed divides by the part of the window this flow's data actually covers. Buffered
            // delivery lags the window's end by up to one flush, so dividing by the whole window
            // would make a steady download sawtooth every flush. The WINDOW_SECONDS floor keeps
            // unbuffered delivery at exactly bytes / 2 and bounds a lone aging entry's speed.
            let anchor = state
                .measurements
                .iter()
                .map(|entry| entry.timestamp)
                .max()
                .map_or(now_naive, |newest| newest.min(now_naive));
            let coverage_secs = (anchor - window_start).num_milliseconds() as f64 / 1000.0;
            let speed_divisor = coverage_secs.clamp(WINDOW_SECONDS as f64, window_secs as f64);
            let cache_hit_bytes: i64 = state
                .measurements
                .iter()
                .filter(|entry| entry.is_cache_hit)
                .map(|entry| entry.bytes_sent)
                .sum();
            let request_count = state.measurements.len();
            let cache_miss_bytes = total_bytes - cache_hit_bytes;
            let source = DownloadSource {
                datasources: state.datasources.clone(),
                depot_ids: state.depot_ids.clone(),
                first_seen_utc: format_utc(state.first_seen),
                last_seen_utc: format_utc(state.last_seen),
                active_until_utc: format_utc(state.active_until),
                measured_until_utc: format_utc(state.measured_until),
                bytes_per_second: total_bytes as f64 / speed_divisor,
                total_bytes,
                request_count,
                cache_hit_bytes,
                cache_miss_bytes,
            };

            let group = groups
                .entry((
                    key.client_ip.clone(),
                    key.service.clone(),
                    key.content.clone(),
                ))
                .or_insert_with(|| GameTotals {
                    game_name: state.game_name.clone(),
                    game_app_id: state.game_app_id,
                    first_seen: state.first_seen,
                    last_seen: state.last_seen,
                    active_until: state.active_until,
                    sources: Vec::new(),
                    depot_ids: Vec::new(),
                    depot_bytes: HashMap::new(),
                    total_bytes: 0,
                    request_count: 0,
                    cache_hit_bytes: 0,
                });
            if state.game_name.is_some()
                && (state.last_seen > group.last_seen
                    || (state.last_seen == group.last_seen
                        && state.game_name.as_deref() < group.game_name.as_deref()))
            {
                group.game_name = state.game_name.clone();
            }
            if state.game_app_id.is_some() {
                group.game_app_id = state.game_app_id;
            }
            group.first_seen = group.first_seen.min(state.first_seen);
            group.last_seen = group.last_seen.max(state.last_seen);
            group.active_until = group.active_until.max(state.active_until);
            for depot_id in &state.depot_ids {
                if !group.depot_ids.contains(depot_id) {
                    group.depot_ids.push(*depot_id);
                }
            }
            for measurement in &state.measurements {
                if let Some(depot_id) = measurement.depot_id {
                    *group.depot_bytes.entry(depot_id).or_insert(0) += measurement.bytes_sent;
                }
            }
            group.total_bytes += total_bytes;
            group.request_count += request_count;
            group.cache_hit_bytes += cache_hit_bytes;
            group.sources.push(source);
        }

        let mut game_rows: Vec<(GameSpeedInfo, NaiveDateTime)> = groups
            .into_iter()
            .map(|((client_ip, service, content), mut group)| {
                group.depot_ids.sort_unstable();
                group.sources.sort_by(|a, b| {
                    a.datasources
                        .cmp(&b.datasources)
                        .then_with(|| a.depot_ids.cmp(&b.depot_ids))
                        .then_with(|| a.first_seen_utc.cmp(&b.first_seen_utc))
                        .then_with(|| a.last_seen_utc.cmp(&b.last_seen_utc))
                });
                let depot_id = group
                    .depot_bytes
                    .into_iter()
                    .max_by(|a, b| a.1.cmp(&b.1).then_with(|| b.0.cmp(&a.0)))
                    .map(|(depot_id, _)| depot_id)
                    .or_else(|| group.depot_ids.first().copied())
                    .unwrap_or(0);
                let cache_miss_bytes = group.total_bytes - group.cache_hit_bytes;
                let cache_hit_percent = if group.total_bytes > 0 {
                    group.cache_hit_bytes as f64 / group.total_bytes as f64 * 100.0
                } else {
                    0.0
                };
                let key = traffic_key(&service, &client_ip, &content);
                (
                    GameSpeedInfo {
                        key,
                        depot_id,
                        game_name: group.game_name,
                        game_app_id: group.game_app_id,
                        service,
                        client_ip,
                        // Each source row has its own divisor, so the game's rate is their sum.
                        bytes_per_second: group
                            .sources
                            .iter()
                            .map(|source| source.bytes_per_second)
                            .sum(),
                        total_bytes: group.total_bytes,
                        request_count: group.request_count,
                        cache_hit_bytes: group.cache_hit_bytes,
                        cache_miss_bytes,
                        cache_hit_percent,
                        first_seen_utc: format_utc(group.first_seen),
                        last_seen_utc: format_utc(group.last_seen),
                        active_until_utc: format_utc(group.active_until),
                        sources: group.sources,
                    },
                    group.active_until,
                )
            })
            .collect();

        game_rows.sort_by(|a, b| {
            a.0.service
                .cmp(&b.0.service)
                .then_with(|| a.0.game_name.cmp(&b.0.game_name))
                .then_with(|| a.0.client_ip.cmp(&b.0.client_ip))
                .then_with(|| a.0.game_app_id.cmp(&b.0.game_app_id))
                .then_with(|| a.0.depot_id.cmp(&b.0.depot_id))
        });

        let mut clients: HashMap<String, (f64, i64, usize, i64, i64, NaiveDateTime)> =
            HashMap::new();
        for (game, active_until) in &game_rows {
            let client =
                clients
                    .entry(game.client_ip.clone())
                    .or_insert((0.0, 0, 0, 0, 0, *active_until));
            client.0 += game.bytes_per_second;
            client.1 += game.total_bytes;
            client.2 += 1;
            client.3 += game.cache_hit_bytes;
            client.4 += game.cache_miss_bytes;
            client.5 = client.5.max(*active_until);
        }
        let mut client_speeds: Vec<ClientSpeedInfo> = clients
            .into_iter()
            .map(
                |(
                    client_ip,
                    (
                        bytes_per_second,
                        total_bytes,
                        active_games,
                        cache_hit_bytes,
                        cache_miss_bytes,
                        active_until,
                    ),
                )| ClientSpeedInfo {
                    client_ip,
                    bytes_per_second,
                    total_bytes,
                    active_games,
                    cache_hit_bytes,
                    cache_miss_bytes,
                    active_until_utc: format_utc(active_until),
                },
            )
            .collect();
        client_speeds.sort_by(|a, b| a.client_ip.cmp(&b.client_ip));

        let total_bytes_per_second = game_rows
            .iter()
            .map(|(game, _)| game.bytes_per_second)
            .sum();
        let entries_in_window = game_rows.iter().map(|(game, _)| game.request_count).sum();
        let game_speeds: Vec<GameSpeedInfo> = game_rows.into_iter().map(|(game, _)| game).collect();
        let has_active_downloads = !game_speeds.is_empty();

        DownloadSpeedSnapshot {
            version: PROTOCOL_VERSION,
            stream_id: String::new(),
            revision: self.revision,
            timestamp_utc: now.format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string(),
            is_available: true,
            total_bytes_per_second,
            game_speeds,
            client_speeds,
            window_seconds: WINDOW_SECONDS,
            entries_in_window,
            has_active_downloads,
        }
    }
    async fn load_epic_patterns(&mut self) {
        // Only reload every 60 seconds
        if let Some(last) = self.last_epic_pattern_load {
            if last.elapsed() < Duration::from_secs(60) {
                return;
            }
        }

        // Load patterns with game names, longest ChunkBaseUrl first
        let result = sqlx::query(
            "SELECT p.\"ChunkBaseUrl\", COALESCE(m.\"Name\", p.\"Name\") as \"GameName\" \
             FROM \"EpicCdnPatterns\" p \
             LEFT JOIN \"EpicGameMappings\" m ON p.\"AppId\" = m.\"AppId\" \
             ORDER BY LENGTH(p.\"ChunkBaseUrl\") DESC",
        )
        .fetch_all(&self.pool)
        .await;

        match result {
            Ok(rows) => {
                self.epic_patterns = rows
                    .iter()
                    .filter_map(|row| {
                        let chunk_base_url: Option<String> = row.get("ChunkBaseUrl");
                        let game_name: Option<String> = row.get("GameName");
                        match (chunk_base_url, game_name) {
                            (Some(url), Some(name)) => {
                                Some((url.trim_end_matches('/').to_string(), name))
                            }
                            _ => None,
                        }
                    })
                    .collect();

                self.last_epic_pattern_load = Some(Instant::now());
                replace_pattern_lookup_cache(&mut self.epic_cdn_cache);
            }
            Err(_) => {
                // Silently ignore errors (table may not exist yet)
            }
        }
    }

    async fn lookup_epic_game(&mut self, url: &str) -> Option<String> {
        // Check cache first
        let key = cache_utils::calculate_md5_digest(url);
        if let Some(cached) = self.epic_cdn_cache.get(&key) {
            return cached.clone();
        }

        // Reload patterns if needed
        self.load_epic_patterns().await;

        // Match URL against patterns (longest first for most specific match)
        let result = self
            .epic_patterns
            .iter()
            .find(|(chunk_base, _)| url.contains(chunk_base.as_str()))
            .map(|(_, name)| name.clone());

        // Cache the result (including None for no match)
        self.epic_cdn_cache.insert(key, result.clone());

        result
    }

    async fn load_xbox_patterns(&mut self) {
        // Only reload every 60 seconds (mirrors load_epic_patterns).
        if let Some(last) = self.last_xbox_pattern_load {
            if last.elapsed() < Duration::from_secs(60) {
                return;
            }
        }

        // Load Xbox fragment -> title, longest UrlFragment first so the most specific
        // fragment wins. Prefer the (richer) mapping title, falling back to the pattern title.
        let result = sqlx::query(
            "SELECT p.\"UrlFragment\", COALESCE(m.\"Title\", p.\"Title\") AS \"Title\" \
             FROM \"XboxCdnPatterns\" p \
             LEFT JOIN \"XboxGameMappings\" m ON p.\"ProductId\" = m.\"ProductId\" \
             ORDER BY LENGTH(p.\"UrlFragment\") DESC",
        )
        .fetch_all(&self.pool)
        .await;

        match result {
            Ok(rows) => {
                self.xbox_patterns = rows
                    .iter()
                    .filter_map(|row| {
                        let fragment: Option<String> = row.get("UrlFragment");
                        let title: Option<String> = row.get("Title");
                        match (fragment, title) {
                            // Keep ONLY well-formed /filestreamingservice/files/<GUID> fragments,
                            // using the SAME shared shape guard as log_processor (the primary
                            // canonicalizer) and the C# resolver (XboxMappingService.IsValidFragment).
                            // A malformed / short / non-GUID fragment would `contains()`-match generic
                            // wsus URLs and relabel Windows Update traffic as a game.
                            (Some(frag), Some(name))
                                if cache_utils::is_valid_xbox_fragment(&frag) =>
                            {
                                Some((frag, name))
                            }
                            _ => None,
                        }
                    })
                    .collect();

                self.last_xbox_pattern_load = Some(Instant::now());
                replace_pattern_lookup_cache(&mut self.xbox_cdn_cache);
            }
            Err(_) => {
                // Silently ignore errors (tables may not exist yet).
            }
        }
    }

    async fn lookup_xbox_game(&mut self, url: &str) -> Option<String> {
        // Check cache first.
        let key = cache_utils::calculate_md5_digest(url);
        if let Some(cached) = self.xbox_cdn_cache.get(&key) {
            return cached.clone();
        }

        // Reload patterns if needed.
        self.load_xbox_patterns().await;

        // Match URL against fragments (longest first for the most specific match).
        // Case-insensitive: the Xbox GUID hex casing in the stored fragment can differ from the
        // access-log URL casing, and the C# resolver compares with OrdinalIgnoreCase, so a
        // case-sensitive match here would inconsistently miss real Xbox content. ASCII lowercasing
        // is exact for these paths; the per-URL cache means each unique URL is lowercased once.
        let url_lower = url.to_ascii_lowercase();
        let result = self
            .xbox_patterns
            .iter()
            .find(|(fragment, _)| url_lower.contains(&fragment.to_ascii_lowercase()))
            .map(|(_, name)| name.clone());

        // Cache the result (including None for no match).
        self.xbox_cdn_cache.insert(key, result.clone());

        result
    }

    async fn lookup_depot(&mut self, depot_id: u32) -> (Option<String>, Option<u32>) {
        // Check cache first - only use cached value if we have a real name
        if let Some(cached) = self.depot_cache.get(&depot_id) {
            if cached.0.is_some() {
                return cached.clone();
            }
            // Fall through to re-query for missing names
        }

        // Lookup in database
        let result = self.lookup_depot_from_db(depot_id).await;

        // Only cache successful lookups (where we found a name)
        if result.0.is_some() {
            self.depot_cache.insert(depot_id, result.clone());
        }

        result
    }

    async fn lookup_depot_from_db(&self, depot_id: u32) -> (Option<String>, Option<u32>) {
        let result = sqlx::query(
            "SELECT \"AppId\", \"AppName\" FROM \"SteamDepotMappings\" WHERE \"DepotId\" = $1 AND \"IsOwner\" = true LIMIT 1"
        )
        .bind(depot_id as i64)
        .fetch_optional(&self.pool)
        .await;

        match result {
            Ok(Some(row)) => {
                let app_id: i64 = row.get("AppId");
                let app_name: Option<String> = row.get("AppName");
                (app_name, Some(app_id as u32))
            }
            _ => (None, None),
        }
    }
}

fn content_key(service: &str, game_name: &str) -> ContentKey {
    if game_name
        .trim()
        .eq_ignore_ascii_case(&get_service_display_name(service))
    {
        ContentKey::Service
    } else {
        ContentKey::Named(game_name.trim().to_ascii_lowercase())
    }
}

fn traffic_key(service: &str, client_ip: &str, content: &ContentKey) -> String {
    let identity = match content {
        ContentKey::SteamApp(app_id) => format!("app:{app_id}"),
        ContentKey::SteamDepot(depot_id) => format!("depot:{depot_id}"),
        ContentKey::Named(game_name) => format!("name:{}", game_name.trim().to_ascii_lowercase()),
        ContentKey::Service => "service".to_string(),
    };
    format!(
        "{}|{}|{}",
        service.trim().to_ascii_lowercase(),
        client_ip.trim(),
        identity
    )
}

fn parse_source_args(args: &[String]) -> Result<Vec<(String, PathBuf)>> {
    if args.is_empty() {
        anyhow::bail!("missing required log source argument(s)");
    }

    if args.first().is_some_and(|arg| arg == "--source") {
        if !args.len().is_multiple_of(3) {
            anyhow::bail!("each --source requires a datasource name and log directory");
        }
        let mut configured = Vec::new();
        for group in args.chunks_exact(3) {
            if group[0] != "--source" {
                anyhow::bail!(
                    "named --source arguments cannot be mixed with positional directories"
                );
            }
            let name = group[1].trim();
            let path = group[2].trim();
            if name.is_empty() || path.is_empty() {
                anyhow::bail!(
                    "--source requires nonempty datasource name and log directory values"
                );
            }
            configured.push((name.to_string(), PathBuf::from(path)));
        }
        Ok(configured)
    } else {
        if args.iter().any(|arg| arg == "--source") {
            anyhow::bail!("named --source arguments cannot be mixed with positional directories");
        }
        if args.iter().any(|path| path.trim().is_empty()) {
            anyhow::bail!("positional log directories must be nonempty");
        }
        Ok(args
            .iter()
            .enumerate()
            .map(|(index, path)| (format!("source-{}", index + 1), PathBuf::from(path)))
            .collect())
    }
}

/// DB-free headline aggregates over the rolling window: total bytes/second, the in-window entry
/// count, and whether any download is active. `calculate_snapshot` derives its
/// `total_bytes_per_second`, `entries_in_window`, and `has_active_downloads` fields from exactly
/// this (no depot/game database lookup), so it is the real seam the streaming tail path is verified
/// through without a live pool.
#[cfg(test)]
fn headline_aggregates(
    entries: &VecDeque<SpeedLogEntry>,
    window_start: NaiveDateTime,
    speed_divisor: f64,
) -> (f64, usize, bool) {
    let mut total_bytes: i64 = 0;
    let mut count: usize = 0;
    for entry in entries.iter().filter(|e| e.timestamp >= window_start) {
        total_bytes += entry.bytes_sent;
        count += 1;
    }
    (total_bytes as f64 / speed_divisor, count, count > 0)
}

/// Build a GameSpeedInfo from a group of log entries
#[cfg(test)]
fn build_game_speed_info(
    entries: Vec<SpeedLogEntry>,
    depot_id: u32,
    client_ip: String,
    service: String,
    game_name: Option<String>,
    game_app_id: Option<u32>,
    speed_divisor: f64,
) -> GameSpeedInfo {
    let total_bytes: i64 = entries.iter().map(|e| e.bytes_sent).sum();
    let cache_hit_bytes: i64 = entries
        .iter()
        .filter(|e| e.is_cache_hit)
        .map(|e| e.bytes_sent)
        .sum();
    let cache_miss_bytes = total_bytes - cache_hit_bytes;
    let cache_hit_percent = if total_bytes > 0 {
        (cache_hit_bytes as f64 / total_bytes as f64) * 100.0
    } else {
        0.0
    };

    let first_seen = entries
        .iter()
        .map(|entry| entry.timestamp)
        .min()
        .unwrap_or_else(|| Utc::now().naive_utc());
    let last_seen = entries
        .iter()
        .map(|entry| entry.timestamp)
        .max()
        .unwrap_or(first_seen);
    let content = game_app_id
        .map(ContentKey::SteamApp)
        .or_else(|| (depot_id > 0).then_some(ContentKey::SteamDepot(depot_id)))
        .unwrap_or_else(|| {
            game_name
                .as_deref()
                .map(|name| content_key(&service, name))
                .unwrap_or(ContentKey::Service)
        });

    GameSpeedInfo {
        key: traffic_key(&service, &client_ip, &content),
        depot_id,
        game_name,
        game_app_id,
        service,
        client_ip,
        bytes_per_second: total_bytes as f64 / speed_divisor,
        total_bytes,
        request_count: entries.len(),
        cache_hit_bytes,
        cache_miss_bytes,
        cache_hit_percent,
        first_seen_utc: format_utc(first_seen),
        last_seen_utc: format_utc(last_seen),
        active_until_utc: format_utc(last_seen + chrono::Duration::seconds(MAX_WINDOW_SECONDS)),
        sources: Vec::new(),
    }
}

/// Collapse Steam depot buckets that resolve to the same app into ONE `GameSpeedInfo`
/// per (app, client), summing throughput and request counts. One Steam game spans many
/// depots, so without this a chunk/depot rollover inside the rolling window briefly
/// yields two rows for the same game. Buckets whose depot did NOT resolve to an app keep
/// their own per-depot identity, so two unknown depots never merge. This mirrors the
/// `resolved_groups` collapse the non-depot services use. `resolve` maps a depot_id to
/// its looked-up `(game_name, game_app_id)` (see `lookup_depot`); a partially resolved
/// depot (AppId known, AppName not yet mapped) still merges by app_id.
#[cfg(test)]
fn collapse_depot_groups<F>(
    depot_groups: HashMap<(u32, String), Vec<SpeedLogEntry>>,
    resolve: F,
    speed_divisor: f64,
) -> Vec<GameSpeedInfo>
where
    F: Fn(u32) -> (Option<String>, Option<u32>),
{
    // Key: (is_resolved, app_id-or-depot_id, client_ip). The is_resolved flag keeps a
    // resolved app from colliding with an unresolved depot that shares its numeric id.
    let mut collapsed: HashMap<(bool, u32, String), Vec<SpeedLogEntry>> = HashMap::new();
    for ((depot_id, client_ip), entries) in depot_groups {
        let (_, game_app_id) = resolve(depot_id);
        let key = match game_app_id {
            Some(app_id) => (true, app_id, client_ip),
            None => (false, depot_id, client_ip),
        };
        collapsed.entry(key).or_default().extend(entries);
    }

    collapsed
        .into_iter()
        .map(|((_, _, client_ip), entries)| {
            let rep_depot_id = pick_representative_depot(&entries);
            let (mut game_name, mut game_app_id) = resolve(rep_depot_id);
            // A merged group can mix fully- and partially-resolved depots (a mapping
            // row may carry an AppId with no AppName yet); borrow the missing name/app
            // from a sibling depot so the merged row never loses what a split row had.
            if game_name.is_none() || game_app_id.is_none() {
                let mut sibling_depots: Vec<u32> =
                    entries.iter().filter_map(|e| e.depot_id).collect();
                sibling_depots.sort_unstable();
                sibling_depots.dedup();
                for depot_id in sibling_depots {
                    if game_name.is_some() && game_app_id.is_some() {
                        break;
                    }
                    let (name, app_id) = resolve(depot_id);
                    if game_name.is_none() {
                        game_name = name;
                    }
                    if game_app_id.is_none() {
                        game_app_id = app_id;
                    }
                }
            }
            let service = entries
                .first()
                .map(|e| e.service.clone())
                .unwrap_or_default();
            build_game_speed_info(
                entries,
                rep_depot_id,
                client_ip,
                service,
                game_name,
                game_app_id,
                speed_divisor,
            )
        })
        .collect()
}

/// Pick the representative depot for a collapsed group: the depot that contributed the
/// most bytes in the window (deterministic tie-break on the smaller depot_id), kept for
/// the DTO/UI which still carries a single `depot_id`. Returns 0 if no entry carries a
/// depot (matches the placeholder used for the non-depot path).
#[cfg(test)]
fn pick_representative_depot(entries: &[SpeedLogEntry]) -> u32 {
    let mut bytes_by_depot: HashMap<u32, i64> = HashMap::new();
    for entry in entries {
        if let Some(depot_id) = entry.depot_id {
            *bytes_by_depot.entry(depot_id).or_insert(0) += entry.bytes_sent;
        }
    }
    bytes_by_depot
        .into_iter()
        .max_by(|a, b| a.1.cmp(&b.1).then_with(|| b.0.cmp(&a.0)))
        .map(|(depot_id, _)| depot_id)
        .unwrap_or(0)
}

/// Map normalized service names to human-readable display names
fn get_service_display_name(service: &str) -> String {
    match service {
        "epic" | "epicgames" => "Epic Games".to_string(),
        "origin" | "ea" => "EA / Origin".to_string(),
        "blizzard" | "battlenet" | "battle.net" => "Blizzard / Battle.net".to_string(),
        "riot" | "riotgames" => "Riot Games".to_string(),
        "xbox" | "xboxlive" => "Xbox Live".to_string(),
        "wsus" | "windows" => "Windows Update".to_string(),
        "uplay" | "ubisoft" => "Ubisoft".to_string(),
        "arenanet" => "ArenaNet".to_string(),
        "sony" | "playstation" => "PlayStation".to_string(),
        "nintendo" => "Nintendo".to_string(),
        "rockstar" => "Rockstar Games".to_string(),
        "wargaming" => "Wargaming".to_string(),
        "steam" => "Steam".to_string(),
        "localhost" => "Localhost".to_string(),
        "ip-address" => "Direct IP".to_string(),
        "unknown" => "Unknown Service".to_string(),
        _ => service.to_string(),
    }
}

#[tokio::main]
async fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 2 {
        eprintln!("Usage: {} <log_dir> [log_dir2] ...", args[0]);
        eprintln!(
            "       {} --source <datasource> <log_dir> [--source <datasource> <log_dir>] ...",
            args[0]
        );
        eprintln!("  log_dir: Path to a datasource log directory. Every log source inside it");
        eprintln!("           (a monolithic access.log and/or per-service bare-metal *-access.log");
        eprintln!("           files) is discovered and tailed; access.log is not assumed.");
        eprintln!();
        eprintln!("Database connection is configured via DATABASE_URL environment variable.");
        eprintln!(
            "Outputs JSON speed snapshots to stdout every {}ms",
            BROADCAST_INTERVAL_MS
        );
        eprintln!(
            "Uses a rolling window sized to each log's delivery cadence (min {}s)",
            WINDOW_SECONDS
        );
        // No ProgressReporter/envelope here by design (this bin is a continuous snapshot
        // stream, not a discrete lifecycle operation - see emit_json_line docs). Returning
        // Err (instead of process::exit(1)) still surfaces the fatal reason: anyhow's
        // default main Termination prints "Error: {:#}" to stderr and exits 1.
        anyhow::bail!("missing required <log_dir> argument(s)");
    }

    // Discover the concrete current files to tail across every datasource directory. A directory
    // may hold a monolithic access.log and/or per-service bare-metal logs; the Rust side owns
    // discovery so C# only has to pass the datasource directory.
    let configured = parse_source_args(&args[1..])?;
    let sources = discover_tracked_sources(&configured);

    if sources.is_empty() {
        eprintln!("No log sources discovered in the provided director(ies); nothing to track.");
    }

    let pool = db::create_pool().await?;
    let mut tracker = SpeedTracker::new(pool, sources);
    tracker.run().await
}

#[cfg(test)]
mod tests {
    use super::{
        build_game_speed_info, collapse_depot_groups, discover_tracked_sources,
        headline_aggregates, parse_source_args, replace_pattern_lookup_cache, ContentKey, FlowKey,
        FlowState, SourceKind, SourceState, SpeedLogEntry, SpeedTracker, TrackedSource,
        CADENCE_SAMPLES, MAX_POLL_BYTES, MAX_WINDOW_SECONDS, PROTOCOL_VERSION, WINDOW_SECONDS,
    };
    use chrono::{Duration, NaiveDateTime, Utc};
    use sqlx::postgres::PgPoolOptions;
    use std::collections::{HashMap, VecDeque};
    use std::io::Write;
    use std::path::PathBuf;

    // A minimal in-window Steam entry; the collapse logic only reads client_ip, depot_id,
    // bytes_sent and service, so the rest use inert defaults.
    fn steam_entry(client_ip: &str, depot_id: u32, bytes: i64) -> SpeedLogEntry {
        SpeedLogEntry {
            timestamp: Utc::now().naive_utc(),
            observed_at: Utc::now().naive_utc(),
            client_ip: client_ip.to_string(),
            service: "steam".to_string(),
            depot_id: Some(depot_id),
            bytes_sent: bytes,
            is_cache_hit: false,
            request_url: String::new(),
            cdn_host: None,
            source_root: PathBuf::from("test-root"),
            datasources: vec!["test".to_string()],
            transport_delay_secs: 0.0,
        }
    }

    fn flow_entry(
        timestamp: NaiveDateTime,
        observed_at: NaiveDateTime,
        client_ip: &str,
        service: &str,
        root: &str,
        datasource: &str,
        bytes_sent: i64,
    ) -> SpeedLogEntry {
        SpeedLogEntry {
            timestamp,
            observed_at,
            client_ip: client_ip.to_string(),
            service: service.to_string(),
            depot_id: None,
            bytes_sent,
            is_cache_hit: true,
            request_url: "/content/file.bin".to_string(),
            cdn_host: None,
            source_root: PathBuf::from(root),
            datasources: vec![datasource.to_string()],
            transport_delay_secs: 0.0,
        }
    }

    fn service_key(root: &str, client_ip: &str, service: &str) -> FlowKey {
        FlowKey {
            source_root: PathBuf::from(root),
            client_ip: client_ip.to_string(),
            service: service.to_string(),
            content: ContentKey::Service,
        }
    }

    // Depots 1001 and 1002 both belong to the same Steam app (730) — the many-depots-per-game
    // reality that makes the pre-fix per-depot grouping duplicate a game across rows.
    fn cs2_resolver(depot_id: u32) -> (Option<String>, Option<u32>) {
        match depot_id {
            1001 | 1002 => (Some("Counter-Strike 2".to_string()), Some(730)),
            _ => (None, None),
        }
    }

    fn none_resolver(_depot_id: u32) -> (Option<String>, Option<u32>) {
        (None, None)
    }

    // Reproduces the pre-fix bug AND proves the fix in one place: mapping each (depot, client)
    // bucket straight to a row (the original :415-423) yields TWO rows for one game spanning two
    // depots; collapse_depot_groups yields ONE with summed bytes. (A whole-fix git-stash reverts
    // collapse_depot_groups out of existence and fails to compile, so the pre/post comparison here
    // is the cleaner red/green evidence.)
    #[test]
    fn collapses_same_app_depots_into_one_row_with_summed_bytes() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        groups.insert(
            (1001, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1001, 1000)],
        );
        groups.insert(
            (1002, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1002, 2000)],
        );

        // PRE-FIX behavior (original :415-423): one row per bucket => the duplicate bug.
        let pre_fix: Vec<_> = groups
            .clone()
            .into_iter()
            .map(|((depot_id, client_ip), entries)| {
                let (name, app) = cs2_resolver(depot_id);
                build_game_speed_info(
                    entries,
                    depot_id,
                    client_ip,
                    "steam".to_string(),
                    name,
                    app,
                    WINDOW_SECONDS as f64,
                )
            })
            .collect();
        assert_eq!(
            pre_fix.len(),
            2,
            "pre-fix: two depots of one game render as two rows"
        );

        // POST-FIX behavior: collapsed to a single row with combined throughput.
        let rows = collapse_depot_groups(groups, cs2_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 1, "post-fix: one game => one row");
        let row = &rows[0];
        assert_eq!(row.total_bytes, 3000, "bytes are summed across both depots");
        assert_eq!(row.bytes_per_second, 3000.0 / WINDOW_SECONDS as f64);
        assert_eq!(row.request_count, 2);
        assert_eq!(row.game_app_id, Some(730));
        assert_eq!(row.client_ip, "10.0.0.1");
        // Representative depot = the one contributing the most bytes (1002 here).
        assert_eq!(row.depot_id, 1002);
    }

    #[test]
    fn unresolved_depots_stay_separate() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        groups.insert(
            (5001, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 5001, 500)],
        );
        groups.insert(
            (5002, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 5002, 500)],
        );

        let rows = collapse_depot_groups(groups, none_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 2, "two unknown depots must not merge");
        assert!(rows.iter().all(|r| r.game_app_id.is_none()));
    }

    #[test]
    fn same_app_different_clients_stay_separate() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        groups.insert(
            (1001, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1001, 1000)],
        );
        groups.insert(
            (1001, "10.0.0.2".to_string()),
            vec![steam_entry("10.0.0.2", 1001, 1000)],
        );

        let rows = collapse_depot_groups(groups, cs2_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 2, "per-client separation is intentional");
        assert!(rows.iter().all(|r| r.total_bytes == 1000));
    }

    // A depot whose mapping row has an AppId but no AppName resolves to (None, Some(app));
    // it must still merge with a named sibling depot of the same app, and the merged row
    // must keep the sibling's name even when the unnamed depot is the byte-heavy
    // representative.
    #[test]
    fn partially_resolved_depot_merges_with_named_sibling() {
        fn partial_resolver(depot_id: u32) -> (Option<String>, Option<u32>) {
            match depot_id {
                1001 => (Some("Counter-Strike 2".to_string()), Some(730)),
                1002 => (None, Some(730)),
                _ => (None, None),
            }
        }
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        groups.insert(
            (1001, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1001, 1000)],
        );
        groups.insert(
            (1002, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1002, 2000)],
        );

        let rows = collapse_depot_groups(groups, partial_resolver, WINDOW_SECONDS as f64);
        assert_eq!(
            rows.len(),
            1,
            "partial resolution must not split one game across rows"
        );
        let row = &rows[0];
        assert_eq!(row.total_bytes, 3000);
        assert_eq!(row.game_app_id, Some(730));
        assert_eq!(row.depot_id, 1002, "the unnamed depot has the most bytes");
        assert_eq!(
            row.game_name.as_deref(),
            Some("Counter-Strike 2"),
            "name is borrowed from the named sibling depot"
        );
    }

    #[test]
    fn representative_depot_tie_breaks_to_smaller_depot() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        groups.insert(
            (1001, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1001, 500)],
        );
        groups.insert(
            (1002, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1002, 500)],
        );

        let rows = collapse_depot_groups(groups, cs2_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 1);
        assert_eq!(
            rows[0].depot_id, 1001,
            "equal bytes tie-breaks to the smaller depot id"
        );
    }

    #[test]
    fn representative_depot_is_the_highest_byte_depot() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        // 1001 contributes far more bytes than 1002, so it must be the representative.
        groups.insert(
            (1001, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1001, 9000)],
        );
        groups.insert(
            (1002, "10.0.0.1".to_string()),
            vec![steam_entry("10.0.0.1", 1002, 100)],
        );

        let rows = collapse_depot_groups(groups, cs2_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].depot_id, 1001);
    }

    #[test]
    fn pattern_cache_replacement_releases_retained_capacity() {
        let mut cache = HashMap::with_capacity(256);
        cache.insert(1, Some("Epic Game".to_string()));
        cache.insert(2, None);
        let previous_capacity = cache.capacity();

        replace_pattern_lookup_cache(&mut cache);

        assert!(cache.is_empty());
        assert_eq!(cache.capacity(), 0);
        assert!(previous_capacity > cache.capacity());
    }

    // Current-time nginx access-log timestamp. The `+0000` offset forces UTC regardless of the
    // process TZ, so a freshly written record lands inside the 2s window the headline reads.
    fn now_ts() -> String {
        Utc::now().format("%d/%b/%Y:%H:%M:%S +0000").to_string()
    }

    // A monolithic cachelog line: the service comes from the `[service]` tag, not the filename.
    fn mono_line(service: &str, client_ip: &str, url: &str, bytes: i64, cache: &str) -> String {
        format!(
            "[{service}] {client_ip} / - - - [{ts}] \"GET {url} HTTP/1.1\" 200 {bytes} \"-\" \"BITS\" \"{cache}\" \"download.windowsupdate.com\" \"-\"\n",
            ts = now_ts()
        )
    }

    // A bare-metal http-detailed line (no `[service]` tag). Field order matches
    // access-log-formats/http/detailed.conf; the transferred body-bytes field carries `bytes`.
    fn detailed_steam_line(client_ip: &str, depot: u32, bytes: i64, cache: &str) -> String {
        format!(
            "[{ts}] {client_ip} GET \"/depot/{depot}/chunk/abc\" - HTTP/1.1 200 \"-\" 512 2016 {bytes} 0.005 {bytes} {cache} h.example 200 0.004 \"Steam\"\n",
            ts = now_ts()
        )
    }

    // A bare-metal http-detailed line with a caller-supplied log timestamp, so a buffered flush can
    // be injected as a burst of past-second records with no sleeping. The `+0000` offset forces UTC
    // regardless of the process TZ, matching now_ts().
    fn detailed_steam_line_at(
        client_ip: &str,
        depot: u32,
        bytes: i64,
        cache: &str,
        ts: NaiveDateTime,
    ) -> String {
        let ts = ts.format("%d/%b/%Y:%H:%M:%S +0000").to_string();
        format!(
            "[{ts}] {client_ip} GET \"/depot/{depot}/chunk/abc\" - HTTP/1.1 200 \"-\" 512 2016 {bytes} 0.005 {bytes} {cache} h.example 200 0.004 \"Steam\"\n"
        )
    }

    fn append_bytes(path: &std::path::Path, bytes: &[u8]) {
        let mut file = std::fs::OpenOptions::new().append(true).open(path).unwrap();
        file.write_all(bytes).unwrap();
    }

    // A SpeedTracker over `sources` with a lazily-created pool that never actually connects: the
    // streaming tail path and `headline_aggregates` do no database work, so no server is needed.
    fn lazy_tracker(sources: Vec<TrackedSource>) -> SpeedTracker {
        let pool = PgPoolOptions::new()
            .connect_lazy("postgres://postgres:password@127.0.0.1/lancache_test")
            .expect("lazy pool builds without connecting");
        SpeedTracker::new(pool, sources)
    }

    // The live tracker must stream BOTH layouts a datasource can present: the monolithic cachelog
    // `access.log` and the per-service bare-metal `steam-access.log` (http-detailed). Discovery
    // selects each source's current file; each source seeds to EOF (pre-existing history is not
    // replayed) and is then driven through the REAL `read_new_entries` loop; and the DB-free
    // snapshot headline (the seam `calculate_snapshot` uses for bytes/active-downloads) reflects
    // both. A source whose only surviving member is a compressed rotation contributes nothing.
    #[tokio::test]
    async fn tracks_both_monolithic_and_bare_metal_current_files() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let access = dir.join("access.log");
        let steam = dir.join("steam-access.log");

        // Pre-existing history written BEFORE the tracker observes the files. These carry current
        // timestamps, so if seeding-to-EOF failed to skip them they WOULD count; the test proves
        // they do not.
        std::fs::write(
            &access,
            mono_line("wsus", "10.0.0.9", "/content/old.bin", 999, "HIT"),
        )
        .unwrap();
        std::fs::write(&steam, detailed_steam_line("10.0.0.8", 654321, 999, "MISS")).unwrap();

        // A per-service source whose only surviving member is a compressed rotation: it has no
        // current file to tail, so it must never be tracked.
        std::fs::write(
            dir.join("blizzard-access.log.1.gz"),
            b"ignored compressed bytes",
        )
        .unwrap();

        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(
            tracked.len(),
            2,
            "only the monolithic and bare-metal current files are tracked"
        );
        assert!(
            !tracked
                .iter()
                .any(|t| t.path.file_name().and_then(|n| n.to_str()) == Some("blizzard-access.log")),
            "a compressed rotation-only source has no current file to track"
        );
        // The monolithic file is hint-less; the per-service file carries the steam stem hint.
        assert!(tracked.iter().any(|t| t.kind == SourceKind::Monolithic));
        assert!(tracked
            .iter()
            .any(|t| t.kind == SourceKind::Service("steam".to_string())));

        let mut tracker = lazy_tracker(tracked.clone());

        // First poll seeds every source to its current EOF and reads nothing.
        for source in &tracked {
            tracker.read_new_entries(source).unwrap();
        }
        assert!(
            tracker.entries.is_empty(),
            "seeding to EOF must not replay pre-existing history"
        );

        // Append one live record to EACH format, then drive the real streaming reader for both.
        append_bytes(
            &access,
            mono_line("wsus", "10.0.0.1", "/content/file.bin", 1000, "HIT").as_bytes(),
        );
        append_bytes(
            &steam,
            detailed_steam_line("10.0.0.2", 654321, 2000, "MISS").as_bytes(),
        );
        for source in &tracked {
            tracker.read_new_entries(source).unwrap();
        }

        assert_eq!(
            tracker.entries.len(),
            2,
            "both formats produce one live entry each"
        );
        let mono = tracker
            .entries
            .iter()
            .find(|e| e.service == "wsus")
            .expect("monolithic wsus entry");
        let steam_entry = tracker
            .entries
            .iter()
            .find(|e| e.service == "steam")
            .expect("bare-metal steam entry");
        assert_eq!(mono.bytes_sent, 1000);
        assert!(mono.is_cache_hit, "the monolithic line was a cache HIT");
        assert_eq!(steam_entry.bytes_sent, 2000);
        assert_eq!(
            steam_entry.depot_id,
            Some(654321),
            "steam depot parsed from the http-detailed URL"
        );
        assert!(
            !steam_entry.is_cache_hit,
            "the bare-metal line was a cache MISS"
        );

        // The REAL snapshot headline (the DB-free seam calculate_snapshot derives its
        // total_bytes_per_second / entries_in_window / has_active_downloads from) reflects BOTH
        // formats: throughput sums across sources and the window is active.
        let window_start = Utc::now().naive_utc() - chrono::Duration::seconds(WINDOW_SECONDS);
        let (total_bytes_per_second, entries_in_window, has_active_downloads) =
            headline_aggregates(&tracker.entries, window_start, WINDOW_SECONDS as f64);
        assert_eq!(
            entries_in_window, 2,
            "both live records are inside the window"
        );
        assert_eq!(
            total_bytes_per_second,
            3000.0 / WINDOW_SECONDS as f64,
            "throughput sums both formats"
        );
        assert!(
            has_active_downloads,
            "a non-empty window means active downloads"
        );
    }

    // A record whose bytes arrive in two writes across two polls must be parsed exactly once, with
    // nothing lost: the first (newline-less) fragment is held at the checkpoint, and only the
    // reassembled, newline-terminated record is parsed on the later poll.
    #[tokio::test]
    async fn split_record_across_polls_is_parsed_once() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let access = dir.join("access.log");
        std::fs::write(&access, b"").unwrap();

        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());

        tracker.read_new_entries(&tracked[0]).unwrap();
        assert!(tracker.entries.is_empty());

        // Write one record in TWO parts. The first part is mid-line and carries no newline.
        let line = mono_line("wsus", "10.9.9.9", "/content/split.bin", 4096, "MISS");
        let bytes = line.as_bytes();
        let split_at = bytes.len() / 2;

        append_bytes(&access, &bytes[..split_at]);
        tracker.read_new_entries(&tracked[0]).unwrap();
        assert!(
            tracker.entries.is_empty(),
            "an incomplete record (no newline yet) is neither parsed nor checkpointed past"
        );

        append_bytes(&access, &bytes[split_at..]);
        tracker.read_new_entries(&tracked[0]).unwrap();
        assert_eq!(
            tracker.entries.len(),
            1,
            "the reassembled record is parsed exactly once"
        );
        assert_eq!(tracker.entries[0].client_ip, "10.9.9.9");
        assert_eq!(tracker.entries[0].bytes_sent, 4096);
    }

    // A complete record containing invalid UTF-8 must be skipped (lossy decode, like canonical
    // ingestion) with the checkpoint advancing past it. Earlier valid records in the SAME batch are
    // committed and never replayed on a later clean poll, unlike the pre-fix read_line-on-`String`
    // path which propagated the UTF-8 error, dropped the batch's checkpoint, and re-read forever.
    #[tokio::test]
    async fn invalid_utf8_record_advances_checkpoint_without_replaying_valid_entries() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();

        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(tracked.len(), 1);
        assert_eq!(tracked[0].kind, SourceKind::Service("steam".to_string()));
        let mut tracker = lazy_tracker(tracked.clone());

        tracker.read_new_entries(&tracked[0]).unwrap();
        assert!(tracker.entries.is_empty());

        // ONE batch: a valid record, then a complete record with invalid UTF-8, then another valid
        // record, each newline-terminated.
        let mut batch: Vec<u8> = Vec::new();
        batch.extend_from_slice(detailed_steam_line("10.5.5.1", 654321, 2000, "MISS").as_bytes());
        batch.extend_from_slice(&[0xff, 0x9f, 0x92, 0xde]);
        batch.extend_from_slice(b" not-a-log-line \n");
        batch.extend_from_slice(detailed_steam_line("10.5.5.2", 654321, 3000, "HIT").as_bytes());
        append_bytes(&steam, &batch);

        tracker.read_new_entries(&tracked[0]).unwrap();
        assert_eq!(
            tracker.entries.len(),
            2,
            "both valid records parse; the invalid-UTF-8 record is skipped, not fatal"
        );

        // A second poll has no new data. Because the checkpoint advanced PAST the whole batch, the
        // earlier valid records are not replayed.
        tracker.read_new_entries(&tracked[0]).unwrap();
        assert_eq!(
            tracker.entries.len(),
            2,
            "a clean poll must not replay already-committed records"
        );
    }

    // A record LARGER than MAX_POLL_BYTES with no newline inside the first capped slice must not
    // stall the source: the reader discards the oversized fragment and recovers to the following
    // valid record, reaching it EXACTLY once (not stalled, not duplicated) without corrupting the
    // checkpoint. Before the scan-cursor/discard fix, the capped no-newline slice returned without
    // advancing and the source re-read the same 8 MiB prefix on every poll forever.
    #[tokio::test]
    async fn oversized_record_recovers_to_next_valid_record_exactly_once() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let access = dir.join("access.log");
        std::fs::write(&access, b"").unwrap();

        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());

        // First poll seeds to EOF (empty file) and reads nothing.
        tracker.read_new_entries(&tracked[0]).unwrap();
        assert!(tracker.entries.is_empty());

        // An oversized record: MAX_POLL_BYTES + a remainder of non-newline bytes, so its first
        // newline lies BEYOND the first capped 8 MiB read. It is terminated, then followed by a
        // normal newline-terminated record that must be reached exactly once.
        let oversized_len = MAX_POLL_BYTES as usize + 4096;
        let filler = vec![b'x'; oversized_len];
        append_bytes(&access, &filler);
        append_bytes(&access, b"\n");
        let valid = mono_line(
            "wsus",
            "10.7.7.7",
            "/content/after-oversized.bin",
            5000,
            "HIT",
        );
        append_bytes(&access, valid.as_bytes());

        // Drive the reader across enough polls for the oversized record to be discarded (one capped
        // read per 8 MiB) and the following valid record to be parsed.
        for _ in 0..4 {
            tracker.read_new_entries(&tracked[0]).unwrap();
        }

        assert_eq!(
            tracker.entries.len(),
            1,
            "the oversized record is discarded; only the following valid record is parsed, once"
        );
        assert_eq!(tracker.entries[0].client_ip, "10.7.7.7");
        assert_eq!(tracker.entries[0].bytes_sent, 5000);
        assert!(tracker.entries[0].is_cache_hit);

        // Further polls have no new data: the checkpoint advanced past the whole file, so nothing
        // is replayed and the source did not stall.
        tracker.read_new_entries(&tracked[0]).unwrap();
        assert_eq!(
            tracker.entries.len(),
            1,
            "a clean poll after recovery must not replay or duplicate the valid record"
        );
    }

    #[tokio::test]
    async fn cold_flow_keeps_membership_after_measurement_then_expires_at_boundary() {
        let mut tracker = lazy_tracker(Vec::new());
        let base = Utc::now();
        let entry = flow_entry(
            base.naive_utc(),
            base.naive_utc(),
            "10.0.0.2",
            "wsus",
            "root-a",
            "Primary",
            2000,
        );
        let old_entries = VecDeque::from([entry.clone()]);
        tracker.record_flow(
            service_key("root-a", "10.0.0.2", "wsus"),
            vec![entry],
            Some("Windows Update".to_string()),
            None,
        );

        let fixed_start =
            (base + Duration::seconds(10)).naive_utc() - Duration::seconds(WINDOW_SECONDS);
        let (_, _, old_active) =
            headline_aggregates(&old_entries, fixed_start, WINDOW_SECONDS as f64);
        assert!(
            !old_active,
            "the former two-second membership drops a cold flow during a ten-second pause"
        );

        let measured = tracker
            .calculate_snapshot(base + Duration::seconds(1))
            .await;
        assert_eq!(measured.game_speeds.len(), 1);
        assert_eq!(measured.client_speeds.len(), 1);
        assert_eq!(measured.entries_in_window, 1);
        assert_eq!(measured.game_speeds[0].bytes_per_second, 1000.0);

        let quiet = tracker
            .calculate_snapshot(base + Duration::seconds(WINDOW_SECONDS))
            .await;
        assert_eq!(quiet.game_speeds.len(), 1);
        assert_eq!(quiet.client_speeds.len(), 1);
        assert_eq!(quiet.entries_in_window, 0);
        assert_eq!(quiet.game_speeds[0].bytes_per_second, 0.0);
        assert_eq!(quiet.game_speeds[0].total_bytes, 0);
        assert!(quiet.has_active_downloads);
        assert!(tracker
            .flows
            .values()
            .all(|state| state.measurements.is_empty()));

        let paused = tracker
            .calculate_snapshot(base + Duration::seconds(10))
            .await;
        assert_eq!(paused.game_speeds.len(), 1);
        assert_eq!(paused.client_speeds.len(), 1);

        let expired = tracker
            .calculate_snapshot(base + Duration::seconds(MAX_WINDOW_SECONDS))
            .await;
        assert!(expired.game_speeds.is_empty());
        assert!(expired.client_speeds.is_empty());
        assert!(!expired.has_active_downloads);
    }

    #[tokio::test]
    async fn learned_fast_flow_survives_pause_and_eventually_expires() {
        let mut tracker = lazy_tracker(Vec::new());
        let base = Utc::now();
        let key = service_key("root-a", "10.0.0.3", "wsus");
        let mut old_entries = VecDeque::new();

        for second in 0..=CADENCE_SAMPLES as i64 {
            let at = base + Duration::seconds(second);
            let entry = flow_entry(
                at.naive_utc(),
                at.naive_utc(),
                "10.0.0.3",
                "wsus",
                "root-a",
                "Primary",
                1000,
            );
            old_entries.push_back(entry.clone());
            tracker.record_flow(
                key.clone(),
                vec![entry],
                Some("Windows Update".to_string()),
                None,
            );
        }

        let state = tracker.flows.get(&key).expect("warm flow state");
        assert_eq!(state.cadence_count, CADENCE_SAMPLES);
        assert_eq!(
            state.active_until,
            (base + Duration::seconds(4 + MAX_WINDOW_SECONDS)).naive_utc()
        );

        let fixed_start =
            (base + Duration::seconds(9)).naive_utc() - Duration::seconds(WINDOW_SECONDS);
        let (_, _, old_active) =
            headline_aggregates(&old_entries, fixed_start, WINDOW_SECONDS as f64);
        assert!(
            !old_active,
            "the former two-second membership drops the warm flow during a five-second pause"
        );

        let five_second_pause = tracker
            .calculate_snapshot(base + Duration::seconds(9))
            .await;
        assert_eq!(five_second_pause.game_speeds.len(), 1);
        assert_eq!(five_second_pause.game_speeds[0].bytes_per_second, 0.0);

        let paused = tracker
            .calculate_snapshot(base + Duration::seconds(14))
            .await;
        assert_eq!(paused.game_speeds.len(), 1);
        assert_eq!(paused.game_speeds[0].bytes_per_second, 0.0);

        let resumed_at = base + Duration::seconds(14);
        tracker.record_flow(
            key.clone(),
            vec![flow_entry(
                resumed_at.naive_utc(),
                resumed_at.naive_utc(),
                "10.0.0.3",
                "wsus",
                "root-a",
                "Primary",
                1000,
            )],
            Some("Windows Update".to_string()),
            None,
        );
        let state = tracker.flows.get(&key).expect("resumed flow state");
        assert_eq!(
            state.active_until,
            (base + Duration::seconds(36)).naive_utc(),
            "a ten-second recent gap yields a 22-second horizon"
        );

        let expired = tracker
            .calculate_snapshot(base + Duration::seconds(36))
            .await;
        assert!(expired.game_speeds.is_empty());
    }

    #[tokio::test]
    async fn slow_flow_horizon_is_bounded_and_isolated_by_identity_and_source() {
        let base = Utc::now().naive_utc();
        let slow_key = service_key("root-a", "10.0.0.4", "wsus");
        let fast_key = service_key("root-a", "10.0.0.5", "wsus");
        let delayed_key = service_key("root-b", "10.0.0.4", "wsus");
        let mut tracker = lazy_tracker(Vec::new());

        for step in 0..=CADENCE_SAMPLES {
            let slow_at = base + Duration::seconds(step as i64 * 12);
            tracker.record_flow(
                slow_key.clone(),
                vec![flow_entry(
                    slow_at, slow_at, "10.0.0.4", "wsus", "root-a", "Primary", 1000,
                )],
                Some("Windows Update".to_string()),
                None,
            );

            let fast_at = base + Duration::seconds(step as i64);
            tracker.record_flow(
                fast_key.clone(),
                vec![flow_entry(
                    fast_at, fast_at, "10.0.0.5", "wsus", "root-a", "Primary", 1000,
                )],
                Some("Windows Update".to_string()),
                None,
            );

            let mut delayed = flow_entry(
                slow_at,
                slow_at,
                "10.0.0.4",
                "wsus",
                "root-b",
                "Secondary",
                1000,
            );
            delayed.transport_delay_secs = MAX_WINDOW_SECONDS as f64;
            tracker.record_flow(
                delayed_key.clone(),
                vec![delayed],
                Some("Windows Update".to_string()),
                None,
            );
        }

        let slow = tracker.flows.get(&slow_key).expect("slow flow");
        let fast = tracker.flows.get(&fast_key).expect("fast flow");
        let delayed = tracker
            .flows
            .get(&delayed_key)
            .expect("delayed source flow");
        assert_eq!(
            slow.active_until,
            base + Duration::seconds(48 + 26),
            "four twelve-second gaps yield the exact 26-second horizon"
        );
        assert_eq!(
            fast.active_until,
            base + Duration::seconds(4 + MAX_WINDOW_SECONDS),
            "another client's slow cadence does not widen this flow"
        );
        assert_eq!(
            delayed.active_until,
            base + Duration::seconds(48 + 2 * MAX_WINDOW_SECONDS),
            "transport evidence is capped by the 30-second activity ceiling"
        );
    }

    #[tokio::test]
    async fn game_and_client_counts_follow_client_qualified_content_identity() {
        let base = Utc::now();
        let mut tracker = lazy_tracker(Vec::new());
        for (client_ip, game_name, bytes_sent) in [
            ("10.0.0.11", "Game A", 1000),
            ("10.0.0.11", "Game B", 2000),
            ("10.0.0.12", "Game A", 3000),
        ] {
            tracker.record_flow(
                FlowKey {
                    source_root: PathBuf::from("root-a"),
                    client_ip: client_ip.to_string(),
                    service: "epic".to_string(),
                    content: ContentKey::Named(game_name.to_ascii_lowercase()),
                },
                vec![flow_entry(
                    base.naive_utc(),
                    base.naive_utc(),
                    client_ip,
                    "epic",
                    "root-a",
                    "Primary",
                    bytes_sent,
                )],
                Some(game_name.to_string()),
                None,
            );
        }

        let snapshot = tracker
            .calculate_snapshot(base + Duration::seconds(1))
            .await;
        assert_eq!(snapshot.game_speeds.len(), 3);
        assert_eq!(snapshot.client_speeds.len(), 2);
        let first = snapshot
            .client_speeds
            .iter()
            .find(|client| client.client_ip == "10.0.0.11")
            .expect("first client");
        let second = snapshot
            .client_speeds
            .iter()
            .find(|client| client.client_ip == "10.0.0.12")
            .expect("second client");
        assert_eq!(first.active_games, 2);
        assert_eq!(second.active_games, 1);
    }

    #[tokio::test]
    async fn merged_game_keeps_each_source_boundary_and_measurement_separate() {
        let base = Utc::now();
        let mut tracker = lazy_tracker(Vec::new());
        tracker.record_flow(
            service_key("root-a", "10.0.0.13", "wsus"),
            vec![flow_entry(
                base.naive_utc(),
                base.naive_utc(),
                "10.0.0.13",
                "wsus",
                "root-a",
                "Primary",
                2000,
            )],
            Some("Windows Update".to_string()),
            None,
        );
        let later = base + Duration::seconds(5);
        tracker.record_flow(
            service_key("root-b", "10.0.0.13", "wsus"),
            vec![flow_entry(
                later.naive_utc(),
                later.naive_utc(),
                "10.0.0.13",
                "wsus",
                "root-b",
                "Secondary",
                4000,
            )],
            Some("Windows Update".to_string()),
            None,
        );

        let merged = tracker
            .calculate_snapshot(base + Duration::seconds(6))
            .await;
        assert_eq!(merged.game_speeds.len(), 1);
        assert_eq!(merged.game_speeds[0].sources.len(), 2);
        assert_eq!(merged.game_speeds[0].total_bytes, 4000);

        let one_source = tracker
            .calculate_snapshot(base + Duration::seconds(MAX_WINDOW_SECONDS))
            .await;
        assert_eq!(one_source.game_speeds.len(), 1);
        assert_eq!(one_source.game_speeds[0].sources.len(), 1);
        assert_eq!(
            one_source.game_speeds[0].sources[0].datasources,
            vec!["Secondary"]
        );

        let expired = tracker
            .calculate_snapshot(later + Duration::seconds(MAX_WINDOW_SECONDS))
            .await;
        assert!(expired.game_speeds.is_empty());
    }

    #[test]
    fn source_transport_uses_batch_evidence_without_inter_delivery_silence() {
        let base = Utc::now().naive_utc();
        let mut state = SourceState::anchored(0);
        state.record_delivery(base, base, base);
        state.record_delivery(
            base + Duration::seconds(40),
            base + Duration::seconds(40),
            base + Duration::seconds(40),
        );
        assert_eq!(
            state.measured_cadence(),
            0.0,
            "a forty-second quiet interval is not transport delay"
        );

        let delay = state.record_delivery(
            base + Duration::seconds(41),
            base + Duration::seconds(41),
            base + Duration::seconds(51),
        );
        assert_eq!(delay, 10.0, "observation delay is retained for this source");

        let capped = state.record_delivery(
            base + Duration::seconds(52),
            base + Duration::seconds(52),
            base + Duration::seconds(92),
        );
        assert_eq!(capped, MAX_WINDOW_SECONDS as f64);
    }

    #[test]
    fn same_timestamp_counts_once_per_record_without_renewing_the_session() {
        let base = Utc::now().naive_utc();
        let first = flow_entry(base, base, "10.0.0.6", "wsus", "root-a", "Primary", 1000);
        let mut state = FlowState::new(
            &first,
            Some("Windows Update".to_string()),
            None,
            WINDOW_SECONDS,
        )
        .expect("new flow");
        let active_until = state.active_until;
        state.record(
            &flow_entry(base, base, "10.0.0.6", "wsus", "root-a", "Primary", 2000),
            Some("Windows Update".to_string()),
            None,
            WINDOW_SECONDS,
        );
        assert_eq!(state.cadence_count, 0);
        assert_eq!(state.active_until, active_until);
        assert_eq!(state.measurements.len(), 2);
        assert_eq!(
            state
                .measurements
                .iter()
                .map(|entry| entry.bytes_sent)
                .sum::<i64>(),
            3000
        );

        state.record(
            &flow_entry(
                base - Duration::seconds(1),
                base + Duration::seconds(1),
                "10.0.0.6",
                "wsus",
                "root-a",
                "Primary",
                3000,
            ),
            Some("Windows Update".to_string()),
            None,
            WINDOW_SECONDS,
        );
        assert_eq!(
            state.active_until, active_until,
            "out-of-order evidence does not renew activity"
        );
        assert!(
            FlowState::new(
                &flow_entry(
                    base,
                    base + Duration::seconds(MAX_WINDOW_SECONDS),
                    "10.0.0.6",
                    "wsus",
                    "root-a",
                    "Primary",
                    1000,
                ),
                Some("Windows Update".to_string()),
                None,
                WINDOW_SECONDS,
            )
            .is_none(),
            "a delayed completion whose candidate has ended cannot resurrect a flow"
        );
    }

    #[tokio::test]
    async fn future_completion_is_excluded_from_activity_and_measurement() {
        let base = Utc::now();
        let mut tracker = lazy_tracker(Vec::new());
        tracker.entries.push_back(flow_entry(
            (base + Duration::seconds(30)).naive_utc(),
            base.naive_utc(),
            "10.0.0.7",
            "wsus",
            "root-a",
            "Primary",
            1000,
        ));

        let snapshot = tracker.calculate_snapshot(base).await;
        assert!(snapshot.game_speeds.is_empty());
        assert_eq!(snapshot.entries_in_window, 0);
        assert!(tracker.entries.is_empty());
    }

    // A log host whose clock runs a little ahead still shows its downloads: a completion up to
    // the activity floor ahead is counted at the tracker's own time, one a minute ahead is not.
    #[tokio::test]
    async fn completion_slightly_ahead_is_clamped_and_far_ahead_is_dropped() {
        let base = Utc::now();
        let mut tracker = lazy_tracker(Vec::new());
        for (ahead, client_ip) in [(2, "10.0.0.7"), (60, "10.0.0.8")] {
            tracker.entries.push_back(flow_entry(
                (base + Duration::seconds(ahead)).naive_utc(),
                base.naive_utc(),
                client_ip,
                "uplay",
                "root-a",
                "Primary",
                1000,
            ));
        }

        let snapshot = tracker.calculate_snapshot(base).await;
        assert_eq!(snapshot.game_speeds.len(), 1);
        let game = &snapshot.game_speeds[0];
        assert_eq!(game.client_ip, "10.0.0.7");
        assert_eq!(game.total_bytes, 1000);
        assert_eq!(game.last_seen_utc, snapshot.timestamp_utc);
        assert_eq!(snapshot.entries_in_window, 1);
    }

    // A buffered bare-metal log delivers five seconds of completions in one flush. Every sample
    // between flushes must report the true rate, not a spike followed by zeros, while the
    // snapshot keeps the two-second base window the server and the browser require.
    #[tokio::test]
    async fn buffered_flush_reports_the_true_rate_between_flushes() {
        let mut tracker = lazy_tracker(Vec::new());
        let source = PathBuf::from("root-a/uplay-access.log");
        let base = Utc::now();
        let rate = 1_000_000i64;
        let spacing_ms = 100i64;
        let mut samples = Vec::new();
        for flush in 1..=6i64 {
            let flushed_at = base + Duration::seconds(5 * flush);
            let first = flushed_at - Duration::milliseconds(5000 - spacing_ms);
            let mut state = tracker
                .file_positions
                .get(&source)
                .copied()
                .unwrap_or_else(|| SourceState::anchored(0));
            let delay = state.record_delivery(
                first.naive_utc(),
                flushed_at.naive_utc(),
                flushed_at.naive_utc(),
            );
            tracker.file_positions.insert(source.clone(), state);
            for index in 0..5000 / spacing_ms {
                let mut entry = flow_entry(
                    (first + Duration::milliseconds(index * spacing_ms)).naive_utc(),
                    flushed_at.naive_utc(),
                    "10.0.0.9",
                    "uplay",
                    "root-a",
                    "Primary",
                    rate * spacing_ms / 1000,
                );
                entry.transport_delay_secs = delay;
                tracker.entries.push_back(entry);
            }

            for half_seconds in 0..10 {
                let now = flushed_at + Duration::milliseconds(500 * half_seconds);
                let snapshot = tracker.calculate_snapshot(now).await;
                assert_eq!(snapshot.window_seconds, WINDOW_SECONDS);
                // The first flush has no earlier history inside the window yet.
                if flush > 1 {
                    samples.push(snapshot.total_bytes_per_second);
                }
            }
        }

        for (index, sample) in samples.iter().enumerate() {
            assert!(
                (sample - rate as f64).abs() <= rate as f64 * 0.1,
                "sample {index} reported {sample} B/s for a steady {rate} B/s download"
            );
        }
    }

    // A request for depot 0 names no real depot. It parses as an entry without a depot, so the
    // server never sees a depot id it must reject.
    #[tokio::test]
    async fn depot_zero_request_parses_without_a_depot() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();
        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());
        tracker.read_new_entries(&tracked[0]).unwrap();

        append_bytes(
            &steam,
            detailed_steam_line("10.0.0.2", 0, 1000, "MISS").as_bytes(),
        );
        tracker.read_new_entries(&tracked[0]).unwrap();

        let entry = tracker.entries.back().expect("the depot 0 line parses");
        assert_eq!(entry.depot_id, None);
    }

    // One game seen through two sources whose newest completions differ. Each source row divides
    // its own bytes by its own coverage, and the game row is the sum of the source rows.
    #[tokio::test]
    async fn game_rate_is_the_sum_of_its_source_rates() {
        let mut tracker = lazy_tracker(Vec::new());
        let base = Utc::now();
        let mut state = SourceState::anchored(0);
        state.record_delivery(
            (base - Duration::milliseconds(4900)).naive_utc(),
            base.naive_utc(),
            base.naive_utc(),
        );
        tracker
            .file_positions
            .insert(PathBuf::from("root-a/uplay-access.log"), state);
        assert_eq!(tracker.effective_window_secs(), 7);

        for (root, datasource, seconds_ago, bytes_sent) in [
            ("root-a", "Primary", 3, 6000),
            ("root-a", "Primary", 1, 6000),
            ("root-b", "Secondary", 4, 3000),
        ] {
            tracker.record_flow(
                service_key(root, "10.0.0.14", "uplay"),
                vec![flow_entry(
                    (base - Duration::seconds(seconds_ago)).naive_utc(),
                    base.naive_utc(),
                    "10.0.0.14",
                    "uplay",
                    root,
                    datasource,
                    bytes_sent,
                )],
                Some("Ubisoft".to_string()),
                None,
            );
        }

        let snapshot = tracker.calculate_snapshot(base).await;
        assert_eq!(snapshot.window_seconds, WINDOW_SECONDS);
        assert_eq!(snapshot.game_speeds.len(), 1);
        let game = &snapshot.game_speeds[0];
        let rates: Vec<f64> = game
            .sources
            .iter()
            .map(|source| source.bytes_per_second)
            .collect();
        assert_eq!(
            rates,
            vec![12000.0 / 6.0, 3000.0 / 3.0],
            "each source divides by the seconds between the window start and its newest completion"
        );
        assert_eq!(game.bytes_per_second, rates.iter().sum::<f64>());
        assert_eq!(snapshot.total_bytes_per_second, game.bytes_per_second);
    }

    // A buffered per-service log delivers several seconds of history in one flush. Against the
    // fixed 2s window that burst is entirely in the past, so the window empties and the source
    // reads inactive (the 1->0 flicker). The adaptive window must widen to cover the delivery span
    // so the source stays active, and speed must divide by observed coverage.
    #[tokio::test]
    async fn buffered_source_window_widens_to_cover_flush_gap() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();

        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());

        // Seed to EOF (reads nothing, records no cadence).
        tracker.read_new_entries(&tracked[0]).unwrap();

        // One flush delivering a burst whose log timestamps span 5 seconds (8s..3s in the past), all
        // at once. The source's delay sample is the larger of that span and how late the newest
        // record arrived, so the span is chosen to exceed the newest record's age.
        let base = Utc::now().naive_utc();
        let bytes_each = 1000i64;
        let mut burst = String::new();
        for secs in [8i64, 7, 6, 5, 4, 3] {
            burst.push_str(&detailed_steam_line_at(
                "10.0.0.2",
                654321,
                bytes_each,
                "MISS",
                base - Duration::seconds(secs),
            ));
        }
        append_bytes(&steam, burst.as_bytes());
        tracker.read_new_entries(&tracked[0]).unwrap();

        // Delivery span 5s > base window, so the window widens: ceil(5 + WINDOW_SECONDS) = 7.
        let w = tracker.effective_window_secs();
        assert_eq!(
            w, 7,
            "a 5s delivery span widens the window to cover the flush gap"
        );

        // Fixed 2s window: the burst was delivered entirely in the past, so the window is empty and
        // the source reads inactive - exactly the flicker this change removes.
        let fixed_start = base - Duration::seconds(WINDOW_SECONDS);
        let (_, _, fixed_active) =
            headline_aggregates(&tracker.entries, fixed_start, WINDOW_SECONDS as f64);
        assert!(
            !fixed_active,
            "the fixed 2s window empties between buffered flushes"
        );

        // Adaptive window: still covers the burst, so the source stays active, and speed divides by
        // observed coverage (newest in-window timestamp minus window_start), not the whole window.
        let window_start = base - Duration::seconds(w);
        let in_window_bytes: i64 = tracker
            .entries
            .iter()
            .filter(|e| e.timestamp >= window_start)
            .map(|e| e.bytes_sent)
            .sum();
        let anchor = tracker
            .entries
            .iter()
            .map(|e| e.timestamp)
            .filter(|ts| *ts >= window_start)
            .max()
            .expect("the widened window contains the burst");
        let coverage =
            (anchor.min(Utc::now().naive_utc()) - window_start).num_milliseconds() as f64 / 1000.0;
        let divisor = coverage.clamp(WINDOW_SECONDS as f64, w as f64);
        let (bps, cnt, active) = headline_aggregates(&tracker.entries, window_start, divisor);
        assert!(active, "the adaptive window still sees the buffered burst");
        assert!(cnt > 0, "the widened window is non-empty");
        assert_eq!(
            bps,
            in_window_bytes as f64 / divisor,
            "speed divides by observed coverage, not the raw window"
        );
    }

    // Unbuffered/monolithic delivery: each poll appends a line carrying the CURRENT timestamp, so
    // the log's timeline tracks wall-clock and no multi-second gap ever forms. The window must stay
    // at exactly WINDOW_SECONDS and the divisor must be exactly 2 - bit-for-bit today's behavior.
    #[tokio::test]
    async fn unbuffered_source_keeps_two_second_window() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();

        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());
        tracker.read_new_entries(&tracked[0]).unwrap();

        for _ in 0..4 {
            append_bytes(
                &steam,
                detailed_steam_line("10.0.0.2", 654321, 1000, "MISS").as_bytes(),
            );
            tracker.read_new_entries(&tracked[0]).unwrap();
        }

        assert_eq!(
            tracker.effective_window_secs(),
            WINDOW_SECONDS,
            "fresh delivery never widens the window - monolithic behavior is preserved"
        );

        let window_start = Utc::now().naive_utc() - Duration::seconds(WINDOW_SECONDS);
        let (bps, _, active) =
            headline_aggregates(&tracker.entries, window_start, WINDOW_SECONDS as f64);
        assert!(active);
        assert_eq!(
            bps,
            4000.0 / WINDOW_SECONDS as f64,
            "unbuffered speed divides by exactly 2"
        );
    }

    // Two deliveries separated by a long idle gap, each read as soon as it was written. The forty
    // seconds between them measure idleness, not delivery cadence, so the window must stay at
    // WINDOW_SECONDS. Each delivery's observation time is set directly because the tail path
    // stamps every batch with the wall clock.
    #[tokio::test]
    async fn idle_gap_does_not_widen_window() {
        let mut tracker = lazy_tracker(Vec::new());
        let base = Utc::now().naive_utc();
        let first = base - Duration::seconds(40);
        let mut state = SourceState::anchored(0);
        state.record_delivery(first, first, first);
        state.record_delivery(base, base, base);
        tracker
            .file_positions
            .insert(PathBuf::from("root-a/steam-access.log"), state);

        assert_eq!(
            tracker.effective_window_secs(),
            WINDOW_SECONDS,
            "an idle gap between deliveries must not be mistaken for slow delivery cadence"
        );
    }

    #[tokio::test]
    async fn snapshot_serializes_protocol_two_absolute_boundaries_and_source_scope() {
        let base = Utc::now();
        let mut tracker = lazy_tracker(Vec::new());
        tracker.record_flow(
            service_key("private-root", "10.0.0.8", "wsus"),
            vec![flow_entry(
                base.naive_utc(),
                base.naive_utc(),
                "10.0.0.8",
                "wsus",
                "private-root",
                "Primary",
                2000,
            )],
            Some("Windows Update".to_string()),
            None,
        );

        let snapshot = tracker
            .calculate_snapshot(base + Duration::seconds(1))
            .await;
        let value = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(value["version"], PROTOCOL_VERSION);
        assert_eq!(value["streamId"], "");
        assert_eq!(value["revision"], 1);
        assert_eq!(value["isAvailable"], true);
        assert_eq!(value["windowSeconds"], WINDOW_SECONDS);
        assert_eq!(value["hasActiveDownloads"], true);
        assert!(value["gameSpeeds"][0]["key"]
            .as_str()
            .is_some_and(|key| key == "wsus|10.0.0.8|service"));
        assert!(value["gameSpeeds"][0].get("depotIds").is_none());
        let source = &value["gameSpeeds"][0]["sources"][0];
        assert_eq!(source["datasources"][0], "Primary");
        assert!(source["activeUntilUtc"].as_str().is_some());
        assert!(source["measuredUntilUtc"].as_str().is_some());
        assert!(source.get("cacheHitPercent").is_none());
        assert!(value["clientSpeeds"][0]["activeUntilUtc"]
            .as_str()
            .is_some());
        assert!(
            !serde_json::to_string(&snapshot)
                .unwrap()
                .contains("private-root"),
            "physical roots stay internal"
        );

        let second = tracker
            .calculate_snapshot(base + Duration::milliseconds(1100))
            .await;
        assert_eq!(second.revision, 2);
        let mut restarted = lazy_tracker(Vec::new());
        let restarted_snapshot = restarted.calculate_snapshot(base).await;
        assert_eq!(restarted_snapshot.revision, 1);
        assert_eq!(restarted_snapshot.stream_id, "");
    }

    #[tokio::test]
    async fn client_order_is_ordinal_even_when_rates_cross() {
        let base = Utc::now();
        let mut tracker = lazy_tracker(Vec::new());
        tracker.record_flow(
            service_key("root-a", "10.0.0.2", "wsus"),
            vec![flow_entry(
                base.naive_utc(),
                base.naive_utc(),
                "10.0.0.2",
                "wsus",
                "root-a",
                "Primary",
                9000,
            )],
            Some("Windows Update".to_string()),
            None,
        );
        tracker.record_flow(
            service_key("root-a", "10.0.0.10", "wsus"),
            vec![flow_entry(
                base.naive_utc(),
                base.naive_utc(),
                "10.0.0.10",
                "wsus",
                "root-a",
                "Primary",
                1000,
            )],
            Some("Windows Update".to_string()),
            None,
        );

        let snapshot = tracker
            .calculate_snapshot(base + Duration::seconds(1))
            .await;
        let clients: Vec<_> = snapshot
            .client_speeds
            .iter()
            .map(|client| client.client_ip.as_str())
            .collect();
        assert_eq!(clients, vec!["10.0.0.10", "10.0.0.2"]);
    }

    #[tokio::test]
    async fn mapping_transition_preserves_session_boundaries_without_duplicate_flow() {
        let base = Utc::now().naive_utc();
        let mut entry = steam_entry("10.0.0.9", 1001, 1000);
        entry.timestamp = base;
        entry.observed_at = base;
        entry.source_root = PathBuf::from("root-a");
        entry.datasources = vec!["Primary".to_string()];
        let state = FlowState::new(&entry, None, None, WINDOW_SECONDS)
            .expect("unresolved depot starts a live flow");
        let first_seen = state.first_seen;
        let active_until = state.active_until;
        let old_key = FlowKey {
            source_root: PathBuf::from("root-a"),
            client_ip: "10.0.0.9".to_string(),
            service: "steam".to_string(),
            content: ContentKey::SteamDepot(1001),
        };
        let new_key = FlowKey {
            source_root: PathBuf::from("root-a"),
            client_ip: "10.0.0.9".to_string(),
            service: "steam".to_string(),
            content: ContentKey::SteamApp(730),
        };
        let mut tracker = lazy_tracker(Vec::new());
        tracker.flows.insert(old_key.clone(), state);

        tracker.move_depot_flow(
            &PathBuf::from("root-a"),
            "10.0.0.9",
            "steam",
            1001,
            730,
            Some("Counter-Strike 2".to_string()),
        );

        assert!(!tracker.flows.contains_key(&old_key));
        let migrated = tracker.flows.get(&new_key).expect("app flow");
        assert_eq!(migrated.first_seen, first_seen);
        assert_eq!(migrated.active_until, active_until);
        assert_eq!(migrated.depot_ids, vec![1001]);
        assert_eq!(migrated.game_app_id, Some(730));
    }

    #[test]
    fn named_sources_deduplicate_physical_root_and_retain_aliases() {
        let tmp = tempfile::tempdir().unwrap();
        std::fs::write(tmp.path().join("access.log"), b"").unwrap();
        let configured = vec![
            ("Primary".to_string(), tmp.path().to_path_buf()),
            ("primary".to_string(), tmp.path().to_path_buf()),
            ("Secondary".to_string(), tmp.path().to_path_buf()),
        ];

        let tracked = discover_tracked_sources(&configured);
        assert_eq!(tracked.len(), 1);
        assert_eq!(tracked[0].datasources, vec!["Primary", "Secondary"]);
    }

    #[cfg(unix)]
    #[test]
    fn linux_paths_that_differ_only_by_case_remain_distinct_sources() {
        let tmp = tempfile::tempdir().unwrap();
        let upper = tmp.path().join("Cache");
        let lower = tmp.path().join("cache");
        std::fs::create_dir_all(&upper).unwrap();
        std::fs::create_dir_all(&lower).unwrap();
        std::fs::write(upper.join("access.log"), b"").unwrap();
        std::fs::write(lower.join("access.log"), b"").unwrap();

        let tracked =
            discover_tracked_sources(&[("Upper".to_string(), upper), ("Lower".to_string(), lower)]);
        assert_eq!(tracked.len(), 2);
        assert_ne!(tracked[0].root, tracked[1].root);
    }

    #[test]
    fn cli_supports_repeated_named_sources_and_standalone_positional_directories() {
        let named = parse_source_args(&[
            "--source".to_string(),
            "Primary".to_string(),
            "/logs/a".to_string(),
            "--source".to_string(),
            "Secondary".to_string(),
            "/logs/b".to_string(),
        ])
        .unwrap();
        assert_eq!(named.len(), 2);
        assert_eq!(named[0].0, "Primary");

        let positional =
            parse_source_args(&["/logs/a".to_string(), "/logs/b".to_string()]).unwrap();
        assert_eq!(positional[0].0, "source-1");
        assert_eq!(positional[1].0, "source-2");

        assert!(parse_source_args(&[
            "/logs/a".to_string(),
            "--source".to_string(),
            "Primary".to_string(),
            "/logs/b".to_string(),
        ])
        .is_err());
        assert!(parse_source_args(&["--source".to_string(), "Primary".to_string(),]).is_err());
    }

    #[tokio::test]
    async fn rotation_preserves_source_transport_evidence() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();

        let tracked = discover_tracked_sources(&[("test".to_string(), dir.to_path_buf())]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());
        tracker.read_new_entries(&tracked[0]).unwrap();

        let base = Utc::now().naive_utc();
        let mut burst = String::new();
        for seconds in [7i64, 6, 5, 4, 3] {
            burst.push_str(&detailed_steam_line_at(
                "10.0.0.2",
                654321,
                1000,
                "MISS",
                base - Duration::seconds(seconds),
            ));
        }
        append_bytes(&steam, burst.as_bytes());
        tracker.read_new_entries(&tracked[0]).unwrap();

        let learned = tracker
            .file_positions
            .get(&tracked[0].path)
            .expect("source state after delivery")
            .measured_cadence();
        assert!(learned > 0.0);

        std::fs::write(&steam, b"").unwrap();
        tracker.read_new_entries(&tracked[0]).unwrap();

        let after = tracker
            .file_positions
            .get(&tracked[0].path)
            .expect("source state after rotation")
            .measured_cadence();
        assert_eq!(after, learned);
    }
}
