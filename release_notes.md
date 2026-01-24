## What's New

### Notification system overhaul
The notification bar was rewritten from the ground up for better clarity and recovery options.

- Notifications now show detailed progress with estimated times and byte counts
- Failed operations include recovery actions so you can retry or dismiss without hunting through menus
- Each notification type has its own formatter for cleaner, more readable messages
- SignalR events are now centralized in a shared constants file for easier maintenance

### Event frame redesign
Active event indicators got a visual refresh.

- Solid border with a subtle pulsing glow replaces the old dashed outline
- Hovering the badge shows a tooltip listing all active events when multiple overlap
- Smoother animations and better spacing around framed content

### API architecture improvements
The backend received a significant refactor to reduce code duplication and improve validation.

- Repositories were consolidated into services with a cleaner interface
- Request validation now uses FluentValidation with consistent error responses
- CRUD operations share a common base controller pattern
- SignalR event names are defined once and shared between backend and frontend

### .NET 10 upgrade
The API now runs on .NET 10 for improved performance and long-term support.

### Cache deletion permission fixes
Fixed permission errors when deleting cached files on certain Linux and Windows configurations.

- Rust processor now handles permission edge cases more gracefully
- Path resolution improvements for both platforms

### Authentication stability
Resolved session and authentication issues that could cause unexpected logouts.

- Prefill Steam auth hooks no longer trigger redundant re-authentication
- Idle page timeouts behave more predictably
- Cleaned up leftover debug logging

### Prefill progress tracking
The prefill page now updates more reliably when downloads complete.

- Completed downloads reflect in the UI without requiring a refresh
- Progress card handles edge cases during session transitions

### UI polish
Various fixes across components and themes.

- Theme color tokens applied consistently to cards, buttons, dropdowns, and badges
- Checkbox component sizing corrected
- Mobile layout tweaks for management sections
- Animation timing adjustments for overlays and transitions
- Game images now use a shared component with proper caching headers

Thanks for using LANCache Manager!
