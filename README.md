# LanCache Manager

A web-based monitoring and management dashboard for LanCache game caching servers. This application provides real-time monitoring of cache performance, detailed analytics of client usage patterns, and administrative tools for managing cached content.

## Overview

LanCache Manager processes and visualizes data from LanCache access logs to provide insights into cache performance, bandwidth savings, and client behavior. The system includes automatic game detection for Steam titles, real-time download tracking, and comprehensive cache management capabilities.

## Key Features

### Authentication and Security

The application uses an API key-based authentication system to protect management operations. On first startup, the system automatically generates a unique API key that is displayed in the container logs and saved to `/data/api_key.txt`. Each browser or device must register separately using this key to access administrative functions. The system supports multiple registered devices and includes the ability to regenerate the API key, which will revoke access for all previously registered devices.

### Monitoring Capabilities

- Real-time tracking of active downloads with cache hit/miss ratios
- Automatic identification of Steam games with metadata and cover art retrieval
- Per-client bandwidth usage statistics and cache efficiency metrics
- Service-specific analytics for Steam, Epic, Origin, Blizzard, Battle.net, and other platforms
- Historical download data with detailed performance indicators

### Management Tools

- Process entire log history or reset log processing position
- Clear cache contents globally or by specific service
- Database management for resetting download history
- Log file filtering to remove unwanted service entries
- Mock data mode for testing and demonstration purposes

## Installation

### Prerequisites

- Docker and Docker Compose installed on your system
- Access to LanCache log files (typically `/var/log/nginx/access.log`)
- Access to LanCache cache directory for size calculations
- Minimum 2GB RAM recommended for optimal performance

### Docker Compose Setup

Create a `docker-compose.yml` file with the following configuration:

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8081:80"
    volumes:
      - ./data:/data
      - /path/to/lancache/logs:/logs:ro
      - /path/to/lancache/cache:/cache:ro
    environment:
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - LanCache__StartFromEndOfLog=true
```

### Volume Configuration

The application requires three volume mounts:

1. **Data Volume** (`/data`): Stores the SQLite database, API key, and operation state. This directory must be writable.

2. **Logs Volume** (`/logs`): Mount your LanCache log directory here. The application needs read access to the access.log file. Mount as read-only (`:ro`) unless you plan to use log filtering features.

3. **Cache Volume** (`/cache`): Mount your LanCache cache directory for size calculations and cache management. Mount as read-only (`:ro`) for monitoring only, or read-write if you want to use cache clearing features.

### Environment Variables

- `TZ`: Set your timezone for accurate timestamp display
- `LanCache__LogPath`: Path to the LanCache access.log file within the container
- `LanCache__CachePath`: Path to the LanCache cache directory within the container
- `LanCache__StartFromEndOfLog`: When true, starts processing from the current end of the log file. When false, processes from the beginning

### Starting the Application

1. Create the docker-compose.yml file with your configuration
2. Run `docker-compose up -d` to start the container
3. Check the logs for the API key: `docker-compose logs lancache-manager | grep "API Key"`
4. Access the web interface at `http://your-server:8081`

## Authentication Setup

### Initial Setup

When the container starts for the first time, it generates an API key in the format `lm_[32-character-string]`. This key is:
- Displayed in the container logs
- Saved to `/data/api_key.txt` inside the container
- Required for all management operations

### Registering a Device

1. Open the web interface
2. Navigate to the Management tab
3. Click the "Authenticate" button
4. Enter the API key from the logs or api_key.txt file
5. Your device will be registered and can perform management operations

### Security Considerations

- The API key provides full administrative access to cache management functions
- Each browser/device must be registered separately
- Device registrations persist across browser sessions
- To revoke all access, use the "Regenerate Key" function in the Management tab

## Usage

### Dashboard View

The main dashboard provides an overview of cache performance, including total cache size, usage percentage, and real-time download activity. Cache hit rates are color-coded for quick performance assessment:
- Green (75-100%): Excellent cache performance
- Blue (50-75%): Good cache performance
- Yellow (25-50%): Moderate cache performance
- Orange (0-25%): Low cache performance

### Log Processing

The application continuously monitors the LanCache access.log file for new entries. For historical data processing:
1. Navigate to the Management tab
2. Authenticate with your API key
3. Click "Process All Logs" to import historical data

Processing speed varies based on log size but typically handles 50-100MB of logs per minute.

### Cache Management

To clear cached content:
1. Navigate to the Management tab
2. Use "Clear All Cached Files" to remove all content
3. Or select specific services to clear individual game platforms

Note: Cache clearing operations cannot be undone and will force clients to re-download content.

## Technical Details

### Backend Architecture

Built with ASP.NET Core 8.0, the application uses:
- Entity Framework Core with SQLite for data persistence
- SignalR for real-time WebSocket communication
- Background services for continuous log processing
- Channel-based architecture for high-throughput log parsing

### Frontend Technology

The web interface is built with:
- React 18 with Vite for fast development builds
- Tailwind CSS for responsive styling
- Recharts for data visualization
- Microsoft SignalR client for real-time updates

### Performance Characteristics

- Processes 60,000+ log entries per minute
- Supports log files up to several gigabytes
- Real-time updates with sub-second latency
- Efficient SQLite database with automatic cleanup of old entries

## Troubleshooting

### Common Issues

**Cannot find API key**: Check container logs with `docker logs lancache-manager` or look in the mounted data directory at `/data/api_key.txt`

**Log processing not starting**: Ensure the log file path is correct and the container has read permissions

**Cache size showing as 0**: Verify the cache path is correctly mounted and accessible

**Real-time updates not working**: Check that port 80 is accessible within the container and WebSocket connections are not blocked

### Log Locations

- Application logs: Use `docker logs lancache-manager`
- API key location: `/data/api_key.txt` (inside container)
- Database location: `/data/lancache.db` (inside container)
- Position tracking: `/data/logposition.txt` (inside container)

## License

This project is licensed under the MIT License.

## Acknowledgments

This project integrates with LanCache.NET for game content caching. Steam game identification utilizes public Steam API data and community-maintained depot mappings.