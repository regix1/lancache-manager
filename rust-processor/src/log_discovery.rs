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
        // - access.log (current file)
        // - access.log.1, access.log.2 (numbered rotations)
        // - access.log.2.gz, access.log.10.zst (numbered rotations with compression)
        // But exclude:
        // - .bak files (access.log.bak, access.log.1.bak, etc.)
        // - .tmp files (access.log.tmp, access.log.corruption_tmp, etc.)
        // - .old files (access.log.old)
        // - .backup files (access.log.backup)
        // - Any non-numeric suffixes
        if file_name.starts_with(base_name) && !file_name.ends_with(".bak") && !file_name.contains(".tmp") {
            // Ensure it's either exact match or followed by '.' (to avoid matching "access.logfoo")
            let suffix = &file_name[base_name.len()..];
            if suffix.is_empty() {
                // Exact match: access.log
                log_files.push(LogFile::from_path(path));
            } else if suffix.starts_with('.') {
                // Has a suffix like .1, .2.gz, .10.zst
                // Strip compression extensions first
                let name_without_compression = if file_name.ends_with(".gz") || file_name.ends_with(".zst") {
                    if let Some(pos) = file_name.rfind('.') {
                        &file_name[..pos]
                    } else {
                        file_name
                    }
                } else {
                    file_name
                };

                // Now check if the suffix after base_name is a valid rotation number
                let rotation_suffix = &name_without_compression[base_name.len()..];
                if rotation_suffix.starts_with('.') {
                    let number_part = &rotation_suffix[1..]; // Skip the '.'
                    // Only accept if it's a valid number (e.g., "1", "2", "10")
                    // This excludes .old, .backup, etc.
                    if !number_part.is_empty() && number_part.chars().all(|c| c.is_ascii_digit()) {
                        log_files.push(LogFile::from_path(path));
                    }
                }
            }
        }
    }

    // Sort from oldest to newest
    log_files.sort();

    Ok(log_files)
}
