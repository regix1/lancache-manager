# Multi-stage build for Lancache Manager

# Stage 1: Build Frontend (if you have it in Web/ folder)
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

# Copy project file
COPY *.csproj ./
RUN dotnet restore

# Copy everything else
COPY . ./
RUN dotnet publish -c Release -o /app/publish

# Copy frontend build to wwwroot
COPY --from=frontend-builder /app/dist /app/publish/wwwroot

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app

# Install runtime dependencies
RUN apk add --no-cache curl tzdata

# Copy published application
COPY --from=backend-builder /app/publish ./

# Create required directories with proper permissions
RUN mkdir -p /data /logs /cache && \
    chmod 755 /data /logs /cache

# Configure environment
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TZ=UTC

# Health check
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
  CMD curl -f http://localhost/health || exit 1

# Volumes
VOLUME ["/data", "/logs", "/cache"]

# Port
EXPOSE 80

# Run as non-root user (optional, remove if causing permission issues)
# USER app

# Run
ENTRYPOINT ["dotnet", "LancacheManager.dll"]