use anyhow::{anyhow, Result};
use sqlx::postgres::{PgConnectOptions, PgPoolOptions, PgSslMode};
use sqlx::PgPool;
use std::env;
use std::fs;
use std::str::FromStr;

/// The in-container server has no TCP listener, so it is only reachable through a socket in this
/// directory.
const EMBEDDED_SOCKET_DIR: &str = "/var/run/postgresql";

/// The socket file name ends in the port number, so the in-container server is only reachable at
/// the port entrypoint.sh starts it on.
const EMBEDDED_SOCKET_PORT: u16 = 5432;

pub async fn create_pool() -> Result<PgPool> {
    let options = build_connect_options()?;
    let target = describe_connection(&options);

    PgPoolOptions::new()
        .max_connections(10)
        .connect_with(options)
        .await
        .map_err(|e| {
            anyhow!(
                "Failed to connect to PostgreSQL: {e}\n  Target: {target}\n  \
                 Hint: set DATABASE_URL for full control, or set POSTGRES_MODE=external together \
                 with POSTGRES_HOST + POSTGRES_PASSWORD, or leave POSTGRES_MODE at embedded and \
                 make sure the {EMBEDDED_SOCKET_DIR} socket exists."
            )
        })
}

/// Describe where a connection points, for the failure message. Built from the typed fields
/// rather than from the whole value, because `PgConnectOptions` keeps the password in a plain
/// `Debug` field and printing it would leak the password into the log.
fn describe_connection(options: &PgConnectOptions) -> String {
    let host = match options.get_socket() {
        Some(socket) => socket.display().to_string(),
        None => options.get_host().to_string(),
    };

    format!(
        "host {host} port {} database {} user {}",
        options.get_port(),
        options.get_database().unwrap_or("(server default)"),
        options.get_username()
    )
}

/// Build the PostgreSQL connection settings. Source priority:
///   1. `DATABASE_URL` (explicit override, wins over everything)
///   2. `POSTGRES_MODE=external`: TCP to `POSTGRES_HOST`, else the credentials file's `host`
///   3. any other mode: the embedded server's Unix socket, ignoring every host that is configured
///
/// Within 2 and 3, user, password and database come from env vars, then the credentials file,
/// then the defaults. Host and port apply in external mode only, which is how the API resolves
/// them too, so both halves of the app always reach the same server.
pub(crate) fn build_connect_options() -> Result<PgConnectOptions> {
    if let Some(url) = env::var("DATABASE_URL").ok().filter(|s| !s.is_empty()) {
        return PgConnectOptions::from_str(&url).map_err(|e| {
            anyhow!("DATABASE_URL is not a usable PostgreSQL connection string: {e}")
        });
    }

    let settings = resolve_postgres_settings();

    // Every field goes in as a typed value, so a password containing `&`, `#`, `%`, `+`,
    // a space or a tab reaches the server exactly as typed instead of being re-parsed as
    // further connection parameters.
    let mut options = PgConnectOptions::new()
        .username(&settings.username)
        .database(&settings.database);

    if !settings.password.is_empty() {
        options = options.password(&settings.password);
    }

    if settings.external {
        // A Unix socket path here is a leftover from embedded mode rather than a configured
        // server. Connecting to it produces a restart loop against a socket nobody serves, so
        // say what is missing instead.
        let host = settings
            .host
            .as_deref()
            .filter(|host| *host != EMBEDDED_SOCKET_DIR)
            .ok_or_else(|| {
                anyhow!(
                    "POSTGRES_MODE=external but no external PostgreSQL host is configured, so \
                     database '{}' cannot be reached. Set POSTGRES_HOST and POSTGRES_PASSWORD, \
                     or submit the connection details on the setup screen, or set \
                     POSTGRES_MODE=embedded to use the in-container server.",
                    settings.database
                )
            })?;

        Ok(options.host(host).port(settings.port))
    } else {
        // Port and SSL mode are pinned rather than left at whatever `PgConnectOptions::new()`
        // picked up from the ambient libpq variables. An operator who exports PGPORT=5433 so
        // their own psql reaches a different server would otherwise send every Rust binary to
        // /var/run/postgresql/.s.PGSQL.5433, a socket nobody serves, while the API keeps working
        // and the dashboard just stops ingesting. PostgreSQL never speaks TLS over a Unix
        // socket, so PGSSLMODE=require has nothing to negotiate here either.
        Ok(options
            .host(EMBEDDED_SOCKET_DIR)
            .port(EMBEDDED_SOCKET_PORT)
            .ssl_mode(PgSslMode::Disable))
    }
}

