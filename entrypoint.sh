#!/bin/bash

# PUID/PGID support for lancache-manager
# Similar to linuxserver.io images

PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Export PUID/PGID for the .NET application to display
export LANCACHE_PUID=$PUID
export LANCACHE_PGID=$PGID

# Create group if GID doesn't exist
if ! getent group "$PGID" > /dev/null 2>&1; then
    groupadd -g "$PGID" lancache
fi

# Get group name for the GID
GROUP_NAME=$(getent group "$PGID" | cut -d: -f1)

# Create user if UID doesn't exist
if ! getent passwd "$PUID" > /dev/null 2>&1; then
    useradd -u "$PUID" -g "$PGID" -d /app -s /bin/bash -M lancache
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

# Change ownership of application directories
# /data needs write access for database and progress files
# /app needs read access for the application
# Exclude /data/postgresql — it must stay owned by the postgres OS user
chown -R "$PUID:$PGID" /app/rust-processor 2>/dev/null || true
find /data -mindepth 1 -maxdepth 1 ! -name postgresql -exec chown -R "$PUID:$PGID" {} + 2>/dev/null || true
chown "$PUID:$PGID" /data 2>/dev/null || true

# Fix ownership of /logs and /cache if they are writable (not mounted read-only)
# Only chown the directory itself (not -R) to avoid slow recursive operations on large caches
for dir in /logs /cache; do
    if [ -d "$dir" ] && touch "$dir/.permcheck" 2>/dev/null; then
        rm -f "$dir/.permcheck"
        chown "$PUID:$PGID" "$dir" 2>/dev/null || true
        echo "Fixed ownership of $dir for UID:$PUID GID:$PGID"
    fi
done

