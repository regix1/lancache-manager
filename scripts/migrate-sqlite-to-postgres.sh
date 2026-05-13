#!/bin/bash
# ---------------------------------------------------------------------------
# SQLite -> PostgreSQL data migration
# ---------------------------------------------------------------------------
# Migrates data table-by-table using: sqlite3 CSV export -> psql COPY FROM STDIN
# No pgloader/SBCL dependency - just sqlite3 + psql.
#
# Works against either embedded (Unix socket, peer auth) or external Postgres
# (TCP with password). Mode is selected by POSTGRES_MODE env var.
#
# PostgreSQL boolean input natively accepts 0/1 from SQLite.
# sqlite3 CSV outputs NULL as unquoted empty, "" as empty string -
# COPY WITH (NULL '') maps these correctly.
#
# Usage: migrate-sqlite-to-postgres.sh <sqlite_db_path> <pg_database>
# Env:   POSTGRES_MODE        embedded (default) | external
#        POSTGRES_HOST        external mode only
#        POSTGRES_PORT        external mode only (default 5432)
#        POSTGRES_USER        external mode only
#        POSTGRES_PASSWORD    external mode only
# ---------------------------------------------------------------------------
set -eo pipefail

SQLITE_DB="$1"
PGDATABASE="$2"
MIGRATION_MARKER="/data/postgres-migration.complete"

# Validate prerequisites
if ! command -v sqlite3 &>/dev/null; then
    echo "[migration] ERROR: sqlite3 is not installed."
    exit 1
fi

if [ ! -f "$SQLITE_DB" ]; then
    echo "[migration] ERROR: SQLite database not found at $SQLITE_DB"
    exit 1
fi

# Verify sqlite3 can read the database
TABLE_COUNT=$(sqlite3 "$SQLITE_DB" "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory';" 2>&1) || {
    echo "[migration] ERROR: sqlite3 cannot read database: $TABLE_COUNT"
    exit 1
}
echo "[migration] Found $TABLE_COUNT tables to migrate in SQLite database."

# ---------------------------------------------------------------------------
# Mode-aware psql wrapper
# ---------------------------------------------------------------------------
# Embedded: connect as postgres OS user via Unix socket (peer auth).
# External: connect over TCP using credentials from environment.
#
# We use runuser (util-linux) for the embedded path because it preserves argv
# verbatim - unlike `su -c "..."` which flattens everything into one shell
# string and silently mangles quoted SQL passed as `-c "SELECT ..."`.
POSTGRES_MODE="${POSTGRES_MODE:-embedded}"

run_psql() {
    if [ "$POSTGRES_MODE" = "external" ]; then
        PGPASSWORD="$POSTGRES_PASSWORD" psql \
            -h "$POSTGRES_HOST" -p "${POSTGRES_PORT:-5432}" \
            -U "$POSTGRES_USER" -d "$PGDATABASE" "$@"
    else
        runuser -u postgres -- psql -d "$PGDATABASE" "$@"
    fi
}

# Variant that pipes the COPY-with-CSV stream through psql as a single session.
# Needed because the COPY data follows the SQL command on stdin.
copy_stream_psql() {
    if [ "$POSTGRES_MODE" = "external" ]; then
        PGPASSWORD="$POSTGRES_PASSWORD" psql \
            -h "$POSTGRES_HOST" -p "${POSTGRES_PORT:-5432}" \
            -U "$POSTGRES_USER" -d "$PGDATABASE"
    else
        runuser -u postgres -- psql -d "$PGDATABASE"
    fi
}

# ---------------------------------------------------------------------------
# Phase 1: Tune PostgreSQL for bulk loading (best effort)
# ---------------------------------------------------------------------------
# Safe because the SQLite source file is preserved - if migration fails we redo it.
# ALTER SYSTEM requires superuser and is forbidden on most managed services
# (RDS, Azure Database for PostgreSQL, Cloud SQL). Treat failures as warnings:
# the migration still works, just slower.
echo "[migration] Tuning PostgreSQL for bulk import (best-effort)..."
if ! run_psql <<'TUNEEOF' 2>&1; then
-- WAL / fsync: skip durability guarantees during one-shot import
ALTER SYSTEM SET synchronous_commit = 'off';
ALTER SYSTEM SET fsync = 'off';
ALTER SYSTEM SET full_page_writes = 'off';
ALTER SYSTEM SET wal_level = 'minimal';
ALTER SYSTEM SET max_wal_senders = 0;
ALTER SYSTEM SET max_wal_size = '1GB';
ALTER SYSTEM SET checkpoint_completion_target = 0.9;
-- Memory: give the import plenty of room
ALTER SYSTEM SET work_mem = '64MB';
ALTER SYSTEM SET maintenance_work_mem = '512MB';
-- Disable autovacuum during bulk load
ALTER SYSTEM SET autovacuum = 'off';
SELECT pg_reload_conf();
TUNEEOF
    echo "[migration] WARNING: Could not apply ALTER SYSTEM tuning (likely a managed DB). Continuing without it."
fi

# ---------------------------------------------------------------------------
# Phase 2: Drop non-PK/unique indexes for faster inserts
# ---------------------------------------------------------------------------
echo "[migration] Dropping non-PK indexes for faster import..."
run_psql <<'IDXEOF'
CREATE TABLE IF NOT EXISTS _migration_saved_indexes (
    indexname text PRIMARY KEY,
    indexdef  text NOT NULL
);
INSERT INTO _migration_saved_indexes (indexname, indexdef)
SELECT i.indexname, i.indexdef
FROM pg_indexes i
WHERE i.schemaname = 'public'
  AND NOT EXISTS (
      SELECT 1
      FROM pg_constraint c
      JOIN pg_class idx ON idx.oid = c.conindid
      WHERE c.contype IN ('p', 'u')
        AND idx.relname = i.indexname
  )
