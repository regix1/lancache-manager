use anyhow::{anyhow, Result};
use sqlx::postgres::PgPoolOptions;
use sqlx::PgPool;
use std::env;
use std::fs;

pub async fn create_pool() -> Result<PgPool> {
    let database_url = build_database_url();
    let safe_url = redact_database_url(&database_url);

    PgPoolOptions::new()
        .max_connections(10)
        .connect(&database_url)
        .await
        .map_err(|e| {
            anyhow!(
                "Failed to connect to PostgreSQL: {e}\n  URL: {safe_url}\n  \
                 Hint: set DATABASE_URL for full control, or set POSTGRES_HOST + POSTGRES_PASSWORD \
                 (external mode), or ensure the /var/run/postgresql socket exists (embedded mode)."
            )
        })
}

fn redact_database_url(database_url: &str) -> String {
    if let Some(at_pos) = database_url.find('@') {
        if let Some(colon_pos) = database_url[..at_pos].rfind(':') {
            return format!(
                "{}:***{}",
                &database_url[..colon_pos],
                &database_url[at_pos..]
            );
        }
    }

    // Also redact `password=...` in query-string form.
    if let Some(idx) = database_url.find("password=") {
        let prefix = &database_url[..idx + "password=".len()];
        let rest = &database_url[idx + "password=".len()..];
        let end = rest.find('&').unwrap_or(rest.len());
        return format!("{}***{}", prefix, &rest[end..]);
    }

    database_url.to_string()
}

/// Build the PostgreSQL connection URL. Source priority:
///   1. `DATABASE_URL` (explicit override, wins over everything)
///   2. `POSTGRES_HOST` env var (external mode - builds a TCP URL)
///   3. Credentials file `host` field (external mode via UI fallback)
///   4. Unix socket at /var/run/postgresql (embedded mode default)
///
/// Within modes 2-4, user/password/port/database are sourced from:
///   env vars > credentials file > sensible defaults.
fn build_database_url() -> String {
    if let Ok(url) = env::var("DATABASE_URL") {
        return url;
    }

    let creds = read_credentials_file();

    let user = env::var("POSTGRES_USER")
        .ok()
        .or_else(|| creds.as_ref().and_then(|c| c.username.clone()))
        .unwrap_or_else(|| "lancache".to_string());

    let password = env::var("POSTGRES_PASSWORD")
        .ok()
        .or_else(|| creds.as_ref().and_then(|c| c.password.clone()))
        .unwrap_or_default();

    let database = env::var("POSTGRES_DB")
        .ok()
        .or_else(|| creds.as_ref().and_then(|c| c.database.clone()))
        .unwrap_or_else(|| "lancache".to_string());

    let host = env::var("POSTGRES_HOST")
        .ok()
        .filter(|s| !s.is_empty())
        .or_else(|| creds.as_ref().and_then(|c| c.host.clone()));

    let port = env::var("POSTGRES_PORT")
        .ok()
        .and_then(|s| s.parse::<u16>().ok())
        .or_else(|| creds.as_ref().and_then(|c| c.port))
        .unwrap_or(5432);

    match host {
        // External mode: TCP connection
        Some(h) => {
            let mut url = format!(
                "postgres:///{}?host={}&port={}&user={}",
                database, h, port, user
            );
            if !password.is_empty() {
                url.push_str(&format!("&password={}", password));
            }
            url
        }
        // Embedded mode: Unix socket
        None => {
            let mut url = format!(
                "postgres:///{}?host=/var/run/postgresql&user={}",
                database, user
            );
            if !password.is_empty() {
                url.push_str(&format!("&password={}", password));
            }
            url
        }
    }
}

#[derive(Default)]
struct CredentialsFile {
    username: Option<String>,
    password: Option<String>,
    host: Option<String>,
    port: Option<u16>,
    database: Option<String>,
}

fn read_credentials_file() -> Option<CredentialsFile> {
    let config_path = std::env::var("POSTGRES_CREDENTIALS_PATH")
        .unwrap_or_else(|_| "/data/config/postgres-credentials.json".to_string());
    let content = fs::read_to_string(&config_path).ok()?;
    let json: serde_json::Value = serde_json::from_str(&content).ok()?;

    Some(CredentialsFile {
        username: json
            .get("username")
            .and_then(|v| v.as_str())
            .map(String::from),
        password: json
            .get("password")
            .and_then(|v| v.as_str())
            .map(String::from),
        host: json.get("host").and_then(|v| v.as_str()).map(String::from),
        port: json.get("port").and_then(|v| {
            v.as_u64()
                .map(|n| n as u16)
                .or_else(|| v.as_str().and_then(|s| s.parse().ok()))
        }),
        database: json
            .get("database")
            .and_then(|v| v.as_str())
            .map(String::from),
    })
}
