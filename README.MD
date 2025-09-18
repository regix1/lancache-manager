# LanCache Manager

A modern, real-time monitoring interface for LanCache deployments. Track bandwidth usage, analyze cache usage, and manage your gaming content cache through an intuitive web interface.

<div align="center">

<img width="1513" height="1243" alt="LanCache Manager Dashboard" src="https://github.com/user-attachments/assets/ad649ed7-2be6-40b5-961f-01f74e8ab5f8" />

*Real-time dashboard showing live cache performance, bandwidth monitoring, and game download tracking*

</div>

## Screenshots

<div align="center">

### 📥 Download Monitoring
Track game downloads across different view modes

**Compact View**
<img width="1514" height="1013" alt="Downloads Compact View" src="https://github.com/user-attachments/assets/2f21d574-af3a-4657-819f-33a3de2035dc" />

**Normal View**
<img width="1524" height="815" alt="Downloads Normal View" src="https://github.com/user-attachments/assets/bac42c75-6ff0-4d13-ad58-1f95f7762784" />

**Grouped View**
<img width="1520" height="758" alt="Downloads Grouped View" src="https://github.com/user-attachments/assets/5f292a26-6cfe-4b46-91aa-605bc9c161d2" />

### 👥 Client Analytics
Analyze client usage patterns and performance

<img width="1534" height="1070" alt="Client Analytics" src="https://github.com/user-attachments/assets/f4b73c9a-d736-4623-be9d-ca6b906be628" />

### ⚙️ Management Interface
Configure cache settings and monitor system health

<img width="1521" height="1159" alt="Management Overview" src="https://github.com/user-attachments/assets/67cf91ce-545d-4139-9ba0-15e039e25498" />

<img width="1513" height="754" alt="Cache Management" src="https://github.com/user-attachments/assets/826af5c3-f8ee-47b8-992e-52f6c8884d1f" />

### 🎨 Theme Customization
Personalize your interface with custom themes

<img width="1519" height="1061" alt="Theme Manager" src="https://github.com/user-attachments/assets/7720781a-4a35-4f68-8b7e-1c196c8ac1ec" />

<img width="893" height="1120" alt="Theme Creator" src="https://github.com/user-attachments/assets/9b251b7b-0a8d-42ac-b646-bf7fd4d09a15" />

**Custom Theme Example - Sage Wood**
<img width="1518" height="1019" alt="Sage Wood Theme" src="https://github.com/user-attachments/assets/c2a90c2b-70d8-4e8f-83cb-d851c5a3063e" />

</div>

## Key Features

### Real-time Dashboard
- Live bandwidth monitoring with interactive charts
- Cache hit rate tracking and analysis
- Active download monitoring
- Client usage statistics
- Service breakdown (Steam, Epic, Origin, etc.)

### Smart Game Detection
- Automatic Steam game identification with cover art
- Steam depot mapping and App ID resolution
- Game download history with visual previews
- Filters out system and redistributable downloads

### Advanced Analytics
- Multiple time range filters (1h, 6h, 12h, 24h, 7d, 30d, all time, custom)
- Detailed download statistics with sorting and filtering
- Export data in JSON/CSV formats with applied filters
- Service-specific usage metrics

### Modern Interface
- Three view modes: Compact, Normal, and Grouped
- Mobile-responsive design with touch-friendly controls
- 20+ built-in themes with custom theme creation
- Smooth animations and transitions
- Dark and light theme support

### Management Tools
- Cache directory management and cleanup
- Log processing controls with real-time feedback
- Authentication and security settings

### Integrations
- Prometheus metrics endpoint for Grafana
- JSON API for custom integrations
- Real-time WebSocket updates
- Docker-friendly configuration

## Quick Start

### Prerequisites
- Docker and Docker Compose
- Existing LanCache deployment with accessible logs
- System Requirements:
  - Home (1-10 clients): 2GB RAM, 2 CPU cores
  - LAN Party (10-50 clients): 4GB RAM, 4 CPU cores
  - Large Event (50+ clients): 8GB+ RAM, 8+ CPU cores

### One-Line Setup

```bash
docker run -d \
  --name lancache-manager \
  -p 8080:80 \
  -v ./data:/data \
  -v /path/to/lancache/logs:/logs:ro \
  -v /path/to/lancache/cache:/cache:ro \
  -e TZ=America/Chicago \
  -e LanCache__LogPath=/logs/access.log \
  -e LanCache__CachePath=/cache \
  ghcr.io/regix1/lancache-manager:latest
```

Access your dashboard at `http://localhost:8080`

