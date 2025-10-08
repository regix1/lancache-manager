# LANCache Manager

Modern monitoring and management interface for LANCache deployments. Keep tabs on downloads, bandwidth savings, and cache performance in real-time.

## IMPORTANT: Docker Installation

**Always use the `latest` tag for stable releases:**

```bash
docker pull ghcr.io/regix1/lancache-manager:latest
```

GitHub's package page may show `:dev` in the installation command, but this is because development builds are pushed more frequently. The `:dev` tag contains the latest development code from the main branch and may be unstable.

**Use `:latest` for production deployments. Use `:dev` only if you want bleeding-edge features and accept potential instability.**

## Screenshots

<div align="center">

### Dashboard
<img width="1527" alt="Dashboard Overview" src="https://github.com/user-attachments/assets/56b5a0bb-066a-437e-8773-115e6687ddcb" />

### Downloads
<img width="1521" alt="Downloads Tab - Overview" src="https://github.com/user-attachments/assets/8d3421e7-1aac-4172-8eaf-631b697ae7c0" />
<img width="1520" alt="Downloads Tab - Detailed View" src="https://github.com/user-attachments/assets/c8d82fbb-61b2-4ee5-8cd7-33b62fcabfab" />

### Clients
<img width="1522" alt="Client Statistics" src="https://github.com/user-attachments/assets/b591f36f-dbdd-4373-ab5e-e717b0893ebc" />

### Management
<img width="1521" alt="Management Tab - Settings" src="https://github.com/user-attachments/assets/d86ed2d0-13db-4031-8bfd-2bc637179041" />
<img width="1516" alt="Management Tab - Advanced" src="https://github.com/user-attachments/assets/38300c13-b9c6-46e6-b06d-efe2d857ed6c" />

### Themes
<img width="1512" alt="Custom Theme Editor" src="https://github.com/user-attachments/assets/c0628129-12ef-4ff6-a30e-979f40804a57" />

</div>

## Features

### Core Monitoring
- Real-time bandwidth tracking and cache hit statistics
- Automatic game detection with Steam depot mapping
- Service breakdown (Steam, Epic, Origin, Blizzard, Riot, WSUS, and more)
- Per-client download tracking and statistics
- Download session grouping with configurable timeouts
- Comprehensive bandwidth savings calculations

### Log Processing
- High-performance Rust-based log processor (5-8x faster than traditional implementations)
- Automatic log format detection supporting multiple nginx access log formats
- Custom depot mapping for game identification
- Scheduled log processing with configurable intervals
- Manual log processing with real-time progress tracking
- Log retention management with automatic cleanup

### Management Features
- Web-based settings management with API key authentication
- Database reset and maintenance operations
- Cache clearing for specific services or all cached data
- Depot mapping editor for custom Steam game identification
- Log path configuration and validation
- Processing schedule configuration

### Customization
- Custom theme support with TOML-based themes
- 100+ customizable color variables per theme
- Sharp corners mode toggle for interface styling
- Theme import and export functionality
- Built-in theme presets

### Integration
- REST API for programmatic access
- Prometheus metrics export for monitoring integration
- Optional authentication for metrics endpoint
- Multi-user support with persistent API keys

## Quick Start

### Docker (Recommended)

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

Visit `http://localhost:8080`

### Docker Compose

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    ports:
      - "8080:80"
    environment:
      TZ: America/Chicago
      LanCache__LogPath: /logs/access.log
      LanCache__CachePath: /cache
      # Optional: Require API key for metrics endpoint
      # Security__RequireAuthForMetrics: "true"
    volumes:
      - ./data:/data
      - /path/to/lancache/logs:/logs:ro
      - /path/to/lancache/cache:/cache:ro
    restart: unless-stopped
