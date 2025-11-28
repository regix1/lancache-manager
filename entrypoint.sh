#!/bin/bash

# PUID/PGID support for lancache-manager
# Similar to linuxserver.io images

PUID=${PUID:-1000}
PGID=${PGID:-1000}

echo "
───────────────────────────────────────
LanCache Manager
───────────────────────────────────────
User UID: $PUID
User GID: $PGID
───────────────────────────────────────
"

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

echo "Running as user: $USER_NAME ($PUID) / group: $GROUP_NAME ($PGID)"

# Change ownership of application directories
# /data needs write access for database and progress files
# /app needs read access for the application
chown -R "$PUID:$PGID" /data /app/rust-processor 2>/dev/null || true

# Ensure rust binaries are executable
chmod +x /app/rust-processor/* 2>/dev/null || true

# Run the application as the specified user
exec gosu "$PUID:$PGID" dotnet LancacheManager.dll "$@"
