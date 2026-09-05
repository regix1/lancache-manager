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

Starting with 1.10.7, the first setup step asks you to choose username/password, API key plus username/password, API key plus single sign-on, single sign-on, or unauthenticated access. Existing installations confirm this choice once after upgrading. The installation API key always remains available for ownership and recovery and is associated with the first account, the primary administrator. Only the two API-key sign-in modes require entering it at login.

The primary administrator can change the method, including turning authentication off or back on, under **Users → Accounts → Access and sign-in**. Authentication is configured in the app; the Docker Compose `Security__EnableAuthentication` switch is no longer used to override a saved choice. Unauthenticated access gives anyone who can reach the app management access. Local HTTP is supported for password/API-key sign-in and unauthenticated access; use HTTPS to protect credentials and session cookies on untrusted networks. MFA, if enabled in an external sign-in service, is handled by that service; this app does not configure or enforce it.

Single sign-on is self-hosted: the app includes Google, GitHub, Microsoft, and Apple integrations, plus custom OpenID Connect. There is no central login service. Choose a preset, register your own application with that service, and enter its credentials. Register both exact callback URLs shown in setup, then use **Test connection** to complete a real sign-in. A successful test connects the verified identity to the primary administrator and activates the staged settings; a failed test leaves the current settings and working sign-in connections unchanged. Multiple tested services can be available together. Additional users must be explicitly allowed using stable identity identifiers, not email addresses or display names.

Google and GitHub require a client ID and client secret. Microsoft also requires a tenant ID, or `consumers` for personal Microsoft accounts; tenant-independent `common` and `organizations` configurations are not supported. Apple requires a Services ID, Team ID, Key ID, and `.p8` private key from your Apple Developer configuration; the server generates its short-lived client secret. Apple requires a registered HTTPS domain and does not support localhost or IP-address callbacks. Custom OpenID Connect also requires the issuer URL.

Callback URLs are return addresses already handled by this app: one for everyday sign-in, the other for testing a connection. Copy them into your service's application registration; you do not need to build or open them yourself. Open setup at the address people will use to reach the app. [Google permits HTTP localhost and loopback addresses for local testing](https://developers.google.com/identity/protocols/oauth2/web-server#uri-validation), so `http://localhost:8080` works as a local development origin when the browser and app run on the same computer. It does not point to your server from another device. For Google sign-in from other devices, use an HTTPS domain, not a raw LAN IP address. That domain can reach a server accessible only on your LAN or VPN; HTTPS does not require public hosting. Other services have their own callback requirements, including Apple's stricter restriction above.

Changing the method keeps existing account sessions; end them from **Users → Sessions** when needed. Shared unauthenticated access stops when a sign-in mode is selected. An administrator created through SSO must establish local credentials before switching to a password mode. If SSO becomes unavailable, run the existing `./data/scripts/reset-main-admin-password.sh` recovery script on the host. It uses a separate server-held recovery token, opens the recovery screen, and establishes a primary-admin session after the password is reset so the sign-in method can be repaired.

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
| [API Reference](https://regix1.github.io/lancache-manager/api-reference/) | Every endpoint, plus a plain-text summary to hand an AI assistant |
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
