use anyhow::Result;
use chrono::Utc;
use serde::Serialize;
use std::io::Write;
use std::path::Path;
use std::thread;
use std::time::Duration;
use tempfile::NamedTempFile;

/// Write progress data to a JSON file with atomic write-and-rename to avoid race conditions
#[allow(dead_code)]
pub fn write_progress_json<T: Serialize>(progress_path: &Path, progress: &T) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;

    // Use tempfile for automatic cleanup on error
    let parent_dir = progress_path.parent().unwrap_or(Path::new("."));
    let mut temp_file = NamedTempFile::new_in(parent_dir)?;

    // Write JSON to temp file
    temp_file.write_all(json.as_bytes())?;
    temp_file.flush()?;

    // IMPORTANT: On Windows, we must close the file handle before persisting
    // Convert to TempPath which closes the file while keeping the path
    let temp_path = temp_file.into_temp_path();

    // Now atomically replace the target file
    // This works on Windows because the file handle is closed
    temp_path.persist(progress_path)?;

    Ok(())
}

/// Write progress with exponential backoff retry logic using atomic operations
#[allow(dead_code)]
pub fn write_progress_with_retry<T: Serialize>(
    progress_path: &Path,
    progress: &T,
    max_retries: usize,
) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;
    let parent_dir = progress_path.parent().unwrap_or(Path::new("."));

    let mut retries = 0;
    loop {
        // Use atomic write-and-rename to avoid race conditions
        match NamedTempFile::new_in(parent_dir) {
            Ok(mut temp_file) => {
                match temp_file.write_all(json.as_bytes()).and_then(|_| temp_file.flush()) {
                    Ok(_) => {
                        // Close file handle and persist atomically
                        let temp_path = temp_file.into_temp_path();
                        match temp_path.persist(progress_path) {
                            Ok(_) => break,
                            Err(_) if retries < max_retries => {
                                retries += 1;
                                thread::sleep(Duration::from_millis(10 * retries as u64));
                                continue;
                            }
                            Err(e) => return Err(anyhow::Error::new(e.error)),
                        }
                    }
                    Err(_) if retries < max_retries => {
                        retries += 1;
                        thread::sleep(Duration::from_millis(10 * retries as u64));
                        continue;
                    }
                    Err(e) => return Err(e.into()),
                }
            }
            Err(_) if retries < max_retries => {
                retries += 1;
                thread::sleep(Duration::from_millis(10 * retries as u64));
                continue;
            }
            Err(e) => return Err(e.into()),
        }
    }

    Ok(())
}

/// Helper to create a timestamp string in RFC3339 format
pub fn current_timestamp() -> String {
    Utc::now().to_rfc3339()
}