```

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `LanCache__LogPath` | Path to nginx access.log file | `/logs/access.log` |
| `LanCache__CachePath` | Cache storage directory for monitoring | `/cache` |
| `TZ` | Timezone for log timestamps | `UTC` |
| `Security__RequireAuthForMetrics` | Require API key for `/metrics` endpoint | `false` |
| `Security__EnableAuthentication` | Enable authentication for Management tab | `true` |
| `Processing__EnableScheduledProcessing` | Enable automatic log processing | `true` |
| `Processing__ScheduleIntervalMinutes` | Minutes between automatic processing runs | `5` |
| `Processing__SessionTimeoutMinutes` | Minutes of inactivity to end a download session | `5` |

### Volume Mounts

| Path | Purpose | Required |
|------|---------|----------|
| `/data` | Persistent storage for database, API key, themes | Yes |
| `/logs` | LANCache nginx access logs (read-only) | Yes |
| `/cache` | LANCache cache directory for size monitoring (read-only) | Optional |

### Authentication

An API key is automatically generated on first startup and saved to `/data/api_key.txt`. The key is displayed in the application logs during startup and can be found in:
- Docker logs: `docker logs lancache-manager`
- Management tab Settings page in the web UI
- File: `/data/api_key.txt` inside the container

The API key is required for:
- All Management tab operations
- Cache clearing operations
- Database reset and maintenance
- Theme management (upload, delete)
- Depot mapping management
- Log processing operations
- Protected API endpoints
- `/metrics` endpoint (when `Security__RequireAuthForMetrics=true`)

The key persists across container restarts via the `/data` volume mount.

#### Securing Metrics Endpoint

By default, the `/metrics` endpoint is public for easy Prometheus and Grafana integration. To require API key authentication:

```yaml
environment:
  Security__RequireAuthForMetrics: "true"
```

Then configure Prometheus with the API key:

```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    metrics_path: '/metrics'
    headers:
      X-Api-Key: 'your-api-key-here'
```

## Log Processing

LANCache Manager uses a high-performance Rust-based log processor that automatically detects your nginx access log format and processes entries efficiently.

### Automatic Processing

By default, logs are processed automatically every 5 minutes. Configure the interval in Management > Settings:

```yaml
environment:
  Processing__EnableScheduledProcessing: "true"
  Processing__ScheduleIntervalMinutes: "5"
```

Set `EnableScheduledProcessing` to `false` to disable automatic processing and only process logs manually.

### Manual Processing

Use the Management tab to manually trigger log processing with real-time progress tracking. The processor will:

1. Automatically detect your nginx log format
2. Parse log entries with zero-copy regex operations
3. Group downloads into sessions based on activity timeout
4. Identify games using Steam depot mappings
5. Calculate bandwidth and cache hit statistics

### Log Format Support

The processor automatically detects and supports multiple nginx access log formats, including:
- Standard combined format
- Custom formats with millisecond timestamps
- Formats with upstream cache status
- Formats with additional timing information

No configuration needed - the processor adapts to your log format automatically.

### Custom Depot Mappings

Use the Depot Mapping Manager in the Management tab to add custom Steam depot to game mappings. This is useful for:
- Games not in the default SteamDB mappings
- Private or beta depots
- Custom game identification

Changes to depot mappings are applied to future log processing runs. To reprocess existing logs with new mappings, reset the database and reprocess all logs.

## Custom Themes

Themes use TOML format with full color customization. Create and upload themes via Management > Themes.

### Theme Structure

```toml
[meta]
name = "My Custom Theme"
id = "my-theme"
description = "A custom color scheme for LANCache Manager"
author = "Your Name"
version = "1.0.0"
isDark = true

[colors]
# Primary colors
primaryColor = "#3b82f6"
secondaryColor = "#6366f1"
accentColor = "#8b5cf6"

# Backgrounds
bgPrimary = "#111827"
bgSecondary = "#1f2937"
bgTertiary = "#374151"

# Text colors
textPrimary = "#ffffff"
textSecondary = "#d1d5db"
textMuted = "#9ca3af"

# Status colors
successColor = "#10b981"
warningColor = "#f59e0b"
errorColor = "#ef4444"
infoColor = "#3b82f6"

# Service colors
steamColor = "#1b2838"
epicColor = "#2a2a2a"
originColor = "#f56c2d"
blizzardColor = "#148eff"
wsusColor = "#00a4ef"
riotColor = "#d13639"

