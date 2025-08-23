# Multi-stage build for Lancache Manager

# Stage 1: Build Frontend
FROM node:20-alpine AS frontend-builder
WORKDIR /app/web

# Copy frontend package files
COPY Web/package*.json ./
RUN npm ci

# Copy frontend source
COPY Web/ .

# Build frontend
RUN npm run build

# Stage 2: Build Backend
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-builder
WORKDIR /app

# Copy backend project
COPY Api/ ./Api/

# Copy built frontend to wwwroot
COPY --from=frontend-builder /app/Api/LancacheManager/wwwroot ./Api/LancacheManager/wwwroot/

# Restore and publish backend
WORKDIR /app/Api/LancacheManager
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app

# Install necessary packages
RUN apk add --no-cache \
    curl \
    tzdata \
    && rm -rf /var/cache/apk/*

# Copy published app
COPY --from=backend-builder /app/publish .

# Create necessary directories
RUN mkdir -p /data \
    && mkdir -p /logs \
    && mkdir -p /cache

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TZ=UTC
ENV LANG=en_US.UTF-8
ENV ConnectionStrings__DefaultConnection="Data Source=/data/lancache-manager.db"

# Add labels
LABEL org.opencontainers.image.title="Lancache Manager"
LABEL org.opencontainers.image.description="Web-based monitoring and management for Lancache"
LABEL org.opencontainers.image.vendor="Your Organization"

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost/api/management/cache-info || exit 1

# Volume for persistent data
VOLUME ["/data", "/logs"]

# Expose port
EXPOSE 80

# Run as non-root user
RUN adduser -D -u 1000 lancache
USER lancache

# Run the application
ENTRYPOINT ["dotnet", "LancacheManager.dll"]