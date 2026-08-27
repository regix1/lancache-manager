# Prometheus Metrics { #grafana--prometheus }

The app exposes Prometheus metrics on `/metrics`. Scrape them, build dashboards in Grafana or any Prometheus-compatible stack, alert on cache hit ratio - whatever you need. The metrics URL and refresh settings also surface in **Management → Integrations**.

!!! note "Prefill traffic is counted here, the same as on the dashboard"

    The prefill daemon downloads through the cache like any other client, so its traffic is recorded under its own client IP, normally `127.0.0.1`. Both `/metrics` and the dashboard count it, and the two agree.

    A prefill run is nearly all MISS bytes, so it pulls every hit-ratio series down for a while. [Troubleshooting](troubleshooting.md#troubleshooting) explains the arithmetic and how to filter the daemon out of a view.

!!! warning "Client Exclusions now apply to `/metrics`"

    A client excluded under **Management → Clients** is now left out of `/metrics` as well as out of the app's own screens. It disappears from `lancache_client_bytes_total` and the rest of the `lancache_client_*` series, and its bytes stop counting toward the `lancache_service_*` families and the global totals. Both modes are honored: **Stats only**, which keeps a client visible in Downloads and live speeds but out of totals and hit rates, and **Hidden**, which removes it everywhere.

    If you have any client excluded, those series drop on the release carrying this change. Your history is not rewritten, but every value from that point on is lower than the equivalent value before it. Re-check any `rate()` panel or alert threshold you tuned against the old numbers.

    This is also how to keep prefill traffic out of Prometheus. Exclude the daemon's client IP, normally `127.0.0.1`, under **Management → Clients**.

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

### Per-game metrics

| Metric | Description |
|--------|-------------|
| `lancache_game_bytes` | Bytes served for this game, all time |
| `lancache_game_cache_hit_bytes` | Bytes served from cache |
| `lancache_game_cache_miss_bytes` | Bytes fetched from upstream |
| `lancache_game_downloads` | Download count |
| `lancache_game_cache_hit_ratio` | Hit ratio for this game (0-1) |
| `lancache_game_active_downloads` | Downloads active now or finished in the last 5 minutes |

All six carry the same three labels: `service`, `game` and `app_id`. The `service` value is lowercased and matches the value `lancache_service_bytes_total` already uses, so the two families join. Note that Xbox traffic appears under its raw service names here, while the app's own screens fold them together.

`app_id` is the Steam app id for Steam titles, the Epic app id for Epic titles, and `0` for named games from Blizzard, Riot and Xbox - the same `0` the cache detection stores for them. It exists so a series keeps its identity when a game is renamed in the catalog.

Only the top games by all-time bytes are exported, 50 by default, and you can change the cap in **Management → Integrations → Refresh and scrape rates**. A game that drops out of the top N stops being exported rather than reporting zero, so its line ends cleanly instead of falling to the floor. Changing the cap takes effect on the next refresh cycle, so give it up to one full data refresh interval.

These six count every download the cache recorded, including downloads whose files have since been evicted, which matches the rest of the download metrics on this page.

### Games on disk

| Metric | Description |
|--------|-------------|
| `lancache_game_cache_bytes` | Bytes this game occupies on disk (not deduplicated) |
| `lancache_game_cache_files` | Cache files found for this game |
| `lancache_games_on_disk_bytes` | Total bytes attributed to games, deduplicated |
| `lancache_games_on_disk_count` | Number of games found on disk |
| `lancache_identified_service_bytes` | Total bytes attributed to a service but to no game, deduplicated |
| `lancache_detection_computed_timestamp` | When the on-disk numbers were last computed, as a unix timestamp |

The on-disk family skips evicted games, which is what the Games on Disk view does, so the two agree. `lancache_detection_computed_timestamp` is there because these numbers only recompute on a cache scan or a removal. Without it a flat graph looks the same whether nothing changed or nothing ran.

!!! note "Per-game on-disk bytes do not add up to the total"

    `lancache_games_on_disk_bytes` is deduplicated across every game and every service, because lancache shares cached objects between titles. The per-game `lancache_game_cache_bytes` values are raw and count a shared object once for each game that uses it.

    Summing the per-game series therefore gives a larger number than the headline. That is expected, not a bug, and the app's own dashboard shows the same two numbers side by side.

### Steam mapping coverage

| Metric | Description |
|--------|-------------|
| `lancache_steam_unknown_game_bytes` | Steam bytes served for downloads with no resolved game name |
| `lancache_steam_unknown_game_downloads` | Steam downloads with no resolved game name |

These are Steam-only on purpose. WSUS and bare-metal traffic carries no game name by design, so a metric counting every nameless download would report a perfectly healthy install as broken.

### LAN events

| Metric | Description |
|--------|-------------|
| `lancache_event_bytes` | Bytes served for downloads tagged to this event |
| `lancache_event_cache_hit_bytes` | Cache hit bytes for this event |
| `lancache_event_downloads` | Download session count for this event |
| `lancache_event_cache_hit_ratio` | Hit ratio for this event (0-1) |

All four carry `event` (the display name) and `event_id`. They count tagged downloads only, the same set the dashboard event filter uses, and they honour client exclusions like the rest of `/metrics`.

Only the top 50 events by bytes are exported. An event with no tagged downloads is omitted rather than reporting zero. These are gauges: graph them directly. The dashboard's elapsed-from-start overlay is not exported here — Prometheus scrapes totals, not hour-by-hour from each party's start.

### Which of these are safe with rate()

A name ending in `_total` conventionally means a counter, and `rate()`, `irate()` and `increase()` are built for counters. **None of the new metrics above carries `_total`, and none of them is safe to use with `rate()`.**

They are all gauges and they can legitimately go down. Old download rows are cleaned up when a service stops appearing in the log files, and eviction removes games from disk. `rate()` reads any decrease as a counter reset and turns it into a large spike that never happened.

Graph them directly instead, or use `delta()` or `deriv()` if you want a rate of change. `avg_over_time()`, `max_over_time()`, `topk()` and `sum()` are all fine.

This does make the new names inconsistent with `lancache_service_bytes_total` and its neighbours, which are also gauges but shipped with a `_total` suffix. Those keep their names, because renaming them would break every dashboard already using them. New names describe what they actually are rather than copying the older mistake.

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

# Top 10 games by bandwidth
topk(10, lancache_game_bytes)

# Hit rate per game as percentage
lancache_game_cache_hit_ratio * 100

# Share of Steam bytes that could not be matched to a game
lancache_steam_unknown_game_bytes / lancache_service_bytes_total{service="steam"}

# Bytes served per LAN event
topk(8, lancache_event_bytes)
```

That last query mixes suffixes and it is not a typo. `lancache_steam_unknown_game_bytes` is one of the new gauges and carries no `_total`; `lancache_service_bytes_total{service="steam"}` is an existing series that keeps the suffix it shipped with. Both sides are gauges measuring bytes, so the ratio is correct as written.
