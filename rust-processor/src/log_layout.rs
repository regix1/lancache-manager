use crate::log_discovery::{discover_log_files, LogFile};
use crate::models::LogEntry;
use anyhow::Result;
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

/// The monolithic lancache log stem. A directory containing this stem (or its rotations)
/// is processed exactly as it always has been.
pub const MONOLITHIC_STEM: &str = "access.log";

/// Bare-metal writes unmatched-vhost traffic here. It is a real source (positions advance
/// through it so rotation replay cannot occur) but its lines are never inserted.
#[allow(dead_code)] // used by log_processor; other binaries share this module
pub const FALLBACK_STEM: &str = "fallback-access.log";

/// How a discovered source attributes services.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SourceKind {
    /// `access.log`: lines carry their own `[service]` tag (cachelog format).
    Monolithic,
    /// A per-service bare-metal file (`steam-access.log`, ...): the filename is the
    /// service hint for http-detailed records. A cachelog `[tag]` in the file still wins.
    Service(String),
    /// `fallback-access.log`: counted and position-tracked, never ingested.
    Fallback,
}

/// One logical log source: a stem plus its rotation series ordered oldest -> newest.
#[derive(Debug, Clone)]
pub struct LogSource {
    pub stem: String,
    pub kind: SourceKind,
    pub files: Vec<LogFile>,
}

impl LogSource {
    /// Service hint for http-detailed records in this source. None for the monolithic
    /// stem (lines must self-identify) and for the fallback stem (never ingested).
    #[allow(dead_code)] // used by log_processor; other binaries share this module
    pub fn service_hint(&self) -> Option<&str> {
        match &self.kind {
            SourceKind::Service(service) => Some(service.as_str()),
            _ => None,
        }
    }
}

/// The resolved set of sources for a directory, after any `logs/ -> logs/http/` descent.
#[derive(Debug)]
pub struct SourceSet {
    /// The directory the sources actually live in (may be `<dir>/http`).
    #[allow(dead_code)] // used by log_processor; other binaries share this module
    pub dir: PathBuf,
    pub sources: Vec<LogSource>,
}

impl SourceSet {
    #[allow(dead_code)] // used by log_processor; other binaries share this module
    pub fn is_empty(&self) -> bool {
        self.sources.is_empty()
    }

    /// Presentation-only layout label. Never drives dispatch or capability.
    #[allow(dead_code)] // used by log_processor; other binaries share this module
    pub fn layout(&self) -> &'static str {
        let has_monolithic = self
            .sources
            .iter()
            .any(|s| s.kind == SourceKind::Monolithic);
        let has_bare_metal = self
            .sources
            .iter()
            .any(|s| matches!(s.kind, SourceKind::Service(_) | SourceKind::Fallback));
        match (has_monolithic, has_bare_metal) {
            (true, true) => "mixed",
            (false, true) => "bare_metal",
            _ => "monolithic",
        }
    }
}

/// Filename prefixes (before `-access.log`) that bare-metal actually writes: the five
/// per-service vhosts plus the special `fallback` file. This is a CLOSED set on purpose.
/// A log directory can hold other `*-access.log` files that are NOT lancache cache logs —
/// most commonly the nginx stream module's `stream-access.log` next to a monolithic
/// `access.log` — and treating those as per-service sources would misread a monolithic
/// datasource as bare-metal/mixed and wrongly gate its disk features. Only these names
/// count as bare-metal sources; anything else is ignored.
const BARE_METAL_SOURCE_PREFIXES: [&str; 6] = [
    "steam",
    "epicgames",
    "blizzard",
    "riot",
    "windows-update",
    "fallback",
];

/// True when `prefix` is one of the recognized bare-metal source filenames (case-insensitive).
fn is_recognized_bare_metal_prefix(prefix: &str) -> bool {
    let lower = prefix.to_ascii_lowercase();
    BARE_METAL_SOURCE_PREFIXES.iter().any(|known| *known == lower)
}

/// Map a per-service filename prefix (the part before `-access.log`) to the manager's
/// service name. Only called for prefixes already confirmed by
/// `is_recognized_bare_metal_prefix`, so the arms below are exhaustive for real inputs.
pub fn service_for_prefix(prefix: &str) -> String {
    let lower = prefix.to_ascii_lowercase();
    match lower.as_str() {
        "steam" => "steam".to_string(),
        "epicgames" => "epicgames".to_string(),
        "blizzard" => "blizzard".to_string(),
        "riot" => "riot".to_string(),
        // Bare-metal names the vhost `windows-update`; the manager's service key for that
        // traffic has always been `wsus` (the lancache cache-domains name).
        "windows-update" => "wsus".to_string(),
        _ => crate::service_utils::normalize_service_name(&lower),
    }
}

