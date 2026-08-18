#!/bin/bash

# Resolves the Postgres mode and credentials, and starts the embedded server when there is one.
#
# SOURCED BY entrypoint.sh, NOT EXECUTED. That is load-bearing in three ways, so keep the
# `source` at the call site if this file is ever moved or renamed:
#   1. Everything after the call site reads POSTGRES_MODE, PGHOST, PGPASSWORD, PGDATABASE,
#      which are set here. A subprocess would drop all of them
#      on exit and the migration step would then read empty values and skip itself.
#   2. diagnose_write_denial() is defined in entrypoint.sh and called here for an unwritable
#      /data. A subprocess would not have that function.
#   3. The `exit 1` failure paths below are meant to stop the container. From a subprocess they
#      would only end the subprocess and the entrypoint would carry on and launch the app
#      against a database that never started.

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    echo "postgres-setup.sh must be sourced by entrypoint.sh, not run on its own." >&2
    echo "  Run the container normally; this script sets variables its caller needs." >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Constants used by both embedded and external modes
# ---------------------------------------------------------------------------
PGDATABASE="${POSTGRES_DB:-lancache}"
PG_CONFIG="/data/config/postgres-credentials.json"

# ---------------------------------------------------------------------------
# Mode selection: embedded (default) starts the in-container Postgres;
# external skips it and connects to a user-managed Postgres. Resolved before the
# credentials are read, because host/port/database only apply to external mode.
#
# Slim image variant has no embedded PostgreSQL - detect that and force external
# mode so we fail loudly instead of trying to exec a missing pg_ctl binary.
# ---------------------------------------------------------------------------
POSTGRES_MODE_AS_GIVEN="${POSTGRES_MODE:-embedded}"

