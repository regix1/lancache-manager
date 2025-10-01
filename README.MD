# LanCache Manager

Modern monitoring and management interface for LanCache deployments. Keep tabs on downloads, bandwidth savings, and cache performance in real-time.


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

- Real-time bandwidth and cache hit tracking
- Automatic game detection with Steam depot mapping
- Service breakdown (Steam, Epic, Origin, Blizzard, etc.)
- Custom theme support with TOML-based themes
- REST API and Prometheus metrics export
- Authentication and multi-user support

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
      LanCache__ApiKey: your-secret-key-here
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
| `LanCache__LogPath` | Path to nginx access.log | `/logs/access.log` |
| `LanCache__CachePath` | Cache storage directory | `/cache` |
| `LanCache__ApiKey` | API authentication key | None (public) |
| `TZ` | Timezone | `UTC` |

### Authentication

Set `LanCache__ApiKey` to enable authentication for:
- Cache clearing operations
- Database reset
- Theme management
- API endpoints

Without an API key, the dashboard is read-only with limited management features.

## Building from Source

### Prerequisites

- .NET 8 SDK
- Node.js 20+
- Rust 1.75+ (for log processor)
- Docker (optional)

### Local Development

```bash
# Frontend
cd Web
npm install
npm run dev

# Backend
cd Api/LancacheManager
dotnet run
```

Frontend runs on `http://localhost:3000`, backend on `http://localhost:5000`.

### Docker Build

```bash
# Single platform
docker build -t lancache-manager .

# Multi-platform (amd64 + arm64)
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push \
  .
```

The Dockerfile uses multi-stage builds to compile Rust binaries for both platforms, build the React frontend, and package everything into an ASP.NET runtime image.

### Platform Support

- **Linux**: x64 (amd64), ARM64
- **Windows**: x64 (development only)

### Building Rust Processor

```bash
cd rust-processor
cargo build --release
```

For cross-compilation:

```bash
# Linux x64
cargo build --release --target x86_64-unknown-linux-gnu

# Linux ARM64 (requires aarch64 toolchain)
cargo build --release --target aarch64-unknown-linux-gnu

# Windows (from Linux, requires mingw-w64)
cargo build --release --target x86_64-pc-windows-gnu
```

Built binaries are automatically copied to `rust-processor/bin/<platform>/` and included in Docker builds.

### Production Build

```bash
# .NET publish (includes Rust binaries)
cd Api/LancacheManager
dotnet publish -c Release -r linux-x64 --self-contained false

# Frontend production build
cd Web
npm run build
```

Output goes to `Api/LancacheManager/bin/Release/net8.0/linux-x64/publish/`

## Custom Themes

Themes use TOML format with full color customization. Upload via Settings > Themes.

### Theme Structure

```toml
[meta]
name = "My Theme"
id = "my-theme"
description = "Custom color scheme"
author = "Your Name"
version = "1.0.0"
isDark = true

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
textPrimary = "#ffffff"
# ... (see built-in themes for full list)

[css]
content = """
/* Optional custom CSS */
.custom-class {
  font-weight: bold;
}
"""
```

### Available Colors

Themes support 100+ color variables including:
- Core colors (primary, secondary, accent)
- Backgrounds (primary, secondary, tertiary, hover)
- Text colors (primary, secondary, muted, accent)
- Status colors (success, warning, error, info)
- Service colors (steam, epic, origin, blizzard, wsus, riot)
- Component colors (buttons, inputs, cards, badges, etc.)
- Chart colors (8 data series + grid/text colors)

### Sharp Corners Mode

Toggle between rounded and sharp corners globally in Settings > Appearance.

### Exporting Themes

Select any theme and click Export to download as `.toml` file. Modify and re-upload to customize.

## API Endpoints

### Metrics

```bash
# Prometheus format
curl http://localhost:8080/api/metrics

# JSON format
curl http://localhost:8080/api/metrics/json
```

### Management (requires API key)

```bash
# Process logs
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/process-all-logs

# Reset database
curl -X DELETE -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/database

# Clear cache
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/cache/clear-all
```

### Stats

```bash
# Dashboard stats
curl http://localhost:8080/api/stats/dashboard?period=24h

# Top games
curl http://localhost:8080/api/stats/top-games?limit=10

# Client stats
curl http://localhost:8080/api/stats/clients

# Service stats
curl http://localhost:8080/api/stats/services
```

## Performance

### Recommended Resources

| Environment | CPU | RAM | Notes |
|-------------|-----|-----|-------|
| Home (1-10 clients) | 2 cores | 2 GB | Light usage |
| LAN party (10-50 clients) | 4 cores | 4 GB | Medium load |
| Large events (50+ clients) | 8+ cores | 8+ GB | High throughput |

### Log Processor Performance

Processing 4M log entries:

| Implementation | Time | Throughput |
|----------------|------|------------|
| C# (EF Core) | ~25 min | 2,666 lines/sec |
| Rust (rusqlite) | 3-5 min | 13,333-22,222 lines/sec |

**5-8x faster** with:
- Bulk transactions (100k entries per commit)
- Prepared statements and query caching
- SQLite WAL mode with 1GB cache
- Session-based download grouping (5min timeout)
- Zero-copy regex parsing

## Contributing

- Open issues for bugs or feature requests
- Share custom themes in discussions
- Submit PRs for fixes or enhancements

## License

MIT License - free for personal and commercial use.
