#!/bin/bash

# PUID/PGID support for lancache-manager
# Similar to linuxserver.io images

PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Export PUID/PGID for the .NET application to display
export LANCACHE_PUID=$PUID
export LANCACHE_PGID=$PGID

# Create group if GID doesn't exist
# Use -o to allow non-unique GIDs (e.g. PGID=100 may conflict with existing 'users' group)
if ! getent group "$PGID" > /dev/null 2>&1; then
    groupadd -o -g "$PGID" lancache
elif getent group lancache > /dev/null 2>&1; then
    groupmod -o -g "$PGID" lancache 2>/dev/null || true
fi

# Get group name for the GID
GROUP_NAME=$(getent group "$PGID" | cut -d: -f1)

# Create user if UID doesn't exist
# Use -o to allow non-unique UIDs (e.g. PUID=99 may conflict with existing 'nobody' user)
if ! getent passwd "$PUID" > /dev/null 2>&1; then
    useradd -o -u "$PUID" -g "$PGID" -d /app -s /bin/bash -M lancache
elif getent passwd lancache > /dev/null 2>&1; then
    usermod -o -u "$PUID" -g "$PGID" lancache 2>/dev/null || true
fi

# Get username for the UID
USER_NAME=$(getent passwd "$PUID" | cut -d: -f1)

# Handle docker socket permissions if mounted
# This allows the container to communicate with docker for nginx log rotation
DOCKER_GROUP=""
if [ -S /var/run/docker.sock ]; then
    DOCKER_GID=$(stat -c '%g' /var/run/docker.sock)

    # Create docker group with the socket's GID if it doesn't exist
    if ! getent group "$DOCKER_GID" > /dev/null 2>&1; then
        groupadd -g "$DOCKER_GID" docker
    fi

    DOCKER_GROUP=$(getent group "$DOCKER_GID" | cut -d: -f1)

    # Add our user to the docker group
    usermod -aG "$DOCKER_GROUP" "$USER_NAME" 2>/dev/null || true

    echo "Docker socket detected (GID: $DOCKER_GID). User '$USER_NAME' added to group '$DOCKER_GROUP'."
fi

# Detect if running as root - ownership fixes require root
IS_ROOT=0
if [ "$(id -u)" -eq 0 ]; then
    IS_ROOT=1
fi

# Change ownership of application directories
# /data needs write access for database and progress files
# /app needs read access for the application
# Exclude /data/postgresql - it must stay owned by the postgres OS user
if [ "$IS_ROOT" -eq 1 ]; then
    chown -R "$PUID:$PGID" /app/rust-processor 2>/dev/null || true
    find /data -mindepth 1 -maxdepth 1 ! -name postgresql -exec chown -R "$PUID:$PGID" {} + 2>/dev/null || true
    chown "$PUID:$PGID" /data 2>/dev/null || true
else
    echo "WARNING: Not running as root (uid=$(id -u)). Cannot fix file ownership."
    echo "  Ensure directories are pre-owned by UID:$PUID GID:$PGID on the host."
fi

# Fix ownership of /logs and /cache if they are writable (not mounted read-only)
# Only chown the directory itself (not -R) to avoid slow recursive operations on large caches
for dir in /logs /cache; do
    if [ -d "$dir" ] && touch "$dir/.permcheck" 2>/dev/null; then
        rm -f "$dir/.permcheck"
        if [ "$IS_ROOT" -eq 1 ]; then
            chown "$PUID:$PGID" "$dir" 2>/dev/null || true
            echo "Fixed ownership of $dir for UID:$PUID GID:$PGID"
        fi
    fi
done

# Try to clear restrictive ACLs on bind-mounted dirs (needed for Unraid)
if command -v setfacl &>/dev/null; then
    for dir in /logs /cache /data; do
        if [ -d "$dir" ]; then
            setfacl -b "$dir" 2>/dev/null || chmod 775 "$dir" 2>/dev/null || true
        fi
    done
