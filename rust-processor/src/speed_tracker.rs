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

mod db;
mod service_utils;

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

        let timestamp = self.parse_timestamp(time_str)?;
        let cache_status = self.extract_cache_status(rest);
        let is_cache_hit = cache_status == "HIT";

        let depot_id = self.depot_regex
            .captures(url)
            .and_then(|cap| cap.get(1))
            .and_then(|m| m.as_str().parse::<u32>().ok());

        Some(SpeedLogEntry {
            timestamp,
            client_ip,
            service,
            depot_id,
            bytes_sent,
            is_cache_hit,
            request_url: url.to_string(),
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
}

struct SpeedTracker {
    pool: PgPool,
    log_paths: Vec<PathBuf>,
    parser: LogParser,
    entries: VecDeque<SpeedLogEntry>,
    depot_cache: HashMap<u32, (Option<String>, Option<u32>)>, // depot_id -> (game_name, game_app_id)
    file_positions: HashMap<PathBuf, u64>,
    epic_cdn_cache: HashMap<String, Option<String>>, // url -> game_name (None = no match)
    epic_patterns: Vec<(String, String)>,            // (ChunkBaseUrl trimmed, GameName)
    last_epic_pattern_load: Option<Instant>,
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

                // Output JSON to stdout (C# will read this)
                if let Ok(json) = serde_json::to_string(&snapshot) {
                    println!("{}", json);
                }

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

        // Group by depot + client for game speeds (Steam and other services with depot IDs)
        let mut depot_groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        // Group by service + client for non-depot entries (Epic, Origin, etc.)
        let mut service_groups: HashMap<(String, String), Vec<SpeedLogEntry>> = HashMap::new();

        for entry in &window_entries {
            if let Some(depot_id) = entry.depot_id {
                depot_groups.entry((depot_id, entry.client_ip.clone())).or_default().push(entry.clone());
            } else {
                service_groups.entry((entry.service.clone(), entry.client_ip.clone())).or_default().push(entry.clone());
            }
        }

        // Pre-populate depot cache for game name lookups
        let depot_ids: Vec<u32> = depot_groups.keys().map(|(id, _)| *id).collect();
        for depot_id in depot_ids {
            self.lookup_depot(depot_id).await;
        }

        // Build game speeds from depot groups (Steam and services with depot IDs)
        let mut game_speeds: Vec<GameSpeedInfo> = depot_groups.into_iter()
            .map(|((depot_id, client_ip), entries)| {
                let (game_name, game_app_id) = self.depot_cache.get(&depot_id)
                    .cloned()
                    .unwrap_or((None, None));
                let service = entries.first().map(|e| e.service.clone()).unwrap_or_default();
                build_game_speed_info(entries, depot_id, client_ip, service, game_name, game_app_id)
            })
            .collect();

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

        // Group by client for client speeds (all entries with actual data)
        let mut client_groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
        for entry in &window_entries {
            client_groups.entry(entry.client_ip.clone()).or_default().push(entry.clone());
        }

        let mut client_speeds: Vec<ClientSpeedInfo> = client_groups.into_iter()
            .map(|(client_ip, entries)| {
                let total_bytes: i64 = entries.iter().map(|e| e.bytes_sent).sum();
                let cache_hit_bytes: i64 = entries.iter().filter(|e| e.is_cache_hit).map(|e| e.bytes_sent).sum();
                let cache_miss_bytes = total_bytes - cache_hit_bytes;
                // Count active games: unique depot IDs + unique non-depot services
                let depot_count = entries.iter()
                    .filter_map(|e| e.depot_id)
                    .collect::<std::collections::HashSet<_>>()
                    .len();
                let service_count = entries.iter()
                    .filter(|e| e.depot_id.is_none())
                    .map(|e| e.service.as_str())
                    .collect::<std::collections::HashSet<_>>()
                    .len();
                let active_games = depot_count + service_count;

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

        let entries_count = window_entries.len();
        let total_bytes: i64 = window_entries.iter().map(|e| e.bytes_sent).sum();

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
        if let Some(cached) = self.epic_cdn_cache.get(url) {
            return cached.clone();
        }

        // Reload patterns if needed
        self.load_epic_patterns().await;

        // Match URL against patterns (longest first for most specific match)
        let result = self.epic_patterns.iter()
            .find(|(chunk_base, _)| url.contains(chunk_base.as_str()))
            .map(|(_, name)| name.clone());

        // Cache the result (including None for no match)
        self.epic_cdn_cache.insert(url.to_string(), result.clone());

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
