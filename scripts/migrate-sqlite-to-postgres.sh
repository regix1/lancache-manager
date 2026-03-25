#!/bin/bash
# ---------------------------------------------------------------------------
# SQLite → PostgreSQL data migration
# ---------------------------------------------------------------------------
# Migrates data table-by-table using: sqlite3 CSV export → psql COPY FROM STDIN
# No pgloader/SBCL dependency — just sqlite3 + psql.
#
# PostgreSQL boolean input natively accepts 0/1 from SQLite.
# sqlite3 CSV outputs NULL as unquoted empty, "" as empty string —
# COPY WITH (NULL '') maps these correctly.
#
# Usage: migrate-sqlite-to-postgres.sh <sqlite_db_path> <pg_database> <pg_data_dir>
# ---------------------------------------------------------------------------
set -eo pipefail

SQLITE_DB="$1"
PGDATABASE="$2"
PGDATA="$3"

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
# Phase 1: Tune PostgreSQL for bulk loading
# ---------------------------------------------------------------------------
# Safe because the SQLite source file is preserved — if migration fails we redo it.
echo "[migration] Tuning PostgreSQL for bulk import..."
su - postgres -c "psql -d $PGDATABASE" <<'TUNEEOF'
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

# ---------------------------------------------------------------------------
# Phase 2: Drop non-PK/unique indexes for faster inserts
# ---------------------------------------------------------------------------
echo "[migration] Dropping non-PK indexes for faster import..."
su - postgres -c "psql -d $PGDATABASE" <<'IDXEOF'
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

# Get user tables from SQLite (skip internal tables and EF migration history —
# EF Core already wrote the correct PostgreSQL migration entry during schema creation)
SQLITE_TABLES=$(sqlite3 "$SQLITE_DB" \
    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory' ORDER BY name;")

# Disable triggers (including FK enforcement) so tables can load in any order
for TABLE in $SQLITE_TABLES; do
    su - postgres -c "psql -q -d $PGDATABASE -c 'ALTER TABLE \"$TABLE\" DISABLE TRIGGER ALL;'" 2>/dev/null
done

MIGRATION_FAILED=0
for TABLE in $SQLITE_TABLES; do
    ROW_COUNT=$(sqlite3 "$SQLITE_DB" "SELECT COUNT(*) FROM \"$TABLE\";")
    if [ "$ROW_COUNT" -eq 0 ]; then
        echo "[migration]   $TABLE: empty, skipping"
        continue
    fi

    # Build quoted column list from SQLite schema to guarantee column order
    QUOTED_COLUMNS=$(sqlite3 "$SQLITE_DB" "PRAGMA table_info('$TABLE');" \
        | cut -d'|' -f2 | sed 's/.*/"&"/' | paste -sd',')

    echo "[migration]   $TABLE: $ROW_COUNT rows..."

    # Stream: COPY command → CSV data → end-of-data marker, all into one psql session
    if ! { echo "COPY \"$TABLE\" ($QUOTED_COLUMNS) FROM STDIN WITH (FORMAT csv, NULL '');"; \
           sqlite3 -csv "$SQLITE_DB" "SELECT * FROM \"$TABLE\";"; \
           echo '\.' ; } | su - postgres -c "psql -q -d $PGDATABASE" 2>&1; then
        echo "[migration] ERROR: Failed to migrate table $TABLE"
        MIGRATION_FAILED=1
        break
    fi
done

# Re-enable triggers
for TABLE in $SQLITE_TABLES; do
    su - postgres -c "psql -q -d $PGDATABASE -c 'ALTER TABLE \"$TABLE\" ENABLE TRIGGER ALL;'" 2>/dev/null
done

# ---------------------------------------------------------------------------
# Phase 4: Post-migration cleanup
# ---------------------------------------------------------------------------
if [ "$MIGRATION_FAILED" -eq 0 ]; then
    # Recreate indexes that were dropped before the bulk load
    echo "[migration] Recreating indexes..."
    su - postgres -c "psql -d $PGDATABASE" <<'IDXREOF'
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

    # Repair auto-increment sequences — COPY does not advance them
    echo "[migration] Resetting PostgreSQL sequences..."
    su - postgres -c "psql -d $PGDATABASE -v ON_ERROR_STOP=1" <<'SEQEOF'
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

    # Restore safe PostgreSQL settings after bulk load
    echo "[migration] Restoring safe PostgreSQL settings..."
    su - postgres -c "psql" <<'RESTOREEOF'
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
    su - postgres -c "psql -d $PGDATABASE -c 'ANALYZE;'"

    touch "$PGDATA/.migration_complete"
    echo "[migration] Data migration complete. SQLite database preserved at $SQLITE_DB"
else
    echo "[migration] ERROR: Data migration failed. Check output above."
    exit 1
fi
