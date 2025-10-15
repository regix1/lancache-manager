## What Changed

### Steam Credentials Now Stored Separately
Your Steam login info now lives in its own encrypted file (`data/steam_auth/credentials.json`) instead of being mixed in with the main state.json file. This keeps things more organized and a bit more secure.

### Setup Wizard Fixes
Fixed a bunch of bugs where the setup wizard would skip ahead too early or get confused about what step you were on.

## Changelog

### Security & Storage
- Steam credentials now get their own encrypted file instead of living in state.json
- If you're upgrading, your existing Steam login will automatically move to the new location
- Cleaned up some old fields that weren't being used anymore (`LastDataLoadTime` and `LastDataMappingCount`)

### Authentication
- Regenerating your API key now logs you out of Steam (just to be safe)
- Logging out now properly clears Steam session data from memory and disk

### Setup Flow
- Fixed the setup wizard marking itself as "done" before actually finishing all the steps
- Fixed state not being saved properly when you refresh during setup
- Fixed issues with detecting if a download was already running after a page reload
- Improved timing between setup steps so they don't trip over each other
- Added better debug logging to help track down issues

### Backend Changes
- Added a new service (SteamAuthStorageService) to handle Steam credential storage
- Refactored how Steam session state and credentials are managed

## Technical Notes

### First Run Migration
When you start this version for the first time, it'll automatically move your Steam credentials from state.json to the new location. You don't need to do anything.

### File Locations
- Steam credentials: `data/steam_auth/credentials.json` (encrypted)
- App state: `data/state.json` (no longer has Steam stuff)
- Encryption keys: `data/DataProtection-Keys/`

### Security
- Uses Microsoft's Data Protection API for encryption
- On Windows, encryption uses DPAPI
- On Linux/Docker, files are protected with chmod 700

## Summary

This update mainly fixes the annoying setup wizard bugs and moves Steam credentials to their own file. The setup process should be less janky now.
