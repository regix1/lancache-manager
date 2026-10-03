use anyhow::{Context, Result};
use flate2::read::GzDecoder;
use std::fs::File;
use std::io::{BufRead, BufReader, Read, Seek, SeekFrom};
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
        Self::open_with_byte_counter_identity(path, byte_counter, None)
            .map(|(reader, _, _, _)| reader)
    }

    /// Opens a counted reader and validates an optional saved prefix on the same handle.
    pub fn open_with_byte_counter_identity<P: AsRef<Path>>(
        path: P,
        byte_counter: Arc<AtomicU64>,
        mark: Option<&crate::log_resume::FileMark>,
    ) -> Result<(Self, crate::log_purge::FileIdentity, u64, bool)> {
        let path = path.as_ref();
        let mut file = open_file_shared_read(path)?;
        let identity = crate::log_purge::file_identity_of(&file)?;
        let len = file.metadata()?.len();
        let prefix_matched = mark.is_some_and(|mark| {
            if identity != mark.identity
                || len <= mark.len
                || matches!(
                    path.extension().and_then(|value| value.to_str()),
                    Some("gz" | "zst")
                )
            {
                return false;
            }
            let (Some(offset), Some(expected_crc)) = (mark.offset, mark.tail_crc) else {
                return false;
            };
            offset <= mark.len
                && len >= offset
                && crate::log_resume::tail_crc_of(&mut file, offset).ok() == Some(expected_crc)
        });
        file.seek(SeekFrom::Start(0))?;
        let counting = CountingReader {
            inner: file,
            counter: byte_counter,
        };
        Self::build(path, Box::new(counting)).map(|reader| (reader, identity, len, prefix_matched))
    }

    /// Opens a plain log file at a validated byte offset. A changed resume target returns
    /// `None` so the caller can restart the series with its line position.
    pub fn open_at_offset<P: AsRef<Path>>(
        path: P,
        offset: u64,
        expected: &crate::log_purge::FileIdentity,
        tail_crc: u32,
        byte_counter: Arc<AtomicU64>,
    ) -> Result<Option<Self>> {
        let path = path.as_ref();
        if matches!(
            path.extension().and_then(|value| value.to_str()),
            Some("gz" | "zst")
        ) {
            anyhow::bail!("compressed log files cannot resume at a byte offset")
        }

        let mut file = open_file_shared_read(path)?;
        if crate::log_purge::file_identity_of(&file)? != *expected {
            return Ok(None);
        }
        if file.metadata()?.len() < offset
            || crate::log_resume::tail_crc_of(&mut file, offset)? != tail_crc
        {
            return Ok(None);
        }
        file.seek(SeekFrom::Start(offset))?;
        byte_counter.store(offset, Ordering::Relaxed);
        let counting = CountingReader {
            inner: file,
            counter: byte_counter,
        };
        Self::build(path, Box::new(counting)).map(Some)
    }

    fn build(path: &Path, source: Box<dyn Read>) -> Result<Self> {
        let extension = path.extension().and_then(|e| e.to_str()).unwrap_or("");

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
        File::open(path).with_context(|| format!("Failed to open file: {}", path.display()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::log_purge::file_identity;

    #[test]
    fn open_at_offset_reads_only_the_remaining_lines() {
        let directory = tempfile::tempdir().expect("create reader fixture");
        let path = directory.path().join("access.log");
        std::fs::write(&path, b"one\ntwo\nthree\n").expect("write reader fixture");
        let identity = file_identity(&path).expect("read fixture identity");
        let tail_crc = crate::log_resume::tail_crc(&path, 8).expect("hash fixture tail");
        let counter = Arc::new(AtomicU64::new(0));
        let mut reader =
            LogFileReader::open_at_offset(&path, 8, &identity, tail_crc, counter.clone())
                .expect("open at offset")
                .expect("identity remains current");
        let mut line = String::new();

        reader.read_line(&mut line).expect("read remaining line");

        assert_eq!(line, "three\n");
        assert_eq!(counter.load(Ordering::Relaxed), 14);
    }

    #[test]
    fn open_at_offset_rejects_compressed_files() {
        let directory = tempfile::tempdir().expect("create reader fixture");
        let path = directory.path().join("access.log.gz");
        std::fs::write(&path, b"unused").expect("write compressed fixture");
        let identity = file_identity(&path).expect("read fixture identity");

        assert!(
            LogFileReader::open_at_offset(&path, 1, &identity, 0, Arc::new(AtomicU64::new(0)))
                .is_err()
        );
    }

    #[test]
    fn open_at_offset_refuses_a_replaced_file() {
        let directory = tempfile::tempdir().expect("create reader fixture");
        let path = directory.path().join("access.log");
        let old = directory.path().join("access.log.old");
        std::fs::write(&path, b"old\n").expect("write original fixture");
        let identity = file_identity(&path).expect("read original identity");
        let tail_crc = crate::log_resume::tail_crc(&path, 1).expect("hash original tail");
        std::fs::rename(&path, &old).expect("move original fixture");
        std::fs::write(&path, b"replacement\n").expect("write replacement fixture");

        let reader = LogFileReader::open_at_offset(
            &path,
            1,
            &identity,
            tail_crc,
            Arc::new(AtomicU64::new(0)),
        )
        .expect("check replacement identity");

        assert!(reader.is_none());
    }

    #[test]
    fn open_at_offset_refuses_a_rewritten_file() {
        let directory = tempfile::tempdir().expect("create reader fixture");
        let path = directory.path().join("access.log");
        std::fs::write(&path, b"a\nb\nc\n").expect("write original fixture");
        let identity = file_identity(&path).expect("read original identity");
        let tail_crc = crate::log_resume::tail_crc(&path, 6).expect("hash original tail");
        std::fs::write(&path, b"d\ne\nf\ng\nh\n").expect("rewrite fixture");
        assert_eq!(
            file_identity(&path).expect("read rewritten identity"),
            identity
        );

        let reader = LogFileReader::open_at_offset(
            &path,
            6,
            &identity,
            tail_crc,
            Arc::new(AtomicU64::new(0)),
        )
        .expect("check rewritten tail");

        assert!(reader.is_none());
    }

    #[test]
    fn open_at_offset_refuses_a_shortened_file() {
        let directory = tempfile::tempdir().expect("create reader fixture");
        let path = directory.path().join("access.log");
        std::fs::write(&path, b"a\nb\nc\n").expect("write original fixture");
        let identity = file_identity(&path).expect("read original identity");
        let tail_crc = crate::log_resume::tail_crc(&path, 6).expect("hash original tail");
        std::fs::write(&path, b"d\n").expect("shorten fixture");
        assert_eq!(
            file_identity(&path).expect("read shortened identity"),
            identity
        );

        let reader = LogFileReader::open_at_offset(
            &path,
            6,
            &identity,
            tail_crc,
            Arc::new(AtomicU64::new(0)),
        )
        .expect("check shortened file");

        assert!(reader.is_none());
    }

    #[test]
    fn counted_reader_proves_an_appended_plain_prefix_and_rewinds() {
        let directory = tempfile::tempdir().expect("create reader fixture");
        let path = directory.path().join("access.log.1");
        std::fs::write(&path, b"a\nb\n").expect("write saved prefix");
        let mark = crate::log_resume::FileMark {
            identity: file_identity(&path).expect("read saved identity"),
            len: 4,
            records: 2,
            offset: Some(4),
            tail_crc: Some(crate::log_resume::tail_crc(&path, 4).expect("hash saved prefix")),
        };
        std::fs::write(&path, b"a\nb\nc\n").expect("append reader fixture");
        let counter = Arc::new(AtomicU64::new(0));

        let (mut reader, identity, len, prefix_matched) =
            LogFileReader::open_with_byte_counter_identity(&path, counter.clone(), Some(&mark))
                .expect("open appended fixture");
        let mut contents = String::new();
        reader
            .inner
            .read_to_string(&mut contents)
            .expect("read appended fixture");

        assert_eq!(identity, mark.identity);
        assert_eq!(len, 6);
        assert!(prefix_matched);
        assert_eq!(contents, "a\nb\nc\n");
        assert_eq!(counter.load(Ordering::Relaxed), 6);
    }

    #[test]
    fn counted_reader_refuses_a_changed_plain_prefix_and_rewinds() {
        let directory = tempfile::tempdir().expect("create reader fixture");
        let path = directory.path().join("access.log.1");
        std::fs::write(&path, b"a\nb\n").expect("write saved prefix");
        let mark = crate::log_resume::FileMark {
            identity: file_identity(&path).expect("read saved identity"),
            len: 4,
            records: 2,
            offset: Some(4),
            tail_crc: Some(crate::log_resume::tail_crc(&path, 4).expect("hash saved prefix")),
        };
        std::fs::write(&path, b"x\ny\nz\n").expect("rewrite reader fixture");

        let (mut reader, identity, len, prefix_matched) =
            LogFileReader::open_with_byte_counter_identity(
                &path,
                Arc::new(AtomicU64::new(0)),
                Some(&mark),
            )
            .expect("open rewritten fixture");
        let mut contents = String::new();
        reader
            .inner
            .read_to_string(&mut contents)
            .expect("read rewritten fixture");

        assert_eq!(identity, mark.identity);
        assert_eq!(len, 6);
        assert!(!prefix_matched);
        assert_eq!(contents, "x\ny\nz\n");
    }
}
