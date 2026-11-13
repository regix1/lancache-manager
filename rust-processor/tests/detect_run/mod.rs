//! One `cache_game_detect` full scan against a throwaway PostgreSQL schema, shared by the
//! integration tests that read the Services on Disk total it reports.

use std::fs;
use std::path::Path;
use std::process::Command;
use std::str::FromStr;

use sqlx::postgres::{PgConnectOptions, PgPoolOptions};

pub const SERVICE: &str = "wsus";
pub const LISTED_BYTES: u64 = 1_048_576;

fn test_database_url() -> String {
    std::env::var("LANCACHE_TEST_DATABASE_URL")
        .or_else(|_| std::env::var("DATABASE_URL"))
        .expect("set LANCACHE_TEST_DATABASE_URL or DATABASE_URL to a PostgreSQL this test may create a schema in")
}

/// Runs a full detection scan of `cache` with one `SERVICE` log entry for `url` of
/// `LISTED_BYTES`, and returns the `total_size_bytes` the report gives the `SERVICE` row.
pub async fn service_bytes_on_disk(cache: &Path, url: &str) -> u64 {
    let database_url = test_database_url();
    let schema = format!("detect_run_{}", uuid::Uuid::new_v4().simple());
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
    for statement in [
        r#"CREATE TABLE "Downloads" ("Id" BIGINT PRIMARY KEY, "Service" TEXT, "GameAppId" BIGINT, "EpicAppId" TEXT, "GameName" TEXT, "IsEvicted" BOOLEAN NOT NULL DEFAULT false, "CacheHitBytes" BIGINT NOT NULL DEFAULT 0, "CacheMissBytes" BIGINT NOT NULL DEFAULT 0)"#,
        r#"CREATE TABLE "LogEntries" ("Service" TEXT, "Url" TEXT, "BytesServed" BIGINT, "DownloadId" BIGINT, "DepotId" BIGINT)"#,
        r#"CREATE TABLE "SteamDepotMappings" ("DepotId" BIGINT, "AppId" BIGINT, "AppName" TEXT, "DepotName" TEXT, "IsOwner" BOOLEAN, "Source" TEXT)"#,
        r#"CREATE TABLE "CachedGameDetections" ("GameAppId" BIGINT)"#,
    ] {
        sqlx::query(statement).execute(&pool).await.unwrap();
    }
    sqlx::query(r#"INSERT INTO "LogEntries" VALUES ($1, $2, $3, NULL, NULL)"#)
        .bind(SERVICE)
        .bind(url)
        .bind(LISTED_BYTES as i64)
        .execute(&pool)
        .await
        .unwrap();
    pool.close().await;

    let report_path = cache.with_file_name("report.json");
    let separator = if database_url.contains('?') { '&' } else { '?' };
    let output = Command::new(env!("CARGO_BIN_EXE_cache_game_detect"))
        .arg(cache)
        .arg(&report_path)
        .arg("--slice-size")
        .arg(LISTED_BYTES.to_string())
        .env(
            "DATABASE_URL",
            format!("{database_url}{separator}options=-csearch_path%3D{schema}"),
        )
        .output()
        .unwrap();

    sqlx::query(&format!(r#"DROP SCHEMA "{schema}" CASCADE"#))
        .execute(&admin)
        .await
        .unwrap();
    admin.close().await;

    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(output.status.success(), "{stderr}");
    let report: serde_json::Value =
        serde_json::from_slice(&fs::read(&report_path).unwrap()).unwrap();
    let service = report["services"]
        .as_array()
        .expect("the report lists services")
        .iter()
        .find(|row| row["service_name"] == SERVICE)
        .unwrap_or_else(|| panic!("detection found no {SERVICE} row: {report}\n{stderr}"));
    service["total_size_bytes"].as_u64().unwrap()
}
