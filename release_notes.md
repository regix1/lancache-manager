## What Changed

### Code Cleanup and Refactoring
The Steam authentication flow was copied in multiple places, so I pulled it all into reusable hooks and components. This makes the code easier to maintain and keeps behavior consistent across different pages.

### New Features
Added a cancel button to the PICS progress bar so you can stop long-running scans without restarting the whole app. Also improved the visual feedback for Steam connection status.

## Changelog

### Refactoring
- Extracted Steam authentication logic into a shared `useSteamAuthentication` hook
- Created a reusable `SteamAuthModal` component instead of duplicating the auth UI everywhere
- Pulled PICS progress polling into its own `usePicsProgress` hook
- Moved time formatting functions to a dedicated utility file
- Cleaned up `SteamLoginManager`, `SteamPicsAuthStep`, `DepotMappingManager`, and `PicsProgressBar` components by removing duplicated code
- Backend: Extracted SteamKit2 helper methods into their own class

### Features
- Added cancel button to PICS progress bar with confirmation modal
- Progress bar now shows Steam connection and login status icons
- Better handling of mobile confirmation during Steam login
- Improved time display for next scheduled crawl (now shows days/hours/minutes properly)

### Backend
- Added `/api/gameinfo/steamkit/cancel` endpoint to stop PICS scans
- Fixed Data Protection key path setup to avoid service provider warnings
- Improved connection activity tracking

## Technical Notes

### Files Added
- `Web/src/hooks/useSteamAuthentication.ts` - Handles Steam login state and API calls
- `Web/src/hooks/usePicsProgress.ts` - Polling hook for PICS progress updates
- `Web/src/components/auth/SteamAuthModal.tsx` - Shared auth modal component
- `Web/src/utils/timeFormatters.ts` - Time formatting utilities
- `Api/LancacheManager/Services/SteamKit2Helpers.cs` - Static helper methods