# Docker Environment Variables Reference

## Complete List of Working Environment Variables

All environment variables for LanCache Manager with their current implementation status.

### ✅ System Configuration

| Variable | Default | Status | Description |
|----------|---------|--------|-------------|
| `PUID` | 1000 | ✅ Working | User ID for file permissions |
| `PGID` | 1000 | ✅ Working | Group ID for file permissions |
| `TZ` | UTC | ✅ Working | Timezone (e.g., America/Chicago) |
| `ASPNETCORE_URLS` | http://+:80 | ✅ Working | HTTP binding configuration |

### ✅ Core LanCache Settings

| Variable | Default | Status | Description |
|----------|---------|--------|-------------|
| `LanCache__LogPath` | /logs/access.log | ✅ Working | Path to nginx access log |
| `LanCache__CachePath` | /cache | ✅ Working | Path to cache storage |
| `LanCache__StartFromEndOfLog` | true | ✅ Working | Start monitoring from current position vs beginning |

### ✅ Performance Tuning (All Implemented!)

| Variable | Default | Status | Description |
|----------|---------|--------|-------------|
| `LanCache__ChannelCapacity` | 100000 | ✅ Working | Queue size for log processing |
| `LanCache__BatchSize` | 5000 | ✅ Working | Entries per database batch |
| `LanCache__BatchTimeoutMs` | 500 | ✅ Working | Max time before flushing batch (ms) |
| `LanCache__ConsumerCount` | 4 | ✅ Working | Parallel database consumers |
| `LanCache__ParserParallelism` | 8 | ✅ Working | Parallel log parsers |
| `LanCache__UseHighThroughputMode` | false | ✅ Working | Optimize for bulk vs real-time |

### ✅ Security Settings

| Variable | Default | Status | Description |
|----------|---------|--------|-------------|
| `Security__EnableAuthentication` | true | ✅ Working | Require auth for Management tab |
| `Security__RequireAuthForMetrics` | false | ✅ Working | Require auth for /metrics endpoint |

## Example docker-compose.yml

```yaml
version: '3.8'

services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8080:80"
    volumes:
      - ./data:/data
      - /path/to/logs:/logs:ro
      - /path/to/cache:/cache:ro
    environment:
      # System Configuration
      - PUID=1000
      - PGID=1000
      - TZ=America/Chicago

      # Core Settings
      - ASPNETCORE_URLS=http://+:80
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - LanCache__StartFromEndOfLog=true

      # Performance (Small Setup)
      - LanCache__ChannelCapacity=10000
      - LanCache__BatchSize=1000
      - LanCache__BatchTimeoutMs=200
      - LanCache__ConsumerCount=2
      - LanCache__ParserParallelism=4
      - LanCache__UseHighThroughputMode=false

      # Security
      - Security__EnableAuthentication=true
      - Security__RequireAuthForMetrics=false
```

## Performance Presets

### Home Network (1-10 clients)
```yaml
- LanCache__ChannelCapacity=10000
- LanCache__BatchSize=1000
- LanCache__BatchTimeoutMs=200
- LanCache__ConsumerCount=2
- LanCache__ParserParallelism=4
- LanCache__UseHighThroughputMode=false
```

### LAN Party (10-50 clients)
```yaml
- LanCache__ChannelCapacity=100000
- LanCache__BatchSize=5000
- LanCache__BatchTimeoutMs=500
- LanCache__ConsumerCount=4
- LanCache__ParserParallelism=8
- LanCache__UseHighThroughputMode=false
```

### Large Event (50-200 clients)
```yaml
- LanCache__ChannelCapacity=500000
- LanCache__BatchSize=10000
- LanCache__BatchTimeoutMs=1000
- LanCache__ConsumerCount=8
- LanCache__ParserParallelism=16
- LanCache__UseHighThroughputMode=false
```

### Convention (200+ clients)
```yaml
- LanCache__ChannelCapacity=1000000
- LanCache__BatchSize=20000
- LanCache__BatchTimeoutMs=2000
- LanCache__ConsumerCount=16
- LanCache__ParserParallelism=32
- LanCache__UseHighThroughputMode=true
```

## Security Configurations

### Fully Open (Local Network Only!)
```yaml
- Security__EnableAuthentication=false
- Security__RequireAuthForMetrics=false
```

### Protected Management, Open Metrics
```yaml
- Security__EnableAuthentication=true
- Security__RequireAuthForMetrics=false
```

### Fully Protected
```yaml
- Security__EnableAuthentication=true
- Security__RequireAuthForMetrics=true
```

## Monitoring Performance

Check current configuration and queue stats:
```bash
# View active configuration
curl http://localhost:8080/api/performance/config

# Monitor queue depths
curl http://localhost:8080/api/performance/stats

# Full metrics including memory/CPU
curl http://localhost:8080/api/performance/metrics
```

## Notes

1. **All settings are working** - Every environment variable listed here is implemented and functional
2. **Performance settings** - Now properly implemented with parallel processing
3. **Security settings** - Can completely disable authentication if needed
4. **Real-time updates** - Disabled in high throughput mode for better performance
5. **Volume mounts** - Ensure `:ro` (read-only) for logs and cache directories

## Validation Checklist

- [x] Frontend builds successfully
- [x] Backend builds successfully
- [x] SignalR hub events compatible
- [x] Authentication can be disabled
- [x] Performance settings all functional
- [x] StartFromEndOfLog working
- [x] All Docker variables properly read

## Troubleshooting

### Settings not taking effect?
- Restart the container after changing environment variables
- Check logs: `docker logs lancache-manager`

### Performance issues?
- Check queue stats: `/api/performance/stats`
- Increase ConsumerCount and ParserParallelism
- Enable UseHighThroughputMode for bulk processing

### Authentication issues?
- Set `Security__EnableAuthentication=false` to disable
- Check API key file exists at `/data/api_key.txt`

### Database locked errors?
- Reduce ConsumerCount
- Increase BatchTimeoutMs
- Ensure database is on SSD/NVMe