# Release Notes - Version 1.5.6.1

## What's New

### Separate Encrypted Storage for Steam Credentials
Moved Steam authentication credentials out of the main state.json file into a dedicated encrypted file (`data/steam_auth/credentials.json`) for better security and separation of concerns.

### Setup Flow Improvements
Fixed several issues in the initialization wizard that were causing setup steps to complete prematurely or get stuck in certain scenarios.

## Changes

### Security & State Management
- **New SteamAuthStorageService**: Created dedicated service for managing Steam credentials in a separate encrypted file using Microsoft ASP.NET Core Data Protection API
- **StateService refactoring**: Removed Steam auth data from main state.json and migrated to separate storage
- **Automatic migration**: Existing Steam credentials are automatically migrated to the new storage location on first startup
- **Removed unused state fields**: Cleaned up `LastDataLoadTime` and `LastDataMappingCount` fields that were being stored but never read

### Authentication & Session Management
- **AuthController updates**: Added Steam session logout when regenerating API keys for better security
- **Session cleanup**: Steam auth data is now properly cleared from both in-memory and persistent storage when logging out
- **Program.cs**: Registered new SteamAuthStorageService as a singleton

### Frontend Setup Flow Fixes
- **DepotInitializationModal**:
  - Added proper state persistence for Steam auth usage across page reloads
  - Fixed premature setup completion that was marking initialization as done before all steps finished
  - Removed duplicate setup completion calls that were causing step flow issues
  - Better handling of download-in-progress detection after page reload

- **Setup Step Components**:
  - Updated DepotInitStep, DepotMappingStep, LogProcessingStep, PicsProgressStep to properly coordinate with the modal
  - Fixed step transition timing issues
  - Improved console logging for better debugging

- **App.tsx**: Updated initialization flow to work with the new setup completion logic

- **SteamPicsAuthStep**: Minor updates to coordinate with the new Steam auth storage system

### SteamKit2Service Updates
- Updated to use the new SteamAuthStorageService for credential management
- Better separation between Steam session state and credential storage

## Technical Details

### Migration Process
When you first start this version:
1. The app will check for existing Steam auth data in state.json
2. If found, it will be migrated to `data/steam_auth/credentials.json`
3. The Steam auth section will be removed from state.json
4. The migration is one-time and automatic

### File Locations
- **Steam credentials**: `data/steam_auth/credentials.json` (encrypted)
- **Application state**: `data/state.json` (no longer contains Steam auth)
- **Data Protection keys**: `data/DataProtection-Keys/` (used for encryption)

### Security Notes
- Steam credentials are encrypted using Microsoft Data Protection API
- On Windows, encryption keys are protected using DPAPI
- On Linux/Docker, keys are protected by filesystem permissions (chmod 700)
- Regenerating the API key now properly logs out active Steam sessions

## Benefits

This release improves the setup experience by fixing several issues where the initialization wizard would get confused about which step to show or would mark setup as complete before all steps were actually done. The separation of Steam credentials into their own encrypted file also improves security and makes the state management cleaner.
