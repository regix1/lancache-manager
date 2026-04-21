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
                 Hint: Ensure DATABASE_URL is set, or POSTGRES_USER/POSTGRES_PASSWORD are configured, \
                 or /var/run/postgresql socket exists"
            )
        })
}

fn redact_database_url(database_url: &str) -> String {
    if let Some(at_pos) = database_url.find('@') {
        if let Some(colon_pos) = database_url[..at_pos].rfind(':') {
            return format!("{}:***{}", &database_url[..colon_pos], &database_url[at_pos..]);
        }
    }

    database_url.to_string()
}

fn build_database_url() -> String {
    // Check DATABASE_URL first (explicit override)
    if let Ok(url) = env::var("DATABASE_URL") {
        return url;
    }

    let user = env::var("POSTGRES_USER").unwrap_or_else(|_| "lancache".to_string());
    let password = env::var("POSTGRES_PASSWORD").unwrap_or_else(|_| {
        // Try reading from persistent config file
        read_password_from_config().unwrap_or_default()
    });

    if password.is_empty() {
        format!(
            "postgres:///lancache?host=/var/run/postgresql&user={}",
            user
        )
    } else {
        format!(
            "postgres:///lancache?host=/var/run/postgresql&user={}&password={}",
            user, password
        )
    }
}

fn read_password_from_config() -> Option<String> {
    let config_path = std::env::var("POSTGRES_CREDENTIALS_PATH")
        .unwrap_or_else(|_| "/data/config/postgres-credentials.json".to_string());
    let content = fs::read_to_string(&config_path).ok()?;
    let config: serde_json::Value = serde_json::from_str(&content).ok()?;
    config.get("password")?.as_str().map(|s| s.to_string())
}
