# Bare-Metal LANCache { #bare-metal }

[zeropingheroes/lancache-bare-metal](https://github.com/zeropingheroes/lancache-bare-metal) runs the cache's nginx directly on the host instead of in Docker. It writes per-service log files (`steam-access.log`, `blizzard-access.log`, `epicgames-access.log`, `riot-access.log`, `windows-update-access.log`, `fallback-access.log`) to `/srv/lancache/logs/http/`, in an `http-detailed` format with no service tag. LANCache Manager reads this layout natively: mount the log and cache directories and traffic appears, with no nginx changes needed.

### Full Compose example

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    # Share the host PID namespace so the manager can find the host nginx and
    # ask it to reopen its logs after a removal or rewrite.
    pid: host
    # Docker grants CAP_KILL by default and the image keeps it while dropping
    # to the non-root PUID below. Uncomment only if your deployment uses
    # cap_drop to trim the default capability set.
    # cap_add:
    #   - KILL
    ports:
      - "8080:80"
    environment:
      - PUID=33          # keep your normal non-root IDs; root is not required
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs
      - LanCache__CachePath=/cache
    volumes:
      - ./data:/data                       # database, API key, themes, prefill state
      - /srv/lancache/logs/http:/logs:ro   # bare-metal per-service logs
      - /srv/lancache/data:/cache:ro       # drop :ro to allow cache clearing and game removal
```

With `docker run`, the matching options are `--pid=host` and, only after a `cap_drop`, `--cap-add=KILL`. Mounting the parent `/srv/lancache/logs` also works; the manager finds the `http/` folder on its own.

**Why `pid: host`:** after a removal the manager rewrites a log file, and nginx must reopen it or it keeps writing to the deleted inode. Host PID visibility plus `CAP_KILL` lets the manager signal the host nginx automatically. Without them, removals still complete, but the reopen is reported as failed so you can run `nginx -s reopen` yourself.

**Containerized nginx with bare-metal logs:** if nginx runs in a container but writes the per-service log layout, skip `pid: host` and mount the Docker socket instead (`/var/run/docker.sock:/var/run/docker.sock`).

### Log rotation

The manager never truncates or rotates bare-metal log files; its scheduled job only asks nginx to reopen the current ones. Keep log growth under the host's `logrotate`, and have the rule reopen nginx afterwards:

```text
postrotate
    nginx -s reopen
endscript
```

### What works

Everything log-based and disk-based, for the five services bare-metal caches:

- **Monitoring:** live activity, the dashboard, download history, client and service stats, and game naming (Steam depots, Blizzard products, Riot hosts).
- **Logs:** per-service log counts, log removal, and deleting log files.
- **Disk:** game and service removal, repeated-miss corruption scanning, eviction tracking, and clearing the whole cache. Every deletion double-checks the file's own embedded cache key first.

**A note on hit rates:** Blizzard and Windows Update downloads arrive in 1 MB slices, and nginx logs the cache status of only the first slice. Their hit/miss ratio is therefore an approximation (byte counts stay exact). Container installs slice the same way, so this is not bare-metal specific.

### What doesn't: Xbox

Bare-metal serves five services - Steam, Blizzard, Epic, Riot, and Windows Update - and upstream ships no Xbox Live vhost, so it never writes an `xboxlive-access.log`. Xbox traffic that does reach the cache lands in the catch-all `fallback-access.log`, and the manager never ingests that series because its records cannot be attributed to a service.

So **Xbox prefill is not supported on a bare-metal cache**. A session will run to completion, but no Xbox download ever reaches the dashboard, history, or service stats, and no disk feature can act on what it wrote. Xbox prefill needs a container-based lancache. The other four platforms - Steam, Epic, Battle.net, and Riot - each have their own bare-metal log and prefill normally.

### Alternative: switch bare-metal to the standard log format

Prefer the container-style combined log? Patch the bare-metal nginx config instead. Add the standard format to the `http {}` block of `nginx.conf`:

```nginx
log_format cachelog '[$cacheidentifier] $remote_addr / - - - [$time_local] "$request" $status $body_bytes_sent "$http_referer" "$http_user_agent" "$upstream_cache_status" "$host" "$http_range"';
```

Then in each site file under `caches-available/`, set the service name and point every site at one shared log file:

```nginx
set $cacheidentifier steam;   # blizzard / epicgames / riot / wsus per site
access_log /srv/lancache/logs/http/access.log cachelog;
```

Reload with `sudo nginx -s reload` (or `systemctl reload nginx`) and mount `/srv/lancache/logs/http` as above. **One warning, and it matters.** The combined log makes LANCache Manager read the install as a standard container, but the files on disk still carry bare-metal cache keys, so game removal and corruption scans look for files under keys that do not exist. While the old per-service logs are still present alongside the combined log, LANCache Manager sees mixed evidence and refuses those features outright. Clearing the whole cache keeps working either way. Do not remove games or scan for corruption on a patched cache.

-----
