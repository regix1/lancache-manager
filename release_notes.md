# Release Notes - Version 1.5.6

## What's New

### Frontend Code Cleanup
Cleaned up the frontend codebase by removing duplicate components and consolidating shared UI elements for better maintainability.

### Workflow Improvements
Updated the GitHub release workflow to support both `RELEASE_NOTES.md` and `release_notes.md` filenames, making it more flexible for creating releases.

## Changes

### Code Organization
- Moved `Tooltip` component from `common/` to `ui/` folder for better organization
- Removed duplicate `Tooltip.tsx` that wasn't being used
- Removed duplicate inline `SteamIcon` component from `CompactView.tsx`, now uses the shared component from `ui/`
- Updated 11 component files with corrected imports to use the reorganized Tooltip component

### GitHub Workflow
- Updated `.github/workflows/create-release.yml` to accept both uppercase and lowercase release notes filenames
- Workflow now checks for both `RELEASE_NOTES.md` and `release_notes.md` when creating releases
- Automatically removes either file after release creation

### Build & Quality
- All TypeScript builds passing with no errors or warnings
- No unused variables or dead code detected
- All component functionality preserved

The main benefit of this release is improved code organization and a more flexible release workflow, making the project easier to maintain going forward.
