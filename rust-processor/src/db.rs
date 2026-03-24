use sqlx::postgres::PgPoolOptions;
use sqlx::PgPool;
use std::env;
use std::fs;

pub async fn create_pool() -> PgPool {
    let database_url = build_database_url();

    PgPoolOptions::new()
        .max_connections(10)
        .connect(&database_url)
        .await
        .expect("Failed to connect to PostgreSQL")
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
            "postgres://{}:{}@/lancache?host=/var/run/postgresql",
            user, password
        )
    }
}

fn read_password_from_config() -> Option<String> {
    let config_path = "/data/postgres-credentials.json";
    let content = fs::read_to_string(config_path).ok()?;
    let config: serde_json::Value = serde_json::from_str(&content).ok()?;
    config.get("password")?.as_str().map(|s| s.to_string())
}
