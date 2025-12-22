# Multi-stage build for Lancache Manager with Rust processors
# Supports: linux/amd64, linux/arm64

ARG VERSION=1.2.0

# Stage 1: Build Rust binaries
# Using full rust image (not slim) to avoid installing gcc/g++ which causes timeouts
FROM rust:1.83 AS rust-builder

ARG TARGETPLATFORM

WORKDIR /build/rust-processor

# Copy only dependency files first for better caching
COPY rust-processor/Cargo.toml rust-processor/Cargo.lock* ./

# Create dummy src to build dependencies (cache layer)
RUN mkdir src && \
    echo "fn main() {}" > src/main.rs && \
    cargo build --release && \
    rm -rf src target/release/deps/lancache* target/release/lancache* target/release/.fingerprint/lancache*

# Now copy real source and build (only this layer rebuilds on code changes)
COPY rust-processor/src ./src

# Build for native platform
# Binary naming: log_* (log ops), cache_* (cache ops), db_* (database ops)
RUN cargo build --release && \
    mkdir -p /build/output && \
    cp target/release/log_processor /build/output/ && \
    cp target/release/log_service_manager /build/output/ && \
    cp target/release/speed_tracker /build/output/ && \
    cp target/release/cache_clear /build/output/ && \
    cp target/release/cache_corruption /build/output/ && \
    cp target/release/cache_game_detect /build/output/ && \
    cp target/release/cache_game_remove /build/output/ && \
    cp target/release/cache_service_remove /build/output/ && \
    cp target/release/db_reset /build/output/ && \
    cp target/release/db_migrate /build/output/ && \
    chmod +x /build/output/*

# Stage 2: Build Frontend
FROM node:20-alpine AS frontend-builder
WORKDIR /app

# Copy and install dependencies
COPY Web/package*.json ./
RUN npm install --quiet

# Copy source and build
COPY Web/ ./
RUN npm run build

# Stage 3: Build Backend
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-builder

ARG TARGETPLATFORM

WORKDIR /src

# Copy only project file first for dependency caching
COPY Api/LancacheManager/LancacheManager.csproj ./

# Restore dependencies (cached unless csproj changes)
RUN dotnet restore LancacheManager.csproj

# Now copy source code (only this rebuilds on code changes)
COPY Api/LancacheManager/ ./

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

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
ARG VERSION
WORKDIR /app

# Metadata labels
LABEL org.opencontainers.image.title="LanCache Manager"
LABEL org.opencontainers.image.description="Modern monitoring interface for LanCache deployments"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="LanCache Manager"
LABEL org.opencontainers.image.source="https://github.com/regix1/lancache-manager"
LABEL org.opencontainers.image.licenses="MIT"

# Set version as environment variable for runtime access
ENV LANCACHE_MANAGER_VERSION=${VERSION}

# Install runtime dependencies including tools for fast cache clearing and Docker CLI
# Docker CLI is needed to send signals to nginx container via 'docker kill' command
# gosu is needed for PUID/PGID support (running as non-root user)
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
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/debian/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg \
    && chmod a+r /etc/apt/keyrings/docker.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null \
    && apt-get update \
    && apt-get install -y docker-ce-cli \
    && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=backend-builder /app/publish ./

# Copy entrypoint script
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Create required directories with proper permissions
RUN mkdir -p /data /logs /cache /tmp && \
    chmod -R 777 /data /logs /cache /tmp

# Configure environment
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TZ=UTC
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

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
