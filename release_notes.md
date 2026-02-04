## What's New

<details>
<summary><strong>Dashboard & Data Management</strong></summary>

- Consolidated dashboard data fetching into a unified context that manages cache info, client stats, service stats, and downloads together. This eliminates redundant API calls and keeps all dashboard components in sync without separate data providers fighting over updates.
- Dashboard widgets now preserve their previous data while fetching updates instead of flashing empty or loading states. When you change time ranges or filters, you'll see the existing data remain visible until new data arrives.
- Removed response caching from stats and downloads endpoints to ensure the dashboard always shows current data. Previously, 5-second cache durations could cause confusion when making changes and expecting immediate updates.
- Smart debouncing now prevents rapid duplicate API calls when filters change in quick succession, with a 250ms minimum interval between fetches. Custom date range changes bypass debouncing entirely for immediate responsiveness.

</details>

<details>
<summary><strong>Real-Time Updates</strong></summary>

- SignalR events have been reorganized by feature area (Downloads, Cache Operations, Games, Authentication, etc.) making it easier to understand which events relate to which parts of the application.
- Long-running operations like cache clearing, corruption removal, and game detection now stream real-time progress updates via SignalR. You'll see percentage completion, current status messages, and detailed progress instead of waiting for operations to finish.
- All operation events now follow a consistent pattern with operation IDs, completion percentages, and status messages. This standardization makes progress tracking more predictable across different operation types.
- Added type-safe notification record types for all real-time events, eliminating the risk of data format mismatches between the server and web interface.

</details>

<details>
<summary><strong>Authentication & Sessions</strong></summary>

- Steam login now includes a LoginID parameter for improved connection stability and more reliable authentication handling when reconnecting or re-authenticating.
- Guest session duration updates now broadcast immediately to all connected admin panels via SignalR instead of requiring polling or page refreshes.
- Session revocation logic was tightened to only trigger when the device ID explicitly matches the current device, eliminating false-positive session terminations.
- Added automatic login capability for prefill daemon sessions using stored Steam refresh tokens, so you don't need to manually enter credentials each time.

</details>

<details>
<summary><strong>Game & Download Information</strong></summary>

- Downloads now automatically resolve missing game names from Steam depot mappings when the game wasn't identified at download time. Added a database index on DepotId for faster lookups.
- The download associations context properly clears both its cache and state when downloads refresh, preventing stale information from appearing in the UI.
- Atomic database queries replace the previous multi-query approach for fetching downloads with event associations, eliminating race conditions where data could change between queries.

</details>

<details>
<summary><strong>UI & Interface</strong></summary>

- New GitHub Projects dropdown in the header showcases related repositories with smooth hover animations, forked project badges, and a support button linking to the donation page.
- Z-index layering has been normalized across dropdowns, modals, and tooltips to prevent overlap issues. Dropdowns now appear at z-index 85, modals at 80, and tooltips at 90.
- Animation timing on dashboard stat cards was reduced from 700ms to 400ms for a snappier feel. Opacity transitions dropped from 450ms to 300ms.
- Card CSS transitions were simplified for smoother animation performance.

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

- Fixed duplicate notifications from late-arriving SignalR progress events. The system now ignores progress updates for notifications that have already completed or failed.
- Fixed prefill activity log showing duplicate entries by adding deduplication logic that tracks recent entries within a 2-second window.
- Fixed concurrent log processing attempts. Requests now return 409 Conflict if processing is already running instead of queuing or failing silently.
- Fixed Rust speed tracker caching logic to only cache successful game name lookups with actual values, preventing empty cached values from blocking subsequent lookups.
- Fixed custom date range selections to immediately fetch data instead of being delayed by debouncing.
- Dashboard widgets (Cache Growth Trend and Peak Usage Hours) now only show loading indicators on initial load when no data exists.

</details>

<details>
<summary><strong>Technical</strong></summary>

- Introduced standardized operation status constants (Pending, Running, Cancelling, Completed, Failed, Cancelled) for consistent use across all notification events.
- Database count operations in the DatabaseService now run within transactions for consistent snapshots during bulk operations.
- SignalR now uses camelCase JSON serialization to match frontend expectations consistently.
- Removed unused StatCard trend properties and associated rendering logic.
- Deleted barrel export files from components and contexts directories that were exposing internal-only code.
- Cleaned up notification context exports, removing internal handlers and formatters from the public API.
- Added conflict detection to prevent concurrent game detection operations.
- Data file paths now use organized subdirectories (state, settings, security, operations) with automatic migration from the previous flat layout.

</details>

---

Thanks for using LANCache Manager!
