use anyhow::Result;
use chrono::Utc;
use serde::Serialize;
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::Path;
use std::thread;
use std::time::Duration;

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

/// Write progress data to a JSON file with atomic write-and-rename to avoid race conditions
#[allow(dead_code)]
pub fn write_progress_json<T: Serialize>(progress_path: &Path, progress: &T) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;

    // Use atomic write-and-rename on all platforms to avoid race conditions
    // where other processes read the file while it's being truncated/written
    let temp_path = progress_path.with_extension("json.tmp");

    #[cfg(windows)]
    {
        // Write to temp file with sharing flags
        let mut file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(true)
            .share_mode(0x07) // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            .open(&temp_path)?;

        file.write_all(json.as_bytes())?;
        file.flush()?;
        drop(file); // Ensure file is closed before rename

        // Atomic rename - Windows allows this when file is opened with FILE_SHARE_DELETE
        fs::rename(&temp_path, progress_path)?;
    }

    #[cfg(not(windows))]
    {
        fs::write(&temp_path, &json)?;
        fs::rename(&temp_path, progress_path)?;
    }

    Ok(())
}

/// Write progress with exponential backoff retry logic
#[allow(dead_code)]
pub fn write_progress_with_retry<T: Serialize>(
    progress_path: &Path,
    progress: &T,
    max_retries: usize,
) -> Result<()> {
    let json = serde_json::to_string_pretty(progress)?;

    let mut retries = 0;
    loop {
        match File::create(progress_path) {
            Ok(mut file) => {
                match file.write_all(json.as_bytes()).and_then(|_| file.flush()) {
                    Ok(_) => break,
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
