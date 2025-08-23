# Multi-stage build for Lancache Manager

# Stage 1: Build Frontend
FROM node:20-alpine AS frontend-builder
WORKDIR /app

# Copy and install dependencies
COPY Web/package*.json ./
RUN npm ci --quiet

# Copy source and build
COPY Web/ ./
RUN npm run build

# Stage 2: Build Backend
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-builder
WORKDIR /src

# Copy the backend project (it's in Api/LancacheManager/)
COPY Api/LancacheManager/ ./

# Restore and publish
RUN dotnet restore LancacheManager.csproj
RUN dotnet publish LancacheManager.csproj -c Release -o /app/publish

# Copy frontend build to wwwroot in the publish folder
COPY --from=frontend-builder /app/dist /app/publish/wwwroot

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app

# Install runtime dependencies
RUN apk add --no-cache curl tzdata

# Copy published application
COPY --from=backend-builder /app/publish ./

# Create data directories
RUN mkdir -p /data /logs /cache

# Configure environment
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ConnectionStrings__DefaultConnection="Data Source=/data/lancache.db"
ENV LanCache__LogPath=/logs/access.log
ENV LanCache__CachePath=/cache

# Health check
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Volumes
VOLUME ["/data", "/logs", "/cache"]

# Port
EXPOSE 80

# Run
ENTRYPOINT ["dotnet", "LancacheManager.dll"]