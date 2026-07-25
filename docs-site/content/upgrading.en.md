# Upgrading { #upgrading }

Upgrading is a pull and a recreate:

```bash
docker compose pull
docker compose up -d
```

Your history survives because none of it is in the container. In embedded mode it all lives in the `/data` volume: database, API key, themes, prefill state, and settings. In external mode, keep both `/data` and your PostgreSQL storage persistent.

- **Tags:** `latest` always tracks the newest release. Pin a version tag (e.g. `1.10.4`) if you want to control exactly when upgrades happen, and change the tag to upgrade.
- **Coming from an old SQLite build?** The migration to PostgreSQL runs automatically on first start - downloads, settings, and cached data carry over with no manual steps. Some managed Postgres services forbid `ALTER SYSTEM` tuning; the migration skips it and continues.
- **What changed in a release?** See the [Releases page](https://github.com/regix1/lancache-manager/releases) for per-version notes.

-----
