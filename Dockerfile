# Multi-stage build for Lancache Manager with Rust processors
# Supports: linux/amd64, linux/arm64

ARG VERSION=1.2.0

# Stage 1: Build Rust binaries
# Using full rust image (not slim) to avoid installing gcc/g++ which causes timeouts
FROM rust:1.83 AS rust-builder

ARG TARGETPLATFORM

WORKDIR /build/rust-processor

# Copy Rust project files
COPY rust-processor/Cargo.toml rust-processor/Cargo.lock* ./
COPY rust-processor/src ./src

# Build for native platform
RUN cargo build --release && \
    mkdir -p /build/output && \
    cp target/release/lancache_processor /build/output/ && \
    cp target/release/database_reset /build/output/ && \
    chmod +x /build/output/*

# Stage 2: Build Frontend
FROM node:20-alpine AS frontend-builder
WORKDIR /app

# Copy and install dependencies
COPY Web/package*.json ./
RUN npm ci --quiet

# Copy source and build
COPY Web/ ./
RUN npm run build

# Stage 3: Build Backend
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-builder

ARG TARGETPLATFORM

WORKDIR /src

# Copy the entire backend project directory
COPY Api/LancacheManager/ ./

# Determine runtime identifier based on target platform
RUN case "$TARGETPLATFORM" in \
        "linux/amd64") RID="linux-x64" ;; \
        "linux/arm64") RID="linux-arm64" ;; \
        *) RID="linux-x64" ;; \
    esac && \
    echo "Building for RID: $RID" && \
    dotnet restore LancacheManager.csproj && \
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

# Install runtime dependencies including tools for fast cache clearing
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
    && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=backend-builder /app/publish ./

# Create required directories with proper permissions
RUN mkdir -p /data /logs /cache /tmp && \
    chmod -R 777 /data /logs /cache /tmp

# Configure environment
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TZ=UTC
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Health check
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Volumes
VOLUME ["/data", "/logs", "/cache"]

# Port
EXPOSE 80

# Run the application directly
ENTRYPOINT ["dotnet", "LancacheManager.dll"]
