# What's New

## Background Operations Completely Rebuilt
The entire backend operation tracking system got a major rewrite. Operations like game detection, service removal, and corruption removal now properly survive page refreshes and navigation. No more losing track of what's running when you switch tabs.

The backend now has a dedicated `RemovalOperationTracker` service that keeps tabs on everything. The frontend derives its state directly from notifications instead of trying to manage it separately - which means the UI actually reflects what's happening on the server.

## PUID/PGID Support
Docker container now properly supports running as a custom user via PUID/PGID environment variables. This matches how linuxserver.io images work, so it should feel familiar if you're used to those.

The console output at startup now shows which UID/GID the application is running as - helpful for debugging permission issues.

## Client Stats Got Sorting
The Clients tab now lets you sort by total data, downloads, cache hits, cache misses, hit rate, last activity, or IP. Both ascending and descending. Small thing but makes it way easier to find what you're looking for.

## Active Downloads Show Everything
Previously, active downloads would hide anything that didn't have a mapped game name. Now it shows all downloads, using the service name as a fallback for unmapped content. You won't miss downloads just because the depot mapping hasn't caught up yet.

## Bug Fixes
- Fixed notifications getting stuck after operations complete
- Fixed progress updates not showing during long-running operations
- Fixed console output issues
- User permission fixes for various scenarios
- Depot mapping notification cleanup

## Under the Hood
Removed the `useBackendOperation` hook entirely. It was trying to be clever about tracking operations locally, but it caused more problems than it solved. Everything now goes through the centralized notification system which is way more reliable.

The docker-compose.yml got better documentation about when you need write access to the cache directory (hint: only if you want to delete things).

Thanks for using Lancache Manager!
