use anyhow::{Context, Result};
use flate2::read::GzDecoder;
use std::fs::File;
use std::io::{BufRead, BufReader, Read};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

#[cfg(target_os = "windows")]
use std::fs::OpenOptions;
#[cfg(target_os = "windows")]
use std::os::windows::fs::OpenOptionsExt;

/// Wraps the raw (compressed) file handle and counts every byte read from it.
/// Sits BELOW the gzip/zstd decoder, so the counter tracks on-disk bytes consumed,
/// which lets callers compute progress against `metadata().len()` without a
/// line-counting pre-pass.
#[allow(dead_code)]
struct CountingReader {
    inner: File,
    counter: Arc<AtomicU64>,
}

impl Read for CountingReader {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let n = self.inner.read(buf)?;
        self.counter.fetch_add(n as u64, Ordering::Relaxed);
        Ok(n)
    }
}

/// Unified log file reader that wraps different compression types
/// All variants implement BufRead through dynamic dispatch
pub struct LogFileReader {
    inner: Box<dyn BufRead>,
}

impl LogFileReader {
    /// Opens a log file and automatically detects compression based on file extension
    /// Supports: .log, .gz, .zst
    #[allow(dead_code)] // not every binary that includes this module uses every opener
    pub fn open<P: AsRef<Path>>(path: P) -> Result<Self> {
        let path = path.as_ref();
        let file = open_file_shared_read(path)?;
        Self::build(path, Box::new(file))
    }

    /// Opens a log file like [`LogFileReader::open`], but wraps the underlying file
    /// handle in a counting reader that adds every raw (compressed) byte read from
    /// disk to `byte_counter`. Used for byte-based progress reporting.
    #[allow(dead_code)]
    pub fn open_with_byte_counter<P: AsRef<Path>>(
        path: P,
        byte_counter: Arc<AtomicU64>,
    ) -> Result<Self> {
        let path = path.as_ref();
        let file = open_file_shared_read(path)?;
        let counting = CountingReader {
            inner: file,
            counter: byte_counter,
        };
        Self::build(path, Box::new(counting))
    }

    fn build(path: &Path, source: Box<dyn Read>) -> Result<Self> {
        let extension = path
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or("");

        // Reduced buffer sizes from 8MB to 512KB for better memory efficiency
        // 512KB is still large enough for good I/O performance while reducing memory footprint
        const BUFFER_SIZE: usize = 512 * 1024; // 512KB

        let reader: Box<dyn BufRead> = match extension {
            "gz" => {
                let decoder = GzDecoder::new(source);
                Box::new(BufReader::with_capacity(BUFFER_SIZE, decoder))
            }
            "zst" => {
                let decoder = zstd::Decoder::new(source)?;
                Box::new(BufReader::with_capacity(BUFFER_SIZE, decoder))
            }
            _ => {
                // Plain text or unrecognized - treat as plain
                Box::new(BufReader::with_capacity(BUFFER_SIZE, source))
            }
        };

        Ok(LogFileReader { inner: reader })
    }

    /// Read a line from the log file (works transparently across all compression types)
    #[allow(dead_code)] // not every binary that includes this module uses every read method
    pub fn read_line(&mut self, buf: &mut String) -> Result<usize> {
        self.inner
            .read_line(buf)
            .context("Failed to read line from log file")
    }

    /// Read raw bytes up to and including the next `\n` (works transparently across
    /// all compression types). Skips the per-line UTF-8 validation that `read_line`
    /// performs, which matters in hot rewrite loops.
    #[allow(dead_code)]
    pub fn read_until_newline(&mut self, buf: &mut Vec<u8>) -> Result<usize> {
        self.inner
            .read_until(b'\n', buf)
            .context("Failed to read line bytes from log file")
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
