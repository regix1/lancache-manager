# LANCache Manager

**English** | [中文](README.zh.md)

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue)](LICENSE)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy_Me_A_Coffee-support_the_project-yellow)](https://www.buymeacoffee.com/regix)
[![Documentation](https://img.shields.io/badge/Docs-lancache--manager-purple)](https://regix1.github.io/lancache-manager/)

A web UI for [LANCache](https://lancache.net/). See what your cache is doing, and act on it, without touching a terminal. Available in English and 简体中文.

The dashboard is the live view: bandwidth saved, cache hit ratio, top clients, and per-service analytics. Behind it sits a full cache browser with every cached game, its cover art, size, and per-client history. Prometheus metrics are exposed out of the box.

It also acts on the cache. Prefill Steam, Epic, Battle.net, Riot, and Xbox games before guests arrive, by hand or on a schedule, and run the server from the browser: process logs, clear cache, detect corruption, and check DNS and cache health when something looks off.

> [!IMPORTANT]
> **Always pull the `latest` tag.** GitHub's package page surfaces `:dev` because dev builds publish more often, but `:dev` is for testing only and can break at any time.
>
> ```bash
> docker pull ghcr.io/regix1/lancache-manager:latest
> ```

## Quick start

You need a running LANCache whose logs and cache this host can read, and Docker with the Compose plugin. The default image bundles its own PostgreSQL, so this is the whole install:

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

Then open `http://<host>:8080` and get your API key with `docker exec lancache-manager cat /data/security/api_key.txt`.

Full walkthrough, including the cache path gotcha that catches most people, is in the [Quick Start guide](https://regix1.github.io/lancache-manager/quick-start/).

## Documentation

The full documentation lives at **[regix1.github.io/lancache-manager](https://regix1.github.io/lancache-manager/)**.

| Guide | What it covers |
|---|---|
| [Quick Start](https://regix1.github.io/lancache-manager/quick-start/) | Install, first run, API key |
| [Upgrading](https://regix1.github.io/lancache-manager/upgrading/) | Moving up a version, what carries over |
| [What You Get](https://regix1.github.io/lancache-manager/what-you-get/) | A tour of every page in the app |
| [Prefill](https://regix1.github.io/lancache-manager/prefill/) | Steam, Epic, Battle.net, Riot, Xbox, and scheduling |
| [Bare-Metal LANCache](https://regix1.github.io/lancache-manager/bare-metal-lancache/) | Running against a non-Docker cache |
| [Images and Database Modes](https://regix1.github.io/lancache-manager/choosing-an-image-and-database-mode/) | Embedded vs external PostgreSQL |
| [Configuration Reference](https://regix1.github.io/lancache-manager/configuration-reference/) | Every environment variable and volume |
| [Unraid / Recipes](https://regix1.github.io/lancache-manager/recipes/) | Unraid Docker template setup |
| [Multiple Datasources](https://regix1.github.io/lancache-manager/multiple-datasources/) | Split caches or combine several LANCache servers |
| [Reverse Proxy](https://regix1.github.io/lancache-manager/reverse-proxy/) | Nginx in front of the manager |
| [Prometheus Metrics](https://regix1.github.io/lancache-manager/prometheus-metrics/) | Scraping `/metrics` and example queries |
| [Troubleshooting](https://regix1.github.io/lancache-manager/troubleshooting/) | Logs not processing, games not identified, permissions |
| [Custom Themes](https://regix1.github.io/lancache-manager/custom-themes/) | Building and sharing a theme |
| [Building from Source](https://regix1.github.io/lancache-manager/building-from-source/) | Local development |
| [Contributing Translations](https://regix1.github.io/lancache-manager/contributing-translations/) | Adding or improving a language |

## Support

Stuck on something? [Open an issue](https://github.com/regix1/lancache-manager/issues), or find the LANCache community on the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

If LANCache Manager has been useful and you want to support development, you can [buy me a coffee](https://www.buymeacoffee.com/regix).

## License

Licensed under the [GNU Affero General Public License v3.0](LICENSE). Self-host it, modify it, and use it however you like. If you run a modified version for other people, including as a hosted service, you must offer those users the corresponding source of your modified version under the same license.

Versions up to and including `1.10.3` were released under MIT and stay MIT. The name and logo are covered by the [Trademark Policy](TRADEMARK.md). Contributions are accepted under [these terms](CONTRIBUTING.md). Full details on the [Support and License](https://regix1.github.io/lancache-manager/support-and-license/) page.
