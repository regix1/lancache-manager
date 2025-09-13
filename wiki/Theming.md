# Theming Guide

LANCache Manager includes a powerful theme engine that lets you customize every color in the interface.

## Quick Start

1. Navigate to **Management** → **Theme Management**
2. Select a theme from the dropdown or create your own
3. Click **Apply** to activate

## Theme Structure

Themes are TOML files with three sections:

### Meta Information
```toml
[meta]
name = "My Theme"
id = "my-theme"
description = "A custom theme"
author = "Your Name"
version = "1.0.0"
isDark = true
```

### Colors
```toml
[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
textPrimary = "#ffffff"
# ... more colors
```

### Custom CSS (Optional)
```toml
[css]
content = """
/* Your custom styles */
"""
```

## Color Categories

### Core Colors
Control the main brand colors throughout the interface.

- `primaryColor` - Main brand color (buttons, links, active states)
- `secondaryColor` - Complementary accent (highlights, badges)
- `accentColor` - Additional accent (special elements)

### Backgrounds
Layer your UI with proper depth.

- `bgPrimary` - Main app background
- `bgSecondary` - Card and panel backgrounds
- `bgTertiary` - Input fields, nested elements
- `bgHover` - Hover state backgrounds

### Text
Ensure readability with proper text hierarchy.

- `textPrimary` - Main content
- `textSecondary` - Descriptions, subtitles
- `textMuted` - Disabled states, placeholders
- `textAccent` - Links, highlighted values

### Borders
Define element boundaries.

- `borderPrimary` - Main borders (cards, dividers)
- `borderSecondary` - Subtle borders (inputs)
- `borderFocus` - Focused element borders
- `cardBorder` - Specific card borders

### Status Colors
Communicate system states clearly.

Each status has three colors:
- Base color (buttons, indicators)
- Background (alert backgrounds)
- Text (message text)

Example:
```toml
success = "#10b981"
successBg = "#064e3b"
successText = "#34d399"
```

### Service Colors
Customize gaming platform colors.

```toml
steamColor = "#1e40af"
epicColor = "#7c3aed"
originColor = "#ea580c"
blizzardColor = "#0891b2"
wsusColor = "#16a34a"
riotColor = "#dc2626"
```

### Chart Colors
Control data visualization appearance.

- `chartColor1` through `chartColor8` - Data series colors
- `chartBorderColor` - Segment borders
- `chartGridColor` - Grid lines
- `chartTextColor` - Labels and legends
- `chartCacheHitColor` - Cache hit indicators
- `chartCacheMissColor` - Cache miss indicators

## Creating a Theme

### Method 1: In-App Editor

1. Click **Create Theme** button
2. Fill in theme information
3. Adjust colors using the color picker
4. Preview changes in real-time
5. Click **Save Theme**

### Method 2: Upload TOML File

1. Create a `.toml` file with your theme
2. Drag and drop onto the upload area
3. Theme is automatically applied

### Method 3: Edit Existing Theme

1. Click **Edit** on any custom theme
2. Modify colors and settings
3. Save changes

## Color Tips

### Dark Themes
- Use `#111827` to `#1f2937` for backgrounds
- Keep text colors above `#9ca3af` for readability
- Use subtle borders (`#374151` to `#4b5563`)

### Light Themes
- Use `#ffffff` to `#f9fafb` for backgrounds
- Keep text colors below `#6b7280` for contrast
- Use lighter borders (`#e5e7eb` to `#d1d5db`)

### Contrast Guidelines
- Maintain 4.5:1 ratio for normal text
- Maintain 3:1 ratio for large text
- Test with different monitor settings

## Advanced Customization

### Custom CSS
Add your own styles to further customize the interface:

```toml
[css]
content = """
/* Rounded corners on all cards */
.themed-card {
  border-radius: 12px;
}

/* Custom button shadows */
button {
  box-shadow: 0 2px 4px rgba(0,0,0,0.1);
}
"""
```

### Dynamic Theme Colors
The theme engine uses CSS variables, allowing real-time updates:

- `var(--theme-primary)` - Primary color
- `var(--theme-bg-primary)` - Primary background
- `var(--theme-text-primary)` - Primary text
- And many more...

## Sharing Themes

### Export
1. Click **Export** button on any theme
2. Share the `.toml` file with others

### Import
1. Receive a theme `.toml` file
2. Upload via the Theme Manager
3. Apply and enjoy

## Troubleshooting

### Theme Not Applying
- Refresh the page after applying
- Check browser console for errors
- Verify TOML syntax is correct

### Colors Look Wrong
- Check if theme is marked as dark/light correctly
- Verify color hex codes are valid
- Test on different displays

### Can't Edit System Themes
- System themes (dark-default, light-default) are read-only
- Create a copy by exporting and re-uploading

## Example Themes

### Midnight Blue
```toml
[meta]
name = "Midnight Blue"
isDark = true

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#0f172a"
bgSecondary = "#1e293b"
textPrimary = "#f1f5f9"
```

### Forest Green
```toml
[meta]
name = "Forest Green"
isDark = true

[colors]
primaryColor = "#10b981"
bgPrimary = "#14532d"
bgSecondary = "#166534"
textPrimary = "#dcfce7"
```

## Best Practices

1. **Test thoroughly** - Check all tabs and features
2. **Consider accessibility** - Ensure sufficient contrast
3. **Be consistent** - Use similar colors for related elements
4. **Document your theme** - Add meaningful descriptions
5. **Version your changes** - Update version numbers

## Need Help?

- Download the sample theme template
- Check existing themes for inspiration
- Report issues on GitHub