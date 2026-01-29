## What's New

### Permissions check during setup
The first-time setup now verifies your environment before you begin. It checks access to your cache directory, logs directory, and Docker socket, then displays feedback about what will work and what might need attention.

### Corruption detection progress
The corruption scanner now reports progress as it works through your log files. You can see which file is being scanned and the overall completion percentage. Concurrent scans are also prevented now.

### Centralized session preferences
User preferences are now managed through a single context instead of being fetched separately across components. This eliminates redundant API calls and ensures all parts of the UI stay in sync when preferences change.

### Fewer duplicate notifications
Operations like cache clearing and corruption removal no longer stack multiple notifications. Each operation shows one notification that updates as it progresses instead of creating new ones.

### Overlay layering fixes
Dropdowns, tooltips, and modals had inconsistent z-index values causing overlap issues. The layering was normalized so elements appear at the correct depth.

### Faster timezone updates
Changing your timezone or time format preference now updates the UI immediately instead of waiting for the server response. No more flash of stale values while the setting saves.

### Network diagnostics improvements
The prefill network check now properly identifies the Steam trigger domain as the critical DNS entry. Optional domains no longer show warnings if the primary Steam domain is configured correctly. IPv6 bypass detection was also refined.

### Guest session fixes
Guest session timers now display correctly when configured in hours. Fixed a bug where remaining time could show incorrect values. Session revocation also matches the correct device now.

### Sparkline trend calculation
Dashboard sparkline trends now compare recent data points against immediately preceding points rather than the entire dataset. This makes the trend indicator match what you see visually at the end of each chart.

### Game cache detection performance
The game detection scanner now uses an incremental mode when checking small numbers of URLs, which is faster than scanning the entire cache directory structure.

### Path migration
A new service handles migrating data files from the old flat directory layout to the new organized structure with separate folders for state, settings, security, and operations.

### Backend cleanup
Response types were split into smaller files organized by feature. Unused endpoints, old database migrations, and barrel export files were removed.

### SignalR improvements
Real-time events are now organized by feature area. Added a new progress event for corruption detection to enable live scan updates.

Thanks for using LANCache Manager!
