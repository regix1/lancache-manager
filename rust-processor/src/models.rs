use chrono::{NaiveDateTime, TimeDelta};

#[derive(Debug, Clone)]
#[allow(dead_code)] // Some fields only used by lancache_processor binary, not by other binaries
pub(crate) struct LogEntry {
    pub(crate) timestamp: NaiveDateTime,
    pub(crate) client_ip: String,
    pub(crate) service: String,
    pub(crate) url: String,
    pub(crate) status_code: i32,
    pub(crate) bytes_served: i64,
    pub(crate) cache_status: String,
    pub(crate) depot_id: Option<u32>,
}

/// Entry from stream-access.log containing session timing/speed data
/// Format: IP [timestamp] PROTOCOL STATUS BYTES_SENT BYTES_RECV DURATION "HOST"
/// Example: 172.16.1.143 [20/Dec/2025:07:50:58 -0600] TCP 200 7063 910 77.590 "cp601.prod.do.dsp.mp.microsoft.com"
#[derive(Debug, Clone)]
#[allow(dead_code)]
pub(crate) struct StreamLogEntry {
    pub(crate) client_ip: String,
    pub(crate) timestamp: NaiveDateTime,      // End of session (when log entry is written)
    pub(crate) protocol: String,              // TCP/UDP
    pub(crate) status: i32,
    pub(crate) bytes_sent: i64,               // To client (download direction)
    pub(crate) bytes_received: i64,           // From client (upload direction)
    pub(crate) session_duration: f64,         // Duration in seconds
    pub(crate) upstream_host: String,
}

impl StreamLogEntry {
    /// Calculate download speed in bytes per second
    pub(crate) fn download_speed_bps(&self) -> f64 {
        if self.session_duration > 0.0 {
            self.bytes_sent as f64 / self.session_duration
        } else {
            0.0
        }
    }

    /// Calculate upload speed in bytes per second
    pub(crate) fn upload_speed_bps(&self) -> f64 {
        if self.session_duration > 0.0 {
            self.bytes_received as f64 / self.session_duration
        } else {
            0.0
        }
    }

    /// Calculate session start time by subtracting duration from end timestamp
    pub(crate) fn session_start(&self) -> NaiveDateTime {
        let duration_millis = (self.session_duration * 1000.0) as i64;
        self.timestamp - TimeDelta::milliseconds(duration_millis)
    }

    /// Total bytes transferred (both directions)
    pub(crate) fn total_bytes(&self) -> i64 {
        self.bytes_sent + self.bytes_received
    }
}