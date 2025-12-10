# LANCache Manager v1.8.1 Release Notes

---

## What's New

### Retro View for Downloads
Added a new "Retro" view mode for the Downloads tab - a classic table-style layout that groups downloads by depot ID. Each row shows the service, game name, depot ID, client IP, timestamps, cache hit/miss bytes, and session count. Great for users who prefer a denser, spreadsheet-like view of their download data.

### Reusable Pagination Component
Created a new unified Pagination UI component used across the application. Features first/last page buttons, numbered page navigation with ellipsis for large page counts, and a quick-jump dropdown for lists with 10+ pages. Now used in:
- Downloads tab (all view modes)
- Game Cache Detection list
- User Sessions list

### Orphaned Service Cleanup
The background cleanup service now automatically removes database records for services that no longer exist in your log files. This cleans up stale data left behind when log entries were removed but the database wasn't updated. Includes safety checks to prevent accidental data loss if log scanning fails.

### Steam Connection Improvements
- Better error messages when Steam connection times out - now tells you specifically what operation failed and suggests possible causes
- Auto re-login when reconnecting during an active PICS rebuild - no more manual intervention needed if Steam disconnects mid-scan
- Added max reconnection attempts (will stop trying after repeated failures instead of looping forever)
- Progress updates now show reconnection status in the UI so you know what's happening

### Database Lock Handling
Added retry logic with exponential backoff for "database is locked" errors. This handles cases where large operations (like PICS import) hold the database lock. The session tracking service now retries up to 3 times before giving up, preventing unnecessary error spam in the logs.

### Rust Binary Renaming
Renamed all Rust processor binaries to follow a consistent naming convention:
- `log_*` - Log operations (log_processor, log_service_manager)
- `cache_*` - Cache operations (cache_clear, cache_corruption, cache_game_detect, cache_game_remove, cache_service_remove)
- `db_*` - Database operations (db_reset, db_migrate)

---

## Bug Fixes
- Fixed retro view not grouping downloads by depot ID correctly
- Fixed pagination showing wrong counts in retro view (was counting raw downloads instead of grouped depots)
- Fixed Steam authentication issues when reconnecting during PICS scans
- Removed hardcoded user specification from Dockerfile to allow default user

---

## Under the Hood
- New RetroView.tsx component with depot grouping logic
- Pagination.tsx extracted as reusable UI component
- DownloadsTab refactored to support retro view pagination separately from other views
- GamesList.tsx and UserTab.tsx now use the shared Pagination component
- SteamKit2Service.Connection.cs has improved reconnection logic with better error handling
- DeviceAuthService.cs has retry logic for database lock errors
- DownloadCleanupService.cs handles orphaned service cleanup

---

Thanks for using LANCache Manager!
