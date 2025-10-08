# LANCache Manager

A modern web interface for monitoring and managing your LANCache deployment. Track downloads, see what games your clients are pulling, and monitor bandwidth savings in real-time.

## IMPORTANT: Docker Installation

**Always use the `latest` tag for stable releases:**

```bash
docker pull ghcr.io/regix1/lancache-manager:latest
```

GitHub's package page may show `:dev` in the installation command, but this is because development builds are pushed more frequently. The `:dev` tag contains the latest development code from the main branch and may be unstable.

**Use `:latest` for production deployments. Use `:dev` only if you want bleeding-edge features and accept potential instability.**

## What It Does

LANCache Manager gives you visibility into your LANCache deployment. It processes your nginx access logs to show you real-time bandwidth statistics, identifies which games are being downloaded, tracks per-client usage, and calculates how much bandwidth you're saving. The web interface is clean and customizable with theme support, and everything runs in a single Docker container.

The log processor is written in Rust for performance - it can process millions of log entries in just a few minutes. It automatically detects your nginx log format, groups downloads into sessions, and identifies Steam games using depot mappings from SteamDB.

## Quick Start

### Docker Run

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
    volumes:
      - ./data:/data
      - /path/to/lancache/logs:/logs:ro
      - /path/to/lancache/cache:/cache:ro
    restart: unless-stopped
```

After starting the container, visit `http://localhost:8080` and check the logs for your API key:

```bash
docker logs lancache-manager | grep "API Key"
```

You'll need the API key to access the Management tab where you can process logs, manage depot mappings, customize themes, and configure settings.

## Configuration

The application uses environment variables for configuration. Only a few are required:

| Variable | Description | Default |
|----------|-------------|---------|
| `LanCache__LogPath` | Path to your nginx access.log file | `/logs/access.log` |
| `LanCache__CachePath` | Path to your cache directory (for size monitoring) | `/cache` |
| `TZ` | Your timezone for accurate timestamps | `UTC` |
| `Security__RequireAuthForMetrics` | Require API key for the `/metrics` endpoint | `false` |
| `Security__EnableAuthentication` | Enable authentication for the Management tab | `true` |

The `/data` volume persists your database, API key, and custom themes across container restarts. The `/logs` and `/cache` mounts should be read-only.

## Features

**Dashboard** - See bandwidth usage, cache hit rates, top games, and service breakdown at a glance. Filter by time period to analyze usage patterns.

**Downloads** - View detailed download sessions with game names, file sizes, client IPs, and download speeds. Sessions are automatically grouped by client activity.

**Clients** - Track which devices are using your cache and how much bandwidth each one is consuming.

**Management** - Process logs manually or let the automatic processor run periodically. Clear cache for specific services, reset the database, manage custom depot mappings, and upload custom themes. Everything requires API key authentication.

**Themes** - Choose from built-in themes or create your own using TOML format. Export any theme, modify it, and upload it back. Toggle between rounded and sharp corners.

**API & Metrics** - REST API for programmatic access and a `/metrics` endpoint in Prometheus format for Grafana dashboards.

## Custom Themes

Themes are TOML files with color variables. Here's a minimal example:

```toml
[meta]
name = "My Theme"
id = "my-theme"
isDark = true

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
textPrimary = "#ffffff"
```

Upload themes through Management > Themes. Export any built-in theme to see all available color variables (there are over 100 for complete customization).

## Depot Mappings

The application uses Steam depot mappings from SteamDB to identify games. If a game isn't recognized, you can add custom mappings in Management > Depot Mappings. This is useful for games not in the SteamDB database, private depots, or beta branches.

After adding new mappings, you can reprocess existing logs to update the game names in your statistics.

## API Examples

All stats endpoints are public. Management endpoints require the `X-Api-Key` header.

```bash
# Get dashboard stats
curl http://localhost:8080/api/stats/dashboard?period=24h

# Get top games
curl http://localhost:8080/api/stats/top-games?limit=10

# Process logs (requires API key)
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/process-all-logs

# Prometheus metrics (public by default)
curl http://localhost:8080/metrics
```

To require authentication for the `/metrics` endpoint, set `Security__RequireAuthForMetrics=true`.

## Performance

The Rust log processor is fast. Processing 4 million log entries takes 3-5 minutes on typical hardware, compared to 25+ minutes with the original C# implementation. This is thanks to bulk transactions, prepared statements, SQLite WAL mode, and zero-copy regex parsing.

For resource planning:
- Home use (1-10 clients): 2 CPU cores, 2GB RAM
- Small LAN (10-25 clients): 2 cores, 4GB RAM
- Medium LAN (25-50 clients): 4 cores, 4GB RAM
- Large events (50+ clients): 8+ cores, 8GB RAM

Database size depends on activity level. Expect 50-100MB per month for light usage, 500MB-1GB for medium usage, and 2-5GB for heavy usage.

## Building from Source

Requires .NET 8 SDK, Node.js 20+, and Rust 1.75+.

```bash
# Clone and build Rust processor
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager/rust-processor
cargo build --release

# Build frontend
cd ../Web
npm install
npm run dev  # http://localhost:3000

# Build and run backend (in another terminal)
cd ../Api/LancacheManager
dotnet run  # http://localhost:5000
```

For Docker builds:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push \
  .
```

## Troubleshooting

**Logs not processing?** Check that the log path is correct in Management > Settings and that the log file is readable (verify volume mount permissions).

**Games not identified?** Add custom depot mappings in Management > Depot Mappings, then reprocess logs.

**Lost API key?** Check `/data/api_key.txt` in your data volume, or view container logs: `docker logs lancache-manager | grep "API Key"`. If you can't find it, delete `api_key.txt` and restart the container to generate a new one.

## Contributing

Contributions are welcome. Open issues for bugs or feature requests, submit PRs for fixes or enhancements, and share custom themes in discussions.

## License

MIT License - free for personal and commercial use.
