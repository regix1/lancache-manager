# Release Notes

## What's New

### Unified Cache Removal Modal
Consolidated the separate game and service removal modals into a single, smarter `CacheRemovalModal` component. Whether you're removing a game's cached data or wiping an entire service, you now get the same clean confirmation dialog with accurate file counts, size estimates, and depot information. Less code, same functionality.

### Database Reset Progress on Login Screen
The authentication modal now shows real-time database reset progress via SignalR. If someone triggers a database reset while you're on the login screen, you'll see a progress bar with status messages instead of wondering why authentication isn't working. Once complete, a "Database Reset Complete" banner appears for 5 seconds letting you know you're good to log in.

### Service Color System
Added a centralized `serviceColors.ts` utility that provides consistent service-specific colors across the entire application. Steam, Epic, Origin, Blizzard, Riot, Xbox, and WSUS all now use the same color values everywhere - badges, icons, and text. Uses CSS variables from the theme system so colors adapt to your selected theme.

### Hold-to-Repeat Button Hook
New `useHoldTimer` hook for buttons that should repeat their action when held down. After an initial 400ms delay, the action repeats every 150ms until you release. Used for pagination controls and anywhere else rapid-fire clicking would be tedious.

### Retro View Overhaul
The retro downloads view got a significant refresh:
- Fully responsive design with a dedicated mobile layout
- Game header images display alongside download entries (for Steam games with valid app IDs)
- Visual cache efficiency indicators with color-coded progress bars
- "Excellent/Partial/Miss" labels based on hit percentage
- Sessions and client counts shown when multiple entries are grouped
- Multiple sort options: latest, oldest, largest, smallest, service, efficiency, sessions, alphabetical

### API Response Standardization
Created a comprehensive `ApiResponses.cs` file with strongly-typed DTOs for all API endpoints. Every controller now returns consistent response objects instead of anonymous types. This makes the API more predictable for the frontend and easier to maintain. Covers: version info, operation tracking, cache info, permissions, auth status, system config, and more.

### Steam Authentication Improvements
- Auth state is now cleared immediately when the login modal opens, preventing race conditions with periodic auth checks
- Fixed issues where reconnecting during an active PICS scan would cause problems
- Better handling of Steam connection timeouts and reconnection attempts

### Database Lock Handling
Extended the retry logic for SQLite "database is locked" errors with proper exception handling. The system now catches both generic database exceptions and SQLite-specific locked errors, retrying up to 3 times with exponential backoff before giving up.

## Bug Fixes
- Fixed authentication modal interfering with user input due to stale auth state
- Fixed depot mapping manager with cleaner state management
- Removed redundant code from CompactView and NormalView components
- Fixed various console errors in the notification system
- Fixed retro view pagination counting raw items instead of grouped depots

## Under the Hood
- Major refactoring pass across 34+ files removing ~280 lines of duplicate code
- Extracted shared download view logic into reusable patterns
- Simplified `StatsContext` and `DownloadsContext` with cleaner state management
- `NotificationsContext` refactored for better SignalR event handling
- Controllers now use factory methods for creating standardized responses
- Added Vite build configuration for injecting version info at build time
- Removed `GameRemovalModal.tsx` and `ServiceRemovalModal.tsx` (merged into `CacheRemovalModal.tsx`)

Thanks for using LANCache Manager!
