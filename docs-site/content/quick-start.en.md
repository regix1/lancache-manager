<a id="docker-compose"></a>

# Quick Start { #quick-start }

You need two things: a running LANCache whose logs and cache this host can read, and Docker with the Compose plugin (`docker compose`). The default image bundles its own PostgreSQL, so this one file is the whole install:

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

!!! tip
    **Check your cache path before starting.** `LanCache__CachePath` must point at the directory that *directly contains* the hashed cache folders (`00`, `1a`, ... `ff`). A standard monolithic LANCache nests them one level down. If your mount shows a single `cache/` folder inside, use `LanCache__CachePath=/cache/cache` instead. Details in [Troubleshooting](troubleshooting.md#troubleshooting).

Then:

1. Grab your API key from inside the container:

   ```bash
   docker exec lancache-manager cat /data/security/api_key.txt
   ```

2. Open `http://localhost:8080` and enter the API key when prompted.
3. A setup wizard opens on first run. It checks your mount permissions, offers to import your existing log history, and sets up Steam depot mapping so downloads show real game names and cover art. You can step back through it at any point.

The wizard itself only runs once, but its steps stay available afterwards: re-import history with **Process All** in the `⋯` menu on the **Log Processing** card (**Management → Logs & Cache**), and refresh depot mapping from **Management → Data → Steam Game Mapping**.

Two notes on the mounts:

- Drop `:ro` from the `/cache` mount if you want to clear cache, remove individual games, or remove corrupted files from the UI.
- The Docker socket is only needed for prefill and nginx log rotation. Without it the **Prefill** tab is hidden rather than shown broken, so if the tab is missing, that mount is why.

<details>
<summary><strong>Prefer <code>docker run</code> for a quick test?</strong></summary>

`docker run` needs absolute host paths for its volume mounts (relative paths like `./data` only work in Compose):

```bash
docker run -d \
  --name lancache-manager \
  -p 8080:80 \
  -v /srv/lancache-manager/data:/data \
  -v /path/to/lancache/logs:/logs:ro \
  -v /path/to/lancache/cache:/cache:ro \
  -e PUID=33 \
  -e PGID=33 \
  -e TZ=America/Chicago \
  -e LanCache__LogPath=/logs/access.log \
  -e LanCache__CachePath=/cache \
  ghcr.io/regix1/lancache-manager:latest
```

</details>

-----
