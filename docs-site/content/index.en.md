# LANCache Manager

A web UI for [LANCache](https://lancache.net/). See what your cache is doing, and act on it, without touching a terminal.

The Dashboard is the live view: Bandwidth Saved, Cache Hit Ratio, Top Clients, and Service Analytics. Behind it sits Downloads, with every cached game, its cover art, size, and per-client history. Prometheus metrics are exposed out of the box, including per-game bandwidth and hit rates.

It also acts on the cache. Prefill Steam, Epic, Battle.net, Riot, and Xbox games before guests arrive, by hand or on a schedule, and run the server from the browser: Log Processing, Clear Cache, Corruption Detection and Removal, and Status Check when something looks off.

!!! important "Always pull the `latest` tag"

    GitHub's package page surfaces `:dev` because dev builds publish more often, but `:dev` is for testing only and can break at any time.

    ```bash
    docker pull ghcr.io/regix1/lancache-manager:latest
    ```

## Where to start

| If you want to | Read |
|---|---|
| Get it running | [Quick Start](quick-start.md) |
| Choose how users sign in | [Access and sign-in](sign-in.md) |
| Move up from an older version | [Upgrading](upgrading.md) |
| See what each page does | [What You Get](what-you-get.md) |
| Prefill games before an event | [Prefill](prefill.md) |
| Run against a non-Docker cache | [Bare-Metal LANCache](bare-metal-lancache.md) |
| Set up on Unraid | [Unraid / Recipes](recipes.md) |
| Split or combine cache servers | [Multiple Datasources](multiple-datasources.md) |
| Tune settings and environment variables | [Configuration Reference](configuration-reference.md) |
| Fix something that is not working | [Troubleshooting](troubleshooting.md) |
