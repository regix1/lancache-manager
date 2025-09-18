# LanCache Manager Theming Guide

Customize the appearance of your LanCache Manager interface with powerful theming capabilities.

## Quick Start

1. Navigate to Management → Theme Management
2. Browse available themes in the dropdown
3. Preview themes instantly with live preview
4. Click Apply Theme to activate

## Theme Structure

```toml
[meta]
name = "Theme Name"
id = "theme-id"
isDark = true

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
textPrimary = "#ffffff"
# ... all color definitions

[css]
content = """
/* Optional custom CSS */
"""
```

## Color System

### Core Colors
- `primaryColor` - Primary brand color used for main actions, active states, links
- `secondaryColor` - Secondary accent for alternative actions, badges, highlights
- `accentColor` - Tertiary accent for special elements, notifications, indicators

### Background Layers
- `bgPrimary` - Main application background, base layer for entire interface
- `bgSecondary` - Elevated cards, panels, modals - creates visual depth
- `bgTertiary` - Input fields, nested cards, dropdown menus - highest elevation
- `bgHover` - Interactive element hover states, menu item highlights

### Text Hierarchy
- `textPrimary` - Main content, headings, primary information
- `textSecondary` - Descriptions, subtitles, secondary information
- `textMuted` - Disabled states, placeholders, less important text
- `textAccent` - Links, important values, highlighted text
- `textPlaceholder` - Form input placeholder text

### Navigation
- `navBg` - Navigation bar background
- `navBorder` - Navigation bottom border
- `navTabActive` - Active tab background
- `navTabInactive` - Inactive tab text color
- `navTabHover` - Tab hover background
- `navTabActiveBorder` - Active tab indicator border
- `navMobileMenuBg` - Mobile menu dropdown background
- `navMobileItemHover` - Mobile menu item hover state

### Borders & Dividers
- `borderPrimary` - Main borders for cards, sections, dividers
- `borderSecondary` - Subtle borders for input fields, table rows
- `borderFocus` - Focus indicator for active inputs, selections

### Status Indicators
- `success`, `successBg`, `successText` - Success states, confirmations, positive actions
- `warning`, `warningBg`, `warningText` - Warnings, cautions, attention needed
- `error`, `errorBg`, `errorText` - Errors, failures, destructive actions
- `info`, `infoBg`, `infoText` - Information, tips, neutral messages

### Service Colors
Gaming platforms and services shown in charts and badges:
- `steamColor` - Steam downloads
- `epicColor` - Epic Games downloads
- `originColor` - Origin/EA downloads
- `blizzardColor` - Battle.net downloads
- `wsusColor` - Windows Update
- `riotColor` - Riot Games downloads

### Component Styles
- `cardBg` - Card background color
- `cardBorder` - Card border color
- `buttonBg` - Default button background
- `buttonHover` - Button hover state
- `buttonText` - Button text color
- `inputBg` - Form input background
- `inputBorder` - Input field border
- `inputFocus` - Input focus border
- `checkboxAccent` - Checkbox checked color
- `checkboxBorder` - Checkbox border
- `sliderAccent` - Slider filled track
- `sliderThumb` - Slider thumb/handle
- `sliderTrack` - Slider unfilled track
- `badgeBg` - Badge background
- `badgeText` - Badge text color
- `progressBar` - Progress bar fill
- `progressBg` - Progress bar background

### Hit Rate Colors
Cache hit rate indicators with color-coded performance:
- `hitRateHighBg`, `hitRateHighText` - 80%+ hit rate (excellent)
- `hitRateMediumBg`, `hitRateMediumText` - 50-79% hit rate (good)
- `hitRateLowBg`, `hitRateLowText` - 20-49% hit rate (poor)
- `hitRateWarningBg`, `hitRateWarningText` - <20% hit rate (critical)

### Action Buttons
Specific colors for management actions:
- `actionResetBg`, `actionResetHover` - Reset/refresh actions
- `actionProcessBg`, `actionProcessHover` - Process/start actions
- `actionDeleteBg`, `actionDeleteHover` - Delete/remove actions

