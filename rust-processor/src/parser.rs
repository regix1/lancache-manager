use crate::models::LogEntry;
use chrono::{FixedOffset, NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use regex::Regex;

pub struct LogParser {
    main_regex: Regex,
    depot_regex: Regex,
    local_tz: Tz,
}

impl LogParser {
    pub fn new(local_tz: Tz) -> Self {
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

    pub fn parse_line(&self, line: &str) -> Option<LogEntry> {
        let captures = self.main_regex.captures(line)?;

        let service = captures
            .name("service")
            .map(|m| m.as_str().to_string())
            .unwrap_or_else(|| "unknown".to_string());

        let client_ip = captures.name("ip")?.as_str().to_string();
        let time_str = captures.name("time")?.as_str();
        let url = captures.name("url")?.as_str().to_string();
        let status_code = captures.name("status")?.as_str().parse::<i32>().ok()?;

        let bytes_str = captures.name("bytes")?.as_str();
        let bytes_served = if bytes_str == "-" {
            0
        } else {
            bytes_str.parse::<i64>().ok()?
        };

        let rest = captures.name("rest").map(|m| m.as_str()).unwrap_or("");

        // Parse timestamp
        let timestamp = self.parse_timestamp(time_str)?;

        // Extract cache status from rest
        let cache_status = self.extract_cache_status(rest);

        // Extract depot ID for Steam service
        let depot_id = if service.to_lowercase() == "steam" {
            self.extract_depot_id(&url)
        } else {
            None
        };

        Some(LogEntry {
            timestamp,
            client_ip,
            service,
            url,
            status_code,
            bytes_served,
            cache_status,
            depot_id,
        })
    }

    fn parse_timestamp(&self, time_str: &str) -> Option<NaiveDateTime> {
        // Extract timezone offset if present (e.g., " -0600" or " +0000")
        let (time_without_tz, tz_offset) = if let Some(pos) = time_str.rfind(|c| c == '+' || c == '-') {
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
            return Some(self.convert_to_utc(naive_dt, tz_offset));
        }

        // Try format: yyyy-MM-dd HH:mm:ss
        if let Ok(naive_dt) = NaiveDateTime::parse_from_str(time_without_tz, "%Y-%m-%d %H:%M:%S") {
            return Some(self.convert_to_utc(naive_dt, tz_offset));
        }

        // Try format: yyyy-MM-ddTHH:mm:ss
        if let Ok(naive_dt) = NaiveDateTime::parse_from_str(time_without_tz, "%Y-%m-%dT%H:%M:%S") {
            return Some(self.convert_to_utc(naive_dt, tz_offset));
        }

        None
    }

    fn convert_to_utc(&self, naive_dt: NaiveDateTime, tz_offset_secs: Option<i32>) -> NaiveDateTime {
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
        if let Some(local_dt) = self.local_tz.from_local_datetime(&naive_dt).earliest() {
            return local_dt.with_timezone(&Utc).naive_utc();
        }
        // Fallback: assume UTC if conversion fails
        naive_dt
    }

    fn extract_cache_status(&self, rest: &str) -> String {
        // Extract 3rd quoted field: "HIT" or "MISS"
        let mut quote_count = 0;
        let mut start_idx = None;

        for (i, ch) in rest.chars().enumerate() {
            if ch == '"' {
                quote_count += 1;
                if quote_count == 5 {
                    // Start of 3rd quoted field
                    start_idx = Some(i + 1);
                } else if quote_count == 6 {
                    // End of 3rd quoted field
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

    fn extract_depot_id(&self, url: &str) -> Option<u32> {
        self.depot_regex
            .captures(url)
            .and_then(|cap| cap.get(1))
            .and_then(|m| m.as_str().parse::<u32>().ok())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_actual_log_format() {
        let parser = LogParser::new(chrono_tz::UTC);

        // Test heartbeat line - from actual file
        let line1 = r#"[127.0.0.1] 127.0.0.1 / - - - [10/Jan/2024:16:28:34 -0600] "GET /lancache-heartbeat HTTP/1.1" 204 0 "-" "Wget/1.19.4 (linux-gnu)" "-" "127.0.0.1" "-""#;
        let entry1 = parser.parse_line(line1);
        println!("Line1: {}", line1);
        println!("Entry1: {:?}", entry1);
        println!("Line1 len: {}", line1.len());
        println!("Line1 bytes: {:?}", &line1.as_bytes()[0..50]);

        // Also test with escaped quotes (what might be in file)
        let line1_alt = "[127.0.0.1] 127.0.0.1 / - - - [10/Jan/2024:16:28:34 -0600] \"GET /lancache-heartbeat HTTP/1.1\" 204 0 \"-\" \"Wget/1.19.4 (linux-gnu)\" \"-\" \"127.0.0.1\" \"-\"";
        let entry1_alt = parser.parse_line(line1_alt);
        println!("Line1_alt: {}", line1_alt);
        println!("Entry1_alt: {:?}", entry1_alt);

        assert!(entry1.is_some() || entry1_alt.is_some(), "Failed to parse heartbeat line");

        // Test steam line
        let line2 = r#"[steam] 172.16.1.143 / - - - [29/Aug/2025:19:48:49 -0500] "GET /depot/2767031/chunk/115d1e0e2ea9e4ed02b5111c5e3d061d052c292a HTTP/1.1" 200 414016 "-" "Valve/Steam HTTP Client 1.0" "MISS" "fastly.cdn.steampipe.steamcontent.com" "-""#;
        let entry2 = parser.parse_line(line2);
        println!("Line2: {}", line2);
        println!("Entry2: {:?}", entry2);
        assert!(entry2.is_some(), "Failed to parse steam line");

        let entry2 = entry2.unwrap();
        assert_eq!(entry2.service, "steam");
        assert_eq!(entry2.client_ip, "172.16.1.143");
        assert_eq!(entry2.status_code, 200);
        assert_eq!(entry2.bytes_served, 414016);
        assert_eq!(entry2.depot_id, Some(2767031));
        assert_eq!(entry2.cache_status, "MISS");
    }
}