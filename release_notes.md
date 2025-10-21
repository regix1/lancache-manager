## What Changed

### Live Log Monitoring Fix
Fixed an issue where active downloads stopped updating in real-time. The progress file was being written with a non-atomic operation, causing the monitoring service to sometimes read incomplete data. Now using atomic file operations to ensure progress updates always work.

### Performance and Memory Improvements
Applied several optimizations to reduce memory usage during log processing. The Rust processor now cleans up session tracking data periodically and releases memory after processing batches. Also improved I/O handling and reduced memory allocations.

### Corruption Detection (Experimental)
Added tools to detect and analyze corrupted cache files by comparing log entries with actual file hashes. This helps identify which cached files might be causing download issues. The removal feature is still experimental - use with caution as it hasn't been fully tested yet.

## Changelog

### Bug Fixes
- Fixed live log monitoring not updating active downloads (progress file race condition)
- Fixed polling rate not being respected in active downloads component
- Fixed memory leak in session tracking during log processing
- Fixed path handling issues in Rust components
- Fixed theme-related UI issues

### Performance
- Reduced memory usage during log processing by cleaning up session tracker periodically
- Improved I/O performance with better file handling
- Applied memory optimizations to batch processing (reduced batch size, added shrink_to_fit)

### Features
- Added corruption detection tool to analyze cache files against log entries
- Added experimental cache file removal by service (use with caution - not fully tested)

### Refactoring
- Moved more log processing logic into Rust for better performance
- Improved error handling in log file discovery
- Cleaned up unused functions

### Important Notes
- **Corruption removal is experimental** - The feature to remove corrupted cache files hasn't been thoroughly tested. Back up your cache directory before using this feature.
- Session tracker now automatically cleans up old sessions every 1000 updates to prevent memory leaks

## Summary

This release fixes the live monitoring that stopped working after recent memory optimizations, and includes several performance improvements to reduce memory usage during log processing. The new corruption detection feature can help identify problematic cache files, but the removal functionality should be used carefully as it's still being tested.
