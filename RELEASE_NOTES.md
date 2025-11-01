This release includes important security features, management improvements, and bug fixes following the v1.6.1.1 release. Major highlights include the new Admin session management interface, configurable device limits, read-only directory support, and improved permission handling.

## What's New

### Admin Session Management Interface
Introduced a comprehensive Admin interface for managing all user sessions. View detailed information about connected devices and guest sessions, including device names, IP addresses, operating systems, browsers, and when they last connected. You can now revoke sessions, logout specific devices, and see real-time status for all connections.

### Configurable Device Limits
Control how many devices can use the same admin API key simultaneously through the `Security__MaxAdminDevices` setting (default: 3). This gives you better control over who has access while still being convenient for trusted users.

### Read-Only Directory Support
The system now automatically detects when your cache or log directories are mounted as read-only (with `:ro` flags). Management sections display lock badges and collapse when directories are read-only, preventing failed operations and showing you exactly how to enable full functionality if needed.

### Enhanced Session Tracking
All sessions now record detailed connection information including IP addresses, device details, and last-seen timestamps. This provides better visibility into who's accessing your LANCache and when.

## Security Enhancements

- Added comprehensive permission checking across all management features
- Proper error messages (403 Forbidden) when operations aren't allowed due to permissions
- Improved tracking and validation for both device and guest sessions
- Better error handling to help you understand why something didn't work
- Security checks before all operations to prevent failures

## Bug Fixes

### User Interface
- Improved IP address display with better formatting and IPv6 cleanup
- Added lock icon indicators for read-only sections using theme colors
- Enhanced color consistency across all management components
- Fixed modal display and tooltip positioning issues
- Improved responsive layout for admin session cards

### Management Operations
- Cache clearing now checks permissions before running
- Corruption detection properly validates both cache and log directory access
- Game cache removal works correctly with permission validation
- Better error messages that tell you exactly what to do to fix issues
- Prevented unnecessary error messages when directories are read-only

## Additional Changes

### Admin Interface Features
- Statistics cards showing total sessions, authenticated devices, and guest sessions
- Session filtering and sorting by last activity
- Visual badges to distinguish between authenticated (ADMIN) and guest sessions
- Device information display (OS, browser, connection details)
- Confirmation prompts before revoking or deleting sessions
- Auto-refresh capability for session list

### Permission & Error Handling
- Reduced log noise by adjusting unnecessary permission check messages
- Error messages now include helpful guidance (e.g., "Remove :ro from docker-compose.yml")
- Better distinction between "not authenticated" and "insufficient permissions"

### UI/UX Improvements
- Read-only management sections now collapse to show only headers with lock badges
- Hidden unnecessary warnings when both directories are read-only
- Improved badge styling with consistent colors
- Better visual hierarchy in admin session management
- Enhanced color picker component for theme management
- Improved responsive layouts across all management interfaces

### Configuration
- Added `Security__MaxAdminDevices` environment variable (default: 3)
- Enhanced API key display to show device slot usage
- Improved configuration validation and error reporting

## Upgrade Notes

If you have Docker volumes mounted as read-only (`:ro`), you will see new lock badges in the management interface. To enable full management features, remove the `:ro` flag from your `docker-compose.yml`:

```yaml
volumes:
  - ./logs:/logs        # Remove :ro to enable log management
  - ./cache:/cache      # Remove :ro to enable cache management
```

After updating your configuration, restart the container to enable all features.
