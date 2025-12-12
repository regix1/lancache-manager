# Release Notes

## What's New

### Steam Session Replacement Handling
Completely reworked how the application handles Steam session replacement errors (when your account logs in elsewhere). Previously, the system would keep trying to reconnect even after auto-logout triggered, leading to an endless loop of failed connection attempts.

Now when auto-logout kicks in after repeated session replacements:
- A dedicated flag prevents the disconnect handler from attempting reconnection
- The flag clears automatically on fresh connections
- No more spinning wheel of reconnection doom

Also tightened up the thresholds - auto-logout now triggers after 2 session replacements (down from 3), and reconnection gives up after 2 attempts (down from 5). If Steam keeps kicking you off, the app backs off faster instead of hammering the servers.

### Reconnection Timing Fix
Increased the reconnection wait time during PICS crawls from 1 second to 10 seconds. The previous timeout was too short - by the time the code checked if reconnection succeeded, it was still in progress. Now it waits long enough for the reconnect delay (5s) plus actual connection time before deciding the connection failed.

### Retro View Theme Support
The retro downloads view now properly respects your selected theme. Previously it used hardcoded hex colors for progress bars and efficiency indicators, which looked wrong on non-default themes.

Updated to use theme CSS variables:
- Progress bar backgrounds use `--theme-progress-bg`
- Cache hit bars use `--theme-chart-cache-hit`
- Cache miss bars use `--theme-error`
- Efficiency ratings use `--theme-success-text`, `--theme-warning-text`, and `--theme-error-text`

If you're using a custom theme, the retro view will now match.

### Sage & Wood Theme Improvements
Updated the Sage & Wood community theme with better contrast for status indicators. The text colors were too muted before - now they pop:
- Success text: brighter green instead of dusty sage
- Warning text: richer orange instead of cream
- Error text: vibrant coral instead of washed-out salmon
- Hit rate indicators updated to match

## Bug Fixes
- Fixed Steam reconnection loop that could occur after session replacement auto-logout
- Fixed duplicate alert icon in the Steam auto-logout warning banner
- Fixed retro view colors not matching selected theme

## Under the Hood
- Added `_sessionReplacementAutoLogout` flag to SteamKit2Service for cleaner logout state management
- Reduced reconnection aggressiveness to be gentler on Steam servers
- Sage & Wood theme bumped to version 2.0.3

Thanks for using LANCache Manager!
