use std::collections::HashSet;
use std::fs;
use std::io::Write;
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

fn walk_root<F>(cache_path: &Path, mut visit_file: F) -> Result<PathBuf>
where
    F: FnMut(&fs::DirEntry) -> Result<WalkAction>,
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

    let mut pending = vec![canonical_path.clone()];
    while let Some(directory) = pending.pop() {
        let entries = fs::read_dir(&directory)
            .with_context(|| format!("failed to enumerate cache path {}", directory.display()))?;
        for entry in entries {
            let entry = entry.with_context(|| {
                format!("failed to read an entry under {}", directory.display())
            })?;
            let path = entry.path();
            let file_type = entry
                .file_type()
                .with_context(|| format!("failed to inspect cache path {}", path.display()))?;
            if file_type.is_symlink() {
                continue;
            }
            if file_type.is_dir() {
                pending.push(path);
                continue;
            }
            if file_type.is_file() {
                if matches!(visit_file(&entry)?, WalkAction::Stop) {
                    return Ok(canonical_path);
                }
            }
        }
    }

    Ok(canonical_path)
}

pub fn scan_root(cache_path: &Path) -> Result<RootFiles> {
    let mut digests = HashSet::new();
    let canonical_path = walk_root(cache_path, |entry| {
        if let Some(digest) = entry
            .file_name()
            .to_str()
            .and_then(cache_utils::parse_cache_file_digest)
        {
            digests.insert(digest);
        }
        Ok(WalkAction::Continue)
    })?;

    Ok(RootFiles {
        canonical_path,
        digests,
    })
}

fn root_has_digest_with<F>(cache_path: &Path, mut on_file: F) -> Result<(PathBuf, bool)>
where
    F: FnMut(&Path),
{
    let mut found = false;
    let canonical_path = walk_root(cache_path, |entry| {
        on_file(&entry.path());
        if entry
            .file_name()
            .to_str()
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
