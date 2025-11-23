# What's New

## RESTful API Architecture
Completely refactored the backend API to follow RESTful design principles for better organization and maintainability.

**What Changed**: The old monolithic controller structure has been split into resource-based controllers (ApiKeysController, CacheController, DatabaseController, DepotsController, DevicesController, GamesController, LogsController, SessionsController, SteamAuthController, SystemController).

**Impact**: This is a breaking change for any external tools or scripts that directly interact with the API. The frontend has been updated to use the new endpoints.

## Server-Side User Preferences & Session Management
Moved all user preferences and session management from browser localStorage to secure server-side storage.

**The Problem**: Previously, user preferences (theme, timezone settings, etc.) and session data were stored in browser localStorage. This meant settings didn't sync across devices, guest sessions could be manipulated, and clearing browser data lost all preferences.

**What's Fixed**:
- All user preferences now stored in the database
- Session management moved to secure server-side storage with HttpOnly cookies
- Device fingerprinting generates stable device IDs without localStorage
- Preferences automatically sync across all devices
- Automatic session migration on first login

## HttpOnly Cookie Authentication
Replaced localStorage-based API key storage with secure HttpOnly cookies.

**The Security Issue**: API keys were previously stored in browser localStorage, which is accessible to any JavaScript code on the page, making them vulnerable to XSS attacks.

**What's Fixed**:
- API keys now stored in HttpOnly cookies that JavaScript cannot access
- No sensitive data exposed to client-side code
- Backend manages all authentication state
- System automatically migrates existing localStorage authentication to cookies on first login

## Enhanced Depot Mapping Management
Added convenient "Full Scan" button in the depot mapping interface that triggers a complete refresh of all depot mappings. When depot mappings become outdated (game updates, new DLC releases), you can now trigger a full refresh with a single click.

# Authentication & Session Improvements

## Session Management Overhaul
Fixed numerous session-related issues that caused authentication problems and unexpected logouts.

**What Was Broken**:
- Session IDs and device IDs were conflated, causing confusion
- Guest sessions used timestamped IDs that changed on each visit
- Sessions could get into inconsistent states
- Device fingerprinting fallback could create duplicate sessions

**What's Fixed**:
- Separated session ID from device ID in the database schema
- Guest sessions now use stable device IDs instead of timestamped identifiers
- Better session validation and expiry checking
- Fixed race conditions in session creation
- Improved session cleanup and garbage collection

## Steam Authentication Improvements
- Removed unnecessary v2 API requirements that were blocking some Steam login attempts
- Fixed issue where you couldn't log out of Steam if the API key wasn't configured
- Better error messages when Steam authentication fails

## Guest Session Fixes
- Guest sessions now properly initialize on first visit
- Theme changes save and persist for guest users
- Session expiry is managed server-side with proper notifications

# User Interface Enhancements

## Enhanced Dropdown Components
Significantly improved dropdown UI with new features and better visual design.

**What's New**:
- Right-aligned labels showing contextual info like time intervals ("10s", "1m")
- Dropdown titles for context at the top of menus
- Footer notes for warnings or helpful tips
- Clean style mode without icons for simpler dropdowns
- Smooth slide-in animations with improved timing
- Dropdowns intelligently open upward when near bottom of screen

## Mobile Responsiveness Improvements
- Fixed pagination controls not working properly on mobile devices
- Improved activity tracker to work correctly on mobile browsers
- Better touch target sizes for mobile interaction
- Fixed layout issues on smaller screens

## CSV Export & Icon Improvements
- CSV downloads now have proper formatting across all data types
- Fixed character encoding issues in exported files
- Fixed icon background color inconsistencies
- Updated Clients tab icon from Users to Laptop for better visual distinction

# Bug Fixes

## Timezone Handling
- Timezone preference only triggers re-render when actually changed (prevents render loops)
- Timezone selection properly saves to server-side preferences
- All timestamp conversions now consistently respect the timezone preference
- Fixed API endpoint validation that rejected valid timezone changes

## Notification System Fixes
- Notification bar properly shows and hides based on operation state
- Reduced SignalR console logging that was affecting performance
- Fixed notification timing to match operation lifecycle
- Better cleanup of stale notifications
- Proper handling of rapid notification updates

## Database Fixes
- Fixed database cleanup operations that could fail silently
- Improved error handling for database migrations
- Better handling of concurrent database operations
- Fixed unique constraint violations in some edge cases

## 401 Error Handling
- Consistent 401 error handling across all API calls
- Proper redirect to authentication when session expires
- Better error messages for authentication failures
- Automatic session refresh on 401 if possible

# Technical Improvements

- Reduced excessive console logging in SignalR context
- Better React rendering optimization with proper dependency tracking
- Removed duplicate code across Rust processing utilities
- Consolidated cache path calculations into shared modules
- Improved error handling patterns throughout the codebase

# Migration Notes

This update includes automatic migrations:
- Database migrations will run automatically on first startup
- Session migration happens automatically when you first access the app
- Preference migration transfers your theme and settings to server-side storage

No manual intervention is required. Your existing sessions and preferences will be preserved.
