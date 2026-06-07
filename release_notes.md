## What's New

<details>
<summary><strong>External PostgreSQL and slim image</strong></summary>

Deployments can now point at a Postgres instance you manage yourself. Set `POSTGRES_MODE=external` with `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD`, and the manager connects over TCP instead of starting an embedded server. The setup wizard adds dedicated steps for entering and validating external credentials on first launch, and the SQLite-to-Postgres migration script works in either mode.

A new `:latest-slim` image variant (and matching `:dev-slim` builds) drops the bundled Postgres binary for a roughly 150 MB smaller footprint. Slim images auto-detect the missing server and force external mode, so a sidecar or managed Postgres is required. The README now documents both image tags, embedded vs external compose examples, and when to pick each.

</details>

<details>
<summary><strong>Dashboard size accuracy</strong></summary>

Games-on-disk totals are now computed server-side with path deduplication so a shared cache file is counted once across games and services. Results are persisted in a new `CachedDetectionSummary` table after each detection scan, keeping the Used Space stat, Games on Disk count, and Service Analytics chart aligned without recomputing on every page load.

Stat card labels and tooltips now distinguish live vs historical values, and stale scan results are flagged when on-disk data may be out of date. The Recent Downloads panel and Service Analytics chart pull full-period data so chart bars and row sizes stay consistent with the dashboard header stats.

</details>

<details>
<summary><strong>Cache file scanning</strong></summary>

A new Cache File Scan scheduled service walks the cache directory on a configurable interval (default 24 hours) and refreshes the persisted Rust cache-size totals used by the dashboard Cache Files stat and unmapped-cache figures. The job appears on the Schedules page like any other background service, supports Run on Startup and Run Now, and pushes a SignalR event when a scan completes so the UI updates without a refresh.

</details>

<details>
<summary><strong>Schedule improvements</strong></summary>

The Schedules page interval picker now supports custom sub-hour intervals. Pick "Custom" and enter any value from 1 to 59 minutes, or stay with the existing hour-based presets. The dropdown shows the active interval in plain language whether you chose a preset or typed a custom value.

</details>

<details>
<summary><strong>Eviction and removal polish</strong></summary>

Automatic eviction scans in Remove mode now run silently end to end, including the follow-up data purge, so you are not flooded with progress notifications for cleanup you already opted into. Manual scans still notify as before. Post-scan recovery inserts missing detection rows for newly evicted games and services so they stay visible in the Evicted Items list, and the detection cache refreshes in place after each reconciliation pass.

</details>

<details>
<summary><strong>Prometheus and Grafana integration</strong></summary>

Prometheus scraping was restored after a security policy change had started rejecting all unauthenticated scrape requests before the metrics middleware could run. The metrics auth middleware now runs ahead of authorization and accepts both `X-Api-Key` and standard `Authorization: Bearer` headers, matching the scrape config documented in the README.

The Grafana Endpoints card in Management loads metrics security state from the API, shows whether authentication is driven by environment or UI override, and broadcasts live updates over SignalR when an admin toggles the setting.

</details>

<details>
<summary><strong>Settings that stick</strong></summary>

Guest session duration and metrics authentication settings no longer reset unexpectedly after a restart or page reload. Each setting now reports its source (environment variable vs UI override), shows the configured env value when one exists, and offers a reset button to drop the UI override and fall back to the compose or appsettings default. Changes broadcast over SignalR so every connected admin sees the effective value immediately.

</details>

<details>
<summary><strong>Epic launcher mapping</strong></summary>

The Epic Games Launcher is now seeded as a well-known non-game CDN pattern. Launcher update chunks that live under `/Builds/UnrealEngineLauncher/` are labeled correctly instead of piling up as thousands of Unknown entries in detection results.

</details>

<details>
<summary><strong>Downloads page fixes</strong></summary>

Retro view pagination and filtering are now fully server-side. Page count, visible rows, and active filters stay in sync when Group by Game is enabled, and filter state survives URL refreshes and shared links.

</details>

<details>
<summary><strong>Documentation and configuration cleanup</strong></summary>

The README was reorganized with an Image Variants section at the top, expanded PostgreSQL documentation, and trimmed compose comments that referenced removed or renamed variables. Dead `Security__MaxAdminDevices` references were removed from docs and templates. Prefill env vars were renamed to `Prefill__SteamDockerImage` / `Prefill__EpicDockerImage` to match the code, and TCP port defaults in comments were corrected. The Windows dev-setup script moved to `scripts/dev-setup.ps1`, and PUID/PGID guidance now explains when to use 33, 99, or 1000.

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

- Prometheus metrics endpoint returning 401 for all scrape requests regardless of `RequireAuthForMetrics` setting
- Dashboard Used Space and Games on Disk totals disagreeing with per-game rows and the Service Analytics chart
- Evicted-data Remove mode spamming progress and completion notifications for automatic background purges
- Newly evicted games and services missing from the Evicted Items list until a full detection rescan
- Guest session duration and metrics security UI overrides not persisting across restarts
- Retro view page count and row list diverging when filters or Group by Game were active
- Epic Games Launcher chunks appearing as Unknown in detection results
- Cache Files stat not refreshing after a background directory scan completed
- PostgreSQL entrypoint edge cases on slim images and external-mode credential handoff
- Rust processor database connection handling under external Postgres configurations
- Various button, mobile layout, and theme styling regressions across Management and the dashboard

</details>

---

Thanks for using LANCache Manager!
