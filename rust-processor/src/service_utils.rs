/// Utility functions for service name normalization and URL filtering
/// This ensures consistent service names and URL handling across all modules

/// Check if a URL should be skipped from processing
/// Returns true for health check/heartbeat endpoints that legitimately have no cache status
pub fn should_skip_url(url: &str) -> bool {
    url.contains("/lancache-heartbeat") ||
    url.contains("/health") ||
    url.contains("/ping")
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
        let non_ip_chars = service_lower.chars().filter(|c| !c.is_numeric() && *c != '.').count();
        if non_ip_chars == 0 {
            return "ip-address".to_string();
        }
    }

    service_lower
}

/// Extract and normalize service name from a log line
/// Format: [service] ...
pub fn extract_service_from_line(line: &str) -> Option<String> {
    if line.starts_with('[') {
        if let Some(end_idx) = line.find(']') {
            let service = &line[1..end_idx];
            return Some(normalize_service_name(service));
        }
    }
    None
}
