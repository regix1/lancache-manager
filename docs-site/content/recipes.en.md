# Recipes { #recipes }

### Unraid { #unraid }

The repo ships a Docker template at [`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml). Save it to `/boot/config/plugins/dockerMan/templates-user/` on your Unraid box, or paste the raw-file URL into **Docker → Add Container → Template**. Then fill in the same paths and variables as the compose example. On Unraid, use `PUID=99` / `PGID=100` (the `nobody:users` default).

### Multiple Datasources { #multiple-datasources }

Most people run a single LANCache instance and never touch this section. You only need it if services are split across cache directories, or if several LANCache servers should combine into one dashboard.

A "datasource" is a paired log + cache directory. Each one is processed and tracked separately, then aggregated in the dashboard and downloads views.

Common reasons to use it:

- **Outsourced services** - Steam lives on a separate drive from everything else.
- **Multiple LANCache instances** - separate cache servers for different rooms or purposes.
- **Segmented storage** - different services on different partitions.

#### Auto-discovery (recommended)

Point the app at the parent directories and let it scan:

```yaml
environment:
  - LanCache__LogPath=/logs
  - LanCache__CachePath=/cache
  - LanCache__AutoDiscoverDatasources=true
```

Discovery walks your cache and log paths together, level by level, to a maximum of three levels below the root. That depth is fixed and not configurable. Any level where both a cache folder and a log folder have real content becomes a datasource, and finding one doesn't stop the search inside it:

1. **Root** - if `/logs/access.log` exists and `/cache` contains LANCache hash directories (`00/`, `01/`, etc.), the root becomes "Default".
2. **Nested folders** - any matched cache/log pair from level 1 to 3 becomes a datasource named after its cache folder (e.g. `/cache/steam` + `/logs/steam` → "Steam").
3. **Level 4 and deeper is never scanned** - move the folder up, or configure it manually.

The matching rules:

- **Names are matched** exactly first, then case-insensitively, then normalized (dashes, underscores, and a trailing "s" are ignored).
- **A differently-named wrapper folder doesn't block discovery.** If a cache folder and a log folder at the same level don't share a name but each holds exactly one child, that pair is followed anyway. Two folders that are already valid datasources in their own right are never paired with each other.
- **Skipped without stopping the scan:** hidden and system folders, LANCache's two-character hash buckets, symlinks, and branches it can't read.
- **A name that collides with one already found is skipped and logged**, rather than silently shadowing the first.
- **If nothing valid turns up anywhere,** the app falls back to a single `default` datasource built from the paths you configured.

Example layout with a grouping parent folder, still three datasources (Default, Steam, Epic):

```
/mnt/lancache/
├── cache/
│   ├── 00/, 01/, a1/, ff/       ← Default cache (hash dirs at root, level 0)
│   └── outsourced/
│       ├── steam/
│       │   └── 00/, 01/, ...    ← Steam, level 2
│       └── epic/
│           └── 00/, 01/, ...    ← Epic, level 2
└── logs/
    ├── access.log               ← Default log
    └── outsourced/
        ├── steam/
        │   └── access.log       ← Steam log
        └── epic/
            └── access.log       ← Epic log
```

A cache folder with no matching log folder at the same level (or the reverse) is skipped quietly. It never becomes a datasource, and nothing errors. For drives or layouts too asymmetric for auto-discovery to pair correctly, declare datasources explicitly - see Manual configuration below.

#### Manual configuration

For drives in totally separate locations or finer control, declare each datasource explicitly. Manual config wins over auto-discovery if both are set.

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
  # Only if auto-detection cannot tell which cache-key scheme this datasource uses:
  # - LanCache__DataSources__1__SchemeOverride=bare_metal
```

With matching volume mounts:

```yaml
volumes:
  - /mnt/lancache/cache:/cache:ro
  - /mnt/lancache/logs:/logs:ro
  - /mnt/steam-drive/cache:/steam-cache:ro
  - /mnt/steam-drive/logs:/steam-logs:ro
```

### Reverse Proxy (Nginx) { #nginx-reverse-proxy }

LANCache Manager runs fine behind nginx. HTTPS is recommended, and required if you plan to use guest sessions across origins (cross-origin image cookies need `Secure`).

!!! tip
    Fronting the manager with a proxy? Also set `Security__KnownProxyNetworks` (see [Security](configuration-reference.md#security)) so client IPs are reported correctly.

#### Single origin (recommended)

Serve the UI and API from the same hostname. Cookies stay first-party, CORS is a non-issue.

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
    proxy_read_timeout 600s;  # Keep at 600s or higher so idle SignalR WebSocket connections aren't dropped
  }
}

server {
  listen 80;
  server_name lancache.example.com;
  return 301 https://$host$request_uri;
}
```

#### Separate API origin (only if you must)

If the UI and API live on different hostnames:

- Build the UI with `VITE_API_URL=https://api.lancache.example.com`.
- Keep `SameSite=None; Secure` cookies (the app already sets this).
- Allow credentials in CORS for the UI origin.

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
    proxy_read_timeout 600s;  # Keep at 600s or higher so idle SignalR WebSocket connections aren't dropped
  }
}
```

### Prometheus Metrics { #grafana--prometheus }

The app exposes Prometheus metrics on `/metrics`. Scrape them, build dashboards in Grafana or any Prometheus-compatible stack, alert on cache hit ratio - whatever you need. The metrics URL and refresh settings also surface in **Management → Integrations**.

#### Available metrics

The most commonly used series:

| Metric | Description |
|--------|-------------|
| `lancache_cache_capacity_bytes` | Total storage capacity |
| `lancache_cache_used_bytes` | Currently used space |
| `lancache_cache_free_bytes` | Remaining free space |
| `lancache_cache_hit_bytes_total` | Bandwidth saved (cache hits) |
| `lancache_cache_miss_bytes_total` | New data downloaded |
| `lancache_cache_hit_ratio` | Cache effectiveness (0-1) |
| `lancache_active_downloads` | Current active downloads |
| `lancache_active_clients` | Clients seen recently |
| `lancache_service_downloads_total` | Downloads per service |
| `lancache_service_bytes_total` | Bandwidth per service |
| `lancache_service_hit_ratio` | Hit ratio per service |
| `lancache_client_bytes_total` | Bandwidth per client |

There's more where that came from - throughput, hourly breakdowns, cache growth trend, days-until-full projection, peak-hour stats, and per-service hit/miss series. Open `/metrics` in a browser to see the full set with help text.

#### Prometheus config

```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    scrape_interval: 30s
    metrics_path: /metrics
```

If you've set `Security__RequireAuthForMetrics=true`, add bearer auth:

```yaml
    authorization:
      type: Bearer
      credentials: 'your-api-key-here'
```

#### Example queries

```promql
# Cache hit rate as percentage
lancache_cache_hit_ratio * 100

# Bandwidth saved in last 24 hours
increase(lancache_cache_hit_bytes_total[24h])

# Cache usage in GB
lancache_cache_used_bytes / 1024 / 1024 / 1024
```

-----
