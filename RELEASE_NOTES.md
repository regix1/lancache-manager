## Version 1.6.0

This release includes significant improvements to stability, performance, and cache management capabilities. Major highlights include comprehensive memory leak fixes, enhanced corruption detection and removal, and the addition of selective game cache removal functionality.

### What's New

**Selective Game Cache Removal**

Added the ability to remove individual games from cache without requiring a full cache wipe. The removal process performs comprehensive cleanup including cache files on disk, database records (Downloads, LogEntries, SteamDepotMappings), and associated log entries. Real-time progress tracking is provided throughout the removal operation.

**Automatic Game Cache Detection**

Implemented automatic cache directory scanning to identify cached games. The system maps depot IDs to game names using the Steam database and provides cache size information per game. Scanning runs as a background operation to minimize performance impact.

### Major Fixes

**Memory Management**

Resolved critical memory leaks affecting long-running instances. Implemented the following improvements:
- Updated to .NET 8.0.121 containing essential memory leak fixes
- Added platform-specific garbage collection management for Windows and Linux
- Improved object disposal throughout the application lifecycle
- Introduced memory monitoring endpoints for diagnostics

**Corruption Detection**

Corrected corruption detection logic to properly identify and remove corrupted cache files. The system now correctly handles nginx cache key formats, removes entries from both disk and database, and provides detailed progress reporting during cleanup operations.

**Polling Rate Configuration**

Fixed issue where user-configured polling rates were not being applied. The application now respects the selected polling interval, and SignalR refresh events properly honor the configured rate instead of using default values.

### UI Improvements

- Renamed cache deletion modes from "Preserve" and "Bulk" to "Safe Mode" and "Fast Mode" for improved clarity
- Resolved issue where "Full Scan Required" modal appeared incorrectly when data was already current
- Fixed depot mapping to prevent games from displaying as "Unknown Game" during initial processing
- Corrected progress bar display during Steam PICS data downloads
- Resolved various modal display issues throughout the application

### Backend Improvements

- Resolved database foreign key constraint errors during corrupted data deletion
- Corrected Steam incremental scanning with enhanced error handling
- Introduced new Rust-based tools for corruption management, game detection, and cache removal
- Improved timeout handling to prevent hanging operations
- Enhanced log processing to automatically discover and process all rotated log files
- Improved background operation tracking with resumption support and reduced log verbosity
- Implemented authentication middleware for Swagger UI
- Performed general code cleanup and resolved compiler warnings