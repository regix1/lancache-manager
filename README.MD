# LANCache Manager

A modern web interface for monitoring and managing your LANCache deployment. Track downloads, identify games, and monitor bandwidth savings in real-time.

## IMPORTANT: Docker Installation

> [!IMPORTANT]
> **Always use the `latest` tag for stable releases:**
>
> ```bash
> docker pull ghcr.io/regix1/lancache-manager:latest
> ```
>
> GitHub's package page may show `:dev` in the installation command because dev builds are pushed more frequently. **The `:dev` tag is unstable development code.**
>
> **Use `:latest` for production. Use `:dev` only for bleeding-edge features.**

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

## Quick Start

### Basic Docker Run

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

### Complete Example with All Options

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    ports:
      - "8080:80"
    environment:
      # Timezone (default: UTC)
      TZ: America/Chicago

      # Required: Path to LANCache nginx access.log
      LanCache__LogPath: /logs/access.log

      # Required: Path to LANCache cache directory
      LanCache__CachePath: /cache

      # Optional: Start reading logs from end (default: true)
      # Set to false to process entire log on first run
      LanCache__StartFromEndOfLog: "true"

      # Optional: Require API key for /metrics endpoint (default: false)
      # Enable this if you want to protect Prometheus metrics
      Security__RequireAuthForMetrics: "false"

      # Optional: Enable authentication for Management tab (default: true)
      # Set to false to disable authentication (not recommended)
      Security__EnableAuthentication: "true"

      # Optional: Custom path for API key file (default: /data/api_key.txt)
      # Security__ApiKeyPath: /data/custom_api_key.txt

      # Optional: Custom path for device registrations (default: /data/devices)
      # Security__DevicesPath: /data/custom_devices
    volumes:
      # Required: Persistent data (database, API key, themes)
      - ./data:/data

      # Required: LANCache logs (read-only for security)
      - /path/to/lancache/logs:/logs:ro

      # Optional: Cache directory for size monitoring
      # Use read-write if you want cache clearing functionality
      - /path/to/lancache/cache:/cache:ro
    restart: unless-stopped
```

Visit **http://localhost:8080** and get your **API key** from the logs:

```bash
docker logs lancache-manager | grep "API Key"
```

**API key is also saved to `/data/api_key.txt`**

## Features

**Dashboard** - Bandwidth usage, cache hit rates, top games, service breakdown

**Downloads** - Detailed sessions with game names, sizes, client IPs, speeds

**Clients** - Per-device usage tracking

**Management** - Process logs, clear cache, manage depot mappings, upload themes (requires **API key**)

**Themes** - Built-in themes or create custom TOML themes with 100+ color variables

**API** - REST endpoints and **`/metrics`** for Prometheus

## Custom Themes

Upload TOML files through **Management > Themes**:

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

Export any built-in theme to see all available variables.

## Depot Mappings

Includes auto-updated mappings from **[lancache-pics](https://github.com/regix1/lancache-pics)** (updated every 2 days). No setup required.

Add custom mappings in **Management > Depot Mappings** for unrecognized games, private depots, or beta branches.

You can provide your own JSON file following the lancache-pics format.

## API Examples

Stats endpoints are **public**. Management endpoints require **`X-Api-Key` header**.

```bash
# Dashboard stats
curl http://localhost:8080/api/stats/dashboard?period=24h

# Top games
curl http://localhost:8080/api/stats/top-games?limit=10

# Process logs (requires API key)
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/process-all-logs

# Prometheus metrics (public by default)
curl http://localhost:8080/metrics
```

Set **`Security__RequireAuthForMetrics=true`** to protect the `/metrics` endpoint.

## Building from Source

**Requirements:** .NET 8 SDK, Node.js 20+, Rust 1.75+

```bash
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager/rust-processor
cargo build --release

cd ../Web
npm install
npm run dev  # http://localhost:3000

cd ../Api/LancacheManager
dotnet run  # http://localhost:5000
```

**Docker build:**

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push \
  .
```

## Troubleshooting

**Logs not processing?** Check log path in **Management > Settings** and verify volume mount permissions.

**Games not identified?** Add custom mappings in **Management > Depot Mappings**, then reprocess logs.

**Lost API key?** Check **`/data/api_key.txt`** or run:
```bash
docker logs lancache-manager | grep "API Key"
```
Delete `api_key.txt` and restart to generate a new one.

## License

MIT License
