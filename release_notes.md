## What's New

### Datasource Mapping Normalization
The background cleanup service now automatically normalizes datasource mappings in your database.

- Fixes null or empty datasource values
- Corrects inconsistent casing (e.g., "default" vs "Default")
- Remaps invalid datasource names to your configured default datasource
- Runs automatically during scheduled cleanup cycles

### Dashboard & Card Layout Improvements
- StatCard components now use proper flexbox layout for consistent heights
- Sparklines are positioned at the bottom of cards using `mt-auto` for better alignment
- Reduced dashboard grid spacing for a more compact layout

### Theme & UI Fixes
- Fixed LancacheIcon shadow elements to use hardcoded colors instead of CSS variables, preventing theme-related rendering issues
- AccordionSection and CollapsibleSection shadow styling now uses rgba values for consistent appearance across all themes
- Modal backdrop and content now properly inherits theme colors with explicit background and border color assignments

Thanks for using LANCache Manager!
