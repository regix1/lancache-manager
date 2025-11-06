# Version 1.6.7 Release Notes

## Quick Note on Version 1.6.6

Version 1.6.6 focused on backend stability improvements. Database clearing operations were refactored to prevent crashes during long-running operations. All background processes (log processing, database reset, cache clearing) now use unified process management for better reliability.

---

## What's New in 1.6.7

### Cache Management Fix

Fixed an issue where the cache clearing button would get stuck showing "Clearing..." after operations completed.

**What Happened:** When cache validation failed quickly (like with an empty cache directory), the completion notification could arrive before the browser finished processing the start request, leaving the button disabled.

**What's Fixed:** Cache operations now track their state immediately when started, so the UI always responds correctly regardless of how fast operations complete.

### Code Cleanup & Performance

Cleaned up the codebase by removing unused code and consolidating duplicate logic.

**Backend Changes:**
- Removed unused authentication code that was never called.
- Consolidated duplicate Rust code - cache path calculations were copy-pasted across 4 files, now unified into a shared module.
- Fixed compiler warnings for shared functions.

**Frontend Changes:**
- Removed unused files (`usePicsProgress.ts`, `silentFetch.ts`).
- Simplified cache management state tracking - replaced complex operation tracking with simple boolean flags.
- Fixed state management hook to always clean up properly, even when backend calls fail.

**Impact:** Smaller bundle size, cleaner code, and more reliable state management across all background operations.

### Bug Fixes

**State Management:** Background operations now properly reset their UI state in all scenarios, including when backend API calls fail.

**Resource Cleanup:** All background processes now clean up temporary files and resources correctly when operations complete or fail.

---

*This release improves reliability for cache operations and cleans up technical debt accumulated over previous versions.*
