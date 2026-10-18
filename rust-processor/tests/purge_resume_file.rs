use std::fs;
use std::path::Path;
use std::process::Command;

/// Runs the purge binary with `--resume` at `resume` and returns its exit success and stderr.
fn purge_with_resume(temp: &Path, resume: &Path) -> (bool, String) {
    let logs = temp.join("logs");
    fs::create_dir_all(&logs).unwrap();
    fs::write(
        logs.join("access.log"),
        "[steam] 192.0.2.10 / - - - [01/Oct/2026:10:00:00 +0000] \"GET /depot/1/chunk/a HTTP/1.1\" 200 1024 \"-\" \"Test\" \"HIT\" \"cdn.test\" \"-\"\n",
    )
    .unwrap();
    let request = temp.join("request.json");
    fs::write(&request, r#"{"urls":[],"depot_ids":[1]}"#).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_cache_purge_log_entries"))
        .arg(&logs)
        .arg(&request)
        .arg(temp.join("report.json"))
        .arg("--resume")
        .arg(resume)
        .output()
        .unwrap();
    (
        output.status.success(),
        String::from_utf8_lossy(&output.stderr).into_owned(),
    )
}

#[test]
fn a_missing_resume_file_is_not_reported_as_unreadable() {
    let temp = tempfile::tempdir().unwrap();

    let (success, stderr) = purge_with_resume(temp.path(), &temp.path().join("resume.json"));

    assert!(success, "{stderr}");
    assert!(!stderr.contains("resume file"), "{stderr}");
}

#[test]
fn a_resume_file_that_cannot_be_read_is_still_reported() {
    let temp = tempfile::tempdir().unwrap();
    // A directory in its place fails to read with an error other than a missing file.
    let resume = temp.path().join("resume.json");
    fs::create_dir(&resume).unwrap();

    let (success, stderr) = purge_with_resume(temp.path(), &resume);

    assert!(success, "{stderr}");
    assert!(stderr.contains("unreadable"), "{stderr}");
}
