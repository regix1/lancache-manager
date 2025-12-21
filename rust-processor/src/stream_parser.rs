use crate::models::StreamLogEntry;
use chrono::{FixedOffset, NaiveDateTime, TimeZone, Utc};
use chrono_tz::Tz;
use regex::Regex;

/// Parser for stream-access.log files
/// Format: IP [timestamp] PROTOCOL STATUS BYTES_SENT BYTES_RECV DURATION "HOST"
/// Example: 172.16.1.143 [20/Dec/2025:07:50:58 -0600] TCP 200 7063 910 77.590 "cp601.prod.do.dsp.mp.microsoft.com"
pub(crate) struct StreamLogParser {
    regex: Regex,
    local_tz: Tz,
}

impl StreamLogParser {
    pub(crate) fn new(local_tz: Tz) -> Self {
        // Regex to match stream-access.log format:
        // IP [timestamp with tz] PROTOCOL STATUS BYTES_SENT BYTES_RECV DURATION "HOST"
        let regex = Regex::new(
            r#"^(?P<ip>\S+)\s+\[(?P<time>[^\]]+)\]\s+(?P<protocol>\S+)\s+(?P<status>\d+)\s+(?P<bytes_sent>\d+)\s+(?P<bytes_recv>\d+)\s+(?P<duration>[\d.]+)\s+"(?P<host>[^"]+)"$"#
        ).unwrap();

        Self { regex, local_tz }
    }

    pub(crate) fn parse_line(&self, line: &str) -> Option<StreamLogEntry> {
        let captures = self.regex.captures(line)?;

        let client_ip = captures.name("ip")?.as_str().to_string();
        let time_str = captures.name("time")?.as_str();
        let protocol = captures.name("protocol")?.as_str().to_string();
        let status = captures.name("status")?.as_str().parse::<i32>().ok()?;
        let bytes_sent = captures.name("bytes_sent")?.as_str().parse::<i64>().ok()?;
        let bytes_received = captures.name("bytes_recv")?.as_str().parse::<i64>().ok()?;
        let session_duration = captures.name("duration")?.as_str().parse::<f64>().ok()?;
        let upstream_host = captures.name("host")?.as_str().to_string();

        // Parse timestamp
        let timestamp = self.parse_timestamp(time_str)?;

        Some(StreamLogEntry {
            client_ip,
            timestamp,
            protocol,
            status,
            bytes_sent,
            bytes_received,
            session_duration,
            upstream_host,
        })
    }

    fn parse_timestamp(&self, time_str: &str) -> Option<NaiveDateTime> {
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
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_stream_log_line() {
        let parser = StreamLogParser::new(chrono_tz::UTC);

        let line = r#"172.16.1.143 [20/Dec/2025:07:50:58 -0600] TCP 200 7063 910 77.590 "cp601.prod.do.dsp.mp.microsoft.com""#;

        let entry = parser.parse_line(line).expect("Should parse successfully");

        assert_eq!(entry.client_ip, "172.16.1.143");
        assert_eq!(entry.protocol, "TCP");
        assert_eq!(entry.status, 200);
        assert_eq!(entry.bytes_sent, 7063);
        assert_eq!(entry.bytes_received, 910);
        assert_eq!(entry.session_duration, 77.590);
        assert_eq!(entry.upstream_host, "cp601.prod.do.dsp.mp.microsoft.com");
    }

    #[test]
    fn test_speed_calculation() {
        let parser = StreamLogParser::new(chrono_tz::UTC);

        // 283399 bytes in 15.520 seconds = ~18261 bytes/sec
        let line = r#"172.16.1.143 [20/Dec/2025:07:59:34 -0600] TCP 200 283399 91106 15.520 "fe2cr.update.microsoft.com""#;

        let entry = parser.parse_line(line).expect("Should parse successfully");

        let download_speed = entry.download_speed_bps();
        let upload_speed = entry.upload_speed_bps();

        // 283399 / 15.520 ≈ 18261 bytes/sec
        assert!((download_speed - 18261.0).abs() < 1.0);
        // 91106 / 15.520 ≈ 5870 bytes/sec
        assert!((upload_speed - 5870.0).abs() < 1.0);
    }

    #[test]
    fn test_session_start_calculation() {
        let parser = StreamLogParser::new(chrono_tz::UTC);

        // Duration is 77.590 seconds, end time is 07:50:58
        // Start time should be approximately 07:49:40
        let line = r#"172.16.1.143 [20/Dec/2025:13:50:58 +0000] TCP 200 7063 910 77.590 "test.example.com""#;

        let entry = parser.parse_line(line).expect("Should parse successfully");
        let start = entry.session_start();

        // End time: 13:50:58 UTC
        // Duration: 77.590 seconds ≈ 1 min 17.59 sec
        // Start should be around 13:49:40
        assert_eq!(start.format("%H:%M:%S").to_string(), "13:49:40");
    }
}
