//! Byte-offset resume points supplement the public line-count positions. File identities include
//! the device, so a remount refuses a resume. If the sidecar is missing or invalidated, one pass
//! uses the line skip and writes a fresh sidecar.

use crate::log_purge::{file_identity, FileIdentity};
use anyhow::Result;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

pub const RESUME_SCHEMA_VERSION: u32 = 1;
/// Bytes hashed immediately before a saved offset to detect a rewritten file.
pub const TAIL_CRC_BYTES: u64 = 4096;

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
pub struct FileMark {
    pub identity: FileIdentity,
    pub len: u64,
    pub records: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub offset: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tail_crc: Option<u32>,
    /// The member would not open, so none of its records were imported. A pass that resumes
    /// past it never opens it again, so the mark carries that forward.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub unread: bool,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
pub struct StemResume {
    pub position: u64,
    pub older_files: Vec<FileMark>,
    pub file_identity: FileIdentity,
    pub offset: u64,
    pub file_records: u64,
    pub tail_crc: u32,
}

#[derive(Serialize, Deserialize, Clone, Debug, Default, PartialEq, Eq)]
pub struct ResumeFile {
    pub schema_version: u32,
    pub stems: BTreeMap<String, StemResume>,
}

pub struct ResumePoint {
    pub file_index: usize,
    pub offset: u64,
    pub records_before: u64,
    pub older_files: Vec<FileMark>,
}

pub fn load(path: &Path) -> ResumeFile {
    let contents = match std::fs::read_to_string(path) {
        Ok(contents) => contents,
        // A first pass and a reset log position both leave no file, so its absence is normal.
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return ResumeFile::default();
        }
        Err(error) => {
            eprintln!(
                "Warning: resume file {} unreadable ({}); using the line position",
                path.display(),
                error
            );
            return ResumeFile::default();
        }
    };
    let file = match serde_json::from_str::<ResumeFile>(&contents) {
        Ok(file) => file,
        Err(error) => {
            eprintln!(
                "Warning: resume file {} unparseable ({}); using the line position",
                path.display(),
                error
            );
            return ResumeFile::default();
        }
    };
    if file.schema_version != RESUME_SCHEMA_VERSION {
        eprintln!(
            "Warning: resume file {} has schema version {}; using the line position",
            path.display(),
            file.schema_version
        );
        return ResumeFile::default();
    }
    file
}

pub fn save(path: &Path, file: &ResumeFile) -> Result<()> {
    let mut saved = file.clone();
    saved.schema_version = RESUME_SCHEMA_VERSION;
    crate::progress_utils::write_progress_json(path, &saved)
}

pub fn tail_crc(path: &Path, offset: u64) -> std::io::Result<u32> {
    let mut file = std::fs::File::open(path)?;
    tail_crc_of(&mut file, offset)
}

/// Hashes the bytes immediately before an offset on an already opened file.
pub fn tail_crc_of(file: &mut std::fs::File, offset: u64) -> std::io::Result<u32> {
    let size = offset.min(TAIL_CRC_BYTES);
    file.seek(SeekFrom::Start(offset - size))?;
    let mut bytes = vec![0; size as usize];
    file.read_exact(&mut bytes)?;
    Ok(crc32fast::hash(&bytes))
}

pub fn resume_point(
    entry: &StemResume,
    start_position: u64,
    files: &[PathBuf],
) -> Option<ResumePoint> {
    if entry.position != start_position {
        return None;
    }

    let file_index = files.iter().position(|path| {
        file_identity(path).ok().as_ref() == Some(&entry.file_identity)
            && !matches!(
                path.extension().and_then(|value| value.to_str()),
                Some("gz" | "zst")
            )
    })?;
    if file_index > entry.older_files.len() {
        return None;
    }

    let suffix_start = entry.older_files.len() - file_index;
    let older_files = entry.older_files[suffix_start..].to_vec();
    for (path, mark) in files[..file_index].iter().zip(&older_files) {
        let identity = file_identity(path).ok()?;
        let len = std::fs::metadata(path).ok()?.len();
        if identity != mark.identity || len != mark.len || !proof_holds(mark, path, false) {
            return None;
        }
    }

    if std::fs::metadata(&files[file_index]).ok()?.len() < entry.offset
        || tail_crc(&files[file_index], entry.offset).ok()? != entry.tail_crc
    {
        return None;
    }

    Some(ResumePoint {
        file_index,
        offset: entry.offset,
        records_before: older_files.iter().map(|mark| mark.records).sum::<u64>()
            + entry.file_records,
        older_files,
    })
}

