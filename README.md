# LANCache Manager

High-performance cache monitoring and management for LANCache deployments.

![Dashboard Overview](images/dashboard.png)

## What it does

Monitors and manages your LANCache server in real-time. Shows what's being downloaded, who's downloading it, and how well your cache is performing.

## Key Features

### Dashboard
- Live statistics: cache size, bandwidth saved, hit ratios
- Service distribution chart (Steam, Epic, Blizzard, etc.)
- Recent downloads with progress tracking
- Top clients by usage

<img width="1513" height="1222" alt="Home" src="https://github.com/user-attachments/assets/eeda1b52-bb41-4b3a-bd8e-e4b5b8afc388" />

### Downloads View
- Real-time download monitoring
- Service filtering and search
- Hit rate indicators
- Client IP tracking

<img width="1516" height="688" alt="Downloads" src="https://github.com/user-attachments/assets/3c3f4e26-c4db-48f7-b304-3c771cb68c68" />

### Client Management
- Active client monitoring
- Per-client statistics
- Cache hit/miss ratios
- Historical data tracking

<img width="1510" height="1069" alt="Clients" src="https://github.com/user-attachments/assets/c27d4fae-bbfa-45df-8e40-641b8e08a2f9" />

### Management Tools
- **Authentication**: API key based security
- **Mock Mode**: Demo data for testing
- **Database Management**: Reset statistics and history
- **Cache Management**: Clear cached files
- **Log Processing**: Control access.log processing
- **Data Export**: Prometheus metrics, JSON API endpoints
- **Theme Engine**: Full UI customization

<img width="1512" height="1159" alt="Management-1" src="https://github.com/user-attachments/assets/5d394dc9-a73f-480c-9664-5825a135b478" />
<img width="1511" height="1226" alt="Management-2" src="https://github.com/user-attachments/assets/866d68ad-d60f-42e3-adfb-b70678bf6a68" />

## Quick Start

### Docker Compose
```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    ports:
      - "5050:5050"
    volumes:
      - /path/to/lancache/logs:/data/logs:ro
      - /path/to/lancache/cache:/data/cache:ro
      - ./data:/data
    environment:
      - ConnectionStrings__DefaultConnection=/data/lancache.db
      - LogProcessing__LogPath=/data/logs/access.log
      - CachePath=/data/cache
```

### Manual Setup

#### Backend (API)
```bash
cd Api/LancacheManager
dotnet restore
dotnet run
```

#### Frontend (Web)
```bash
cd Web
npm install
npm run dev
```

Access at `http://localhost:5173`

## Configuration

### Environment Variables
- `LogProcessing__LogPath`: Path to LANCache access.log
- `CachePath`: Path to LANCache cache directory
- `Security__RequireAuthForMetrics`: Enable/disable metrics authentication

### API Authentication
1. Find your API key in `/data/api_key.txt` or container logs
2. Click "Authenticate" in Management tab
3. Enter API key to enable management features

## API Endpoints

### Public Endpoints
- `GET /api/dashboard` - Dashboard statistics
- `GET /api/downloads` - Active downloads
- `GET /api/clients` - Connected clients
- `GET /api/metrics` - Prometheus metrics
- `GET /api/metrics/json` - JSON metrics

### Protected Endpoints (require API key)
- `POST /api/database/reset` - Reset database
- `POST /api/cache/clear` - Clear cache files
- `POST /api/logs/process` - Trigger log processing
- `POST /api/auth/regenerate` - Regenerate API key

## Theming

Create custom themes to match your setup. See [Wiki: Theming Guide](wiki/Theming.md) for details.

## Requirements

- LANCache server with access.log
- .NET 8.0 Runtime (for API)
- Node.js 18+ (for development)
- Docker (for containerized deployment)

## Performance

- Handles 100,000+ log entries efficiently
- Real-time updates via SignalR WebSockets
- SQLite database for persistence
- Memory-cached statistics

## Support

- [Issues](https://github.com/regix1/lancache-manager/issues)
- [Wiki](https://github.com/regix1/lancache-manager/wiki)

## License

MIT License - See LICENSE file for details
