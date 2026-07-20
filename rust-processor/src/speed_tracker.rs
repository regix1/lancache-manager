use anyhow::Result;
use chrono::{NaiveDateTime, Utc};
use chrono_tz::Tz;
use serde::Serialize;
use sqlx::PgPool;
use sqlx::Row;
use std::collections::{HashMap, VecDeque};
use std::env;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::PathBuf;
use std::time::{Duration, Instant};

mod cache_utils;
mod db;
mod log_discovery;
mod log_layout;
mod models;
mod parser;
mod parser_http_detailed;
mod progress_events;
mod riot_hosts;
mod service_utils;
mod tact_products;

use log_layout::{discover_log_sources, SourceKind};
use parser::LogParser;
use parser_http_detailed::HttpDetailedParser;

// Configuration
const WINDOW_SECONDS: i64 = 2;
// Upper bound on the adaptive rolling window and the fixed retention horizon. This is a backstop,
// not the mechanism: it matches the DB-side activity grace so the live view never claims a download
// is active longer than the database's own definition of active.
const MAX_WINDOW_SECONDS: i64 = 15;
// Depth of each source's recent-max cadence ring. A small fixed ring keeps SourceState `Copy` and
// auto-expires a stale large delivery gap after this many fresh deliveries, with no time-based
// pruning.
const CADENCE_SAMPLES: usize = 4;
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
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct GameSpeedInfo {
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
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DownloadSpeedSnapshot {
    timestamp_utc: String,
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
}

/// Resolve every datasource directory to the concrete files worth tailing. A directory may
/// expose 1..N sources (a monolithic `access.log`, and/or per-service bare-metal `*-access.log`
/// files); for each we tail only the CURRENT unrotated, uncompressed member. A source whose sole
/// surviving member is a rotated/compressed archive has no live file and is skipped, and the
/// fallback series is never ingested. Discovery reuses `log_layout::discover_log_sources`, so the
/// tracker carries no filename or layout grammar of its own.
fn discover_tracked_sources(dirs: &[PathBuf]) -> Vec<TrackedSource> {
    let mut tracked = Vec::new();
    for dir in dirs {
        let set = match discover_log_sources(dir) {
            Ok(set) => set,
            Err(e) => {
                eprintln!("Failed to discover log sources in {}: {}", dir.display(), e);
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
            });
        }
    }
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
        client_ip: entry.client_ip,
        service: entry.service,
        depot_id: entry.depot_id,
        bytes_sent: entry.bytes_served,
        is_cache_hit: entry.cache_status.eq_ignore_ascii_case("HIT"),
        request_url: entry.url,
        cdn_host: entry.cdn_host,
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
    // Log timestamp of the newest entry the previous entry-producing poll delivered. Lets the next
    // delivery measure how far the log's own timeline advanced between deliveries. `None` until the
    // first delivery.
    last_batch_ts: Option<NaiveDateTime>,
    // Recent-max ring of delivery-cadence samples (seconds). The window must cover the LARGEST
    // realistic delivery gap, so cadence is the max of the ring, not an average.
    cadence_ring: [f64; CADENCE_SAMPLES],
    // Write index into cadence_ring.
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
            last_batch_ts: None,
            cadence_ring: [0.0; CADENCE_SAMPLES],
            cadence_head: 0,
        }
    }

    /// Record one delivery's timestamp span into the recent-max cadence ring. The sample is the
    /// larger of the intra-batch span (a buffered flush delivers several seconds of history at
    /// once) and the advance since the previous delivery (which reinforces the cadence across
    /// consecutive bursts). An inter-delivery advance longer than the retention horizon measures
    /// idleness, not delivery cadence, so its signal is discarded to keep an overnight gap from
    /// pinning the window wide on the next download; the intra-batch span still counts. The
    /// previous-delivery timestamp is always updated so the next advance is measured from here.
    fn record_delivery(&mut self, min_ts: NaiveDateTime, max_ts: NaiveDateTime) {
        let intra_batch_span = (max_ts - min_ts).num_milliseconds() as f64 / 1000.0;
        let inter_advance = match self.last_batch_ts {
            Some(last) => {
                let advance = (max_ts - last).num_milliseconds() as f64 / 1000.0;
                if advance > MAX_WINDOW_SECONDS as f64 {
                    0.0
                } else {
                    advance
                }
            }
            None => 0.0,
        };
        let sample = intra_batch_span.max(inter_advance).max(0.0);
        self.cadence_ring[self.cadence_head] = sample;
        self.cadence_head = (self.cadence_head + 1) % CADENCE_SAMPLES;
        self.last_batch_ts = Some(max_ts);
    }

    /// The largest recent delivery-gap sample in seconds. This is the per-source cadence the
    /// effective window sizes itself to cover.
    fn measured_cadence(&self) -> f64 {
        self.cadence_ring.iter().copied().fold(0.0, f64::max)
    }
}

