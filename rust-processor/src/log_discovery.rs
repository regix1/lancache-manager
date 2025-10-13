use anyhow::{Context, Result};
use std::cmp::Ordering;
use std::path::{Path, PathBuf};

/// Represents a discovered log file with metadata for sorting
#[derive(Debug, Clone)]
pub struct LogFile {
    pub path: PathBuf,
    pub rotation_number: Option<u32>,
    #[allow(dead_code)]
    pub is_compressed: bool,
}

impl LogFile {
    /// Parse a log file path to extract rotation number and compression info
    /// Examples:
    ///   - access.log -> rotation_number = None (current file)
    ///   - access.log.1 -> rotation_number = Some(1)
    ///   - access.log.2.gz -> rotation_number = Some(2), is_compressed = true
    ///   - access.log.10.zst -> rotation_number = Some(10), is_compressed = true
    pub fn from_path<P: AsRef<Path>>(path: P) -> Self {
        let path = path.as_ref();
        let file_name = path
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("");

        // Check for compression extensions
        let is_compressed = file_name.ends_with(".gz") || file_name.ends_with(".zst");

        // Remove compression extension if present
        let name_without_compression = if is_compressed {
            if let Some(pos) = file_name.rfind('.') {
                &file_name[..pos]
            } else {
                file_name
            }
        } else {
            file_name
        };

        // Extract rotation number
        // Look for pattern like "access.log.123" -> 123
        let rotation_number = if let Some(pos) = name_without_compression.rfind('.') {
            let number_part = &name_without_compression[pos + 1..];
            number_part.parse::<u32>().ok()
        } else {
            None
        };

        LogFile {
            path: path.to_path_buf(),
            rotation_number,
            is_compressed,
        }
    }
}

impl PartialEq for LogFile {
    fn eq(&self, other: &Self) -> bool {
        self.rotation_number == other.rotation_number
    }
}

impl Eq for LogFile {}

impl PartialOrd for LogFile {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for LogFile {
    /// Sort log files from oldest to newest
    /// - Files with rotation numbers are older than current file
    /// - Higher rotation numbers are older than lower rotation numbers
    /// - Current file (no rotation number) is newest
    fn cmp(&self, other: &Self) -> Ordering {
        match (self.rotation_number, other.rotation_number) {
            (None, None) => Ordering::Equal,
            (None, Some(_)) => Ordering::Greater, // Current file is newest
            (Some(_), None) => Ordering::Less,    // Rotated file is older
            (Some(a), Some(b)) => b.cmp(&a),      // Higher number = older (reverse order)
        }
    }
}

/// Discover all log files matching a base pattern
/// Returns files sorted from oldest to newest
pub fn discover_log_files<P: AsRef<Path>>(log_directory: P, base_name: &str) -> Result<Vec<LogFile>> {
    let log_dir = log_directory.as_ref();

    if !log_dir.exists() {
        return Ok(Vec::new());
    }

    let mut log_files = Vec::new();

    // Read directory entries
    let entries = std::fs::read_dir(log_dir)
        .with_context(|| format!("Failed to read log directory: {}", log_dir.display()))?;

    for entry in entries {
        let entry = entry?;
        let path = entry.path();

        if !path.is_file() {
            continue;
        }

        let file_name = path
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("");

        // Match files like:
        // - access.log
        // - access.log.1
        // - access.log.2.gz
        // - access.log.10.zst
        if file_name.starts_with(base_name) {
            // Ensure it's either exact match or followed by '.' (to avoid matching "access.log.bak")
            let suffix = &file_name[base_name.len()..];
            if suffix.is_empty() || suffix.starts_with('.') {
                log_files.push(LogFile::from_path(path));
            }
        }
    }

    // Sort from oldest to newest
    log_files.sort();

    Ok(log_files)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_current_log() {
        let log = LogFile::from_path("access.log");
        assert_eq!(log.rotation_number, None);
        assert!(!log.is_compressed);
    }

    #[test]
    fn test_parse_rotated_log() {
        let log = LogFile::from_path("access.log.1");
        assert_eq!(log.rotation_number, Some(1));
        assert!(!log.is_compressed);
    }

    #[test]
    fn test_parse_compressed_gzip() {
        let log = LogFile::from_path("access.log.2.gz");
        assert_eq!(log.rotation_number, Some(2));
        assert!(log.is_compressed);
    }

    #[test]
    fn test_parse_compressed_zstd() {
        let log = LogFile::from_path("access.log.5.zst");
        assert_eq!(log.rotation_number, Some(5));
        assert!(log.is_compressed);
    }

    #[test]
    fn test_sorting_order() {
        let mut logs = vec![
            LogFile::from_path("access.log"),      // Current (newest)
            LogFile::from_path("access.log.1"),    // Recent rotation
            LogFile::from_path("access.log.10.gz"), // Old rotation
            LogFile::from_path("access.log.2.zst"), // Mid rotation
        ];

        logs.sort();

        // Should be sorted oldest to newest: 10 -> 2 -> 1 -> current
        assert_eq!(logs[0].rotation_number, Some(10));
        assert_eq!(logs[1].rotation_number, Some(2));
        assert_eq!(logs[2].rotation_number, Some(1));
        assert_eq!(logs[3].rotation_number, None);
    }

    #[test]
    fn test_double_digit_rotation() {
        let log = LogFile::from_path("access.log.15.gz");
        assert_eq!(log.rotation_number, Some(15));
    }
}
