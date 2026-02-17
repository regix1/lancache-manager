## What's New

<details>
<summary><strong>Session-Based Authentication</strong></summary>

- Complete rebuild from device-based to session-based authentication with admin and guest tiers
- Admin sessions created via API key login, guest sessions via a single endpoint
- Session token rotation with 30-second grace period so concurrent requests and multiple tabs work seamlessly
- Guest users can access the dashboard and downloads with limited permissions
- Admin-only endpoints are automatically skipped for guest sessions to avoid 403 errors
- Removed legacy device registration, browser fingerprinting, and heartbeat polling
- New SignalR events for session lifecycle: `UserSessionCreated`, `UserSessionRevoked`, `GuestModeLockChanged`, `GuestPrefillConfigChanged`, `GuestPrefillPermissionChanged`

</details>

<details>
<summary><strong>Steam Session Management</strong></summary>

- Automatic anonymous mode when the prefill daemon is active prevents session replacement loops
- Dedicated LoginID ranges for SteamKit2 (16384-65535) and prefill daemon (0-16383) eliminate connection conflicts
- `OnDaemonAuthenticated` / `OnAllDaemonsLoggedOut` events let SteamKit2Service proactively yield and reclaim sessions
- On session replacement the system auto-switches to anonymous mode and requires manual re-authentication instead of retrying in a loop
- Removed unreliable session replacement tracking flags in favor of direct daemon status checks
- PICS cancel now unblocks pending connection/login tasks immediately instead of waiting for timeout

</details>

<details>
<summary><strong>Delisted Game Detection</strong></summary>

- New `ResolveOrphanDepotsAsync()` discovers depot mappings for delisted/removed Steam games that never appear in Steam's GetAppList API
- Generates candidate parent app IDs using common heuristics (depotId-1, depotId, depotId-2) and queries PICS directly
- Runs automatically after the main PICS scan while the Steam connection is still active
- Locally-resolved orphan mappings are tagged with `source="orphan-resolved"` and preserved through GitHub data imports
- Depot mapping now re-resolves downloads with placeholder names (e.g. "Steam App 12345") on subsequent runs instead of skipping them

</details>

<details>
<summary><strong>Corruption Detection & Removal</strong></summary>

- Detection now verifies cache files exist on disk before flagging, so cold-cache misses are no longer reported as corruption
- New detection mode toggle: "Cache + Logs" vs "Logs Only" for flexible scanning
- Configurable miss threshold parameter (default: 3)
- 5-minute grace period for recently removed services prevents them from immediately reappearing in results
- Compression-aware log rewriting: output files automatically match the input format (.gz, .zst, or plain text)
- Removed deprecated synchronous `GetCorruptionSummary` endpoint in favor of the async cached/polling pattern

</details>

<details>
<summary><strong>Cancellation & Progress</strong></summary>

- All long-running operations now support cancel via the notification bar: log processing, log removal, game removal, service removal, database reset, and data import
- `Started` SignalR events added for all operation types, enabling the cancel button from the moment an operation begins
- Every notification handler now includes `operationId` in details, fixing cancel buttons that were previously non-functional
- If an operation already completed, the notification is dismissed instead of getting stuck in "cancelling" state
- All operation tracking consolidated into `UnifiedOperationTracker` with type-safe metadata classes
- Game and service removal report granular progress with `filesDeleted` and `bytesFreed`
- Speed tracking now uses depot entry presence instead of raw byte counts, preventing false activity from non-game entries
- 1.5-second grace period in SpeedContext prevents zero-state flicker during tab switches

</details>

<details>
<summary><strong>Prefill Overhaul</strong></summary>

- `usePrefillSignalR` hook split into three focused modules: `usePrefillAnimation`, `usePrefillEventHandlers`, and shared constants/types
- New prefill command button tiles with CSS-based styling replacing inline button variants
- Removed auto-login feature for prefill daemon sessions
- Extended thread count options up to 256 threads for 10Gbps+ connections
- Guest users can now access prefill with time-expiring access tokens granted by admins
- Per-session `MaxThreadCount` limits how many prefill download threads a guest can use, with a system-wide default fallback
- Thread limits enforced at both API and UI levels so guests cannot select values beyond their allowed maximum
- Added dedicated `prefill.css` stylesheet for all prefill-related component styles

</details>

<details>
<summary><strong>Dashboard & UI</strong></summary>

- Removed delayed skeleton loading pattern from the dashboard, eliminating flash-of-loading on time range changes
- Downloads tab mobile layout improved: search input and settings gear share a single row
- Downloads pagination shows compact stats on mobile and verbose stats on desktop
- New `count-badge` CSS component replaces inline `({{count}})` translation patterns with styled pill badges
- `HelpDefinition` component replaced with new `HelpSection` and `HelpNote` components for cleaner help popovers
- `DownloadsHeader.tsx` reduced from ~380 to 170 lines with styles extracted to CSS
- Complete redesign of all three colorblind-accessible themes (Deuteranopia, Protanopia, Tritanopia) using a neutral dark grey palette with distinct colorblind-safe accent colors
- Added Ubisoft service color support across all themes
- CSS `:has()` selectors wrapped in `@supports` for broader browser compatibility
- Pre-React loading fallback with diagnostic info when the app fails to mount
- Firework animations rewritten with proper rocket trail physics and CSS-driven launch timing

</details>

<details>
<summary><strong>Backend & Rust Improvements</strong></summary>

- Removed `StateService` session replacement tracking and related `IStateService` interface members
- Removed `PrefillDaemonHub.TryAutoLogin` endpoint and supporting auto-login infrastructure
- SignalR event types expanded with strongly-typed `Started` event interfaces for all operation types
- Removed `canvas-confetti` dependency and deprecated model classes
- URL normalization collapses consecutive slashes in nginx log URLs (e.g. `//files/...` becomes `/files/...`)
- IPv6 address detection groups IPv6 entries as "ip-address" service instead of creating a separate service per address
- Speed tracker file rotation handling fixed: continues processing from position 0 instead of early exit
- Progress percentage capped at 100% to prevent overflow in progress bars
- Log removal processes each datasource sequentially with per-datasource progress, skipping read-only or missing directories gracefully

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

- Fixed SQL injection vulnerability in DataMigrationController table name lookup
- Fixed loading state getting stuck when rapid auth transitions triggered multiple fetch effects
- Fixed SignalR connecting without a valid session token, causing unnecessary connection churn
- Fixed active sessions IP display formatting
- Fixed cache path calculation in corruption/game removal (corrected chunk end offset formula)
- Reduced database reset batch size from 100,000 to 10,000 for faster cancellation response

</details>

---

Thanks for using LANCache Manager!