/// Derive the logical stem for a file name, stripping compression and rotation suffixes.
/// Returns None when the name is not an access-log series member.
/// `access.log`, `access.log.2.gz` -> `access.log`; `steam-access.log.1` -> `steam-access.log`.
fn logical_stem(file_name: &str) -> Option<String> {
    let (without_compression, is_compressed) = if let Some(name) = file_name
        .strip_suffix(".gz")
        .or_else(|| file_name.strip_suffix(".zst"))
    {
        (name, true)
    } else {
        (file_name, false)
    };

    let (base, has_rotation) = match without_compression.rfind('.') {
        Some(pos) => {
            let suffix = &without_compression[pos + 1..];
            if !suffix.is_empty() && suffix.chars().all(|c| c.is_ascii_digit()) {
                (&without_compression[..pos], true)
            } else {
                (without_compression, false)
            }
        }
        None => (without_compression, false),
    };

    // Discovery accepts compressed files only when they are numbered rotations.
    // Do not create a logical source for compression-only names that discovery
    // cannot put into the source's file series.
    if is_compressed && !has_rotation {
        return None;
    }

    if base == MONOLITHIC_STEM {
        return Some(base.to_string());
    }
    if let Some(prefix) = base.strip_suffix("-access.log") {
        // Only recognized bare-metal source names count. A stray `*-access.log` (e.g. the
        // nginx stream module's stream-access.log) must NOT become a per-service source, or
        // a monolithic datasource would be misread as bare-metal/mixed.
        if !prefix.is_empty() && is_recognized_bare_metal_prefix(prefix) {
            return Some(base.to_string());
        }
    }
    None
}

/// Kind for a logical stem. Lenient on purpose: a base name that is not an access-log
/// stem at all (a caller-supplied arbitrary file) is treated as monolithic, i.e. lines
/// must self-identify with a `[service]` tag.
pub fn kind_for_stem(stem: &str) -> SourceKind {
    if stem == MONOLITHIC_STEM {
        return SourceKind::Monolithic;
    }
    if stem == FALLBACK_STEM {
        return SourceKind::Fallback;
    }
    match stem.strip_suffix("-access.log") {
        Some(prefix) if !prefix.is_empty() => SourceKind::Service(service_for_prefix(prefix)),
        _ => SourceKind::Monolithic,
    }
}

/// Enumerate the access-log stems present in one directory (no descent).
fn stems_in(dir: &Path) -> Result<BTreeSet<String>> {
    let mut stems = BTreeSet::new();
    if !dir.exists() {
        return Ok(stems);
    }
    for entry in std::fs::read_dir(dir)? {
        let entry = entry?;
        if !entry.path().is_file() {
            continue;
        }
        let name = entry.file_name();
        let Some(name) = name.to_str() else { continue };
        if name.ends_with(".bak") || name.contains(".tmp") {
            continue;
        }
        if let Some(stem) = logical_stem(name) {
            stems.insert(stem);
        }
    }
    Ok(stems)
}

/// Discover every log source in `dir`: the `access.log` stem (if present) AND every
/// `*-access.log` stem (if present). When `dir` itself has no sources but `dir/http`
/// carries the bare-metal per-service topology, descend into it — the real bare-metal
/// tree is `logs/` (holding only `nginx-error.log` at top level) with the HTTP access
/// logs under `logs/http/`, and accepting the parent then reporting zero-work success
/// was exactly the trap this closes.
pub fn discover_log_sources<P: AsRef<Path>>(dir: P) -> Result<SourceSet> {
    let dir = dir.as_ref();
    let mut resolved_dir = dir.to_path_buf();
    let mut stems = stems_in(dir)?;

    if stems.is_empty() {
        let http_dir = dir.join("http");
        if http_dir.is_dir() {
            let http_stems = stems_in(&http_dir)?;
            let has_per_service = http_stems
                .iter()
                .any(|stem| matches!(kind_for_stem(stem), SourceKind::Service(_)));
            if has_per_service {
                resolved_dir = http_dir;
                stems = http_stems;
            }
        }
    }

    let mut sources = Vec::with_capacity(stems.len());
    for stem in stems {
        let files = discover_log_files(&resolved_dir, &stem)?;
        if files.is_empty() {
            continue;
        }
        sources.push(LogSource {
            kind: kind_for_stem(&stem),
            stem,
            files,
        });
    }

    Ok(SourceSet {
        dir: resolved_dir,
        sources,
    })
}

