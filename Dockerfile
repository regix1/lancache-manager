# Multi-stage build for Lancache Manager

# Stage 1: Build Frontend
FROM node:20-alpine AS frontend-builder
WORKDIR /app/web

# Copy frontend package files
COPY Web/package*.json ./

# Install dependencies
RUN npm install

# Copy frontend source
COPY Web/ .

# Build frontend
RUN node node_modules/vite/bin/vite.js build

# Stage 2: Build Backend
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-builder
WORKDIR /app

# Copy backend project
COPY Api/ ./Api/

# Clean wwwroot to prevent duplicates
RUN rm -rf ./Api/LancacheManager/wwwroot/* || true

# Copy built frontend
COPY --from=frontend-builder /app/Api/wwwroot ./Api/LancacheManager/wwwroot/

# Restore and publish backend
WORKDIR /app/Api/LancacheManager
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app

# Install necessary packages including su-exec for user switching
RUN apk add --no-cache \
    curl \
    tzdata \
    su-exec \
    shadow \
    && rm -rf /var/cache/apk/*

# Copy published app
COPY --from=backend-builder /app/publish .

# Create necessary directories
RUN mkdir -p /data \
    && mkdir -p /logs \
    && mkdir -p /cache

# Create a user and group that will be modified by entrypoint
RUN addgroup -g 1000 lancache && \
    adduser -D -u 1000 -G lancache lancache

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ConnectionStrings__DefaultConnection="Data Source=/data/lancache-manager.db"
ENV TZ=UTC
ENV PUID=1000
ENV PGID=1000

# Add labels
LABEL org.opencontainers.image.title="Lancache Manager"
LABEL org.opencontainers.image.description="Web-based monitoring and management for Lancache"

# Create entrypoint script
RUN echo '#!/bin/sh' > /entrypoint.sh && \
    echo 'set -e' >> /entrypoint.sh && \
    echo '' >> /entrypoint.sh && \
    echo '# Set user and group ID' >> /entrypoint.sh && \
    echo 'PUID=${PUID:-1000}' >> /entrypoint.sh && \
    echo 'PGID=${PGID:-1000}' >> /entrypoint.sh && \
    echo '' >> /entrypoint.sh && \
    echo '# Modify existing lancache user/group' >> /entrypoint.sh && \
    echo 'if [ "$PUID" != "1000" ] || [ "$PGID" != "1000" ]; then' >> /entrypoint.sh && \
    echo '    echo "Setting user to $PUID:$PGID"' >> /entrypoint.sh && \
    echo '    deluser lancache 2>/dev/null || true' >> /entrypoint.sh && \
    echo '    delgroup lancache 2>/dev/null || true' >> /entrypoint.sh && \
    echo '    addgroup -g $PGID lancache' >> /entrypoint.sh && \
    echo '    adduser -D -u $PUID -G lancache lancache' >> /entrypoint.sh && \
    echo 'fi' >> /entrypoint.sh && \
    echo '' >> /entrypoint.sh && \
    echo '# Fix permissions' >> /entrypoint.sh && \
    echo 'chown -R $PUID:$PGID /data /app || true' >> /entrypoint.sh && \
    echo '' >> /entrypoint.sh && \
    echo '# Execute the application as the specified user' >> /entrypoint.sh && \
    echo 'exec su-exec $PUID:$PGID dotnet LancacheManager.dll' >> /entrypoint.sh && \
    chmod +x /entrypoint.sh

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Volume for persistent data
VOLUME ["/data", "/logs"]

# Expose port
EXPOSE 80

# Use the entrypoint script
ENTRYPOINT ["/entrypoint.sh"]