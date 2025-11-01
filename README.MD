# LANCache Manager

A modern web interface for monitoring and managing your LANCache deployment. Track downloads, identify games, monitor bandwidth savings in real-time, and manage your cache with a beautiful, customizable interface.

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
> **Use `:latest` for production. Use `:dev` only for testing new features.**

## 📑 Table of Contents

- [Screenshots](#screenshots)
- [Features](#features)
- [Quick Start](#quick-start)
  - [Basic Docker Run](#basic-docker-run)
  - [Complete Docker Compose Example](#complete-docker-compose-example)
- [Getting Started](#getting-started)
- [Admin Features](#admin-features)
- [Custom Themes](#custom-themes)
- [Depot Mappings](#depot-mappings)
- [Grafana & Prometheus Integration](#grafana--prometheus-integration)
- [API Examples](#api-examples)
- [Troubleshooting](#troubleshooting)
- [Building from Source](#building-from-source)
- [Contributing](#contributing)
- [Support](#support)
- [License](#license)

---

## Screenshots

<div align="center">

### Dashboard
<img width="1524" height="1083" alt="Dashboard Overview" src="https://github.com/user-attachments/assets/4069eb3a-aefb-44bd-876f-3a486e9aae4e" />

*Monitor your cache performance, bandwidth savings, and active downloads at a glance*

### Downloads

**Normal View**
<img width="1533" height="1248" alt="Downloads Tab - Normal View" src="https://github.com/user-attachments/assets/b7b92470-d769-4471-bc25-3c3e74f0fa0f" />

**Compact View**
<img width="1527" height="1245" alt="Downloads Tab - Compact View" src="https://github.com/user-attachments/assets/53419342-3ecc-49be-8793-daa7ccc55829" />

*Track all downloads with detailed game information, progress, and bandwidth data*

### Clients
<img width="1525" height="1082" alt="Client Statistics" src="https://github.com/user-attachments/assets/11515589-2030-44ef-9e46-43b9a1ee90b1" />

*See which devices are using your cache and how much bandwidth they're saving*

### Management

<img width="1528" height="917" alt="Management - Cache Operations" src="https://github.com/user-attachments/assets/2340d975-3981-4f1e-8cae-758d2ff23427" />

<img width="1522" height="883" alt="Management - Log Processing" src="https://github.com/user-attachments/assets/08e019e0-6b1e-404d-9f04-132a22fca4b8" />

<img width="1513" height="1223" alt="Management - Depot Mappings" src="https://github.com/user-attachments/assets/2dd06323-6fc4-4b73-b677-f904987bc257" />

<img width="1527" height="566" alt="Management - Settings" src="https://github.com/user-attachments/assets/05140260-32f8-477a-8b85-ee7831ecc364" />

*Manage your cache, process logs, customize depot mappings, and configure settings*

### Theme Customization

<img width="1524" height="1245" alt="Theme Management" src="https://github.com/user-attachments/assets/83501aac-726d-4d19-ac9b-c44bdb89409a" />

*Create and customize themes to match your style*

### Admin Session Management

<img width="1516" height="980" alt="Admin Page" src="https://github.com/user-attachments/assets/a7793069-b2ac-4795-856b-87593afedd0c" />

*Manage user sessions, view connected devices, and control access*

### Theme Examples

**LANCache Unofficial**
<img width="1527" height="1081" alt="LANCache Unofficial Theme" src="https://github.com/user-attachments/assets/215d43bc-1912-44f7-b488-4e892c61e82a" />

**Sage and Wood**
<img width="1525" height="1088" alt="Sage and Wood Theme" src="https://github.com/user-attachments/assets/1be6eb4c-6f46-4639-9942-f83a082ecc81" />

*Choose from built-in themes or create your own*

</div>

## Features

- **Real-Time Monitoring** - Watch downloads happen live with bandwidth tracking and progress updates
- **Game Identification** - Automatically identifies Steam games from depot IDs with support for custom mappings
- **Bandwidth Analytics** - See exactly how much bandwidth your cache is saving
- **Client Tracking** - Monitor which devices are using your cache
- **Cache Management** - Clear cache, remove specific games, and detect corrupted files
- **Session Management** - Control who has access with device limits and session tracking
- **Custom Themes** - Personalize the interface with built-in themes or create your own
- **Prometheus Metrics** - Export stats to Grafana for advanced visualization
- **Read-Only Support** - Works with read-only volumes while clearly showing what's available
- **Easy Depot Updates** - Download 290,000+ depot mappings from [lancache-pics](https://github.com/regix1/lancache-pics) with one click

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

### Complete Docker Compose Example

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
      # Your data directory (database, API key, themes)
      - ./data:/data

      # Your LANCache log directory (read-only recommended)
      - /mnt/logs:/logs:ro

      # Your LANCache cache directory
      # Use :ro for read-only monitoring
      # Remove :ro to enable cache clearing and game removal
      - /mnt/cache/cache:/cache:ro
    environment:
      # User/Group IDs for file permissions
      - PUID=1006
      - PGID=1006

      # Timezone for accurate timestamps
      - TZ=America/Chicago

      # Required: Path to LANCache nginx access.log
      - LanCache__LogPath=/logs/access.log

      # Required: Path to LANCache cache directory
      - LanCache__CachePath=/cache

      # Start reading logs from end (default: true)
      # Set to false to process entire log history on first run
      - LanCache__StartFromEndOfLog=true

      # Process historical logs on startup (default: false)
      - LanCache__ProcessHistoricalLogs=false

      # Security Settings

      # Enable authentication (default: true)
      # Set to 'false' to disable authentication (not recommended)
      - Security__EnableAuthentication=true

      # Maximum admin devices sharing the same API key (default: 3)
      - Security__MaxAdminDevices=3

      # Require API key for /metrics endpoint (default: false)
      # Set to 'true' to protect Prometheus metrics
      - Security__RequireAuthForMetrics=false
```

Visit **http://localhost:8080** and get your **API key** from the logs:

```bash
docker logs lancache-manager | grep "API Key"
```

**API key is also saved to `/data/api_key.txt`**

## Getting Started

### First Time Setup

1. **Start the container** using the docker-compose example above
2. **Get your API key** from the logs or `/data/api_key.txt`
3. **Open your browser** to `http://localhost:8080`
4. **Navigate to Management** and enter your API key when prompted
5. **Click "Process Logs"** to start analyzing your cache data

That's it! The dashboard will populate with your cache statistics and download history.

### Understanding Read-Only Volumes

If you mount your cache or log directories with `:ro` (read-only), you'll see lock badges in the Management section. This is completely normal and the app will work fine for monitoring.

**With read-only volumes, you can:**
- View all statistics and downloads
- Monitor cache performance
- Track client activity
- Process and analyze logs

**To enable management features, remove `:ro`:**
```yaml
volumes:
  - /path/to/cache:/cache      # Can clear cache and remove games
  - /path/to/logs:/logs:ro     # Keep logs read-only (recommended)
```

## Admin Features

### Session Management

Control who has access to your LANCache Manager through the Admin page:

- **View All Sessions** - See every connected device and guest session
- **Device Information** - Check device names, IP addresses, operating systems, and browsers
- **Session Control** - Revoke access or logout specific devices
- **Device Limits** - Set maximum devices per API key (default: 3)
- **Activity Tracking** - Monitor last-seen timestamps and connection history

### API Key Management

- **Automatic Generation** - API key is created on first startup
- **Secure Storage** - Saved to `/data/api_key.txt`
- **Device Tracking** - Each device using the API key is tracked separately
- **Session Revocation** - Regenerating the API key revokes all active sessions

## Custom Themes

Make LANCache Manager match your style:

1. **Use Built-in Themes** - Choose from several pre-made themes including "LANCache Unofficial"
2. **Create Your Own** - Use the theme editor to customize colors and appearance
3. **Export & Share** - Download your theme as a TOML file
4. **Import Themes** - Upload TOML files from others

### Theme File Example

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

Access theme management in **Management > Themes**

## Depot Mappings

LANCache Manager automatically identifies Steam games using depot mappings from the community-maintained [lancache-pics](https://github.com/regix1/lancache-pics) repository.

The lancache-pics repository updates **every 4 hours** with the latest Steam depot information, containing **290,000+ current depot mappings**. You can download the latest mappings directly within the app - no external setup required!

**To download mappings:**
1. Go to **Management > Depot Mappings**
2. Click **"Download from GitHub"**
3. Wait 1-2 minutes for the download and import to complete

The app will automatically apply the mappings to your existing downloads.

### Adding Custom Mappings

For unrecognized games, private depots, or beta branches:

1. Go to **Management > Depot Mappings**
2. Click **"Add Custom Mapping"**
3. Enter the depot ID and game name
4. Save and reprocess logs

You can also upload your own JSON file following the lancache-pics format.

## Grafana & Prometheus Integration

Export your cache statistics to Grafana for advanced visualization and monitoring.

### Available Metrics

**Cache Statistics:**
- `lancache_cache_capacity_bytes` - Total storage capacity
- `lancache_cache_size_bytes` - Current used space
- `lancache_cache_usage_ratio` - How full your cache is (0-1)

**Bandwidth & Performance:**
- `lancache_cache_hit_bytes_total` - Bandwidth saved from cache hits
- `lancache_cache_miss_bytes_total` - New data added to cache
- `lancache_total_bytes_served` - Total data served
- `lancache_cache_hit_ratio` - Cache effectiveness (0-1)

**Activity:**
- `lancache_active_downloads` - Downloads in progress
- `lancache_active_clients` - Active client IPs
- `lancache_total_downloads` - Total downloads tracked

**Per-Service Metrics:**
- `lancache_downloads_by_service` - Downloads by service (Steam, Epic, etc.)
- `lancache_bytes_served_by_service` - Bandwidth by service

### Prometheus Setup (No Authentication)

Add this to your `prometheus.yml`:

```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    scrape_interval: 30s
    metrics_path: /metrics
```

### Prometheus Setup (With Authentication)

**1. Enable metrics authentication in your docker-compose:**

```yaml
environment:
  - Security__RequireAuthForMetrics=true
```

**2. Add your API key to Prometheus config:**

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

### Grafana Dashboard Examples

**Cache Hit Rate (Percentage)**
```promql
lancache_cache_hit_ratio * 100
```

**Bandwidth Saved Today**
```promql
increase(lancache_cache_hit_bytes_total[24h])
```

**Cache Usage (GB)**
```promql
lancache_cache_size_bytes / 1024 / 1024 / 1024
```

**Most Popular Services**
```promql
topk(5, rate(lancache_bytes_served_by_service[1h]))
```

### Testing Your Metrics

```bash
# Without authentication
curl http://localhost:8080/metrics

# With authentication
curl -H "X-Api-Key: your-key-here" http://localhost:8080/metrics
```

## API Examples

Stats endpoints are **public** and don't require authentication. Management endpoints require your **API key**.

```bash
# View dashboard stats
curl http://localhost:8080/api/stats/dashboard?period=24h

# Get top games
curl http://localhost:8080/api/stats/top-games?limit=10

# Get client statistics
curl http://localhost:8080/api/stats/clients

# Process logs (requires API key)
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/process-all-logs

# Clear cache for a specific service (requires API key)
curl -X POST -H "X-Api-Key: your-key" \
  http://localhost:8080/api/management/cache/clear/steam

# View Prometheus metrics (public by default)
curl http://localhost:8080/metrics
```

## Troubleshooting

### Logs Not Processing?

1. Check the log path in **Management > Settings**
2. Verify your volume mount matches the log path:
   ```yaml
   volumes:
     - /your/logs:/logs:ro  # Must match LanCache__LogPath
   ```
3. Click **"Process Logs"** in the Management tab
4. Check container logs for errors:
   ```bash
   docker logs lancache-manager
   ```

### Games Not Identified?

1. Download the latest depot mappings from **Management > Depot Mappings > Download from GitHub**
2. Add custom mappings for private depots or unrecognized games
3. After adding mappings, click **"Reprocess All Logs"** to update existing downloads

### Lost Your API Key?

**Option 1: Check the file**
```bash
cat ./data/api_key.txt
```

**Option 2: Check container logs**
```bash
docker logs lancache-manager | grep "API Key"
```

**Option 3: Generate a new one**
```bash
# Stop container
docker stop lancache-manager

# Delete the API key file
rm ./data/api_key.txt

# Start container (new key will be generated)
docker start lancache-manager

# Get the new key
docker logs lancache-manager | grep "API Key"
```

### Permission Issues?

If you see permission errors when trying to clear cache or remove games:

1. Check that volumes aren't mounted as read-only
2. Verify PUID and PGID match your file ownership:
   ```bash
   ls -n /path/to/cache  # Check numeric user/group IDs
   ```
3. Update docker-compose with correct IDs:
   ```yaml
   environment:
     - PUID=1006  # Your user ID
     - PGID=1006  # Your group ID
   ```

## Building from Source

**Requirements:** .NET 8 SDK, Node.js 20+, Rust 1.75+

```bash
# Clone the repository
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Build Rust processor
cd rust-processor
cargo build --release

# Build and run the web interface
cd ../Web
npm install
npm run dev  # Opens at http://localhost:3000

# Build and run the API
cd ../Api/LancacheManager
dotnet run  # Opens at http://localhost:5000
```

**Build Docker image:**

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push \
  .
```

## Contributing

Found a bug or have a feature request? [Open an issue](https://github.com/regix1/lancache-manager/issues) on GitHub!

## Support

If you find LANCache Manager useful and want to support development, consider buying me a coffee!

<div align="center">
  <div style="display: flex; justify-content: center; align-items: flex-start; gap: 60px; max-width: 800px; margin: 0 auto;">
    <div style="text-align: center; flex: 1;">
      <a href="https://www.buymeacoffee.com/regix">
        <img src="https://media0.giphy.com/media/v1.Y2lkPTc5MGI3NjExcGw1bHduZGxrNjVzYnM3ZnppZmZ1YmV5Z2J3dThjMGJnNWswejFyaiZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9cw/kmIZ4lx2ZHpr5jY0W4/giphy.gif" alt="Buy Me A Coffee" width="250" height="250">
      </a>
      <p style="margin-top: 20px; font-size: 16px;"><strong>Click to donate</strong></p>
    </div>
    <div style="text-align: center; flex: 1;">
      <a href="https://www.buymeacoffee.com/regix">
        <img src="https://github.com/user-attachments/assets/6e2ff32c-bb4c-454a-a626-03be6e6de42d" alt="Buy Me A Coffee QR Code" width="250" height="250">
      </a>
      <p style="margin-top: 20px; font-size: 16px;"><strong>Or scan QR code</strong></p>
    </div>
  </div>
</div>

## License

[MIT License](https://github.com/regix1/lancache-manager/blob/main/LICENSE)
