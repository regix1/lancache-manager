## What's New

### External PostgreSQL & slim image

You can now run LANCache Manager against a Postgres instance you manage yourself — sidecar, remote host, or managed service (RDS, Azure, etc.).

- Set `POSTGRES_MODE=external` with `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD`.
- The setup wizard includes steps to enter and validate external credentials on first launch.
- SQLite → Postgres migration still runs automatically when upgrading from older builds.
- New **`:latest-slim`** image drops the bundled Postgres binary (~150 MB smaller). Slim images require external Postgres — they auto-detect the missing server and force external mode.
- README updated with both image tags (`:latest` vs `:latest-slim`), embedded vs external compose examples, and guidance on when to pick each.

### Dashboard & cache stats

Dashboard numbers were sometimes inconsistent because the same cache file could be counted more than once, and heavy scans ran on every page load.

- Games-on-disk totals are computed server-side with path deduplication — a shared cache file counts once across games and services.
- Scan results persist in **`CachedDetectionSummary`** after each detection run, so Used Space, Games on Disk, and Service Analytics stay aligned without recomputing every visit.
- Stat card labels and tooltips now distinguish live vs historical values; stale scan results are flagged when on-disk data may be out of date.
- Recent Downloads and Service Analytics pull full-period data so chart bars match the header stats.
- New **Cache File Scan** scheduled service walks the cache directory on a configurable interval (default 24 hours) and refreshes Rust cache-size totals used by the Cache Files stat. It appears on the Schedules page with Run on Startup / Run Now, and pushes a SignalR event when finished.

### Schedules

- The interval picker now supports **custom sub-hour intervals**. Choose **Custom** and enter any value from 1–59 minutes, or keep using the hour-based presets. The dropdown shows your active interval in plain language either way.

### Eviction & removal

- Automatic eviction scans in **Remove** mode run silently end to end, including the follow-up data purge — no flood of progress notifications for cleanup you already opted into. Manual scans still notify as before.
- Post-scan recovery inserts missing detection rows for newly evicted games and services so they appear in **Evicted Items** without waiting for a full detection rescan. The detection cache refreshes in place after each reconciliation pass.

### Monitoring (Prometheus & Grafana)

- Prometheus scraping was restored after a security policy change started rejecting unauthenticated scrape requests before the metrics middleware could run. Scrapes now accept **`X-Api-Key`** and standard **`Authorization: Bearer`** headers, matching the README scrape config.
- The **Grafana Endpoints** card in Management loads metrics security state from the API, shows whether auth is driven by environment or a UI override, and broadcasts live updates over SignalR when an admin toggles the setting.

### Settings that stick

- Guest session duration and metrics authentication UI overrides no longer reset unexpectedly after a restart or page reload.
- Each setting reports its source (environment variable vs UI override), shows the configured env value when one exists, and offers a **Reset** button to drop the override and fall back to compose/appsettings defaults. Changes broadcast over SignalR so every connected admin sees the effective value immediately.

### Downloads page

- New **Smooth / Crisp** banner scaling toggle in Display settings for Normal, Card, Retro, and Compact views. Smooth uses standard browser scaling; Crisp applies a subtle sharpness boost — handy on small thumbnails in Retro view.
- **Retro view** pagination and filtering are fully server-side. Page count, visible rows, and active filters stay in sync when **Group by Game** is enabled, and filter state survives URL refreshes and shared links.

### Setup & import

- The setup wizard no longer briefly flashes the wrong step (e.g. embedded DB form) while auth and setup status are still loading.
- **DeveLanCacheUI import** shows a clear warning when `Security__AllowedBrowsePaths` is not configured, and switches to Manual mode so you know why browse isn't available.

### Storage & permissions

The Logs & Cache page used to hide action buttons until permission checks finished, which made the UI feel slow or broken.

- Buttons now **appear immediately** with spinners while directory permissions resolve — no more pop-in after several seconds.
- Permission fetching is centralized for the Storage section instead of each manager calling the API separately.
- **Game Cache Detection**: **Expand All** and **Remove All** are visible on page load and stay disabled until the initial cache list is ready.

### Other improvements

- **Epic Games Launcher** is seeded as a well-known CDN pattern — launcher update chunks under `/Builds/UnrealEngineLauncher/` label correctly instead of piling up as thousands of Unknown entries.
- Documentation cleanup: dead env references removed, prefill vars renamed to `Prefill__SteamDockerImage` / `Prefill__EpicDockerImage`, Windows dev script moved to `scripts/dev-setup.ps1`, PUID/PGID guidance expanded.

### Bug fixes

- Prometheus `/metrics` returning 401 for all scrape requests regardless of `RequireAuthForMetrics`
- Dashboard Used Space and Games on Disk disagreeing with per-game rows and Service Analytics
- Evicted-data Remove mode spamming progress and completion notifications
- Newly evicted games/services missing from Evicted Items until a full rescan
- Guest session and metrics security UI overrides not persisting across restarts
- Retro view page count and row list diverging when filters or Group by Game were active
- Cache Files stat not updating after a background directory scan completed
- PostgreSQL entrypoint edge cases on slim images and external-mode credential handoff
- Rust processor database connection handling under external Postgres
- Various button, mobile layout, and theme styling regressions across Management and the dashboard

---

Thanks for using LANCache Manager!
