# Configuration Reference { #configuration }

This section is a lookup table. Skim the headers and dig in where you need to. The two settings with a real decision behind them have their own walkthroughs: [database mode](choosing-an-image-and-database-mode.md#image-variants) and [prefill networking](prefill.md#prefill-network).

For a normal install, start from the checked-in [`docker-compose.yml`](https://github.com/regix1/lancache-manager/blob/main/docker-compose.yml). For every supported variable in one place, jump to the [complete annotated Compose example](#complete-compose) at the end of this section.

### Volumes { #volumes }

| Volume | Purpose | Notes |
|--------|---------|-------|
| `/data` | PostgreSQL database, security, state and config, themes, cached images | Required |
| `/logs` | LANCache access logs | Add `:ro` for read-only |
| `/cache` | LANCache cached files | Add `:ro` to monitor without touching files |
| `/var/run/docker.sock` | Docker API access | Optional. Needed for nginx log rotation and Steam/Epic/Battle.net/Riot/Xbox prefill |

### Required Settings { #required-settings }

| Variable | Default | Description |
|----------|---------|-------------|
| `PUID` | `33` (shipped Compose file) | User ID the app runs as. Match the owner of your cache and log files. |
| `PGID` | `33` (shipped Compose file) | Group ID the app runs as. |
| `TZ` | `UTC` | Timezone for log timestamps (e.g., `America/Chicago`). `TimeZone` is also accepted as a fallback. |
| `LanCache__LogPath` | - | Path inside the container to the LANCache access log. |
| `LanCache__CachePath` | - | Path inside the container to the LANCache cache directory. |

**Which PUID/PGID?** Match the owner of your cache and log files - `ls -n /path/to/cache` shows it. The shipped Compose file uses `33:33` (www-data), which fits most stock lancache setups. Unraid uses `99:100`. If you run the raw image without `PUID` or `PGID`, the entrypoint falls back to `1000` for whichever one is missing. Treat that as a fallback, not a recommended value.

### PostgreSQL { #postgresql }

The mode decision and full compose examples live in [Choosing an Image and Database Mode](choosing-an-image-and-database-mode.md#image-variants). The variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `POSTGRES_MODE` | `embedded` | `embedded` or `external`. |
| `POSTGRES_USER` | `lancache` | PostgreSQL username. Both modes. |
| `POSTGRES_PASSWORD` | - | PostgreSQL password. In embedded mode the UI shows a setup page if this is unset. In external mode it must be set (or entered via the UI fallback before the app can connect). |
| `POSTGRES_HOST` | - | **External mode only.** Hostname or IP of the Postgres server. |
| `POSTGRES_PORT` | `5432` | **External mode only.** |
| `POSTGRES_DB` | `lancache` | Database name. Both modes. |

### Security { #security }

| Variable | Default | Description |
|----------|---------|-------------|
| `Security__EnableAuthentication` | `true` | Require an API key for admin actions. Only turn off for local dev. |
| `Security__GuestSessionDurationHours` | `6` | Default guest session length (also configurable in the UI). |
| `Security__RequireAuthForMetrics` | `false` | Require an API key on `/metrics`. The UI toggle in Management → Integrations overrides this when set. |
| `Security__ProtectSwagger` | `true` | Require auth on Swagger docs in production. |
| `Security__AllowedOrigins` | (empty) | Comma-separated CORS allow list. Empty allows all. |
| `Security__ApiKeyPath` | `/data/security/api_key.txt` | Override the file path the admin API key is read from and written to. Useful if you bind-mount secrets from outside `/data`. |
| `Security__KnownProxyNetworks` | (empty) | Comma-separated CIDR list of trusted proxy networks for `X-Forwarded-For` (e.g. `172.16.0.0/12,10.0.0.0/8`). Set this when nginx, Traefik, or another reverse proxy fronts the manager so client IPs are reported correctly. Loopback is always trusted. |
| `Security__TrustAllProxies` | `false` | Trust every upstream proxy unconditionally. Convenient for local dev. **Never enable on an internet-exposed host** - anyone can spoof a client IP. |
| `Security__ForceSecureCookies` | `false` | Force the `Secure` flag on the session cookie even when the request isn't detected as HTTPS. Enable when running behind a TLS-terminating reverse proxy. |

Not every `/metrics` setting has an environment variable. The update interval, the Prometheus scrape interval used in the sample config, and how many games the per-game series export are all set in Management → Integrations. See [Prometheus Metrics](prometheus-metrics.md#grafana--prometheus).

#### Access Levels

| Level | What you can do | Examples |
|-------|----------------|----------|
| **Admin** | Everything. Requires the API key. | Clear cache, process logs, change settings |
| **Guest** | Read-only views. Requires admin auth or a guest session. | Browse downloads, stats, events, client data |

To give someone read-only access without sharing your API key, make sure guest logins are **Unlocked** on the **Users** page. Your guest then clicks **Guest Mode** on the sign-in screen. Session length and other defaults live under **Guest Defaults**. Every page and action needs either admin auth or a guest session, with one exception: `/metrics` is public unless you set `Security__RequireAuthForMetrics=true`.

### Prefill settings { #prefill-config }

Prefill auto-detects the right values for almost everything in this table. Three things to know before reaching for it:

- **Set `Prefill__LancacheIp` to your cache server's IP and prefill stops depending on DNS.** It matters most for Battle.net, whose CDN domains are often missing from lancache DNS and can make prefill hang.
- Leave it unset and the manager auto-detects your cache. It probes likely candidates with a quick health check and only uses an address that answered like a real lancache.
- Reach for the other variables only when auto-detection gets them wrong. The decision table lives in [Network setup](prefill.md#prefill-network).

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
| `Prefill__SessionTimeoutMinutes` | `120` | Total lifetime of a non-persistent admin prefill session. Guest and persistent sessions use their own limits. |
| `Prefill__StallTimeoutSeconds` | `180` | Advanced. No-progress time before a non-persistent session counts as stalled. Scheduled Prefill uses its own 30-minute cutoff. |
| `Prefill__DaemonBasePath` | `/data/prefill` | Container path where prefill session state is stored. |
| `Prefill__HostDataPath` | `auto` | Host path that maps to the manager's `/data` volume. Detected from the manager's mount config; set explicitly only when detection fails (unusual platforms, custom volume drivers). |
| `Prefill__UseTcp` | `auto` | Communicate with the daemon over TCP instead of a Unix domain socket. `auto` resolves to `true` on Windows, `false` on Linux. *Linux users only need to set this if they want to force TCP mode.* |
| `Prefill__TcpPort` | `45555` | TCP port the daemon listens on inside its container. *Used in TCP mode only - Windows by default, Linux only when `Prefill__UseTcp=true`.* |
| `Prefill__HostTcpPort` | (random free port) | TCP port the daemon's container publishes on the host. *TCP mode only.* |
| `Prefill__TcpHost` | `127.0.0.1` | Host the daemon binds to and the manager connects to over TCP. *TCP mode only.* |

!!! note
    **TCP mode is the platform divide.** On Windows, prefill containers communicate over TCP because Windows doesn't expose Unix domain sockets to Docker. On Linux, prefill uses a Unix domain socket by default - the four TCP variables above are ignored unless you set `Prefill__UseTcp=true`. Stock Linux installs can skip the TCP rows entirely.

### Paths and Datasources { #paths-and-datasources }

| Variable | Default | Description |
|----------|---------|-------------|
| `LanCache__EnvFilePath` | (auto) | Path to the lancache `.env` file (used to read `CACHE_DISK_SIZE`). Searches common locations if unset. |
| `LanCache__AutoDiscoverDatasources` | `false` | Auto-detect datasources from matching subdirectories under `/cache` and `/logs`, up to three levels deep. |
| `LanCache__DataSources__<n>__SchemeOverride` | `auto` | Cache-key scheme for one datasource: `auto`, `monolithic`, or `bare_metal`. Only needed when custom log filenames defeat auto-detection and the datasource is left read-only for disk actions. Also settable per datasource in the UI. |

If you run more than one cache instance or split services across drives, see [Multiple Datasources](multiple-datasources.md).

### Nginx Log Rotation { #nginx-log-rotation }

| Variable | Default | Description |
|----------|---------|-------------|
| `NginxLogRotation__Enabled` | `true` | Tell nginx to reopen its logs after the app rewrites them. Containerized nginx uses the Docker socket; nginx running directly on the host uses the host PID namespace and sufficient signal privilege. |
| `NginxLogRotation__ContainerName` | (empty = auto-detect) | LANCache container name. When empty (or set to `auto`), the app finds containers with "lancache" in the name. |
| `NginxLogRotation__ScheduleHours` | `24` | How often to check whether rotation is needed. |

### API and Advanced { #api-and-advanced }

| Variable | Default | Description |
|----------|---------|-------------|
| `ApiOptions__MaxClientsPerRequest` | `1000` | Max clients returned in a single stats request. |
| `ApiOptions__DefaultClientsLimit` | `100` | Default client limit when none is provided. |
| `Optimizations__EnableGarbageCollectionManagement` | `false` | Show memory management controls in Management. Helpful on low-memory hosts. |
| `ASPNETCORE_URLS` | `http://+:80` | Internal port binding. Don't change unless you know exactly why. |
| `ConnectionStrings__DefaultConnection` | (auto) | Full PostgreSQL connection string override. For power users with complex setups not covered by individual `POSTGRES_*` variables. |
| `CacheSnapshots__RetentionDays` | `90` | How long to keep cache snapshots. Older snapshots are automatically deleted. |
| `CacheSnapshots__IntervalMinutes` | `60` | Advanced. How often to record a cache-size snapshot. |

### Complete annotated Compose example { #complete-compose }

Prefer one file that lists everything? The example below is a complete, working compose file. The active lines match Quick Start; every optional setting is present but commented, with its default and when it matters, so copying it is safe.

<details markdown>
<summary><strong>Complete annotated Compose example</strong> - every supported variable</summary>

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8080:80"
    volumes:
      - ./data:/data                                # database, API key, themes, prefill state
      - /mnt/lancache/logs:/logs:ro                 # LANCache access logs
      - /mnt/lancache/cache:/cache:ro               # drop :ro to allow cache clearing and game removal
      - /var/run/docker.sock:/var/run/docker.sock   # optional: prefill and containerized nginx log rotation
    environment:
      # --- Required (same as Quick Start) ---
      - PUID=33                                 # user id the app runs as; 33 = shipped Compose value (www-data). Unraid: 99
      - PGID=33                                 # group id; 33 = shipped Compose value. Unraid: 100
      - TZ=America/Chicago                      # IANA timezone; default UTC
      - LanCache__LogPath=/logs/access.log      # access log inside the container
      - LanCache__CachePath=/cache              # cache dir inside the container

      # --- PostgreSQL (the defaults run embedded Postgres with no extra setup) ---
      # - POSTGRES_MODE=embedded                # embedded (default) or external; the slim image is external-only
      # - POSTGRES_USER=lancache                # default lancache
      # - POSTGRES_PASSWORD=                    # secret; leave unset and the first-run page asks for one
      # External mode only:
      # - POSTGRES_HOST=lancache-db
      # - POSTGRES_PORT=5432
      # - POSTGRES_DB=lancache

      # --- Security ---
      # - Security__EnableAuthentication=true     # false turns off ALL auth; local dev only
      # - Security__RequireAuthForMetrics=false   # true = /metrics needs a Bearer token
      # - Security__GuestSessionDurationHours=6
      # - Security__AllowedOrigins=               # CSV of CORS origins; empty allows all
      # - Security__ProtectSwagger=true
      # - Security__ForceSecureCookies=false      # set true behind a TLS-terminating proxy
      # - Security__KnownProxyNetworks=           # CSV CIDRs of trusted proxies, e.g. 172.16.0.0/12
      # - Security__TrustAllProxies=false         # never true on an internet-exposed host
      # - Security__ApiKeyPath=/data/security/api_key.txt

      # --- Prefill (auto-detected; set these only when detection fails) ---
      # - Prefill__LancacheIp=192.168.1.10        # cache server IP; the most reliable override
      # - Prefill__NetworkMode=auto               # host, bridge, a Docker network name, or auto
      # - Prefill__LancacheDnsIp=auto             # DNS server IP; bridge mode only
      # - Prefill__SteamDockerImage=ghcr.io/regix1/steam-prefill-daemon:latest
      # - Prefill__EpicDockerImage=ghcr.io/regix1/epic-prefill-daemon:latest
      # - Prefill__BattlenetDockerImage=ghcr.io/regix1/battlenet-prefill-daemon:latest
      # - Prefill__RiotDockerImage=ghcr.io/regix1/riot-prefill-daemon:latest
      # - Prefill__XboxDockerImage=ghcr.io/regix1/xbox-prefill-daemon:latest
      # - Prefill__SessionTimeoutMinutes=120      # lifetime of a non-persistent admin session
      # - Prefill__StallTimeoutSeconds=180        # advanced: stall cutoff for non-persistent sessions
      # - Prefill__DaemonBasePath=/data/prefill   # must stay under /data
      # - Prefill__HostDataPath=auto              # host path of /data; set only if detection fails
      # - Prefill__UseTcp=auto                    # auto = TCP on Windows, Unix socket on Linux
      # - Prefill__TcpPort=45555                  # TCP mode only
      # - Prefill__HostTcpPort=                   # TCP mode only; empty picks a free port
      # - Prefill__TcpHost=127.0.0.1              # TCP mode only

      # --- Nginx log rotation (docker.sock for containers; pid: host for host nginx) ---
      # - NginxLogRotation__Enabled=true
      # - NginxLogRotation__ContainerName=        # empty or "auto" finds the "lancache" container
      # - NginxLogRotation__ScheduleHours=24

      # --- API, optimization, cache snapshots ---
      # - ApiOptions__MaxClientsPerRequest=1000
      # - ApiOptions__DefaultClientsLimit=100
      # - Optimizations__EnableGarbageCollectionManagement=false   # low-memory hosts only
      # - CacheSnapshots__RetentionDays=90        # cache-size history retention
      # - CacheSnapshots__IntervalMinutes=60      # advanced: how often to record a snapshot
      # - ASPNETCORE_URLS=http://+:80             # internal bind; leave as-is

      # --- Paths and datasources ---
      # - LanCache__EnvFilePath=/lancache/.env    # unset = auto-search common locations
      # - LanCache__AutoDiscoverDatasources=false # scan /cache and /logs for matching subfolders, up to 3 levels deep
      # Multiple datasources replace LogPath/CachePath above. Keep the numbers
      # contiguous and add more the same way: __2__, __3__, ...
      # - LanCache__DataSources__0__Name=Default
      # - LanCache__DataSources__0__CachePath=/cache
      # - LanCache__DataSources__0__LogPath=/logs
      # - LanCache__DataSources__0__Enabled=true
      # - LanCache__DataSources__1__Name=Steam
      # - LanCache__DataSources__1__CachePath=/steam-cache
      # - LanCache__DataSources__1__LogPath=/steam-logs
      # - LanCache__DataSources__1__Enabled=true

      # --- Power users ---
      # - ConnectionStrings__DefaultConnection=Host=/var/run/postgresql;Database=lancache;Username=lancache;Maximum Pool Size=20;Minimum Pool Size=2   # overrides POSTGRES_*; secret if it embeds a password
      # - Logging__LogLevel__LancacheManager.Infrastructure.Platform=Debug   # any logging category; values Trace..None
```

</details>

**Coming from 1.10.3?** Three variables were added, all optional: `Prefill__XboxDockerImage` (Xbox is the fifth prefill platform), the advanced `Prefill__StallTimeoutSeconds`, and the per-datasource `LanCache__DataSources__<n>__SchemeOverride` for bare-metal installs whose log filenames defeat auto-detection. Nothing was renamed or removed, so existing Compose files keep working. One cleanup: `Security__MaxAdminDevices` is an old no-op setting that current code ignores - you can delete it.

-----
