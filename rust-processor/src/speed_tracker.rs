use anyhow::Result;
use chrono::{NaiveDateTime, Utc};
use chrono_tz::Tz;
use regex::Regex;
use sqlx::PgPool;
use sqlx::Row;
use serde::Serialize;
use std::collections::{HashMap, VecDeque};
use std::env;
use std::fs::File;
use std::io::{BufRead, BufReader, Seek, SeekFrom};
use std::path::PathBuf;
use std::time::{Duration, Instant};

mod cache_utils;
mod db;
mod progress_events;
mod riot_hosts;
mod service_utils;
mod tact_products;

// Configuration
const WINDOW_SECONDS: i64 = 2;
const BROADCAST_INTERVAL_MS: u64 = 500;
const POLL_INTERVAL_MS: u64 = 100;

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

struct LogParser {
    main_regex: Regex,
    depot_regex: Regex,
    local_tz: Tz,
}

impl LogParser {
    fn new(local_tz: Tz) -> Self {
        let main_regex = Regex::new(
            r#"^(?:\[(?P<service>[^\]]+)\]\s+)?(?P<ip>\S+)\s+/\s+-\s+-\s+-\s+\[(?P<time>[^\]]+)\]\s+"(?P<method>[A-Z]+)\s+(?P<url>\S+)(?:\s+HTTP/(?P<httpVersion>[^"\s]+))?"\s+(?P<status>\d{3})\s+(?P<bytes>-|\d+)(?P<rest>.*)$"#
        ).unwrap();

        let depot_regex = Regex::new(r"/depot/(\d+)/").unwrap();

        Self {
            main_regex,
            depot_regex,
            local_tz,
        }
    }

    fn parse_line(&self, line: &str) -> Option<SpeedLogEntry> {
        let captures = self.main_regex.captures(line)?;

        let service = captures
            .name("service")
            .map(|m| service_utils::normalize_service_name(m.as_str()))
            .unwrap_or_else(|| "unknown".to_string());

        let client_ip = captures.name("ip")?.as_str().to_string();
        let time_str = captures.name("time")?.as_str();
        let url = captures.name("url")?.as_str();

        if service_utils::should_skip_url(url) {
            return None;
        }

        let bytes_str = captures.name("bytes")?.as_str();
        let bytes_sent = if bytes_str == "-" {
            return None; // Skip entries with no bytes
        } else {
            bytes_str.parse::<i64>().ok()?
        };

        if bytes_sent <= 0 {
            return None;
        }

        let rest = captures.name("rest").map(|m| m.as_str()).unwrap_or("");

        // Manager Status Check probes tag themselves; never show them as live activity.
        if service_utils::is_manager_probe(rest) {
            return None;
        }

        let timestamp = self.parse_timestamp(time_str)?;
        let cache_status = self.extract_cache_status(rest);
        let is_cache_hit = cache_status == "HIT";

        let depot_id = self.depot_regex
            .captures(url)
            .and_then(|cap| cap.get(1))
            .and_then(|m| m.as_str().parse::<u32>().ok());

        // Riot CDN host (4th quoted field, $host) — only the riot service needs it.
        let cdn_host = if service == "riot" {
            let host = self.extract_quoted_field(rest, 4);
            (!host.is_empty()).then(|| host.to_lowercase())
        } else {
            None
        };

        Some(SpeedLogEntry {
            timestamp,
            client_ip,
            service,
            depot_id,
            bytes_sent,
            is_cache_hit,
            request_url: url.to_string(),
            cdn_host,
        })
    }

    fn parse_timestamp(&self, time_str: &str) -> Option<NaiveDateTime> {
        let (time_without_tz, tz_offset) = if let Some(pos) = time_str.rfind(['+', '-']) {
            let tz_str = time_str[pos..].trim();
            let offset = if tz_str.len() >= 5 {
                let sign = if tz_str.starts_with('-') { -1 } else { 1 };
                let hours: i32 = tz_str[1..3].parse().ok()?;
                let minutes: i32 = tz_str[3..5].parse().ok()?;
                Some(sign * (hours * 3600 + minutes * 60))
            } else {
                None
            };
            (time_str[..pos].trim(), offset)
        } else {
            (time_str, None)
        };

        if let Ok(naive_dt) = NaiveDateTime::parse_from_str(time_without_tz, "%d/%b/%Y:%H:%M:%S") {
            return Some(self.convert_to_utc(naive_dt, tz_offset));
        }

        None
    }