/// Why a recognized record was deliberately not ingested.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[allow(dead_code)] // used by log_processor; other binaries share this module
pub enum IgnoredReason {
    /// Carries the manager's own probe User-Agent marker (synthetic Status Check traffic).
    Probe,
    /// Heartbeat / health-check endpoint.
    Heartbeat,
    /// Line lives in `fallback-access.log` (position advances; never ingested).
    Fallback,
    /// An http-detailed record in a hint-less file (e.g. a renamed `access.log`): the
    /// format is recognized but there is no service attribution, so it cannot ingest.
    Hintless,
    /// Blank / whitespace-only record.
    Blank,
}

/// Typed classification of one complete record. Structural recognizers decide the bucket;
/// classification never depends on a database outcome.
#[derive(Debug)]
#[allow(clippy::large_enum_variant)]
#[allow(dead_code)] // used by log_processor; other binaries share this module
pub enum ParseOutcome {
    Parsed(LogEntry),
    RecognizedIgnored(IgnoredReason),
    /// Final record of a file with no trailing newline. Never counted toward positions:
    /// the writer is mid-line, and the completed line must ingest exactly once later.
    Incomplete,
    /// The record contains invalid UTF-8 and no recognizer accepts its lossy form.
    InvalidEncoding,
    Unrecognized,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn logical_stem_handles_rotation_and_compression() {
        assert_eq!(logical_stem("access.log").as_deref(), Some("access.log"));
        assert_eq!(logical_stem("access.log.1").as_deref(), Some("access.log"));
        assert_eq!(
            logical_stem("access.log.10.gz").as_deref(),
            Some("access.log")
        );
        assert_eq!(
            logical_stem("steam-access.log").as_deref(),
            Some("steam-access.log")
        );
        assert_eq!(
            logical_stem("steam-access.log.2.zst").as_deref(),
            Some("steam-access.log")
        );
        assert_eq!(
            logical_stem("windows-update-access.log.3").as_deref(),
            Some("windows-update-access.log")
        );
    }

    #[test]
    fn logical_stem_rejects_non_access_logs() {
        assert_eq!(logical_stem("nginx-error.log"), None);
        assert_eq!(logical_stem("error.log"), None);
        assert_eq!(logical_stem("-access.log"), None);
        assert_eq!(logical_stem("access.logfoo"), None);
        assert_eq!(logical_stem("stream.log"), None);
    }

    #[test]
    fn logical_stem_rejects_unknown_service_access_logs() {
        // The nginx stream module's log and other stray `*-access.log` files are NOT
        // lancache bare-metal sources and must never flip a monolithic dir to bare-metal.
        assert_eq!(logical_stem("stream-access.log"), None);
        assert_eq!(logical_stem("nginx-access.log"), None);
        assert_eq!(logical_stem("stream-access.log.1"), None);
        // The six real bare-metal sources still resolve.
        for name in [
            "steam-access.log",
            "epicgames-access.log",
            "blizzard-access.log",
            "riot-access.log",
            "windows-update-access.log",
            "fallback-access.log",
        ] {
            assert_eq!(logical_stem(name).as_deref(), Some(name));
        }
    }

    #[test]
    fn discovery_ignores_stream_access_log_next_to_monolithic() {
        // The reported real-world case: a monolithic access.log plus the nginx stream
        // module's stream-access.log must stay "monolithic", not become "mixed".
        let tmp = tempfile::tempdir().unwrap();
        touch(tmp.path(), "access.log");
        touch(tmp.path(), "stream-access.log");
        let set = discover_log_sources(tmp.path()).unwrap();
        assert_eq!(set.sources.len(), 1);
        assert_eq!(set.sources[0].stem, "access.log");
        assert_eq!(set.layout(), "monolithic");
    }

