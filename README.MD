# LANCache Manager

A web interface for monitoring and managing your LANCache. Track downloads, see which games are being cached, check bandwidth savings, and customize everything with themes.

## IMPORTANT: Docker Installation

> [!IMPORTANT]
> **Always use the `latest` tag:**
>
> ```bash
> docker pull ghcr.io/regix1/lancache-manager:latest
> ```
>
> GitHub's package page shows `:dev` in the installation command because dev builds are pushed more frequently. **`:dev` is unstable.**
>
> **Use `:latest` for production. Use `:dev` only for testing.**

## 📑 Table of Contents

*Click the ⬆ arrow on any section to return here*

- [Screenshots](#screenshots)
- [Features](#features)
- [Quick Start](#quick-start)
- [Getting Started](#getting-started)
- [Admin Features](#admin-features)
- [Custom Themes](#custom-themes)
- [Optimization Settings](#optimization-settings)
- [Image Caching](#image-caching)
- [Nginx Log Rotation](#nginx-log-rotation)
- [Depot Mappings](#depot-mappings)
- [Grafana & Prometheus Integration](#grafana--prometheus-integration)
- [API Examples](#api-examples)
- [Troubleshooting](#troubleshooting)
- [Building from Source](#building-from-source)
- [Contributing](#contributing)
- [Support](#support)
- [License](#license)

---

<a id="screenshots"></a>
## Screenshots [⬆](#-table-of-contents)

<div align="center">

### Dashboard
<img width="1539" height="1085" alt="Dashboard Overview" src="https://github.com/user-attachments/assets/2eb41fb5-e3f1-4770-aeda-62b6fb324174" />
<img width="1738" height="609" alt="Dashboard Charts" src="https://github.com/user-attachments/assets/56f668a1-526d-442f-b496-65021f2c0a68" />

*Stats at a glance - drag and drop to reorder cards, filter by time range*

### Downloads

**Normal View**
<img width="1530" height="1254" alt="Downloads Tab - Normal View" src="https://github.com/user-attachments/assets/188b1e9d-05fa-40c3-8185-230e0b4cf32f" />

**Compact View**
<img width="1533" height="1255" alt="Downloads Tab - Compact View" src="https://github.com/user-attachments/assets/8a882729-9274-4843-814d-2d01c877b737" />

*Switch between normal and compact views, filter and sort however you want*

### Clients
<img width="1550" height="911" alt="Clients" src="https://github.com/user-attachments/assets/8901db78-dca6-4a37-8139-ce87907e6c23" />

*See which devices are using your cache*

### Users
<img width="1527" height="1055" alt="Users" src="https://github.com/user-attachments/assets/e82c881d-bf5c-414d-9b23-2107f7042b0c" />

*Manage sessions and control access*

### Management

**Authentication & Access**
<img width="1563" height="599" alt="Authentication & Access" src="https://github.com/user-attachments/assets/9b08a54a-cfc4-44d0-bfa5-efa7237a6f9b" />

**Integration & Services**
<img width="1544" height="1244" alt="Integration & Services" src="https://github.com/user-attachments/assets/ad272fc0-8f3a-4e31-a6c9-e2b4a2116dcb" />

**Database Management**
<img width="1521" height="1201" alt="Database Management" src="https://github.com/user-attachments/assets/b3398a91-fccf-4d0b-9fab-45c059278b55" />

**Import Historical Data**
<img width="1475" height="786" alt="Import Historical Data" src="https://github.com/user-attachments/assets/664ddff5-9656-422a-9630-3ec001c3f45b" />

**Disk Cache Management and Log Processing**
<img width="1483" height="608" alt="Disk Cache Management and Log Processing" src="https://github.com/user-attachments/assets/ca975cfb-970e-4d5e-b830-6d7166ded4cb" />

**Depot Mapping**
<img width="1472" height="745" alt="Depot Mapping" src="https://github.com/user-attachments/assets/736ef150-09f3-4acf-9ad7-5c6d6ac35928" />

**Log & Cache Management**
<img width="1481" height="663" alt="Log & Cache Management" src="https://github.com/user-attachments/assets/defae056-2e99-48e8-b3cb-5fadf21fdf30" />

**Game Cache Detection**
<img width="1481" height="490" alt="Game Cache Detection" src="https://github.com/user-attachments/assets/b2745ef0-ead8-47de-95d2-46bd03e5aa5a" />

**Theme Management**
<img width="1468" height="1079" alt="Theme Management" src="https://github.com/user-attachments/assets/00212a80-0f15-432b-9849-22967e7119f9" />

*Manage cache, process logs, configure depot mappings, and tweak settings*

### Theme Examples

**Sage and Wood**
<img width="1526" height="1085" alt="Sage and Wood Theme" src="https://github.com/user-attachments/assets/17d710e1-4b9b-4dd0-bac6-c9f89ef8b3f0" />

**LANCache Unofficial**
<img width="1527" height="1085" alt="LANCache Unofficial Theme" src="https://github.com/user-attachments/assets/7aa8d363-ab61-4e63-a50e-94021d95eb68" />

</div>

<a id="features"></a>
## Features [⬆](#-table-of-contents)

### Dashboard
- **Draggable stat cards** - Reorder them how you want, hide the ones you don't care about
- **Time range filtering** - Live, last hour, 24h, 7 days, 30 days, or custom date ranges
- **Service breakdown** - See which services (Steam, Epic, etc.) are eating your bandwidth
- **Recent downloads** - Quick view of what's been cached lately
- **Top clients** - See who's downloading the most

### Downloads
- **Normal and Compact views** - Cards or list, your choice
- **Sorting** - By date, size, cache efficiency, number of sessions, alphabetical
- **Filtering** - By service, client, hide small files, hide unknown games
- **Presets** - Pretty, Minimal, Show All, or tweak every setting yourself
- **Export** - JSON or CSV

### Management (requires authentication)
- **Cache operations** - Clear cache, remove specific games or services
- **Log processing** - Reprocess logs, remove corruption
- **Game detection** - See what games are in your cache with actual sizes
- **Database management** - Clear specific tables without nuking everything
- **Steam integration** - PICS authentication for depot mappings

### Other Stuff
- **Custom themes** - Build your own or grab community themes
- **Prometheus metrics** - Export to Grafana
- **Multi-device auth** - Share API key across devices (configurable limit)
- **Guest mode** - Read-only access without API key
- **Read-only volume support** - Works fine with `:ro` mounts

<a id="quick-start"></a>
## Quick Start [⬆](#-table-of-contents)

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

### Docker Compose

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
      # Data directory - stores database, API key, themes, and cached images
      - ./data:/data

      # LANCache log directory (read-only recommended)
      - /mnt/logs:/logs:ro

      # LANCache cache directory
      # Use :ro for read-only monitoring
      # Remove :ro to enable cache clearing, corruption removal, and game removal
      - /mnt/cache/cache:/cache:ro

      # Docker socket for nginx log rotation (optional)
      # Allows signaling nginx to reopen logs after manipulation
      # Set NginxLogRotation__Enabled=false and remove this line to disable
      - /var/run/docker.sock:/var/run/docker.sock:ro
    environment:
      #=== User/Group Settings ===#
      # PUID/PGID: User and group ID the application runs as after setup
      # Set these to match your NFS/host filesystem permissions
      - PUID=1000
      - PGID=1000

      # TZ: Timezone for log timestamps and display
      - TZ=America/Chicago

      # ASPNETCORE_URLS: Internal port binding (required, do not change)
      - ASPNETCORE_URLS=http://+:80

      #=== Path Settings ===#
      # LanCache__LogPath: Path to the LANCache access log file
      - LanCache__LogPath=/logs/access.log

      # LanCache__CachePath: Path to the LANCache cache directory
      - LanCache__CachePath=/cache

      #=== Security Settings ===#
      # Security__EnableAuthentication: Master switch for authentication
      # true = require API key for admin features (recommended)
      # false = disable ALL authentication (dev/testing only)
      - Security__EnableAuthentication=true

      # Security__MaxAdminDevices: Number of devices that can use the same API key
      - Security__MaxAdminDevices=3

      # Security__GuestSessionDurationHours: Initial default for guest session duration
      # Only used on first startup - can be changed in Management UI afterwards
      # - Security__GuestSessionDurationHours=6

      # Security__RequireAuthForMetrics: Require API key for /metrics endpoint
      # true = Prometheus/Grafana must use Bearer token authentication
      # false = /metrics endpoint is publicly accessible
      - Security__RequireAuthForMetrics=false

      #=== Nginx Log Rotation Settings ===#
      # NginxLogRotation__Enabled: Auto-signal nginx to reopen logs after manipulation
      # Prevents monolithic container from losing log file handle
      # Requires docker.sock volume mount
      - NginxLogRotation__Enabled=true

      # NginxLogRotation__ContainerName: Name of the LANCache container
      # Leave empty for auto-detection, or specify container name (e.g., "lancache")
      - NginxLogRotation__ContainerName=

      #=== Optimization Settings ===#
      # Optimizations__EnableGarbageCollectionManagement: Enable GC controls in UI
      # true = show memory management controls in Management page
      # false = use default .NET garbage collection (recommended for most users)
      # Enable this for low-memory systems or if memory usage climbs over time
      - Optimizations__EnableGarbageCollectionManagement=false

      #=== Cache Clearing Settings ===#
      # CacheClear__DeleteMode: How to delete files when clearing cache
      # "preserve" = keep directory structure, only delete files (default, safer)
      # "full" = delete everything including directories (faster, more thorough)
      - CacheClear__DeleteMode=preserve
```

Get your **API key**:

```bash
docker logs lancache-manager | grep "API Key"
```

Also saved to `/data/api_key.txt`

<a id="getting-started"></a>
## Getting Started [⬆](#-table-of-contents)

1. Start the container
2. Get your API key from the logs or `/data/api_key.txt`
3. Open `http://localhost:8080`
4. Go to Management and enter your API key
5. Click "Process Logs" to start analyzing

### Read-Only Volumes

If you mount with `:ro`, you'll see lock badges in Management. Totally fine for monitoring.

**With read-only:**
- View stats and downloads
- Monitor performance
- Track clients

**To enable management features:**
```yaml
volumes:
  - /path/to/cache:/cache      # Can clear cache
  - /path/to/logs:/logs:ro     # Keep logs read-only
```

<a id="admin-features"></a>
## Admin Features [⬆](#-table-of-contents)

### User Management

- **Real-time sessions** - See who's connected, what devices they're using
- **Session types** - Authenticated users get full access, guests get 6-hour read-only access
- **Controls** - Revoke sessions, delete records

### API Key

- Auto-generated on first startup
- Saved to `/data/api_key.txt`
- Multi-device support (default 3 devices)

```yaml
environment:
  - Security__MaxAdminDevices=3
```

<a id="custom-themes"></a>
## Custom Themes [⬆](#-table-of-contents)

### Built-in Options

- Create themes from scratch with the visual editor
- Browse and install community themes
- Import/export as TOML files
- Auto-updates for installed themes

### Creating a Theme

1. Go to **Management > Preferences > Theme Management**
2. Click **Create Theme** or edit existing
3. Customize colors with live preview
4. Save and apply

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
# ... lots more options
```

Themes save to `/data/themes/`

<a id="optimization-settings"></a>
## Optimization Settings [⬆](#-table-of-contents)

### Memory Management

Optional GC controls for low-memory systems. Disabled by default.

```yaml
environment:
  - Optimizations__EnableGarbageCollectionManagement=true
```

Once enabled, you get controls in Management:
- Collection aggressiveness
- Memory threshold
- Manual GC trigger

**Use if:** Low memory system, memory climbing over time

**Skip if:** 8GB+ RAM, performance matters more than memory

<a id="image-caching"></a>
## Image Caching [⬆](#-table-of-contents)

Game artwork gets cached automatically to `/data/cached-img/`. Loads instantly after first fetch, auto-updates when Steam changes artwork.

**Clear cache:**
```bash
docker stop lancache-manager
rm -rf ./data/cached-img
docker start lancache-manager
```

<a id="nginx-log-rotation"></a>
## Nginx Log Rotation [⬆](#-table-of-contents)

LANCache's monolithic container has an nginx quirk - when you rotate logs, nginx keeps writing to the old file handle. This tool can signal nginx automatically.

### Setup

```yaml
volumes:
  - /var/run/docker.sock:/var/run/docker.sock:ro

environment:
  - NginxLogRotation__Enabled=true
  - NginxLogRotation__ContainerName=   # leave empty for auto-detect
```

**Security:** Socket is read-only, only runs `docker exec` to send signals.

**Manual alternative:**
```bash
docker exec lancache nginx -s reopen
```

<a id="depot-mappings"></a>
## Depot Mappings [⬆](#-table-of-contents)

Game identification uses mappings from [lancache-pics](https://github.com/regix1/lancache-pics) - 290,000+ depot mappings, updated every 4 hours.

**Download mappings:**
1. Go to **Management > Depot Mappings**
2. Click **Download from GitHub**
3. Wait 1-2 minutes

For private depots or unrecognized games, add custom mappings in the same section.

### Steam Web API (V1 vs V2)

Steam provides two Web API versions for fetching game data:

| API Version | Requirements | Access Level |
|-------------|--------------|--------------|
| **V2** | None | Public games only (no auth) |
| **V2 + Account Login** | Steam credentials | Public + playtest/restricted games |
| **V1** | API Key | Public + playtest/restricted games |

**Key difference:** V1 API keys are tied to your Steam account, so they inherently provide access to playtest/restricted games without needing a separate login. V2 requires a Steam account login to access those same restricted games.

**V1 API Key Setup:**
1. Get your API key from [Steam Web API](https://steamcommunity.com/dev/apikey)
2. Go to **Management > Steam Web API Status**
3. Click **Configure API Key** and enter your key

The system automatically uses V2 when available and falls back to V1 if V2 becomes unavailable.

### Steam Account Login vs API Key

| Method | Requirements | Access Level |
|--------|--------------|--------------|
| **Anonymous (V2)** | None | Public games only |
| **Account Login (V2)** | V2 API + Steam credentials | Public + playtest/restricted games |
| **V1 API Key** | API key from Steam | Public + playtest/restricted games |

> **Important:** Steam account login requires the **V2 API to be available**. When V2 is unavailable, the account login option is disabled. If you have a V1 API key configured, you already have access to playtest/restricted games through that key.

**For most users:** The V1 API key method is recommended. It provides access to all depot mappings including playtest games, doesn't require V2 API availability, and doesn't require entering your Steam password.

<a id="grafana--prometheus-integration"></a>
## Grafana & Prometheus Integration [⬆](#-table-of-contents)

### Available Metrics

- `lancache_cache_capacity_bytes` - Total storage
- `lancache_cache_size_bytes` - Used space
- `lancache_cache_hit_bytes_total` - Bandwidth saved
- `lancache_cache_miss_bytes_total` - New data cached
- `lancache_active_downloads` - Current downloads
- `lancache_cache_hit_ratio` - Effectiveness (0-1)
- `lancache_downloads_by_service` - Per-service breakdown
- `lancache_bytes_served_by_service` - Bandwidth per service

### Prometheus Config

**No auth:**
```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    scrape_interval: 30s
    metrics_path: /metrics
```

**With auth:**
```yaml
environment:
  - Security__RequireAuthForMetrics=true
```

```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    scrape_interval: 30s
    metrics_path: /metrics
    authorization:
      type: Bearer
      credentials: 'your-api-key-here'
```

### Grafana Examples

```promql
# Cache hit rate
lancache_cache_hit_ratio * 100

# Bandwidth saved today
increase(lancache_cache_hit_bytes_total[24h])

# Cache usage in GB
lancache_cache_size_bytes / 1024 / 1024 / 1024
```

<a id="api-examples"></a>
## API Examples [⬆](#-table-of-contents)

Stats endpoints are public. Management endpoints need your API key.

```bash
# Dashboard stats
curl http://localhost:8080/api/stats/dashboard?period=24h

# Top games
curl http://localhost:8080/api/stats/top-games?limit=10

# Client stats
curl http://localhost:8080/api/stats/clients

# Process logs (needs API key)
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/process-all-logs

# Clear service cache (needs API key)
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/cache/clear/steam

# Prometheus metrics
curl http://localhost:8080/metrics
```

<a id="troubleshooting"></a>
## Troubleshooting [⬆](#-table-of-contents)

### Logs Not Processing?

1. Check log path in **Management > Settings**
2. Verify volume mount matches:
   ```yaml
   volumes:
     - /your/logs:/logs:ro  # Must match LanCache__LogPath
   ```
3. Click **Process Logs** in Management
4. Check container logs: `docker logs lancache-manager`

### Games Not Identified?

1. Download latest mappings from **Management > Depot Mappings > Download from GitHub**
2. Add custom mappings for private depots
3. Click **Reprocess All Logs** after adding mappings

### Lost API Key?

```bash
# Option 1: Check the file
cat ./data/api_key.txt

# Option 2: Check logs
docker logs lancache-manager | grep "API Key"

# Option 3: Generate new one
docker stop lancache-manager
rm ./data/api_key.txt
docker start lancache-manager
docker logs lancache-manager | grep "API Key"
```

### Permission Issues?

1. Check volumes aren't read-only
2. Verify PUID/PGID match file ownership:
   ```bash
   ls -n /path/to/cache
   ```
3. Update docker-compose:
   ```yaml
   environment:
     - PUID=1000
     - PGID=1000
   ```

<a id="building-from-source"></a>
## Building from Source [⬆](#-table-of-contents)

**Requirements:** .NET 8 SDK, Node.js 20+, Rust 1.75+

```bash
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Rust processor
cd rust-processor
cargo build --release

# Web interface
cd ../Web
npm install
npm run dev  # http://localhost:3000

# API
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

<a id="contributing"></a>
## Contributing [⬆](#-table-of-contents)

Found a bug or have a feature request? [Open an issue](https://github.com/regix1/lancache-manager/issues) on GitHub.

<a id="support"></a>
## Support [⬆](#-table-of-contents)

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

<a id="license"></a>
## License [⬆](#-table-of-contents)

[MIT License](https://github.com/regix1/lancache-manager/blob/main/LICENSE)
