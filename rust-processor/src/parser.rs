use crate::log_layout::SourceKind;
use crate::models::LogEntry;
use crate::parser_http_detailed::HttpDetailedParser;
use crate::service_utils;
use crate::tact_products;
use chrono::{FixedOffset, NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use regex::Regex;

pub(crate) struct LogParser {
    main_regex: Regex,
    depot_regex: Regex,
    local_tz: Tz,
}

impl LogParser {
    pub(crate) fn new(local_tz: Tz) -> Self {
        // Updated regex to match the actual format:
        // [service] ip / - - - [timestamp] "METHOD URL HTTP/version" status bytes "referer" "user-agent" "cache-status" "upstream" "other"
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

    pub(crate) fn parse_line(&self, line: &str) -> Option<LogEntry> {
        let captures = self.main_regex.captures(line)?;

        let service = captures
            .name("service")
            .map(|m| service_utils::normalize_service_name(m.as_str()))
            .unwrap_or_else(|| "unknown".to_string());

        let client_ip = captures.name("ip")?.as_str().to_string();
        let method = captures.name("method")?.as_str().to_string();
        let time_str = captures.name("time")?.as_str();
        let raw_url = captures.name("url")?.as_str().to_string();
        let url = Self::normalize_url(&raw_url);
        let status_code = captures.name("status")?.as_str().parse::<i32>().ok()?;

        let bytes_str = captures.name("bytes")?.as_str();
        let bytes_served = if bytes_str == "-" {
            0
        } else {
            bytes_str.parse::<i64>().ok()?
        };

        let rest = captures.name("rest").map(|m| m.as_str()).unwrap_or("");

        // The manager's own Status Check probes (heartbeat + HTTPS-redirect) tag themselves
        // with a marker User-Agent; they are synthetic traffic and must never become
        // downloads, corruption evidence, or purge candidates.
        if service_utils::is_manager_probe(rest) {
            return None;
        }

        // Parse timestamp
        let timestamp = self.parse_timestamp(time_str)?;

        // Extract cache status from rest
        let cache_status = self.extract_cache_status(rest);

        // Extract HTTP Range header from rest (5th quoted field, quotes 9-10)
        let http_range = self.extract_quoted_field(rest, 5);

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
            let host = self.extract_quoted_field(rest, 4);
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
}
