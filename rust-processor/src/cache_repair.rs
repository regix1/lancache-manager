use std::collections::HashSet;
use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::cache_utils;

const RECEIPT_VERSION: u32 = 1;
const RECEIPT_PREFIX: &str = ".lancache-repair-";

#[derive(Debug)]
pub struct RootFiles {
    pub canonical_path: PathBuf,
    pub digests: HashSet<u128>,
    /// False when a folder or entry below the root could not be read, so a digest missing from
    /// `digests` proves nothing about this root.
    pub fully_checked: bool,
}

impl RootFiles {
    pub fn is_empty(&self) -> bool {
        self.digests.is_empty()
    }
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RepairReceipt {
    version: u32,
    operation_id: String,
    cache_path: String,
    had_cache_files: bool,
}

pub fn operation_uuid(value: &str) -> Result<Uuid> {
    Uuid::parse_str(value).with_context(|| format!("invalid cache repair operation ID {value}"))
}

pub fn receipt_path(cache_path: &Path, operation_id: Uuid) -> PathBuf {
    cache_path.join(format!("{RECEIPT_PREFIX}{}.json", operation_id.simple()))
}

enum WalkAction {
    Continue,
    Stop,
}

/// One entry of a directory listing: reading the entry and reading its type can fail separately.
type ListedEntry = io::Result<(PathBuf, io::Result<fs::FileType>)>;

/// Lists one directory as `(path, file type)` pairs without following links.
fn list_directory(directory: &Path) -> io::Result<Vec<ListedEntry>> {
    Ok(fs::read_dir(directory)?
        .map(|entry| entry.map(|entry| (entry.path(), entry.file_type())))
        .collect())
}

/// A path below the cache root that the app may not read is skipped and marks the walk
/// incomplete; every other failure still stops the walk.
fn skip_permission_denied<T>(
    result: io::Result<T>,
    fully_checked: &mut bool,
) -> io::Result<Option<T>> {
    match result {
        Ok(value) => Ok(Some(value)),
        Err(error) if error.kind() == io::ErrorKind::PermissionDenied => {
            *fully_checked = false;
            Ok(None)
        }
        Err(error) => Err(error),
    }
}

/// Returns the canonical root and whether every folder and entry below it could be read.
fn walk_root<F>(cache_path: &Path, visit_file: F) -> Result<(PathBuf, bool)>
where
    F: FnMut(&Path) -> Result<WalkAction>,
{
    walk_root_with(cache_path, list_directory, visit_file)
}

/// `walk_root` with the directory listing passed in, so a test can fail the listing, an entry
/// read or a type lookup for one path.
fn walk_root_with<R, F>(
    cache_path: &Path,
    mut read_dir: R,
    mut visit_file: F,
) -> Result<(PathBuf, bool)>
where
    R: FnMut(&Path) -> io::Result<Vec<ListedEntry>>,
    F: FnMut(&Path) -> Result<WalkAction>,
{
    let canonical_path = cache_path
        .canonicalize()
        .with_context(|| format!("failed to resolve cache root {}", cache_path.display()))?;
    if !canonical_path.is_dir() {
        bail!(
            "cache root is not a directory: {}",
            canonical_path.display()
        );
    }

    let mut fully_checked = true;
    let mut pending = vec![canonical_path.clone()];
    while let Some(directory) = pending.pop() {
        // The root itself must be readable; only folders below it may be skipped.
        let listing = if directory == canonical_path {
            read_dir(&directory).map(Some)
        } else {
            skip_permission_denied(read_dir(&directory), &mut fully_checked)
        };
        let Some(entries) = listing
            .with_context(|| format!("failed to enumerate cache path {}", directory.display()))?
        else {
            continue;
        };
        for entry in entries {
            let Some((path, file_type)) = skip_permission_denied(entry, &mut fully_checked)
                .with_context(|| {
                    format!("failed to read an entry under {}", directory.display())
                })?
            else {
                continue;
            };
            let Some(file_type) = skip_permission_denied(file_type, &mut fully_checked)
                .with_context(|| format!("failed to inspect cache path {}", path.display()))?
            else {
                continue;
            };
            if file_type.is_symlink() {
                continue;
            }
            if file_type.is_dir() {
                pending.push(path);
                continue;
            }
            if file_type.is_file() && matches!(visit_file(&path)?, WalkAction::Stop) {
                return Ok((canonical_path, fully_checked));
            }
        }
    }

    Ok((canonical_path, fully_checked))
}

pub fn scan_root(cache_path: &Path) -> Result<RootFiles> {
    let mut digests = HashSet::new();
    let (canonical_path, fully_checked) = walk_root(cache_path, |path| {
        if let Some(digest) = path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(cache_utils::parse_cache_file_digest)
        {
            digests.insert(digest);
        }
        Ok(WalkAction::Continue)
    })?;

    Ok(RootFiles {
        canonical_path,
        digests,
        fully_checked,
    })
}

fn root_has_digest_with<F>(cache_path: &Path, mut on_file: F) -> Result<(PathBuf, bool)>
where
    F: FnMut(&Path),
{
    let mut found = false;
    // An incomplete walk that finds no digest writes no receipt, so the root stays unverified.
    let (canonical_path, _) = walk_root(cache_path, |path| {
        on_file(path);
        if path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(cache_utils::parse_cache_file_digest)
            .is_some()
        {
            found = true;
            return Ok(WalkAction::Stop);
        }
        Ok(WalkAction::Continue)
    })?;
    Ok((canonical_path, found))
}

pub fn prepare_receipt(cache_path: &Path, operation_id: Option<&str>) -> Result<Option<PathBuf>> {
    let Some(operation_id) = operation_id else {
        return Ok(None);
    };
    let operation_id = operation_uuid(operation_id)?;
    let canonical_path = cache_path
        .canonicalize()
        .with_context(|| format!("failed to resolve cache root {}", cache_path.display()))?;
    let path = receipt_path(&canonical_path, operation_id);

    if path.exists() {
        validate_receipt(&canonical_path, operation_id)?;
        return Ok(Some(path));
    }

    let (receipt_root, had_cache_files) = root_has_digest_with(&canonical_path, |_| {})?;
    if !had_cache_files {
        return Ok(None);
    }

    let receipt = RepairReceipt {
        version: RECEIPT_VERSION,
        operation_id: operation_id.hyphenated().to_string(),
        cache_path: receipt_root.to_string_lossy().into_owned(),
        had_cache_files: true,
    };
    let encoded =
        serde_json::to_vec(&receipt).context("failed to serialize cache repair receipt")?;
    let mut temporary = tempfile::NamedTempFile::new_in(&receipt_root).with_context(|| {
        format!(
            "failed to create cache repair receipt under {}",
            receipt_root.display()
        )
    })?;
    temporary
        .write_all(&encoded)
        .context("failed to write cache repair receipt")?;
    temporary
        .as_file_mut()
        .sync_all()
        .context("failed to flush cache repair receipt")?;

    match temporary.persist_noclobber(&path) {
        Ok(file) => {
            file.sync_all()
                .context("failed to flush persisted cache repair receipt")?;
        }
        Err(error) if error.error.kind() == std::io::ErrorKind::AlreadyExists => {
            validate_receipt(&receipt_root, operation_id)?;
            return Ok(Some(path));
        }
        Err(error) => {
            return Err(error.error).with_context(|| {
                format!("failed to persist cache repair receipt {}", path.display())
            });
        }
    }

    #[cfg(unix)]
    std::fs::File::open(&receipt_root)
        .and_then(|directory| directory.sync_all())
        .with_context(|| {
            format!(
                "failed to flush cache repair receipt directory {}",
                receipt_root.display()
            )
        })?;

    Ok(Some(path))
}

pub fn validate_receipt(cache_path: &Path, operation_id: Uuid) -> Result<PathBuf> {
    let canonical_path = cache_path
        .canonicalize()
        .with_context(|| format!("failed to resolve cache root {}", cache_path.display()))?;
    let path = receipt_path(&canonical_path, operation_id);
    let bytes = fs::read(&path)
        .with_context(|| format!("failed to read cache repair receipt {}", path.display()))?;
    let receipt: RepairReceipt = serde_json::from_slice(&bytes)
        .with_context(|| format!("failed to parse cache repair receipt {}", path.display()))?;

    if receipt.version != RECEIPT_VERSION {
        bail!(
            "unsupported cache repair receipt version {} in {}",
            receipt.version,
            path.display()
        );
    }
    if operation_uuid(&receipt.operation_id)? != operation_id {
        bail!(
            "cache repair receipt operation ID does not match {}",
            path.display()
        );
    }
    if Path::new(&receipt.cache_path) != canonical_path {
        bail!(
            "cache repair receipt path does not match {}",
            path.display()
        );
    }
    if !receipt.had_cache_files {
        bail!(
            "cache repair receipt lacks populated-root evidence: {}",
            path.display()
        );
    }

    Ok(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    const DIGEST: &str = "0123456789abcdef0123456789abcdef";

    fn create_cache_file(root: &Path) -> PathBuf {
        let path = root.join("ef").join("cd").join(DIGEST);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, b"cache").unwrap();
        path
    }

    #[test]
    fn populated_root_writes_strict_receipt_before_mutation() {
        let root = tempfile::tempdir().unwrap();
        create_cache_file(root.path());
        let operation_id = Uuid::new_v4();

        let path = prepare_receipt(root.path(), Some(&operation_id.to_string()))
            .unwrap()
            .unwrap();
        let receipt: serde_json::Value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();

        assert_eq!(receipt["version"], 1);
        assert_eq!(receipt["operationId"], operation_id.to_string());
        assert_eq!(
            receipt["cachePath"],
            root.path()
                .canonicalize()
                .unwrap()
                .to_string_lossy()
                .as_ref()
        );
        assert_eq!(receipt["hadCacheFiles"], true);
        assert_eq!(receipt.as_object().unwrap().len(), 4);
        assert_eq!(
            path.file_name().unwrap().to_string_lossy(),
            format!("{RECEIPT_PREFIX}{}.json", operation_id.simple())
        );
    }

    #[test]
    fn empty_root_cannot_create_populated_receipt() {
        let root = tempfile::tempdir().unwrap();
        let operation_id = Uuid::new_v4();

        assert!(
            prepare_receipt(root.path(), Some(&operation_id.to_string()))
                .unwrap()
                .is_none()
        );
        assert!(!receipt_path(&root.path().canonicalize().unwrap(), operation_id).exists());
    }

    #[test]
    fn receipt_probe_stops_after_the_first_digest() {
        let root = tempfile::tempdir().unwrap();
        let first = create_cache_file(root.path());
        let second = root
            .path()
            .join("10")
            .join("32")
            .join("fedcba9876543210fedcba9876543210");
        fs::create_dir_all(second.parent().unwrap()).unwrap();
        fs::write(&second, b"cache").unwrap();
        assert!(first.is_file());
        assert!(second.is_file());
        assert_eq!(scan_root(root.path()).unwrap().digests.len(), 2);
        let visited = std::cell::Cell::new(0usize);

        let (_, found) = root_has_digest_with(root.path(), |_| visited.set(visited.get() + 1))
            .expect("probe populated root");

        assert!(found);
        assert_eq!(visited.get(), 1);
    }

    #[test]
    fn receipt_probe_checks_every_regular_file_when_no_digest_exists() {
        let root = tempfile::tempdir().unwrap();
        let nested = root.path().join("one").join("two");
        fs::create_dir_all(&nested).unwrap();
        fs::write(root.path().join("foreign-a"), b"a").unwrap();
        fs::write(root.path().join("one").join("foreign-b"), b"b").unwrap();
        fs::write(nested.join("foreign-c"), b"c").unwrap();
        let visited = std::cell::Cell::new(0usize);

        let (_, found) = root_has_digest_with(root.path(), |_| visited.set(visited.get() + 1))
            .expect("probe root without digests");

        assert!(!found);
        assert_eq!(visited.get(), 3);
    }

    #[test]
    fn existing_receipt_survives_an_empty_reentry() {
        let root = tempfile::tempdir().unwrap();
        let cache_file = create_cache_file(root.path());
        let operation_id = Uuid::new_v4();
        let first = prepare_receipt(root.path(), Some(&operation_id.to_string()))
            .unwrap()
            .unwrap();
        fs::remove_file(cache_file).unwrap();

        let second = prepare_receipt(root.path(), Some(&operation_id.to_string()))
            .unwrap()
            .unwrap();

        assert_eq!(first, second);
    }

    #[test]
    fn non_directory_scan_fails_deterministically() {
        let root = tempfile::tempdir().unwrap();
        let file = root.path().join("not-a-directory");
        fs::write(&file, b"content").unwrap();

        let error = scan_root(&file).unwrap_err();

        assert!(error.to_string().contains("cache root is not a directory"));
    }

    fn permission_denied() -> io::Error {
        io::Error::from(io::ErrorKind::PermissionDenied)
    }

    /// Two cache files in different folders: `ef/cd/<DIGEST>` and `10/32/<OTHER>`.
    fn two_folder_root() -> (tempfile::TempDir, PathBuf, PathBuf) {
        let root = tempfile::tempdir().unwrap();
        let first = create_cache_file(root.path()).canonicalize().unwrap();
        let second = root
            .path()
            .join("10")
            .join("32")
            .join("fedcba9876543210fedcba9876543210");
        fs::create_dir_all(second.parent().unwrap()).unwrap();
        fs::write(&second, b"cache").unwrap();
        let second = second.canonicalize().unwrap();
        (root, first, second)
    }

    fn visited_files<R>(root: &Path, read_dir: R) -> Result<(Vec<PathBuf>, bool)>
    where
        R: FnMut(&Path) -> io::Result<Vec<ListedEntry>>,
    {
        let mut visited = Vec::new();
        let (_, fully_checked) = walk_root_with(root, read_dir, |path| {
            visited.push(path.to_path_buf());
            Ok(WalkAction::Continue)
        })?;
        Ok((visited, fully_checked))
    }

    #[test]
    fn unreadable_folder_below_the_root_is_skipped_and_marks_the_walk_incomplete() {
        let (root, first, second) = two_folder_root();
        let blocked = first.parent().unwrap().to_path_buf();

        let (visited, fully_checked) = visited_files(root.path(), |directory| {
            if directory == blocked {
                return Err(permission_denied());
            }
            list_directory(directory)
        })
        .expect("an unreadable folder below the root must not fail the walk");

        assert_eq!(visited, vec![second]);
        assert!(!fully_checked);
    }

    #[test]
    fn unreadable_entry_below_the_root_is_skipped_and_marks_the_walk_incomplete() {
        let (root, first, second) = two_folder_root();
        let parent = first.parent().unwrap().to_path_buf();

        let (mut visited, fully_checked) = visited_files(root.path(), |directory| {
            let mut entries = list_directory(directory)?;
            if directory == parent {
                entries.insert(0, Err(permission_denied()));
            }
            Ok(entries)
        })
        .expect("an unreadable entry must not fail the walk");

        visited.sort();
        let mut expected = vec![first, second];
        expected.sort();
        assert_eq!(visited, expected);
        assert!(!fully_checked);
    }

    #[test]
    fn uninspectable_entry_below_the_root_is_skipped_and_marks_the_walk_incomplete() {
        let (root, first, second) = two_folder_root();

        let (visited, fully_checked) = visited_files(root.path(), |directory| {
            Ok(list_directory(directory)?
                .into_iter()
                .map(|entry| {
                    entry.map(|(path, file_type)| {
                        if path == first {
                            (path, Err(permission_denied()))
                        } else {
                            (path, file_type)
                        }
                    })
                })
                .collect())
        })
        .expect("an entry whose type cannot be read must not fail the walk");

        assert_eq!(visited, vec![second]);
        assert!(!fully_checked);
    }

    #[test]
    fn unreadable_root_and_other_errors_below_it_still_fail() {
        let (root, first, _) = two_folder_root();
        let canonical_root = root.path().canonicalize().unwrap();
        let blocked = first.parent().unwrap().to_path_buf();

        let root_error = visited_files(root.path(), |directory| {
            if directory == canonical_root {
                return Err(permission_denied());
            }
            list_directory(directory)
        })
        .unwrap_err();
        let other_error = visited_files(root.path(), |directory| {
            if directory == blocked {
                return Err(io::Error::other("injected listing failure"));
            }
            list_directory(directory)
        })
        .unwrap_err();
        let (visited, fully_checked) = visited_files(root.path(), list_directory).unwrap();

        assert!(root_error
            .to_string()
            .contains("failed to enumerate cache path"));
        assert!(other_error
            .to_string()
            .contains("failed to enumerate cache path"));
        assert_eq!(visited.len(), 2);
        assert!(fully_checked);
    }

    #[test]
    fn receipt_rejects_unknown_fields() {
        let root = tempfile::tempdir().unwrap();
        create_cache_file(root.path());
        let operation_id = Uuid::new_v4();
        let path = receipt_path(&root.path().canonicalize().unwrap(), operation_id);
        fs::write(
            &path,
            format!(
                r#"{{"version":1,"operationId":"{operation_id}","cachePath":"{}","hadCacheFiles":true,"extra":1}}"#,
                root.path().canonicalize().unwrap().display()
            ),
        )
        .unwrap();

        assert!(validate_receipt(root.path(), operation_id).is_err());
    }
}
