# Troubleshooting { #troubleshooting }

Before working through the entries below, open **Management → Status Check**. It tests DNS, cache reachability, and whether recent downloads used the cache, and it will often name the problem outright.

### Logs aren't processing

1. Check the log path under **Management → Logs & Cache → Log Processing**.
2. Confirm your volume mount lines up with `LanCache__LogPath`.
3. Choose **Process All** from the `⋯` menu on that same page.
4. Look at the container logs: `docker logs lancache-manager`.

### The web UI says "No log file" (but the file exists)

`LanCache__LogPath` must be the **full path to the access log file inside the container**, including the filename - e.g. `/logs/access.log`, not just `/logs`. Confirm:

1. Your volume mounts the host log directory into the container (e.g. `- /host/path/lancache/logs:/logs`).
2. `LanCache__LogPath` points at the file at that *container* path (`/logs/access.log`).
3. The file is readable from inside the container: `docker exec -it lancache-manager cat /logs/access.log | head`.

Network shares (NFS/SMB) are fine. A trailing `+` in `ls -la` output (e.g. `-rw-r--r--+`) is just an NFS ACL marker - it does **not** block reads. If `cat` prints the file, permissions are not the problem and the configured path is what to check.

### Cache size / disk usage isn't showing on the dashboard

`LanCache__CachePath` must point at the directory that **directly contains the hashed cache folders** (named like `00`, `1a`, `ff`), not its parent.

A standard lancache (monolithic) layout nests the content one level down. If you mount your cache data directory to `/cache`, the actual folders usually live at `/cache/cache` - so set `LanCache__CachePath=/cache/cache`. Point it one level too high and the dashboard shows the drive but reports no cache usage. Network-mounted (NFS/SMB) caches behave the same.

Verify what's actually at the path:

```bash
docker exec -it lancache-manager ls /cache
# you should see the hashed folders (00, 1a, ... ff), not a single "cache" folder
```

### Games aren't being identified

**Steam:**

1. Refresh the mappings: **Management → Schedules**, find the **Steam Game Mapping** row, and hit **Run Now** (the play icon at the end of the row).
2. Add custom mappings for any private depots you care about.
3. Re-import the history: in **Logs & Cache**, choose **Reposition** from the `⋯` menu on the **Log Processing** card to move the read position back to the start, then process again. Already-processed entries are skipped automatically, so reprocessing is safe.

**Epic:**

1. Sign in to Epic under **Management → Integrations**.
2. Go to **Management → Schedules**, find the **Epic Game Mapping** row, and hit **Run Now**. The mapping service queries the Epic API to identify what's in your cache.
3. Game names and cover art come down automatically.

### Lost API key

```bash
# from the host, if /data is bind-mounted (the default docker-compose.yml)
cat ./data/security/api_key.txt

# from anywhere, reading straight from inside the container (also works with named volumes)
docker exec lancache-manager cat /data/security/api_key.txt
```

On Windows Git Bash, prefix the `docker exec` form with `MSYS_NO_PATHCONV=1` (`MSYS_NO_PATHCONV=1 docker exec lancache-manager cat /data/security/api_key.txt`) or the path gets rewritten to a local Windows path.

To rotate the key, stop the container, delete `./data/security/api_key.txt`, and start it again.

### Permission issues

Make sure `PUID` and `PGID` match the owner of your cache and log files:

```bash
ls -n /path/to/cache
```

### Prefill won't run

The checklist is the same for all five platforms. Work down the list:

1. Confirm the Docker socket is mounted.
2. Confirm you're authenticated as admin in lancache-manager.
3. Look in the container logs for the network diagnostics block (`═══ PREFILL CONTAINER NETWORK DIAGNOSTICS ═══`) - it tells you whether the container has internet and where CDN domains resolve to.
4. **No internet inside the prefill container.** The container can't reach the platform's servers. Common fixes:
   - Set `Prefill__NetworkMode=bridge` (works for most setups).
   - Confirm your Docker network has outbound internet.
   - Check firewall rules for outbound traffic.
5. **HTTP 400 errors during download.** The container can't resolve CDN domains to your cache. The most reliable fix is `Prefill__LancacheIp=<your-cache-ip>` - it bypasses DNS entirely for CDN traffic. The full decision table (and what `LancacheDnsIp`/`NetworkMode` do instead) is in [Network setup](prefill.md#prefill-network).
6. **IPv6 traffic bypassing DNS.** If your network has IPv6, queries can bypass `lancache-dns`. The app already disables IPv6 in prefill containers to prevent this.
7. **Epic OAuth never connects.** Complete the OAuth flow in the browser window that pops open. The token is stored securely and persists across sessions.

To find the IP of your `lancache-dns`:

```bash
docker inspect lancache-dns | grep IPAddress
```

### A scheduled prefill "skipped" services

That's [Scheduled Prefill](prefill.md#scheduled-prefill) working as designed: a scheduled run only uses a persistent container that is already running and, for Steam/Epic/Xbox, signed in. Start the container from the Scheduled Prefill card and sign in if needed; the next run will include it. Skips don't count as failures, and the schedule keeps ticking.

### Hit rate looks lower than expected after a prefill

Usually nothing is wrong - the number includes the prefill itself. The dashboard hit rate is byte-weighted across every client that touched the cache, including the prefill container (the [Prometheus](recipes.md#grafana--prometheus) `lancache_service_hit_ratio` metrics blend the same way).

On an empty cache, a prefill run is close to 100% MISS and the install that follows is close to 100% HIT. Together, one game blends to roughly 50%. Each reinstall pushes the number toward 66%, 75%, and higher, because the MISS bytes are a fixed floor while every install adds more HIT bytes.

To hide the prefill traffic from view, filter out client `127.0.0.1` in the Downloads/Dashboard client filter. The daemon runs on the same host, so this hides its traffic without touching the underlying data.

For a specific install's real hit rate, switch Downloads to the **Retro** view, open the settings panel, leave **Group by game** unchecked under **Display**, and check that client's own row - or grep `access.log` for its IP. The manager only counts literal `HIT`/`MISS` from nginx's `upstream_cache_status`, so it always agrees with a manual log count.

Some real misses are normal even on a clean install:

- the prefill daemon only grabs Windows/x64/English depots by default
- an interrupted earlier run without `--force` leaves gaps
- HTTPS/IPv6 traffic that bypasses `lancache-dns` never touches the cache

### Debug logging

Turn this on when path auto-detection fails, prefill containers won't spawn, volume mounts look wrong, or you're on an unusual platform:

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

-----
