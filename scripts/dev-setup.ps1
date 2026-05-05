#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"

$PG_HOST = "localhost"
$PG_PORT = "5432"
$PG_USER = "lancache"
$PG_PASSWORD = "lancache"
$PG_DATABASE = "lancache"
$CONTAINER_NAME = "lancache-postgres"

Write-Host "Checking for PostgreSQL on ${PG_HOST}:${PG_PORT}..." -ForegroundColor Cyan

$pgReady = $false
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect($PG_HOST, [int]$PG_PORT)
    $tcp.Close()
    $pgReady = $true
    Write-Host "PostgreSQL is running on ${PG_HOST}:${PG_PORT}" -ForegroundColor Green
} catch {
    Write-Host "PostgreSQL not reachable on ${PG_HOST}:${PG_PORT}" -ForegroundColor Yellow
}

if (-not $pgReady) {
    Write-Host "Starting PostgreSQL Docker container..." -ForegroundColor Cyan
    $existing = docker ps -a --filter "name=$CONTAINER_NAME" --format "{{.Names}}" 2>$null
    if ($existing -eq $CONTAINER_NAME) {
        docker start $CONTAINER_NAME
    } else {
        docker run -d --name $CONTAINER_NAME -e POSTGRES_PASSWORD=$PG_PASSWORD -e POSTGRES_USER=$PG_USER -e POSTGRES_DB=$PG_DATABASE -p "${PG_PORT}:5432" postgres:17-alpine
    }

    Write-Host "Waiting for PostgreSQL..." -ForegroundColor Cyan
    $retries = 30
    while ($retries -gt 0) {
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $tcp.Connect($PG_HOST, [int]$PG_PORT)
            $tcp.Close()
            break
        } catch { Start-Sleep -Seconds 1; $retries-- }
    }
    if ($retries -eq 0) { Write-Error "PostgreSQL failed to start"; exit 1 }

    # Wait extra for postgres to accept connections
    Start-Sleep -Seconds 3
    Write-Host "PostgreSQL is ready!" -ForegroundColor Green
}

# Create database and user via docker exec on any running postgres container
$anyPostgres = docker ps --filter "ancestor=postgres" --format "{{.Names}}" 2>$null | Select-Object -First 1
if (-not $anyPostgres) {
    $anyPostgres = docker ps --filter "name=postgres" --format "{{.Names}}" 2>$null | Select-Object -First 1
}

if ($anyPostgres) {
    Write-Host "Creating database and user via container '$anyPostgres'..." -ForegroundColor Cyan
    # Create user if not exists
    $userExists = (docker exec $anyPostgres psql -U postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname='$PG_USER'" 2>$null) -join ""
    if ($userExists.Trim() -ne "1") {
        docker exec $anyPostgres psql -U postgres -c "CREATE USER $PG_USER WITH SUPERUSER PASSWORD '$PG_PASSWORD'" 2>$null
        Write-Host "  Created user '$PG_USER'" -ForegroundColor Green
    } else {
        # Ensure password is set (may have been created without one)
        docker exec $anyPostgres psql -U postgres -c "ALTER USER $PG_USER WITH PASSWORD '$PG_PASSWORD'" 2>$null
        Write-Host "  User '$PG_USER' already exists (password updated)" -ForegroundColor Gray
    }
    # Create database if not exists
    $dbExists = (docker exec $anyPostgres psql -U postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$PG_DATABASE'" 2>$null) -join ""
    if ($dbExists.Trim() -ne "1") {
        docker exec $anyPostgres psql -U postgres -c "CREATE DATABASE $PG_DATABASE OWNER $PG_USER" 2>$null
        Write-Host "  Created database '$PG_DATABASE'" -ForegroundColor Green
    } else {
        Write-Host "  Database '$PG_DATABASE' already exists" -ForegroundColor Gray
    }
    Write-Host "Database '$PG_DATABASE' and user '$PG_USER' ready." -ForegroundColor Green
} else {
    Write-Host "No postgres container found. Please ensure database '$PG_DATABASE' and user '$PG_USER' exist." -ForegroundColor Yellow
}

$env:DATABASE_URL = "postgres://${PG_USER}:${PG_PASSWORD}@${PG_HOST}:${PG_PORT}/${PG_DATABASE}"
$env:POSTGRES_USER = $PG_USER
$env:POSTGRES_PASSWORD = $PG_PASSWORD

Write-Host ""
Write-Host "Dev environment ready!" -ForegroundColor Green
Write-Host "  Connection: Host=$PG_HOST;Port=$PG_PORT;Database=$PG_DATABASE;Username=$PG_USER;Password=$PG_PASSWORD"
Write-Host "  DATABASE_URL: $env:DATABASE_URL"
Write-Host ""
Write-Host "Run: dotnet run --project Api/LancacheManager" -ForegroundColor Cyan
