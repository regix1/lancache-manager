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

# Copy the entire backend project directory
COPY Api/LancacheManager/ ./

# Restore and publish
RUN dotnet restore LancacheManager.csproj
RUN dotnet publish LancacheManager.csproj -c Release -o /app/publish

# Copy frontend build to wwwroot
COPY --from=frontend-builder /app/dist /app/publish/wwwroot

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

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