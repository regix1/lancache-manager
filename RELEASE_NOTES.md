This release includes important bug fixes, performance improvements, and UI enhancements following the v1.6.0 release. Major highlights include faster game cache removal, improved notification system, enhanced game cache detection, and various authentication and UI fixes.

## What's New

### Universal Notification System

Introduced a notification bar component that consolidates all system notifications into a single, unified interface. The new system provides better visibility for ongoing operations including PICS updates, log processing, depot mapping, and cache operations with improved progress tracking and user feedback.

### Enhanced Game Cache Detection

Improved game cache detection service with better error handling and session management. The detection process now properly tracks cache state until application restart, providing more reliable identification of cached games and their associated data.

## Performance Improvements

### Optimized Game Cache Removal

Significantly improved the performance of game cache removal operations through Rust processor optimizations. The removal process is now faster and more efficient while maintaining data integrity across cache files, database records, and log entries.

### Corruption Detection Refinements

Enhanced corruption detection logic with improved chunk validation and error handling. The system now more accurately identifies and processes corrupted cache segments with reduced false positives.

## Bug Fixes

### Authentication

- Fixed authentication manager to properly handle session validation
- Corrected authentication service token refresh behavior
- Improved error handling during authentication failures

### User Interface

- Resolved various UI inconsistencies and display issues
- Improved color scheme consistency across components
- Enhanced text readability and contrast throughout the application
- Fixed modal display timing and positioning

### Game Information

- Improved depot ID to game name mapping accuracy
- Enhanced game metadata retrieval and caching
- Fixed issues with game information display during operations

## Additional Changes

- Added new community theme: "LANCache Unofficial" featuring official LANCache branding colors
- Improved code organization and removed duplicate logic
- Enhanced error messages for better troubleshooting
- Updated dependency management and build processes
- General code cleanup and compiler warning resolution
