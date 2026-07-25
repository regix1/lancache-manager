# Unraid / Recipes { #recipes }

Unraid-specific setup. For everything else, use the dedicated guides:

- [Multiple Datasources](multiple-datasources.md) - split caches or combine several LANCache servers
- [Reverse Proxy](reverse-proxy.md) - put Nginx in front of the manager
- [Prometheus Metrics](prometheus-metrics.md) - scrape `/metrics` and example queries

## Unraid { #unraid }

The repo ships a Docker template at [`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml). It tracks the same paths and environment variables as [`docker-compose.yml`](https://github.com/regix1/lancache-manager/blob/main/docker-compose.yml), with Unraid defaults for `PUID=99` / `PGID=100` (`nobody:users`).

### Install the template

1. Save [`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml) to `/boot/config/plugins/dockerMan/templates-user/` on your Unraid box, **or** paste the [raw file URL](https://raw.githubusercontent.com/regix1/lancache-manager/main/unraid/lancache-manager.xml) into **Docker → Add Container → Template**.
2. Set **LANCache Logs** and **LANCache Cache** to your LANCache share paths.
3. Keep the Docker socket mounted if you want nginx log reopen and Steam / Epic / Battle.net / Riot / Xbox prefill.
4. Under **Show more settings**, set **Prefill Cache Server IP** to your LANCache HTTP IP when auto-detection is unreliable (recommended for Battle.net).
5. Apply and open the WebUI. Grab the API key with:

```bash
docker exec lancache-manager cat /data/security/api_key.txt
```

### Notes

- Required paths and variables match the Compose quick start: `/data`, `/logs`, `/cache`, `TZ`, `LanCache__LogPath`, `LanCache__CachePath`.
- Advanced settings cover PostgreSQL mode, security, all five prefill daemon images, stall timeout, nginx rotation schedule, and reverse-proxy helpers (`Security__KnownProxyNetworks`, `Security__ForceSecureCookies`).
- Host-run nginx (bare-metal) needs Extra Parameters `--pid=host` so the manager can ask nginx to reopen logs after a removal. Root (`PUID=0`) is not required.
- Full variable reference: [Configuration Reference](configuration-reference.md).