    fn convert_to_utc(&self, naive_dt: NaiveDateTime, tz_offset_secs: Option<i32>) -> NaiveDateTime {
        use chrono::{FixedOffset, TimeZone, Utc};

        if let Some(offset_secs) = tz_offset_secs {
            if let Some(offset) = FixedOffset::east_opt(offset_secs) {
                if let Some(dt_with_tz) = offset.from_local_datetime(&naive_dt).earliest() {
                    return dt_with_tz.with_timezone(&Utc).naive_utc();
                }
            }
        }

        if let Some(local_dt) = self.local_tz.from_local_datetime(&naive_dt).earliest() {
            return local_dt.with_timezone(&Utc).naive_utc();
        }

        naive_dt
    }

    fn extract_cache_status(&self, rest: &str) -> String {
        let mut quote_count = 0;
        let mut start_idx = None;

        for (i, ch) in rest.char_indices() {
            if ch == '"' {
                quote_count += 1;
                if quote_count == 5 {
                    start_idx = Some(i + 1);
                } else if quote_count == 6 {
                    if let Some(start) = start_idx {
                        let status = &rest[start..i];
                        if status == "HIT" || status == "MISS" {
                            return status.to_string();
                        }
                    }
                    break;
                }
            }
        }

        "UNKNOWN".to_string()
    }

    /// Extract the Nth quoted field from the rest string (1-indexed).
    /// Returns empty string if the field doesn't exist or is "-".
    /// (Mirrors `parser.rs::extract_quoted_field`; field 4 = the `$host` value.)
    fn extract_quoted_field(&self, rest: &str, field_number: usize) -> String {
        let target_open = (field_number - 1) * 2 + 1; // Quote that opens the field
        let target_close = target_open + 1; // Quote that closes the field
        let mut quote_count = 0usize;
        let mut start_idx = None;

        for (i, ch) in rest.char_indices() {
            if ch == '"' {
                quote_count += 1;
                if quote_count == target_open {
                    start_idx = Some(i + 1);
                } else if quote_count == target_close {
                    if let Some(start) = start_idx {
                        let value = &rest[start..i];
                        if value == "-" {
                            return String::new();
                        }
                        return value.to_string();
                    }
                    break;
                }
            }
        }

        String::new()
    }
}

struct SpeedTracker {
    pool: PgPool,
    log_paths: Vec<PathBuf>,
    parser: LogParser,
    entries: VecDeque<SpeedLogEntry>,
    depot_cache: HashMap<u32, (Option<String>, Option<u32>)>, // depot_id -> (game_name, game_app_id)
    file_positions: HashMap<PathBuf, u64>,
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
    fn new(pool: PgPool, log_paths: Vec<PathBuf>) -> Self {
        let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
        let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);

