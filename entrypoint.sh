#!/bin/bash

# Defense-in-depth (GitHub issue #25): ensure PostgreSQL server binaries are reachable even if the
# image PATH was not configured. Harmless on the slim image — the directory simply won't exist.
export PATH="${PATH}:/usr/lib/postgresql/17/bin"

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

# Explain WHY a directory rejects writes, using the actual owner/mode/mount rather than a
# canned "chown to PUID:PGID" that is wrong when the directory is already owned correctly.
# A dir owned by PUID:PGID that still rejects writes - even from root - is a mount-level
# restriction (read-only mount, NFS root_squash / UID mapping, a CIFS/SMB share whose
# credentials lack write, or Docker userns-remap), not an ownership problem. Chowning it
# would not help, so we do not tell the user to.
diagnose_write_denial() {
    local dir="$1"

    # Numeric owner/mode as the kernel sees them. On CIFS/NFS the displayed name (e.g.
    # "www-data") can be a mount-option alias, so compare numbers, not names.
    local owner_uid owner_gid mode
    owner_uid=$(stat -c '%u' "$dir" 2>/dev/null)
    owner_gid=$(stat -c '%g' "$dir" 2>/dev/null)
    mode=$(stat -c '%a' "$dir" 2>/dev/null)

    # Filesystem type and read-only flag of the mount backing this dir.
    local fstype="unknown" mount_ro=0
    if command -v findmnt >/dev/null 2>&1; then
        fstype=$(findmnt -n -o FSTYPE --target "$dir" 2>/dev/null || echo unknown)
        findmnt -n -o OPTIONS --target "$dir" 2>/dev/null | tr ',' '\n' | grep -qx ro && mount_ro=1
    fi

    # If root itself cannot write here, standard Unix ownership is not the gate.
    local root_can_write=0
    if touch "$dir/.root_write_test" 2>/dev/null; then
        rm -f "$dir/.root_write_test" 2>/dev/null
        root_can_write=1
    fi

    echo "WARNING: No write access to $dir as ${PUID}:${PGID}"
    echo "  Directory owner ${owner_uid:-?}:${owner_gid:-?}, mode ${mode:-?}, filesystem ${fstype:-unknown}"

    if [ "$mount_ro" -eq 1 ]; then
        echo "  Cause: the mount is read-only. Remove ':ro' from this volume, or make the export/share writable."
        return
    fi

    if [ "$owner_uid" = "$PUID" ] && [ "$root_can_write" -eq 0 ]; then
        echo "  Cause: the directory is ALREADY owned by ${PUID}:${PGID}, yet not even root can write to it."
        echo "  This is a mount-level restriction, not an ownership problem - chowning will NOT help."
        case "$fstype" in
            nfs|nfs4)
                echo "  NFS: the block is the server-side export, not the client. On the NFS server that"
                echo "       exports this path, run 'exportfs -v': the export must be 'rw' (not 'ro') and must"
                echo "       not 'all_squash', and the exported directory must be owned by ${PUID}:${PGID} on"
                echo "       the server. Add 'no_root_squash' too if the container's root setup must write."
                ;;
            cifs|smb3|smb2)
                echo "  CIFS/SMB: mount with credentials that can write, add the 'noperm' option, and ensure the"
                echo "       share ACL grants that account write access."
                ;;
            *)
                echo "  Check for: an NFS/CIFS/FUSE share denying writes, or Docker userns-remap mapping ${PUID}"
                echo "       to a different host UID. Confirm on the host with: touch <host_path_to_$dir>/.t"
                ;;
        esac
        return
    fi

    # Ownership genuinely differs - the classic PUID/PGID remedy applies.
    echo "  Cause: the directory is owned by ${owner_uid:-?}:${owner_gid:-?}, not ${PUID}:${PGID}."
    echo "  On the host: chown -R ${PUID}:${PGID} <host_path_to_$dir> && chmod -R 775 <host_path_to_$dir>"
    echo "  If on Unraid, also run: setfacl -Rb <host_path_to_$dir>"
}

# Write access diagnostics - warn if the app user cannot write to critical dirs.
for dir in /logs /cache; do
    if [ -d "$dir" ]; then
        if ! gosu "$USER_NAME" touch "$dir/.write_test" 2>/dev/null; then
            diagnose_write_denial "$dir"
        else
            rm -f "$dir/.write_test" 2>/dev/null
        fi
    fi
done

# Ensure rust binaries are executable
chmod +x /app/rust-processor/* 2>/dev/null || true

# ---------------------------------------------------------------------------
# PostgreSQL: mode, credentials, and the embedded server.
#
# Sourced rather than run, so it can use diagnose_write_denial() above, so the variables it
# resolves (POSTGRES_MODE, PGHOST, PGPASSWORD, PGDATABASE, SQLITE_DB, MIGRATION_MARKER) reach
# the migration step below, and so its `exit 1` paths stop the container instead of stopping
# only a subprocess and letting the app launch against a database that never started.
# ---------------------------------------------------------------------------
source /scripts/postgres-setup.sh

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

# Run the application as the specified user.
# The app's MigrateAsync creates/updates the PostgreSQL schema on startup.
if [ "$PUID" -eq 0 ]; then
    echo "PUID=0: running application without a privilege drop."
    exec dotnet LancacheManager.dll "$@"
fi

# CAP_KILL is bit 5 (0x20). Ambient capabilities must also be inheritable, so use
# setpriv only when the installed version supports ambient caps and CAP_KILL is bounded.
CAP_KILL_DROP_SUPPORTED=0
if command -v setpriv >/dev/null 2>&1 &&
    setpriv --help 2>&1 | grep -q -- '--ambient-caps'; then
    CAP_BND=$(awk '$1 == "CapBnd:" { print $2; exit }' /proc/self/status 2>/dev/null)
    if [[ "$CAP_BND" =~ ^[[:xdigit:]]+$ ]]; then
        CAP_BND_LOW_BYTE="${CAP_BND: -2}"
        if (( (16#$CAP_BND_LOW_BYTE & 0x20) != 0 )); then
            # Preflight the exact transition so an unsupported runtime falls back safely.
            if setpriv --reuid "$PUID" --regid "$PGID" --init-groups \
                --inh-caps=+kill --ambient-caps=+kill /bin/true >/dev/null 2>&1; then
                CAP_KILL_DROP_SUPPORTED=1
            fi
        fi
    fi
fi

if [ "$CAP_KILL_DROP_SUPPORTED" -eq 1 ]; then
    echo "Privilege drop: preserving CAP_KILL for host nginx signaling."
    exec setpriv --reuid "$PUID" --regid "$PGID" --init-groups \
        --inh-caps=+kill --ambient-caps=+kill \
        dotnet LancacheManager.dll "$@"
fi

# Use username (not UID:GID) so gosu picks up supplementary groups from /etc/group.
echo "Privilege drop: standard privilege drop."
exec gosu "$USER_NAME" dotnet LancacheManager.dll "$@"