struct SpeedTracker {
    pool: PgPool,
    sources: Vec<TrackedSource>,
    cachelog: LogParser,
    detailed: HttpDetailedParser,
    entries: VecDeque<SpeedLogEntry>,
    depot_cache: HashMap<u32, (Option<String>, Option<u32>)>, // depot_id -> (game_name, game_app_id)
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
            depot_cache: HashMap::new(),
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
        eprintln!("SpeedTracker started - monitoring {} log file(s)", self.sources.len());

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

            // Clean old entries
            self.clean_old_entries();

            // Broadcast if interval passed
            if last_broadcast.elapsed() >= Duration::from_millis(BROADCAST_INTERVAL_MS) {
                let snapshot = self.calculate_snapshot().await;

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
            rotated.last_batch_ts = state.last_batch_ts;
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
                    self.parse_records(&buffer[resume..complete_len], &source.kind);
                    state.discarding = false;
                } else {
                    // Commit only PAST complete, newline-terminated records. Everything after the
                    // last newline is an incomplete final record read again next poll.
                    self.parse_records(&buffer[..complete_len], &source.kind);
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

        // A poll that appended entries just observed one delivery from this source. Measure the
        // log-timestamp span of those records so the window can size itself to real delivery
        // cadence: a buffered log (nginx flush) lands several seconds of history in a single burst,
        // and a fixed short window would empty between bursts and flicker the active state.
        if self.entries.len() > before {
            let mut min_ts: Option<NaiveDateTime> = None;
            let mut max_ts: Option<NaiveDateTime> = None;
            for entry in self.entries.iter().skip(before) {
                let ts = entry.timestamp;
                min_ts = Some(min_ts.map_or(ts, |current| current.min(ts)));
                max_ts = Some(max_ts.map_or(ts, |current| current.max(ts)));
            }
            if let (Some(min_ts), Some(max_ts)) = (min_ts, max_ts) {
                state.record_delivery(min_ts, max_ts);
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
    fn parse_records(&mut self, bytes: &[u8], kind: &SourceKind) {
        for record in bytes.split(|&b| b == b'\n') {
            if record.is_empty() {
                continue;
            }
            let decoded = String::from_utf8_lossy(record);
            let trimmed = decoded.trim();
            if trimmed.is_empty() {
                continue;
            }
            if let Some(entry) = parse_speed_entry(&self.cachelog, &self.detailed, trimmed, kind) {
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

    fn clean_old_entries(&mut self) {
        // Retention is fixed at the backstop horizon and decoupled from the adaptive snapshot
        // window: every downstream read re-filters by the current window_start, so retaining a bit
        // more is always safe, memory stays bounded, and a window that widens on a later burst can
        // retroactively re-include entries a narrower window would already have evicted.
        let cutoff = Utc::now().naive_utc() - chrono::Duration::seconds(MAX_WINDOW_SECONDS);

        self.entries.retain(|entry| entry.timestamp >= cutoff);
    }

    async fn calculate_snapshot(&mut self) -> DownloadSpeedSnapshot {
        let now = Utc::now();
        let now_naive = now.naive_utc();
        let window_secs = self.effective_window_secs();
        let window_start = now_naive - chrono::Duration::seconds(window_secs);

        // Clone entries within window to avoid borrow issues
        let window_entries: Vec<SpeedLogEntry> = self.entries.iter()
            .filter(|e| e.timestamp >= window_start)
            .cloned()
            .collect();

        // Speed divides by OBSERVED coverage, not the whole window. The newest in-window timestamp
        // (clamped to now so a future-dated line cannot shrink the divisor) marks how much of the
        // window actually holds delivered data. Buffered delivery lags the window tail by up to one
        // flush, so dividing by the full window would make a steady download's reported speed
        // sawtooth every flush cycle. The floor of WINDOW_SECONDS restores exact monolithic parity
        // (full coverage clamps up to 2.0 => bytes/2) and bounds a lone aging entry's speed.
        let anchor = window_entries
            .iter()
            .map(|e| e.timestamp)
            .max()
            .map(|max_ts| max_ts.min(now_naive))
            .unwrap_or(now_naive);
        let coverage_secs = (anchor - window_start).num_milliseconds() as f64 / 1000.0;
        let speed_divisor = coverage_secs.clamp(WINDOW_SECONDS as f64, window_secs as f64);

        // Whole-window headline aggregates come from the shared, DB-free `headline_aggregates`
        // seam (the same computation the streaming tail tests drive), reading straight from the
        // in-window entries. The per-client aggregation below still reads from window_entries
        // BEFORE the grouping consumes it by move, so this snapshot clones every windowed entry
        // exactly once (it used to clone each entry up to three times, every 500ms broadcast).
        let (total_bytes_per_second, entries_count, has_active_downloads) =
            headline_aggregates(&self.entries, window_start, speed_divisor);

        // Per-client (total, cache-hit) byte aggregates - only three fields are read per
        // entry, so no entry clone is needed.
        let mut client_aggregates: HashMap<String, (i64, i64)> = HashMap::new();
        for entry in &window_entries {
            let aggregate = client_aggregates.entry(entry.client_ip.clone()).or_insert((0, 0));
            aggregate.0 += entry.bytes_sent;
            if entry.is_cache_hit {
                aggregate.1 += entry.bytes_sent;
            }
        }

        // Group by depot + client for game speeds (Steam and other services with depot IDs)
        let mut depot_groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        // Group by service + client for non-depot entries (Epic, Origin, etc.)
        let mut service_groups: HashMap<(String, String), Vec<SpeedLogEntry>> = HashMap::new();

        for entry in window_entries {
            if let Some(depot_id) = entry.depot_id {
                let key = (depot_id, entry.client_ip.clone());
                depot_groups.entry(key).or_default().push(entry);
            } else {
                let key = (entry.service.clone(), entry.client_ip.clone());
                service_groups.entry(key).or_default().push(entry);
            }
        }

        // Resolve every depot up front. The collapse below must see lookup_depot's
        // actual results, not just depot_cache: a mapping row with an AppId but no
        // AppName is deliberately never cached (lookup_depot retries missing names),
        // so a cache-only read would treat that depot as unresolved and split one
        // game across rows.
        let depot_ids: Vec<u32> = depot_groups.keys().map(|(id, _)| *id).collect();
        let mut depot_resolutions: HashMap<u32, (Option<String>, Option<u32>)> = HashMap::new();
        for depot_id in depot_ids {
            let resolved = self.lookup_depot(depot_id).await;
            depot_resolutions.insert(depot_id, resolved);
        }

        // Build game speeds from depot groups (Steam and services with depot IDs).
        // Collapse buckets that resolve to the same Steam app so a game spanning many
        // depots (or a chunk/depot rollover inside the 2s window) shows as ONE row with
        // combined throughput, mirroring the resolved_groups collapse used for the
        // non-depot services below. Unresolved depots keep their per-depot identity.
        let mut game_speeds: Vec<GameSpeedInfo> = collapse_depot_groups(depot_groups, |depot_id| {
            depot_resolutions.get(&depot_id).cloned().unwrap_or((None, None))
        }, speed_divisor);

        // Add non-depot service entries (Epic, Origin, etc.)
        let mut resolved_groups: HashMap<(String, String), Vec<SpeedLogEntry>> = HashMap::new();
        for ((service, client_ip), entries) in service_groups {
            if service.contains("epic") {
                // Try to resolve each entry's URL to a game name, then sub-group
                let mut sub_groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
                for entry in entries {
                    let game_name = self.lookup_epic_game(&entry.request_url).await
                        .unwrap_or_else(|| get_service_display_name(&service));
                    sub_groups.entry(game_name).or_default().push(entry);
                }
                for (game_name, sub_entries) in sub_groups {
                    resolved_groups.entry((game_name, client_ip.clone())).or_default().extend(sub_entries);
                }
            } else if service.contains("blizzard") || service.contains("battle") {
                // Collapse a client's Blizzard traffic into ONE group named by the dominant
                // resolved game, so live rows show the real game (e.g. "Call of Duty: Black
                // Ops 4") without flickering. A single game download interleaves game-specific
                // /tpr/<game>/ paths with shared /tpr/configs|agent/ paths, so per-segment
                // sub-grouping (like Epic) splits one download into a flapping Game + "Battle.net
                // (shared)" pair as the 2s window's membership shifts. Pick the most-frequent
                // resolved Game when present; else the shared label; else the placeholder. (A
                // client downloading two Blizzard titles at once — rare — shows under the
                // dominant one in this transient live view.)
                let mut game_counts: HashMap<String, usize> = HashMap::new();
                let mut shared_label: Option<String> = None;
                for entry in &entries {
                    if let Some(seg) = tact_products::extract_tact_product(&entry.request_url) {
                        match tact_products::resolve_tact_segment(&seg) {
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
                    .or(shared_label)
                    .unwrap_or_else(|| get_service_display_name(&service));
                resolved_groups.entry((group_name, client_ip)).or_default().extend(entries);
            } else if service.contains("riot") {
                // Riot bundle URLs carry no product slug; sub-group each entry by the
                // game resolved from its CDN host (lol/valorant/bacon). One Riot game
                // maps to exactly one host, so there is no flapping (unlike Blizzard)
                // and no dominant-game collapse is needed. Unknown/absent hosts fall
                // back to the generic "Riot Games" service label.
                let mut sub_groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
                for entry in entries {
                    let game_name = entry
                        .cdn_host
                        .as_deref()
                        .and_then(riot_hosts::resolve_riot_host)
                        .map(|name| name.to_string())
                        .unwrap_or_else(|| get_service_display_name(&service));
                    sub_groups.entry(game_name).or_default().push(entry);
                }
                for (game_name, sub_entries) in sub_groups {
                    resolved_groups.entry((game_name, client_ip.clone())).or_default().extend(sub_entries);
                }
            } else if service.contains("wsus") || service.contains("xboxlive") {
                // Xbox / Microsoft Store content reaches the cache two ways: Delivery-Optimization
                // CLIENT traffic tagged `wsus` (shared with generic Windows Update / Office /
                // Defender) over /filestreamingservice/files/<GUID>, and prefill-daemon traffic
                // tagged `xboxlive` pulled direct from assets1.xboxlive.com. Sub-group each entry by
                // the Xbox title resolved from a stored XboxCdnPattern.UrlFragment (like Epic);
                // entries that match no Xbox fragment are generic Windows Update / Xbox Live and fall
                // back to the service label, so real OS updates never get mislabeled as a game. This
                // mirrors log_processor's is_xbox_cache_service guard.
                let mut sub_groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
                for entry in entries {
                    let game_name = self.lookup_xbox_game(&entry.request_url).await
                        .unwrap_or_else(|| get_service_display_name(&service));
                    sub_groups.entry(game_name).or_default().push(entry);
                }
                for (game_name, sub_entries) in sub_groups {
                    resolved_groups.entry((game_name, client_ip.clone())).or_default().extend(sub_entries);
                }
            } else {
                let display_name = get_service_display_name(&service);
                resolved_groups.entry((display_name, client_ip)).or_default().extend(entries);
            }
        }

        for ((game_name, client_ip), entries) in resolved_groups {
            let service = entries.first().map(|e| e.service.clone()).unwrap_or_default();
            game_speeds.push(build_game_speed_info(entries, 0, client_ip, service, Some(game_name), None, speed_divisor));
        }

        // Sort by speed descending
        game_speeds.sort_by(|a, b| b.bytes_per_second.partial_cmp(&a.bytes_per_second).unwrap_or(std::cmp::Ordering::Equal));

        // Client speeds from the per-client aggregates computed before the grouping.
        let mut client_speeds: Vec<ClientSpeedInfo> = client_aggregates.into_iter()
            .map(|(client_ip, (total_bytes, cache_hit_bytes))| {
                let cache_miss_bytes = total_bytes - cache_hit_bytes;
                // Count active games as this client's rows in the collapsed game_speeds
                // list, so the client card agrees with the games list (counting raw
                // depot IDs would show one multi-depot game as several games).
                let active_games = game_speeds.iter().filter(|g| g.client_ip == client_ip).count();

                ClientSpeedInfo {
                    client_ip,
                    bytes_per_second: total_bytes as f64 / speed_divisor,
                    total_bytes,
                    active_games,
                    cache_hit_bytes,
                    cache_miss_bytes,
                }
            })
            .collect();

        client_speeds.sort_by(|a, b| b.bytes_per_second.partial_cmp(&a.bytes_per_second).unwrap_or(std::cmp::Ordering::Equal));

        DownloadSpeedSnapshot {
            timestamp_utc: now.format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string(),
            total_bytes_per_second,
            game_speeds,
            client_speeds,
            window_seconds: window_secs,
            entries_in_window: entries_count,
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
             ORDER BY LENGTH(p.\"ChunkBaseUrl\") DESC"
        )
        .fetch_all(&self.pool)
        .await;

        match result {
            Ok(rows) => {
                self.epic_patterns = rows.iter()
                    .filter_map(|row| {
                        let chunk_base_url: Option<String> = row.get("ChunkBaseUrl");
                        let game_name: Option<String> = row.get("GameName");
                        match (chunk_base_url, game_name) {
                            (Some(url), Some(name)) => Some((url.trim_end_matches('/').to_string(), name)),
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
        let result = self.epic_patterns.iter()
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
             ORDER BY LENGTH(p.\"UrlFragment\") DESC"
        )
        .fetch_all(&self.pool)
        .await;

        match result {
            Ok(rows) => {
                self.xbox_patterns = rows.iter()
                    .filter_map(|row| {
                        let fragment: Option<String> = row.get("UrlFragment");
                        let title: Option<String> = row.get("Title");
                        match (fragment, title) {
                            // Keep ONLY well-formed /filestreamingservice/files/<GUID> fragments,
                            // using the SAME shared shape guard as log_processor (the primary
                            // canonicalizer) and the C# resolver (XboxMappingService.IsValidFragment).
                            // A malformed / short / non-GUID fragment would `contains()`-match generic
                            // wsus URLs and relabel Windows Update traffic as a game.
                            (Some(frag), Some(name)) if cache_utils::is_valid_xbox_fragment(&frag) => {
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
        let result = self.xbox_patterns.iter()
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

/// DB-free headline aggregates over the rolling window: total bytes/second, the in-window entry
/// count, and whether any download is active. `calculate_snapshot` derives its
/// `total_bytes_per_second`, `entries_in_window`, and `has_active_downloads` fields from exactly
/// this (no depot/game database lookup), so it is the real seam the streaming tail path is verified
/// through without a live pool.
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
    let cache_hit_bytes: i64 = entries.iter().filter(|e| e.is_cache_hit).map(|e| e.bytes_sent).sum();
    let cache_miss_bytes = total_bytes - cache_hit_bytes;
    let cache_hit_percent = if total_bytes > 0 {
        (cache_hit_bytes as f64 / total_bytes as f64) * 100.0
    } else {
        0.0
    };

    GameSpeedInfo {
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
                let mut sibling_depots: Vec<u32> = entries.iter().filter_map(|e| e.depot_id).collect();
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
            let service = entries.first().map(|e| e.service.clone()).unwrap_or_default();
            build_game_speed_info(entries, rep_depot_id, client_ip, service, game_name, game_app_id, speed_divisor)
        })
        .collect()
}

/// Pick the representative depot for a collapsed group: the depot that contributed the
/// most bytes in the window (deterministic tie-break on the smaller depot_id), kept for
/// the DTO/UI which still carries a single `depot_id`. Returns 0 if no entry carries a
/// depot (matches the placeholder used for the non-depot path).
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
        eprintln!("  log_dir: Path to a datasource log directory. Every log source inside it");
        eprintln!("           (a monolithic access.log and/or per-service bare-metal *-access.log");
        eprintln!("           files) is discovered and tailed; access.log is not assumed.");
        eprintln!();
        eprintln!("Database connection is configured via DATABASE_URL environment variable.");
        eprintln!("Outputs JSON speed snapshots to stdout every {}ms", BROADCAST_INTERVAL_MS);
        eprintln!("Uses a rolling window sized to each log's delivery cadence (min {}s)", WINDOW_SECONDS);
        // No ProgressReporter/envelope here by design (this bin is a continuous snapshot
        // stream, not a discrete lifecycle operation - see emit_json_line docs). Returning
        // Err (instead of process::exit(1)) still surfaces the fatal reason: anyhow's
        // default main Termination prints "Error: {:#}" to stderr and exits 1.
        anyhow::bail!("missing required <log_dir> argument(s)");
    }

    // Discover the concrete current files to tail across every datasource directory. A directory
    // may hold a monolithic access.log and/or per-service bare-metal logs; the Rust side owns
    // discovery so C# only has to pass the datasource directory.
    let dirs: Vec<PathBuf> = args[1..].iter().map(PathBuf::from).collect();
    let sources = discover_tracked_sources(&dirs);

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
        headline_aggregates, replace_pattern_lookup_cache, SourceKind, SpeedLogEntry, SpeedTracker,
        TrackedSource, MAX_POLL_BYTES, WINDOW_SECONDS,
    };
    use chrono::{Duration, NaiveDateTime, Utc};
    use sqlx::postgres::PgPoolOptions;
    use std::collections::HashMap;
    use std::io::Write;

    // A minimal in-window Steam entry; the collapse logic only reads client_ip, depot_id,
    // bytes_sent and service, so the rest use inert defaults.
    fn steam_entry(client_ip: &str, depot_id: u32, bytes: i64) -> SpeedLogEntry {
        SpeedLogEntry {
            timestamp: Utc::now().naive_utc(),
            client_ip: client_ip.to_string(),
            service: "steam".to_string(),
            depot_id: Some(depot_id),
            bytes_sent: bytes,
            is_cache_hit: false,
            request_url: String::new(),
            cdn_host: None,
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
        groups.insert((1001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1001, 1000)]);
        groups.insert((1002, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1002, 2000)]);

        // PRE-FIX behavior (original :415-423): one row per bucket => the duplicate bug.
        let pre_fix: Vec<_> = groups
            .clone()
            .into_iter()
            .map(|((depot_id, client_ip), entries)| {
                let (name, app) = cs2_resolver(depot_id);
                build_game_speed_info(entries, depot_id, client_ip, "steam".to_string(), name, app, WINDOW_SECONDS as f64)
            })
            .collect();
        assert_eq!(pre_fix.len(), 2, "pre-fix: two depots of one game render as two rows");

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
        groups.insert((5001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 5001, 500)]);
        groups.insert((5002, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 5002, 500)]);

        let rows = collapse_depot_groups(groups, none_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 2, "two unknown depots must not merge");
        assert!(rows.iter().all(|r| r.game_app_id.is_none()));
    }

    #[test]
    fn same_app_different_clients_stay_separate() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        groups.insert((1001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1001, 1000)]);
        groups.insert((1001, "10.0.0.2".to_string()), vec![steam_entry("10.0.0.2", 1001, 1000)]);

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
        groups.insert((1001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1001, 1000)]);
        groups.insert((1002, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1002, 2000)]);

        let rows = collapse_depot_groups(groups, partial_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 1, "partial resolution must not split one game across rows");
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
        groups.insert((1001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1001, 500)]);
        groups.insert((1002, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1002, 500)]);

        let rows = collapse_depot_groups(groups, cs2_resolver, WINDOW_SECONDS as f64);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].depot_id, 1001, "equal bytes tie-breaks to the smaller depot id");
    }

    #[test]
    fn representative_depot_is_the_highest_byte_depot() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        // 1001 contributes far more bytes than 1002, so it must be the representative.
        groups.insert((1001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1001, 9000)]);
        groups.insert((1002, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1002, 100)]);

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

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
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

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
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

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
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

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
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

    // A buffered per-service log delivers several seconds of history in one flush. Against the
    // fixed 2s window that burst is entirely in the past, so the window empties and the source
    // reads inactive (the 1->0 flicker). The adaptive window must widen to cover the delivery span
    // so the source stays active, and speed must divide by observed coverage. Pre-fix, the window
    // is fixed at 2s and effective_window_secs does not exist, so the widening and active
    // assertions here fail; post-fix they pass.
    #[tokio::test]
    async fn buffered_source_window_widens_to_cover_flush_gap() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());

        // Seed to EOF (reads nothing, records no cadence).
        tracker.read_new_entries(&tracked[0]).unwrap();

        // One flush delivering a burst whose log timestamps span 4 seconds (7s..3s in the past), all
        // at once. Timestamps are anchored to a fixed reference so the assertions do not race the
        // wall clock.
        let base = Utc::now().naive_utc();
        let bytes_each = 1000i64;
        let mut burst = String::new();
        for secs in [7i64, 6, 5, 4, 3] {
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

        // Delivery span 4s > base window, so the window widens: ceil(4 + WINDOW_SECONDS) = 6.
        let w = tracker.effective_window_secs();
        assert_eq!(w, 6, "a 4s delivery span widens the window to cover the flush gap");

        // Fixed 2s window: the burst was delivered entirely in the past, so the window is empty and
        // the source reads inactive - exactly the flicker this change removes.
        let fixed_start = base - Duration::seconds(WINDOW_SECONDS);
        let (_, _, fixed_active) =
            headline_aggregates(&tracker.entries, fixed_start, WINDOW_SECONDS as f64);
        assert!(!fixed_active, "the fixed 2s window empties between buffered flushes");

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

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
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

    // Two deliveries separated by a long idle gap (far larger than the retention horizon). The
    // inter-delivery advance measures idleness, not delivery cadence, so it must be discarded: the
    // window must stay at WINDOW_SECONDS. Without the idle gate the 40s advance would pin it wide.
    #[tokio::test]
    async fn idle_gap_does_not_widen_window() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());
        tracker.read_new_entries(&tracked[0]).unwrap();

        let base = Utc::now().naive_utc();

        // First delivery: a single line (zero intra-batch span).
        append_bytes(
            &steam,
            detailed_steam_line_at("10.0.0.2", 654321, 1000, "MISS", base - Duration::seconds(40))
                .as_bytes(),
        );
        tracker.read_new_entries(&tracked[0]).unwrap();

        // Second delivery after a long gap: a single line 40s later. Its inter-delivery advance is
        // far beyond the retention horizon and must not be read as slow delivery cadence.
        append_bytes(
            &steam,
            detailed_steam_line_at("10.0.0.2", 654321, 1000, "MISS", base).as_bytes(),
        );
        tracker.read_new_entries(&tracked[0]).unwrap();

        assert_eq!(
            tracker.effective_window_secs(),
            WINDOW_SECONDS,
            "an idle gap between deliveries must not be mistaken for slow delivery cadence"
        );
    }

    // Daily logrotate replaces the file with a smaller one but leaves nginx's flush configuration
    // unchanged, so the learned delivery cadence must survive rotation - otherwise the first
    // download after rotation re-learns from scratch and re-flickers.
    #[tokio::test]
    async fn rotation_preserves_learned_cadence() {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path();
        let steam = dir.join("steam-access.log");
        std::fs::write(&steam, b"").unwrap();

        let tracked = discover_tracked_sources(&[dir.to_path_buf()]);
        assert_eq!(tracked.len(), 1);
        let mut tracker = lazy_tracker(tracked.clone());
        tracker.read_new_entries(&tracked[0]).unwrap();

        // Teach a multi-second delivery cadence with one buffered burst.
        let base = Utc::now().naive_utc();
        let mut burst = String::new();
        for secs in [7i64, 6, 5, 4, 3] {
            burst.push_str(&detailed_steam_line_at(
                "10.0.0.2",
                654321,
                1000,
                "MISS",
                base - Duration::seconds(secs),
            ));
        }
        append_bytes(&steam, burst.as_bytes());
        tracker.read_new_entries(&tracked[0]).unwrap();

        let learned = tracker
            .file_positions
            .get(&tracked[0].path)
            .expect("source state is present after a delivery")
            .measured_cadence();
        assert!(
            learned > WINDOW_SECONDS as f64,
            "the burst taught a cadence above the base window"
        );

        // Rotate: replace the file with a smaller (empty) one. The rotation branch resets file
        // offsets but must carry the cadence ring over, and the empty file appends no new delivery.
        std::fs::write(&steam, b"").unwrap();
        tracker.read_new_entries(&tracked[0]).unwrap();

        let after = tracker
            .file_positions
            .get(&tracked[0].path)
            .expect("source state survives rotation")
            .measured_cadence();
        assert_eq!(
            after, learned,
            "rotation resets file positions but preserves the learned cadence"
        );
    }
}
