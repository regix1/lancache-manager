# What's New

## Authentication Display Fix

Fixed a frustrating issue where scheduled depot mapping scans would show the wrong authentication status in notifications.

**What Happened:** When you set up automatic depot mapping scans (like every hour), the scans would run fine in the background. But if your browser was asleep or minimized, the notification would show "Steam Anonymous" instead of "Steam Authenticated" - even though you were actually logged in with your Steam account. This was purely a display issue, but it made it hard to tell if your authenticated scans were actually working correctly.

**What's Fixed:** The system now correctly identifies your authentication status based on your saved credentials, not just the active connection state. Your scheduled scans will now show the right badge - "Steam Authenticated" when using your account, "Steam Anonymous" when not - regardless of whether the page was active when the scan started.

**Why This Matters:** When you're logged into Steam, you get access to all depot mappings including private and playtest games. It's important to know your automatic scans are actually using your authenticated session, not falling back to anonymous mode with limited access.

## Notification System Improvements

Cleaned up various notification-related quirks to make the UI more reliable and responsive.

**What's Fixed:**
- Notifications now consistently appear and disappear at the right times
- Fixed issues where notification messages would become stale or not update properly
- Removed duplicate UI elements that could confuse the interface
- Improved timezone handling for accurate timestamps across all notifications

**Impact:** The notification bar should feel smoother and more predictable. When operations complete, you'll see the right status immediately without having to refresh or wonder if something got stuck.

## Nginx Log Rotation Support

Added automatic nginx signal handling to prevent log access issues with monolithic LANCache containers.

**The Problem:** Some LANCache setups use a single monolithic container that rotates log files periodically. When this happens, nginx needs to be told to reopen the log file, or the old file handle becomes stale and new data doesn't get written. This meant LANCache Manager could lose visibility into new downloads.

**The Solution:** LANCache Manager can now automatically detect when log files are being manipulated and signal the nginx container to reopen them. This keeps your download tracking seamless even through log rotations.

**How It Works:**
1. Mount the Docker socket in your docker-compose (see README for details)
2. LANCache Manager detects log file changes
3. Automatically sends the appropriate signal to nginx
4. Your log tracking continues without interruption

**Security Note:** This feature mounts the Docker socket read-only and only needs to run `docker exec` commands to signal nginx. You can disable it entirely if you don't need it - see the README for configuration options.

---

This release focuses on polish and reliability - fixing the small things that make day-to-day use more pleasant. Your scheduled scans now show accurate status, notifications behave consistently, and log rotation won't break your tracking.