/// PostgreSQL connection settings once env vars, the credentials file and the defaults have been
/// merged. `host` stays optional because external mode has nothing to connect to without it.
struct PostgresSettings {
    external: bool,
    host: Option<String>,
    port: u16,
    username: String,
    password: String,
    database: String,
}

fn resolve_postgres_settings() -> PostgresSettings {
    let creds = read_credentials_file();

    // Every field is filtered on both sources, and separately. entrypoint.sh exports these
    // unconditionally, so an empty env var is the normal shape for "not set" and has to fall
    // through to the file rather than shadow it; a hand-edited file can carry `"username": ""`
    // just as easily, and taking that would authenticate as a role that does not exist instead
    // of as the default the entrypoint actually created. The API reads them the same way, and
    // disagreeing would point the two halves of the app at different credentials.
    let username = env::var("POSTGRES_USER")
        .ok()
        .filter(|s| !s.is_empty())
        .or_else(|| creds.as_ref().and_then(|c| c.username.clone()))
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "lancache".to_string());

    let password = env::var("POSTGRES_PASSWORD")
        .ok()
        .filter(|s| !s.is_empty())
        .or_else(|| creds.as_ref().and_then(|c| c.password.clone()))
        .unwrap_or_default();

    let database = env::var("POSTGRES_DB")
        .ok()
        .filter(|s| !s.is_empty())
        .or_else(|| creds.as_ref().and_then(|c| c.database.clone()))
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "lancache".to_string());

    let host = env::var("POSTGRES_HOST")
        .ok()
        .filter(|s| !s.is_empty())
        .or_else(|| creds.as_ref().and_then(|c| c.host.clone()))
        .filter(|s| !s.is_empty());

    let port = env::var("POSTGRES_PORT")
        .ok()
        .and_then(|s| s.parse::<u16>().ok())
        .or_else(|| creds.as_ref().and_then(|c| c.port))
        .unwrap_or(5432);

    // entrypoint.sh lowercases and trims this before exporting it, but that only covers the
    // processes it launches. A binary started by hand, on bare metal or in a dev shell sees the
    // raw value, and `External` resolving to embedded would send it to the container socket while
    // its failure message named a path the operator never configured.
    PostgresSettings {
        external: env::var("POSTGRES_MODE")
            .map(|mode| mode.trim().eq_ignore_ascii_case("external"))
            .unwrap_or(false),
        host,
        port,
        username,
        password,
        database,
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

    // Carrying on with env vars and defaults is right, but doing it silently is how one bad
    // byte in this file turns into a connection failure with no stated cause. A file that is
    // simply absent is the normal first-run shape and stays quiet.
    let content = match fs::read_to_string(&config_path) {
        Ok(content) => content,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return None,
        Err(e) => {
            eprintln!(
                "Warning: could not read {config_path} ({e}). Using environment variables and \
                 defaults for the PostgreSQL connection."
            );
            return None;
        }
    };

    let json: serde_json::Value = match serde_json::from_str(&content) {
        Ok(json) => json,
        Err(e) => {
            eprintln!(
                "Warning: {config_path} is not valid JSON ({e}). Using environment variables and \
                 defaults for the PostgreSQL connection."
            );
            return None;
        }
    };

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

#[cfg(test)]
const CONNECTION_VARS: [&str; 11] = [
    "DATABASE_URL",
    "POSTGRES_MODE",
    "POSTGRES_HOST",
    "POSTGRES_PORT",
    "POSTGRES_USER",
    "POSTGRES_PASSWORD",
    "POSTGRES_DB",
    "POSTGRES_CREDENTIALS_PATH",
    // These three reach `PgConnectOptions::new()` as defaults, and the tests below set them on
    // purpose to prove an ambient libpq variable cannot move the embedded connection. Clearing
    // happens at the start of each test, so leaving one out lets the value a test sets leak
    // into every test that runs after it. PGUSER and PGDATABASE are deliberately absent:
    // `build_connect_options` sets username and database on every path, so neither can reach
    // an assertion.
    "PGHOST",
    "PGPORT",
    "PGSSLMODE",
];

/// Holds the test environment lock; dropping it restores every connection variable to the value
/// it held when the lock was taken.
#[cfg(test)]
pub(crate) struct TestEnvLock {
    _lock: std::sync::MutexGuard<'static, ()>,
    saved: Vec<(&'static str, Option<String>)>,
}

#[cfg(test)]
impl Drop for TestEnvLock {
    fn drop(&mut self) {
        for (var, value) in &self.saved {
            match value {
                Some(value) => env::set_var(var, value),
                None => env::remove_var(var),
            }
        }
    }
}

/// Connection settings come from process-wide environment variables, so every test that either
/// mutates them or opens a connection through `build_connect_options` while other tests run
/// holds this lock. The guard restores the prior values on drop, so one test clearing
/// DATABASE_URL cannot send another test's connection to a host that does not exist.
#[cfg(test)]
pub(crate) fn lock_test_env() -> TestEnvLock {
    use std::sync::{Mutex, OnceLock};
    static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
    let lock = LOCK
        .get_or_init(|| Mutex::new(()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let saved = CONNECTION_VARS
        .iter()
        .map(|var| (*var, env::var(var).ok()))
        .collect();
    TestEnvLock { _lock: lock, saved }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::path::PathBuf;

    /// These tests additionally start from a known-empty environment, so ambient configuration
    /// cannot reach their assertions.
    fn lock_env() -> TestEnvLock {
        let guard = lock_test_env();

        for var in CONNECTION_VARS {
            env::remove_var(var);
        }
        env::set_var(
            "POSTGRES_CREDENTIALS_PATH",
            "/no-such-directory/postgres-credentials.json",
        );

        guard
    }

    fn write_credentials(name: &str, json: &str) -> PathBuf {
        let path = env::temp_dir().join(name);
        let mut file = fs::File::create(&path).expect("temp credentials file");
        file.write_all(json.as_bytes())
            .expect("temp credentials file");
        env::set_var("POSTGRES_CREDENTIALS_PATH", &path);
        path
    }

    /// The socket directory a set of options resolves to, or `None` for a TCP target. sqlx accepts
    /// either an explicit socket or a host starting with `/`, so both shapes are checked.
    fn socket_dir(options: &PgConnectOptions) -> Option<String> {
        match options.get_socket() {
            Some(socket) => Some(socket.display().to_string()),
            None if options.get_host().starts_with('/') => Some(options.get_host().to_string()),
            None => None,
        }
    }

    #[test]
    fn embedded_mode_ignores_a_configured_host() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "embedded");
        env::set_var("POSTGRES_HOST", "db.example.invalid");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(socket_dir(&options).as_deref(), Some(EMBEDDED_SOCKET_DIR));
    }

    #[test]
    fn embedded_mode_ignores_a_host_from_the_credentials_file() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "embedded");
        write_credentials(
            "lancache-db-embedded-host.json",
            r#"{"username":"someone","password":"p","host":"db.example.invalid","port":6543,"database":"other"}"#,
        );

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(socket_dir(&options).as_deref(), Some(EMBEDDED_SOCKET_DIR));
        assert_eq!(options.get_username(), "someone");
        assert_eq!(options.get_database(), Some("other"));
    }

    #[test]
    fn an_unset_mode_means_embedded() {
        let _guard = lock_env();
        env::set_var("POSTGRES_HOST", "db.example.invalid");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(socket_dir(&options).as_deref(), Some(EMBEDDED_SOCKET_DIR));
    }

    #[test]
    fn external_mode_without_a_host_is_an_error_not_a_socket() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "external");
        env::set_var("POSTGRES_DB", "lancache_prod");

        let message = build_connect_options()
            .expect_err("external mode with no host must not connect")
            .to_string();

        assert!(message.contains("lancache_prod"), "{message}");
        assert!(!message.contains(EMBEDDED_SOCKET_DIR), "{message}");
    }

    #[test]
    fn external_mode_with_a_leftover_socket_host_is_an_error() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "external");
        env::set_var("POSTGRES_HOST", EMBEDDED_SOCKET_DIR);

        assert!(
            build_connect_options().is_err(),
            "external mode connected to the embedded socket"
        );
    }

    #[test]
    fn an_empty_host_in_the_credentials_file_counts_as_absent() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "external");
        write_credentials(
            "lancache-db-empty-host.json",
            r#"{"username":"u","password":"p","host":"","database":"d"}"#,
        );

        assert!(
            build_connect_options().is_err(),
            "an empty host was treated as a real host"
        );
    }

    #[test]
    fn an_empty_username_or_database_in_the_credentials_file_counts_as_absent() {
        let _guard = lock_env();
        write_credentials(
            "lancache-db-empty-user.json",
            r#"{"username":"","password":"p","database":""}"#,
        );

        let settings = resolve_postgres_settings();

        assert_eq!(settings.username, "lancache");
        assert_eq!(settings.database, "lancache");
    }

    #[test]
    fn an_empty_host_env_var_still_defers_to_the_credentials_file() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "external");
        env::set_var("POSTGRES_HOST", "");
        write_credentials(
            "lancache-db-file-host.json",
            r#"{"username":"u","password":"p","host":"db.example.invalid","port":6543,"database":"d"}"#,
        );

        let options = build_connect_options().expect("the credentials file supplies the host");

        assert_eq!(options.get_host(), "db.example.invalid");
        assert_eq!(options.get_port(), 6543);
    }

    #[test]
    fn an_empty_user_env_var_still_defers_to_the_credentials_file() {
        let _guard = lock_env();
        env::set_var("POSTGRES_USER", "");
        env::set_var("POSTGRES_PASSWORD", "");
        write_credentials(
            "lancache-db-file-user.json",
            r#"{"username":"someone","password":"hunter2","database":"d"}"#,
        );

        let settings = resolve_postgres_settings();

        assert_eq!(settings.username, "someone");
        assert_eq!(settings.password, "hunter2");
    }

    #[test]
    fn a_password_with_url_characters_round_trips_exactly() {
        let _guard = lock_env();
        let password = "a&b#c%d+e f\tg";
        env::set_var("POSTGRES_MODE", "embedded");
        env::set_var("POSTGRES_PASSWORD", password);

        let settings = resolve_postgres_settings();

        assert_eq!(settings.password, password);
    }

    #[test]
    fn a_password_with_url_characters_survives_the_credentials_file() {
        let _guard = lock_env();
        let password = "a&b#c%d+e f\tg";
        write_credentials(
            "lancache-db-password.json",
            &format!(
                r#"{{"username":"u","password":{},"database":"d"}}"#,
                serde_json::Value::String(password.to_string())
            ),
        );

        let settings = resolve_postgres_settings();

        assert_eq!(settings.password, password);
    }

    #[test]
    fn a_password_holding_url_characters_cannot_move_the_target() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "embedded");
        env::set_var("POSTGRES_PASSWORD", "p&host=/tmp/hijacked");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(socket_dir(&options).as_deref(), Some(EMBEDDED_SOCKET_DIR));
    }

    #[test]
    fn the_failure_target_names_the_server_without_the_password() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "external");
        env::set_var("POSTGRES_HOST", "db.example.invalid");
        env::set_var("POSTGRES_PORT", "6543");
        env::set_var("POSTGRES_USER", "someone");
        env::set_var("POSTGRES_PASSWORD", "hunter2");
        env::set_var("POSTGRES_DB", "lancache_prod");

        let target = describe_connection(&build_connect_options().expect("a host is configured"));

        assert!(target.contains("db.example.invalid"), "{target}");
        assert!(target.contains("6543"), "{target}");
        assert!(target.contains("lancache_prod"), "{target}");
        assert!(target.contains("someone"), "{target}");
        assert!(!target.contains("hunter2"), "{target}");
    }

    #[test]
    fn database_url_still_wins_over_everything() {
        let _guard = lock_env();
        env::set_var(
            "DATABASE_URL",
            "postgres://someone:hunter2@db.example.invalid:6543/lancache_prod",
        );
        env::set_var("POSTGRES_MODE", "external");
        env::set_var("POSTGRES_HOST", "ignored.example.invalid");

        let options = build_connect_options().expect("a usable connection string");

        assert_eq!(options.get_host(), "db.example.invalid");
        assert_eq!(options.get_port(), 6543);
        assert_eq!(options.get_database(), Some("lancache_prod"));
    }

    #[test]
    fn an_ambient_pgport_cannot_move_the_embedded_socket() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "embedded");
        env::set_var("PGPORT", "5433");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(socket_dir(&options).as_deref(), Some(EMBEDDED_SOCKET_DIR));
        assert_eq!(options.get_port(), EMBEDDED_SOCKET_PORT);
    }

    #[test]
    fn a_postgres_port_meant_for_an_external_server_cannot_move_the_embedded_socket() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "embedded");
        env::set_var("POSTGRES_PORT", "6543");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(options.get_port(), EMBEDDED_SOCKET_PORT);
    }

    #[test]
    fn an_ambient_pghost_cannot_move_the_embedded_socket() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "embedded");
        env::set_var("PGHOST", "db.example.invalid");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(socket_dir(&options).as_deref(), Some(EMBEDDED_SOCKET_DIR));
    }

    #[test]
    fn an_ambient_pgsslmode_cannot_demand_tls_on_the_embedded_socket() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "embedded");
        env::set_var("PGSSLMODE", "require");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert!(
            matches!(options.get_ssl_mode(), PgSslMode::Disable),
            "TLS was demanded on a Unix socket, which PostgreSQL cannot serve"
        );
    }

    #[test]
    fn a_differently_cased_external_mode_still_means_external() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "External");
        env::set_var("POSTGRES_HOST", "db.example.invalid");

        let options = build_connect_options().expect("a host is configured");

        assert_eq!(options.get_host(), "db.example.invalid");
        assert_eq!(socket_dir(&options), None);
    }

    #[test]
    fn a_padded_external_mode_still_means_external() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "  EXTERNAL \n");
        env::set_var("POSTGRES_HOST", "db.example.invalid");

        let options = build_connect_options().expect("a host is configured");

        assert_eq!(options.get_host(), "db.example.invalid");
    }

    #[test]
    fn a_mode_that_merely_contains_external_is_not_external() {
        let _guard = lock_env();
        env::set_var("POSTGRES_MODE", "external-preview");
        env::set_var("POSTGRES_HOST", "db.example.invalid");

        let options = build_connect_options().expect("embedded mode always has a target");

        assert_eq!(socket_dir(&options).as_deref(), Some(EMBEDDED_SOCKET_DIR));
    }

    #[test]
    fn a_corrupt_credentials_file_falls_back_to_the_defaults() {
        let _guard = lock_env();
        write_credentials("lancache-db-truncated.json", r#"{"username":"u","passw"#);

        let settings = resolve_postgres_settings();

        assert_eq!(settings.username, "lancache");
        assert_eq!(settings.database, "lancache");
    }
}
