use crate::models::LogEntry;
use crate::parser::{self, LogParser};
use crate::service_utils;
use crate::tact_products;
use chrono_tz::Tz;
use regex::Regex;

/// Parser for the bare-metal (zeropingheroes/lancache-bare-metal) `http-detailed`
/// access-log format. Field order is pinned by `access-log-formats/http/detailed.conf`:
///
/// ```text
///  1 [$time_local]              bracketed
///  2 $remote_addr
///  3 $request_method
///  4 "$request_uri"             quoted
///  5 $http_range                unquoted; `-` or empty when absent
///  6 $server_protocol
///  7 $status
///  8 "$http_referer"            quoted
///  9 $request_length
/// 10 $bytes_sent
/// 11 $body_bytes_sent           -> bytes_served (same semantics monolithic ingestion counts)
/// 12 $request_time
/// 13 $upstream_response_length  may be a comma list on upstream retries
/// 14 $upstream_cache_status
/// 15 $host                      -> riot cdn_host
/// 16 $upstream_status           may be a comma list
/// 17 $upstream_response_time    may be a comma list
/// 18 "$http_user_agent"         quoted
/// ```
///
/// There is NO `[service]` tag: attribution comes from the per-service filename hint the
/// caller passes in. Output is the SAME `LogEntry` the cachelog parser produces, so
/// everything downstream (sessions, stats, game naming) is format-blind.
pub(crate) struct HttpDetailedParser {
    main_regex: Regex,
    depot_regex: Regex,
    local_tz: Tz,
}

/// Structurally recognized record fields, before service attribution.
struct DetailedRecord<'a> {
    time: &'a str,
    ip: &'a str,
    method: &'a str,
    url: &'a str,
    range: &'a str,
    status: i32,
    body_bytes: i64,
    cache_status: &'a str,
    host: &'a str,
    referer: &'a str,
    user_agent: &'a str,
}

impl HttpDetailedParser {
    pub(crate) fn new(local_tz: Tz) -> Self {
        // Left-anchored fields 1-12, the flexible 13-17 tail as `rest`, and the final quoted
        // user agent. The range field (5) is anchored between the quoted request field and the
        // literal `HTTP/` protocol so both a `-` placeholder and a fully empty rendering
        // (adjacent spaces) are tolerated. Numeric fields tolerate `-` (absent variable).
        let main_regex = Regex::new(
            r#"^\[(?P<time>[^\]]+)\]\s+(?P<ip>\S+)\s+(?P<method>[A-Z]+)\s+"(?P<url>[^"]*)"\s+(?:(?P<range>[^\s"]+)\s+)?HTTP/\S+\s+(?P<status>\d{3})\s+"(?P<referer>[^"]*)"\s+(?P<reqlen>-|\d+)\s+(?P<bytes_sent>-|\d+)\s+(?P<body_bytes>-|\d+)\s+(?P<reqtime>-|[0-9.]+)\s+(?P<rest>.*?)\s+"(?P<ua>[^"]*)"$"#,
        )
        .unwrap();

        let depot_regex = Regex::new(r"/depot/(\d+)/").unwrap();

        Self {
            main_regex,
            depot_regex,
            local_tz,
        }
    }

    /// Consume one whitespace-tokenized upstream variable. Nginx renders multiple
    /// upstreams as either `value, value` or `value,value` and multiple groups as
    /// `value : value`, so a whitespace token may contain one or more comma-list elements.
    fn consume_upstream_field<F>(tokens: &[&str], idx: &mut usize, value_is_valid: F) -> bool
    where
        F: Fn(&str) -> bool,
    {
        loop {
            let Some(&token) = tokens.get(*idx) else {
                return false;
            };
            let has_trailing_comma = token.ends_with(',');
            let values = token.strip_suffix(',').unwrap_or(token);
            if values
                .split(',')
                .any(|value| value.is_empty() || !value_is_valid(value))
            {
                return false;
            }
            *idx += 1;

            if has_trailing_comma {
                continue;
            }
            if tokens.get(*idx) == Some(&":") {
                *idx += 1;
                continue;
            }
            return true;
        }
    }