ON CONFLICT DO NOTHING;

DO $$
DECLARE r record;
BEGIN
    FOR r IN
        SELECT indexname FROM _migration_saved_indexes
    LOOP
        EXECUTE format('DROP INDEX IF EXISTS public.%I', r.indexname);
    END LOOP;
END $$;
IDXEOF

# ---------------------------------------------------------------------------
# Phase 3: Copy data table-by-table
# ---------------------------------------------------------------------------
echo "[migration] Starting table-by-table data migration..."

# Get tables that exist in BOTH SQLite and PostgreSQL (skip internal tables,
# EF migration history - EF Core already wrote the correct entry during schema creation)
SQLITE_TABLES=$(sqlite3 "$SQLITE_DB" \
    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory' ORDER BY name;")

PG_TABLES=$(run_psql -tA -c "SELECT tablename FROM pg_tables WHERE schemaname='public';")

# Only migrate tables that exist on both sides
TABLES_TO_MIGRATE=""
for TABLE in $SQLITE_TABLES; do
    if echo "$PG_TABLES" | grep -qx "$TABLE"; then
        TABLES_TO_MIGRATE="$TABLES_TO_MIGRATE $TABLE"
    else
        echo "[migration]   $TABLE: not in PostgreSQL schema, skipping"
    fi
done

# Disable triggers (including FK enforcement) so tables can load in any order
for TABLE in $TABLES_TO_MIGRATE; do
    run_psql -q -c "ALTER TABLE \"$TABLE\" DISABLE TRIGGER ALL;" || true
done

MIGRATION_FAILED=0
for TABLE in $TABLES_TO_MIGRATE; do
    ROW_COUNT=$(sqlite3 "$SQLITE_DB" "SELECT COUNT(*) FROM \"$TABLE\";")
    if [ "$ROW_COUNT" -eq 0 ]; then
        echo "[migration]   $TABLE: empty, skipping"
        continue
    fi

    # Build quoted column list from SQLite schema to guarantee column order
    QUOTED_COLUMNS=$(sqlite3 "$SQLITE_DB" "PRAGMA table_info('$TABLE');" \
        | cut -d'|' -f2 | sed 's/.*/"&"/' | paste -sd',')

    echo "[migration]   $TABLE: $ROW_COUNT rows..."

    # Stream: COPY command -> CSV data -> end-of-data marker, all into one psql session
    if ! { echo "COPY \"$TABLE\" ($QUOTED_COLUMNS) FROM STDIN WITH (FORMAT csv, NULL '');"; \
           sqlite3 -csv "$SQLITE_DB" "SELECT * FROM \"$TABLE\";"; \
           echo '\.' ; } | copy_stream_psql 2>&1; then
        echo "[migration] ERROR: Failed to migrate table $TABLE"
        MIGRATION_FAILED=1
        break
    fi
done

# Re-enable triggers
for TABLE in $TABLES_TO_MIGRATE; do
    run_psql -q -c "ALTER TABLE \"$TABLE\" ENABLE TRIGGER ALL;" || true
done

# ---------------------------------------------------------------------------
# Phase 4: Post-migration cleanup
# ---------------------------------------------------------------------------
if [ "$MIGRATION_FAILED" -eq 0 ]; then
    # Recreate indexes that were dropped before the bulk load
    echo "[migration] Recreating indexes..."
    run_psql <<'IDXREOF'
DO $$
DECLARE r record;
BEGIN
    FOR r IN
        SELECT indexdef FROM _migration_saved_indexes
    LOOP
        EXECUTE r.indexdef;
    END LOOP;
END $$;
DROP TABLE IF EXISTS _migration_saved_indexes;
IDXREOF

    # Repair auto-increment sequences - COPY does not advance them
    echo "[migration] Resetting PostgreSQL sequences..."
    run_psql -v ON_ERROR_STOP=1 <<'SEQEOF'
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
SEQEOF

    # Restore safe PostgreSQL settings after bulk load (best-effort, same caveat as tuning)
    echo "[migration] Restoring safe PostgreSQL settings (best-effort)..."
    run_psql <<'RESTOREEOF' 2>&1 || echo "[migration] WARNING: Could not restore ALTER SYSTEM defaults (likely a managed DB)."
ALTER SYSTEM RESET synchronous_commit;
ALTER SYSTEM RESET fsync;
ALTER SYSTEM RESET full_page_writes;
ALTER SYSTEM RESET wal_level;
ALTER SYSTEM RESET max_wal_senders;
ALTER SYSTEM RESET max_wal_size;
ALTER SYSTEM RESET work_mem;
ALTER SYSTEM RESET maintenance_work_mem;
ALTER SYSTEM RESET autovacuum;
SELECT pg_reload_conf();
RESTOREEOF

    # Run ANALYZE so the planner has accurate stats after bulk import
    echo "[migration] Running ANALYZE..."
    run_psql -c "ANALYZE;"

    # Write marker file at a mode-agnostic location
    mkdir -p "$(dirname "$MIGRATION_MARKER")"
    touch "$MIGRATION_MARKER"
    echo "[migration] Data migration complete. SQLite database preserved at $SQLITE_DB"
else
    echo "[migration] ERROR: Data migration failed. Check output above."
    exit 1
fi
