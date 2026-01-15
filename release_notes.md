What's New

Prefill reliability and diagnostics
- Prefill now runs network diagnostics and shows the results, including internet reachability and DNS resolution to your cache.
- Network mode auto-detection is more reliable, with bridge mode support and IPv6 disabled inside prefill containers to prevent DNS bypass.
- Prefill works on Docker Desktop for Windows, with improved daemon connectivity.
- The prefill screen has a clearer start flow, confirmation, progress card, and activity log.

Guest access and permissions
- Read only endpoints now require a guest session or admin auth so data is not exposed publicly.
- Prefill access can be granted per guest with a configurable default duration.
- Images and other protected requests include auth headers so guest mode loads reliably.

Client stats control
- You can exclude specific client IPs from analytics in the Clients section.
- Stats and downloads refresh more consistently when time ranges change.

Polish and fixes
- UI cleanup across dashboard, downloads, events, management, and themes.
- Better completion detection and history tracking for prefill sessions.
- Fixes for charts, dropdowns, tooltips, and layout spacing.

Documentation
- Added an Nginx reverse proxy guide and expanded prefill networking troubleshooting.

Thanks for using LANCache Manager!
