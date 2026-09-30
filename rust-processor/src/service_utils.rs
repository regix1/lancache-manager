//! Utility functions for service name normalization and URL filtering
//! This ensures consistent service names and URL handling across all modules

use crate::parser::parse_cachelog_json;

/// User-Agent marker carried by every server-side probe the manager itself sends through the
/// cache (Status Check heartbeat and HTTPS-redirect checks). Lines carrying it are synthetic
/// traffic, never client downloads. Must stay in sync with ProbeUserAgent in the C# side's
/// LancacheServerLocator.
#[allow(dead_code)]
pub const MANAGER_PROBE_USER_AGENT: &str = "lancache-manager-status-check";

/// Check if a URL should be skipped from processing
/// Returns true for health check/heartbeat endpoints that legitimately have no cache status.
/// Deliberately NO URL-shape heuristics beyond these fixed endpoints: the manager must never
/// hide access-log lines it didn't generate, so its own traffic is recognized only by the
/// explicit probe User-Agent marker below.
#[allow(dead_code)]
pub fn should_skip_url(url: &str) -> bool {
    url.contains("/lancache-heartbeat") || url.contains("/health") || url.contains("/ping")
}

/// True when an access-log line's quoted tail (referer/user-agent/... fields) carries the
/// manager's probe User-Agent.
#[allow(dead_code)]
pub fn is_manager_probe(rest_fields: &str) -> bool {
    rest_fields.contains(MANAGER_PROBE_USER_AGENT)
}

/// Normalize service names to ensure consistency
/// - Converts localhost/127.x variants to "localhost"
/// - Converts raw IP addresses to "ip-address"
/// - Converts to lowercase
pub fn normalize_service_name(service: &str) -> String {
    let service_lower = service.to_lowercase();

    // Normalize localhost and 127.x IPs
    if service_lower.starts_with("127.") || service_lower == "127" || service_lower == "localhost" {
        return "localhost".to_string();
    }

    // If it looks like an IP address (has dots and numbers), group as "ip-address"
    if service_lower.contains('.') && service_lower.chars().any(|c| c.is_numeric()) {
        // Check if it's mostly numbers and dots (likely an IP)
        let non_ip_chars = service_lower
            .chars()
            .filter(|c| !c.is_numeric() && *c != '.')
            .count();
        if non_ip_chars == 0 {
            return "ip-address".to_string();
        }
    }

    // If it looks like an IPv6 address (contains colons and hex digits), group as "ip-address"
    if service_lower.contains(':') {
        let non_ipv6_chars = service_lower
            .chars()
            .filter(|c| !c.is_ascii_hexdigit() && *c != ':')
            .count();
        if non_ipv6_chars == 0 {
            return "ip-address".to_string();
        }
    }

    service_lower
}

/// Extract and normalize service name from a log line
/// Format: [service] ...
#[allow(dead_code)]
pub fn extract_service_from_line(line: &str) -> Option<String> {
    if line.starts_with('{') {
        let record = parse_cachelog_json(line)?;
        return Some(normalize_service_name(&record.cache_identifier));
    }

    if line.starts_with('[') {
        if let Some(end_idx) = line.find(']') {
            let service = &line[1..end_idx];
            return Some(normalize_service_name(service));
        }
    }
    None
}

/// Match a cachelog record's explicit service without treating other line contents as identity.
/// Bracketed text decodes only the tag so invalid bytes elsewhere remain harmless. JSON uses the
/// same typed decoder and normalization as ingestion and service extraction.
pub fn line_matches_service(raw_line: &[u8], service_lower: &str) -> bool {
    let trimmed = raw_line
        .iter()
        .position(|byte| !byte.is_ascii_whitespace())
        .map(|start| &raw_line[start..])
        .unwrap_or_default();

    if trimmed.first() == Some(&b'{') {
        let line = String::from_utf8_lossy(trimmed);
        return extract_service_from_line(line.trim())
            .is_some_and(|service| service == service_lower);
    }

    if trimmed.first() != Some(&b'[') {
        return false;
    }
    let Some(end) = trimmed.iter().position(|byte| *byte == b']') else {
        return false;
    };
    std::str::from_utf8(&trimmed[1..end])
        .map(normalize_service_name)
        .is_ok_and(|service| service == service_lower)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn json_line(service: &str) -> String {
        serde_json::json!({
            "cache_identifier": service,
            "remote_addr": "192.0.2.40",
            "time_local": "12/Sep/2026:18:22:34 +1000",
            "method": "GET",
            "path": "/content/file",
            "status": "200",
            "bytes_sent": 1,
            "user_agent": "Fixture/1.0",
            "upstream_cache_status": "HIT",
            "host": "cdn.example.test",
            "http_range": "-"
        })
        .to_string()
    }

    #[test]
    fn normalize_localhost_variants() {
        assert_eq!(normalize_service_name("localhost"), "localhost");
        assert_eq!(normalize_service_name("LOCALHOST"), "localhost");
        assert_eq!(normalize_service_name("127.0.0.1"), "localhost");
        assert_eq!(normalize_service_name("127.1"), "localhost");
    }

    #[test]
    fn normalize_ipv4_to_ip_address_bucket() {
        assert_eq!(normalize_service_name("192.168.1.10"), "ip-address");
        assert_eq!(normalize_service_name("10.0.0.5"), "ip-address");
    }

    #[test]
    fn normalize_ipv6_to_ip_address_bucket() {
        assert_eq!(normalize_service_name("2001:db8::1"), "ip-address");
        assert_eq!(normalize_service_name("::1"), "ip-address");
    }

    #[test]
    fn normalize_named_service_lowercases() {
        assert_eq!(normalize_service_name("Steam"), "steam");
        assert_eq!(normalize_service_name("epicgames"), "epicgames");
        assert_eq!(normalize_service_name("WSUS"), "wsus");
    }

    #[test]
    fn should_skip_health_probe_urls() {
        assert!(should_skip_url("/lancache-heartbeat"));
        assert!(should_skip_url("/api/health"));
        assert!(should_skip_url("/ping"));
        assert!(!should_skip_url("/depot/123/chunk"));
    }

    #[test]
    fn is_manager_probe_matches_user_agent_marker() {
        let probe_line = r#""-" "lancache-manager-status-check/1.0""#;
        assert!(is_manager_probe(probe_line));
        assert!(!is_manager_probe(r#""-" "Mozilla/5.0""#));
    }

    #[test]
    fn extract_service_from_bracketed_line() {
        assert_eq!(
            extract_service_from_line("[Steam] GET /foo"),
            Some("steam".to_string())
        );
        assert_eq!(extract_service_from_line("no bracket"), None);
    }

    #[test]
    fn extract_service_from_json_uses_typed_record() {
        let line = json_line("XboxLive");
        assert_eq!(
            extract_service_from_line(&line),
            Some("xboxlive".to_string())
        );
        assert!(line_matches_service(line.as_bytes(), "xboxlive"));
        assert!(!line_matches_service(line.as_bytes(), "wsus"));

        let blank = json_line("   ");
        assert_eq!(extract_service_from_line(&blank), None);
        assert!(!line_matches_service(blank.as_bytes(), "xboxlive"));
        assert!(!line_matches_service(b"{malformed JSON}\n", "xboxlive"));
    }

    #[test]
    fn line_matches_service_keeps_bracketed_byte_tolerance() {
        let line = b"  [Steam] 192.0.2.40 / - - - [time] \xff\n";
        assert!(line_matches_service(line, "steam"));
        assert!(!line_matches_service(line, "epicgames"));
    }
}