Replace `/path/to/lancache/` with your actual LanCache paths. The logs should point to your nginx access.log file.

## Configuration

### Docker Compose (Recommended)

Create a `docker-compose.yml` file:

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
      - /mnt/logs:/logs:ro
      - /mnt/cache/cache:/cache:ro
    environment:
      # System Configuration
      - PUID=1000
      - PGID=1000
      - TZ=America/Chicago
      - ASPNETCORE_URLS=http://+:80

      # LanCache Paths
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - LanCache__StartFromEndOfLog=true
      - LanCache__ProcessHistoricalLogs=false

      # Security Settings
      - Security__RequireAuthForMetrics=false  # Set to 'true' to require API key for Grafana
      - Security__EnableAuthentication=true    # Set to 'false' to disable management auth
```

Start with: `docker-compose up -d`

See the [Environment Variables Reference](wiki/DOCKER_ENVIRONMENT_REFERENCE.MD) for all configuration options.

## Authentication and Security

### First Run Setup
1. Start the container and check logs for your auto-generated API key:
   ```bash
   docker logs lancache-manager | grep "API Key"
   ```
2. Access the web interface at `http://localhost:8080`
3. Go to the Management tab and use the API key to access admin features

### Security Options
```yaml
environment:
  # Disable all authentication (local networks only)
  - Security__EnableAuthentication=false

  # Require API key for Grafana/Prometheus metrics
  - Security__RequireAuthForMetrics=true
```

### API Key Usage
For Grafana or custom integrations, add header:
```bash
curl -H "X-Api-Key: your-api-key-here" http://localhost:8080/api/metrics
```

## API Endpoints

### Metrics (Grafana/Prometheus)
```bash
# Prometheus format
curl http://localhost:8080/api/metrics

# JSON format
curl http://localhost:8080/api/metrics/json

# Check if authentication is required
curl http://localhost:8080/api/metrics/status
```

### Data Export
All data can be exported directly from the Downloads tab in JSON or CSV format with applied filters.

## User Interface Guide

### Dashboard Tab
- Real-time Stats: Bandwidth, cache hit rates, active downloads
- Interactive Charts: Click and drag to zoom, hover for details
- Custom Cards: Drag and drop to rearrange, hide/show stats
- Time Filters: Switch between time ranges instantly

### Downloads Tab
- View Modes: Switch between Compact, Normal, and Grouped views
- Game Detection: Automatic Steam game identification with cover art
- Advanced Filters: Filter by service, time, size, hide localhost
- Data Export: Export filtered data as JSON or CSV
- Mobile Responsive: Touch-friendly interface on all devices

### Management Tab
- Authentication: Manage API keys and security settings
- Cache Management: Clean and organize cache directories
- Logs: Monitor log processing status
- Themes: Customize appearance with 20+ built-in themes
- Grafana: Get metrics endpoints for external monitoring

## Development

### Tech Stack
- **Backend**: .NET 8, ASP.NET Core, SignalR, Entity Framework, SQLite
- **Frontend**: React 18, TypeScript, Vite, Tailwind CSS
- **Charts**: Recharts with custom animations
- **Real-time**: SignalR WebSocket connections
- **Containerization**: Docker with multi-stage builds

### Building from Source

```bash
# Clone repository
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Build frontend
cd Web
npm install
npm run build

# Build backend
cd ../Api/LancacheManager
dotnet build

# Build Docker image
cd ../..
docker build -t lancache-manager .
```

### Development Mode
```bash
# Run backend (http://localhost:5000)
cd Api/LancacheManager
dotnet run

# Run frontend with hot reload (http://localhost:3000)
cd Web
npm run dev
```

## Documentation

- [Quick Start Guide](wiki/README.MD) - Get up and running in minutes
- [Environment Variables](wiki/DOCKER_ENVIRONMENT_REFERENCE.MD) - Complete configuration reference
- [Grafana Integration](wiki/GRAFANA.MD) - Set up monitoring dashboards
- [Custom Themes](wiki/THEMING.MD) - Create and share custom themes
- [Community Themes](community-themes/README.MD) - Download community themes

## Contributing

We welcome contributions! Whether it's:
- Bug reports and fixes
- New features and improvements
- Custom themes
- Documentation improvements
- Feature improvements

## License

MIT License - feel free to use in personal and commercial projects.

---

<div align="center">

Star this repo if LanCache Manager helped you!

[Documentation](wiki/) • [Issues](../../issues) • [Discussions](../../discussions) • [Themes](community-themes/)

</div>
