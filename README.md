# LanCache Manager

A comprehensive web-based monitoring and management dashboard for LanCache game caching servers. Track real-time downloads, monitor cache performance, manage cached content, and analyze client usage patterns with an intuitive interface.

<img width="1533" height="1122" alt="image" src="https://github.com/user-attachments/assets/0e3db833-9464-460e-87fa-80ffd1a00f53" />

## Features

### 📊 Real-Time Monitoring
- **Live Dashboard** - Monitor cache performance, active downloads, and client activity in real-time
- **Service Distribution** - Visualize cache usage across different gaming platforms (Steam, Epic, Origin, Blizzard, etc.)
- **Download Tracking** - Track active and historical downloads with cache hit/miss ratios
- **Client Analytics** - Monitor individual client usage, bandwidth savings, and cache efficiency

### 🎮 Game Detection
- Automatic Steam game identification with cover art
- App ID mapping for better game recognition
- Support for Steam depot-to-game mapping
- Detailed game information display

### 💾 Cache Management
- View total cache size and usage statistics
- Clear entire cache or specific services
- Monitor disk space utilization
- Track bandwidth savings

### 📈 Statistics & Analytics
- Per-client bandwidth usage and savings
- Service-specific cache hit rates
- Historical download data
- Cache performance metrics with visual indicators

### 🛠️ Management Tools
- **Database Management** - Reset or clean download history
- **Log Processing** - Process historical logs or reset log position
- **Service Filtering** - Remove unwanted services from logs
- **Mock Mode** - Demo mode with sample data for testing

## Screenshots

### Dashboard
<img width="1533" height="1122" alt="image" src="https://github.com/user-attachments/assets/0e3db833-9464-460e-87fa-80ffd1a00f53" />
*Real-time overview of cache performance and activity*

### Downloads
<img width="1525" height="1063" alt="image" src="https://github.com/user-attachments/assets/2bc94a59-007d-4887-97c8-227b6637a337" />
*Detailed download tracking with game identification*

### Client Statistics
<img width="1524" height="370" alt="image" src="https://github.com/user-attachments/assets/9d3037f0-a42a-4c82-b3e3-2d5226f2dd04" />
*Per-client usage and performance metrics*

### Management
<img width="1524" height="1220" alt="image" src="https://github.com/user-attachments/assets/542798d6-50f4-4c17-bc77-5c5f40d23c19" />
*Administrative tools for cache and database management*

## Installation

### Docker Compose (Recommended)

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    user: root
    ports:
      - "8081:80"
    volumes:
      - ./data:/data
      - /path/to/lancache/logs:/logs
      - /path/to/lancache/cache:/cache
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=America/Chicago
      - ASPNETCORE_URLS=http://+:80
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - LanCache__StartFromEndOfLog=true
      - LanCache__ProcessHistoricalLogs=false
```

### Configuration

#### Required Volumes
- `/data` - Application database and persistent data
- `/logs` - LanCache access logs location
- `/cache` - LanCache cache directory (for size calculation and management)

#### Environment Variables
- `PUID/PGID` - User/Group ID for file permissions
- `TZ` - Timezone for correct timestamp display
- `LanCache__LogPath` - Path to LanCache access.log file
- `LanCache__CachePath` - Path to LanCache cache directory
- `LanCache__StartFromEndOfLog` - Start processing from end of log (true/false)
- `LanCache__ProcessHistoricalLogs` - Process all historical logs on startup (true/false)

## Usage

1. Access the web interface at `http://your-server:8081`
2. The dashboard will automatically start monitoring your LanCache logs
3. Navigate through tabs to view different statistics and management options

### Key Features

#### Cache Performance Indicators
- **Green (75-100%)** - Excellent cache performance
- **Blue (50-75%)** - Good cache performance  
- **Yellow (25-50%)** - Fair cache performance
- **Orange (0-25%)** - Low cache performance (mostly new downloads)

#### Download Tracking
- Automatic game detection for Steam downloads
- Real-time cache hit/miss ratio display
- Expandable details with game information
- Direct links to Steam store pages

#### Management Functions
- **Clear Cache** - Remove all cached files or specific services
- **Reset Database** - Clear all download history
- **Process Logs** - Manually trigger log processing
- **Remove Services** - Filter out unwanted services from logs

## Requirements

- LanCache server with accessible logs
- Docker and Docker Compose
- Network access to LanCache log and cache directories
- Minimum 2GB RAM recommended

## Tech Stack

### Backend
- ASP.NET Core 8.0
- Entity Framework Core with SQLite
- SignalR for real-time updates
- Background services for log processing

### Frontend  
- React with Vite
- Tailwind CSS for styling
- Recharts for data visualization
- Lucide React for icons

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- [LanCache.NET](https://lancache.net/) for the amazing caching solution
- [SteamDB](https://steamdb.info/) for Steam app information
- The LanCache community for feedback and support