    /// Split and validate the fields 13-17 tail. Upstream variables render as lists when
    /// nginx tried more than one upstream: `", "` separates servers within one upstream
    /// group and `" : "` separates groups (internal redirects / X-Accel), e.g.
    /// `"0, 10 : 20"`. `$upstream_cache_status` and `$host` are per-request variables
    /// and always single tokens.
    fn parse_tail(rest: &str) -> Option<(&str, &str)> {
        let tokens: Vec<&str> = rest.split_whitespace().collect();
        let mut idx = 0usize;
        let dash_or_digits = |value: &str| {
            value == "-" || (!value.is_empty() && value.chars().all(|c| c.is_ascii_digit()))
        };
        let dash_or_decimal = |value: &str| {
            if value == "-" {
                return true;
            }
            let mut parts = value.split('.');
            let Some(whole) = parts.next() else {
                return false;
            };
            if whole.is_empty() || !whole.chars().all(|c| c.is_ascii_digit()) {
                return false;
            }
            match parts.next() {
                Some(fraction) => {
                    !fraction.is_empty()
                        && fraction.chars().all(|c| c.is_ascii_digit())
                        && parts.next().is_none()
                }
                None => true,
            }
        };

        // Field 13: $upstream_response_length (comma- and colon-group list aware).
        if !Self::consume_upstream_field(&tokens, &mut idx, dash_or_digits) {
            return None;
        }

        // Field 14: $upstream_cache_status — single token, `-` or an nginx cache status word.
        let cache_status = *tokens.get(idx)?;
        if cache_status != "-"
            && !cache_status
                .chars()
                .all(|c| c.is_ascii_uppercase() || c == '_')
        {
            return None;
        }
        idx += 1;

        // Field 15: $host — single token.
        let host = *tokens.get(idx)?;
        idx += 1;

        // Fields 16 + 17 are unused, but must each be structurally complete. Merely checking
        // for two remaining tokens is unsafe: two tokens from a field-16 comma list can
        // otherwise masquerade as both required fields when field 17 is absent.
        if !Self::consume_upstream_field(&tokens, &mut idx, dash_or_digits)
            || !Self::consume_upstream_field(&tokens, &mut idx, dash_or_decimal)
            || idx != tokens.len()
        {
            return None;
        }

        Some((cache_status, host))
    }

