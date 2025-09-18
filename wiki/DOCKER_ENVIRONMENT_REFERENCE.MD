# Docker Environment Variables Reference

Complete configuration reference for LanCache Manager Docker deployment.

## System Configuration

Control the Docker container runtime environment.

| Variable | Default | Description | Example |
|----------|---------|-------------|---------|
| `PUID` | 1000 | User ID for file permissions | `1000` |
| `PGID` | 1000 | Group ID for file permissions | `1000` |
| `TZ` | UTC | Timezone for logs and timestamps | `America/Chicago` |
| `ASPNETCORE_URLS` | http://+:80 | HTTP binding for the web server | `http://+:80` |

## Core LanCache Settings

Essential configuration for connecting to your LanCache deployment.

| Variable | Default | Description | Example |
|----------|---------|-------------|---------|
| `LanCache__LogPath` | /logs/access.log | Path to nginx access log file | `/logs/access.log` |
| `LanCache__CachePath` | /cache | Cache directory path | `/cache` |
| `LanCache__StartFromEndOfLog` | true | Start processing from current log position | `true` |
| `LanCache__ProcessHistoricalLogs` | false | Process existing logs on startup | `false` |

## Security Settings

Control who can access what.

| Variable | Default | Description | Example |
|----------|---------|-------------|---------|
| `Security__EnableAuthentication` | true | Require API key for Management tab access | `true` |
| `Security__RequireAuthForMetrics` | false | Require API key for Grafana/Prometheus metrics | `false` |

## Complete docker-compose.yml Example

### Basic Setup
```yaml
version: '3.8'

services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    user: root
    ports:
      - "8080:80"
    volumes:
      # Data persistence
      - ./data:/data

      # Your actual LanCache paths (read-only)
      - /path/to/lancache/logs:/logs:ro
      - /path/to/lancache/cache:/cache:ro
    environment:
      # System Configuration
      - PUID=1000
      - PGID=1000
      - TZ=America/Chicago
      - ASPNETCORE_URLS=http://+:80

      # Core LanCache Settings
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - LanCache__StartFromEndOfLog=true
      - LanCache__ProcessHistoricalLogs=false

      # Security Settings (adjust for your network)
      - Security__EnableAuthentication=true
      - Security__RequireAuthForMetrics=false
```

## API Endpoints

### Metrics (Grafana/Prometheus)
```bash
# Prometheus format
curl http://localhost:8080/api/metrics

# JSON format
curl http://localhost:8080/api/metrics/json

# Check authentication status
curl http://localhost:8080/api/metrics/status
```

### Data Export
All download data can be exported from the **Downloads** tab in JSON or CSV format with applied filters.

## Common Configurations

### Public Network Setup
For networks where authentication isn't needed:
```yaml
environment:
  - Security__EnableAuthentication=false
  - Security__RequireAuthForMetrics=false
```

### Secure Production Setup
For production deployments requiring authentication:
```yaml
environment:
  - Security__EnableAuthentication=true
  - Security__RequireAuthForMetrics=true
```

### Development/Testing Setup
For development or testing environments:
```yaml
environment:
  - LanCache__ProcessHistoricalLogs=true
  - LanCache__StartFromEndOfLog=false
```

## Troubleshooting

| Problem | Solution | Related Variables |
|---------|----------|-------------------|
| **Settings not applied** | Restart container with `docker-compose restart` | All environment variables |
| **Authentication issues** | Disable auth for local networks | `Security__EnableAuthentication=false` |
| **Grafana no data** | Check metrics endpoint and auth settings | `Security__RequireAuthForMetrics` |
| **Container won't start** | Verify volume mounts and file permissions | `PUID`, `PGID` |

## Container Monitoring

### Monitor Container Resources
```bash
# View container resource usage
docker stats lancache-manager

# View container logs
docker logs -f lancache-manager

# Check API key from logs
docker logs lancache-manager | grep "API Key"
```

### Health Checks
```bash
# Verify container is responding
curl http://localhost:8080/api/health
```