fi

# Write access diagnostics - warn if the app user cannot write to critical dirs
for dir in /logs /cache; do
    if [ -d "$dir" ]; then
        if ! gosu "$USER_NAME" touch "$dir/.write_test" 2>/dev/null; then
            echo "WARNING: No write access to $dir as ${PUID}:${PGID}"
            echo "  If on Unraid, run on host: setfacl -Rb <host_path_to_$dir>"
            echo "  Or ensure: chown -R ${PUID}:${PGID} <host_path> && chmod -R 775 <host_path>"
        else
            rm -f "$dir/.write_test" 2>/dev/null
        fi
    fi
done

# Ensure rust binaries are executable
chmod +x /app/rust-processor/* 2>/dev/null || true

# ---------------------------------------------------------------------------
# Constants used by both embedded and external modes
# ---------------------------------------------------------------------------
PGDATABASE="${POSTGRES_DB:-lancache}"
SQLITE_DB="/data/db/LancacheManager.db"
PG_CONFIG="/data/config/postgres-credentials.json"
MIGRATION_MARKER="/data/postgres-migration.complete"

# ---------------------------------------------------------------------------
# Credential sourcing (env var > config file > defaults)
# Reads username/password for both modes; host/port/database for external mode.
# ---------------------------------------------------------------------------
PGUSER="${POSTGRES_USER:-lancache}"
PGPASSWORD="${POSTGRES_PASSWORD:-}"
PGHOST="${POSTGRES_HOST:-}"
PGPORT="${POSTGRES_PORT:-5432}"

if [ -f "$PG_CONFIG" ]; then
    if command -v jq &>/dev/null; then
        # Preferred: use jq for reliable JSON parsing
        [ -z "$PGPASSWORD" ] && PGPASSWORD=$(jq -r '.password // empty' "$PG_CONFIG" 2>/dev/null)
        PGUSER_FROM_CONFIG=$(jq -r '.username // empty' "$PG_CONFIG" 2>/dev/null)
        [ -z "$PGHOST" ]     && PGHOST=$(jq -r '.host // empty' "$PG_CONFIG" 2>/dev/null)
        PGPORT_FROM_CONFIG=$(jq -r '.port // empty' "$PG_CONFIG" 2>/dev/null)
        PGDB_FROM_CONFIG=$(jq -r '.database // empty' "$PG_CONFIG" 2>/dev/null)
    else
        # Fallback: regex extraction
        [ -z "$PGPASSWORD" ] && PGPASSWORD=$(sed -n 's/.*"password"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
        PGUSER_FROM_CONFIG=$(sed -n 's/.*"username"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
        [ -z "$PGHOST" ]     && PGHOST=$(sed -n 's/.*"host"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
        PGPORT_FROM_CONFIG=$(sed -n 's/.*"port"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' "$PG_CONFIG" | head -n1)
        PGDB_FROM_CONFIG=$(sed -n 's/.*"database"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
    fi
    PGUSER="${PGUSER_FROM_CONFIG:-$PGUSER}"
    [ -n "$PGPORT_FROM_CONFIG" ] && [ -z "$POSTGRES_PORT" ] && PGPORT="$PGPORT_FROM_CONFIG"
    [ -n "$PGDB_FROM_CONFIG" ] && [ -z "$POSTGRES_DB" ] && PGDATABASE="$PGDB_FROM_CONFIG"
fi

# Export for the .NET app and child processes
export POSTGRES_USER="$PGUSER"
export POSTGRES_PASSWORD="$PGPASSWORD"
export POSTGRES_HOST="$PGHOST"
export POSTGRES_PORT="$PGPORT"
export POSTGRES_DB="$PGDATABASE"

# ---------------------------------------------------------------------------
# Mode dispatch: embedded (default) starts the in-container Postgres;
# external skips it and connects to a user-managed Postgres.
# ---------------------------------------------------------------------------
POSTGRES_MODE="${POSTGRES_MODE:-embedded}"
export POSTGRES_MODE

if [ "$POSTGRES_MODE" = "external" ]; then
    echo "[postgres] External mode - skipping embedded PostgreSQL startup."
    if [ -n "$PGHOST" ] && [ -n "$PGPASSWORD" ]; then
        echo "[postgres] External target: ${PGHOST}:${PGPORT}/${PGDATABASE} as ${PGUSER}"
    else
        echo "[postgres] External credentials not provided yet. App will start in setup-only mode."
        echo "[postgres] Open the UI and submit external DB credentials, then restart the container."
    fi
else
    # -----------------------------------------------------------------------
    # Embedded PostgreSQL startup
    # -----------------------------------------------------------------------
    # Determine PostgreSQL data directory
    # Primary: /data/postgresql (inside the user's /data volume - backed up alongside app data)
    # Fallback: /var/lib/postgresql/data (Docker-managed volume - survives container removal)
    PGDATA_PRIMARY="/data/postgresql"
    PGDATA_FALLBACK="/var/lib/postgresql/data"

    if [ -f "$PGDATA_PRIMARY/PG_VERSION" ]; then
        # Already initialized at primary location
        PGDATA="$PGDATA_PRIMARY"
    elif [ -f "$PGDATA_FALLBACK/PG_VERSION" ]; then
        # Existing install with data at fallback location - don't break it
        PGDATA="$PGDATA_FALLBACK"
        echo "[postgres] Using existing data at $PGDATA (mount postgres_data volume to persist)"
    elif [ -d "/data" ] && touch "/data/.pgcheck" 2>/dev/null; then
        # Fresh install - /data is writable, use primary location
        rm -f "/data/.pgcheck"
        PGDATA="$PGDATA_PRIMARY"
    else
        # /data not writable - use fallback
        PGDATA="$PGDATA_FALLBACK"
        echo "[postgres] /data not writable, using fallback: $PGDATA"
    fi

    PG_LOG="/var/log/postgresql.log"

    # Initialize PostgreSQL data directory on first run
    if [ ! -f "$PGDATA/PG_VERSION" ]; then
        echo "[postgres] Initializing data directory..."
        mkdir -p "$PGDATA"
        chown -R postgres:postgres "$PGDATA"
        su - postgres -c "/usr/lib/postgresql/17/bin/initdb -D $PGDATA --auth-local=trust --auth-host=trust" > /dev/null

        # Apply our tuned config
        cp /etc/postgresql/17/main/postgresql.conf "$PGDATA/postgresql.conf"

        # Allow only local (Unix socket) connections; no TCP
        {
            echo "local all all trust"
        } > "$PGDATA/pg_hba.conf"
    fi

    # Ensure PostgreSQL data directory is owned by postgres (may have been changed by upgrades or manual chown)
    chown -R postgres:postgres "$PGDATA"

    # Ensure the PostgreSQL log file exists and is writable by the postgres user
    touch "$PG_LOG"
    chown postgres:postgres "$PG_LOG"

    # Clean stale PID file from unclean shutdown to prevent
    # "another server might be running" warning on container restart
    rm -f "$PGDATA/postmaster.pid"

    # Start PostgreSQL as the postgres OS user
    echo "[postgres] Starting PostgreSQL 17..."
    su - postgres -c "/usr/lib/postgresql/17/bin/pg_ctl -D $PGDATA -l $PG_LOG start" > /dev/null

    # Wait until PostgreSQL is ready (pg_isready, max 30 s)
    echo "[postgres] Waiting for PostgreSQL to be ready..."
    timeout 30 bash -c "until su - postgres -c 'pg_isready -q' 2>/dev/null; do sleep 1; done" \
        || { echo "[postgres] ERROR: PostgreSQL did not become ready in time"; exit 1; }
    echo "[postgres] PostgreSQL is ready."

    # Create/update PostgreSQL role with credentials
    if [ -n "$PGPASSWORD" ]; then
        # Credentials available - create user with password
        su - postgres -c "psql -qtc \"SELECT 1 FROM pg_roles WHERE rolname='$PGUSER'\" | grep -q 1 \
            || psql -qc \"CREATE USER $PGUSER WITH SUPERUSER PASSWORD '$PGPASSWORD';\""
        # Update password if user already exists (in case password changed)
        su - postgres -c "psql -qc \"ALTER USER $PGUSER WITH PASSWORD '$PGPASSWORD';\""
    else
        # No password yet - create user without password (local trust auth)
        # App will show first-run setup page to collect credentials
        su - postgres -c "psql -qtc \"SELECT 1 FROM pg_roles WHERE rolname='$PGUSER'\" | grep -q 1 \
            || psql -qc \"CREATE USER $PGUSER WITH SUPERUSER;\""
        echo "WARNING: No POSTGRES_PASSWORD set. The app will prompt for credentials on first access."
    fi

    # Create database if it doesn't exist
    su - postgres -c "psql -qtc \"SELECT 1 FROM pg_database WHERE datname='$PGDATABASE'\" | grep -q 1 \
        || psql -qc \"CREATE DATABASE $PGDATABASE OWNER $PGUSER;\""
fi

# ---------------------------------------------------------------------------
# SQLite -> PostgreSQL data migration (before starting the web app)
# ---------------------------------------------------------------------------
# Works for both embedded and external modes. In external mode we only run
# when connection details are available; otherwise the app will start in
# setup-only mode and the user will configure DB creds via the UI.
# ---------------------------------------------------------------------------
CAN_RUN_MIGRATION=0
if [ "$POSTGRES_MODE" = "external" ]; then
    if [ -n "$PGHOST" ] && [ -n "$PGPASSWORD" ]; then
        CAN_RUN_MIGRATION=1
    fi
else
    CAN_RUN_MIGRATION=1
fi

# Backward-compat: older builds wrote the marker inside the embedded PGDATA dir.
# Treat that as "already migrated" so we don't re-run on upgrade.
LEGACY_MIGRATION_MARKER_PRIMARY="/data/postgresql/.migration_complete"
LEGACY_MIGRATION_MARKER_FALLBACK="/var/lib/postgresql/data/.migration_complete"
if [ ! -f "$MIGRATION_MARKER" ]; then
    if [ -f "$LEGACY_MIGRATION_MARKER_PRIMARY" ] || [ -f "$LEGACY_MIGRATION_MARKER_FALLBACK" ]; then
        echo "[migration] Found legacy migration marker - skipping re-import."
        mkdir -p "$(dirname "$MIGRATION_MARKER")"
        touch "$MIGRATION_MARKER"
    fi
fi

if [ "$CAN_RUN_MIGRATION" -eq 1 ] && [ -f "$SQLITE_DB" ] && [ ! -f "$MIGRATION_MARKER" ]; then
    echo "[postgres] SQLite database found. Preparing PostgreSQL schema before startup..."

    echo "[migration] Running EF Core migrations in migrate-only mode..."
    if gosu "$USER_NAME" env LANCACHE_MIGRATE_ONLY=1 dotnet LancacheManager.dll; then
        echo "[migration] EF Core schema created successfully."
    else
        echo "[migration] ERROR: EF Core migrate-only run failed."
        exit 1
    fi

    echo "[migration] Running SQLite -> PostgreSQL data migration..."
    if ! /scripts/migrate-sqlite-to-postgres.sh "$SQLITE_DB" "$PGDATABASE"; then
        echo "[migration] ERROR: Data migration script failed."
        exit 1
    fi
fi

# Run the application as the specified user
# Use username (not UID:GID) so gosu picks up supplementary groups from /etc/group
# The app's MigrateAsync creates/updates the PostgreSQL schema on startup.
exec gosu "$USER_NAME" dotnet LancacheManager.dll "$@"
