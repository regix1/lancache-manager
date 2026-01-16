# LANCache Manager

A web interface for monitoring and managing your [LANCache](https://lancache.net/). Track downloads in real-time, identify which games are being cached, measure bandwidth savings, and prefill your cache before LAN parties.

-----

## Docker Installation

> [!IMPORTANT]
> **Always use the `latest` tag:**
>
> ```bash
> docker pull ghcr.io/regix1/lancache-manager:latest
> ```
>
> GitHub's package page shows `:dev` in the installation command because dev builds are pushed more frequently. The `:dev` tag is unstable and intended for testing only.

-----

## Table of Contents

- [Screenshots](#screenshots)
- [Features](#features)
- [Quick Start](#quick-start)
- [Docker Compose Reference](#docker-compose-reference)
- [Configuration Options](#configuration-options)
- [Tabs Overview](#tabs-overview)
- [Steam Prefill](#steam-prefill)
- [Custom Themes](#custom-themes)
- [Grafana & Prometheus](#grafana--prometheus)
- [Multiple Datasources](#multiple-datasources)
- [Nginx Reverse Proxy](#nginx-reverse-proxy)
- [Troubleshooting](#troubleshooting)
- [Building from Source](#building-from-source)
- [Contributing Translations](#contributing-translations)
- [Need Help?](#need-help)

-----

<a id="screenshots"></a>
## Screenshots

<div align="center">

### Dashboard
<img width="1517" height="1150" alt="Dashboard Overview" src="https://github.com/user-attachments/assets/a9a7e9a5-ca5d-44e0-ad80-fac163782ee9" />
<img width="1526" height="988" alt="Dashboard Charts" src="https://github.com/user-attachments/assets/695c61cc-f2cd-4bc5-bbed-a5f7e6b0f11c" />

*Stats at a glance with draggable cards and time range filtering*

### Downloads

**Normal View**
<img width="1517" height="1236" alt="Downloads - Normal View" src="https://github.com/user-attachments/assets/29dbd4ba-ef24-4796-a527-ae50add3570d" />

**Compact View**
<img width="1527" height="1124" alt="Downloads - Compact View" src="https://github.com/user-attachments/assets/87f61e41-9c11-4aac-89a9-aad7e60f86f6" />

**Retro View**
<img width="1516" height="1248" alt="Downloads - Retro View" src="https://github.com/user-attachments/assets/6588f68e-ee72-4226-ae08-f5bd92ff81c2" />

*Three view modes to browse your cached games*

### Clients
<img width="1521" height="909" alt="Clients" src="https://github.com/user-attachments/assets/0a80fcdb-d51e-42a8-8d9b-1b0a436e2ab1" />

*Monitor which devices are using your cache*

### Users
<img width="1523" height="510" alt="Users - Session Overview" src="https://github.com/user-attachments/assets/0c5e4a95-7fad-4fa7-819d-bf49fc7355e0" />
<img width="1497" height="899" alt="Users - Details" src="https://github.com/user-attachments/assets/84194a8c-e574-4e5e-abbb-a48840c32d36" />

*Manage sessions and access controls*

### Events
<img width="1523" height="1249" alt="Events Calendar" src="https://github.com/user-attachments/assets/123dd833-d300-49e5-8023-62ea8d4e4acc" />

*Calendar view of download activity and LAN events*

### Prefill
<img width="1522" height="1069" alt="Steam Prefill" src="https://github.com/user-attachments/assets/a42963cd-cf5a-45a4-a538-638bf43e7c06" />

*Pre-download games to your cache before your LAN party*

</div>

-----

<a id="features"></a>
## Features

### Dashboard
- **Draggable stat cards** - Reorder or hide cards to customize your view
- **Time range filtering** - View stats for live, 1h, 24h, 7 days, 30 days, or custom ranges
- **Service breakdown** - See bandwidth usage per platform (Steam, Epic, Battle.net, etc.)
- **Recent downloads** - Quick view of what's been cached
- **Top clients** - Identify your heaviest users

### Downloads
- **Three view modes** - Normal (cards), Compact (list), and Retro (terminal style)
- **Flexible sorting** - By date, size, cache efficiency, session count, or name
- **Powerful filtering** - By service, client, or hide small/unknown downloads
- **Data export** - JSON or CSV

### Clients
- **Device tracking** - See all devices that have used your cache
- **Client grouping** - Organize clients with custom names
- **Bandwidth stats** - Track usage per client

### Users
- **Session management** - View active authenticated and guest sessions
- **Guest access** - Configurable read-only access duration
- **Access control** - Revoke sessions as needed

### Events
- **Calendar view** - Visualize download activity over time
- **Custom events** - Create events for LAN parties
- **Activity tracking** - See what was downloaded during each event

### Prefill
- **Steam integration** - Pre-download games directly to your cache
- **Steam Guard support** - Works with all authentication methods
- **Library selection** - Choose which games to prefill
- **Real-time progress** - Monitor downloads as they happen

### Management
- **Cache operations** - Clear cache by service or remove specific games
- **Log processing** - Reprocess logs, handle corruption
- **Game detection** - See what games are in your cache with actual sizes
- **Depot mappings** - Download 290,000+ game mappings from GitHub
- **Theme editor** - Create and import custom themes
- **Prometheus metrics** - Export data to Grafana

-----

<a id="quick-start"></a>
## Quick Start

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

### Getting Your API Key

```bash
docker logs lancache-manager | grep "API Key"
```

The key is also saved to `/data/api_key.txt`.

### First Steps

1. Open `http://localhost:8080`
2. Navigate to Management and enter your API key
3. Click **Process Logs** to analyze your existing cache data

-----

<a id="docker-compose-reference"></a>
## Docker Compose Reference

A complete docker-compose.yml with all available options:

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
      - /mnt/lancache/logs:/logs:ro
      - /mnt/lancache/cache:/cache:ro
      - /var/run/docker.sock:/var/run/docker.sock

    environment:
      #===========================================================
      # REQUIRED - You must configure these for your environment
      #===========================================================

      # User/Group ID - match your LANCache filesystem permissions
      - PUID=33
      - PGID=33

      # Your timezone
      - TZ=America/Chicago

      # Paths to your LANCache log file and cache directory
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache

      #===========================================================
      # OPTIONAL - Defaults work for most users (auto-detected)
      #===========================================================

      # Internal port binding (do not change)
      - ASPNETCORE_URLS=http://+:80

      # Security (all have sensible defaults)
      # - Security__EnableAuthentication=true
      # - Security__MaxAdminDevices=3
      # - Security__GuestSessionDurationHours=6
      # - Security__RequireAuthForMetrics=false
      # - Security__ProtectSwagger=true

      # Nginx log rotation (auto-detects container name)
      # - NginxLogRotation__Enabled=true
      # - NginxLogRotation__ContainerName=
      # - NginxLogRotation__ScheduleHours=24

      # Memory management (leave disabled unless needed)
      # - Optimizations__EnableGarbageCollectionManagement=false

      # Cache clearing mode
      # - CacheClear__DeleteMode=preserve

      # Steam Prefill (all auto-detected)
      # - Prefill__DockerImage=ghcr.io/regix1/steam-prefill-daemon:latest
      # - Prefill__SessionTimeoutMinutes=120
      # - Prefill__DaemonBasePath=/data/prefill-sessions
      # - Prefill__HostDataPath=

      # Multiple datasources (auto-discovery available)
      # - LanCache__AutoDiscoverDatasources=true
      # - LanCache__DataSources__0__Name=Default
      # - LanCache__DataSources__0__CachePath=/cache
      # - LanCache__DataSources__0__LogPath=/logs
      # - LanCache__DataSources__0__Enabled=true
```

> [!NOTE]
> Remove `:ro` from the cache volume to enable cache clearing and game removal features. The Docker socket is required for nginx log rotation and Steam Prefill functionality.

-----

<a id="configuration-options"></a>
## Configuration Options

### Volumes

| Volume | Purpose | Notes |
|--------|---------|-------|
| `/data` | Database, API key, themes, cached images | Required |
| `/logs` | LANCache access logs | Add `:ro` for read-only |
| `/cache` | LANCache cached files | Add `:ro` for monitoring-only |
| `/var/run/docker.sock` | Docker API access | Required for nginx rotation and Steam Prefill |

### User/Group Settings

| Variable | Default | Description |
|----------|---------|-------------|
| `PUID` | 33 | User ID the app runs as. Match your filesystem permissions. |
| `PGID` | 33 | Group ID the app runs as. |
| `TZ` | UTC | Timezone for log timestamps (e.g., `America/Chicago`). |

### Path Settings

| Variable | Default | Description |
|----------|---------|-------------|
| `LanCache__LogPath` | - | Path to the LANCache access log file. |
| `LanCache__CachePath` | - | Path to the LANCache cache directory. |
| `LanCache__AutoDiscoverDatasources` | false | Auto-detect datasources from matching subdirectories. |

### Security

| Variable | Default | Description |
|----------|---------|-------------|
| `Security__EnableAuthentication` | true | Require API key for admin features. Set `false` for dev only. |
| `Security__MaxAdminDevices` | 3 | Number of devices that can share the same API key. |
| `Security__GuestSessionDurationHours` | 6 | Default guest session length (configurable in UI). |
| `Security__RequireAuthForMetrics` | false | Require API key for `/metrics` endpoint. |
| `Security__ProtectSwagger` | true | Require auth for Swagger API docs in production. |

#### Access Levels

The API uses two levels of endpoint protection:

| Level | Description | Examples |
|-------|-------------|----------|
| **Admin Only** | Requires API key authentication | Cache clearing, log processing, settings changes |
| **Guest Session** | Requires admin auth OR valid guest session | Viewing downloads, stats, events, client data |

**Guest sessions** allow read-only access without sharing your API key. Guests must have a valid session (created by an admin) to view data. No endpoints are publicly accessible without authentication.

To grant guest access:
1. Navigate to **Users** tab
2. Click **Create Guest Link**
3. Share the link with your guests

Guests can view dashboards, downloads, and events but cannot modify settings or clear cache.

### Nginx Log Rotation

| Variable | Default | Description |
|----------|---------|-------------|
| `NginxLogRotation__Enabled` | true | Signal nginx to reopen logs after manipulation. |
| `NginxLogRotation__ContainerName` | (auto) | LANCache container name. Leave empty for auto-detection. |
| `NginxLogRotation__ScheduleHours` | 24 | How often to check for rotation needs. |

### Optimization

| Variable | Default | Description |
|----------|---------|-------------|
| `Optimizations__EnableGarbageCollectionManagement` | false | Enable memory management controls. Useful for low-memory systems. |

### Cache Clearing

| Variable | Default | Description |
|----------|---------|-------------|
| `CacheClear__DeleteMode` | preserve | `preserve` keeps directory structure, `full` removes everything. |

### Steam Prefill

| Variable | Default | Description |
|----------|---------|-------------|
| `Prefill__DockerImage` | ghcr.io/regix1/steam-prefill-daemon:latest | Docker image for prefill containers. |
| `Prefill__SessionTimeoutMinutes` | 120 | Inactive session cleanup timeout. |
| `Prefill__DaemonBasePath` | /data/prefill-sessions | Session data storage path. |
| `Prefill__HostDataPath` | (auto) | Host path mapping to `/data`. Set only if auto-detection fails. |
| `Prefill__LancacheDnsIp` | (auto) | IP of your lancache-dns container. Auto-detected if lancache-dns is running. |
| `Prefill__NetworkMode` | (auto) | Network mode for prefill containers: `host`, `bridge`, or a network name. Auto-detected from lancache-dns. |

-----

<a id="tabs-overview"></a>
## Tabs Overview

### Dashboard
Your home base for cache statistics. View overall performance, recent activity, and service breakdowns. Stat cards are draggable—arrange them however you prefer.

### Downloads
Browse everything that's been cached. Choose between Normal view (cards with game art), Compact view (dense list for scanning), or Retro view (terminal aesthetic). Filter and sort to find what you're looking for.

### Clients
Track every device that has downloaded through your cache. Group clients together with friendly names to identify machines at your LAN party.

### Users
Manage active sessions. See who's connected, create guest access links, configure session duration, and revoke sessions when needed. Guest sessions provide read-only access to dashboards, downloads, and events without sharing your API key.

### Events
A calendar showing download activity over time. Create custom events to mark LAN parties, then review what got downloaded during each one.

### Prefill
Pre-download games to your cache before people arrive. Log in with Steam, select games from your library, and let them download overnight.

### Management
The admin panel (requires authentication). Process logs, clear cache, download depot mappings, configure Steam API access, manage themes, and handle database operations.

-----

<a id="steam-prefill"></a>
## Steam Prefill

The Prefill tab lets you pre-download games to your cache before your LAN party starts. This feature is powered by [steam-lancache-prefill](https://github.com/tpill90/steam-lancache-prefill) by [@tpill90](https://github.com/tpill90).

### Requirements

- Docker socket mounted (without `:ro`)
- Authenticated as admin
- Lancache-dns container running (or manual DNS configuration)

### Network Configuration

For prefill to work, the prefill container needs:
1. **Internet access** - to authenticate with Steam and download game data
2. **DNS resolution** - Steam CDN domains should resolve to your cache server

The app **auto-detects** network settings in most cases:
- If your lancache-dns uses `network_mode: host`, prefill containers will also use host networking
- Otherwise, it uses the lancache-dns container's IP address for DNS resolution

**Network Diagnostics:**
When a prefill session starts, the app automatically tests the container's network configuration and logs the results. Check your container logs for output like:
```
═══════════════════════════════════════════════════════════════════════
  PREFILL CONTAINER NETWORK DIAGNOSTICS - prefill-daemon-abc123
═══════════════════════════════════════════════════════════════════════
  ✓ Internet connectivity: OK (reached api.steampowered.com)
  lancache.steamcontent.com resolved to 192.168.1.5
  ✓ DNS looks correct (private IP - likely your lancache server)
═══════════════════════════════════════════════════════════════════════
```

**Manual configuration** (if auto-detection fails):

```yaml
environment:
  # Option 1: Use bridge networking (recommended for most setups)
  - Prefill__NetworkMode=bridge

  # Option 2: Use host networking (for host-networked lancache-dns)
  - Prefill__NetworkMode=host

  # Option 3: Explicit DNS IP (when lancache-dns IP isn't auto-detected)
  - Prefill__LancacheDnsIp=192.168.1.10
```

> [!TIP]
> If prefill containers have no internet access, try setting `Prefill__NetworkMode=bridge`. This is a common fix when Docker's default network isolation prevents outbound connections.

### How It Works

1. Navigate to the Prefill tab
2. Log in with your Steam account (Steam Guard supported)
3. Select games from your library
4. Start the prefill

Downloads run in a separate container with real-time progress updates. When your guests arrive, the games are already cached and ready to serve at full speed.

### Importing App IDs

If you're migrating from [steam-lancache-prefill](https://github.com/tpill90/steam-lancache-prefill) or have a list of Steam App IDs you want to prefill, you can import them directly:

1. Click **Select Apps** to open the game selection modal
2. Click **Import App IDs** to expand the import section
3. Paste your App IDs in any of these formats:
   - Comma-separated: `730, 570, 440`
   - JSON array: `[730, 570, 440]`
   - One per line
4. Click **Import**

The import will show you how many games were added, how many were already selected, and how many IDs aren't in your Steam library (these will be skipped during prefill).

**Migrating from steam-lancache-prefill:** Copy the contents of your `selectedAppsToPrefill.json` file and paste it directly into the import field.

-----

<a id="custom-themes"></a>
## Custom Themes

### Theme Editor

Navigate to **Management > Preferences > Theme Management** to:
- Create themes from scratch with live preview
- Browse and install community themes
- Import/export themes as TOML files

### Theme Format

```toml
[meta]
name = "My Theme"
id = "my-theme"
isDark = true
version = "1.0.0"
author = "Your Name"

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
textPrimary = "#ffffff"
```

Themes are stored in `/data/themes/`.

-----

<a id="grafana--prometheus"></a>
## Grafana & Prometheus

### Available Metrics

| Metric | Description |
|--------|-------------|
| `lancache_cache_capacity_bytes` | Total storage capacity |
| `lancache_cache_size_bytes` | Currently used space |
| `lancache_cache_hit_bytes_total` | Bandwidth saved (cache hits) |
| `lancache_cache_miss_bytes_total` | New data downloaded |
| `lancache_active_downloads` | Current active downloads |
| `lancache_cache_hit_ratio` | Cache effectiveness (0-1) |
| `lancache_downloads_by_service` | Downloads per service |
| `lancache_bytes_served_by_service` | Bandwidth per service |

### Prometheus Configuration

```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    scrape_interval: 30s
    metrics_path: /metrics
```

For authenticated metrics, set `Security__RequireAuthForMetrics=true` and add:

```yaml
    authorization:
      type: Bearer
      credentials: 'your-api-key-here'
```

### Example Queries

```promql
# Cache hit rate as percentage
lancache_cache_hit_ratio * 100

# Bandwidth saved in last 24 hours
increase(lancache_cache_hit_bytes_total[24h])

# Cache size in GB
lancache_cache_size_bytes / 1024 / 1024 / 1024
```

-----

<a id="multiple-datasources"></a>
## Multiple Datasources

Most users run a single LANCache instance and don't need this section. However, if you have outsourced specific services to separate cache directories or run multiple LANCache instances, you can configure multiple datasources to monitor them all from one dashboard.

### When You Need This

- **Outsourced services** - You've configured LANCache to store Steam on a separate drive from other services
- **Multiple LANCache instances** - You run separate cache servers for different purposes
- **Segmented storage** - Different services on different drives or partitions

### How It Works

Each datasource represents a log/cache directory pair. The app processes logs and tracks cache usage separately for each, then combines the data in the dashboard and downloads views.

-----

### Auto-Discovery (Recommended)

The simplest approach—let the app scan for matching subdirectories:

```yaml
environment:
  - LanCache__LogPath=/logs
  - LanCache__CachePath=/cache
  - LanCache__AutoDiscoverDatasources=true
```

**What it detects:**

1. **Root-level datasource** - If `/logs/access.log` exists and `/cache` contains LANCache hash directories (`00/`, `01/`, etc.), creates a "Default" datasource
2. **Subdirectory datasources** - For each folder that exists in both `/cache` and `/logs`, creates a named datasource (e.g., `/cache/steam` + `/logs/steam` → "Steam")

**Example directory structure:**

```
/mnt/lancache/
├── cache/
│   ├── 00/, 01/, a1/, ff/    ← Default cache (hash dirs at root)
│   ├── steam/
│   │   └── 00/, 01/, ...     ← Outsourced Steam
│   └── epic/
│       └── 00/, 01/, ...     ← Outsourced Epic
└── logs/
    ├── access.log            ← Default log
    ├── steam/
    │   └── access.log        ← Steam log
    └── epic/
        └── access.log        ← Epic log
```

This creates three datasources: **Default**, **Steam**, and **Epic**.

> [!NOTE]
> Folder matching is case-insensitive. `Steam`, `steam`, and `STEAM` all match.

-----

### Manual Configuration

For precise control or when directories are on separate drives, define each datasource explicitly:

```yaml
environment:
  # Main LANCache
  - LanCache__DataSources__0__Name=Default
  - LanCache__DataSources__0__CachePath=/cache
  - LanCache__DataSources__0__LogPath=/logs
  - LanCache__DataSources__0__Enabled=true

  # Steam on a separate drive
  - LanCache__DataSources__1__Name=Steam
  - LanCache__DataSources__1__CachePath=/steam-cache
  - LanCache__DataSources__1__LogPath=/steam-logs
  - LanCache__DataSources__1__Enabled=true
```

With corresponding volume mounts:

```yaml
volumes:
  - /mnt/lancache/cache:/cache:ro
  - /mnt/lancache/logs:/logs:ro
  - /mnt/steam-drive/cache:/steam-cache:ro
  - /mnt/steam-drive/logs:/steam-logs:ro
```

> [!NOTE]
> Manual datasource configuration takes priority over auto-discovery.

-----

<a id="nginx-reverse-proxy"></a>
## Nginx Reverse Proxy

LANCache Manager works well behind Nginx. HTTPS is recommended (and required for guest session cookies to be sent on cross-origin image requests).

### Single origin (recommended)

Serve both the UI and API from the same origin. This keeps cookies first-party and avoids cross-origin issues.

```nginx
server {
  listen 443 ssl http2;
  server_name lancache.example.com;

  ssl_certificate     /etc/letsencrypt/live/lancache.example.com/fullchain.pem;
  ssl_certificate_key /etc/letsencrypt/live/lancache.example.com/privkey.pem;

  # Increase if you have large responses
  client_max_body_size 50m;

  location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;

    # SignalR (WebSockets)
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
  }
}

server {
  listen 80;
  server_name lancache.example.com;
  return 301 https://$host$request_uri;
}
```

### Separate API origin (only if required)

If you split UI and API across hosts, you must use HTTPS and allow credentials. In that case:

- Set `VITE_API_URL=https://api.lancache.example.com` when building the Web UI.
- Keep `SameSite=None; Secure` cookies (configured by the app).
- Ensure CORS allows credentials for your UI origin.

Example API proxy:

```nginx
server {
  listen 443 ssl http2;
  server_name api.lancache.example.com;

  ssl_certificate     /etc/letsencrypt/live/api.lancache.example.com/fullchain.pem;
  ssl_certificate_key /etc/letsencrypt/live/api.lancache.example.com/privkey.pem;

  location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;

    # SignalR (WebSockets)
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
  }
}
```

-----

<a id="troubleshooting"></a>
## Troubleshooting

### Logs Not Processing

1. Verify the log path in **Management > Settings**
2. Confirm your volume mount matches `LanCache__LogPath`
3. Click **Process Logs** in Management
4. Check container logs: `docker logs lancache-manager`

### Games Not Identified

1. Download latest mappings from **Management > Depot Mappings**
2. Add custom mappings for private depots if needed
3. Click **Reprocess All Logs** after adding mappings

### Lost API Key

```bash
cat ./data/api_key.txt
# or
docker logs lancache-manager | grep "API Key"
```

To generate a new key, stop the container, delete `./data/api_key.txt`, and restart.

### Permission Issues

Verify PUID/PGID match your file ownership:
```bash
ls -n /path/to/cache
```

### Prefill Not Working

1. Ensure Docker socket is mounted without `:ro`
2. Confirm you're authenticated as admin
3. Check container logs for network diagnostics output (look for `═══ PREFILL CONTAINER NETWORK DIAGNOSTICS ═══`)
4. **Container has no internet access:** The prefill container can't reach Steam. Common fixes:
   - Set `Prefill__NetworkMode=bridge` in your docker-compose.yml (recommended for most setups)
   - Ensure your Docker network has outbound internet access
   - Check firewall rules for outbound connections
5. **HTTP 400 errors during download:** The prefill container can't resolve Steam CDN domains to your cache. Either:
   - Set `Prefill__NetworkMode=host` if your lancache-dns uses host networking
   - Set `Prefill__LancacheDnsIp` to your lancache-dns IP address
   - The app auto-detects this if your lancache-dns container is running
6. **DNS bypassing lancache via IPv6:** If your network uses IPv6, DNS queries might bypass lancache-dns. The app automatically disables IPv6 in prefill containers to prevent this.

**Network Diagnostics:**
When a prefill container starts, the app runs network diagnostics that appear in the logs:
- Internet connectivity test (can reach api.steampowered.com)
- DNS resolution for `lancache.steamcontent.com` and `steam.cache.lancache.net`
- Warnings if DNS resolves to public IPs instead of your cache

**Finding your lancache-dns IP:**
```bash
docker inspect lancache-dns | grep IPAddress
```

**Example configurations:**

Bridge mode (recommended for most Docker setups):
```yaml
environment:
  - Prefill__NetworkMode=bridge
```

Host networking (when lancache-dns uses host mode):
```yaml
environment:
  - Prefill__NetworkMode=host
```

Explicit DNS IP (when auto-detection fails):
```yaml
environment:
  - Prefill__LancacheDnsIp=192.168.1.10
```

-----

<a id="building-from-source"></a>
## Building from Source

**Requirements:** .NET 8 SDK, Node.js 20+, Rust 1.75+

```bash
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Rust processor
cd rust-processor && cargo build --release

# Web interface
cd ../Web && npm install && npm run dev  # http://localhost:3000

# API
cd ../Api/LancacheManager && dotnet run  # http://localhost:5000
```

**Docker build:**
```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push .
```

-----

<a id="contributing-translations"></a>
## Contributing Translations

LANCache Manager supports internationalization (i18n) and welcomes community translations! The app is fully prepared for localization—all UI strings are externalized and ready for translation.

### How to Contribute

1. **Fork the repository** on GitHub
2. Navigate to `Web/src/i18n/locales/`
3. Copy `en.json` to a new file named with your language code (e.g., `de.json`, `fr.json`, `es.json`, `pt-BR.json`)
4. Translate all the string values (keep the keys unchanged)
5. Submit a **Pull Request** with your translation

### Translation File Structure

```
Web/src/i18n/locales/
├── en.json          ← English (reference)
├── de.json          ← German (your contribution)
├── fr.json          ← French (your contribution)
└── ...
```

### Guidelines

- **Keep JSON keys unchanged** - Only translate the string values
- **Preserve placeholders** - Keep `{{variable}}` placeholders intact (e.g., `{{count}}`, `{{name}}`)
- **Maintain formatting** - Preserve any HTML tags like `<strong>` if present
- **Test your translation** - Run the app locally to verify your translations display correctly

### Example

```json
// en.json
{
  "dashboard": {
    "title": "Dashboard",
    "recentDownloads": "Recent Downloads",
    "totalCache": "Total Cache: {{size}}"
  }
}

// de.json
{
  "dashboard": {
    "title": "Übersicht",
    "recentDownloads": "Letzte Downloads",
    "totalCache": "Gesamter Cache: {{size}}"
  }
}
```

### Need Help?

If you have questions about contributing translations or need clarification on any strings, feel free to [open an issue](https://github.com/regix1/lancache-manager/issues) or reach out on the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

-----

<a id="need-help"></a>
## Need Help?

If you run into issues, feel free to [open an issue](https://github.com/regix1/lancache-manager/issues) on GitHub.

You can also find the LANCache community at the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

-----

## Support

<div align="center">

### Enjoying LANCache Manager?

If this project has been helpful, consider supporting development.

<br>

<a href="https://www.buymeacoffee.com/regix">
  <img src="https://media0.giphy.com/media/v1.Y2lkPTc5MGI3NjExcGw1bHduZGxrNjVzYnM3ZnppZmZ1YmV5Z2J3dThjMGJnNWswejFyaiZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9cw/kmIZ4lx2ZHpr5jY0W4/giphy.gif" alt="Buy Me A Coffee" width="600">
</a>

**[☕ Click to donate](https://www.buymeacoffee.com/regix)**

<br>

*Every coffee helps keep this project alive!*

</div>

-----

## License

[MIT License](https://github.com/regix1/lancache-manager/blob/main/LICENSE)
