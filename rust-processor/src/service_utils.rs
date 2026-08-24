/// Utility functions for service name normalization and URL filtering
/// This ensures consistent service names and URL handling across all modules

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

#[cfg(test)]
mod tests {
    use super::*;

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
}

/// Extract and normalize service name from a log line
/// Format: [service] ...
#[allow(dead_code)]
pub fn extract_service_from_line(line: &str) -> Option<String> {
    if line.starts_with('[') {
        if let Some(end_idx) = line.find(']') {
            let service = &line[1..end_idx];
            return Some(normalize_service_name(service));
        }
    }
    None
}
