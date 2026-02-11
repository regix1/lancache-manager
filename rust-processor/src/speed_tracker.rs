use anyhow::Result;
use chrono::{NaiveDateTime, Utc};
use chrono_tz::Tz;
use regex::Regex;
use rusqlite::{params, Connection};
use serde::Serialize;
use std::collections::{HashMap, VecDeque};
use std::env;
use std::fs::File;
use std::io::{BufRead, BufReader, Seek, SeekFrom};
use std::path::PathBuf;
use std::thread;
use std::time::{Duration, Instant};

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
    db_path: PathBuf,
    log_paths: Vec<PathBuf>,
    parser: LogParser,
    entries: VecDeque<SpeedLogEntry>,
    depot_cache: HashMap<u32, (Option<String>, Option<u32>)>, // depot_id -> (game_name, game_app_id)
    file_positions: HashMap<PathBuf, u64>,
}

impl SpeedTracker {
    fn new(db_path: PathBuf, log_paths: Vec<PathBuf>) -> Self {
        let tz_str = env::var("TZ").unwrap_or_else(|_| "UTC".to_string());
        let local_tz: Tz = tz_str.parse().unwrap_or(chrono_tz::UTC);

        Self {
            db_path,
            log_paths,
            parser: LogParser::new(local_tz),
            entries: VecDeque::new(),
            depot_cache: HashMap::new(),
            file_positions: HashMap::new(),
        }
    }

    fn run(&mut self) -> Result<()> {
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
                let snapshot = self.calculate_snapshot();

                // Output JSON to stdout (C# will read this)
                if let Ok(json) = serde_json::to_string(&snapshot) {
                    println!("{}", json);
                }

                last_broadcast = Instant::now();
            }

