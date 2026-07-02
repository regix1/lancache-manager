# LANCache Manager

**English** | [中文](README.zh.md)

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue)](LICENSE)

A web UI for [LANCache](https://lancache.net/). Watch downloads as they happen, see what's already cached, track bandwidth saved, and prefill Steam, Epic, Battle.net, Riot, and Xbox games before your next LAN party. The web UI is available in English and 简体中文.

**What it does:**

- **Live dashboard** - downloads as they happen, bandwidth saved, top clients, service analytics
- **Cache browser** - see every cached game with cover art, sizes, and per-client history
- **Prefill** - download Steam, Epic, Battle.net, Riot, and Xbox games into your cache *before* guests arrive
- **Management** - process logs, clear cache, detect corruption, find what's cached, manage themes
- **Monitoring** - Prometheus metrics out of the box, Grafana-ready

> [!IMPORTANT]
> **Always pull the `latest` tag.** GitHub's package page surfaces `:dev` because dev builds publish more often, but `:dev` is for testing only and can break at any time.
>
> ```bash
> docker pull ghcr.io/regix1/lancache-manager:latest
> ```

-----

## Table of Contents

- [Quick Start](#quick-start)
- [Screenshots](#screenshots)
- [Choosing an Image and Database Mode](#image-variants)
- [Configuration Reference](#configuration)
  - [Volumes](#volumes)
  - [Required Settings](#required-settings)
  - [PostgreSQL](#postgresql)
  - [Security](#security)
  - [Prefill](#prefill-config)
  - [Paths and Datasources](#paths-and-datasources)
  - [Nginx Log Rotation](#nginx-log-rotation)
  - [API and Advanced](#api-and-advanced)
- [Prefill (Steam, Epic, Battle.net, Riot & Xbox)](#prefill-steam--epic)
- [Custom Themes](#custom-themes)
- [Multiple Datasources](#multiple-datasources)
- [Reverse Proxy (Nginx)](#nginx-reverse-proxy)
- [Monitoring (Grafana & Prometheus)](#grafana--prometheus)
- [Troubleshooting](#troubleshooting)
- [Building from Source](#building-from-source)
- [Contributing Translations](#contributing-translations)
- [Support and License](#support-and-license)

-----

<a id="quick-start"></a>
<a id="docker-compose"></a>
## Quick Start

Point the container at your existing LANCache logs and cache, and you're online in under a minute. The default image includes its own PostgreSQL, so this is genuinely all you need:

```yaml
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
      - /var/run/docker.sock:/var/run/docker.sock  # Optional: for prefill and log rotation
    environment:
      - PUID=33
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
```

```bash
docker compose up -d
```

Then:

1. Grab your API key from the container logs:

   ```bash
   docker logs lancache-manager | grep "API Key"
   ```

   It's also written to `/data/security/api_key.txt`.

2. Open `http://localhost:8080`.
3. Head to **Management**, paste your API key, and click **Process Logs** to import your existing cache history.

Two notes on the mounts:

- Drop `:ro` from the `/cache` mount if you want to clear cache or remove individual games from the UI.
- The Docker socket is optional - you only need it for nginx log rotation and Steam/Epic/Battle.net/Riot/Xbox prefill.

<details>
<summary><strong>Prefer <code>docker run</code> for a quick test?</strong></summary>

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

</details>

-----

<a id="screenshots"></a>
## Screenshots

<div align="center">

### Dashboard
<img alt="Dashboard Overview" src="docs/images/dashboard-overview.png" />
<img alt="Dashboard Stats" src="docs/images/dashboard-stats.png" />

*Stats at a glance with draggable cards, service analytics, and top clients*

### Downloads

**Normal View**
<img alt="Downloads - Normal View" src="docs/images/downloads-normal.png" />
<img alt="Downloads - Game Detail" src="docs/images/downloads-detail.png" />

**Compact View**
<img alt="Downloads - Compact View" src="docs/images/downloads-compact.png" />

**Retro View**
<img alt="Downloads - Retro View" src="docs/images/downloads-retro.png" />

*Three view modes to browse your cached games. Display settings include grouping, timestamps, and banner scaling (Smooth / Crisp).*

### Clients
<img alt="Clients" src="docs/images/clients.png" />

*Monitor which devices are using your cache*

### Events
<img alt="Events Calendar" src="docs/images/events.png" />

*Calendar view of download activity and LAN events*

### Management
<img alt="Management - Settings" src="docs/images/management-settings.png" />

*Authentication, demo mode, and display preferences*

<img alt="Management - Logs & Cache" src="docs/images/management-logs.png" />
<img alt="Management - Cache Operations" src="docs/images/management-cache.png" />

*Log processing, corruption detection, and game cache detection*

<img alt="Management - Client Nicknames" src="docs/images/management-clients.png" />

*Assign friendly names to client IPs and exclude clients from stats*

</div>

-----

<a id="image-variants"></a>
## Choosing an Image and Database Mode

This is one decision, not two: **where does PostgreSQL run?** LANCache Manager stores everything in PostgreSQL, and the image tag follows from your answer.

| Mode | What it means | Image tag |
|------|---------------|-----------|
| **Embedded** (default) | PostgreSQL 17 runs *inside* the lancache-manager container over a Unix socket. One container, nothing extra to configure. | `:latest` |
| **External** | You run PostgreSQL yourself - a sidecar container, a remote host, or a managed service (RDS, Azure DB, Cloud SQL). Standard Docker pattern, easier upgrades. | `:latest` works, or `:latest-slim` (~150 MB smaller, drops the unused embedded Postgres). Requires `POSTGRES_MODE=external`. |

The same pairing applies to every tag family the CI publishes (all multi-arch, amd64 + arm64):

| Tag | What it is |
|-----|------------|
| `latest` / `latest-slim` | Latest release. What you should run. |
| `1.2.0` / `1.2.0-slim` | Version-pinned releases - pin one if you want explicit control over upgrades. |
| `release` / `release-slim` | Alias of `latest`. |
| `dev` / `dev-slim` | Latest dev build. Testing only - can break at any time. |

```bash
# Full - default, supports both embedded and external Postgres
docker pull ghcr.io/regix1/lancache-manager:latest

# Slim - external Postgres only
docker pull ghcr.io/regix1/lancache-manager:latest-slim
```

### Example 1: Embedded (default)

This is the [Quick Start](#quick-start) compose file - one container, no sidecar. Optionally add a database password:

```yaml
    environment:
      # ...everything from Quick Start, plus:
      - POSTGRES_PASSWORD=your-secure-password
```

Leave `POSTGRES_PASSWORD` unset and the first-run UI will prompt for it. That's the entire embedded setup.

### Example 2: External (sidecar Postgres)

Two services: `lancache-manager` connects over TCP to `lancache-db`.

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest-slim
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
      - PUID=33
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - POSTGRES_MODE=external
      - POSTGRES_HOST=lancache-db
      - POSTGRES_PORT=5432
      - POSTGRES_DB=lancache
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=change-this-password
    depends_on:
      - lancache-db

  lancache-db:
    image: postgres:17-alpine
    container_name: lancache-db
    restart: unless-stopped
    environment:
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=change-this-password
      - POSTGRES_DB=lancache
    volumes:
      - postgres_data:/var/lib/postgresql/data

volumes:
  postgres_data:
```

`POSTGRES_PASSWORD` must match between the two services. Bring both up with `docker compose up -d`.

**Pointing at a remote or managed Postgres?** Set `POSTGRES_HOST` to its hostname, drop the `lancache-db` service, drop `depends_on`, and skip the named volume.

**Set `POSTGRES_MODE=external` but left the connection vars unset?** The app boots in setup-only mode and shows a UI form. Credentials submitted there are saved to `/data/config/postgres-credentials.json`; you'll be asked to restart the container so the new connection takes effect.

### Upgrading from an older SQLite build

The migration runs automatically on first start in either mode - your downloads, settings, and cached data carry over with no manual steps. On managed Postgres services some `ALTER SYSTEM` tuning is forbidden; the migration treats it as best-effort and continues without it.

-----

<a id="configuration"></a>
## Configuration Reference

Everything in this section is a lookup table - skim the headers and dig in where you need to. The two sections with real decisions behind them have their own walkthroughs: [database mode](#image-variants) above and [prefill networking](#prefill-network) below.

<a id="volumes"></a>
### Volumes

| Volume | Purpose | Notes |
|--------|---------|-------|
| `/data` | PostgreSQL database, security, state and config, themes, cached images | Required |
| `/logs` | LANCache access logs | Add `:ro` for read-only |
| `/cache` | LANCache cached files | Add `:ro` to monitor without touching files |
| `/var/run/docker.sock` | Docker API access | Optional. Needed for nginx log rotation and Steam/Epic/Battle.net/Riot/Xbox prefill |

<a id="required-settings"></a>
### Required Settings

| Variable | Default | Description |
|----------|---------|-------------|
| `PUID` | `1000` | User ID the app runs as. Match the owner of your cache and log files. |
| `PGID` | `1000` | Group ID the app runs as. |
| `TZ` | `UTC` | Timezone for log timestamps (e.g., `America/Chicago`). `TimeZone` is also accepted as a fallback. |
| `LanCache__LogPath` | - | Path inside the container to the LANCache access log. |
| `LanCache__CachePath` | - | Path inside the container to the LANCache cache directory. |

**Which PUID/PGID?** If unset, the entrypoint falls back to `1000:1000`. The common conventions are `33` (www-data - what the compose examples in this README use, since stock lancache setups are usually owned by www-data) and `99` (Unraid's default `nobody`). Choose based on the file ownership of your host bind-mounts - `ls -n /path/to/cache` shows it.

<a id="postgresql"></a>
### PostgreSQL

The mode decision and full compose examples live in [Choosing an Image and Database Mode](#image-variants). The variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `POSTGRES_MODE` | `embedded` | `embedded` or `external`. |
| `POSTGRES_USER` | `lancache` | PostgreSQL username. Both modes. |
| `POSTGRES_PASSWORD` | - | PostgreSQL password. In embedded mode the UI shows a setup page if this is unset. In external mode it must be set (or entered via the UI fallback before the app can connect). |
| `POSTGRES_HOST` | - | **External mode only.** Hostname or IP of the Postgres server. |
| `POSTGRES_PORT` | `5432` | **External mode only.** |
| `POSTGRES_DB` | `lancache` | Database name. Both modes. |

<a id="security"></a>
### Security

| Variable | Default | Description |
|----------|---------|-------------|
| `Security__EnableAuthentication` | `true` | Require an API key for admin actions. Only turn off for local dev. |
| `Security__GuestSessionDurationHours` | `6` | Default guest session length (also configurable in the UI). |
| `Security__RequireAuthForMetrics` | `false` | Require an API key on `/metrics`. The UI toggle in Management → Security overrides this when set. |
| `Security__ProtectSwagger` | `true` | Require auth on Swagger docs in production. |
| `Security__AllowedOrigins` | (empty) | Comma-separated CORS allow list. Empty allows all. |
| `Security__ApiKeyPath` | `/data/security/api_key.txt` | Override the file path the admin API key is read from and written to. Useful if you bind-mount secrets from outside `/data`. |
| `Security__KnownProxyNetworks` | (empty) | Comma-separated CIDR list of trusted proxy networks for `X-Forwarded-For` (e.g. `172.16.0.0/12,10.0.0.0/8`). Set this when nginx, Traefik, or another reverse proxy fronts the manager so client IPs are reported correctly. Loopback is always trusted. |
| `Security__TrustAllProxies` | `false` | Trust every upstream proxy unconditionally. Convenient for local dev. **Never enable on an internet-exposed host** - anyone can spoof a client IP. |
| `Security__ForceSecureCookies` | `false` | Force the `Secure` flag on the session cookie even when the request isn't detected as HTTPS. Enable when running behind a TLS-terminating reverse proxy. |

#### Access Levels

| Level | What you can do | Examples |
|-------|----------------|----------|
| **Admin** | Everything. Requires the API key. | Clear cache, process logs, change settings |
| **Guest** | Read-only views. Requires admin auth or a guest session. | Browse downloads, stats, events, client data |

To hand out a guest link without sharing your API key, open the **Users** tab and click **Create Guest Link**. Guests can browse the dashboard but can't change anything. Nothing is public - every endpoint requires either admin auth or a valid guest session.

<a id="prefill-config"></a>
### Prefill

Prefill auto-detects the right values for almost everything in this table. **For reliable prefill you should set `Prefill__LancacheIp` to your cache server's IP** - this makes prefill talk to your cache by IP and stop depending on DNS entirely. It is strongly recommended for Battle.net (Blizzard's CDN domains are often missing from lancache DNS, which can make prefill hang) and for any non-standard DNS setup. When it is unset the manager makes a best-effort attempt to auto-detect a running lancache server, but auto-detection can fail; setting it explicitly is the only fully reliable path. Reach for the other variables only when auto-detection gets them wrong.

For setup decisions (when do I need which override?), see [Prefill → Network setup](#prefill-network).

| Variable | Default | Description |
|----------|---------|-------------|
| `Prefill__LancacheIp` | (unset) | IP or hostname of your **cache server** (the HTTP server holding cached files, port 80). Forwarded to the daemon as `LANCACHE_IP`; the daemon then connects directly with a spoofed `Host:` header and skips DNS for CDN traffic. The single most reliable override - set this whenever your DNS isn't a stock `lancache-dns`. |
| `Prefill__LancacheDnsIp` | `auto` | IP of your **DNS server** (lancache-dns, AdGuard, Pi-hole - port 53). Written into the prefill container's `/etc/resolv.conf` so the daemon resolves CDN hostnames against it. Used in `bridge` mode only - Docker silently drops DNS overrides on `host`-network containers. `auto` reuses the IP of your detected `lancache-dns` container. |
| `Prefill__NetworkMode` | `auto` | Docker network mode for prefill containers. Accepts `host`, `bridge`, or a Docker network name. `auto` infers the mode from your `lancache-dns` container. |
| `Prefill__SteamDockerImage` | `ghcr.io/regix1/steam-prefill-daemon:latest` | Docker image used for Steam prefill containers. |
| `Prefill__EpicDockerImage` | `ghcr.io/regix1/epic-prefill-daemon:latest` | Docker image used for Epic prefill containers. |
| `Prefill__BattlenetDockerImage` | `ghcr.io/regix1/battlenet-prefill-daemon:latest` | Docker image used for Battle.net prefill containers. |
| `Prefill__RiotDockerImage` | `ghcr.io/regix1/riot-prefill-daemon:latest` | Docker image used for Riot prefill containers. |
| `Prefill__XboxDockerImage` | `ghcr.io/regix1/xbox-prefill-daemon:latest` | Docker image used for Xbox prefill containers. |
| `Prefill__SessionTimeoutMinutes` | `120` | Minutes of inactivity before an idle prefill session is cleaned up. |
| `Prefill__DaemonBasePath` | `/data/prefill` | Container path where prefill session state is stored. |
| `Prefill__HostDataPath` | `auto` | Host path that maps to the manager's `/data` volume. Detected from the manager's mount config; set explicitly only when detection fails (unusual platforms, custom volume drivers). |
| `Prefill__UseTcp` | `auto` | Communicate with the daemon over TCP instead of a Unix domain socket. `auto` resolves to `true` on Windows, `false` on Linux. *Linux users only need to set this if they want to force TCP mode.* |
| `Prefill__TcpPort` | `45555` | TCP port the daemon listens on inside its container. *Used in TCP mode only - Windows by default, Linux only when `Prefill__UseTcp=true`.* |
| `Prefill__HostTcpPort` | (random free port) | TCP port the daemon's container publishes on the host. *TCP mode only.* |
| `Prefill__TcpHost` | `127.0.0.1` | Host the daemon binds to and the manager connects to over TCP. *TCP mode only.* |

> [!NOTE]
> **TCP mode is the platform divide.** On Windows, prefill containers communicate over TCP because Windows doesn't expose Unix domain sockets to Docker. On Linux, prefill uses a Unix domain socket by default - the four TCP variables above are ignored unless you set `Prefill__UseTcp=true`. Stock Linux installs can skip the TCP rows entirely.

<a id="paths-and-datasources"></a>
### Paths and Datasources

| Variable | Default | Description |
|----------|---------|-------------|
| `LanCache__EnvFilePath` | (auto) | Path to the lancache `.env` file (used to read `CACHE_DISK_SIZE`). Searches common locations if unset. |
| `LanCache__AutoDiscoverDatasources` | `false` | Auto-detect datasources from matching subdirectories under `/cache` and `/logs`. |

If you run more than one cache instance or split services across drives, see [Multiple Datasources](#multiple-datasources).

<a id="nginx-log-rotation"></a>
### Nginx Log Rotation

| Variable | Default | Description |
|----------|---------|-------------|
| `NginxLogRotation__Enabled` | `true` | Tell nginx to reopen its logs after the app rotates them. Requires the Docker socket. |
| `NginxLogRotation__ContainerName` | `auto` | LANCache container name. `auto` finds containers with "lancache" in the name. |
| `NginxLogRotation__ScheduleHours` | `24` | How often to check whether rotation is needed. |

<a id="api-and-advanced"></a>
### API and Advanced

| Variable | Default | Description |
|----------|---------|-------------|
| `ApiOptions__MaxClientsPerRequest` | `1000` | Max clients returned in a single stats request. |
| `ApiOptions__DefaultClientsLimit` | `100` | Default client limit when none is provided. |
| `Optimizations__EnableGarbageCollectionManagement` | `false` | Show memory management controls in Management. Helpful on low-memory hosts. |
| `ASPNETCORE_URLS` | `http://+:80` | Internal port binding. Don't change unless you know exactly why. |
| `ConnectionStrings__DefaultConnection` | (auto) | Full PostgreSQL connection string override. For power users with complex setups not covered by individual `POSTGRES_*` variables. |
| `CacheSnapshots__RetentionDays` | `90` | How long to keep cache snapshots. Older snapshots are automatically deleted. |

<details>
<summary><strong>Every variable in one compose file (all optional lines commented out)</strong></summary>

```yaml
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
      # Required
      - PUID=33
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - ASPNETCORE_URLS=http://+:80

      # Security
      # - Security__EnableAuthentication=true
      # - Security__GuestSessionDurationHours=6
      # - Security__RequireAuthForMetrics=false
      # - Security__ProtectSwagger=true
      # - Security__AllowedOrigins=

      # Prefill (Steam, Epic, Battle.net, Riot & Xbox) - see Configuration > Prefill for the full reference
      # Most installs need none of these; auto-detection covers the common cases.
      # - Prefill__LancacheIp=192.168.1.10        # Cache server IP. The single most reliable override.
      # - Prefill__LancacheDnsIp=192.168.1.20     # DNS server IP. Bridge mode only.
      # - Prefill__NetworkMode=bridge             # `host`, `bridge`, or a Docker network name.
      # - Prefill__SteamDockerImage=ghcr.io/regix1/steam-prefill-daemon:latest
      # - Prefill__EpicDockerImage=ghcr.io/regix1/epic-prefill-daemon:latest
      # - Prefill__BattlenetDockerImage=ghcr.io/regix1/battlenet-prefill-daemon:latest
      # - Prefill__RiotDockerImage=ghcr.io/regix1/riot-prefill-daemon:latest
      # - Prefill__XboxDockerImage=ghcr.io/regix1/xbox-prefill-daemon:latest

      # Nginx Log Rotation
      # - NginxLogRotation__Enabled=true
      # - NginxLogRotation__ContainerName=auto
      # - NginxLogRotation__ScheduleHours=24

      # API Options
      # - ApiOptions__MaxClientsPerRequest=1000
      # - ApiOptions__DefaultClientsLimit=100

      # Optimization
      # - Optimizations__EnableGarbageCollectionManagement=false

      # Paths & Datasources
      # - LanCache__EnvFilePath=/lancache/.env
      # - LanCache__AutoDiscoverDatasources=true

      # Debugging
      # - Logging__LogLevel__LancacheManager.Infrastructure.Platform=Debug
```

</details>

-----

<a id="prefill-steam--epic"></a>
## Prefill (Steam, Epic, Battle.net, Riot & Xbox)

Prefill downloads games into your cache *before* people connect. When guests show up, every install reads from your cache instead of the public internet - full LAN speed, no bandwidth bottleneck.

Steam, Epic, Battle.net, Riot, and Xbox each run in their own container, so you can prefill all of them at the same time without them interfering. Progress streams live to the UI.

<div align="center">
<img alt="Prefill - Platform Picker" src="docs/images/prefill-home.png" />

*Pick between Steam, Epic, Battle.net, Riot, and Xbox to start a prefill session*

<img alt="Prefill - Session" src="docs/images/prefill-session.png" />

*Game selection, download settings, and a real-time activity log*
</div>

### Requirements

- Docker socket mounted (`/var/run/docker.sock`)
- Logged in as admin in lancache-manager
- Your cache server is reachable from the prefill container (see [Network setup](#prefill-network) below)

### Running a prefill

The flow is the same for both services:

1. Open the **Prefill** tab and pick **Steam**, **Epic Games**, **Battle.net**, **Riot**, or **Xbox**
2. Sign in (Steam Guard for Steam, OAuth for Epic, a Microsoft device code for Xbox; Battle.net and Riot need no login)
3. Pick games from your library
4. Hit **Start**

That's it. Leave it running - when guests arrive, everything's cached.

> [!NOTE]
> Steam prefill is built on [steam-prefill-daemon](https://github.com/regix1/steam-prefill-daemon), a fork of [steam-lancache-prefill](https://github.com/tpill90/steam-lancache-prefill) by [@tpill90](https://github.com/tpill90). Epic prefill uses [epic-prefill-daemon](https://github.com/regix1/epic-prefill-daemon). Battle.net prefill uses [battlenet-prefill-daemon](https://github.com/regix1/battlenet-prefill-daemon) and is fully anonymous - no account login required. Riot prefill uses [riot-prefill-daemon](https://github.com/regix1/riot-prefill-daemon) (a fork of [riot-lancache-prefill](https://github.com/tpill90/riot-lancache-prefill)) and is fully anonymous - no account login required; it supports League of Legends and Valorant. Xbox prefill uses [xbox-prefill-daemon](https://github.com/regix1/xbox-prefill-daemon) and signs in with a Microsoft device code (like Epic, it requires an account login).

### Importing Steam App IDs

Have a list of App IDs from `steam-lancache-prefill` or somewhere else? Skip the library browser:

1. Click **Select Apps**
2. Click **Import App IDs**
3. Paste your IDs in any of these formats:
   - Comma-separated: `730, 570, 440`
   - JSON array: `[730, 570, 440]`
   - One per line
4. Click **Import**

The dialog tells you how many games were added, how many were already selected, and how many IDs aren't in your Steam library (those are skipped at prefill time).

> [!TIP]
> **Coming from `steam-lancache-prefill`?** Open `selectedAppsToPrefill.json` and paste the contents straight into the import field - the JSON array is parsed as-is.

<a id="prefill-network"></a>
### Network setup

**Most installs need zero config.** If you run the standard `lancache` + `lancache-dns` containers, lancache-manager auto-detects them and prefill works without further setup.

If your DNS isn't a stock `lancache-dns` (you use AdGuard Home, Pi-hole, public DNS, etc.) or your routing is unusual, set one env var and you're done:

| Your setup | What to set |
|---|---|
| Stock `lancache` + `lancache-dns` containers | nothing |
| Single-box install (lancache on the same host as lancache-manager) | nothing |
| AdGuard Home, Pi-hole, or any DNS replacement | `Prefill__LancacheIp=<your-cache-ip>` |
| Host networking, host's DNS doesn't route CDN to your cache | `Prefill__LancacheIp=<your-cache-ip>` |
| Caddy/Squid/non-nginx cache that routes by `Host:` header | `Prefill__LancacheIp=<your-cache-ip>` |
| You want predictable behavior regardless of environment | always set `Prefill__LancacheIp` |

> [!TIP]
> **`Prefill__LancacheIp` is the universal override.** When set, prefill talks to your cache by IP and never asks DNS where the cache lives. Network mode and DNS server settings stop mattering for CDN traffic.

Full descriptions and defaults for `Prefill__LancacheIp`, `Prefill__LancacheDnsIp`, and `Prefill__NetworkMode` live in the [Configuration → Prefill](#prefill-config) reference table.

> [!IMPORTANT]
> **`LancacheIp` and `LancacheDnsIp` are different services, even on the same machine.**
>
> | | What it is | Port | Job |
> |---|---|---|---|
> | `LancacheIp` | The **cache server** (`lancachenet/monolithic`, or any HTTP cache) | HTTP / 80 | Holds the actual cached game files |
> | `LancacheDnsIp` | The **DNS server** (`lancachenet/lancache-dns`, AdGuard Home, Pi-hole, etc.) | DNS / 53 | Translates `lancache.steamcontent.com` into the cache's IP |
>
> Think of a small town. The **cache** is the library (where the books live). The **DNS server** is the information booth (where you ask "which way to the library?"). Both can sit in the same building - same IP, different ports - but they do completely different jobs. When you set `LancacheIp`, the daemon skips the information booth entirely and walks straight to the library. That's why it's the universal override: DNS becomes irrelevant for cache traffic.

> [!IMPORTANT]
> Steam (`api.steampowered.com`) and Epic (`*.epicgames.com`) auth and manifest endpoints still use normal DNS. `LANCACHE_IP` only redirects CDN chunk traffic - the only domains lancache caches. Your login and metadata traffic is unaffected.

#### Examples

**Most reliable** - `LancacheIp` makes CDN routing DNS-independent:

```yaml
environment:
  - Prefill__NetworkMode=host
  - Prefill__LancacheIp=192.168.1.10
```

**Bridge mode with a non-standard DNS** (e.g., AdGuard Home replacing lancache-dns):

```yaml
environment:
  - Prefill__NetworkMode=bridge
  - Prefill__LancacheIp=192.168.1.10        # cache server
  - Prefill__LancacheDnsIp=192.168.1.20     # DNS server
```

**Bridge mode, stock lancache-dns, no IP override** (legacy DNS-driven path):

```yaml
environment:
  - Prefill__NetworkMode=bridge
  - Prefill__LancacheDnsIp=192.168.1.20
```

> [!TIP]
> **Prefill container has no internet?** Try `Prefill__NetworkMode=bridge`. Some Docker setups block outbound traffic in host mode.

#### Network diagnostics

Each prefill session runs a connectivity test on startup and writes the result to logs:

```
═══════════════════════════════════════════════════════════════════════
  PREFILL CONTAINER NETWORK DIAGNOSTICS - prefill-daemon-abc123
═══════════════════════════════════════════════════════════════════════
  Internet connectivity: OK (reached api.steampowered.com)
  lancache.steamcontent.com resolved to 192.168.1.10
  DNS looks correct (private IP - likely your lancache server)
═══════════════════════════════════════════════════════════════════════
```

If the resolved IP is a public address (Steam's real CDN IPs look like `162.254.x.x`), traffic is bypassing your cache. Set `Prefill__LancacheIp` and restart the session.

<a id="prefill-routing"></a>
<details>
<summary><strong>How routing works (advanced)</strong> - exactly which path a request takes</summary>

```mermaid
flowchart TD
    Start([Prefill needs a game chunk from<br/>lancache.steamcontent.com])
    LIP{Did you set<br/>Prefill__LancacheIp?}

    Start --> LIP

    LIP -->|YES| Direct[Talk to that IP directly,<br/>no DNS lookup needed.<br/>Host header tells the cache<br/>which CDN to serve.]
    Direct --> OK([Hits your cache every time])

    LIP -->|NO| DNS[Daemon asks DNS:<br/>where does lancache.steamcontent.com<br/>actually live?]
    DNS --> Mode{Which NetworkMode<br/>is the container in?}

    Mode -->|host| HostDNS[Container uses whatever DNS<br/>the host machine uses.<br/>Prefill__LancacheDnsIp can't help -<br/>Docker silently drops it in host mode.]
    Mode -->|bridge| BridgeDNS{Did you set<br/>Prefill__LancacheDnsIp?}

    BridgeDNS -->|YES| ForcedDNS[Container queries<br/>that DNS server]
    BridgeDNS -->|NO| AutoDetect[Daemon falls back to its<br/>own auto-detect chain:<br/>CDN name, localhost,<br/>then the default gateway]

    HostDNS --> Outcome{Did DNS return<br/>your cache server's IP?}
    ForcedDNS --> Outcome
    AutoDetect --> Outcome

    Outcome -->|YES| OK
    Outcome -->|NO| Public([Got the real CDN's public IP -<br/>traffic skips your cache])

    style OK fill:#1f883d,color:#fff
    style Public fill:#cf222e,color:#fff
    style Direct fill:#0969da,color:#fff
```

Every combination, in one table:

| `NetworkMode` | `LancacheIp` | `LancacheDnsIp` | Outcome |
|:---:|:---:|:---:|---|
| `host` | set | (any) | Reliable. `LANCACHE_IP` injected; DNS irrelevant. |
| `host` | unset | (any) | Risky. DnsIp is silently dropped (Docker limitation). Works only if the host's DNS already resolves CDN to your cache. |
| `bridge` | set | unset | Reliable. `LANCACHE_IP` injected; DNS irrelevant. |
| `bridge` | set | set | Reliable. `LANCACHE_IP` for CDN, DnsIp for auth/manifest. |
| `bridge` | unset | set | Works if DnsIp resolves CDN to your cache. Container DNS forced to DnsIp. |
| `bridge` | unset | unset | Inconsistent. Auto-detect probes localhost/gateway. Works for some setups. |

**Why `LancacheIp` always works**: with the env var set, the daemon builds requests like `GET http://192.168.1.10/depot/123/chunk/abc` with `Host: lancache.steamcontent.com`. Your cache (nginx, Caddy, or any HTTP server that routes by `Host:`) sees the right hostname and serves from cache. DNS is never consulted for the CDN domain.

</details>

-----

<a id="custom-themes"></a>
## Custom Themes

Open **Management > Preferences > Theme Management** to:

- Build themes from scratch with a live preview
- Browse and install community themes
- Import and export themes as TOML

<div align="center">
<img alt="Theme Management" src="docs/images/management-themes.png" />

*Browse installed themes, switch the active theme, or import community themes*

<img alt="Theme Editor" src="docs/images/management-theme-editor.png" />

*Build your own theme with 160 customizable colors across 16 groups*
</div>

Themes live in `/data/themes/`. Here's the minimum format:

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

-----

<a id="multiple-datasources"></a>
## Multiple Datasources

Most people run a single LANCache instance and never touch this section. You only need it if you've split services across cache directories or you run more than one LANCache server and want them combined in a single dashboard.

A "datasource" is a paired log + cache directory. Each one is processed and tracked separately, then aggregated in the dashboard and downloads views.

Common reasons to use it:

- **Outsourced services** - Steam lives on a separate drive from everything else.
- **Multiple LANCache instances** - separate cache servers for different rooms or purposes.
- **Segmented storage** - different services on different partitions.

### Auto-discovery (recommended)

Point the app at the parent directories and let it scan:

```yaml
environment:
  - LanCache__LogPath=/logs
  - LanCache__CachePath=/cache
  - LanCache__AutoDiscoverDatasources=true
```

Two things get detected:

1. **Root-level datasource** - if `/logs/access.log` exists and `/cache` contains LANCache hash directories (`00/`, `01/`, etc.), it creates a "Default" datasource.
2. **Subdirectory datasources** - for each folder that exists in *both* `/cache` and `/logs`, it creates a named datasource (e.g., `/cache/steam` + `/logs/steam` becomes "Steam").

Folder matching is case-insensitive: `Steam`, `steam`, and `STEAM` all match.

Example layout that creates three datasources (Default, Steam, Epic):

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

### Manual configuration

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
```

With matching volume mounts:

```yaml
volumes:
  - /mnt/lancache/cache:/cache:ro
  - /mnt/lancache/logs:/logs:ro
  - /mnt/steam-drive/cache:/steam-cache:ro
  - /mnt/steam-drive/logs:/steam-logs:ro
```

-----

<a id="nginx-reverse-proxy"></a>
## Reverse Proxy (Nginx)

LANCache Manager runs fine behind nginx. HTTPS is recommended, and required if you plan to use guest sessions across origins (cross-origin image cookies need `Secure`).

> [!TIP]
> Fronting the manager with a proxy? Also set `Security__KnownProxyNetworks` (see [Security](#security)) so client IPs are reported correctly.

### Single origin (recommended)

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
    proxy_read_timeout 600s;  # Must match SignalR timeout (10 min) to prevent nginx killing idle WebSocket connections
  }
}

server {
  listen 80;
  server_name lancache.example.com;
  return 301 https://$host$request_uri;
}
```

### Separate API origin (only if you must)

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
    proxy_read_timeout 600s;  # Must match SignalR timeout (10 min) to prevent nginx killing idle WebSocket connections
  }
}
```

-----

<a id="grafana--prometheus"></a>
## Monitoring (Grafana & Prometheus)

The app exposes Prometheus metrics on `/metrics`. Scrape them, build dashboards, alert on cache hit ratio - whatever you need.

### Available metrics

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

### Prometheus config

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

### Example queries

```promql
# Cache hit rate as percentage
lancache_cache_hit_ratio * 100

# Bandwidth saved in last 24 hours
increase(lancache_cache_hit_bytes_total[24h])

# Cache usage in GB
lancache_cache_used_bytes / 1024 / 1024 / 1024
```

-----

<a id="troubleshooting"></a>
## Troubleshooting

### Logs aren't processing

1. Check the log path in **Management > Settings**.
2. Confirm your volume mount lines up with `LanCache__LogPath`.
3. Click **Process Logs** in Management.
4. Look at the container logs: `docker logs lancache-manager`.

### The web UI says "No log file" (but the file exists)

`LanCache__LogPath` must be the **full path to the access log file inside the container**, including the filename — e.g. `/logs/access.log`, not just `/logs`. Confirm:

1. Your volume mounts the host log directory into the container (e.g. `- /host/path/lancache/logs:/logs`).
2. `LanCache__LogPath` points at the file at that *container* path (`/logs/access.log`).
3. The file is readable from inside the container: `docker exec -it lancache-manager cat /logs/access.log | head`.

Network shares (NFS/SMB) are fine. A trailing `+` in `ls -la` output (e.g. `-rw-r--r--+`) is just an NFS ACL marker — it does **not** block reads. If `cat` prints the file, permissions are not the problem and the configured path is what to check.

### Cache size / disk usage isn't showing on the dashboard

`LanCache__CachePath` must point at the directory that **directly contains the hashed cache folders** (named like `00`, `1a`, `ff`), not its parent.

A standard lancache (monolithic) layout nests the content one level down, so if you mount your cache data directory to `/cache`, the actual cache folders usually live at `/cache/cache` — set `LanCache__CachePath=/cache/cache`. If you point it one level too high (at a folder that only contains a single `cache/` subdirectory), the dashboard shows the drive but reports no cache usage. This applies equally to network-mounted (NFS/SMB) cache directories.

Verify what's actually at the path:

```bash
docker exec -it lancache-manager ls /cache
# you should see the hashed folders (00, 1a, ... ff), not a single "cache" folder
```

### Games aren't being identified

**Steam:**

1. Pull the latest mappings from **Management > Depot Mappings**.
2. Add custom mappings for any private depots you care about.
3. Click **Reprocess All Logs** after adding mappings.

**Epic:**

1. Open **Management > Integrations** and run Epic game mapping.
2. The mapping service queries the Epic API to identify what's in your cache.
3. Game names and cover art come down automatically.

### Lost API key

```bash
cat ./data/security/api_key.txt
# or
docker logs lancache-manager | grep "API Key"
```

To rotate the key, stop the container, delete `./data/security/api_key.txt`, and start it again.

### Permission issues

Make sure `PUID` and `PGID` match the owner of your cache and log files:

```bash
ls -n /path/to/cache
```

### Prefill won't run

This applies to both Steam and Epic. Work down the list:

1. Confirm the Docker socket is mounted.
2. Confirm you're authenticated as admin in lancache-manager.
3. Look in the container logs for the network diagnostics block (`═══ PREFILL CONTAINER NETWORK DIAGNOSTICS ═══`) - it tells you whether the container has internet and where CDN domains resolve to.
4. **No internet inside the prefill container.** The container can't reach Steam or Epic. Common fixes:
   - Set `Prefill__NetworkMode=bridge` (works for most setups).
   - Confirm your Docker network has outbound internet.
   - Check firewall rules for outbound traffic.
5. **HTTP 400 errors during download.** The container can't resolve CDN domains to your cache. The most reliable fix is `Prefill__LancacheIp=<your-cache-ip>` - it bypasses DNS entirely for CDN traffic. The full decision table (and what `LancacheDnsIp`/`NetworkMode` do instead) is in [Network setup](#prefill-network).
6. **IPv6 traffic bypassing DNS.** If your network has IPv6, queries can bypass `lancache-dns`. The app already disables IPv6 in prefill containers to prevent this.
7. **Epic OAuth never connects.** Complete the OAuth flow in the browser window that pops open. The token is stored securely and persists across sessions.

To find the IP of your `lancache-dns`:

```bash
docker inspect lancache-dns | grep IPAddress
```

### Hit rate looks lower than expected after a prefill

The hit rate on the dashboard and in the default (grouped) Downloads view is byte-weighted and aggregates traffic from every client that touched the cache - including the prefill container itself. On an empty cache, a prefill run is close to 100% MISS (it's populating the cache) and the client install that follows is close to 100% HIT. Both get summed into the same total, so one game blends to roughly 50%: 50 GB MISS + 50 GB HIT = 50%. Reinstalling the same game raises the number further, because the MISS bytes are now a fixed floor while each install adds more HIT bytes to the same denominator (50% → 66% → 75% as installs pile up) - that creeping number is the sign it's a blended total, not a single install's real hit rate.

A new setting, **Exclude prefill traffic from statistics** (Management > Settings, on by default), removes the prefill container's bytes from the dashboard, per-service, per-client, and leaderboard hit-rate calculations so they reflect only real client traffic. It relies on the prefill daemons tagging their own requests, so it only takes effect with updated daemon images going forward - older images and historical data already in the database stay blended even with the setting on.

To see the real number for a specific install right now, turn off **group by game** in the Downloads tab and look at that client's own row, or grep `access.log` for the client's IP. The manager only counts literal `HIT`/`MISS` from nginx's `upstream_cache_status` (anything else is excluded from both sides), so it will always agree with a manual log count.

Even with the setting on, expect some real misses on a clean install: the prefill daemon only downloads Windows/x64/English depots by default, so other languages, platforms, and architectures always miss; an interrupted prior prefill run without `--force` leaves gaps; and HTTPS CDN traffic plus IPv6 traffic that bypasses `lancache-dns` on Linux clients never touches the cache.

If you're scraping the [Prometheus metrics](#grafana--prometheus) instead, note that `lancache_service_hit_ratio` and the other ratio series always count every client, including the prefill container. The **Exclude prefill traffic from statistics** setting applies to the web UI (dashboard, per-service, per-client, and leaderboard) but not to the Prometheus exporter, so those series stay blended.

### Debug logging

If you're chasing a path resolution, file system, or Docker socket issue, turn on verbose platform logging:

```yaml
environment:
  - Logging__LogLevel__LancacheManager.Infrastructure.Platform=Debug
```

You'll get extra detail on:

- Path resolution (container vs host paths)
- File system operations and permission checks
- Docker socket communication and container detection
- Volume mount detection
- Linux/Windows platform differences

To use it: add the variable, restart with `docker compose up -d`, reproduce the issue, then check `docker logs lancache-manager`. Remove the variable when you're done - it's noisy.

This is most useful when path auto-detection fails, prefill containers won't spawn, volume mounts look wrong, or you're on an unusual platform.

-----

<a id="building-from-source"></a>
## Building from Source

You'll need the .NET 10 SDK, Node.js 22+, and Rust 1.94+ (matching what the Dockerfile builds with).

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

Multi-arch Docker build:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/regix1/lancache-manager:latest \
  --push .
```

-----

<a id="contributing-translations"></a>
## Contributing Translations

LANCache Manager supports internationalization (i18n) and welcomes community translations. Every UI string is already externalized - there's nothing to refactor before you can translate.

### How to contribute

1. **Fork the repository** on GitHub.
2. Open `Web/src/i18n/locales/`.
3. Copy `en.json` to a file named for your language (e.g., `de.json`, `fr.json`, `es.json`, `pt-BR.json`).
4. Translate the values. Leave the keys alone.
5. Submit a **pull request**.

### File layout

```
Web/src/i18n/locales/
├── en.json          ← English (reference)
├── de.json          ← German (your contribution)
├── fr.json          ← French (your contribution)
└── ...
```

### Guidelines

- **Don't change JSON keys** - only translate the string values.
- **Preserve placeholders** - keep `{{variable}}` intact (e.g., `{{name}}`).
- **Preserve formatting** - leave HTML tags like `<strong>` alone.
- **Test locally** - run the app and verify your translations render correctly.

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

-----

<a id="support-and-license"></a>
## Support and License

Stuck on something? [Open an issue](https://github.com/regix1/lancache-manager/issues) on GitHub, or come find the LANCache community on the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

If LANCache Manager has been useful to you and you'd like to support development, you can [buy me a coffee](https://www.buymeacoffee.com/regix). Every bit helps keep the project alive.

<div align="center">

<a href="https://www.buymeacoffee.com/regix">
  <img src="https://media0.giphy.com/media/v1.Y2lkPTc5MGI3NjExcGw1bHduZGxrNjVzYnM3ZnppZmZ1YmV5Z2J3dThjMGJnNWswejFyaiZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9cw/kmIZ4lx2ZHpr5jY0W4/giphy.gif" alt="Buy Me A Coffee" width="600">
</a>

</div>

### License

LANCache Manager is licensed under the [GNU Affero General Public License v3.0 (AGPL-3.0)](LICENSE).

In plain terms:

- **Self-host it, modify it, use it however you like** for yourself, your LAN, your business, or your gaming cafe.
- **If you run a modified version for other people** (including as a hosted service), you must make your modified source available to them under the same license, with the original copyright intact.
- This keeps the project open: anyone who builds on it has to share their changes back, so it can't be turned into a closed, proprietary reskin.

Versions up to and including `1.10.3` were released under the MIT License and stay MIT. The project name and logo are covered separately by the [Trademark Policy](TRADEMARK.md). Contributions are accepted under [these terms](CONTRIBUTING.md).
