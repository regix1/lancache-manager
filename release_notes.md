## Release 1.9.8.1

**Hotfix**

- Fixed missing database migration for the DepotId index on Downloads table
- Resolves "PendingModelChangesWarning" error that prevented application startup after upgrading to 1.9.8

---

## Release 1.9.8

**Dashboard & Data Management**
- Unified dashboard data fetching eliminates redundant API calls and keeps components in sync
- Widgets preserve previous data while fetching updates instead of flashing loading states
- Removed response caching from stats endpoints to ensure current data
- Smart debouncing prevents rapid duplicate API calls with 250ms minimum intervals

**Real-Time Updates**
- SignalR events reorganized by feature area for better clarity
- Long-running operations now stream real-time progress via SignalR
- Standardized operation events with IDs, completion percentages, and status messages

**Authentication & Sessions**
- Steam login includes LoginID parameter for improved connection stability
- Guest session duration updates broadcast immediately via SignalR
- Added automatic login for prefill daemon sessions using stored refresh tokens

**Game & Download Information**
- Downloads automatically resolve missing game names from Steam depot mappings
- Added database index on DepotId for faster lookups
- Atomic database queries eliminate race conditions when fetching downloads

**UI & Interface**
- New GitHub Projects dropdown in header with related repositories
- Normalized z-index layering across dropdowns, modals, and tooltips
- Faster animation timing on dashboard stat cards

**Bug Fixes**
- Fixed duplicate notifications from late-arriving SignalR progress events
- Fixed prefill activity log showing duplicate entries
- Fixed concurrent log processing returning 409 Conflict properly
- Fixed custom date range selections to fetch immediately

**Technical**
- Standardized operation status constants across all notification events
- SignalR uses camelCase JSON serialization
- Data file paths now use organized subdirectories with automatic migration

---

Thanks for using LANCache Manager!
