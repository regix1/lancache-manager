# Migrate Embedded → External PostgreSQL

`POSTGRES_MODE=external` switches the app to a sidecar/remote Postgres. Auto-migration only handles SQLite → Postgres, so existing embedded-Postgres data must be moved manually.

## 1. Dump (while still embedded)

```bash
docker exec lancache-manager \
    su - postgres -c "pg_dump -d lancache --no-owner --no-acl --clean --if-exists" \
    > lancache-dump.sql
```

## 2. Add sidecar to `docker-compose.yml`

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:dev
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8081:80"
    volumes:
      - ./data:/data
      - /mnt/logs:/logs
      - /mnt/cache/cache:/cache
      - /var/run/docker.sock:/var/run/docker.sock:ro
    environment:
      - PUID=1006
      - PGID=1006
      - TZ=America/New_York
      - LanCache__LogPath=/logs
      - LanCache__CachePath=/cache
      - LanCache__AutoDiscoverDatasources=true
      - Security__EnableAuthentication=true
      - Security__MaxAdminDevices=3
      - Security__RequireAuthForMetrics=false
      - NginxLogRotation__Enabled=true
      - Optimizations__EnableGarbageCollectionManagement=false
      - POSTGRES_MODE=external
      - POSTGRES_HOST=lancache-db
      - POSTGRES_PORT=5432
      - POSTGRES_DB=lancache
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=lancache_pw_change_me_42
    depends_on:
      - lancache-db

  lancache-db:
    image: postgres:17-alpine
    container_name: lancache-db
    restart: unless-stopped
    environment:
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=lancache_pw_change_me_42
      - POSTGRES_DB=lancache
    volumes:
      - postgres_data:/var/lib/postgresql/data

volumes:
  postgres_data:
```

`POSTGRES_PASSWORD` must match in both services.

## 3. Start sidecar, restore, restart app

```bash
docker compose up -d lancache-db
docker exec -i lancache-db psql -U lancache -d lancache < lancache-dump.sql
docker compose up -d lancache-manager
```

## 4. Verify

```bash
docker logs lancache-manager | grep "External target"
docker exec lancache-db psql -U lancache -d lancache -c 'SELECT COUNT(*) FROM "Downloads";'
```

## 5. (Optional) Reclaim space

```bash
docker compose stop lancache-manager
sudo rm -rf ./data/postgresql
docker compose start lancache-manager
```

## Rollback

Remove the six `POSTGRES_*` env vars, the `depends_on`, the `lancache-db` service, and the `postgres_data` volume. Restart. The embedded data in `./data/postgresql/` is still there (unless you ran step 5).
