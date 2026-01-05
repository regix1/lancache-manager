# What's New

This release brings a comprehensive redesign across the entire application—improved layouts, better responsive design, and a more polished experience overall.

-----

## Dashboard

### New Analytics Widgets

Two new widgets with smooth animations and glassmorphism styling:

- **Cache Growth Trend** - Sparkline showing cache growth over time with current usage, daily growth rate, and estimated days until full. Detects cache clears and shows net growth.
- **Peak Usage Hours** - 24-hour heatmap of download activity. Highlights busiest and current hour with hover tooltips for exact counts.

### Stat Card Trendlines

Dashboard cards now include sparkline trendlines showing data changes over time. Visual indicators help you quickly spot increasing, decreasing, or stable values.

-----

## Downloads

### Retro View Redesign

Complete overhaul with a modern dashboard layout:

- **Circular Efficiency Gauge** - Animated visual representation of cache hit efficiency
- **Combined Progress Bars** - Hit and miss amounts in stacked horizontal bars
- **Resizable Columns** - Drag handles to customize layout
- **Grid/List Toggle** - Switch between detailed and compact views
- **Mobile-Optimized** - Responsive design for smaller screens
- **Status Legend** - Color-coded explanation of each status

-----

## Clients

### Client Nicknames

Assign friendly names to IP addresses for easier identification:

- Create client groups to organize devices
- Set custom nicknames for any IP
- Auto-discovery from download history
- Nicknames appear throughout the app instead of raw IPs

-----

## Users

### Redesigned Guest Management

Improved controls and better session visibility:

- Guest mode toggle with enable/disable
- Default theme selection for guests
- Auto-refresh rate settings
- Active session list with individual revoke
- Guest mode lock to prevent new sessions

-----

## Events

### New Event Interface

Better scheduling controls and multiple view options:

- Calendar and list view toggle
- Inline event creation and editing
- Active events banner for running events
- Priority-based scheduling
- Clear visual distinction between event states

-----

## Prefill

### Redesigned Interface

More intuitive workflow for pre-downloading games:

- **Game Selection Modal** - Browse your Steam library with search and filters
- **Download Progress Card** - Real-time progress with speed, elapsed time, and cancel
- **Activity Log Panel** - Scrollable log of status updates and command output
- **Platform Selection** - Choose OS platforms (Windows, Linux, macOS)
- **Thread Concurrency** - Configure 1-32 download threads
- **Multiple Modes** - Selected games, all owned, recently played, recently purchased, or top 50

### Session Management

New admin section for managing Prefill daemon sessions:

- View active sessions with real-time status
- See Steam username, IP, and download progress
- Per-session prefill history
- Terminate individual or all sessions
- Ban/unban Steam users with optional reason
- Real-time updates via SignalR

-----

## Management

### Tab Redesign

Complete restructure with horizontal tab navigation:

- **Settings** - Display preferences and behavior settings
- **Integrations** - Steam Web API and external service connections
- **Logs & Cache** - Cache management, log cleanup, game detection
- **Data** - Depot mapping, import/export, database management
- **Theme** - Theme customization
- **Clients** - Client nickname management
- **Prefill Sessions** - Daemon session and ban management

-----

## Bug Fixes & Improvements

- Improved responsive design across all pages
- Better theme consistency throughout
- Smoother animations and transitions
- Fixed layout issues on smaller screens
- Improved accessibility with better ARIA labels
- Better loading states with skeleton loaders
- Fixed dropdown menu positioning

-----

Thanks for using LANCache Manager!
