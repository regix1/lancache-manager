# LANCache Manager

High-performance monitoring and management interface for LANCache deployments with real-time statistics, advanced analytics, and comprehensive cache control.

<img width="1513" height="1222" alt="Home" src="https://github.com/user-attachments/assets/eeda1b52-bb41-4b3a-bd8e-e4b5b8afc388" />

## Overview

LANCache Manager provides a powerful web-based interface for monitoring and managing your LANCache server. It offers real-time insights into cache performance, active downloads, client behavior, and provides comprehensive management tools for cache optimization.

## Key Features

### Dashboard
- **Real-time Statistics**: Monitor cache size, bandwidth saved, and hit ratios
- **Service Distribution**: Visual breakdown of cached content by service (Steam, Epic, Blizzard, etc.)
- **Active Downloads**: Track ongoing downloads with progress indicators
- **Client Analytics**: Identify top users and their cache utilization
- **Performance Metrics**: Cache efficiency, hit/miss ratios, and bandwidth savings
- **Customizable Layout**: Drag-and-drop stat cards with visibility toggles

### Downloads Monitoring
- **Live Download Tracking**: Real-time monitoring of all active downloads
- **Service Filtering**: Filter downloads by gaming platform
- **Search Functionality**: Find specific games or content
- **Hit Rate Indicators**: Visual indicators for cache hits vs. misses
- **Client IP Tracking**: Identify which clients are downloading
- **Progress Tracking**: Real-time download progress updates
- **Historical Data**: View completed downloads and statistics

<img width="1516" height="688" alt="Downloads" src="https://github.com/user-attachments/assets/3c3f4e26-c4db-48f7-b304-3c771cb68c68" />

### Client Management
- **Active Client Monitoring**: Track all connected clients in real-time
- **Per-Client Statistics**: Individual bandwidth usage and cache efficiency
- **Cache Hit/Miss Ratios**: Client-specific performance metrics
- **Historical Tracking**: Long-term client behavior analysis
- **Top Users Identification**: Identify heavy users and optimization opportunities
- **Client Grouping**: Organize clients by usage patterns

<img width="1510" height="1069" alt="Clients" src="https://github.com/user-attachments/assets/c27d4fae-bbfa-45df-8e40-641b8e08a2f9" />

### Management Panel
- **Authentication System**: Secure API key-based access control
- **Mock Data Mode**: Test interface with simulated data
- **Database Management**: Reset, backup, and maintain statistics
- **Cache Management**: Clear cache by service or entirely
- **Log Processing**: Process historical logs or monitor in real-time
- **Data Export**: Multiple export formats for integration
- **Theme Engine**: Complete UI customization system
- **System Diagnostics**: Health checks and permission verification

<img width="1512" height="1159" alt="Management-1" src="https://github.com/user-attachments/assets/5d394dc9-a73f-480c-9664-5825a135b478" />
<img width="1511" height="1226" alt="Management-2" src="https://github.com/user-attachments/assets/866d68ad-d60f-42e3-adfb-b70678bf6a68" />

## Installation

### Docker Compose (Recommended)

```yaml
version: '3.8'

services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    user: root  # Required for cache file access
    ports:
      - "8080:80"  # Map to your preferred port
    volumes:
      # Required: Your LANCache data directories
      - ./data:/data                        # Manager database and config
      - /path/to/lancache/logs:/logs:ro     # LANCache access logs (read-only)
      - /path/to/lancache/cache:/cache:ro   # LANCache cache directory (read-only)
    environment:
      # User/Group IDs
      - PUID=1006
      - PGID=1006
      - TZ=America/Chicago  # Your timezone

      # Core Configuration
      - ASPNETCORE_URLS=http://+:80
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache

      # Log Processing Options
      - LanCache__StartFromEndOfLog=true      # Start monitoring from current position

      # Performance Tuning (Optional)
      - LanCache__ChannelCapacity=100000      # Log processing queue size
      - LanCache__BatchSize=5000               # Batch processing size
      - LanCache__BatchTimeoutMs=500          # Batch timeout in milliseconds
      - LanCache__ConsumerCount=4             # Number of log consumers
      - LanCache__ParserParallelism=8         # Parallel parser threads
      - LanCache__UseHighThroughputMode=false # Enable for large deployments

      # Security Settings
      - Security__RequireAuthForMetrics=false # Require API key for Prometheus metrics
      - Security__EnableAuthentication=true   # Enable API authentication
```

### Manual Setup (Development)

#### Prerequisites
- .NET 8.0 SDK
- Node.js 18+ and npm
- SQLite support

#### Backend (API)
```bash
cd Api/LancacheManager
dotnet restore
dotnet build
dotnet run
# API runs on http://localhost:5000
```

#### Frontend (Web)
```bash
cd Web
npm install
npm run dev
# UI accessible at http://localhost:5173
```

