# Docker Environment Variables

## System
| Variable | Default | Description |
|----------|---------|-------------|
| `PUID` | 1000 | User ID |
| `PGID` | 1000 | Group ID |
| `TZ` | UTC | Timezone |
| `ASPNETCORE_URLS` | http://+:80 | HTTP binding |

## Core
| Variable | Default | Description |
|----------|---------|-------------|
| `LanCache__LogPath` | /logs/access.log | nginx access log path |
| `LanCache__CachePath` | /cache | Cache directory |
| `LanCache__StartFromEndOfLog` | true | Start from current log position |

## Performance
| Variable | Default | Description |
|----------|---------|-------------|
| `LanCache__ChannelCapacity` | 100000 | Queue size |
| `LanCache__BatchSize` | 5000 | Batch size |
| `LanCache__BatchTimeoutMs` | 500 | Batch timeout (ms) |
| `LanCache__ConsumerCount` | 4 | Parallel consumers |
| `LanCache__ParserParallelism` | 8 | Parallel parsers |
| `LanCache__UseHighThroughputMode` | false | Bulk mode |

## Security
| Variable | Default | Description |
|----------|---------|-------------|
| `Security__EnableAuthentication` | true | Require Management auth |
| `Security__RequireAuthForMetrics` | false | Require metrics auth |

## docker-compose.yml

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
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
```

## Performance Presets

### Home (1-10 clients)
```yaml
LanCache__ConsumerCount=2
LanCache__ParserParallelism=4
LanCache__BatchSize=1000
```

### LAN Party (10-50 clients)
```yaml
LanCache__ConsumerCount=4
LanCache__ParserParallelism=8
LanCache__BatchSize=5000
```

### Large Event (50+ clients)
```yaml
LanCache__ConsumerCount=8
LanCache__ParserParallelism=16
LanCache__BatchSize=10000
LanCache__UseHighThroughputMode=true
```

## API Endpoints

```bash
# Queue stats
curl http://localhost:8080/api/performance/stats

# Performance metrics
curl http://localhost:8080/api/performance/metrics
```

## Troubleshooting

**Settings not applied**: Restart container
**Performance issues**: Increase ConsumerCount and ParserParallelism
**Auth issues**: Set `Security__EnableAuthentication=false`
**Database locked**: Reduce ConsumerCount