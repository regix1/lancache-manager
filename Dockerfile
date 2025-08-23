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

# Build frontend (this will output to ../wwwroot based on vite.config.js)
RUN npm run build

# Stage 2: Build Backend
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-builder
WORKDIR /app

# Copy backend project
COPY Api/ ./Api/

# Copy built frontend from the correct location
# The frontend builds to ../wwwroot which from /app/web is /app/wwwroot
COPY --from=frontend-builder /app/wwwroot ./Api/wwwroot/

# Restore and publish backend
WORKDIR /app/Api
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
RUN mkdir -p /data /logs /cache

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ConnectionStrings__DefaultConnection="Data Source=/data/lancache.db"
ENV LanCache__LogPath=/logs/access.log
ENV LanCache__CachePath=/cache

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Volume for persistent data
VOLUME ["/data", "/logs", "/cache"]

# Expose port
EXPOSE 80

# Run the application
ENTRYPOINT ["dotnet", "LancacheManager.dll"]