#### Production Build
```bash
# Build API
cd Api/LancacheManager
dotnet publish -c Release -o ./publish

# Build Web UI
cd Web
npm run build
# Output in Web/dist directory
```

## Configuration

### Environment Variables

#### Required Settings
| Variable | Description | Default | Example |
|----------|-------------|---------|----------|
| `LanCache__LogPath` | Path to LANCache access.log | `/logs/access.log` | `/logs/access.log` |
| `LanCache__CachePath` | Path to LANCache cache directory | `/cache` | `/cache` |
| `ConnectionStrings__DefaultConnection` | SQLite database path | `/data/lancache.db` | `/data/lancache.db` |

#### Optional Settings
| Variable | Description | Default | Example |
|----------|-------------|---------|----------|
| `PUID` | User ID for file permissions | `1000` | `1006` |
| `PGID` | Group ID for file permissions | `1000` | `1006` |
| `TZ` | Timezone | `UTC` | `America/Chicago` |
| `LanCache__StartFromEndOfLog` | Start monitoring from current log position | `true` | `true` |
| `LanCache__ChannelCapacity` | Log processing queue size | `100000` | `200000` |
| `LanCache__BatchSize` | Batch processing size | `5000` | `10000` |
| `LanCache__BatchTimeoutMs` | Batch timeout in milliseconds | `500` | `1000` |
| `LanCache__ConsumerCount` | Number of log consumers | `4` | `8` |
| `LanCache__ParserParallelism` | Parallel parser threads | `8` | `16` |
| `LanCache__UseHighThroughputMode` | Enable high-throughput mode | `false` | `true` |

#### Security Settings
| Variable | Description | Default | Example |
|----------|-------------|---------|----------|
| `Security__EnableAuthentication` | Enable API authentication | `true` | `true` |
| `Security__RequireAuthForMetrics` | Require API key for metrics endpoints | `false` | `true` |
| `Security__ApiKeyPath` | Path to API key file | `/data/api_key.txt` | `/data/api_key.txt` |
| `Security__DevicesPath` | Path to device configurations | `/data/devices` | `/data/devices` |

### API Authentication

#### Initial Setup
1. On first startup, an API key is automatically generated
2. Find your API key using one of these methods:
   - Check container logs: `docker logs lancache-manager`
   - Read from file: `cat ./data/api_key.txt`
   - View in console output on startup

#### Using the API Key
1. Navigate to the **Management** tab in the web interface
2. Click the **Authenticate** button
3. Enter your API key
4. Management features are now enabled

#### API Key Format
API keys follow the format: `lm_[random-base64-string]`
Example: `lm_Abc123Def456Ghi789Jkl012Mno345`

#### Regenerating API Key
If you need to regenerate your API key:
1. Authenticate with current API key
2. Go to Management → Authentication
3. Click "Regenerate API Key"
4. Save the new key securely

## API Documentation

### Authentication
Protected endpoints require the `X-Api-Key` header with your API key.

```bash
curl -H "X-Api-Key: lm_your-api-key" http://localhost:8080/api/management/cache
```

### Public Endpoints

#### Dashboard & Statistics
- `GET /api/stats/dashboard` - Complete dashboard statistics
- `GET /api/stats/services` - Service-specific statistics
- `GET /api/stats/clients` - Client statistics
- `GET /api/downloads` - Active downloads list
- `GET /api/downloads/recent` - Recent download history

#### Metrics Export
- `GET /api/metrics` - Prometheus-formatted metrics
- `GET /api/metrics/prometheus` - Alternative Prometheus endpoint
- `GET /api/metrics/json` - JSON-formatted metrics
- `GET /api/metrics/grafana` - Grafana-compatible JSON

#### System Information
- `GET /api/management/cache` - Cache information
- `GET /api/management/config` - Current configuration
- `GET /api/management/processing-status` - Log processing status
- `GET /api/operationstate/{key}` - Check operation status

### Protected Endpoints (Require API Key)

#### Cache Management
- `POST /api/management/cache/clear-all` - Clear entire cache
- `DELETE /api/management/cache?service={service}` - Clear specific service cache
- `GET /api/management/cache/clear-status/{operationId}` - Check clear operation status
- `POST /api/management/cache/clear-cancel/{operationId}` - Cancel clear operation

#### Database Management
- `DELETE /api/management/database` - Reset database
- `POST /api/management/reset-logs?clearDatabase={bool}` - Reset log position

#### Log Processing
- `POST /api/management/process-all-logs` - Process entire log file
- `POST /api/management/cancel-processing` - Cancel log processing
- `POST /api/management/logs/remove-service` - Remove service from logs
- `GET /api/management/logs/service-counts` - Get log counts by service

#### Authentication
- `POST /api/auth/validate` - Validate API key
- `POST /api/auth/regenerate` - Generate new API key
- `GET /api/auth/device-status` - Check device authentication status