            thread::sleep(Duration::from_millis(POLL_INTERVAL_MS));
        }
    }

    fn read_new_entries(&mut self, log_path: &PathBuf) -> Result<()> {
        if !log_path.exists() {
            return Ok(());
        }

        let metadata = std::fs::metadata(log_path)?;
        let current_size = metadata.len();
        let last_position = *self.file_positions.get(log_path).unwrap_or(&0);

        // Handle file rotation (new file is smaller)
        if current_size < last_position {
            eprintln!("Log file rotated: {}", log_path.display());
            self.file_positions.insert(log_path.clone(), 0);
            return Ok(());
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

        while let Some(entry) = self.entries.front() {
            if entry.timestamp < cutoff {
                self.entries.pop_front();
            } else {
                break;
            }
        }
    }

    fn calculate_snapshot(&mut self) -> DownloadSpeedSnapshot {
        let now = Utc::now();
        let window_start = now.naive_utc() - chrono::Duration::seconds(WINDOW_SECONDS);

        // Clone entries within window to avoid borrow issues
        let window_entries: Vec<SpeedLogEntry> = self.entries.iter()
            .filter(|e| e.timestamp >= window_start)
            .cloned()
            .collect();

        // Group by depot + client for game speeds
        let mut depot_groups: HashMap<(u32, String), Vec<SpeedLogEntry>> = HashMap::new();
        for entry in &window_entries {
            if let Some(depot_id) = entry.depot_id {
                depot_groups.entry((depot_id, entry.client_ip.clone())).or_default().push(entry.clone());
            }
        }

        // Collect depot IDs first, then lookup game info
        let depot_ids: Vec<u32> = depot_groups.keys().map(|(depot_id, _)| *depot_id).collect();
        for depot_id in depot_ids.into_iter().collect::<std::collections::HashSet<_>>() {
            self.lookup_depot(depot_id); // Pre-populate cache
        }

        let mut game_speeds: Vec<GameSpeedInfo> = depot_groups.into_iter()
            .map(|((depot_id, client_ip), entries)| {
                let total_bytes: i64 = entries.iter().map(|e| e.bytes_sent).sum();
                let cache_hit_bytes: i64 = entries.iter().filter(|e| e.is_cache_hit).map(|e| e.bytes_sent).sum();
                let cache_miss_bytes = total_bytes - cache_hit_bytes;
                let cache_hit_percent = if total_bytes > 0 {
                    (cache_hit_bytes as f64 / total_bytes as f64) * 100.0
                } else {
                    0.0
                };

                let (game_name, game_app_id) = self.depot_cache.get(&depot_id)
                    .cloned()
                    .unwrap_or((None, None));
                let service = entries.first().map(|e| e.service.clone()).unwrap_or_default();

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
            })
            .collect();

        // Sort by speed descending
        game_speeds.sort_by(|a, b| b.bytes_per_second.partial_cmp(&a.bytes_per_second).unwrap());

        // Group by client for client speeds
        let mut client_groups: HashMap<String, Vec<SpeedLogEntry>> = HashMap::new();
        for entry in &window_entries {
            client_groups.entry(entry.client_ip.clone()).or_default().push(entry.clone());
        }

        let mut client_speeds: Vec<ClientSpeedInfo> = client_groups.into_iter()
            .map(|(client_ip, entries)| {
                let total_bytes: i64 = entries.iter().map(|e| e.bytes_sent).sum();
                let cache_hit_bytes: i64 = entries.iter().filter(|e| e.is_cache_hit).map(|e| e.bytes_sent).sum();
                let cache_miss_bytes = total_bytes - cache_hit_bytes;
                let active_games = entries.iter()
                    .filter_map(|e| e.depot_id)
                    .collect::<std::collections::HashSet<_>>()
                    .len();

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

        client_speeds.sort_by(|a, b| b.bytes_per_second.partial_cmp(&a.bytes_per_second).unwrap());

        let total_bytes: i64 = window_entries.iter().map(|e| e.bytes_sent).sum();
        let entries_count = window_entries.len();
        let depot_entry_count = window_entries.iter().filter(|e| e.depot_id.is_some()).count();

        DownloadSpeedSnapshot {
            timestamp_utc: now.format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string(),
            total_bytes_per_second: total_bytes as f64 / WINDOW_SECONDS as f64,
            game_speeds,
            client_speeds,
            window_seconds: WINDOW_SECONDS,
            entries_in_window: entries_count,
            has_active_downloads: depot_entry_count > 0,
        }
    }

    fn lookup_depot(&mut self, depot_id: u32) -> (Option<String>, Option<u32>) {
        // Check cache first - only use cached value if we have a real name
        if let Some(cached) = self.depot_cache.get(&depot_id) {
            if cached.0.is_some() {
                return cached.clone();
            }
            // Fall through to re-query for missing names
        }

        // Lookup in database
        let result = self.lookup_depot_from_db(depot_id);

        // Only cache successful lookups (where we found a name)
        if result.0.is_some() {
            self.depot_cache.insert(depot_id, result.clone());
        }

        result
    }

    fn lookup_depot_from_db(&self, depot_id: u32) -> (Option<String>, Option<u32>) {
        let conn = match Connection::open(&self.db_path) {
            Ok(c) => c,
            Err(_) => return (None, None),
        };

        let result = conn.query_row(
            "SELECT AppId, AppName FROM SteamDepotMappings WHERE DepotId = ? AND IsOwner = 1 LIMIT 1",
            params![depot_id],
            |row| {
                let app_id: u32 = row.get(0)?;
                let app_name: Option<String> = row.get(1)?;
                Ok((app_name, Some(app_id)))
            }
        );

        result.unwrap_or((None, None))
    }
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();

    if args.len() < 3 {
        eprintln!("Usage: {} <db_path> <log_path> [log_path2] ...", args[0]);
        eprintln!("  db_path: Path to SQLite database");
        eprintln!("  log_path: Path to log directory (will monitor access.log)");
        eprintln!("");
        eprintln!("Outputs JSON speed snapshots to stdout every {}ms", BROADCAST_INTERVAL_MS);
        eprintln!("Uses a {}-second rolling window", WINDOW_SECONDS);
        std::process::exit(1);
    }

    let db_path = PathBuf::from(&args[1]);

    // Build log paths from all provided directories
    let log_paths: Vec<PathBuf> = args[2..].iter()
        .map(|dir| PathBuf::from(dir).join("access.log"))
        .collect();

    let mut tracker = SpeedTracker::new(db_path, log_paths);
    tracker.run()
}