        Self {
            pool,
            log_paths,
            parser: LogParser::new(local_tz),
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
        eprintln!("SpeedTracker started - monitoring {} log file(s)", self.log_paths.len());

        // Initialize file positions to end of files
        for log_path in &self.log_paths {
            if log_path.exists() {
                if let Ok(metadata) = std::fs::metadata(log_path) {
                    self.file_positions.insert(log_path.clone(), metadata.len());
                    eprintln!("Initialized {} at position {}", log_path.display(), metadata.len());
                }
            }
        }

        let mut last_broadcast = Instant::now();

        loop {
            // Read new entries from all log files
            for log_path in self.log_paths.clone() {
                if let Err(e) = self.read_new_entries(&log_path) {
                    eprintln!("Error reading {}: {}", log_path.display(), e);
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

    fn read_new_entries(&mut self, log_path: &PathBuf) -> Result<()> {
        if !log_path.exists() {
            return Ok(());
        }

        let metadata = std::fs::metadata(log_path)?;
        let current_size = metadata.len();
        let mut last_position = *self.file_positions.get(log_path).unwrap_or(&0);

        // Handle file rotation (new file is smaller)
        if current_size < last_position {
            eprintln!("Log file rotated: {}", log_path.display());
            self.file_positions.insert(log_path.clone(), 0);
            last_position = 0;
        }

        // No new data
        if current_size == last_position {
            return Ok(());
        }

        // Read new lines
        let file = File::open(log_path)?;
        let mut reader = BufReader::new(file);
        reader.seek(SeekFrom::Start(last_position))?;

        let mut new_position = last_position;
        let mut line = String::new();

        while reader.read_line(&mut line)? > 0 {
            new_position = reader.stream_position()?;

            let trimmed = line.trim();
            if !trimmed.is_empty() {
                if let Some(entry) = self.parser.parse_line(trimmed) {
                    self.entries.push_back(entry);
                }
            }
            line.clear();
        }

        self.file_positions.insert(log_path.clone(), new_position);
        Ok(())
    }

    fn clean_old_entries(&mut self) {
        let cutoff = Utc::now().naive_utc() - chrono::Duration::seconds(WINDOW_SECONDS);

        self.entries.retain(|entry| entry.timestamp >= cutoff);
    }

    async fn calculate_snapshot(&mut self) -> DownloadSpeedSnapshot {
        let now = Utc::now();
        let window_start = now.naive_utc() - chrono::Duration::seconds(WINDOW_SECONDS);

        // Clone entries within window to avoid borrow issues
        let window_entries: Vec<SpeedLogEntry> = self.entries.iter()
            .filter(|e| e.timestamp >= window_start)
            .cloned()
            .collect();

        // Whole-window aggregates and the per-client aggregation read from window_entries
        // BEFORE the grouping below consumes it by move, so this snapshot clones every
        // windowed entry exactly once (it used to clone each entry up to three times, every
        // 500ms broadcast).
        let entries_count = window_entries.len();
        let total_bytes: i64 = window_entries.iter().map(|e| e.bytes_sent).sum();

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
        });

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
            game_speeds.push(build_game_speed_info(entries, 0, client_ip, service, Some(game_name), None));
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
                    bytes_per_second: total_bytes as f64 / WINDOW_SECONDS as f64,
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
            total_bytes_per_second: total_bytes as f64 / WINDOW_SECONDS as f64,
            game_speeds,
            client_speeds,
            window_seconds: WINDOW_SECONDS,
            entries_in_window: entries_count,
            has_active_downloads: entries_count > 0,
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
                self.epic_cdn_cache.clear(); // Clear cache when patterns reload
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
                self.xbox_cdn_cache.clear(); // Clear cache when patterns reload
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

/// Build a GameSpeedInfo from a group of log entries
fn build_game_speed_info(
    entries: Vec<SpeedLogEntry>,
    depot_id: u32,
    client_ip: String,
    service: String,
    game_name: Option<String>,
    game_app_id: Option<u32>,
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
        bytes_per_second: total_bytes as f64 / WINDOW_SECONDS as f64,
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
            build_game_speed_info(entries, rep_depot_id, client_ip, service, game_name, game_app_id)
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
        eprintln!("Usage: {} <log_path> [log_path2] ...", args[0]);
        eprintln!("  log_path: Path to log directory (will monitor access.log)");
        eprintln!("");
        eprintln!("Database connection is configured via DATABASE_URL environment variable.");
        eprintln!("Outputs JSON speed snapshots to stdout every {}ms", BROADCAST_INTERVAL_MS);
        eprintln!("Uses a {}-second rolling window", WINDOW_SECONDS);
        std::process::exit(1);
    }

    // Build log paths from all provided directories
    let log_paths: Vec<PathBuf> = args[1..].iter()
        .map(|dir| PathBuf::from(dir).join("access.log"))
        .collect();

    let pool = db::create_pool().await?;
    let mut tracker = SpeedTracker::new(pool, log_paths);
    tracker.run().await
}

#[cfg(test)]
mod tests {
    use super::{build_game_speed_info, collapse_depot_groups, SpeedLogEntry, WINDOW_SECONDS};
    use chrono::Utc;
    use std::collections::HashMap;

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
                build_game_speed_info(entries, depot_id, client_ip, "steam".to_string(), name, app)
            })
            .collect();
        assert_eq!(pre_fix.len(), 2, "pre-fix: two depots of one game render as two rows");

        // POST-FIX behavior: collapsed to a single row with combined throughput.
        let rows = collapse_depot_groups(groups, cs2_resolver);
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

        let rows = collapse_depot_groups(groups, none_resolver);
        assert_eq!(rows.len(), 2, "two unknown depots must not merge");
        assert!(rows.iter().all(|r| r.game_app_id.is_none()));
    }

    #[test]
    fn same_app_different_clients_stay_separate() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        groups.insert((1001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1001, 1000)]);
        groups.insert((1001, "10.0.0.2".to_string()), vec![steam_entry("10.0.0.2", 1001, 1000)]);

        let rows = collapse_depot_groups(groups, cs2_resolver);
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

        let rows = collapse_depot_groups(groups, partial_resolver);
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

        let rows = collapse_depot_groups(groups, cs2_resolver);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].depot_id, 1001, "equal bytes tie-breaks to the smaller depot id");
    }

    #[test]
    fn representative_depot_is_the_highest_byte_depot() {
        let mut groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        // 1001 contributes far more bytes than 1002, so it must be the representative.
        groups.insert((1001, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1001, 9000)]);
        groups.insert((1002, "10.0.0.1".to_string()), vec![steam_entry("10.0.0.1", 1002, 100)]);

        let rows = collapse_depot_groups(groups, cs2_resolver);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].depot_id, 1001);
    }
}
