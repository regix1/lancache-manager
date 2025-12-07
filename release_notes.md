## What's New

### Unknown Games Auto-Resolution
The game detection system now automatically resolves "Unknown Game (Depot X)" entries when depot mappings become available. Previously, if a game was detected before its depot mapping existed, it would stay unknown forever until you ran another full scan.

Now when you load cached detection results or run an incremental scan, the system checks if any unknown depots can be matched to actual games using your current mappings. If you ran a PICS sync after the initial detection, those unknown entries get updated automatically.

### Xbox Live Theme Support
Added proper Xbox Live platform color support to the theme system. You can now customize the Xbox color alongside Steam, Epic, Origin, Blizzard, WSUS, and Riot in the theme editor. The default Xbox green matches the official branding.

Also added a dedicated Xbox icon component that renders consistently across different views.

### Depot Name Storage
Steam depot mappings now store the depot name from PICS data (like "Ubisoft Connect PC Client Content"). This is used as a fallback display name for redistributable depots that don't have a clear app name - gives you something more useful than just a depot ID.

## Bug Fixes

- Fixed GitHub depot mapping imports running unnecessarily before full scans (only needed for incremental)
- Fixed notification spam when loading game detection results
- Fixed guest device ID generation on mobile browsers
- Various theme color consistency fixes

## Under the Hood

Cleaned up the depot mapping flow so full scans and incremental scans handle the JSON-to-database import correctly. Full scans create fresh data from scratch, so they don't need to import existing JSON first - that was causing duplicate work.

Thanks for using Lancache Manager!
