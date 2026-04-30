## What's New

<details>
<summary><strong>PostgreSQL replaces SQLite</strong></summary>

The entire data layer has moved from SQLite to PostgreSQL 17. New installs spin up a tuned Postgres instance inside the container automatically, and the Setup page walks you through picking credentials on first launch. Existing users get a one-time importer that copies downloads, sessions, depots, detections, and preferences without losing history.

The dashboard and operation history are dramatically faster as a result — especially on busy LANs — and the migration lifts the 32-bit row-id ceiling that very-active caches were starting to hit. On Windows, the manager auto-pulls a Postgres 17 container when port 5432 is not already serving so dev matches production without manual setup.

</details>

<details>
<summary><strong>Service Schedules</strong></summary>

A new Schedules section in Management lists every periodic background job in one place. Each service card shows current status, last run, next run, and a live countdown. You can pick an interval from a dropdown, tick "Run on startup", or hit "Run Now" — the card briefly glows when the job finishes. A "Run All" button kicks off every service in one click and "Reset to Defaults" restores factory intervals across all eleven services. Cards in Storage and Integrations also gain a "View Schedule" link that scrolls the matching card into view and flashes its border.

A new Performance Optimizations card under Settings lets you enable or disable automatic .NET garbage collection and set a memory threshold (512 MB to 32 GB) above which GC fires. When enabled the service joins the Schedules page like any other job, and a manual trigger reports how much memory was actually freed.

</details>

<details>
<summary><strong>Eviction-aware cache and removal</strong></summary>

A periodic reconciliation pass — and an explicit "Scan now" button — checks whether the bytes for each tracked download are still on disk, marks anything nginx has evicted, and propagates that into per-game and per-service cards. Games that reappear are auto-unflagged on the next scan. Every download row now wears a red "Evicted" or yellow "Partially evicted" badge inline alongside the service label, so you can spot eviction state at a glance without opening the dedicated list.

When you remove a game or service, the manager deletes matching log lines, drops detection rows, and clears cache files in one streamed pipeline with granular per-file progress. Heavy operations also check for in-flight conflicts before starting and refuse with a clear message instead of stomping on each other.

</details>

<details>
<summary><strong>Real game sizes on every row</strong></summary>

Game and service rows across the dashboard, downloads list, recent downloads panel, and detection cards now show the actual on-disk install size for each game rather than cumulative cache-hit/miss bandwidth. A 70 GB game served twenty times is still 70 GB on your disk, not 1.4 TB. When a file is evicted, the size drops to zero instead of continuing to show historical bandwidth that no longer corresponds to anything physical.

The Service Analytics chart's new Games view ranks games by on-disk size, the Compare view stacks per-service hits and misses without conflating bandwidth with capacity, and the Cache Removal confirmation modal shows you exactly how much disk space will be freed before you confirm.

</details>

<details>
<summary><strong>Faster dashboard and redesigned charts</strong></summary>

The dashboard now loads from a single batch endpoint with skeleton loaders rather than six independent fetches that flash through partial states. A server-side cache warmer — configurable in the Schedules page — keeps the batch hot so opening the page after the server has been idle no longer shows a cold spinner. Stat values digit-spin smoothly when numbers change, and sparklines re-render in place on updates without flashing.

The Service Analytics chart switches between five views — Services, Compare, Cache Hits, Cache Misses, and Games — with a single segmented control. Two new widgets join the dashboard: Peak Usage Hours and Cache Growth Trend, with a Top Clients table. Time-range presets live in a segmented control with a custom date-range picker.

</details>

<details>
<summary><strong>Faster game image loading</strong></summary>

Game artwork for both Steam and Epic now loads through a single backend proxy. The frontend tracks which app IDs actually have an image available and skips network requests for the rest, so the dashboard, downloads list, and detection tables stop hammering the server with broken-image requests.

Images are cached in the database and served from there rather than hitting the CDN on every page load. After a detection scan finishes, a shared cache-buster fires so newly available artwork appears without a hard refresh.

</details>

<details>
<summary><strong>Downloads page</strong></summary>

The downloads list now uses virtualized rendering so thousands of rows scroll without lag. Each view (Compact, Normal, Retro, Card) remembers its own preferences — items per page, banner column, evicted display mode — between visits, and Retro view pagination is fully server-side so page count and visible rows always match, even with the "Group by Game" toggle.

Filter selections, search queries, and active tabs now save to the URL so refreshing or sharing the link keeps you exactly where you were. A "Back to top" button appears once you have scrolled past 300px.

</details>

