use chrono::NaiveDateTime;

#[derive(Debug, Clone)]
pub struct LogEntry {
    pub timestamp: NaiveDateTime,
    pub client_ip: String,
    pub service: String,
    pub url: String,
    pub status_code: i32,
    pub bytes_served: i64,
    pub cache_status: String,
    pub depot_id: Option<u32>,
}