fn plain(path: &Path) -> bool {
    !matches!(
        path.extension().and_then(|value| value.to_str()),
        Some("gz" | "zst")
    )
}

/// A mark saved with a hash names a file only while that file still hashes the same: the file
/// system hands a deleted log's number to a new log, which can reach the same length. A hash that
/// cannot be read is no evidence for the mark, and `unreadable_counts` says whether it also counts
/// against it: a caller that decides what to skip passes false, because a log that will not open
/// may be a new one; the caller that runs after a member failed to open passes true.
fn proof_holds(mark: &FileMark, path: &Path, unreadable_counts: bool) -> bool {
    let (Some(offset), Some(expected_crc)) = (mark.offset, mark.tail_crc) else {
        return true;
    };
    plain(path)
        && match tail_crc(path, offset) {
            Ok(crc) => crc == expected_crc,
            Err(_) => unreadable_counts,
        }
}

fn mark_matches(mark: &FileMark, path: &Path, identity: &FileIdentity, len: u64) -> bool {
    if identity != &mark.identity {
        return false;
    }
    if len == mark.len {
        return proof_holds(mark, path, false);
    }
    if len <= mark.len || !plain(path) {
        return false;
    }
    let (Some(offset), Some(expected_crc)) = (mark.offset, mark.tail_crc) else {
        return false;
    };
    offset <= mark.len && len >= offset && tail_crc(path, offset).ok() == Some(expected_crc)
}

pub fn target_matches(entry: &StemResume, path: &Path, identity: &FileIdentity, len: u64) -> bool {
    plain(path)
        && identity == &entry.file_identity
        && len >= entry.offset
        && tail_crc(path, entry.offset).ok() == Some(entry.tail_crc)
}

/// Returns the saved record count that is proven absent before the earliest surviving anchor.
/// With no anchor left, a rotation may have deleted read records and renamed unread ones, so
/// none of the position is proven and the whole of it counts: readers start from the series
/// start and the duplicate check absorbs the records read again.
pub fn deleted_prefix_records(entry: &StemResume, files: &[PathBuf]) -> u64 {
    proven_deleted_records(entry, files).unwrap_or(entry.position)
}

/// Returns the saved record count proven absent before the earliest surviving anchor, or None
/// when no saved log or saved current log is left to prove where the importer stopped.
pub fn proven_deleted_records(entry: &StemResume, files: &[PathBuf]) -> Option<u64> {
    let current: Vec<(&Path, FileIdentity, u64)> = files
        .iter()
        .filter_map(|path| {
            Some((
                path.as_path(),
                file_identity(path).ok()?,
                std::fs::metadata(path).ok()?.len(),
            ))
        })
        .collect();
    if let Some(first_present) = entry.older_files.iter().position(|mark| {
        current
            .iter()
            .any(|(path, identity, len)| mark_matches(mark, path, identity, *len))
    }) {
        return Some(
            entry.older_files[..first_present]
                .iter()
                .map(|mark| mark.records)
                .sum(),
        );
    }
    current
        .iter()
        .any(|(path, identity, len)| target_matches(entry, path, identity, *len))
        .then(|| entry.older_files.iter().map(|mark| mark.records).sum())
}

/// Returns a saved record count only when the observed member proves the saved boundary.
pub fn mark_records(
    entry: &StemResume,
    path: &Path,
    identity: &FileIdentity,
    len: u64,
    prefix_matched: bool,
) -> Option<u64> {
    if let Some(mark) = entry
        .older_files
        .iter()
        .find(|mark| &mark.identity == identity)
    {
        return ((len == mark.len && proof_holds(mark, path, true))
            || (len > mark.len && prefix_matched))
            .then_some(mark.records);
    }
    (identity == &entry.file_identity && prefix_matched).then_some(entry.file_records)
}

