use chrono::NaiveDateTime;

#[derive(Debug, Clone)]
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