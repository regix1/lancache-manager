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
        if identity != mark.identity || len != mark.len {
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

fn mark_matches(mark: &FileMark, path: &Path, identity: &FileIdentity, len: u64) -> bool {
    if identity != &mark.identity {
        return false;
    }
    if len == mark.len {
        return true;
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
pub fn deleted_prefix_records(entry: &StemResume, files: &[PathBuf]) -> u64 {
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
        return entry.older_files[..first_present]
            .iter()
            .map(|mark| mark.records)
            .sum();
    }
    if current
        .iter()
        .any(|(path, identity, len)| target_matches(entry, path, identity, *len))
    {
        return entry.older_files.iter().map(|mark| mark.records).sum();
    }
    0
}

/// Returns a saved record count only when the observed member proves the saved boundary.
pub fn mark_records(
    entry: &StemResume,
    identity: &FileIdentity,
    len: u64,
    prefix_matched: bool,
) -> Option<u64> {
    if let Some(mark) = entry
        .older_files
        .iter()
        .find(|mark| &mark.identity == identity)
    {
        return (len == mark.len || (len > mark.len && prefix_matched)).then_some(mark.records);
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
            mark_records(&saved, &older_mark.identity, older_mark.len, false),
            Some(1)
        );
        assert_eq!(mark_records(&saved, &saved.file_identity, 8, true), Some(2));
        assert_eq!(
            mark_records(
                &saved,
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

        assert_eq!(deleted_prefix_records(&saved, &[older, current]), 0);
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
            0
        );
        assert_eq!(
            mark_records(
                &saved,
                &collision.identity,
                std::fs::metadata(replacement)
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
        assert_eq!(deleted_prefix_records(&saved, &[older]), 0);
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
