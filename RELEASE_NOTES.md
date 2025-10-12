# Release v1.5.4

## Changelog

### Performance & Polling Improvements
Fixed dashboard polling rate system to properly respect user-configured intervals and reduced unnecessary component re-renders.

- **Fixed polling rate not being respected** - SignalR updates now properly follow the user-selected polling interval instead of updating faster than configured
- **Fixed navbar button flashing** - Navigation tabs and buttons no longer flash or highlight during poll cycles
- **Improved component performance** - Applied memoization to Navigation, PollingRateSelector, and TimeFilter components using the same pattern as charts

### Technical Details
- Added ref-based tracking for polling interval to prevent stale closures in SignalR event handlers
- Implemented custom comparison functions for memoized components
- Navigation component now only re-renders when active tab changes
- PollingRateSelector and TimeFilter components now only re-render when props change

Dashboard now updates at the exact interval you select (1s, 5s, 10s, 30s, or 60s) without causing visual glitches.
