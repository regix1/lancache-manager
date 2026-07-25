# Multiple Datasources { #multiple-datasources }

Most people run a single LANCache instance and never touch this section. You only need it if services are split across cache directories, or if several LANCache servers should combine into one dashboard.

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

Discovery walks your cache and log paths together, level by level, to a maximum of three levels below the root. That depth is fixed and not configurable. Any level where both a cache folder and a log folder have real content becomes a datasource, and finding one doesn't stop the search inside it:

1. **Root** - if `/logs/access.log` exists and `/cache` contains LANCache hash directories (`00/`, `01/`, etc.), the root becomes "Default".
2. **Nested folders** - any matched cache/log pair from level 1 to 3 becomes a datasource named after its cache folder (e.g. `/cache/steam` + `/logs/steam` → "Steam").
3. **Level 4 and deeper is never scanned** - move the folder up, or configure it manually.

The matching rules:

- **Names are matched** exactly first, then case-insensitively, then normalized (dashes, underscores, and a trailing "s" are ignored).
- **A differently-named wrapper folder doesn't block discovery.** If a cache folder and a log folder at the same level don't share a name but each holds exactly one child, that pair is followed anyway. Two folders that are already valid datasources in their own right are never paired with each other.
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