<details>
<summary><strong>Events and client groups</strong></summary>

A new Events tab adds a calendar with month navigation and a list view, including a "LIVE" badge for active events and per-day popovers on mobile. Mark a date range as a named event and the dashboard tags every download that happened in that window so you can break down totals and cache hit rates per event.

Client Groups let you group LAN IPs under a friendly nickname so the dashboard, top-clients widgets, and event downloads roll multiple machines into a single row. Sessions now also surface country, city, and ISP for the connecting public IP.

</details>

<details>
<summary><strong>Setup wizard and authentication</strong></summary>

The first-run wizard now walks through permissions, platform setup, database init, depot bootstrapping, log processing, and historical-data import — each with its own progress card. Missing prerequisites surface a clear error rather than silently failing mid-run.

Session tokens are now first-class GUIDs with a tolerant parser that accepts legacy formats. Prefill permissions are split per platform with separate expiry timestamps, so you can grant Steam prefill access without also handing over Epic. Active prefill sessions are persisted with a platform tag and expiry so admins can see what is running and time-bound guest access. Admins can also view, validate, and regenerate the API key used by external integrations like Grafana directly from the UI instead of digging through the container filesystem.

</details>

<details>
<summary><strong>Management page refresh</strong></summary>

Management is now broken into labelled sub-sections — Storage, Data, Integrations, Settings, Preferences, Display, Clients, and Prefill Sessions — each with card-based layout and a side nav so you can jump between them without re-mounting the page.

A new Data sub-section lets you migrate your entire download history out of a DeveLanCacheUI database directly into LANCache Manager — point at the `.db` file with the integrated file browser, choose whether to overwrite existing records, and watch live progress as it imports. The same form also handles SQLite to Postgres migration for users coming from earlier versions.

The theme editor's create and edit modals share the same form; themes can now be imported from a community catalog or exported as validated JSON. A new Memory Diagnostics page shows live heap breakdown and GC counts with a manual GC button — useful on constrained hosts. The manager also supports multiple nginx datasources simultaneously, and a directory permission monitor surfaces common Docker bind-mount problems in the UI rather than failing silently.

</details>

<details>
<summary><strong>Prefill</strong></summary>

Prefill containers can now be given the exact IP of your lancache server via `Prefill__LancacheIp`. The daemon talks directly to that IP with a spoofed Host header so host networking, AdGuard Home, public DNS, and custom HTTP caches all work without extra config. The Steam session-replacement loop is also gone — on a session conflict the manager switches to anonymous mode, clears credentials, and fails the operation cleanly instead of retrying forever.

The README gains a section explaining how every combination of `NetworkMode`, `LancacheIp`, and `LancacheDnsIp` is routed, including a "pick by your situation" flowchart. Steam app IDs can be imported in bulk from comma-separated, JSON array, or one-per-line formats — paste your existing list straight in.

</details>

<details>
<summary><strong>Code quality</strong></summary>

All background services moved onto two shared base classes, collapsing roughly fifteen ad-hoc periodic loops onto a single lifecycle with consistent run-state tracking. The Steam and Epic integration code was each split into focused partials, and their daemon controllers and SignalR hubs share a common base class instead of duplicating each other.

React contexts were extracted from component files into separate TypeScript files across more than twenty contexts, eliminating Fast Refresh warnings. A large collection of magic strings became typed enums with a Postgres-friendly converter, and memory and path resolution now live behind platform-specific implementations so Windows dev behaves consistently with production Linux. Notification stage and status text for every long-running operation is now driven through i18n, so translations can finally cover the full operation lifecycle instead of just the UI chrome.

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

- Cache reconciliation race that was deleting detection rows instead of clearing them, causing a brief disappearance flicker during scans
- Game-removal progress jumping from 0% straight to "completed" across both bulk and per-entity paths
- Corrupted chunk removal cascading — only MISS/UNKNOWN log lines are now removed for corrupted URLs so the next scan does not double-count
- Delisted Steam apps showing as "Unknown depot" — orphan depots are now queried directly via PICS after the main scan
- Depot-mapping progress events missing their operation ID, leaving cancel buttons doing nothing
- Compression middleware corrupting SignalR WebSocket frames
- Background prefetch crashing low-RAM servers
- Schedule defaults regressing on save
- Sparklines duplicating on dashboard re-renders
- Setup wizard returning 400 on completion
- Nginx log rotation permission issues
- Dropdown menu colors mismatching the active theme
- Game card menus not opening on the first click after image load

</details>

---

Thanks for using LANCache Manager!
