What's New

Management Tab Redesign
The Management tab has been completely redesigned with a modern horizontal navigation system, replacing the previous long scrolling page with collapsible sections.

**Horizontal Tab Navigation** - Five organized sections replace the single scrolling page: Authentication, Integrations, Storage, Data, and Preferences. Click any tab to switch between sections instantly.

**Authentication Section** - API key management and demo mode toggle are now grouped together in a clean, focused layout with proper card styling and iconography.

**Integrations Section** - Steam Web API status and Steam Login Manager are displayed in a responsive two-column grid. Steam Web API status now shows version badges and status messages on separate lines for better readability.

Configurable Metrics Scrape Interval
You can now configure how often Prometheus scrapes metrics directly from the UI.

**Adjustable Interval** - Choose from 5, 10, 15, 30, or 60 second intervals. Changes take effect immediately without requiring a restart.

**Live Configuration Preview** - The Prometheus configuration example updates in real-time to show your selected scrape interval, making it easy to copy the correct settings.

**Backend Integration** - The metrics service respects the configured interval, updating metric values at your chosen frequency. Lower intervals provide more granular data but use more resources.

**Storage Section** - Data sources, cache management, log/corruption cleanup, and game cache detection are organized together. The game cache detection pagination bar now integrates seamlessly with the card design.

**Data Section** - Depot mapping, data import/export, and database management grouped for easy access.

**Preferences Section** - Theme management and garbage collection settings in one place.

**Improved Component Styling** - Refresh buttons across all components now show proper loading states with spinners. Cards use consistent tertiary backgrounds and proper spacing.

Color Blindness Accessibility Themes
Three new themes designed specifically for users with color vision deficiencies, all based on the dark default theme.

**Protanopia (Red-Blindness)** - Uses a blue-yellow-orange palette. Replaces red with orange, green with cyan/blue. Success indicators are blue, warnings are yellow, and errors are orange.

**Deuteranopia (Green-Blindness)** - Uses a blue-yellow-magenta palette. Replaces green with cyan, red with magenta. Success indicators are blue, warnings are yellow, and errors are magenta.

**Tritanopia (Blue-Yellow Blindness)** - Uses a teal-green-pink-red palette. Replaces blue with teal, yellow with pink/orange. Success indicators are green, warnings are pink, and errors are red.

Each theme includes carefully selected colors for charts, service indicators, status badges, and all UI elements to ensure clear visual distinction for users with that type of color blindness.

Game Cache Detection Pagination
The pagination bar in the Game Cache Detection section has been redesigned for better visual integration.

**Proper Positioning** - The pagination bar now stays with the content instead of sticking to the bottom of the viewport. Scrolling behaves naturally.

**Edge-to-Edge Design** - The pagination bar extends to the card edges, properly covering rounded corners of game cards above it. No more visual gaps or items showing through.

**Consistent Styling** - The bar now uses a subtle border-top separator and matching rounded bottom corners that align with the parent card.

Dropdown Menu Improvements
Fixed issues with action menus appearing behind other elements and not responding to page layout changes.

**Portal Rendering** - Dropdown menus now render at the document body level, escaping parent stacking contexts. Menus always appear above other elements regardless of their z-index.

**Layout Shift Detection** - Dropdowns automatically close when notifications disappear or any layout shift moves the trigger button. Previously, dismissing a notification at the top of the page would cause the dropdown to stay floating in its original position.

**Viewport Awareness** - Menus are positioned with boundary checking to ensure they don't go off-screen on smaller viewports.

Bug Fixes
- Fixed Management tab being too long and hard to navigate
- Fixed Steam Web API status text being cramped when V2 unavailable message displayed
- Fixed refresh buttons not showing loading state in GC Manager and other components
- Fixed theme action menu appearing behind the next theme card in the list
- Fixed dropdown menus staying visible after notification dismissal caused page content to shift
- Fixed pagination bar sticking to viewport bottom instead of component bottom
- Fixed pagination bar not extending to card edges, causing rounded corners of items to show through
- Fixed Downloads tab pagination styling after shared component changes
