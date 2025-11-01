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

*Click the ⬆ arrow on any section to return here*

- [Screenshots](#screenshots)
- [Features](#features)
- [Quick Start](#quick-start)
  - [Basic Docker Run](#basic-docker-run)
  - [Complete Docker Compose Example](#complete-docker-compose-example)
- [Getting Started](#getting-started)
- [Admin Features](#admin-features)
- [Custom Themes](#custom-themes)
- [Optimization Settings](#optimization-settings)
- [Image Caching](#image-caching)
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

### Users

<img width="1502" height="991" alt="Users Page" src="https://github.com/user-attachments/assets/820bc238-8da0-4b89-830a-3b748b962e9e" />

*Manage user sessions, view connected devices, and control access*

### Theme Examples

**LANCache Unofficial**
<img width="1527" height="1081" alt="LANCache Unofficial Theme" src="https://github.com/user-attachments/assets/215d43bc-1912-44f7-b488-4e892c61e82a" />

**Sage and Wood**
<img width="1525" height="1088" alt="Sage and Wood Theme" src="https://github.com/user-attachments/assets/1be6eb4c-6f46-4639-9942-f83a082ecc81" />

*Choose from built-in themes or create your own*

</div>

<a id="features"></a>
## Features [⬆](#-table-of-contents)

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

      # Optimization Settings

      # Enable garbage collection management (default: false)
      # Set to 'true' to enable memory optimization controls in the UI
      - Optimizations__EnableGarbageCollectionManagement=false
```

Visit **http://localhost:8080** and get your **API key** from the logs:

```bash
docker logs lancache-manager | grep "API Key"
```

**API key is also saved to `/data/api_key.txt`**

<a id="getting-started"></a>
## Getting Started [⬆](#-table-of-contents)

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

<a id="admin-features"></a>
## Admin Features [⬆](#-table-of-contents)

Access the **Users** tab (visible only to authenticated users) to manage all sessions and control access to your LANCache Manager.

### User Management

**Real-Time Session Monitoring**
- Live refresh every 3 seconds shows current connection status
- View total users, authenticated users, and guest sessions at a glance
- Track all active and historical sessions in one place

**Session Types**
- **Authenticated Users** - Full access via API key, multiple devices can share the same key (configurable limit, default: 3)
- **Guest Sessions** - Temporary 6-hour read-only access without requiring an API key

**Detailed Session Information**
- Device name and unique session ID
- IP address (with localhost detection)
- Operating system and browser
- Creation time and last seen timestamp
- Session expiry countdown (for guests)
- Revocation status and history

**Session Controls**
- **Revoke** - Immediately kick out guest users (they'll see "expired" on next request)
- **Delete** - Permanently remove session records from history
- Visual indicators for expired, revoked, and active sessions

### API Key Management

**Setup & Storage**
- API key automatically generated on first startup
- Securely saved to `/data/api_key.txt`
- Display in container logs for easy retrieval

**Multi-Device Support**
- Configure maximum devices per API key (default: 3)
- Each device tracked separately with full session details
- Regenerating the API key revokes all existing sessions

**Access Control**
```yaml
environment:
  # Maximum admin devices sharing the same API key (default: 3)
  - Security__MaxAdminDevices=3
```

<a id="custom-themes"></a>
## Custom Themes [⬆](#-table-of-contents)

Fully customize LANCache Manager to match your personal style or branding:

### Features

- **Built-in Themes** - Start with professionally designed themes like "LANCache Unofficial" and "Sage and Wood"
- **Visual Theme Editor** - Live preview as you customize colors, backgrounds, and accents
- **Community Themes** - Browse and install themes from the community gallery
- **Import/Export** - Share your creations as TOML files
- **Auto-Updates** - Installed themes can receive updates from creators
- **Theme Preview** - See changes in real-time before applying

### Creating a Custom Theme

1. Go to **Management > Preferences > Theme Management**
2. Click **"Create Theme"** or edit an existing one
3. Customize colors, backgrounds, borders, and service icons
4. Preview your changes live
5. Save and apply your theme

### Importing Community Themes

1. Go to **Management > Preferences > Theme Management**
2. Click **"Community Themes"**
3. Browse available themes with previews
4. Click **"Install"** on any theme you like
5. Optionally enable auto-updates

### Theme File Format

Themes use TOML format for easy editing:

```toml
[meta]
name = "My Theme"
id = "my-theme"
isDark = true
version = "1.0.0"
author = "Your Name"

[colors]
# Primary colors
primaryColor = "#3b82f6"
secondaryColor = "#10b981"

# Backgrounds
bgPrimary = "#111827"
bgSecondary = "#1f2937"
bgHover = "#374151"

# Text
textPrimary = "#ffffff"
textSecondary = "#d1d5db"
textMuted = "#9ca3af"

# Service icons (Steam, Epic, etc.)
steam = "#1b2838"
epic = "#2a2a2a"
# ... and more
```

### Theme Storage

Themes are saved to `/data/themes/` and can be backed up with your data directory.

<a id="optimization-settings"></a>
## Optimization Settings [⬆](#-table-of-contents)

### Memory Management (Advanced)

LANCache Manager includes optional garbage collection controls for managing memory usage. This is disabled by default and only recommended for advanced users experiencing memory issues.

**To enable:**

```yaml
environment:
  - Optimizations__EnableGarbageCollectionManagement=true
```

Once enabled, you'll see an **Optimizations** section in the Management tab where you can:

- **Set Collection Aggressiveness** - Control how often garbage collection runs (from disabled to every second)
- **Configure Memory Threshold** - Set when GC should trigger (2GB - 16GB)
- **Manual Trigger** - Run garbage collection on demand for testing

**When to use this:**
- You're running on low-memory systems (< 4GB RAM)
- You notice memory usage climbing over time
- You have limited resources and need tight memory control

**When NOT to use this:**
- Your system has adequate RAM (8GB+)
- Performance is more important than memory usage
- The default .NET memory management works fine for you

### Performance Notes

- **Disabled (default)**: Best performance, lets .NET handle memory automatically
- **On Page Load**: Balanced approach, runs GC only when you open/refresh the page
- **Timed intervals**: More aggressive, can impact performance but keeps memory lower

<a id="image-caching"></a>
## Image Caching [⬆](#-table-of-contents)

LANCache Manager automatically caches game artwork to provide instant loading and reduce bandwidth usage.

### How It Works

- **First Load**: Game images are downloaded from Steam's CDN
- **Subsequent Loads**: Images are served from local cache instantly
- **Auto-Updates**: When Steam updates game artwork, the cache automatically refreshes
- **Smart Handling**: Games without images are remembered to avoid repeated lookups

### Cache Location

Images are stored in `/data/cached-img/` inside your data directory.

### Cache Management

**Clear the cache:**
```bash
# Stop the container
docker stop lancache-manager

# Remove cached images
rm -rf ./data/cached-img

# Start the container
docker start lancache-manager
```

The cache will rebuild automatically as you browse your downloads.

### Benefits

- **Instant Loading** - Game headers appear immediately when scrolling
- **Reduced Bandwidth** - Steam images are fetched once, not repeatedly
- **Always Fresh** - Automatically detects when Steam updates artwork
- **Efficient Storage** - Only images you've seen are cached

<a id="depot-mappings"></a>
## Depot Mappings [⬆](#-table-of-contents)

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

<a id="grafana--prometheus-integration"></a>
## Grafana & Prometheus Integration [⬆](#-table-of-contents)

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

<a id="api-examples"></a>
## API Examples [⬆](#-table-of-contents)

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

<a id="troubleshooting"></a>
## Troubleshooting [⬆](#-table-of-contents)

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

<a id="building-from-source"></a>
## Building from Source [⬆](#-table-of-contents)

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

<a id="contributing"></a>
## Contributing [⬆](#-table-of-contents)

Found a bug or have a feature request? [Open an issue](https://github.com/regix1/lancache-manager/issues) on GitHub!

<a id="support"></a>
## Support [⬆](#-table-of-contents)

<div align="center">

### Enjoying LANCache Manager?

If this project has been helpful, consider supporting its development!

<br>

<a href="https://www.buymeacoffee.com/regix">
  <img src="https://media0.giphy.com/media/v1.Y2lkPTc5MGI3NjExcGw1bHduZGxrNjVzYnM3ZnppZmZ1YmV5Z2J3dThjMGJnNWswejFyaiZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9cw/kmIZ4lx2ZHpr5jY0W4/giphy.gif" alt="Buy Me A Coffee" width="600">
</a>

**[☕ Click to donate](https://www.buymeacoffee.com/regix)**

<br>

*Every coffee helps keep this project alive and growing!*

</div>

<a id="license"></a>
## License [⬆](#-table-of-contents)

[MIT License](https://github.com/regix1/lancache-manager/blob/main/LICENSE)