# The .NET app and all ten Rust binaries compare this value against "external" verbatim, so a
# compose file saved with CRLF line endings, or a stray space, silently puts one half of the app
# on the embedded socket and the other on a TCP server. Normalise here, before the export below,
# so every reader downstream sees the same token.
POSTGRES_MODE=$(printf '%s' "$POSTGRES_MODE_AS_GIVEN" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')

if [ "$POSTGRES_MODE" != "embedded" ] && [ "$POSTGRES_MODE" != "external" ]; then
    echo "WARNING: POSTGRES_MODE is '$POSTGRES_MODE_AS_GIVEN', which is neither 'embedded' nor 'external'."
    echo "  Using the embedded server. Set POSTGRES_MODE=external to connect to a server you run yourself."
    POSTGRES_MODE="embedded"
fi

if [ "$POSTGRES_MODE" = "embedded" ] && ! ls /usr/lib/postgresql/*/bin/pg_ctl >/dev/null 2>&1; then
    echo "[postgres] Slim image detected: no embedded PostgreSQL binary in this image."
    echo "[postgres] Forcing POSTGRES_MODE=external. For embedded mode, use the full image tag"
    echo "[postgres] (e.g. :latest or :dev) instead of the :slim variant."
    POSTGRES_MODE="external"
fi

export POSTGRES_MODE

# ---------------------------------------------------------------------------
# Credential sourcing (env var > config file > defaults)
# Reads username/password for both modes; host/port/database for external mode.
#
# Forgotten or wrong embedded password: delete $PG_CONFIG and restart. The setup page
# comes back, and submitting a password rewrites the file and runs ALTER USER without
# touching a single row. The embedded server never checks that password anyway - initdb
# runs with --auth-local=trust and the pg_hba.conf written below is 'local all all trust'
# on a socket-only server - so nothing here can put stored data out of reach.
# In external mode the same file is the only record of the host, port, database and
# username, so read it before deleting it.
# ---------------------------------------------------------------------------
PGUSER="${POSTGRES_USER:-lancache}"
PGPASSWORD="${POSTGRES_PASSWORD:-}"
PGHOST="${POSTGRES_HOST:-}"
PGPORT="${POSTGRES_PORT:-5432}"

# The embedded server listens on a Unix socket only, so a host is an external-mode setting.
# Carrying one into embedded mode is what lets the .NET app use the socket while the Rust
# binaries read and write somebody else's server over TCP.
if [ "$POSTGRES_MODE" != "external" ] && [ -n "$PGHOST" ]; then
    echo "WARNING: POSTGRES_HOST=$PGHOST is set but POSTGRES_MODE is embedded, so it is ignored."
    echo "  Set POSTGRES_MODE=external to use that server instead."
    PGHOST=""
fi

if [ -f "$PG_CONFIG" ]; then
    PG_CONFIG_PARSED=1
    if command -v jq &>/dev/null; then
        # Preferred: use jq for reliable JSON parsing
        if ! jq -e . "$PG_CONFIG" >/dev/null 2>&1; then
            echo "WARNING: $PG_CONFIG is not valid JSON, so the credentials in it are being ignored."
            echo "  Delete the file and restart to enter them again on the setup page."
            PG_CONFIG_PARSED=0
        fi
        PGPASSWORD_FROM_CONFIG=$(jq -r '.password // empty' "$PG_CONFIG" 2>/dev/null)
        PGUSER_FROM_CONFIG=$(jq -r '.username // empty' "$PG_CONFIG" 2>/dev/null)
        [ "$POSTGRES_MODE" = "external" ] && [ -z "$PGHOST" ] && PGHOST=$(jq -r '.host // empty' "$PG_CONFIG" 2>/dev/null)
        PGPORT_FROM_CONFIG=$(jq -r '.port // empty' "$PG_CONFIG" 2>/dev/null)
        PGDB_FROM_CONFIG=$(jq -r '.database // empty' "$PG_CONFIG" 2>/dev/null)
    else
        # Fallback: regex extraction
        PGPASSWORD_FROM_CONFIG=$(sed -n 's/.*"password"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
        PGUSER_FROM_CONFIG=$(sed -n 's/.*"username"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
        [ "$POSTGRES_MODE" = "external" ] && [ -z "$PGHOST" ] && PGHOST=$(sed -n 's/.*"host"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
        PGPORT_FROM_CONFIG=$(sed -n 's/.*"port"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' "$PG_CONFIG" | head -n1)
        PGDB_FROM_CONFIG=$(sed -n 's/.*"database"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PG_CONFIG" | head -n1)
    fi
    # Every writer of this file stores a username and a password together, so either one
    # missing means the file is truncated or was hand-edited. Line matching cannot report
    # that on its own, and the jq check above only covers JSON that does not parse at all.
    if [ "$PG_CONFIG_PARSED" -eq 1 ] && { [ -z "$PGUSER_FROM_CONFIG" ] || [ -z "$PGPASSWORD_FROM_CONFIG" ]; }; then
        echo "WARNING: $PG_CONFIG has no readable username or password, so it is incomplete."
        echo "  Delete the file and restart to enter them again on the setup page."
    fi
    [ -z "$PGPASSWORD" ] && PGPASSWORD="$PGPASSWORD_FROM_CONFIG"
    # Same order as the password above, and as the .NET and Rust readers: the env var wins.
    # Letting the file's username through anyway pairs it with an env password that was issued
    # for a different role, and exports a username the operator did not ask for.
    [ -z "$POSTGRES_USER" ] && PGUSER="${PGUSER_FROM_CONFIG:-$PGUSER}"
    [ -n "$PGPORT_FROM_CONFIG" ] && [ -z "$POSTGRES_PORT" ] && PGPORT="$PGPORT_FROM_CONFIG"
    [ -n "$PGDB_FROM_CONFIG" ] && [ -z "$POSTGRES_DB" ] && PGDATABASE="$PGDB_FROM_CONFIG"
fi

# Export for the .NET app and child processes
export POSTGRES_USER="$PGUSER"
export POSTGRES_PASSWORD="$PGPASSWORD"
export POSTGRES_PORT="$PGPORT"
export POSTGRES_DB="$PGDATABASE"

# An empty POSTGRES_HOST is not the same as an unset one: a child that reads it sees a
# configured value and tries to connect to it.
if [ -n "$PGHOST" ]; then
    export POSTGRES_HOST="$PGHOST"
else
    unset POSTGRES_HOST
fi

# Mode dispatch
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
    # Fallback: /var/lib/postgresql/data. The image does not declare this path a VOLUME, so
    # unless the user mounted something there themselves it is the container's writable layer
    # and 'docker compose down' or any recreate takes the database with it.
    PGDATA_PRIMARY="/data/postgresql"
    PGDATA_FALLBACK="/var/lib/postgresql/data"

    # What keeps the fallback path alive is sitting on any mount other than the container's own
    # root filesystem, not being a mount point itself: 'postgres_data:/var/lib/postgresql' keeps
    # the database exactly as well as a mount on /var/lib/postgresql/data. Comparing device
    # numbers answers that question; an equality test against the mount point called the first
    # layout unmounted and told the user their database was about to be deleted when it was not.
    # The path may not exist yet on a fresh container, so probe the nearest parent that does.
    PGDATA_FALLBACK_MOUNTED=0
    if command -v stat >/dev/null 2>&1; then
        PGDATA_FALLBACK_PROBE="$PGDATA_FALLBACK"
        while [ ! -d "$PGDATA_FALLBACK_PROBE" ] && [ "$PGDATA_FALLBACK_PROBE" != "/" ]; do
            PGDATA_FALLBACK_PROBE=$(dirname "$PGDATA_FALLBACK_PROBE")
        done
        PGDATA_FALLBACK_DEVICE=$(stat -c %d "$PGDATA_FALLBACK_PROBE" 2>/dev/null || echo "")
        ROOT_DEVICE=$(stat -c %d / 2>/dev/null || echo "")
        if [ -n "$PGDATA_FALLBACK_DEVICE" ] && [ -n "$ROOT_DEVICE" ] &&
            [ "$PGDATA_FALLBACK_DEVICE" != "$ROOT_DEVICE" ]; then
            PGDATA_FALLBACK_MOUNTED=1
        fi
    fi

    if [ -f "$PGDATA_PRIMARY/PG_VERSION" ]; then
        # Already initialized at primary location
        PGDATA="$PGDATA_PRIMARY"
    elif [ -f "$PGDATA_FALLBACK/PG_VERSION" ]; then
        # Existing install with data at fallback location - don't break it
        PGDATA="$PGDATA_FALLBACK"
        echo "[postgres] Using existing data at $PGDATA"
    elif [ -d "/data" ] && touch "/data/.pgcheck" 2>/dev/null; then
        # Fresh install - /data is writable, use primary location
        rm -f "/data/.pgcheck"
        PGDATA="$PGDATA_PRIMARY"
    else
        # /data not writable - use fallback
        PGDATA="$PGDATA_FALLBACK"
        echo "[postgres] /data not writable, using fallback: $PGDATA"
        if [ -d "/data" ]; then
            diagnose_write_denial "/data"
        else
            echo "WARNING: /data does not exist in the container, so nothing is mounted there."
        fi
    fi

    # Covers both routes onto the fallback path, the pre-existing cluster and the fresh one.
    if [ "$PGDATA" = "$PGDATA_FALLBACK" ] && [ "$PGDATA_FALLBACK_MOUNTED" -eq 0 ]; then
        echo "WARNING: The database at $PGDATA is not on a mounted volume."
        echo "  It lives in the container's writable layer, so 'docker compose down', 'docker rm'"
        echo "  or any recreate of this container DELETES it permanently."
        echo "  Fix the /data mount so the database lands in $PGDATA_PRIMARY, or mount a volume"
        echo "  at $PGDATA to keep what is already there."
    fi

    PG_LOG="/var/log/postgresql.log"

    # Initialize PostgreSQL data directory on first run
    if [ ! -f "$PGDATA/PG_VERSION" ]; then
        # A brand new empty cluster and a database the container can no longer see produce the
        # same empty dashboard, so name every place a cluster could still be before creating one.
        if [ "$PGDATA" = "$PGDATA_PRIMARY" ]; then
            PGDATA_OTHER="$PGDATA_FALLBACK"
        else
            PGDATA_OTHER="$PGDATA_PRIMARY"
        fi
        if [ -f "$PGDATA_OTHER/PG_VERSION" ]; then
            echo "WARNING: A PostgreSQL cluster already exists at $PGDATA_OTHER."
            echo "  A new empty one is about to be created at $PGDATA and the app will look like a"
            echo "  clean install. Stop the container now if that other cluster holds your data."
        elif [ -d "$PGDATA_OTHER" ] && [ -n "$(ls -A "$PGDATA_OTHER" 2>/dev/null)" ]; then
            echo "WARNING: $PGDATA_OTHER holds files but no PG_VERSION, so it is not a usable cluster."
            echo "  A new empty database is being created at $PGDATA instead."
        fi
        echo "[postgres] No database found at $PGDATA. Creating an empty one."
        echo "[postgres] If this container had data before, it is probably still on the host: check"
        echo "[postgres] that the path behind /data is the one you used previously. A renamed volume,"
        echo "[postgres] an edited bind path or a compose file run from another directory all make an"
        echo "[postgres] intact database look like a fresh install."
        echo "[postgres] Initializing data directory..."
        mkdir -p "$PGDATA"
        chown -R postgres:postgres "$PGDATA"
        su - postgres -c "/usr/lib/postgresql/17/bin/initdb -D $PGDATA --auth-local=trust --auth-host=trust" \
            || { echo "[postgres] ERROR: initdb could not create the data directory at $PGDATA."; cat "$PG_LOG" 2>/dev/null; exit 1; }

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
    su - postgres -c "/usr/lib/postgresql/17/bin/pg_ctl -D $PGDATA -l $PG_LOG start" \
        || { echo "[postgres] ERROR: pg_ctl could not start the server."; cat "$PG_LOG" 2>/dev/null; exit 1; }

    # Wait until PostgreSQL is ready (pg_isready, max 30 s)
    echo "[postgres] Waiting for PostgreSQL to be ready..."
    timeout 30 bash -c "until su - postgres -c 'pg_isready -q' 2>/dev/null; do sleep 1; done" \
        || { echo "[postgres] ERROR: PostgreSQL did not become ready in time"; cat "$PG_LOG" 2>/dev/null; exit 1; }
    echo "[postgres] PostgreSQL is ready."

    # The SQL travels on stdin and never inside the command string su executes, so a role name,
    # database name or password holding $(...), a backtick or a quote cannot be re-parsed as a
    # command by the shell su starts. These names come from postgres-credentials.json, which the
    # setup endpoint writes, so they are not all under the operator's control.
    run_postgres_sql() {
        printf '%s\n' "$1" | su - postgres -c 'psql -v ON_ERROR_STOP=1 -qtA -f -'
    }

    # A SQL identifier is double-quoted with any embedded double quote doubled. Without this a
    # role named with a hyphen is a syntax error and the statement silently does not run.
    sql_identifier() {
        printf '"%s"' "$(printf '%s' "$1" | sed 's/"/""/g')"
    }

    # A SQL string literal is single-quoted with any embedded single quote doubled.
    # standard_conforming_strings is on, so a backslash is an ordinary character here.
    sql_literal() {
        printf "'%s'" "$(printf '%s' "$1" | sed "s/'/''/g")"
    }

    PGUSER_IDENTIFIER=$(sql_identifier "$PGUSER")
    PGUSER_LITERAL=$(sql_literal "$PGUSER")
    PGDATABASE_IDENTIFIER=$(sql_identifier "$PGDATABASE")
    PGDATABASE_LITERAL=$(sql_literal "$PGDATABASE")

    # Create/update PostgreSQL role with credentials
    if [ "$(run_postgres_sql "SELECT 1 FROM pg_roles WHERE rolname = $PGUSER_LITERAL;")" = "1" ]; then
        PGUSER_EXISTS=1
    else
        PGUSER_EXISTS=0
    fi

    if [ -n "$PGPASSWORD" ]; then
        PGPASSWORD_LITERAL=$(sql_literal "$PGPASSWORD")
        if [ "$PGUSER_EXISTS" -eq 1 ]; then
            # Update password if user already exists (in case password changed)
            run_postgres_sql "ALTER USER $PGUSER_IDENTIFIER WITH PASSWORD $PGPASSWORD_LITERAL;"
        else
            run_postgres_sql "CREATE USER $PGUSER_IDENTIFIER WITH SUPERUSER PASSWORD $PGPASSWORD_LITERAL;"
        fi
    else
        # No password yet - create user without password (local trust auth)
        # App will show first-run setup page to collect credentials
        if [ "$PGUSER_EXISTS" -eq 0 ]; then
            run_postgres_sql "CREATE USER $PGUSER_IDENTIFIER WITH SUPERUSER;"
        fi
        echo "WARNING: No POSTGRES_PASSWORD set. The app will prompt for credentials on first access."
    fi

    # WITH SUPERUSER only appears on the CREATE branches above, so a role that already existed
    # without it fails the first schema update with "must be owner of table" and the container
    # exits. Put the grant back rather than leaving that to be guessed from the error.
    PGUSER_IS_SUPERUSER=$(run_postgres_sql "SELECT rolsuper FROM pg_roles WHERE rolname = $PGUSER_LITERAL;" 2>/dev/null)
    if [ "$PGUSER_IS_SUPERUSER" = "f" ]; then
        echo "[postgres] Role '$PGUSER' exists without SUPERUSER. Granting it back."
        run_postgres_sql "ALTER ROLE $PGUSER_IDENTIFIER WITH SUPERUSER;" \
            || echo "WARNING: Could not grant SUPERUSER to role '$PGUSER'. Schema updates will fail with 'must be owner of table'."
    fi

    # Create database if it doesn't exist
    if [ "$(run_postgres_sql "SELECT 1 FROM pg_database WHERE datname = $PGDATABASE_LITERAL;" 2>/dev/null)" != "1" ]; then
        # A changed POSTGRES_DB leaves the old database full of rows with nothing pointing at it,
        # and the fresh schema in the new one reads as a clean install. Name both.
        OTHER_DATABASES=$(run_postgres_sql "SELECT datname FROM pg_database WHERE datistemplate = false AND datname <> 'postgres';" 2>/dev/null \
            | grep -vFx -- "$PGDATABASE" | tr '\n' ' ')
        if [ -n "$OTHER_DATABASES" ]; then
            echo "WARNING: Creating a new empty database '$PGDATABASE' while this server already holds: $OTHER_DATABASES"
            echo "  Those keep every row, and nothing points at them once '$PGDATABASE' is in use."
            echo "  Set POSTGRES_DB back to the earlier name to reach that data again."
        fi
        run_postgres_sql "CREATE DATABASE $PGDATABASE_IDENTIFIER OWNER $PGUSER_IDENTIFIER;"
    fi
fi
