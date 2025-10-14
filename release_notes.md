# Release Notes - Version 1.5.5

## What's New

### Multiple Access Log Support
The log processor now automatically discovers and processes rotated log files (access.log.1, access.log.2, etc.) including compressed formats like .gz and .zst. This means you can now process your entire log history without manually combining files.

### Enhanced Metrics
Added more Prometheus-compatible metrics for better system monitoring and observability.

### UI Improvements
- Fixed modals that were getting stuck in their parent containers
- Log File Management now shows clear error messages when scanning fails, with a refresh button to retry
- Cleaned up the Database Management section with better button styling and consistent sizing
- Improved dashboard charts and recent downloads panel

### Bug Fixes
- Fixed depot mapping associations for better game name detection
- Cleaned up excessive logging in production
- Fixed navigation rendering and swipe gesture handling on mobile
- Removed deprecated code paths

The main benefit of this release is the multi-log support - you can now process years of cache history without worrying about log rotation or compression formats.
