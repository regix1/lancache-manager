# Theming

## Quick Start

1. Management → Theme Management
2. Select theme from dropdown
3. Apply Theme

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
- `primaryColor` - Main brand color
- `secondaryColor` - Secondary accent
- `accentColor` - Tertiary accent

### Backgrounds
- `bgPrimary` - Main background
- `bgSecondary` - Cards/panels
- `bgTertiary` - Nested elements
- `bgHover` - Hover states

### Text
- `textPrimary` - Main text
- `textSecondary` - Subtitles
- `textMuted` - Disabled
- `textAccent` - Links

### Status
- `success`, `successBg`, `successText`
- `warning`, `warningBg`, `warningText`
- `error`, `errorBg`, `errorText`
- `info`, `infoBg`, `infoText`

### Services
- `steamColor`, `epicColor`, `originColor`
- `blizzardColor`, `riotColor`, `uplayColor`
- `wsusColor`, `xboxColor`, `playstationColor`

## Creating Themes

### Method 1: Visual Editor
1. Create New Theme
2. Set metadata
3. Customize colors
4. Preview changes
5. Save Theme

### Method 2: TOML File
```toml
[meta]
name = "Custom Theme"
id = "custom"
isDark = true

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#0f172a"
bgSecondary = "#1e293b"
textPrimary = "#f8fafc"
# ... all required colors
```

### Method 3: Duplicate
1. Select existing theme
2. Duplicate Theme
3. Edit colors
4. Save changes

## Custom CSS

```toml
[css]
content = """
.themed-card {
  border-radius: 12px;
  backdrop-filter: blur(10px);
}

button:hover {
  transform: scale(1.02);
}
"""
```

### CSS Variables
```css
var(--theme-primary)
var(--theme-bg-primary)
var(--theme-text-primary)
var(--theme-border-primary)
```

## Best Practices

### Dark Themes
- Base: `#0f172a` - `#111827`
- Cards: `#1e293b` - `#1f2937`
- Text: `#f8fafc` - `#ffffff`
- Never use pure black

### Light Themes
- Base: `#ffffff` - `#fafafa`
- Cards: `#f9fafb` - `#f3f4f6`
- Text: `#111827` - `#0f172a`
- Never use pure white for text

### Accessibility
- Normal text: 4.5:1 minimum
- Large text: 3:1 minimum
- UI components: 3:1 minimum

## Import/Export

### Export
1. Find theme in list
2. Click Export
3. Downloads as `.toml`

### Import
1. Click Import Theme
2. Select `.toml` file
3. Theme validates and imports

## Troubleshooting

**Theme not applying**: Clear cache (Ctrl+F5)
**Colors wrong**: Check `isDark` setting
**Import fails**: Validate TOML syntax
**CSS not working**: Check specificity

## Example Themes

### Cyberpunk
```toml
[meta]
name = "Cyberpunk"
id = "cyberpunk"
isDark = true

[colors]
primaryColor = "#00ffff"
secondaryColor = "#ff00ff"
accentColor = "#ffff00"
bgPrimary = "#0a0a0a"
bgSecondary = "#1a0a1a"
textPrimary = "#ffffff"
borderPrimary = "#00ffff"
```

### Dracula
```toml
[meta]
name = "Dracula"
id = "dracula"
isDark = true

[colors]
primaryColor = "#bd93f9"
secondaryColor = "#ff79c6"
accentColor = "#8be9fd"
bgPrimary = "#282a36"
bgSecondary = "#44475a"
textPrimary = "#f8f8f2"
borderPrimary = "#44475a"
```
