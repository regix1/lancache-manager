## What's New

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

### Time Format Preferences
The timezone selector now includes 24-hour format options. Four choices available:

- **Server (24h)** - Server timezone, 24-hour format (15:30)
- **Server (12h)** - Server timezone, 12-hour format (3:30 PM)
- **Local (24h)** - Your browser timezone, 24-hour format
- **Local (12h)** - Your browser timezone, 12-hour format

Preference syncs across devices via your user account.

### Game Detection Improvements
- **Clickable service badges** - Click any service badge (Steam, Epic, etc.) to filter the list
- Detection results now store which datasource they came from
- Better handling when scanning existing cache data that was previously detected

### Sage & Wood Theme Updates
Improved color contrast for status indicators and text elements.

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
