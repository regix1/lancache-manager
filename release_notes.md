## What's New

### UI Display Improvements
- Updated display technique for better item rendering
- Fixed alignment issues across various components
- Improved visual consistency for item layouts

### Multiple Datasources Support
You can now configure multiple log and cache directory pairs. Useful if you're running multiple LANCache instances or have logs split across different locations.

- New **Datasources** section in the Management tab shows all configured data sources
- Each datasource displays its cache path, logs path, and read/write status
- Process logs individually per datasource or all at once
- Reset log positions per datasource to reprocess history or skip to new entries
- Downloads now track which datasource they came from
- Game detection results remember their source datasource
- Filter downloads and detection results by datasource

Configure via `docker-compose.yml` environment variables or `appsettings.json`.

### Live Clock Display
The timezone selector now shows a live clock that updates every second. The button displays the current time in your chosen format, making it easy to see both the time and quickly access format settings.

### Time Format Preferences
Four time display options available:

- **Server (24h)** - Server timezone, 24-hour format (15:30)
- **Server (12h)** - Server timezone, 12-hour format (3:30 PM)
- **Local (24h)** - Your browser timezone, 24-hour format
- **Local (12h)** - Your browser timezone, 12-hour format

Preference syncs across devices via your user account.

### Date Range Picker Improvements
- Month and year dropdowns now close when clicking outside
- Custom themed scrollbar styling in dropdowns
- Clearing dates and closing the modal now automatically switches back to live mode
- Improved dropdown sizing without excess padding

### Simplified Polling Rate Options
Removed unnecessary server load warnings from polling rate descriptions. For a single-user application, the performance difference between intervals is negligible - the only real difference is data freshness.

### Game Detection Improvements
- **Clickable service badges** - Click any service badge (Steam, Epic, etc.) to filter the list
- Detection results now store which datasource they came from
- Better handling when scanning existing cache data that was previously detected

### Clearer Download Counts
Download badges now show "X clients · Y requests" instead of "X downloads". This makes it clear how many unique machines downloaded a game versus how many individual download sessions occurred. Steam creates many sessions per game download, so "3 clients · 1271 requests" is much clearer than "1271 downloads".

### Sage & Wood Theme Updates
Improved color contrast for status indicators and text elements.

## UI Improvements
- Service clear buttons (Clear steam, Clear wsus, etc.) now have visible borders for better visibility across themes
- Depot Mapping moved to top of Data Configuration section
- CustomScrollbar component now supports compact padding mode for better dropdown styling
- EnhancedDropdown uses themed scrollbar with reduced padding

## Bug Fixes
- Fixed log processing failing when multiple log directories are configured
- Fixed live log monitoring not picking up changes from all datasources
- Fixed game detection not properly handling pre-existing cache scan results
- Removed unnecessary progress bar from the notification banner

## Under the Hood
- New `DatasourceService` for managing multiple cache/log directory configurations
- Database migrations adding datasource tracking to downloads and cached detections
- Rust log processor and service manager updated to accept datasource parameters
- `StateRepository` extended with per-datasource log position tracking
