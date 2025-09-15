# LanCache Manager

Real-time monitoring and management interface for LanCache.

## Features

- **Real-time Stats** - Live bandwidth monitoring, cache hit rates, client tracking
- **Download History** - Steam depot detection with game identification
- **Cache Management** - Direct cache manipulation and cleanup tools
- **Performance** - Parallel log processing with configurable workers
- **Theming** - Full UI customization with 20+ built-in themes

## Requirements

- Docker
- LanCache instance with accessible logs
- 2GB RAM minimum (4GB+ recommended for large deployments)

## Quick Start

```bash
docker run -d \
  --name lancache-manager \
  -p 8080:80 \
  -v ./data:/data \
  -v /path/to/cache/logs:/logs:ro \
  -v /path/to/cache/data:/cache:ro \
  ghcr.io/regix1/lancache-manager:latest
```

Access at `http://localhost:8080`

## Configuration

### docker-compose.yml

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

See [wiki/DOCKER_ENVIRONMENT_REFERENCE.MD](wiki/DOCKER_ENVIRONMENT_REFERENCE.MD) for all options.

## Performance Tuning

Default configuration handles 10-50 clients. For larger deployments:

```yaml
environment:
  # 200+ clients configuration
  - LanCache__ConsumerCount=8
  - LanCache__ParserParallelism=16
  - LanCache__BatchSize=10000
```

See [wiki/PERFORMANCE_TUNING.MD](wiki/PERFORMANCE_TUNING.MD) for details.

## API

### Metrics Endpoint
```bash
curl http://localhost:8080/metrics
```

### Performance Stats
```bash
curl http://localhost:8080/api/performance/stats
```

## Authentication

Management operations require authentication by default. On first run, check logs for API key:

```bash
docker logs lancache-manager | grep "API Key"
```

To disable authentication (local networks only):
```yaml
environment:
  - Security__EnableAuthentication=false
```

## Development

### Tech Stack
- **Backend**: .NET 8, SignalR, SQLite
- **Frontend**: React, TypeScript, Vite
- **Charts**: Recharts
- **Styling**: Tailwind CSS

### Building

```bash
# Frontend
cd Web && npm install && npm run build

# Backend
cd Api/LancacheManager && dotnet build

# Docker
docker build -t lancache-manager .
```

## Documentation

- [Environment Variables](wiki/DOCKER_ENVIRONMENT_REFERENCE.MD)
- [Performance Tuning](wiki/PERFORMANCE_TUNING.MD)
- [Grafana Integration](wiki/GRAFANA.MD)
- [Custom Themes](wiki/THEMING.MD)

## License

MIT