    #[test]
    fn logical_stem_rejects_compression_without_rotation() {
        assert_eq!(logical_stem("access.log.gz"), None);
        assert_eq!(logical_stem("steam-access.log.zst"), None);
        assert_eq!(
            logical_stem("access.log.1.gz").as_deref(),
            Some("access.log")
        );
    }

    #[test]
    fn service_map_pins_windows_update_to_wsus() {
        assert_eq!(service_for_prefix("steam"), "steam");
        assert_eq!(service_for_prefix("epicgames"), "epicgames");
        assert_eq!(service_for_prefix("blizzard"), "blizzard");
        assert_eq!(service_for_prefix("riot"), "riot");
        assert_eq!(service_for_prefix("windows-update"), "wsus");
        assert_eq!(service_for_prefix("Steam"), "steam");
    }

    #[test]
    fn kind_for_stem_covers_all_three_buckets() {
        assert_eq!(kind_for_stem("access.log"), SourceKind::Monolithic);
        assert_eq!(kind_for_stem("fallback-access.log"), SourceKind::Fallback);
        assert_eq!(
            kind_for_stem("windows-update-access.log"),
            SourceKind::Service("wsus".to_string())
        );
    }

    fn touch(dir: &Path, name: &str) {
        std::fs::write(dir.join(name), b"").unwrap();
    }

    #[test]
    fn discovery_monolithic_only() {
        let tmp = tempfile::tempdir().unwrap();
        touch(tmp.path(), "access.log");
        touch(tmp.path(), "access.log.1");
        let set = discover_log_sources(tmp.path()).unwrap();
        assert_eq!(set.sources.len(), 1);
        assert_eq!(set.sources[0].stem, "access.log");
        assert_eq!(set.sources[0].files.len(), 2);
        assert_eq!(set.layout(), "monolithic");
    }

    #[test]
    fn discovery_bare_metal_only() {
        let tmp = tempfile::tempdir().unwrap();
        touch(tmp.path(), "steam-access.log");
        touch(tmp.path(), "blizzard-access.log");
        touch(tmp.path(), "fallback-access.log");
        touch(tmp.path(), "nginx-error.log");
        let set = discover_log_sources(tmp.path()).unwrap();
        assert_eq!(set.sources.len(), 3);
        assert_eq!(set.layout(), "bare_metal");
        assert!(set.sources.iter().any(|s| s.kind == SourceKind::Fallback));
    }

    #[test]
    fn discovery_both_yields_mixed_and_all_sources() {
        let tmp = tempfile::tempdir().unwrap();
        touch(tmp.path(), "access.log");
        touch(tmp.path(), "steam-access.log");
        let set = discover_log_sources(tmp.path()).unwrap();
        assert_eq!(set.sources.len(), 2);
        assert_eq!(set.layout(), "mixed");
    }

    #[test]
    fn discovery_descends_into_http_for_bare_metal_topology() {
        let tmp = tempfile::tempdir().unwrap();
        touch(tmp.path(), "nginx-error.log");
        let http = tmp.path().join("http");
        std::fs::create_dir(&http).unwrap();
        touch(&http, "steam-access.log");
        touch(&http, "riot-access.log");
        let set = discover_log_sources(tmp.path()).unwrap();
        assert_eq!(set.dir, http);
        assert_eq!(set.sources.len(), 2);
        assert_eq!(set.layout(), "bare_metal");
    }

    #[test]
    fn discovery_does_not_descend_without_per_service_topology() {
        let tmp = tempfile::tempdir().unwrap();
        touch(tmp.path(), "nginx-error.log");
        let http = tmp.path().join("http");
        std::fs::create_dir(&http).unwrap();
        touch(&http, "access.log");
        let set = discover_log_sources(tmp.path()).unwrap();
        assert!(set.is_empty());
    }

    #[test]
    fn discovery_does_not_descend_for_fallback_only_http_dir() {
        let tmp = tempfile::tempdir().unwrap();
        let http = tmp.path().join("http");
        std::fs::create_dir(&http).unwrap();
        touch(&http, "fallback-access.log");

        let set = discover_log_sources(tmp.path()).unwrap();
        assert_eq!(set.dir, tmp.path());
        assert!(set.is_empty());
    }

    #[test]
    fn discovery_empty_dir_is_empty_not_error() {
        let tmp = tempfile::tempdir().unwrap();
        let set = discover_log_sources(tmp.path()).unwrap();
        assert!(set.is_empty());
        assert_eq!(set.layout(), "monolithic");
    }
}
