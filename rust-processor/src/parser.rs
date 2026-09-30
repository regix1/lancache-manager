use crate::log_layout::SourceKind;
use crate::models::LogEntry;
use crate::parser_http_detailed::HttpDetailedParser;
use crate::service_utils;
use crate::tact_products;
use chrono::{FixedOffset, NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use regex::Regex;
use serde::Deserialize;

#[derive(Deserialize)]
pub(crate) struct CachelogRecord {
    pub(crate) cache_identifier: String,
    remote_addr: String,
    time_local: String,
    method: String,
    path: String,
    status: String,
    bytes_sent: i64,
    user_agent: String,
    upstream_cache_status: String,
    host: String,
    http_range: String,
}

pub(crate) fn parse_cachelog_json(line: &str) -> Option<CachelogRecord> {
    let record: CachelogRecord = serde_json::from_str(line).ok()?;
    if record.cache_identifier.trim().is_empty()
        || record.remote_addr.is_empty()
        || record.remote_addr.chars().any(char::is_whitespace)
        || record.method.is_empty()
        || !record
            .method
            .chars()
            .all(|character| character.is_ascii_uppercase())
        || record.path.is_empty()
        || record.path.chars().any(char::is_whitespace)
        || record.status.len() != 3
        || !record.status.bytes().all(|byte| byte.is_ascii_digit())
        || record.bytes_sent < 0
    {
        return None;
    }

    Some(record)
}

pub struct LogParser {
    main_regex: Regex,
    depot_regex: Regex,
    local_tz: Tz,
}

impl LogParser {
    pub fn new(local_tz: Tz) -> Self {
        // The producer-owned forwarded field may contain spaces. The dash before remote_user is
        // the fixed cachelog separator and keeps this grammar record-scoped.
        let main_regex = Regex::new(
            r#"^(?:\[(?P<service>[^\]]+)\]\s+)?(?P<ip>\S+)\s+/\s+(?P<forwarded>.*?)\s+-\s+(?P<remoteUser>\S+)\s+\[(?P<time>[^\]]+)\]\s+"(?P<method>[A-Z]+)\s+(?P<url>\S+)(?:\s+HTTP/(?P<httpVersion>[^"\s]+))?"\s+(?P<status>\d{3})\s+(?P<bytes>-|\d+)(?P<rest>.*)$"#
        ).unwrap();

        let depot_regex = Regex::new(r"/depot/(\d+)/").unwrap();

        Self {
            main_regex,
            depot_regex,
            local_tz,
        }
    }

    pub(crate) fn normalize_url(url: &str) -> String {
        // Fast path: the overwhelming majority of URLs contain no consecutive
        // slashes, so skip the char-walk entirely when no "//" pair exists.
        if !url.as_bytes().windows(2).any(|pair| pair == b"//") {
            return url.to_string();
        }

        // Collapse consecutive slashes to a single slash
        // This handles cases where nginx logs record the same URL with double slashes
        // e.g., /filestreamingservice//files/... vs /filestreamingservice/files/...
        let mut result = String::with_capacity(url.len());
        let mut prev_was_slash = false;

        for ch in url.chars() {
            if ch == '/' {
                if !prev_was_slash {
                    result.push(ch);
                }
                prev_was_slash = true;
            } else {
                result.push(ch);
                prev_was_slash = false;
            }
        }

        result
    }

    pub fn parse_line(&self, line: &str) -> Option<LogEntry> {
        let trimmed = line.trim_start();
        let (
            service,
            client_ip,
            method,
            time_str,
            raw_url,
            status_code,
            bytes_served,
            cache_status,
            http_range,
            host,
            probe_fields,
        ) = if trimmed.starts_with('{') {
            let record = parse_cachelog_json(trimmed)?;
            let service = service_utils::normalize_service_name(&record.cache_identifier);
            let status_code = record.status.parse::<i32>().ok()?;
            let cache_status =
                if record.upstream_cache_status.is_empty() || record.upstream_cache_status == "-" {
                    "UNKNOWN".to_string()
                } else {
                    record.upstream_cache_status
                };
            let http_range = if record.http_range == "-" {
                String::new()
            } else {
                record.http_range
            };
            let host = if record.host == "-" {
                String::new()
            } else {
                record.host
            };

            (
                service,
                record.remote_addr,
                record.method,
                record.time_local,
                record.path,
                status_code,
                record.bytes_sent,
                cache_status,
                http_range,
                host,
                record.user_agent,
            )
        } else {
            let captures = self.main_regex.captures(line)?;
            let service = captures
                .name("service")
                .map(|capture| service_utils::normalize_service_name(capture.as_str()))
                .unwrap_or_else(|| "unknown".to_string());
            let rest = captures
                .name("rest")
                .map(|capture| capture.as_str())
                .unwrap_or("");
            let bytes_str = captures.name("bytes")?.as_str();
            let bytes_served = if bytes_str == "-" {
                0
            } else {
                bytes_str.parse::<i64>().ok()?
            };

            (
                service,
                captures.name("ip")?.as_str().to_string(),
                captures.name("method")?.as_str().to_string(),
                captures.name("time")?.as_str().to_string(),
                captures.name("url")?.as_str().to_string(),
                captures.name("status")?.as_str().parse::<i32>().ok()?,
                bytes_served,
                self.extract_cache_status(rest),
                self.extract_quoted_field(rest, 5),
                self.extract_quoted_field(rest, 4),
                rest.to_string(),
            )
        };

        // The manager's own Status Check probes (heartbeat + HTTPS-redirect) tag themselves
        // with a marker User-Agent; they are synthetic traffic and must never become
        // downloads, corruption evidence, or purge candidates.
        if service_utils::is_manager_probe(&probe_fields) {
            return None;
        }

        let timestamp = self.parse_timestamp(&time_str)?;
        let url = Self::normalize_url(&raw_url);

        // Extract depot ID for Steam service
        let service_lower = service.to_lowercase();
        let depot_id = if service_lower == "steam" {
            self.extract_depot_id(&url)
        } else {
            None
        };

        // Extract Blizzard TACT product code (segment after /tpr/) for the blizzard service.
        // Blizzard has no integer app id; the product code is the game discriminator.
        let tact_product = if service_lower == "blizzard" {
            tact_products::extract_tact_product(&url)
        } else {
            None
        };

        // Extract the Riot CDN host (access.log $host, the 4th quoted field) for the
        // riot service. Riot bundle URLs have no product slug, so the host subdomain
        // (lol/valorant/bacon) is the only game discriminator. Lowercased for stable
        // matching; None when absent ("-").
        let cdn_host = if service_lower == "riot" {
            (!host.is_empty()).then(|| host.to_lowercase())
        } else {
            None
        };

        Some(LogEntry {
            timestamp,
            client_ip,
            method,
            service,
            raw_url,
            url,
            status_code,
            bytes_served,
            cache_status,
            depot_id,
            tact_product,
            http_range,
            cdn_host,
        })
    }

    /// Host (`$host`, the 4th quoted tail field) and User-Agent (the 2nd quoted field) of a
    /// cachelog record, reusing the same `main_regex` and quoted-field extractor `parse_line`
    /// uses. The read-only content scan needs the request host for a DNS check, which the
    /// produced `LogEntry` only exposes (as `cdn_host`) for the riot service. Returns None when
    /// the line is not a cachelog record.
    #[allow(dead_code)] // used by the content scan in log_service_manager; other binaries share this module
    pub(crate) fn extract_host_and_user_agent(&self, line: &str) -> Option<(String, String)> {
        let trimmed = line.trim_start();
        if trimmed.starts_with('{') {
            let record = parse_cachelog_json(trimmed)?;
            let host = if record.host == "-" {
                String::new()
            } else {
                record.host
            };
            return Some((host, record.user_agent));
        }

        let captures = self.main_regex.captures(line)?;
        let rest = captures.name("rest").map(|m| m.as_str()).unwrap_or("");
        Some((
            self.extract_quoted_field(rest, 4),
            self.extract_quoted_field(rest, 2),
        ))
    }

    fn parse_timestamp(&self, time_str: &str) -> Option<NaiveDateTime> {
        parse_nginx_timestamp(time_str, self.local_tz)
    }

    /// Extract the Nth quoted field from the rest string (1-indexed).
    /// Returns empty string if the field doesn't exist or is "-".
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

    fn extract_cache_status(&self, rest: &str) -> String {
        // Preserve the literal 3rd quoted field. Detector eligibility is deliberately strict
        // (`MISS` or `HIT` depending on mode), so BYPASS/EXPIRED/STALE must never be collapsed
        // into a value that could later be mistaken for corruption evidence.
        let status = self.extract_quoted_field(rest, 3);
        if status.is_empty() {
            "UNKNOWN".to_string()
        } else {
            status
        }
    }

    fn extract_depot_id(&self, url: &str) -> Option<u32> {
        self.depot_regex
            .captures(url)
            .and_then(|cap| cap.get(1))
            .and_then(|m| m.as_str().parse::<u32>().ok())
    }
}

/// Parse either supported access-log format using the source attribution rules shared by
/// ingestion, purge, and corruption detection. An explicit cachelog service tag takes
/// precedence over a per-service filename hint.
#[allow(dead_code)] // some binaries share the parser module without dispatching both formats
pub(crate) fn parse_log_line(
    cachelog: &LogParser,
    detailed: &HttpDetailedParser,
    line: &str,
    source_kind: &SourceKind,
) -> Option<LogEntry> {
    if let Some(entry) = cachelog.parse_line(line) {
        return Some(entry);
    }

    match source_kind {
        SourceKind::Service(service) => detailed.parse_line(line, service),
        SourceKind::Monolithic | SourceKind::Fallback => None,
    }
}

/// Parse an nginx access-log timestamp (`dd/MMM/yyyy:HH:mm:ss [+-]HHMM` and the ISO-ish
/// fallbacks) into a UTC NaiveDateTime. Shared by every log parser so cachelog and
/// http-detailed records with the same timestamp produce the same instant.
pub(crate) fn parse_nginx_timestamp(time_str: &str, local_tz: Tz) -> Option<NaiveDateTime> {
    // Extract timezone offset if present (e.g., " -0600" or " +0000")
    let (time_without_tz, tz_offset) = if let Some(pos) = time_str.rfind(['+', '-']) {
        let tz_str = time_str[pos..].trim();
        // Parse timezone like "+0000" or "-0600"
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

    // Try format: dd/MMM/yyyy:HH:mm:ss (most common for nginx/lancache logs)
    if let Ok(naive_dt) = NaiveDateTime::parse_from_str(time_without_tz, "%d/%b/%Y:%H:%M:%S") {
        return Some(convert_to_utc(naive_dt, tz_offset, local_tz));
    }

    // Try format: yyyy-MM-dd HH:mm:ss
    if let Ok(naive_dt) = NaiveDateTime::parse_from_str(time_without_tz, "%Y-%m-%d %H:%M:%S") {
        return Some(convert_to_utc(naive_dt, tz_offset, local_tz));
    }

    // Try format: yyyy-MM-ddTHH:mm:ss
    if let Ok(naive_dt) = NaiveDateTime::parse_from_str(time_without_tz, "%Y-%m-%dT%H:%M:%S") {
        return Some(convert_to_utc(naive_dt, tz_offset, local_tz));
    }

    None
}

fn convert_to_utc(
    naive_dt: NaiveDateTime,
    tz_offset_secs: Option<i32>,
    local_tz: Tz,
) -> NaiveDateTime {
    if let Some(offset_secs) = tz_offset_secs {
        // Create a DateTime with the timezone offset
        if let Some(offset) = FixedOffset::east_opt(offset_secs) {
            if let Some(dt_with_tz) = offset.from_local_datetime(&naive_dt).earliest() {
                // Convert to UTC and return as NaiveDateTime
                return dt_with_tz.with_timezone(&Utc).naive_utc();
            }
        }
    }
    // If no timezone info, it's in local time - convert to UTC
    // The nginx log timestamp is in the server's local timezone
    if let Some(local_dt) = local_tz.from_local_datetime(&naive_dt).earliest() {
        return local_dt.with_timezone(&Utc).naive_utc();
    }
    // Fallback: assume UTC if conversion fails
    naive_dt
}

#[cfg(test)]
mod tests {
    use super::*;

    const DETAILED_LINE: &str = "[01/Jan/2024:00:00:00 +0000] 192.0.2.10 GET \"/depot/42/chunk/a\" - HTTP/1.1 200 \"-\" 512 1040 1024 0.005 1024 MISS cdn.test 200 0.004 \"Test\"";

    fn dispatch_parsers() -> (LogParser, HttpDetailedParser) {
        (
            LogParser::new(chrono_tz::UTC),
            HttpDetailedParser::new(chrono_tz::UTC),
        )
    }

    fn official_json() -> serde_json::Value {
        serde_json::json!({
            "cache_identifier": "steam",
            "remote_addr": "192.0.2.40",
            "time_local": "12/Sep/2026:18:22:34 +1000",
            "method": "GET",
            "path": "/depot/42/chunk/a",
            "status": "206",
            "bytes_sent": 2048,
            "user_agent": "Fixture/1.0",
            "upstream_cache_status": "MISS",
            "host": "cdn.example.test",
            "http_range": "bytes=0-2047",
            "timestamp": "2026-09-12T18:22:34+10:00",
            "forwarded_for": "198.51.100.9, 2001:db8::9",
            "remote_user": "fixture-user",
            "referer": "-",
            "proto": "HTTP/1.1",
            "scheme": "https",
            "unused_extra": true
        })
    }

    #[test]
    fn parse_log_line_prefers_explicit_cachelog_service() {
        let (cachelog, detailed) = dispatch_parsers();
        let line = "[epicgames] 192.0.2.10 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /Builds/object HTTP/1.1\" 200 1024 \"-\" \"Test\" \"MISS\" \"cdn.test\" \"-\"";

        let entry = parse_log_line(
            &cachelog,
            &detailed,
            line,
            &SourceKind::Service("steam".to_string()),
        )
        .expect("cachelog line");

        assert_eq!(entry.service, "epicgames");
    }

    #[test]
    fn parse_log_line_uses_service_hint_for_http_detailed() {
        let (cachelog, detailed) = dispatch_parsers();

        let entry = parse_log_line(
            &cachelog,
            &detailed,
            DETAILED_LINE,
            &SourceKind::Service("steam".to_string()),
        )
        .expect("http-detailed line");

        assert_eq!(entry.service, "steam");
        assert_eq!(entry.depot_id, Some(42));
    }

    #[test]
    fn parse_log_line_drops_hintless_http_detailed() {
        let (cachelog, detailed) = dispatch_parsers();

        assert!(
            parse_log_line(&cachelog, &detailed, DETAILED_LINE, &SourceKind::Monolithic,).is_none()
        );
    }

    #[test]
    fn normalize_url_fast_path_returns_input_unchanged() {
        let url = "/depot/123456/chunk/abcdef0123456789";
        assert_eq!(LogParser::normalize_url(url), url);
    }

    #[test]
    fn normalize_url_collapses_double_slashes() {
        assert_eq!(
            LogParser::normalize_url("/filestreamingservice//files/abc"),
            "/filestreamingservice/files/abc"
        );
    }

    #[test]
    fn normalize_url_collapses_runs_of_slashes() {
        assert_eq!(LogParser::normalize_url("///a//b////c/"), "/a/b/c/");
    }

    #[test]
    fn parse_line_normalizes_doubled_slash_url() {
        let parser = LogParser::new(chrono_tz::UTC);
        let line = "[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /depot/123456//chunk/abcdef HTTP/1.1\" 200 1024 \"-\" \"Valve/Steam\" \"HIT\" \"-\" \"-\"";
        let entry = parser.parse_line(line).expect("line should parse");
        assert_eq!(entry.url, "/depot/123456/chunk/abcdef");
        assert_eq!(entry.raw_url, "/depot/123456//chunk/abcdef");
        assert_eq!(entry.service, "steam");
        assert_eq!(entry.method, "GET");
        assert_eq!(entry.cache_status, "HIT");
    }

    #[test]
    fn parse_line_drops_manager_probe_lines_by_user_agent() {
        // The Status Check HTTPS-redirect probe: GET / with the marker UA. Must never parse
        // into an entry, or every sweep manufactures phantom downloads per service.
        let parser = LogParser::new(chrono_tz::UTC);
        let line = "[steam] 172.20.0.5 / - - - [01/Jan/2024:00:00:00 +0000] \"GET / HTTP/1.1\" 301 162 \"-\" \"lancache-manager-status-check/1.0\" \"MISS\" \"lancache.steamcontent.com\" \"-\"";
        assert!(parser.parse_line(line).is_none());
    }

    #[test]
    fn should_skip_url_covers_heartbeat_but_never_bare_root() {
        // Bare "/" stays visible: the manager must never hide lines it didn't generate.
        // Only the explicit probe User-Agent marker identifies manager traffic.
        assert!(service_utils::should_skip_url("/lancache-heartbeat"));
        assert!(!service_utils::should_skip_url("/"));
        assert!(!service_utils::should_skip_url("/depot/123/chunk/abc"));
    }

    #[test]
    fn parse_line_preserves_method_status_cache_status_and_range() {
        let parser = LogParser::new(chrono_tz::UTC);
        let line = "[wsus] 192.168.1.60 / - - - [01/Jan/2024:00:00:00 +0000] \"HEAD /content/file.bin HTTP/1.1\" 504 0 \"-\" \"BITS\" \"BYPASS\" \"download.windowsupdate.com\" \"bytes=1048576-2097151\"";
        let entry = parser.parse_line(line).expect("line should parse");

        assert_eq!(entry.method, "HEAD");
        assert_eq!(entry.status_code, 504);
        assert_eq!(entry.cache_status, "BYPASS");
        assert_eq!(entry.http_range, "bytes=1048576-2097151");
    }

    #[test]
    fn parse_line_keeps_literal_unknown_without_aliasing_other_statuses() {
        let parser = LogParser::new(chrono_tz::UTC);
        let unknown = "[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /depot/1/chunk/a HTTP/1.1\" 200 1 \"-\" \"Steam\" \"UNKNOWN\" \"host\" \"-\"";
        let missing = "[steam] 192.168.1.50 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /depot/1/chunk/a HTTP/1.1\" 200 1 \"-\" \"Steam\" \"-\" \"host\" \"-\"";

        assert_eq!(
            parser
                .parse_line(unknown)
                .expect("unknown line")
                .cache_status,
            "UNKNOWN"
        );
        assert_eq!(
            parser
                .parse_line(missing)
                .expect("missing line")
                .cache_status,
            "UNKNOWN"
        );
    }

    #[test]
    fn parse_line_accepts_populated_forwarded_and_remote_user_fields() {
        let parser = LogParser::new(chrono_tz::UTC);
        let cases = [
            (Some("xboxlive"), "192.0.2.40", "-", "-", "xboxlive"),
            (Some("steam"), "192.0.2.41", "198.51.100.9", "-", "steam"),
            (None, "192.0.2.42", "-", "fixture-user", "unknown"),
            (
                Some("blizzard"),
                "2001:db8::40",
                "2001:db8::9",
                "DOMAIN\\fixture",
                "blizzard",
            ),
            (
                Some("riot"),
                "192.0.2.43",
                "198.51.100.9, 2001:db8::9",
                "fixture-user",
                "riot",
            ),
            (
                Some("epicgames"),
                "2001:db8::43",
                "arbitrary malformed forwarded header text",
                "fixture-user",
                "epicgames",
            ),
        ];

        for (service, remote_addr, forwarded, remote_user, expected_service) in cases {
            let service_prefix = service
                .map(|service| format!("[{service}] "))
                .unwrap_or_default();
            let line = format!(
                "{service_prefix}{remote_addr} / {forwarded} - {remote_user} [12/Sep/2026:18:22:34 +1000] \"GET /content/{remote_addr} HTTP/1.1\" 200 1 \"-\" \"Fixture/1.0\" \"HIT\" \"cdn.example.test\" \"-\""
            );
            let entry = parser.parse_line(&line).expect("official text record");
            assert_eq!(entry.client_ip, remote_addr);
            assert_eq!(entry.service, expected_service);
        }
    }

    #[test]
    fn json_and_text_records_produce_equal_entries() {
        let parser = LogParser::new(chrono_tz::UTC);
        let cases = [
            ("steam", "/depot/42/chunk//a", "cdn.example.test"),
            ("blizzard", "/tpr/WoW/data/file", "blizzard.example.test"),
            (
                "riot",
                "/channels/public/bundles/hash.bundle",
                "VALORANT.PATCH.EXAMPLE.TEST",
            ),
        ];

        for (service, path, host) in cases {
            let text = format!(
                "[{service}] 192.0.2.40 / 198.51.100.9, 2001:db8::9 - fixture-user [12/Sep/2026:18:22:34 +1000] \"GET {path} HTTP/1.1\" 206 2048 \"-\" \"Fixture/1.0\" \"MISS\" \"{host}\" \"bytes=0-2047\""
            );
            let mut json = official_json();
            json["cache_identifier"] = serde_json::json!(service);
            json["path"] = serde_json::json!(path);
            json["host"] = serde_json::json!(host);

            let text_entry = parser.parse_line(&text).expect("official text record");
            let json_entry = parser
                .parse_line(&json.to_string())
                .expect("official JSON record");

            assert_eq!(json_entry.service, text_entry.service);
            assert_eq!(json_entry.client_ip, text_entry.client_ip);
            assert_eq!(json_entry.timestamp, text_entry.timestamp);
            assert_eq!(json_entry.method, text_entry.method);
            assert_eq!(json_entry.raw_url, text_entry.raw_url);
            assert_eq!(json_entry.url, text_entry.url);
            assert_eq!(json_entry.status_code, text_entry.status_code);
            assert_eq!(json_entry.bytes_served, text_entry.bytes_served);
            assert_eq!(json_entry.cache_status, text_entry.cache_status);
            assert_eq!(json_entry.http_range, text_entry.http_range);
            assert_eq!(json_entry.depot_id, text_entry.depot_id);
            assert_eq!(json_entry.tact_product, text_entry.tact_product);
            assert_eq!(json_entry.cdn_host, text_entry.cdn_host);
        }
    }

    #[test]
    fn extract_host_and_user_agent_decodes_json_strings() {
        let parser = LogParser::new(chrono_tz::UTC);
        let mut json = official_json();
        json["host"] = serde_json::json!("escaped.example.test");
        json["user_agent"] = serde_json::json!("Fixture \"quoted\" agent");

        assert_eq!(
            parser.extract_host_and_user_agent(&json.to_string()),
            Some((
                "escaped.example.test".to_string(),
                "Fixture \"quoted\" agent".to_string()
            ))
        );
    }

    #[test]
    fn json_requires_every_typed_consumed_field() {
        let parser = LogParser::new(chrono_tz::UTC);
        let fields = [
            "cache_identifier",
            "remote_addr",
            "time_local",
            "method",
            "path",
            "status",
            "bytes_sent",
            "user_agent",
            "upstream_cache_status",
            "host",
            "http_range",
        ];

        for field in fields {
            let mut missing = official_json();
            missing.as_object_mut().expect("JSON object").remove(field);
            assert!(
                parser.parse_line(&missing.to_string()).is_none(),
                "missing {field}"
            );

            let mut null = official_json();
            null[field] = serde_json::Value::Null;
            assert!(
                parser.parse_line(&null.to_string()).is_none(),
                "null {field}"
            );

            let mut wrong_type = official_json();
            wrong_type[field] = if field == "bytes_sent" {
                serde_json::json!("2048")
            } else {
                serde_json::json!(2048)
            };
            assert!(
                parser.parse_line(&wrong_type.to_string()).is_none(),
                "wrong type {field}"
            );
        }
    }

    #[test]
    fn json_rejects_invalid_values_without_identity_fallbacks() {
        let parser = LogParser::new(chrono_tz::UTC);
        let invalid_values = [
            ("cache_identifier", serde_json::json!("   ")),
            ("remote_addr", serde_json::json!("")),
            ("remote_addr", serde_json::json!("192.0.2.40 extra")),
            ("time_local", serde_json::json!("not a timestamp")),
            ("method", serde_json::json!("Get")),
            ("method", serde_json::json!("")),
            ("path", serde_json::json!("")),
            ("path", serde_json::json!("/has a space")),
            ("status", serde_json::json!("20")),
            ("status", serde_json::json!("2O0")),
            ("bytes_sent", serde_json::json!(-1)),
        ];

        for (field, value) in invalid_values {
            let mut json = official_json();
            json[field] = value;
            assert!(
                parser.parse_line(&json.to_string()).is_none(),
                "invalid {field}"
            );
        }

        let mut missing_remote = official_json();
        missing_remote
            .as_object_mut()
            .expect("JSON object")
            .remove("remote_addr");
        missing_remote["forwarded_for"] = serde_json::json!("198.51.100.9");
        assert!(parser.parse_line(&missing_remote.to_string()).is_none());
        assert!(parser
            .parse_line(r#"{"cache_identifier":"steam""#)
            .is_none());
        assert!(parser
            .parse_line(
                r#"{"cache_identifier":"steam","remote_addr":"192.0.2.40","time_local":"12/Sep/2026:18:22:34 +1000","method":"GET","path":"/file","status":"200","bytes_sent":9223372036854775808,"user_agent":"Fixture","upstream_cache_status":"HIT","host":"cdn.example.test","http_range":"-"}"#
            )
            .is_none());
    }

    #[test]
    fn json_preserves_status_and_zero_byte_meaning() {
        let parser = LogParser::new(chrono_tz::UTC);
        for cache_status in ["UNKNOWN", "BYPASS", "EXPIRED", "STALE", "-", ""] {
            let mut json = official_json();
            json["status"] = serde_json::json!("504");
            json["bytes_sent"] = serde_json::json!(0);
            json["upstream_cache_status"] = serde_json::json!(cache_status);
            let entry = parser
                .parse_line(&json.to_string())
                .expect("valid JSON record");
            assert_eq!(entry.status_code, 504);
            assert_eq!(entry.bytes_served, 0);
            let expected = if cache_status.is_empty() || cache_status == "-" {
                "UNKNOWN"
            } else {
                cache_status
            };
            assert_eq!(entry.cache_status, expected);
        }
    }
}
