## What's New

<details>
<summary><strong>Steam Session Management</strong></summary>

- Automatic anonymous mode when prefill daemon is active prevents session replacement loops between depot mapping and prefill services
- Dedicated LoginID ranges for SteamKit2 (16384-65535) and prefill daemon (0-16383) eliminate connection conflicts
- New `OnDaemonAuthenticated` / `OnAllDaemonsLoggedOut` events allow SteamKit2Service to proactively yield and reclaim sessions
- On session replacement, the system now auto-switches to anonymous mode and requires manual re-authentication instead of retrying in a loop
- Removed unreliable session replacement tracking flags (`_sessionReplacementAutoLogout`, `SessionReplacedCount`) in favor of direct daemon status checks
- PICS cancel now unblocks pending connection/login tasks immediately instead of waiting for timeout

</details>

<details>
<summary><strong>Corruption Detection & Removal</strong></summary>

- Corruption detection now verifies cache files exist on disk before flagging — cold-cache misses are no longer reported as corruption
- Corruption removal only deletes MISS/UNKNOWN log entries, preserving HIT entries to prevent snowball detection where each scan finds double the corruption
- Downloads are only removed from the database when ALL their log entries were corrupted, not just when any entry was
- CacheMissBytes is recalculated on remaining downloads after partial corruption removal
- Configurable miss threshold parameter for corruption detection scans (default: 3)
- Removed aggressive memory cleanup that was discarding tracking data prematurely during detection scans
- Added `RemoveCachedServiceAsync` to clear cached corruption entries after successful removal

</details>

<details>
<summary><strong>Cancellation & Operations</strong></summary>

- All long-running operations now support cancel via the notification bar: log processing, log removal, game removal, service removal, database reset, and data import
- `Started` SignalR events added for log processing, log removal, game removal, service removal, and database reset — enables cancel button from the moment an operation begins
- Every notification handler now includes `operationId` in details, fixing cancel buttons that were previously non-functional
- Cancel error handling improved: if an operation already completed, the notification is dismissed instead of getting stuck in "cancelling" state
- Cancellable operation types defined in a single lookup map (`CANCEL_TOOLTIP_KEYS`) instead of duplicated arrays
- Cache clearing migrated from `RemovalOperationTracker` to `UnifiedOperationTracker` with granular `OperationMetrics` tracking

</details>

<details>
<summary><strong>Multi-Datasource Support</strong></summary>

- Log removal now processes each datasource sequentially with per-datasource progress reporting, skipping read-only or missing directories gracefully
- Cache clearing validates write permissions per-datasource with clear error messages when PUID/PGID is misconfigured
- Corruption removal extracted from `CacheManagementService` into dedicated service with multi-datasource iteration
- Rust processors receive datasource-specific progress files to prevent monitoring clashes

</details>

<details>
<summary><strong>Progress & Real-Time Updates</strong></summary>

- Game removal Rust processor now reports granular progress (10%-70% range) during cache file deletion with per-percent updates
- Service removal Rust processor tracks `percentComplete`, `filesProcessed`, and `totalFiles` for real-time progress bars
- Corruption removal progress model updated with proper JSON serialization for `percentComplete`, `filesProcessed`, and `totalFiles`
- Depot mapping progress extracted into a reusable `SendDepotMappingProgress` method for consistent reconnection status updates
- `BuildDepotIndexAsync` decomposed into `PrepareForScanAsync`, `ProcessAppBatchesAsync`, and `FinalizeAndNotifyAsync` for better maintainability
- Depot mapping now re-resolves downloads with placeholder names (e.g. "Steam App 12345") on subsequent runs instead of skipping them

</details>

<details>
<summary><strong>Prefill UI Overhaul</strong></summary>

- `usePrefillSignalR` hook (893 lines) split into three focused modules: `usePrefillAnimation`, `usePrefillEventHandlers`, and shared `prefillConstants`/`prefillTypes`
- New prefill command button tiles with CSS-based styling replacing inline button variants
- Removed auto-login feature for prefill daemon sessions (`TryAutoLoginWithTokenAsync` and `TryAutoLogin` hub method removed)
- Added dedicated `prefill.css` stylesheet (226 lines) for all prefill-related component styles
- Session timeout and completion notification window extracted into named constants

</details>

<details>
<summary><strong>Dashboard & Downloads UI</strong></summary>

- Removed delayed skeleton loading pattern from dashboard — eliminates flash-of-loading on time range changes
- Downloads tab mobile layout improved: search input and settings gear now share a single row
- Downloads pagination shows compact stats on mobile (download count only) and verbose stats on desktop (page numbers + depot groups)
- Added 700+ lines of CSS for downloads header, active downloads, and compact view layouts
- `DownloadsHeader.tsx` significantly reduced (from ~380 to 170 lines) with styles extracted to CSS

</details>

<details>
<summary><strong>GitHub Projects Dropdown</strong></summary>

- Firework rocket trail now properly spawns behind the rocket using visual rotation angle instead of velocity direction
- Spin animation uses CSS `animationend` event instead of `setTimeout` for precise firework launch timing
- Removed debug `console.log` statements from firework color and trail rendering
- Removed unused velocity normalization variables

</details>

<details>
<summary><strong>Backend Cleanup</strong></summary>

- Removed `StateService` session replacement tracking methods and related `IStateService` interface members
- Removed `PrefillDaemonHub.TryAutoLogin` endpoint and supporting auto-login infrastructure
- `DeviceAuthService` database retry logic refactored into generic `ExecuteWithRetry<T>` methods eliminating duplicated retry/backoff code
- `PrefillCacheService` added for managing prefill-related cache data
- SignalR event types expanded with strongly-typed `Started` event interfaces for all operation types

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

- Fixed corruption removal snowball effect where each scan found double the corruption by preserving HIT log entries during removal
- Fixed corruption detection false positives from cold-cache misses by verifying cache files exist on disk
- Fixed cancel buttons not working on most notification types due to missing `operationId` in event details
- Fixed cancel getting stuck in "cancelling" state when the operation had already completed
- Fixed PICS rebuild cancel hanging when connection/login tasks were still waiting
- Fixed firework launch timing inconsistency caused by `setTimeout` drift vs CSS animation duration
- Fixed dashboard flashing delayed skeleton loaders on every time range change
- Fixed dashboard showing "Steam App {id}" placeholder names instead of resolved game names — depot mapping now re-resolves placeholder names, and the dashboard filters them consistently with the downloads page

</details>

---

Thanks for using LANCache Manager!
