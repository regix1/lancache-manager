## What's New

### Data Directory Cleanup
Reorganized the data directory structure for better organization and automatic cleanup:

**New `data/operations/` directory** - All temporary operation files are now stored in a dedicated subdirectory:
- `cache_operations.json` - Active cache clear tracking
- `operation_history.json` - Game detection and other operation history
- `cache_clear_progress_*.json` - Rust cache cleaner progress files
- `corruption_removal_*.json` - Rust corruption manager progress files
- `log_remove_progress.json` - Rust log removal progress

**Operation report files are now preserved** for history:
- `game_removal_{appId}_{timestamp}.json` - Game cache removal reports
- `corruption_details_{service}_{timestamp}.json` - Corruption detection reports

**Cleaner `state.json`** - Removed temporary operation data, now contains only settings and state:
- Log processing position
- PICS scan recovery state
- Theme preferences
- Crawl settings
- PICS viability cache

**Automatic cleanup on startup:**
- Progress files older than 24 hours are deleted
- Completed cache clear operations older than 24 hours are purged
- Completed operation history older than 48 hours is purged

### Double-Click Prevention
Added synchronous double-click prevention to all action buttons on the Management tab. Previously, clicking buttons like "Apply Now" or "Clear Selected" multiple times quickly could trigger duplicate API requests before the loading state kicked in. Now all async operations use refs to block duplicate calls immediately.

Affected buttons:
- Depot Mapping "Apply Now"
- Database Management "Clear Selected Tables"
- Cache Management "Clear All Cache" and delete mode buttons
- Theme Importer "Refresh" and "Import" buttons

### Steam Connection Exponential Backoff
Steam reconnection attempts now use exponential backoff instead of a fixed 5-second delay. When disconnected during a PICS scan, the service waits progressively longer between attempts: 5s, 10s, 20s, 40s, up to 60s max. This is gentler on Steam's servers and reduces the chance of rate limiting.

### Game Cache Detection Improvements
Clearing "Game Cache Detection" in Database Management now properly clears both detected games AND services (like wsus, steam, epic). Previously it only cleared games, leaving stale service entries in the cache.

## Bug Fixes

- Fixed duplicate key errors when saving Steam depot mappings by adding proper handling for UNIQUE constraint violations
- Fixed race condition where multiple depot mapping requests could be sent simultaneously
- Updated Database Management UI wording to reflect that it clears "game and service detection scans"
- Fixed docker socket permission denied error - gosu now properly inherits supplementary groups
- Fixed operation report files being deleted immediately after creation

## Under the Hood

- Added `Microsoft.Data.Sqlite` error handling to `SteamService.cs` for concurrent depot mapping saves
- Refactored try/catch blocks to use `finally` for consistent cleanup in async operations
- All button action handlers now follow the ref-guard pattern for double-click prevention
- Added `ReadOutputJsonAsync` method to preserve report files while `ReadAndCleanupOutputJsonAsync` still cleans up temporary files
- Entrypoint script now uses username with gosu (instead of UID:GID) to properly inherit supplementary groups

Thanks for using Lancache Manager!
