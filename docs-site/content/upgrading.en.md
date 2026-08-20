# Upgrading { #upgrading }

Upgrading is a pull and a recreate:

```bash
docker compose pull
docker compose up -d
```

Your history survives because none of it is in the container. In embedded mode it all lives in the `/data` volume: database, API key, themes, prefill state, and settings. In external mode, keep both `/data` and your PostgreSQL storage persistent.

- **Tags:** `latest` always tracks the newest release. Pin a version tag (e.g. `1.10.4`) if you want to control exactly when upgrades happen, and change the tag to upgrade.
- **Coming from an old SQLite build? Read this before upgrading.** Releases after 1.10.5 no longer import the old database, and they **delete it on first start**. Upgrading straight to one of them destroys every download recorded before the switch, permanently and with no way back. If you want that history, upgrade to 1.10.5 first, let it start once so the import runs, confirm your downloads are there, and only then upgrade further. If you do not want it, upgrade directly and the old files are cleaned up for you.
- **What gets cleaned up.** On first start the app removes `/data/db/LancacheManager.db` with its `-wal`/`-shm` siblings (or `/data/LancacheManager.db` on much older layouts), the markers `/data/postgres-migration.complete` and `/data/postgresql/.migration_complete`, and the old corruption-scan baselines under `/data/state/corruption-structural`. The database and corruption-baseline removals are named in the log.
- **What changed in a release?** See the [Releases page](https://github.com/regix1/lancache-manager/releases) for per-version notes.

-----
