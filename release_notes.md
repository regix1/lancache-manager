# What's New

## Polling Rate Overhaul

The polling rate system has been completely reworked to actually respect your settings.

**Live Mode** - New option for instant updates via SignalR. When you want to watch downloads happen in real-time, select "Live" and data refreshes the moment something changes on the server.

**Throttled Modes** - Choose from 1s, 5s, 10s (default), 30s, or 60s intervals. Unlike before, these settings now actually work. SignalR events are properly throttled to your selected rate, and changing the rate takes effect immediately without needing a page refresh.

**Server-Side Storage** - Your polling rate preference is now saved to `state.json` on the server instead of browser localStorage. This means your setting persists across different browsers and devices.

## Silent Background Processing

The processing notification bar no longer appears during normal downloads. Previously, when you were downloading a game, the log processing notification would pop up showing "Processing: X MB of Y MB" even though nothing user-facing was happening.

Now, the live log monitor runs completely silently in the background. The processing notification only appears when you manually click "Process All Logs" - which is when you actually want to see progress.

The backend now tracks whether processing is in "silent mode" and the frontend respects this, clearing any stale notifications instead of displaying them.

## Auto Discovery Improvements

Fixed reliability issues with datasource auto-discovery. The system now better handles various directory configurations and edge cases when detecting cache and log paths.

## Game Detection Fix

Added IP address back to the game cache detection service list. This field was accidentally removed and is now restored for proper game identification.

## Bug Fixes

- Fixed polling rate selector not updating the actual refresh interval
- Fixed SignalR events bypassing the user's polling rate preference
- Fixed stale processing notifications appearing on page load
- Fixed polling interval not re-initializing when rate changes
