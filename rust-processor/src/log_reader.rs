use anyhow::{Context, Result};
use flate2::read::GzDecoder;
use std::fs::File;
use std::io::{BufRead, BufReader};
use std::path::Path;

#[cfg(target_os = "windows")]
use std::fs::OpenOptions;
#[cfg(target_os = "windows")]
use std::os::windows::fs::OpenOptionsExt;

/// Unified log file reader that wraps different compression types
/// All variants implement BufRead through dynamic dispatch
pub struct LogFileReader {
    inner: Box<dyn BufRead>,
}

impl LogFileReader {
    /// Opens a log file and automatically detects compression based on file extension
    /// Supports: .log, .gz, .zst
    pub fn open<P: AsRef<Path>>(path: P) -> Result<Self> {
        let path = path.as_ref();
        let file = open_file_shared_read(path)?;

        let extension = path
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or("");

        // Reduced buffer sizes from 8MB to 512KB for better memory efficiency
        // 512KB is still large enough for good I/O performance while reducing memory footprint
        const BUFFER_SIZE: usize = 512 * 1024; // 512KB

        let reader: Box<dyn BufRead> = match extension {
            "gz" => {
                let decoder = GzDecoder::new(file);
                Box::new(BufReader::with_capacity(BUFFER_SIZE, decoder))
            }
            "zst" => {
                let decoder = zstd::Decoder::new(file)?;
                Box::new(BufReader::with_capacity(BUFFER_SIZE, decoder))
            }
            _ => {
                // Plain text or unrecognized - treat as plain
                Box::new(BufReader::with_capacity(BUFFER_SIZE, file))
            }
        };

        Ok(LogFileReader { inner: reader })
    }

    /// Read a line from the log file (works transparently across all compression types)
    pub fn read_line(&mut self, buf: &mut String) -> Result<usize> {
        self.inner
            .read_line(buf)
            .context("Failed to read line from log file")
    }

    /// Get the underlying BufRead trait object for generic operations
    #[allow(dead_code)]
    pub fn as_buf_read(&mut self) -> &mut dyn BufRead {
        &mut *self.inner
    }
}

/// Opens a file for reading with proper sharing on Windows
/// This allows other processes (like lancache) to continue writing while we read
fn open_file_shared_read(path: &Path) -> Result<File> {
    #[cfg(target_os = "windows")]
    {
        // On Windows, use share_mode to allow other processes to read, write, and delete
        // FILE_SHARE_READ (0x01) | FILE_SHARE_WRITE (0x02) | FILE_SHARE_DELETE (0x04) = 0x07
        OpenOptions::new()
            .read(true)
            .share_mode(0x07)
            .open(path)
            .with_context(|| format!("Failed to open file with shared access: {}", path.display()))
    }

    #[cfg(not(target_os = "windows"))]
    {
        // On Unix, File::open already allows sharing
        File::open(path)
            .with_context(|| format!("Failed to open file: {}", path.display()))
    }
}
