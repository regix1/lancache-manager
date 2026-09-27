# Multiple Datasources { #multiple-datasources }

Most people run a single LANCache instance and never touch this section. You only need it if services are split across cache directories, or if several LANCache servers should combine into one dashboard.

A "datasource" is a paired log + cache directory. Each one is processed and tracked separately, then aggregated in the dashboard and downloads views.

The datasource name is its durable identity. Keep the name unchanged when you move or temporarily disable a source. Downloads and log observations from disabled or retired names stay readable. Global removal can use the retained URL history, but it changes files only for active datasources whose source capture completed successfully.

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

Discovery walks your cache and log paths together, level by level, to a maximum of three levels below the root. That depth is fixed and not configurable. Any level where both a cache folder and a log folder have real content becomes a datasource, and finding one doesn't stop the search inside it:

1. **Root** - if `/logs/access.log` exists and `/cache` contains LANCache hash directories (`00/`, `01/`, etc.), the root becomes "Default".
2. **Nested folders** - any matched cache/log pair from level 1 to 3 becomes a datasource named after its cache folder (e.g. `/cache/steam` + `/logs/steam` → "Steam").
3. **Level 4 and deeper is never scanned** - move the folder up, or configure it manually.

The matching rules:

- **Names are matched** exactly first, then case-insensitively, then normalized (dashes, underscores, and a trailing "s" are ignored).
- **A differently-named wrapper folder doesn't block discovery.** If a level holds exactly one cache folder and exactly one log folder and the two don't share a name, that pair is followed anyway. Two folders that are already valid datasources in their own right are never paired with each other.
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

### Deployment and writer setup

Choose the row that matches where LANCache Manager and the process writing the logs run. Directory layout can describe the files on disk, but it does not prove which process writes them.

| Deployment | Manager configuration | Mounts and writer proof | Apply the change |
| --- | --- | --- | --- |
| Native Windows, legacy or static logs | Set `LanCache__CachePath` and `LanCache__LogPath` in the app environment or app configuration. | Point to the existing cache and log directories. For copied logs, record how the copy is refreshed. | Restart LANCache Manager. |
| Native Linux with host nginx | Set the app environment to the host cache root and nginx log root. | Grant the manager account read access. Prove the host nginx process and its active configuration write those files. | Restart LANCache Manager. |
| Containers with one log file per service | Add explicit `LanCache__DataSources__N__*` environment values to the Manager service. | Mount each cache root and the directory containing the per-service `*-access.log` files. Confirm the cache containers or host nginx configuration write those mounted paths. | Recreate the Manager container. |
| Monolithic container | Set one legacy datasource or one explicit datasource for the monolithic cache and log mounts. | Mount the monolithic cache and `access.log` directory. Confirm the monolithic container owns the writer process. | Recreate the Manager container. |
| Static or imported logs | Declare an explicit datasource for the imported cache and log roots. | Provide a repeatable copy, sync, or export job and verify a new source record arrives. A directory that only looks valid is not writer proof. | Restart a native manager or recreate its container. |
| Native Windows Manager with a live Docker log writer | Set datasource values in the native app environment or configuration. | Use host paths that receive the container volumes and confirm the running container writes those exact paths. The current native Windows implementation does not support destructive changes to logs held by that Docker writer. | Run the Manager in a Linux environment with the same cache and log mounts, or provide a static or copied log source. |

Native installations read app configuration and environment variables when the process starts. Compose installations read service environment and volume definitions when the container is created, so changing the Compose file requires container recreation.

Log ingestion, status views, and other read-only operations remain separate from destructive log changes. Cache-only clearing also remains available when it does not rewrite logs. Before any physical work, a destructive action checks every eligible selected log target. The settings GET is advisory: it does not lock files or signal writers.

Docker Desktop testing on native Windows showed that a live bind-mounted log can stay attached to the old file across a replacement. This observed deployment limit does not mean every Windows filesystem lacks atomic rename. LANCache Manager denies this combination because the current implementation cannot guarantee safe publication and writer reopen for a verified Docker writer. Use a Linux Manager with the same mounts, or use a static log source, for destructive log changes.

`Enabled=true` selects a datasource for current reads, scans, and file changes. `sourceCount` reports the number of log streams found inside that one datasource; it is not a datasource count. An explicit list in which every entry is disabled has zero enabled datasources and does not create a default row.

Missing explicit or discovered roots stay unavailable. LANCache Manager does not create them or an `http` child to make an unavailable mount look empty. The legacy single-path mode can initialize its two configured roots during startup for compatibility. A later scan never recreates a missing root.
