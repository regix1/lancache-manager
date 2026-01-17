## What's New

### Docker availability detection
The application now detects whether Docker is accessible and shows helpful setup instructions.

- Prefill, log rotation, and cache management features show Docker setup help when the socket is not mounted
- Linux and Windows get platform-specific guidance
- Management features gracefully handle missing Docker access

### Prefill cache verification
Prefill now verifies which games are already cached and up to date before downloading.

- Games are checked against Steam manifest data to confirm they match the latest version
- The game selection modal shows a badge for games already fully cached
- Saves bandwidth by skipping games that do not need updates

### Network diagnostics improvements
Expanded IPv4 and IPv6 connectivity checks in the prefill diagnostics panel.

- IPv4 and IPv6 connectivity are tested separately
- IPv6 detection warns about potential cache bypass when lancache-dns only supports IPv4
- Host networking mode shows a hint about switching to bridge mode if Steam login hangs
- Diagnostics section is now collapsible to reduce clutter

### Client exclusions
Exclude specific client IPs from all stats and pages.

- Excluded clients are hidden from dashboard metrics, downloads, top clients, and activity charts
- Pick from known clients or enter IPs manually
- Supports both IPv4 and IPv6 addresses

### Retro view scaling
Fixed column sizing issues on the retro downloads page.

- Columns now scale properly when the window is resized
- Minimum column widths are enforced so content remains readable
- Better handling of narrow viewports

### Calendar and event fixes
Calendar navigation and event creation are more reliable.

- Events cannot be created with a start time in the past
- Click-outside behavior closes the expanded day popover correctly
- Week start day setting is respected consistently

### UI polish
Various fixes across dropdowns, tooltips, and layout spacing.

- Timezone selector has an improved layout
- Enhanced dropdown handles edge cases better
- Download views have cleaner spacing and alignment

Thanks for using LANCache Manager!
