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

## Performance Tuning

Optimize performance based on your deployment size and hardware.

| Variable | Default | Description | Range | Example |
|----------|---------|-------------|-------|---------|
| `LanCache__ChannelCapacity` | 100000 | Internal queue size for log processing | 1000-1000000 | `100000` |
| `LanCache__BatchSize` | 5000 | Database batch size for bulk operations | 100-50000 | `5000` |
| `LanCache__BatchTimeoutMs` | 500 | Timeout for batch operations (milliseconds) | 100-5000 | `500` |
| `LanCache__ConsumerCount` | 4 | Number of parallel log processors | 1-16 | `4` |
| `LanCache__ParserParallelism` | 8 | Number of parallel log parsers | 1-32 | `8` |
| `LanCache__UseHighThroughputMode` | false | Enable high-throughput mode for events | true/false | `false` |

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

## Performance Presets

Choose the configuration that matches your deployment size.

### Home Network (1-10 clients)
Resources: 2GB RAM, 2 CPU cores
```yaml
environment:
  - LanCache__ConsumerCount=2
  - LanCache__ParserParallelism=4
  - LanCache__BatchSize=1000
  - LanCache__ChannelCapacity=50000
```

### LAN Party (10-50 clients)
Resources: 4GB RAM, 4 CPU cores
```yaml
environment:
  - LanCache__ConsumerCount=4
  - LanCache__ParserParallelism=8
  - LanCache__BatchSize=5000
  - LanCache__ChannelCapacity=100000
```

### Large Event (50-200 clients)
Resources: 8GB RAM, 8 CPU cores
```yaml
environment:
  - LanCache__ConsumerCount=8
  - LanCache__ParserParallelism=16
  - LanCache__BatchSize=10000
  - LanCache__UseHighThroughputMode=true
  - LanCache__ChannelCapacity=200000
```

### Enterprise (200+ clients)
Resources: 16GB+ RAM, 16+ CPU cores
```yaml
environment:
  - LanCache__ConsumerCount=16
  - LanCache__ParserParallelism=32
  - LanCache__BatchSize=20000
  - LanCache__UseHighThroughputMode=true
  - LanCache__ChannelCapacity=500000
```

## API Endpoints

### Performance Monitoring
```bash
# Real-time queue statistics
curl http://localhost:8080/api/performance/stats

# Detailed performance metrics
curl http://localhost:8080/api/performance/metrics

# System health check
curl http://localhost:8080/api/health
```

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
| **High memory usage** | Reduce queue size and batch operations | `ChannelCapacity`, `BatchSize` |
| **Slow log processing** | Increase parallel processing | `ConsumerCount`, `ParserParallelism` |
| **Authentication issues** | Disable auth for local networks | `Security__EnableAuthentication=false` |
| **Grafana no data** | Check metrics endpoint and auth settings | `Security__RequireAuthForMetrics` |
| **Database locked errors** | Reduce concurrent operations | `ConsumerCount`, `BatchSize` |
| **Container won't start** | Verify volume mounts and file permissions | `PUID`, `PGID` |
| **Dashboard not updating** | Disable high-throughput mode | `UseHighThroughputMode=false` |

## Performance Monitoring

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

# Check performance stats
curl http://localhost:8080/api/performance/stats | jq
```