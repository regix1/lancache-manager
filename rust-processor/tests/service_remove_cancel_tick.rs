//! A canceled service removal writes one last progress tick. It must count deleted files out of
//! the files the delete loop walks, as every tick before it does, or the card can read
//! "120 of 3" for a service with three URLs and many slices each.
#![cfg(unix)]

use std::fs;
use std::io::Write;
use std::process::{Command, Stdio};
use std::str::FromStr;
use std::time::{Duration, Instant};

use lancache_processor::cache_utils;
use sqlx::postgres::{PgConnectOptions, PgPoolOptions};

const SERVICE: &str = "wsus";
const URL: &str = "/c/msdownload/update/software/canceled-removal.cab";

fn test_database_url() -> String {
    std::env::var("LANCACHE_TEST_DATABASE_URL")
        .or_else(|_| std::env::var("DATABASE_URL"))
        .expect("set LANCACHE_TEST_DATABASE_URL or DATABASE_URL to a PostgreSQL this test may create a schema in")
}

#[tokio::test]
async fn a_canceled_service_removal_counts_its_last_tick_out_of_the_files_it_walks() {
    let temp = tempfile::tempdir().unwrap();
    let cache = temp.path().join("cache");
    // One URL, two cache files on disk: its whole-object file and its unsliced file.
    let whole = cache_utils::calculate_cache_path_no_range(&cache, SERVICE, URL);
    let unsliced = cache_utils::cache_path_for_digest(
        &cache,
        cache_utils::calculate_md5_digest(&format!("{SERVICE}{URL}::noslice")),
    );
    for path in [&whole, &unsliced] {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, b"cache").unwrap();
    }
    let logs = temp.path().join("logs");
    fs::create_dir_all(&logs).unwrap();

    let database_url = test_database_url();
    let schema = format!("service_remove_cancel_{}", uuid::Uuid::new_v4().simple());
    let options = PgConnectOptions::from_str(&database_url).expect("parse test database URL");
    let admin = PgPoolOptions::new()
        .max_connections(1)
        .connect_with(options.clone())
        .await
        .expect("connect to test database");
    sqlx::query(&format!(r#"CREATE SCHEMA "{schema}""#))
        .execute(&admin)
        .await
        .expect("create test schema");
    let pool = PgPoolOptions::new()
        .max_connections(1)
        .connect_with(options.options([("search_path", schema.as_str())]))
        .await
        .expect("connect to test schema");
    sqlx::query(r#"CREATE TABLE "LogEntries" ("Service" TEXT, "Url" TEXT, "BytesServed" BIGINT)"#)
        .execute(&pool)
        .await
        .unwrap();
    sqlx::query(r#"INSERT INTO "LogEntries" VALUES ($1, $2, 5)"#)
        .bind(SERVICE)
        .bind(URL)
        .execute(&pool)
        .await
        .unwrap();
    pool.close().await;

    let progress_path = temp.path().join("progress.json");
    let separator = if database_url.contains('?') { '&' } else { '?' };
    let stderr_path = temp.path().join("stderr.txt");
    let mut child = Command::new(env!("CARGO_BIN_EXE_cache_service_remove"))
        .arg(&logs)
        .arg(&cache)
        .arg(SERVICE)
        .arg(temp.path().join("report.json"))
        .arg(&progress_path)
        .arg("--slice-size")
        .arg("1048576")
        .arg("--skip-db-delete")
        .env(
            "DATABASE_URL",
            format!("{database_url}{separator}options=-csearch_path%3D{schema}"),
        )
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(fs::File::create(&stderr_path).unwrap())
        .spawn()
        .unwrap();
    // The stdin watcher starts first in main, long before the database connect, so the delete
    // loop sees the cancel before it removes anything.
    child.stdin.take().unwrap().write_all(b"CANCEL\n").unwrap();
    let deadline = Instant::now() + Duration::from_secs(60);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() > deadline {
            child.kill().unwrap();
            panic!("cache_service_remove did not exit within 60 s");
        }
        std::thread::sleep(Duration::from_millis(50));
    };

    sqlx::query(&format!(r#"DROP SCHEMA "{schema}" CASCADE"#))
        .execute(&admin)
        .await
        .unwrap();
    admin.close().await;

    let stderr = fs::read_to_string(&stderr_path).unwrap();
    assert!(status.success(), "{stderr}");
    assert!(
        stderr.contains("Cancellation"),
        "the run was not canceled:\n{stderr}"
    );
    let progress: serde_json::Value =
        serde_json::from_slice(&fs::read(&progress_path).unwrap()).unwrap();
    assert_eq!(
        progress["stageKey"], "signalr.serviceRemove.cache.file.progress",
        "{progress}"
    );
    assert_eq!(progress["context"]["total"], 2, "{progress}\n{stderr}");
    assert_eq!(progress["totalFiles"], 2, "{progress}");
    assert!(
        progress["context"]["n"].as_u64().unwrap()
            <= progress["context"]["total"].as_u64().unwrap(),
        "{progress}"
    );
}
