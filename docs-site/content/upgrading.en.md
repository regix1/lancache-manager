# Upgrading { #upgrading }

Upgrading is a pull and a recreate:

```bash
docker compose pull
docker compose up -d
```

Your history survives because none of it is in the container. In embedded mode it all lives in the `/data` volume: database, API key, themes, prefill state, and settings. In external mode, keep both `/data` and your PostgreSQL storage persistent.

- **Tags:** `latest` always tracks the newest release. Pin a version tag (e.g. `1.10.4`) if you want to control exactly when upgrades happen, and change the tag to upgrade.
- **Coming from an old SQLite build?** Upgrade to 1.10.6 or earlier first and let it start once. That release still imports the old database into PostgreSQL; later releases do not, and upgrading straight to one of them leaves the old `LancacheManager.db` in place and unread. The app starts normally in that state and simply shows no history from before the switch, with a warning in the log naming the file.
- **Reclaiming space after the switch.** Once you are happy with the new install, these are left behind and nothing reads them: `/data/db/LancacheManager.db` and its `-wal`/`-shm` siblings (or `/data/LancacheManager.db` on much older layouts), and the markers `/data/postgres-migration.complete`, `/data/postgresql/.migration_complete` and `/var/lib/postgresql/data/.migration_complete`. Delete them by hand. The old corruption-scan baselines under `/data/state/corruption-structural` are removed for you on first start.
- **What changed in a release?** See the [Releases page](https://github.com/regix1/lancache-manager/releases) for per-version notes.

-----
