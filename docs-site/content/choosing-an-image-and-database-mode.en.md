<a id="image-variants"></a>

# Choosing an Image and Database Mode { #prefill-routing }

This is one decision, not two: **where does PostgreSQL run?** LANCache Manager stores everything in PostgreSQL, and the image tag follows from your answer.

| Mode | What it means | Image tag |
|------|---------------|-----------|
| **Embedded** (default) | PostgreSQL 17 runs *inside* the lancache-manager container over a Unix socket. One container, nothing extra to configure. | `:latest` |
| **External** | You run PostgreSQL yourself - a sidecar container, a remote host, or a managed service (RDS, Azure DB, Cloud SQL). Standard Docker pattern, easier upgrades. | `:latest` works, or `:latest-slim` (~150 MB smaller, drops the unused embedded Postgres). Requires `POSTGRES_MODE=external`. |

The same pairing applies to every tag family the CI publishes (all multi-arch, amd64 + arm64):

| Tag | What it is |
|-----|------------|
| `latest` / `latest-slim` | Latest release. What you should run. |
| `1.2.0` / `1.2.0-slim` | Version-pinned releases - pin one if you want explicit control over upgrades. |
| `release` / `release-slim` | Alias of `latest`. |
| `dev` / `dev-slim` | Latest dev build. Testing only - can break at any time. |

```bash
# Full - default, supports both embedded and external Postgres
docker pull ghcr.io/regix1/lancache-manager:latest

# Slim - external Postgres only
docker pull ghcr.io/regix1/lancache-manager:latest-slim
```

### Example 1: Embedded (default)

This is the [Quick Start](quick-start.md#quick-start) compose file - one container, no sidecar. Optionally add a database password:

```yaml
    environment:
      # ...everything from Quick Start, plus:
      - POSTGRES_PASSWORD=your-secure-password
```

Leave `POSTGRES_PASSWORD` unset and the first-run UI will prompt for it. That's the entire embedded setup.

### Example 2: External (sidecar Postgres)

Two services: `lancache-manager` connects over TCP to `lancache-db`.

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest-slim
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8080:80"
    volumes:
      - ./data:/data
      - /mnt/lancache/logs:/logs:ro
      - /mnt/lancache/cache:/cache:ro
      - /var/run/docker.sock:/var/run/docker.sock
    environment:
      - PUID=33
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - POSTGRES_MODE=external
      - POSTGRES_HOST=lancache-db
      - POSTGRES_PORT=5432
      - POSTGRES_DB=lancache
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=change-this-password
    depends_on:
      - lancache-db

  lancache-db:
    image: postgres:17-alpine
    container_name: lancache-db
    restart: unless-stopped
    environment:
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=change-this-password
      - POSTGRES_DB=lancache
    volumes:
      - postgres_data:/var/lib/postgresql/data

volumes:
  postgres_data:
```

`POSTGRES_PASSWORD` must match between the two services. Bring both up with `docker compose up -d`.

**Pointing at a remote or managed Postgres?** Set `POSTGRES_HOST` to its hostname, drop the `lancache-db` service, drop `depends_on`, and skip the named volume.

**Set `POSTGRES_MODE=external` but left the connection vars unset?** The app boots in setup-only mode and shows a UI form. Credentials submitted there are saved to `/data/config/postgres-credentials.json`; you'll be asked to restart the container so the new connection takes effect.

**Already running embedded and want to switch?** Your existing data does not move on its own - the automatic migration only covers the old SQLite database, not embedded Postgres. Dump and restore it by hand first: see [Migrate Embedded to External PostgreSQL](https://github.com/regix1/lancache-manager/blob/main/docs/external-postgres-migration.md).

-----
