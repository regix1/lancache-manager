# What's New

## Admin-Controlled Guest Polling Rates

Admins can now set and manage polling rates for guest users. Guests no longer control their own refresh rate - instead, the admin sets a default guest polling rate from the User tab.

**Default Guest Polling Rate** - Set a global default (Live, 1s, 5s, 10s, 30s, or 60s) that applies to all new guest sessions.

**Per-Guest Override** - Click on any guest session to set a custom polling rate for that specific guest. Useful when you want to give one guest faster updates without affecting others.

**Real-Time Updates** - Changes push to connected guests immediately via SignalR. No page refresh needed on the guest's end.

The polling rate preference is now stored in the database per-user, enabling it to sync across devices and persist through browser changes.

## Polling Rate Improvements

**Live Mode** - Select "Live" for instant updates via SignalR. Data refreshes the moment something changes on the server.

**Throttled Modes** - Choose from 1s, 5s, 10s (default), 30s, or 60s intervals. SignalR events are properly throttled to your selected rate, and changing the rate takes effect immediately without needing a page refresh.

**Server-Side Storage** - Your polling rate preference is saved to the server instead of browser localStorage. Your setting persists across different browsers and devices.

## Session Activity Tracking

Sessions now show accurate "Active" status based on heartbeat data.

**SignalR Connection Tracking** - New backend service maps SignalR connections to device IDs, enabling targeted messaging to specific clients.

**Live Heartbeats** - The session list updates in real-time when guests are active. No more waiting for the next poll cycle to see if someone is online.

**Instant Status** - Your own session shows as "Active" immediately based on local activity detection, rather than waiting for a server round-trip.

## Silent Background Processing

The processing notification bar no longer appears during normal downloads. Previously, when you were downloading a game, the log processing notification would pop up showing "Processing: X MB of Y MB" even though nothing user-facing was happening.

Now, the live log monitor runs completely silently in the background. The processing notification only appears when you manually click "Process All Logs" - which is when you actually want to see progress.

## Chart Theme Colors Fixed

Fixed an issue where service charts (Steam, Epic, Blizzard, etc.) would display incorrect colors for guest users or after theme changes. Chart colors now properly read from CSS variables on each render instead of caching stale values.

## Bug Fixes

- Fixed polling rate changes not taking effect for guest users
- Fixed guest polling rates not persisting to server
- Fixed session heartbeat updates not reflecting in the UI immediately
- Fixed chart colors showing wrong theme colors after switching themes
- Fixed activity status not updating in real-time for admin session list
- Fixed SignalR events bypassing the user's polling rate preference
- Fixed stale processing notifications appearing on page load
