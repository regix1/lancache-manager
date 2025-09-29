# LanCache Manager

LanCache Manager is the control room for your existing LanCache deployment. It gives you a friendly, real-time look at who is downloading what, how well your cache is performing, and whether your hardware needs attention before the next wave of installs hits.

![Replace with a dashboard screenshot](docs/screenshots/dashboard-placeholder.png)
> Swap the placeholder above with a real capture of your dashboard. A quick annotation callout helps readers orient themselves.

## Why teams run it
- Keep an eye on bandwidth, cache hit rate, and live downloads without guesswork
- Spot which games and services (Steam, Epic, Origin, etc.) are driving traffic
- Detect new titles automatically with cover art and depot mapping
- Manage cache directories, logs, and authentication from one place
- Stream metrics into Grafana or your own tooling via WebSocket and REST APIs

## What you can show off
### Dashboard overview
![Add your dashboard screenshot here](docs/screenshots/dashboard.png)
_A 10-second glance should explain bandwidth, hit rate, and active downloads._

### Downloads & analytics
![Add your downloads screenshot here](docs/screenshots/downloads.png)
_Show the compact/normal/grouped views, filters, and data export options._

### Themes & customization
![Add your theme gallery screenshot here](docs/screenshots/themes.png)
_Highlight the theme switcher or a favorite custom theme._

## Getting started
### Prerequisites
- Docker and Docker Compose
- An existing LanCache instance with access to logs and cache storage
- Suggested resources
  - Home setups (1-10 clients): 2 CPU cores, 2 GB RAM
  - LAN parties (10-50 clients): 4 CPU cores, 4 GB RAM
  - Large events (50+ clients): 8+ CPU cores, 8+ GB RAM

### One-line Docker run
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
Visit `http://localhost:8080` (or your server address) to see the dashboard.

### Docker Compose (recommended)
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
      LanCache__ApiKey: change-me
    volumes:
      - ./data:/data
      - /path/to/lancache/logs:/logs:ro
      - /path/to/lancache/cache:/cache:ro
    restart: unless-stopped
```

## Configuration cheat sheet
- `LanCache__LogPath`: Path to your LanCache access log (nginx)
- `LanCache__CachePath`: Root of the cached game data
- `LanCache__ApiKey`: Enables API authentication for metrics and integrations
- `LanCache__Theme`: Default theme slug (optional)

Metrics and API examples:
```bash
# Prometheus metrics
curl http://localhost:8080/api/metrics

# JSON payload for dashboards or automation
curl http://localhost:8080/api/metrics/json

# Health check when using API keys
curl -H "X-Api-Key: your-api-key" http://localhost:8080/api/metrics/status
```

## Dev setup
```bash
# Clone the project
git clone https://github.com/regix1/lancache-manager.git
cd lancache-manager

# Frontend (React + Vite)
cd Web
npm install
npm run dev

# Backend (.NET 8)
cd ../Api/LancacheManager
dotnet run
```
Build for production with `npm run build` (frontend) and `dotnet publish` or `docker build` (backend).

## Need help or want to contribute?
- Open an issue for bugs, feature ideas, or installation questions
- Share themes and tips in discussions
- Long-form documentation is being refreshed; keep an eye out for new guides
- Send a pull request with fixes, docs, or new features; small contributions are welcome

## License
MIT License. Use it freely for personal events, gaming lounges, and commercial deployments.