    fn capture<'a>(&self, line: &'a str) -> Option<DetailedRecord<'a>> {
        let captures = self.main_regex.captures(line)?;

        let rest = captures.name("rest").map(|m| m.as_str()).unwrap_or("");
        // The final quoted run is the User-Agent. Quotes in fields 13-17 would let a
        // malformed earlier quoted run hide inside `rest` while a later one binds as UA.
        if rest.contains('"') {
            return None;
        }
        let (cache_status, host) = Self::parse_tail(rest)?;

        let parse_dash_i64 = |name: &str| -> Option<i64> {
            let s = captures.name(name)?.as_str();
            if s == "-" {
                Some(0)
            } else {
                s.parse::<i64>().ok()
            }
        };

        Some(DetailedRecord {
            time: captures.name("time")?.as_str(),
            ip: captures.name("ip")?.as_str(),
            method: captures.name("method")?.as_str(),
            url: captures.name("url")?.as_str(),
            range: captures
                .name("range")
                .map(|m| m.as_str())
                .filter(|r| *r != "-")
                .unwrap_or(""),
            status: captures.name("status")?.as_str().parse::<i32>().ok()?,
            body_bytes: parse_dash_i64("body_bytes")?,
            cache_status,
            host,
            referer: captures.name("referer")?.as_str(),
            user_agent: captures.name("ua")?.as_str(),
        })
    }

    /// Structural recognizer only: is this line an http-detailed record? Never consults
    /// the service hint, so a hint-less file can still be diagnosed as http-detailed.
    #[allow(dead_code)] // used by log_processor's classifier; other binaries share this module
    pub(crate) fn recognizes(&self, line: &str) -> bool {
        self.capture(line).is_some()
    }

    /// Host (`$host`, field 15) and User-Agent (field 18) of an http-detailed record, reusing
    /// the same recognizer `parse_line` uses. The read-only content scan needs the request host
    /// for a DNS check; the produced `LogEntry` only carries `cdn_host` for the riot service.
    /// Returns None when the line is not an http-detailed record.
    #[allow(dead_code)] // used by the content scan in log_service_manager; other binaries share this module
    pub(crate) fn extract_host_and_user_agent(&self, line: &str) -> Option<(String, String)> {
        let record = self.capture(line)?;
        Some((record.host.to_string(), record.user_agent.to_string()))
    }

    /// Parse an http-detailed record into a `LogEntry`, attributing the given service.
    /// `service_hint` is the manager service name derived from the source filename
    /// (already through the filename map, e.g. `windows-update` -> `wsus`).
    pub(crate) fn parse_line(&self, line: &str, service_hint: &str) -> Option<LogEntry> {
        let record = self.capture(line)?;

        // Same synthetic-traffic rule as the cachelog parser: the manager's own Status
        // Check probes must never become downloads or corruption evidence.
        if service_utils::is_manager_probe(record.referer)
            || service_utils::is_manager_probe(record.user_agent)
        {
            return None;
        }

        let service = service_utils::normalize_service_name(service_hint);
        let timestamp = parser::parse_nginx_timestamp(record.time, self.local_tz)?;

        let raw_url = record.url.to_string();
        let url = LogParser::normalize_url(&raw_url);

        let cache_status = if record.cache_status == "-" {
            "UNKNOWN".to_string()
        } else {
            record.cache_status.to_string()
        };

        let service_lower = service.to_lowercase();
        let depot_id = if service_lower == "steam" {
            self.depot_regex
                .captures(&url)
                .and_then(|cap| cap.get(1))
                .and_then(|m| m.as_str().parse::<u32>().ok())
        } else {
            None
        };

        let tact_product = if service_lower == "blizzard" {
            tact_products::extract_tact_product(&url)
        } else {
            None
        };

        // Riot bundle URLs carry no product slug; the $host field is the only per-game
        // discriminator, exactly as the cachelog parser reads it from the quoted tail.
        let cdn_host = if service_lower == "riot" {
            (record.host != "-" && !record.host.is_empty()).then(|| record.host.to_lowercase())
        } else {
            None
        };

        Some(LogEntry {
            timestamp,
            client_ip: record.ip.to_string(),
            method: record.method.to_string(),
            service,
            raw_url,
            url,
            status_code: record.status,
            bytes_served: record.body_bytes,
            cache_status,
            depot_id,
            tact_product,
            http_range: record.range.to_string(),
            cdn_host,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn detailed_parser() -> HttpDetailedParser {
        HttpDetailedParser::new(chrono_tz::UTC)
    }

    fn cachelog_parser() -> LogParser {
        LogParser::new(chrono_tz::UTC)
    }

    const TS: &str = "01/Jan/2024:00:00:00 +0000";

    /// Build a detailed line with default plumbing fields.
    fn detailed_line(
        ip: &str,
        method: &str,
        uri: &str,
        range: &str,
        status: u32,
        body: u64,
        cache: &str,
        host: &str,
        ua: &str,
    ) -> String {
        format!(
            "[{TS}] {ip} {method} \"{uri}\" {range} HTTP/1.1 {status} \"-\" 512 {sent} {body} 0.005 {body} {cache} {host} {status} 0.004 \"{ua}\"",
            sent = body + 16,
        )
    }

    /// Build the semantically identical cachelog line: same timestamp, client, request,
    /// status, BODY bytes, cache status, host and range.
    fn cachelog_line(
        service: &str,
        ip: &str,
        method: &str,
        uri: &str,
        range: &str,
        status: u32,
        body: u64,
        cache: &str,
        host: &str,
        ua: &str,
    ) -> String {
        let range_field = if range.is_empty() || range == "-" {
            "-"
        } else {
            range
        };
        format!(
            "[{service}] {ip} / - - - [{TS}] \"{method} {uri} HTTP/1.1\" {status} {body} \"-\" \"{ua}\" \"{cache}\" \"{host}\" \"{range_field}\""
        )
    }

    fn assert_golden_pair(
        service_hint: &str,
        cachelog_service_tag: &str,
        ip: &str,
        uri: &str,
        range: &str,
        status: u32,
        body: u64,
        cache: &str,
        host: &str,
    ) {
        let ua = "test-agent/1.0";
        let detailed = detailed_line(
            ip,
            "GET",
            uri,
            if range.is_empty() { "-" } else { range },
            status,
            body,
            cache,
            host,
            ua,
        );
        let cachelog = cachelog_line(
            cachelog_service_tag,
            ip,
            "GET",
            uri,
            range,
            status,
            body,
            cache,
            host,
            ua,
        );

        let d = detailed_parser()
            .parse_line(&detailed, service_hint)
            .unwrap_or_else(|| panic!("detailed line must parse: {detailed}"));
        let c = cachelog_parser()
            .parse_line(&cachelog)
            .unwrap_or_else(|| panic!("cachelog line must parse: {cachelog}"));

        assert_eq!(d.timestamp, c.timestamp, "timestamp");
        assert_eq!(d.client_ip, c.client_ip, "client_ip");
        assert_eq!(d.method, c.method, "method");
        assert_eq!(d.service, c.service, "service");
        assert_eq!(d.raw_url, c.raw_url, "raw_url");
        assert_eq!(d.url, c.url, "url");
        assert_eq!(d.status_code, c.status_code, "status");
        assert_eq!(d.bytes_served, c.bytes_served, "bytes_served (body bytes)");
        assert_eq!(d.cache_status, c.cache_status, "cache_status");
        assert_eq!(d.http_range, c.http_range, "http_range");
        assert_eq!(d.depot_id, c.depot_id, "depot_id");
        assert_eq!(d.tact_product, c.tact_product, "tact_product");
        assert_eq!(d.cdn_host, c.cdn_host, "cdn_host");
    }

    #[test]
    fn golden_pair_steam_with_depot() {
        assert_golden_pair(
            "steam",
            "steam",
            "192.168.1.50",
            "/depot/123456/chunk/abcdef0123456789",
            "",
            200,
            1_048_576,
            "HIT",
            "lancache.steamcontent.com",
        );
    }

    #[test]
    fn golden_pair_blizzard_tact_product() {
        assert_golden_pair(
            "blizzard",
            "blizzard",
            "10.0.0.7",
            "/tpr/wow/data/ab/cd/abcdef",
            "",
            200,
            65536,
            "MISS",
            "level3.blizzard.com",
        );
    }

    #[test]
    fn golden_pair_riot_host_discriminator() {
        assert_golden_pair(
            "riot",
            "riot",
            "10.0.0.9",
            "/channels/public/bundles/ABC123.bundle",
            "",
            200,
            4096,
            "HIT",
            "lol.dyn.riotcdn.net",
        );
    }

    #[test]
    fn golden_pair_wsus_with_range() {
        // The bare-metal vhost is `windows-update`; discovery maps the hint to `wsus`
        // BEFORE the parser runs, so the parser receives `wsus` directly.
        assert_golden_pair(
            "wsus",
            "wsus",
            "192.168.1.60",
            "/filestreamingservice/files/12345678-90ab-cdef-1234-567890abcdef",
            "bytes=1048576-2097151",
            206,
            1_048_576,
            "MISS",
            "download.windowsupdate.com",
        );
    }

    #[test]
    fn golden_pair_epicgames() {
        assert_golden_pair(
            "epicgames",
            "epicgames",
            "10.1.1.4",
            "/Builds/Org/o-abc123/hash456/default/ChunksV4/00/file.chunk",
            "",
            200,
            32768,
            "EXPIRED",
            "epicgames-download1.akamaized.net",
        );
    }

    #[test]
    fn collision_cachelog_lines_are_rejected() {
        let p = detailed_parser();
        let tagged = cachelog_line(
            "steam",
            "192.168.1.50",
            "GET",
            "/depot/1/chunk/a",
            "",
            200,
            10,
            "HIT",
            "h",
            "ua",
        );
        let untagged = "1.2.3.4 / - - - [01/Jan/2024:00:00:00 +0000] \"GET /x HTTP/1.1\" 200 5 \"-\" \"ua\" \"HIT\" \"h\" \"-\"";
        assert!(
            !p.recognizes(&tagged),
            "cachelog tagged line must not be recognized"
        );
        assert!(
            !p.recognizes(untagged),
            "cachelog untagged line must not be recognized"
        );
    }

    #[test]
    fn collision_detailed_lines_are_rejected_by_cachelog_parser() {
        let line = detailed_line(
            "192.168.1.50",
            "GET",
            "/depot/1/chunk/a",
            "-",
            200,
            10,
            "HIT",
            "host",
            "ua",
        );
        assert!(cachelog_parser().parse_line(&line).is_none());
    }

    #[test]
    fn collision_prefixes_and_truncations_never_cross_accept() {
        // Cross-format property: no truncation of a line in one format may ever be
        // accepted by the OTHER format's parser. (The cachelog regex tolerating
        // truncations of its own format is pre-existing, frozen behavior.)
        let d = detailed_parser();
        let c = cachelog_parser();
        let detailed = detailed_line(
            "10.0.0.1",
            "GET",
            "/depot/9/chunk/z",
            "-",
            200,
            42,
            "HIT",
            "host",
            "agent",
        );
        let cachelog = cachelog_line(
            "steam",
            "10.0.0.1",
            "GET",
            "/depot/9/chunk/z",
            "",
            200,
            42,
            "HIT",
            "host",
            "agent",
        );
        for cut in 1..=detailed.len() {
            if !detailed.is_char_boundary(cut) {
                continue;
            }
            assert!(
                c.parse_line(&detailed[..cut]).is_none(),
                "cachelog parser accepted detailed-line truncation at {cut}"
            );
        }
        for cut in 1..=cachelog.len() {
            if !cachelog.is_char_boundary(cut) {
                continue;
            }
            assert!(
                !d.recognizes(&cachelog[..cut]),
                "detailed recognizer accepted cachelog-line truncation at {cut}"
            );
        }
        // And truncating the detailed line strips its closing UA quote, so the detailed
        // recognizer itself must reject every strict prefix.
        for cut in 1..detailed.len() {
            if !detailed.is_char_boundary(cut) {
                continue;
            }
            assert!(
                !d.recognizes(&detailed[..cut]),
                "detailed recognizer accepted its own truncation at {cut}"
            );
        }
    }

    #[test]
    fn degenerate_ipv6_client_parses() {
        let line = detailed_line(
            "2001:db8::42",
            "GET",
            "/depot/5/chunk/b",
            "-",
            200,
            100,
            "HIT",
            "h.example",
            "ua",
        );
        let e = detailed_parser()
            .parse_line(&line, "steam")
            .expect("ipv6 line");
        assert_eq!(e.client_ip, "2001:db8::42");
    }

    #[test]
    fn degenerate_empty_range_renderings() {
        let p = detailed_parser();
        // `-` placeholder
        let dash = detailed_line("1.1.1.1", "GET", "/x", "-", 200, 1, "HIT", "h", "ua");
        assert_eq!(
            p.parse_line(&dash, "steam").expect("dash range").http_range,
            ""
        );
        // fully empty rendering: adjacent spaces between the quoted URI and HTTP/
        let empty = format!(
            "[{TS}] 1.1.1.1 GET \"/x\"  HTTP/1.1 200 \"-\" 512 17 1 0.005 1 HIT h 200 0.004 \"ua\""
        );
        assert_eq!(
            p.parse_line(&empty, "steam")
                .expect("empty range")
                .http_range,
            ""
        );
    }

    #[test]
    fn degenerate_quoted_ua_with_spaces_and_quotes_in_referer() {
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"http://ref/\" 512 17 1 0.005 1 HIT h 200 0.004 \"Mozilla/5.0 (Windows NT 10.0; Win64) Chrome/120\""
        );
        assert!(detailed_parser().parse_line(&line, "steam").is_some());
    }

    #[test]
    fn degenerate_truncated_and_garbage_rejected() {
        let p = detailed_parser();
        assert!(!p.recognizes(""));
        assert!(!p.recognizes("[01/Jan/2024"));
        assert!(!p.recognizes("garbage line with words"));
        assert!(!p.recognizes("\u{fffd}\u{fffd}\u{fffd}"));
        // Missing the final quoted UA
        let no_ua = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 1 0.005 1 HIT h 200 0.004"
        );
        assert!(!p.recognizes(&no_ua));
        // Tail too short (missing upstream_status + upstream_response_time)
        let short_tail =
            format!("[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 1 0.005 1 HIT \"ua\"");
        assert!(!p.recognizes(&short_tail));
    }

    #[test]
    fn upstream_comma_lists_are_tolerated() {
        // nginx tried two upstreams: length/status/time render as comma lists.
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 9 0.005 0, 1048576 MISS h.example 502, 200 0.004, 1.2 \"ua\""
        );
        let e = detailed_parser()
            .parse_line(&line, "steam")
            .expect("comma-list line");
        assert_eq!(e.cache_status, "MISS");
        assert_eq!(e.bytes_served, 9);
    }

    #[test]
    fn upstream_comma_lists_without_spaces_are_tolerated() {
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 9 0.005 0,1048576 MISS h.example 502,200 0.004,1.2 \"ua\""
        );
        let e = detailed_parser()
            .parse_line(&line, "steam")
            .expect("compact comma-list line");
        assert_eq!(e.cache_status, "MISS");
        assert_eq!(e.bytes_served, 9);
    }

    #[test]
    fn upstream_colon_separated_groups_are_tolerated() {
        // Internal redirect through a second upstream group: nginx joins the groups with
        // " : " inside each $upstream_* variable.
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 9 0.005 0, 10 : 20 HIT h.example 502, 200 : 200 0.004, 1.2 : 0.9 \"ua\""
        );
        let e = detailed_parser()
            .parse_line(&line, "steam")
            .expect("colon-group line");
        assert_eq!(e.cache_status, "HIT");
        assert_eq!(e.bytes_served, 9);
    }

    #[test]
    fn missing_upstream_response_time_is_not_masked_by_status_list() {
        // Field 16 has two values, but field 17 is absent. A raw remaining-token count
        // mistakes the two status tokens for the two required fields.
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 9 0.005 1 HIT h.example 502, 200 \"ua\""
        );
        assert!(!detailed_parser().recognizes(&line));
    }

    #[test]
    fn extra_tail_tokens_are_rejected() {
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 9 0.005 1 HIT h.example 200 0.004 unexpected \"ua\""
        );
        assert!(!detailed_parser().recognizes(&line));
    }

    #[test]
    fn quoted_token_inside_structured_tail_is_rejected() {
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 17 9 0.005 1 HIT \"h.example\" 200 0.004 \"ua\""
        );
        assert!(!detailed_parser().recognizes(&line));
    }

    #[test]
    fn probe_user_agent_never_parses() {
        let line = detailed_line(
            "172.20.0.5",
            "GET",
            "/",
            "-",
            301,
            162,
            "MISS",
            "lancache.steamcontent.com",
            "lancache-manager-status-check/1.0",
        );
        assert!(detailed_parser().parse_line(&line, "steam").is_none());
    }

    #[test]
    fn probe_marker_in_referer_never_parses() {
        let line = format!(
            "[{TS}] 172.20.0.5 GET \"/\" - HTTP/1.1 301 \"lancache-manager-status-check/1.0\" 512 178 162 0.005 162 MISS h 301 0.004 \"ordinary-agent\""
        );
        let parser = detailed_parser();
        assert!(parser.recognizes(&line));
        assert!(parser.parse_line(&line, "steam").is_none());
    }

    #[test]
    fn dash_cache_status_maps_to_unknown() {
        let line = detailed_line("1.1.1.1", "GET", "/x", "-", 200, 1, "-", "h", "ua");
        assert_eq!(
            detailed_parser()
                .parse_line(&line, "steam")
                .expect("line")
                .cache_status,
            "UNKNOWN"
        );
    }

    #[test]
    fn riot_uses_host_field_and_dash_host_is_none() {
        let p = detailed_parser();
        let with_host = detailed_line(
            "1.1.1.1",
            "GET",
            "/channels/public/bundles/a.bundle",
            "-",
            200,
            1,
            "HIT",
            "LOL.dyn.riotcdn.net",
            "ua",
        );
        assert_eq!(
            p.parse_line(&with_host, "riot")
                .expect("riot line")
                .cdn_host
                .as_deref(),
            Some("lol.dyn.riotcdn.net")
        );
        let e = crate::riot_hosts::resolve_riot_host("lol.dyn.riotcdn.net");
        assert!(e.is_some(), "resolver must know the lol host");
        let no_host = detailed_line(
            "1.1.1.1",
            "GET",
            "/channels/public/bundles/a.bundle",
            "-",
            200,
            1,
            "HIT",
            "-",
            "ua",
        );
        assert_eq!(
            p.parse_line(&no_host, "riot").expect("riot line").cdn_host,
            None
        );
    }

    #[test]
    fn url_double_slash_normalized_same_as_cachelog() {
        let line = detailed_line(
            "1.1.1.1",
            "GET",
            "/filestreamingservice//files/abc",
            "-",
            200,
            1,
            "HIT",
            "h",
            "ua",
        );
        let e = detailed_parser().parse_line(&line, "wsus").expect("line");
        assert_eq!(e.url, "/filestreamingservice/files/abc");
        assert_eq!(e.raw_url, "/filestreamingservice//files/abc");
    }

    #[test]
    fn body_bytes_field_11_not_bytes_sent_field_10() {
        // bytes_sent (field 10) deliberately differs from body_bytes_sent (field 11);
        // bytes_served must be field 11.
        let line = format!(
            "[{TS}] 1.1.1.1 GET \"/x\" - HTTP/1.1 200 \"-\" 512 99999 1234 0.005 1234 HIT h 200 0.004 \"ua\""
        );
        assert_eq!(
            detailed_parser()
                .parse_line(&line, "steam")
                .expect("line")
                .bytes_served,
            1234
        );
    }
}