#### Diagnostics
- `GET /api/management/debug/permissions` - Check file permissions
- `GET /api/management/cache/active-operations` - List active operations

### WebSocket Endpoints
- `/hubs/downloads` - Real-time download updates via SignalR

## Grafana Integration

LANCache Manager provides comprehensive Prometheus metrics for advanced monitoring and visualization in Grafana.

### Quick Setup
1. Configure Prometheus to scrape `/api/metrics` endpoint
2. Add Prometheus as data source in Grafana
3. Import dashboard or create custom visualizations

### Available Metrics
- Service-level statistics (hit/miss rates, bandwidth)
- Client metrics (activity, usage patterns)
- Cache performance (size, efficiency, growth)
- System metrics (uptime, processing rates)

For detailed Grafana setup and dashboard examples, see the [Grafana Integration Guide](https://github.com/regix1/lancache-manager/blob/main/wiki/Grafana.md).

## Theme Customization

LANCache Manager includes a powerful theme engine allowing complete UI customization.

### Quick Start
1. Navigate to **Management → Theme Management**
2. Choose from built-in themes or create your own
3. Customize colors using the visual editor
4. Export/import themes as TOML files

### Theme Structure
Themes are TOML files containing:
- **Meta Information**: Name, author, version
- **Color Schemes**: Complete color palette
- **Service Colors**: Platform-specific colors
- **Chart Colors**: Data visualization colors
- **Custom CSS**: Additional styling (optional)

For detailed theming documentation, see the [Theming Guide](https://github.com/regix1/lancache-manager/blob/main/wiki/Theming.md).

### Community Themes
Browse pre-made themes in the [community-themes](https://github.com/regix1/lancache-manager/tree/main/community-themes) folder, including:
- **[Sage & Wood](https://github.com/regix1/lancache-manager/blob/main/community-themes/sage-wood.toml)**: Earthy sage green with warm wood browns
- More themes coming soon!

To use a community theme, import any `.toml` file from the folder via the Theme Manager.

## System Requirements

### Minimum Requirements
- LANCache server with access.log
- 2GB RAM
- 1GB disk space for database
- Docker or .NET 8.0 Runtime

### Recommended Requirements
- 4GB+ RAM for large cache deployments
- SSD storage for database
- Multi-core CPU for parallel log processing
- 10GB+ disk space for historical data

### Software Dependencies
- **Production**: Docker 20.10+ with Docker Compose
- **Development**:
  - .NET 8.0 SDK
  - Node.js 18+ with npm
  - SQLite 3.x

## Performance & Scalability

### Performance Features
- **High-Throughput Log Processing**: Handles 100,000+ log entries per second
- **Real-Time Updates**: WebSocket-based live data streaming
- **Efficient Storage**: SQLite with optimized indexes
- **Memory Caching**: In-memory statistics cache
- **Parallel Processing**: Multi-threaded log parsing
- **Batch Operations**: Efficient bulk data processing

### Optimization Tips
1. **Large Deployments**: Enable high-throughput mode
2. **Historical Processing**: Increase batch size and parser parallelism
3. **Memory Usage**: Adjust channel capacity based on available RAM
4. **Database Performance**: Regular VACUUM operations recommended

### Benchmarks
- Log Processing: ~50,000 entries/second (standard mode)
- Log Processing: ~150,000 entries/second (high-throughput mode)
- WebSocket Latency: <100ms for live updates
- API Response Time: <50ms for cached data

## Troubleshooting

### Common Issues

#### API Key Not Found
- Check container logs: `docker logs lancache-manager`
- Verify file exists: `ls -la ./data/api_key.txt`
- Check permissions on data directory

#### No Data Showing
- Verify log file path is correct
- Check if access.log has data: `tail -f /path/to/access.log`
- Ensure container has read permissions
- Try processing historical logs

#### Permission Errors
- Run container as root user or matching UID/GID
- Verify volume mount permissions
- Check Management → Diagnostics for permission status

#### High Memory Usage
- Reduce `LanCache__ChannelCapacity`
- Lower `LanCache__BatchSize`
- Decrease `LanCache__ConsumerCount`

### Getting Help

- **Bug Reports**: [GitHub Issues](https://github.com/regix1/lancache-manager/issues)
- **Documentation**: [Wiki Folder](https://github.com/regix1/lancache-manager/tree/main/wiki)
- **Themes**: [Community Themes](https://github.com/regix1/lancache-manager/tree/main/community-themes)
- **Community**: LANCache Discord Server

## Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Open a Pull Request

### Development Setup
```bash
# Clone repository
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Start development environment
docker-compose -f docker-compose.dev.yml up

# Or run manually
# Terminal 1: API
cd Api/LancacheManager
dotnet watch run

# Terminal 2: Web UI
cd Web
npm run dev
```

## License

MIT License - See LICENSE file for details