/// Chooses the sidecar entry retained after a stem finishes or freezes.
pub fn entry_after_run(
    previous: Option<&StemResume>,
    fresh: Option<StemResume>,
    frozen: bool,
    published: u64,
    start: u64,
) -> Option<StemResume> {
    if !frozen {
        fresh
    } else if published == start {
        previous.cloned()
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn write(path: &Path, contents: &[u8]) -> FileMark {
        std::fs::write(path, contents).expect("write resume fixture");
        FileMark {
            identity: file_identity(path).expect("read resume fixture identity"),
            len: contents.len() as u64,
            records: contents.iter().filter(|byte| **byte == b'\n').count() as u64,
            offset: None,
            tail_crc: None,
            unread: false,
        }
    }

    fn entry(path: &Path, position: u64, offset: u64) -> StemResume {
        StemResume {
            position,
            older_files: Vec::new(),
            file_identity: file_identity(path).expect("read resume fixture identity"),
            offset,
            file_records: 2,
            tail_crc: tail_crc(path, offset).expect("hash resume fixture"),
        }
    }

    #[test]
    fn resume_requires_position_identity_size_and_tail() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let path = directory.path().join("access.log");
        std::fs::write(&path, b"one\ntwo\nthree\n").expect("write resume fixture");
        let saved = entry(&path, 2, 8);

        assert!(resume_point(&saved, 3, std::slice::from_ref(&path)).is_none());

        std::fs::write(&path, b"short\n").expect("truncate resume fixture");
        assert!(resume_point(&saved, 2, std::slice::from_ref(&path)).is_none());

        std::fs::write(&path, b"ONE\ntwo\nthree\n").expect("rewrite resume fixture");
        assert!(resume_point(&saved, 2, std::slice::from_ref(&path)).is_none());
    }

    #[test]
    fn resume_accepts_a_suffix_after_oldest_files_are_deleted() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let oldest = directory.path().join("access.log.2");
        let older = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let oldest_mark = write(&oldest, b"oldest\n");
        let older_mark = write(&older, b"older\n");
        std::fs::write(&current, b"one\ntwo\nthree\n").expect("write current fixture");
        let mut saved = entry(&current, 4, 8);
        saved.older_files = vec![oldest_mark, older_mark.clone()];
        std::fs::remove_file(&oldest).expect("remove oldest fixture");

        let point = resume_point(&saved, 4, &[older, current]).expect("resume from suffix");

        assert_eq!(point.file_index, 1);
        assert_eq!(
            point.records_before,
            older_mark.records + saved.file_records
        );
        assert_eq!(point.older_files, vec![older_mark]);
    }

    #[test]
    fn resume_rejects_compressed_target_and_changed_older_member() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let older = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let older_mark = write(&older, b"older\n");
        std::fs::write(&current, b"one\ntwo\n").expect("write current fixture");
        let mut saved = entry(&current, 3, 8);
        saved.older_files = vec![older_mark];
        std::fs::write(&older, b"changed\n").expect("replace older fixture");
        assert!(resume_point(&saved, 3, &[older, current]).is_none());

        let compressed = directory.path().join("access.log.gz");
        std::fs::write(&compressed, b"one\ntwo\n").expect("write compressed fixture");
        let compressed_saved = entry(&compressed, 2, 8);
        assert!(resume_point(&compressed_saved, 2, &[compressed]).is_none());
    }

    #[test]
    fn deleted_prefix_counts_only_missing_oldest_marks() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let first = directory.path().join("access.log.3");
        let second = directory.path().join("access.log.2");
        let current = directory.path().join("access.log");
        let first_mark = write(&first, b"1\n2\n3\n4\n5\n");
        let second_mark = write(&second, b"1\n2\n3\n");
        std::fs::write(&current, b"now\n").expect("write current fixture");
        let mut saved = entry(&current, 9, 4);
        saved.older_files = vec![first_mark.clone(), second_mark.clone()];

        assert_eq!(
            deleted_prefix_records(&saved, &[first.clone(), second.clone(), current.clone()]),
            0
        );
        std::fs::remove_file(&first).expect("remove oldest fixture");
        assert_eq!(
            deleted_prefix_records(&saved, &[second.clone(), current.clone()]),
            first_mark.records
        );
        std::fs::remove_file(&second).expect("remove remaining rotation");
        assert_eq!(
            deleted_prefix_records(&saved, std::slice::from_ref(&current)),
            first_mark.records + second_mark.records
        );
    }

    #[test]
    fn mark_records_finds_older_resume_and_unknown_identities() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let older = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let unknown = directory.path().join("unknown.log");
        let older_mark = write(&older, b"old\n");
        std::fs::write(&current, b"one\ntwo\n").expect("write current fixture");
        std::fs::write(&unknown, b"unknown\n").expect("write unknown fixture");
        let mut saved = entry(&current, 3, 8);
        saved.older_files = vec![older_mark.clone()];

        assert_eq!(
            mark_records(&saved, &older, &older_mark.identity, older_mark.len, false),
            Some(1)
        );
        assert_eq!(
            mark_records(&saved, &current, &saved.file_identity, 8, true),
            Some(2)
        );
        assert_eq!(
            mark_records(
                &saved,
                &unknown,
                &file_identity(&unknown).expect("read unknown identity"),
                8,
                false,
            ),
            None
        );
    }

    #[test]
    fn deleted_prefix_requires_a_surviving_anchor() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let older = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let moved_older = directory.path().join("saved-older");
        let moved_current = directory.path().join("saved-current");
        let older_mark = write(&older, b"old-a\nold-b\n");
        std::fs::write(&current, b"current-a\ncurrent-b\n").expect("write current fixture");
        let mut saved = entry(&current, 4, 20);
        saved.older_files = vec![older_mark];
        std::fs::rename(&older, &moved_older).expect("move saved older fixture");
        std::fs::rename(&current, &moved_current).expect("move saved current fixture");
        std::fs::write(&older, b"old-a\nold-b\n").expect("replace older fixture");
        std::fs::write(&current, b"current-a\ncurrent-b\n").expect("replace current fixture");

        assert_eq!(
            deleted_prefix_records(&saved, &[older, current]),
            saved.position
        );
    }

    #[test]
    fn deleted_prefix_counts_the_whole_position_after_a_rotation_leaves_no_anchor() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let older = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let compressed = directory.path().join("access.log.1.gz");
        let older_mark = write(&older, b"a1\na2\na3\n");
        std::fs::write(&current, b"t1\nt2\n").expect("write current fixture");
        let mut saved = entry(&current, 5, 6);
        saved.older_files = vec![older_mark];

        // rotate 1 + compress without delaycompress: the read rotation is deleted, the read
        // current log becomes a new compressed file, and nginx starts a new current log.
        std::fs::remove_file(&older).expect("delete read rotation");
        std::fs::write(&compressed, b"t1\nt2\nu1\nu2\n").expect("write compressed rotation");
        std::fs::remove_file(&current).expect("rename current fixture");
        std::fs::write(&current, b"n1\nn2\n").expect("write new current fixture");
        assert_eq!(
            deleted_prefix_records(&saved, &[compressed.clone(), current.clone()]),
            5
        );

        // copytruncate + rotate 1: the read rotation is replaced by a copy, and the current log
        // keeps its identity but is cut below the saved offset.
        let copytruncate = tempfile::tempdir().expect("create copytruncate fixture");
        let older = copytruncate.path().join("access.log.1");
        let current = copytruncate.path().join("access.log");
        let older_mark = write(&older, b"a1\na2\na3\na4\n");
        std::fs::write(&current, b"t1\nt2\n").expect("write current fixture");
        let mut saved = entry(&current, 6, 6);
        saved.older_files = vec![older_mark];
        std::fs::remove_file(&older).expect("delete read rotation");
        std::fs::write(&older, b"t1\nt2\nu1\n").expect("write copied rotation");
        std::fs::write(&current, b"n1\n").expect("truncate current fixture");
        assert_eq!(deleted_prefix_records(&saved, &[older, current]), 6);
    }

    #[test]
    fn identity_collision_does_not_establish_a_changed_mark() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let missing = directory.path().join("missing.log");
        let replacement = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let moved_current = directory.path().join("saved-current");
        let mut collision = write(&missing, b"old\n");
        std::fs::write(&current, b"current\n").expect("write current fixture");
        let mut saved = entry(&current, 2, 8);
        std::fs::rename(&current, &moved_current).expect("move saved current fixture");
        std::fs::write(&replacement, b"different-length\n").expect("write collision fixture");
        std::fs::write(&current, b"new-current\n").expect("replace current fixture");
        collision.identity = file_identity(&replacement).expect("read collision identity");
        saved.older_files = vec![collision.clone()];

        assert_eq!(
            deleted_prefix_records(&saved, &[replacement.clone(), current]),
            saved.position
        );
        assert_eq!(
            mark_records(
                &saved,
                &replacement,
                &collision.identity,
                std::fs::metadata(&replacement)
                    .expect("read collision length")
                    .len(),
                false,
            ),
            None
        );
    }

    #[test]
    fn appended_plain_mark_requires_its_saved_prefix() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let older = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let mut older_mark = write(&older, b"old-a\nold-b\n");
        older_mark.offset = Some(12);
        older_mark.tail_crc = Some(tail_crc(&older, 12).expect("hash older prefix"));
        std::fs::write(&current, b"current\n").expect("write current fixture");
        let mut saved = entry(&current, 3, 8);
        saved.older_files = vec![older_mark.clone()];
        std::fs::write(&older, b"old-a\nold-b\nlate\n").expect("append older fixture");

        assert_eq!(deleted_prefix_records(&saved, &[older.clone(), current]), 0);
        std::fs::write(&older, b"changed-prefix\nlate\n").expect("rewrite older fixture");
        assert_eq!(deleted_prefix_records(&saved, &[older]), saved.position);
    }

    #[test]
    fn a_new_log_with_a_deleted_logs_number_and_length_is_not_that_log() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let older = directory.path().join("access.log.1");
        let recycled = directory.path().join("access.log.2");
        let unchanged = directory.path().join("access.log.3");
        let current = directory.path().join("access.log");
        let moved_current = directory.path().join("saved-current");
        let mut older_mark = write(&older, b"a1\na2\na3\n");
        older_mark.offset = Some(older_mark.len);
        older_mark.tail_crc = Some(tail_crc(&older, older_mark.len).expect("hash older log"));
        std::fs::write(&current, b"t1\nt2\n").expect("write current fixture");
        let mut saved = entry(&current, 5, 6);
        std::fs::write(&recycled, b"b1\nb2\nb3\n").expect("write recycled fixture");
        std::fs::write(&unchanged, b"a1\na2\na3\n").expect("write unchanged fixture");

        // The file system gave the deleted older log's number to a different log of the same length.
        let mut recycled_mark = older_mark.clone();
        recycled_mark.identity = file_identity(&recycled).expect("read recycled identity");
        saved.older_files = vec![recycled_mark];

        // The saved current log survives beside it, so it is the older record count that proves the
        // position, not zero.
        let with_current = [recycled.clone(), current.clone()];
        assert_eq!(
            proven_deleted_records(&saved, &with_current),
            Some(older_mark.records)
        );
        assert!(resume_point(&saved, 5, &with_current).is_none());

        // No other log proves where the importer stopped, so the whole position counts.
        std::fs::rename(&current, &moved_current).expect("move saved current fixture");
        assert_eq!(proven_deleted_records(&saved, &[recycled.clone()]), None);
        assert_eq!(deleted_prefix_records(&saved, &[recycled]), saved.position);

        // The same number and the same bytes is the log the record names.
        let mut unchanged_mark = older_mark.clone();
        unchanged_mark.identity = file_identity(&unchanged).expect("read unchanged identity");
        saved.older_files = vec![unchanged_mark];
        assert_eq!(proven_deleted_records(&saved, &[unchanged]), Some(0));
    }

    #[test]
    fn a_mark_with_a_hash_credits_only_the_bytes_it_hashed() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let older = directory.path().join("access.log.1");
        let other = directory.path().join("access.log.2");
        let compressed = directory.path().join("access.log.3.gz");
        let unreadable = directory.path().join("access.log.4");
        let current = directory.path().join("access.log");
        let mut older_mark = write(&older, b"a1\na2\na3\n");
        older_mark.offset = Some(older_mark.len);
        older_mark.tail_crc = Some(tail_crc(&older, older_mark.len).expect("hash older log"));
        std::fs::write(&current, b"t1\nt2\n").expect("write current fixture");
        let mut saved = entry(&current, 5, 6);
        saved.older_files = vec![older_mark.clone()];
        std::fs::write(&other, b"b1\nb2\nb3\n").expect("write other fixture");
        std::fs::write(&compressed, b"b1\nb2\nb3\n").expect("write compressed fixture");
        // A directory cannot be read as a log on any platform, with no permission change.
        std::fs::create_dir(&unreadable).expect("create unreadable fixture");
        let identity = &older_mark.identity;
        let len = older_mark.len;

        assert_eq!(
            mark_records(&saved, &older, identity, len, false),
            Some(older_mark.records)
        );
        // A different log that took the deleted log's number, plain or compressed, is not that log.
        assert_eq!(mark_records(&saved, &other, identity, len, false), None);
        assert_eq!(
            mark_records(&saved, &compressed, identity, len, false),
            None
        );
        // A member that cannot be read gives no evidence against the mark, so it keeps its credit.
        assert_eq!(
            mark_records(&saved, &unreadable, identity, len, false),
            Some(older_mark.records)
        );
    }

    #[test]
    fn an_unreadable_hash_is_not_proof_of_a_saved_log() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let newer = directory.path().join("access.log.1");
        let current = directory.path().join("access.log");
        let mut mark = write(&newer, b"a1\na2\na3\n");
        // An offset past the end of the file makes the hash read fail on every platform, with no
        // permission change.
        mark.offset = Some(mark.len + 1);
        mark.tail_crc = Some(0);
        assert!(tail_crc(&newer, mark.offset.expect("mark offset")).is_err());
        std::fs::write(&current, b"t1\nt2\n").expect("write current fixture");
        let mut saved = entry(&current, 5, 6);
        saved.older_files = vec![mark.clone()];

        // The log has the saved log's number and length but its hash cannot be read, so it is not
        // shown to be that log.
        assert!(!mark_matches(&mark, &newer, &mark.identity, mark.len));
        let with_current = [newer.clone(), current.clone()];
        assert!(resume_point(&saved, 5, &with_current).is_none());
        assert_eq!(
            proven_deleted_records(&saved, &with_current),
            Some(mark.records)
        );

        // With no other log to prove where the importer stopped, the whole position counts.
        assert_eq!(proven_deleted_records(&saved, &[newer.clone()]), None);
        assert_eq!(
            deleted_prefix_records(&saved, &[newer.clone()]),
            saved.position
        );

        // A log that failed to open keeps the credit the earlier pass gave it.
        assert_eq!(
            mark_records(&saved, &newer, &mark.identity, mark.len, false),
            Some(mark.records)
        );
    }

    #[test]
    fn entry_after_run_keeps_only_the_valid_frozen_entry() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let path = directory.path().join("access.log");
        std::fs::write(&path, b"one\ntwo\n").expect("write resume fixture");
        let previous = entry(&path, 2, 8);
        let fresh = StemResume {
            position: 3,
            ..previous.clone()
        };

        assert_eq!(
            entry_after_run(Some(&previous), Some(fresh.clone()), false, 3, 2),
            Some(fresh)
        );
        assert_eq!(
            entry_after_run(Some(&previous), None, true, 2, 2),
            Some(previous.clone())
        );
        assert_eq!(entry_after_run(None, None, true, 2, 2), None);
        assert_eq!(entry_after_run(Some(&previous), None, true, 3, 2), None);
    }

    #[test]
    fn load_defaults_and_save_sets_the_schema_version() {
        let directory = tempfile::tempdir().expect("create resume fixture");
        let path = directory.path().join("resume.json");
        assert_eq!(load(&path), ResumeFile::default());
        std::fs::write(&path, "not json").expect("write malformed sidecar");
        assert_eq!(load(&path), ResumeFile::default());
        std::fs::write(&path, r#"{"schema_version":9,"stems":{}}"#)
            .expect("write unsupported sidecar");
        assert_eq!(load(&path), ResumeFile::default());

        save(&path, &ResumeFile::default()).expect("save resume sidecar");
        assert_eq!(load(&path).schema_version, RESUME_SCHEMA_VERSION);

        let old_mark: FileMark =
            serde_json::from_str(r#"{"identity":{"first":1,"second":2},"len":3,"records":4}"#)
                .expect("load old schema mark");
        assert_eq!(old_mark.offset, None);
        assert_eq!(old_mark.tail_crc, None);
    }
}