# Chart colors
chartColor1 = "#3b82f6"
chartColor2 = "#10b981"
chartColor3 = "#f59e0b"
chartColor4 = "#ef4444"
chartColor5 = "#8b5cf6"
chartColor6 = "#ec4899"
chartColor7 = "#06b6d4"
chartColor8 = "#84cc16"

# ... (see built-in themes for complete list)

[css]
content = """
/* Optional custom CSS */
.custom-element {
  font-weight: bold;
  text-transform: uppercase;
}
"""
```

### Available Color Variables

Themes support over 100 color variables including:
- **Core colors**: primary, secondary, accent, highlight
- **Backgrounds**: primary, secondary, tertiary, quaternary, hover states
- **Text**: primary, secondary, muted, accent, inverse
- **Status indicators**: success, warning, error, info, pending
- **Service branding**: Steam, Epic, Origin, Blizzard, WSUS, Riot, PlayStation, Nintendo, Xbox
- **Components**: buttons, inputs, cards, badges, tooltips, modals, tabs
- **Charts**: 8 data series colors plus grid and text colors
- **Borders**: primary, secondary, focus, hover states

### Sharp Corners Mode

Toggle between rounded and sharp corners globally in Management > Appearance. This preference is saved per-browser and persists across sessions.

### Exporting Themes

Select any theme and click Export to download as a `.toml` file. Modify the exported theme and re-upload to create custom variations.

## API Endpoints

### Metrics

```bash
# Prometheus format metrics
curl http://localhost:8080/metrics

# With authentication (if enabled)
curl -H "X-Api-Key: your-key" http://localhost:8080/metrics
```

### Stats (Public)

```bash
# Dashboard statistics
curl http://localhost:8080/api/stats/dashboard?period=24h

# Top games
curl http://localhost:8080/api/stats/top-games?limit=10&period=7d

# Client statistics
curl http://localhost:8080/api/stats/clients?period=30d

# Service breakdown
curl http://localhost:8080/api/stats/services?period=24h

# Download sessions
curl http://localhost:8080/api/stats/downloads?limit=50
```

### Management (Requires API Key)

```bash
# Process logs manually
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/process-all-logs

# Check processing status
curl -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/processing-status

# Reset database
curl -X DELETE -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/database

# Clear all cache
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/cache/clear-all

# Clear specific service cache
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/cache/clear/steam

# Get depot mappings
curl -H "X-Api-Key: your-key" \
  http://localhost:8080/api/depot-mapping

# Add custom depot mapping
curl -X POST -H "X-Api-Key: your-key" \
  -H "Content-Type: application/json" \
  -d '{"depotId": 123456, "gameName": "My Game", "source": "custom"}' \
  http://localhost:8080/api/depot-mapping
```

## Performance

### Recommended Resources

| Environment | CPU | RAM | Storage | Notes |
|-------------|-----|-----|---------|-------|
| Home (1-10 clients) | 2 cores | 2 GB | 10 GB | Light usage, infrequent processing |
| Small LAN (10-25 clients) | 2 cores | 4 GB | 20 GB | Regular downloads, hourly processing |
| Medium LAN (25-50 clients) | 4 cores | 4 GB | 50 GB | Active usage, frequent processing |
| Large events (50+ clients) | 8+ cores | 8+ GB | 100+ GB | High throughput, constant activity |

### Log Processor Performance

Processing 4 million log entries on typical hardware:

| Implementation | Processing Time | Throughput | Database Operations |
|----------------|-----------------|------------|---------------------|
| C# (EF Core) | ~25 minutes | 2,666 lines/sec | Individual inserts |
| Rust (rusqlite) | 3-5 minutes | 13,333-22,222 lines/sec | Bulk transactions |

**Performance Optimizations:**
- Bulk transactions (100,000 entries per commit)
- Prepared statements with query caching
- SQLite WAL mode with 1GB page cache
- Session-based download grouping (configurable timeout)
- Zero-copy regex parsing for log entries
- Concurrent processing where possible

### Database Growth

Approximate database size based on cache activity:

| Activity Level | Entries/Day | Database Growth/Month | Notes |
|----------------|-------------|----------------------|-------|
| Low | 10,000 | 50-100 MB | Few clients, occasional downloads |
| Medium | 100,000 | 500 MB - 1 GB | Regular usage, multiple clients |
| High | 500,000+ | 2-5 GB | Busy LANCache, many clients |

Regular database maintenance and old entry cleanup can be managed through the Management tab.

## Building from Source

### Prerequisites

- .NET 8 SDK
- Node.js 20+
- Rust 1.75+ (for log processor)
- Docker (optional, for containerized builds)

### Local Development

```bash
# Clone repository
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Build Rust log processor
cd rust-processor
cargo build --release
cd ..