# Ensure rust binaries are executable
chmod +x /app/rust-processor/* 2>/dev/null || true

# ---------------------------------------------------------------------------
# PostgreSQL startup
# ---------------------------------------------------------------------------
# Determine PostgreSQL data directory
# Primary: /data/postgresql (inside the user's /data volume — backed up alongside app data)
# Fallback: /var/lib/postgresql/data (Docker-managed volume — survives container removal)
PGDATA_PRIMARY="/data/postgresql"
PGDATA_FALLBACK="/var/lib/postgresql/data"

if [ -f "$PGDATA_PRIMARY/PG_VERSION" ]; then
    # Already initialized at primary location
    PGDATA="$PGDATA_PRIMARY"
elif [ -f "$PGDATA_FALLBACK/PG_VERSION" ]; then
    # Existing install with data at fallback location — don't break it
    PGDATA="$PGDATA_FALLBACK"
    echo "[postgres] Using existing data at $PGDATA (mount postgres_data volume to persist)"
elif [ -d "/data" ] && touch "/data/.pgcheck" 2>/dev/null; then
    # Fresh install — /data is writable, use primary location
    rm -f "/data/.pgcheck"
    PGDATA="$PGDATA_PRIMARY"
else
    # /data not writable — use fallback
    PGDATA="$PGDATA_FALLBACK"
    echo "[postgres] /data not writable, using fallback: $PGDATA"
fi

PGDATABASE="lancache"
SQLITE_DB="/data/db/LancacheManager.db"
PG_LOG="/var/log/postgresql.log"

# PostgreSQL credentials (env var → config file → no password)
PGUSER="${POSTGRES_USER:-lancache}"
PGPASSWORD="${POSTGRES_PASSWORD:-}"
PG_CONFIG="/data/postgres-credentials.json"

# If no env var password, check config file
if [ -z "$PGPASSWORD" ] && [ -f "$PG_CONFIG" ]; then
    PGPASSWORD=$(grep -o '"password":"[^"]*"' "$PG_CONFIG" | cut -d'"' -f4)
    PGUSER_FROM_CONFIG=$(grep -o '"username":"[^"]*"' "$PG_CONFIG" | cut -d'"' -f4)
    PGUSER="${PGUSER_FROM_CONFIG:-$PGUSER}"
fi

# Initialize PostgreSQL data directory on first run
if [ ! -f "$PGDATA/PG_VERSION" ]; then
    echo "[postgres] Initializing data directory..."
    mkdir -p "$PGDATA"
    chown -R postgres:postgres "$PGDATA"
    su - postgres -c "/usr/lib/postgresql/17/bin/initdb -D $PGDATA --auth-local=trust --auth-host=trust"

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

# Start PostgreSQL as the postgres OS user
echo "[postgres] Starting PostgreSQL 17..."
su - postgres -c "/usr/lib/postgresql/17/bin/pg_ctl -D $PGDATA -l $PG_LOG start"

# Wait until PostgreSQL is ready (pg_isready, max 30 s)
echo "[postgres] Waiting for PostgreSQL to be ready..."
timeout 30 bash -c "until su - postgres -c 'pg_isready -q' 2>/dev/null; do sleep 1; done" \
    || { echo "[postgres] ERROR: PostgreSQL did not become ready in time"; exit 1; }
echo "[postgres] PostgreSQL is ready."

# Create/update PostgreSQL role with credentials
if [ -n "$PGPASSWORD" ]; then
    # Credentials available — create user with password
    su - postgres -c "psql -tc \"SELECT 1 FROM pg_roles WHERE rolname='$PGUSER'\" | grep -q 1 \
        || psql -c \"CREATE USER $PGUSER WITH SUPERUSER PASSWORD '$PGPASSWORD';\""
    # Update password if user already exists (in case password changed)
    su - postgres -c "psql -c \"ALTER USER $PGUSER WITH PASSWORD '$PGPASSWORD';\""
else
    # No password yet — create user without password (local trust auth)
    # App will show first-run setup page to collect credentials
    su - postgres -c "psql -tc \"SELECT 1 FROM pg_roles WHERE rolname='$PGUSER'\" | grep -q 1 \
        || psql -c \"CREATE USER $PGUSER WITH SUPERUSER;\""
    echo "WARNING: No POSTGRES_PASSWORD set. The app will prompt for credentials on first access."
fi

# Create database if it doesn't exist
su - postgres -c "psql -tc \"SELECT 1 FROM pg_database WHERE datname='$PGDATABASE'\" | grep -q 1 \
    || psql -c \"CREATE DATABASE $PGDATABASE OWNER $PGUSER;\""

# Export for the .NET app to read
export POSTGRES_USER="$PGUSER"
export POSTGRES_PASSWORD="$PGPASSWORD"

# ---------------------------------------------------------------------------
# SQLite → PostgreSQL data migration (before starting the web app)
# ---------------------------------------------------------------------------
# Run the app once in migrate-only mode so EF Core creates the PostgreSQL
# schema, then import SQLite data synchronously before the main host starts.
# This avoids racing a background import against app startup.
# ---------------------------------------------------------------------------
if [ -f "$SQLITE_DB" ] && [ ! -f "$PGDATA/.migration_complete" ]; then
    echo "[postgres] SQLite database found. Preparing PostgreSQL schema before startup..."

    echo "[migration] Running EF Core migrations in migrate-only mode..."
    if gosu "$USER_NAME" env LANCACHE_MIGRATE_ONLY=1 dotnet LancacheManager.dll; then
        echo "[migration] EF Core schema created successfully."
    else
        echo "[migration] ERROR: EF Core migrate-only run failed."
        exit 1
    fi

    if ! su - postgres -c "psql -d $PGDATABASE -tAc \"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='Downloads'\"" 2>/dev/null | grep -q '1'; then
        echo "[migration] ERROR: Expected PostgreSQL table \"Downloads\" was not created by EF Core migrations."
        exit 1
    fi

    # Create pgloader load file for data-only migration
    # Keep the loader conservative on memory usage: upstream pgloader/SBCL
    # issues recommend small prefetch/batch settings and single-threaded loads
    # to avoid heap exhaustion during one-shot migrations.
    cat > /tmp/pgloader-data-only.load << PGEOF
LOAD DATABASE
    FROM sqlite://$SQLITE_DB
    INTO postgresql://postgres@/$PGDATABASE

WITH data only,
     quote identifiers,
     workers = 1,
     concurrency = 1,
     prefetch rows = 1000,
     batch rows = 250,
     batch size = 1MB

SET work_mem to '64MB', maintenance_work_mem to '512MB'
;
PGEOF

    # pgloader's built-in reset sequences logic is flaky with pre-created
    # schemas, so repair owned sequences explicitly after the load completes.
    cat > /tmp/reset-postgres-sequences.sql <<'SQLEOF'
DO $$
DECLARE
    seq_record record;
    next_value bigint;
BEGIN
    FOR seq_record IN
        SELECT
            n.nspname AS schema_name,
            c.relname AS table_name,
            a.attname AS column_name,
            pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname) AS sequence_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid
        WHERE c.relkind = 'r'
          AND n.nspname = 'public'
          AND a.attnum > 0
          AND NOT a.attisdropped
          AND pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname) IS NOT NULL
    LOOP
        EXECUTE format(
            'SELECT COALESCE(MAX(%I), 0) + 1 FROM %I.%I',
            seq_record.column_name,
            seq_record.schema_name,
            seq_record.table_name
        )
        INTO next_value;

        EXECUTE format(
            'SELECT setval(%L, %s, false)',
            seq_record.sequence_name,
            next_value
        );
    END LOOP;
END $$;
SQLEOF

    echo "[migration] Running pgloader data-only migration..."
    pgloader /tmp/pgloader-data-only.load 2>&1 | tail -20
    PGLOADER_EXIT=${PIPESTATUS[0]}
    rm -f /tmp/pgloader-data-only.load

    if [ "$PGLOADER_EXIT" -eq 0 ]; then
        echo "[migration] Resetting PostgreSQL sequences after import..."
        if ! su - postgres -c "psql -d $PGDATABASE -v ON_ERROR_STOP=1 -f /tmp/reset-postgres-sequences.sql"; then
            rm -f /tmp/reset-postgres-sequences.sql
            echo "[migration] ERROR: PostgreSQL sequence reset failed after data migration."
            exit 1
        fi
        rm -f /tmp/reset-postgres-sequences.sql
        touch "$PGDATA/.migration_complete"
        echo "[migration] Data migration complete. SQLite database preserved at $SQLITE_DB"
    else
        rm -f /tmp/reset-postgres-sequences.sql
        echo "[migration] ERROR: Data migration failed (exit code: $PGLOADER_EXIT). Check pgloader output above."
        exit "$PGLOADER_EXIT"
    fi
fi

# Run the application as the specified user
# Use username (not UID:GID) so gosu picks up supplementary groups from /etc/group
# The app's MigrateAsync creates/updates the PostgreSQL schema on startup.
exec gosu "$USER_NAME" dotnet LancacheManager.dll "$@"
