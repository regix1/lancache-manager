# Multi-stage build for Lancache Manager with Rust processors
# Supports: linux/amd64, linux/arm64

ARG VERSION=1.2.0

# Stage 1: Build Rust binaries
# Using full rust image (not slim) to avoid installing gcc/g++ which causes timeouts
# Rust 1.85+ required for edition2024 support; using latest stable for dependency MSRV headroom
FROM rust:1.94 AS rust-builder

ARG TARGETPLATFORM

WORKDIR /build/rust-processor

# Copy only dependency files first for better caching
COPY rust-processor/Cargo.toml rust-processor/Cargo.lock* ./

# Create dummy src files for all binaries to build dependencies (cache layer)
RUN mkdir src && \
    echo "fn main() {}" > src/log_processor.rs && \
    echo "fn main() {}" > src/speed_tracker.rs && \
    echo "fn main() {}" > src/log_service_manager.rs && \
    echo "fn main() {}" > src/cache_size.rs && \
    echo "fn main() {}" > src/cache_clear.rs && \
    echo "fn main() {}" > src/cache_corruption.rs && \
    echo "fn main() {}" > src/cache_game_detect.rs && \
    echo "fn main() {}" > src/cache_game_remove.rs && \
    echo "fn main() {}" > src/cache_epic_remove.rs && \
    echo "fn main() {}" > src/cache_service_remove.rs && \
    echo "fn main() {}" > src/cache_eviction_scan.rs && \
    echo "fn main() {}" > src/cache_purge_log_entries.rs && \
    echo "fn main() {}" > src/db_reset.rs && \
    cargo build --release && \
    rm -rf src target/release/deps/lancache* target/release/lancache* target/release/.fingerprint/lancache*

# Now copy real source and build (only this layer rebuilds on code changes)
COPY rust-processor/src ./src
# tact_products.json is embedded at compile time via include_str!("../tact_products.json") in tact_products.rs
COPY rust-processor/tact_products.json ./tact_products.json