# Build frontend
cd Web
npm install
npm run dev  # Runs on http://localhost:3000

# In another terminal, build and run backend
cd Api/LancacheManager
dotnet run   # Runs on http://localhost:5000
```

Frontend development server proxies API requests to the backend automatically.

### Docker Build

```bash
# Single platform build
docker build -t lancache-manager .

# Multi-platform build (amd64 + arm64)
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push \
  .
```

The Dockerfile uses multi-stage builds to:
1. Compile Rust binaries for target platforms
2. Build the React frontend with Vite
3. Build the .NET backend
4. Package everything into an optimized ASP.NET runtime image

### Platform Support

**Supported Platforms:**
- Linux: x86_64 (amd64), ARM64 (aarch64)
- Windows: x86_64 (development and testing only)

Docker images are available for both amd64 and arm64 architectures, allowing deployment on x86 servers, Raspberry Pi 4/5, and ARM-based cloud instances.

### Building Rust Processor

The log processor is written in Rust for maximum performance:

```bash
cd rust-processor

# Build for current platform
cargo build --release

# Cross-compile for other platforms
cargo build --release --target x86_64-unknown-linux-gnu
cargo build --release --target aarch64-unknown-linux-gnu
cargo build --release --target x86_64-pc-windows-gnu
```

Built binaries are automatically copied to `rust-processor/bin/<platform>/` and included in Docker builds.

### Production Build

```bash
# Backend production build
cd Api/LancacheManager
dotnet publish -c Release -r linux-x64 --self-contained false -o ./publish

# Frontend production build
cd Web
npm run build  # Output: dist/

# Package for deployment
# The published backend includes the frontend dist/ automatically
```

## Troubleshooting

### Logs Not Processing

1. Verify log path is correct in Management > Settings
2. Ensure log file is readable (check volume mount permissions)
3. Check application logs for format detection errors
4. Try manual processing to see detailed error messages

### Games Not Identified

1. Check if depot mappings exist for the game
2. Add custom depot mapping in Management > Depot Mappings
3. Reprocess logs after adding new mappings

### High Memory Usage

1. Reduce processing schedule frequency
2. Decrease session timeout to reduce active session tracking
3. Clear old data from database periodically
4. Consider increasing system resources for large deployments

### API Key Lost

1. Check `/data/api_key.txt` in the container or data volume
2. View container logs from startup: `docker logs lancache-manager | grep "API Key"`
3. If lost, delete `/data/api_key.txt` and restart container to generate new key

## Contributing

Contributions are welcome and appreciated:

- **Bug Reports**: Open an issue with detailed reproduction steps
- **Feature Requests**: Describe the use case and desired functionality
- **Custom Themes**: Share your themes in GitHub Discussions
- **Pull Requests**: Submit fixes or enhancements with clear descriptions
- **Documentation**: Help improve setup guides and troubleshooting

Please ensure PRs include:
- Clear description of changes
- Testing on both amd64 and arm64 if platform-specific
- Updated documentation if adding features

## License

MIT License - free for personal and commercial use.

## Acknowledgments

- Steam depot data sourced from SteamDB
- Built with ASP.NET Core, React, and Rust
- Designed for LANCache by lancache.net community
