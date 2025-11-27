## What's New

### Help System Overhaul
Added contextual help popovers throughout the Management tab. Each section now has a `?` icon that explains what it does without cluttering the interface. The help content uses color-coded highlights and clean formatting instead of bullet lists.

Covers: Cache Management, Log Processing, Corruption Detection, Database Management, Depot Mapping, Steam Login, GC Settings, Grafana Endpoints, Theme Manager, and Data Import/Export.

### Corruption Details Actually Work Now
Fixed the corruption details panel - it was only showing counts, not the actual corrupted chunks. Now when you expand a service with corrupted files, you'll see the URLs, miss counts, and cache file paths like you're supposed to.

## Bug Fixes
- Fixed cancel button behavior for running operations
- Fixed notifications getting stuck after operations complete
- Fixed operationId tracking issues
- Various console errors cleaned up
- Cache removal and game cache removal fixes

## UI Improvements
- Cleaner header layout
- Better color consistency across themes
- Navigation improvements
- Compact view enhancements for downloads
- Removed redundant UI elements

## Under the Hood
Cleaned up a bunch of dead code that was sitting around doing nothing:
- Removed unused database status endpoint
- Removed unused database cleanup endpoint
- Removed orphaned AlertsManager component
- Removed unused service methods (GetServicesFromLogs, SetThreadCount, GetThreadCount)
- General code cleanup and optimization

Thanks for using Lancache Manager!