# Build for native platform
# Binary naming: log_* (log ops), cache_* (cache ops), db_* (database ops)
RUN cargo build --release && \
    mkdir -p /build/output && \
    cp target/release/log_processor /build/output/ && \
    cp target/release/log_service_manager /build/output/ && \
    cp target/release/speed_tracker /build/output/ && \
    cp target/release/cache_size /build/output/ && \
    cp target/release/cache_clear /build/output/ && \
    cp target/release/cache_corruption /build/output/ && \
    cp target/release/cache_game_detect /build/output/ && \
    cp target/release/cache_game_remove /build/output/ && \
    cp target/release/cache_epic_remove /build/output/ && \
    cp target/release/cache_service_remove /build/output/ && \
    cp target/release/cache_eviction_scan /build/output/ && \
    cp target/release/cache_purge_log_entries /build/output/ && \
    cp target/release/db_reset /build/output/ && \
    chmod +x /build/output/*

# Stage 2: Build Frontend
FROM node:22-alpine AS frontend-builder
WORKDIR /app

# Copy and install dependencies
COPY Web/package*.json ./
RUN npm ci --quiet

# Copy source and build
COPY Web/ ./
RUN npm run build

# Stage 3: Build Backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-builder

ARG TARGETPLATFORM

WORKDIR /src

# Copy only project file first for dependency caching
COPY Api/LancacheManager/LancacheManager.csproj ./

# Restore dependencies (cached unless csproj changes)
RUN dotnet restore LancacheManager.csproj

# Now copy source code (only this rebuilds on code changes)
COPY Api/LancacheManager/ ./
# csproj embeds <EmbeddedResource Include="..\..\rust-processor\tact_products.json"> (and blizzard_steam_appids.json);
# the csproj sits at /src here, so those relative paths resolve to /rust-processor/<file> — provide them there.
COPY rust-processor/tact_products.json /rust-processor/tact_products.json
COPY rust-processor/blizzard_steam_appids.json /rust-processor/blizzard_steam_appids.json

# Determine runtime identifier based on target platform
RUN case "$TARGETPLATFORM" in \
        "linux/amd64") RID="linux-x64" ;; \
        "linux/arm64") RID="linux-arm64" ;; \
        *) RID="linux-x64" ;; \
    esac && \
    echo "Building for RID: $RID" && \
    dotnet publish LancacheManager.csproj -c Release -o /app/publish -r $RID --self-contained false -p:SkipRustBuild=true

# Copy frontend build to wwwroot
COPY --from=frontend-builder /app/dist /app/publish/wwwroot

# Copy Rust binaries to the publish directory
COPY --from=rust-builder /build/output/* /app/publish/rust-processor/

# Stage 4: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION

# Image variant control.
#   INSTALL_POSTGRES=true / IMAGE_VARIANT=full  (default) -> ships embedded PostgreSQL 17.
#                                                           Supports POSTGRES_MODE=embedded or external.
#   INSTALL_POSTGRES=false / IMAGE_VARIANT=slim           -> no embedded server (~150 MB smaller).
#                                                           Requires POSTGRES_MODE=external.
ARG INSTALL_POSTGRES=true
ARG IMAGE_VARIANT=full

WORKDIR /app

# Metadata labels
LABEL org.opencontainers.image.title="LanCache Manager"
LABEL org.opencontainers.image.description="Modern monitoring interface for LanCache deployments"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="LanCache Manager"
LABEL org.opencontainers.image.source="https://github.com/regix1/lancache-manager"
LABEL org.opencontainers.image.licenses="MIT"
LABEL io.lancache-manager.variant="${IMAGE_VARIANT}"

# Set version as environment variable for runtime access
ENV LANCACHE_MANAGER_VERSION=${VERSION}

# Install runtime dependencies including tools for fast cache clearing and Docker CLI
# Docker CLI is needed to send signals to nginx container via 'docker kill' command
# gosu is needed for PUID/PGID support (running as non-root user)
# Note: .NET 10 uses Ubuntu 24.04 (noble) base image, so we use Docker's Ubuntu repository
RUN apt-get update && \
    apt-get install -y \
    curl \
    bash \
    nano \
    procps \
    net-tools \
    rsync \
    findutils \
    coreutils \
    ca-certificates \
    gnupg \
    lsb-release \
    gosu \
    acl \
    jq \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg \
    && chmod a+r /etc/apt/keyrings/docker.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null \
    && apt-get update \
    && apt-get install -y docker-ce-cli \
    && rm -rf /var/lib/apt/lists/*

# sqlite3 is always installed - used by the SQLite -> PostgreSQL migration script
# in both full and slim variants.
RUN apt-get update && apt-get install -y --no-install-recommends sqlite3 \
    && rm -rf /var/lib/apt/lists/*

# Install embedded PostgreSQL 17 only for the "full" variant.
# Slim builds skip this and require POSTGRES_MODE=external at runtime.
RUN if [ "$INSTALL_POSTGRES" = "true" ]; then \
        apt-get update && apt-get install -y --no-install-recommends postgresql-common \
        && /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y \
        && apt-get update && apt-get install -y --no-install-recommends postgresql-17 \
        && rm -rf /var/lib/apt/lists/* \
        && mkdir -p /var/run/postgresql \
        && chown postgres:postgres /var/run/postgresql; \
    fi

# Copy embedded-postgres tuning config only when embedded is installed.
COPY postgresql.conf /tmp/postgresql.conf
RUN if [ "$INSTALL_POSTGRES" = "true" ]; then \
        mkdir -p /etc/postgresql/17/main \
        && mv /tmp/postgresql.conf /etc/postgresql/17/main/postgresql.conf; \
    else \
        rm -f /tmp/postgresql.conf; \
    fi

# Copy published application
COPY --from=backend-builder /app/publish ./

# Copy entrypoint and migration scripts
COPY entrypoint.sh /entrypoint.sh
COPY scripts/ /scripts/
RUN chmod +x /entrypoint.sh /scripts/*.sh

# Create /tmp directory (data/logs/cache are created by the application)
RUN mkdir -p /tmp && chmod 777 /tmp

# Configure environment
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TZ=UTC
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# PostgreSQL 17 binaries install to /usr/lib/postgresql/17/bin, but Debian/PGDG do not add that
# directory to PATH. Without it, entrypoint.sh's slim-detection (command -v pg_ctl) false-negatives
# on the full image and forces external mode (GitHub issue #25); it also breaks the bare-name
# pg_isready/psql calls later in the script. Mirrors the official postgres image.
ENV PATH="${PATH}:/usr/lib/postgresql/17/bin"

# Enable server GC for better throughput (auto-detects CPU count)
ENV DOTNET_gcServer=1

# Health check
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Volumes
VOLUME ["/data", "/logs", "/cache"]

# Port
EXPOSE 80

# Run via entrypoint script for PUID/PGID support
ENTRYPOINT ["/entrypoint.sh"]
