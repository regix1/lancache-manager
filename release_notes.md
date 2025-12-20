# Release Notes - Critical Fix

## What's New

### Critical: Dashboard Data Consistency Fix
Resolved a significant bug where dashboard statistics displayed incorrect data when switching time ranges.

- Fixed animated values showing nonsensical numbers like "537 TB" when units changed (GB → TB)
- Dashboard now validates incoming data matches the selected time range
- Stats clear immediately on time range change instead of showing stale values

### AnimatedValue Fix
The counting animation now detects unit changes and skips animation to prevent visual glitches.

- Tracks suffix changes (e.g., GB → TB)
- Smooth transitions preserved for same-unit changes

### API Time Filtering
All statistics endpoints now use precise Unix timestamps instead of period strings.

- `getSparklineData()`, `getHourlyActivity()`, `getCacheGrowth()` updated
- All stats query Downloads table directly for consistent data
- Widgets use TimeFilterContext directly instead of prop drilling

### User Preference Persistence
Time range selection now persists across page refreshes.

- Selected time range saved to localStorage
- Custom date ranges also persist

### Code Cleanup
- Removed debug console.log statements from Dashboard and StatsContext

---

Thanks for using LANCache Manager!
