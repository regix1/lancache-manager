This release includes user interface improvements and enhanced session tracking following the v1.6.2 release. Major highlights include the renamed Users management interface, improved session information display, and better IP address formatting.

## What's New

### Users Tab (formerly Admin)
Renamed the "Admin" interface to "Users" to better reflect its purpose of managing all user sessions. The tab now appears before the Management tab in the navigation for easier access. Updated all terminology throughout the interface to use "User" instead of "Admin" for authenticated sessions.

### Enhanced Session Information
Guest sessions now display the same detailed information as authenticated sessions, including:
- Operating system with version detection (e.g., "Windows 10/11", "macOS 14.2")
- Browser with version numbers (e.g., "Chrome 140.0.0.0", "Firefox 115.0")
- Consistent information display across both session types

### Improved IP Address Tracking
The "Revoked by" field now shows the IP address of the user who revoked a session instead of their device name. This provides clearer audit trails and better tracking of administrative actions. Localhost addresses (::1, 127.0.0.1) are automatically displayed as "localhost" for better readability.

## Bug Fixes

### User Interface
- Fixed "Revoked by" field to display IP addresses instead of device names
- Improved localhost detection - now shows "localhost" instead of "::1" or "127.0.0.1"
- Enhanced browser detection with full version numbers
- Improved OS detection including Windows 10/11, macOS versions, and Android versions
- Changed Clients tab icon from Users to Laptop for better visual distinction
- Removed database icon from "Detect Games" button (kept loading spinner)

### Session Management
- Guest sessions now store and display operating system information
- Guest sessions now store and display browser information
- Consistent IPv6 address cleanup across all IP displays
- Better user agent parsing for more accurate device information

## Additional Changes

### Navigation
- Reordered tabs: Dashboard → Downloads → Clients → Users → Management
- Updated tab icons for better clarity (Users icon for Users tab, Laptop icon for Clients tab)
- Improved visual consistency across navigation elements