### Icon Backgrounds
Colored backgrounds for stat card icons:
- `iconBgBlue` - Downloads, bandwidth icons
- `iconBgGreen` - Success, cache hit icons
- `iconBgEmerald` - Active, online icons
- `iconBgPurple` - Services, platform icons
- `iconBgIndigo` - Special, featured icons
- `iconBgOrange` - Warning, attention icons
- `iconBgYellow` - Caution, pending icons
- `iconBgCyan` - Information, stats icons
- `iconBgRed` - Error, cache miss icons

### Chart Colors
Data visualization colors for graphs:
- `chartColor1` through `chartColor8` - Series colors for multi-line charts
- `chartBorderColor` - Chart borders and segment dividers
- `chartGridColor` - Grid lines and axes
- `chartTextColor` - Labels, legends, tooltips
- `chartCacheHitColor` - Cache hit indicators (usually green)
- `chartCacheMissColor` - Cache miss indicators (usually red)

### UI Elements
- `scrollbarTrack` - Scrollbar track background
- `scrollbarThumb` - Scrollbar thumb/handle
- `scrollbarHover` - Scrollbar thumb hover state
- `dragHandleColor` - Drag handle grip dots
- `dragHandleHover` - Drag handle hover state

### Access Indicators
Security/access level badges:
- `publicAccessBg`, `publicAccessText`, `publicAccessBorder` - Public/open access
- `securedAccessBg`, `securedAccessText`, `securedAccessBorder` - Secured/authenticated access

## Creating Themes

### Method 1: Visual Editor
1. Create New Theme
2. Set metadata (name, ID, dark/light mode)
3. Customize each color using color picker
4. Preview changes in real-time
5. Save Theme to local storage

### Method 2: TOML File
Create a `.toml` file with all required colors. See [community-themes](../community-themes/) for examples.

### Method 3: Duplicate & Edit
1. Select existing theme
2. Click Duplicate Theme
3. Edit duplicated theme
4. Save changes

## Custom CSS

Add custom styles beyond colors:

```toml
[css]
content = """
/* Border radius adjustments */
.themed-card {
  border-radius: 12px;
}

/* Animation effects */
button {
  transition: transform 0.2s;
}

button:hover {
  transform: scale(1.02);
}

/* Custom shadows */
.stat-card {
  box-shadow: 0 4px 6px rgba(0,0,0,0.1);
}
"""
```

### CSS Variables
All theme colors available as CSS variables:
```css
var(--theme-primary)
var(--theme-bg-primary)
var(--theme-text-primary)
var(--theme-border-primary)
/* ... and all other color names */
```

## Best Practices

### Dark Themes
- Base background: `#0f172a` to `#111827` (avoid pure black)
- Card backgrounds: `#1e293b` to `#1f2937` (elevated from base)
- Primary text: `#f8fafc` to `#ffffff` (high contrast)
- Borders: `#334155` to `#374151` (visible but subtle)

### Light Themes
- Base background: `#ffffff` to `#fafafa` (clean white)
- Card backgrounds: `#f9fafb` to `#f3f4f6` (subtle depth)
- Primary text: `#111827` to `#0f172a` (avoid pure black)
- Borders: `#d1d5db` to `#e5e7eb` (soft definition)

### Accessibility
- Normal text: 4.5:1 contrast minimum
- Large text: 3:1 contrast minimum
- UI components: 3:1 contrast minimum
- Focus indicators: clearly visible

## Import/Export

### Export
1. Find theme in list
2. Click Export icon
3. Downloads as `.toml` file

### Import
1. Click Import Theme
2. Select `.toml` file
3. Theme validates and imports

## Troubleshooting

**Theme not applying**: Clear browser cache (Ctrl+F5)
**Colors incorrect**: Verify `isDark` matches theme type
**Import fails**: Validate TOML syntax, check all required colors present
**CSS not working**: Check selector specificity, use `!important` if needed
**Performance issues**: Reduce CSS animations, optimize selectors

## Example Themes

See [community-themes](../community-themes/) folder for complete theme examples. The application includes several built-in themes including Sage Wood.