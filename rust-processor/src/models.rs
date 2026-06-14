use chrono::NaiveDateTime;

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
    /// Blizzard TACT product code parsed from the CDN path (e.g. "wow", "fenris").
    /// Only populated for the `blizzard` service; None otherwise. Used to map a
    /// Blizzard download to a game name and to group sessions per game.
    pub(crate) tact_product: Option<String>,
    /// HTTP Range header value (e.g., "bytes=0-1048575"). Empty if not present.
    /// Used to distinguish WSUS/BITS range requests from corruption retries.
    pub(crate) http_range: String,
    /// Riot CDN host parsed from the access.log `$host` field (e.g. "lol.dyn.riotcdn.net").
    /// Only populated (lowercased) for the `riot` service; None otherwise. Riot bundle
    /// URLs carry no product slug, so the host is the only per-game discriminator — used
    /// to map a Riot download to a game name and to group/dedup sessions per game.
    pub(crate) cdn_host: Option<String>,
}