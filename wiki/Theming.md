# LANCache Manager Theme Customization Guide

LANCache Manager features a comprehensive theme engine that provides complete control over the visual appearance of the interface. This guide covers everything from basic color adjustments to advanced CSS customization.

<img width="1505" height="1015" alt="Wiki-Page-Themeing-Example" src="https://github.com/user-attachments/assets/c0f59824-f42e-465c-bacc-f78570c9240b" />

## Table of Contents
- [Quick Start](#quick-start)
- [Theme Structure](#theme-structure)
- [Color System](#color-system)
- [Creating Themes](#creating-themes)
- [Advanced Customization](#advanced-customization)
- [Theme Distribution](#theme-distribution)
- [Troubleshooting](#troubleshooting)

## Quick Start

### Accessing Theme Manager
1. Navigate to **Management** tab in the main interface
2. Scroll to **Theme Management** section
3. Select a pre-built theme from the dropdown menu
4. Click **Apply Theme** to activate immediately
5. Changes are applied instantly without page refresh

### Built-in Themes
- **Dark Default**: Modern dark theme optimized for low-light environments
- **Light Default**: Clean light theme for bright environments
- **Custom Themes**: User-created themes appear here after creation

## Theme Structure

Themes are defined using TOML (Tom's Obvious, Minimal Language) format, which provides a clean and readable structure for configuration. Each theme consists of three main sections:

### 1. Meta Information Section
Defines theme metadata and identification:

```toml
[meta]
name = "My Custom Theme"          # Display name in theme selector
id = "my-custom-theme"             # Unique identifier (alphanumeric + hyphens)
description = "A detailed description of your theme"
author = "Your Name"               # Theme creator
version = "1.0.0"                  # Semantic versioning
isDark = true                      # Theme type (affects defaults)
createdAt = "2024-01-01T00:00:00Z" # ISO 8601 timestamp
updatedAt = "2024-01-01T00:00:00Z" # Last modification time
```

### 2. Colors Section
Contains all color definitions for the interface:

```toml
[colors]
# Primary brand colors
primaryColor = "#3b82f6"     # Main brand color
secondaryColor = "#8b5cf6"   # Secondary accent
accentColor = "#ec4899"       # Tertiary accent

# Background layers
bgPrimary = "#111827"         # Main background
bgSecondary = "#1f2937"       # Cards and panels
bgTertiary = "#374151"        # Nested elements
bgHover = "#4b5563"           # Hover states

# Text hierarchy
textPrimary = "#ffffff"       # Main text
textSecondary = "#d1d5db"     # Subtitles
textMuted = "#9ca3af"         # Disabled text
textAccent = "#60a5fa"        # Links and highlights

# ... additional colors
```

### 3. Custom CSS Section (Optional)
Allows advanced styling beyond color changes:

```toml
[css]
content = """
/* Custom CSS rules */
.themed-card {
  border-radius: 16px;
  backdrop-filter: blur(10px);
}

/* Animation effects */
.stat-card {
  transition: transform 0.2s ease;
}

.stat-card:hover {
  transform: translateY(-2px);
}
"""
```

## Color System

The theme engine uses a comprehensive color system organized into logical categories. Each color serves a specific purpose and affects multiple UI elements.

### Core Brand Colors
These colors define your theme's primary identity and are used throughout the interface:

| Color | Usage | Default (Dark) | Default (Light) |
|-------|-------|----------------|------------------|
| `primaryColor` | Buttons, links, active states, primary actions | `#3b82f6` | `#2563eb` |
| `secondaryColor` | Secondary buttons, badges, highlights | `#8b5cf6` | `#7c3aed` |
| `accentColor` | Special elements, notifications, alerts | `#ec4899` | `#db2777` |

### Background Layers
Create visual hierarchy through layered backgrounds:

| Color | Usage | Dark Theme | Light Theme | Notes |
|-------|-------|------------|-------------|-------|
| `bgPrimary` | Main application background | `#111827` | `#ffffff` | Base layer |
| `bgSecondary` | Cards, panels, modals | `#1f2937` | `#f9fafb` | First elevation |
| `bgTertiary` | Input fields, nested cards | `#374151` | `#f3f4f6` | Second elevation |
| `bgHover` | Hover states for interactive elements | `#4b5563` | `#e5e7eb` | Interaction feedback |
| `bgActive` | Active/selected states | `#6b7280` | `#d1d5db` | Selection indicator |
| `bgOverlay` | Modal overlays, dropdowns | `rgba(0,0,0,0.5)` | `rgba(0,0,0,0.3)` | Semi-transparent |

### Text Colors
Maintain readability with a clear text hierarchy:

| Color | Usage | Contrast Ratio | Examples |
|-------|-------|----------------|----------|
| `textPrimary` | Main content, headings | 15:1 (dark) / 21:1 (light) | Page titles, primary text |
| `textSecondary` | Descriptions, subtitles | 7:1 minimum | Timestamps, metadata |
| `textMuted` | Disabled states, placeholders | 4.5:1 minimum | Input placeholders |
| `textAccent` | Links, important values | 7:1 minimum | Hyperlinks, statistics |
| `textInverse` | Text on colored backgrounds | 4.5:1 minimum | Button text |
| `textDanger` | Error messages | 7:1 minimum | Validation errors |

### Border System
Define element boundaries and visual separation:

| Color | Width | Usage | Example |
|-------|-------|-------|----------|
| `borderPrimary` | 1px | Main borders, dividers | Card edges, section dividers |
| `borderSecondary` | 1px | Subtle borders | Input fields, table rows |
| `borderFocus` | 2px | Focus indicators | Active input fields |
| `cardBorder` | 1px | Card-specific borders | Dashboard cards |
| `borderHover` | 1px | Hover state borders | Interactive element hover |
| `borderDivider` | 1px | Content separators | Horizontal rules |

### Status Colors
Communicate system states with consistent color patterns. Each status type includes three color variants for different use cases:

#### Success States
```toml
success = "#10b981"        # Success buttons, icons
successBg = "#064e3b"      # Success alert backgrounds
successText = "#34d399"    # Success message text
successBorder = "#059669"  # Success alert borders
```

#### Warning States
```toml
warning = "#f59e0b"        # Warning indicators
warningBg = "#78350f"      # Warning backgrounds
warningText = "#fbbf24"    # Warning text
warningBorder = "#d97706"  # Warning borders
```

#### Error States
```toml
error = "#ef4444"          # Error buttons, icons
errorBg = "#7f1d1d"        # Error backgrounds
errorText = "#f87171"      # Error messages
errorBorder = "#dc2626"    # Error borders
```

#### Information States
```toml
info = "#3b82f6"           # Info indicators
infoBg = "#1e3a8a"         # Info backgrounds
infoText = "#60a5fa"       # Info text
infoBorder = "#2563eb"     # Info borders
```

### Service Colors
Customize colors for different gaming platforms and services. These colors are used in charts, badges, and service indicators:

```toml
# Gaming Platforms
steamColor = "#1e40af"         # Steam (blue)
epicColor = "#7c3aed"          # Epic Games (purple)
originColor = "#ea580c"        # Origin/EA (orange)
blizzardColor = "#0891b2"      # Battle.net (cyan)
riotColor = "#dc2626"          # Riot Games (red)
uplayColor = "#0ea5e9"         # Ubisoft Connect (sky blue)

# System Services
wsusColor = "#16a34a"          # Windows Update (green)
xboxColor = "#10b981"          # Xbox/Microsoft (emerald)
playstationColor = "#1e3a8a"   # PlayStation (navy)
nintendoColor = "#ef4444"      # Nintendo (bright red)

# Other Services
otherColor = "#6b7280"         # Unknown services (gray)
customColor = "#8b5cf6"        # Custom services (violet)
```

#### Service Color Guidelines
- Use official brand colors when possible
- Ensure sufficient contrast with backgrounds
- Consider color-blind accessibility
- Test colors in both light and dark themes

### Chart and Visualization Colors
Data visualization colors for graphs, charts, and statistics:

#### Data Series Colors
```toml
# Primary data series (used in order)
chartColor1 = "#3b82f6"   # Primary series
chartColor2 = "#10b981"   # Secondary series
chartColor3 = "#f59e0b"   # Tertiary series
chartColor4 = "#ef4444"   # Quaternary series
chartColor5 = "#8b5cf6"   # Additional series
chartColor6 = "#ec4899"   # Additional series
chartColor7 = "#14b8a6"   # Additional series
chartColor8 = "#f97316"   # Additional series
```

#### Chart Structure Colors
```toml
chartBorderColor = "#374151"     # Chart and segment borders
chartGridColor = "#1f2937"       # Grid lines and axes
chartTextColor = "#9ca3af"       # Labels, legends, tooltips
chartBgColor = "#111827"         # Chart background
chartTooltipBg = "#1f2937"       # Tooltip backgrounds
```

#### Performance Indicators
```toml
chartCacheHitColor = "#10b981"   # Cache hit bars/lines
chartCacheMissColor = "#ef4444"  # Cache miss bars/lines
chartBandwidthColor = "#3b82f6"  # Bandwidth indicators
chartLatencyColor = "#f59e0b"    # Latency indicators
```

## Creating Themes

### Method 1: Visual Theme Editor (Recommended)

#### Step 1: Access Theme Creator
1. Navigate to **Management → Theme Management**
2. Click the **Create New Theme** button
3. The theme editor modal will open

#### Step 2: Configure Theme Metadata
```
Theme Name: [Enter descriptive name]
Theme ID: [Auto-generated or custom]
Description: [Describe your theme]
Author: [Your name]
Version: [1.0.0]
Theme Type: [x] Dark Theme  [ ] Light Theme
```

#### Step 3: Customize Colors
1. **Use Color Picker**: Click any color swatch to open the picker
2. **Enter Hex Values**: Type exact hex codes for precision
3. **Copy from Existing**: Use eyedropper tool to sample colors
4. **Preview Live**: Changes appear instantly in the preview pane

#### Step 4: Test Your Theme
1. Click **Preview Theme** to see full-page preview
2. Navigate through different tabs to test all elements
3. Check readability and contrast
4. Verify color consistency

#### Step 5: Save and Apply
1. Click **Save Theme** to store locally
2. Click **Apply Theme** to activate
3. Theme is now available in the theme selector

### Method 2: TOML File Upload

#### Creating a TOML File
1. Create a new file with `.toml` extension
2. Use a text editor (VS Code, Notepad++, etc.)
3. Follow this template structure:

```toml
# theme-template.toml
[meta]
name = "My Custom Theme"
id = "my-custom-theme"
description = "A beautiful custom theme"
author = "Your Name"
version = "1.0.0"
isDark = true

[colors]
# Core Colors
primaryColor = "#3b82f6"
secondaryColor = "#8b5cf6"
accentColor = "#ec4899"

# Backgrounds
bgPrimary = "#0f172a"
bgSecondary = "#1e293b"
bgTertiary = "#334155"
bgHover = "#475569"

# Text
textPrimary = "#f8fafc"
textSecondary = "#cbd5e1"
textMuted = "#94a3b8"
textAccent = "#38bdf8"

# Borders
borderPrimary = "#334155"
borderSecondary = "#1e293b"
borderFocus = "#3b82f6"

# Status Colors
success = "#10b981"
successBg = "#064e3b"
successText = "#34d399"

warning = "#f59e0b"
warningBg = "#78350f"
warningText = "#fbbf24"

error = "#ef4444"
errorBg = "#7f1d1d"
errorText = "#f87171"

info = "#3b82f6"
infoBg = "#1e3a8a"
infoText = "#60a5fa"

# Service Colors
steamColor = "#1e40af"
epicColor = "#7c3aed"
originColor = "#ea580c"
blizzardColor = "#0891b2"
wsusColor = "#16a34a"
riotColor = "#dc2626"

# Chart Colors
chartColor1 = "#3b82f6"
chartColor2 = "#10b981"
chartColor3 = "#f59e0b"
chartColor4 = "#ef4444"
chartColor5 = "#8b5cf6"
chartColor6 = "#ec4899"
chartColor7 = "#14b8a6"
chartColor8 = "#f97316"

chartBorderColor = "#334155"
chartGridColor = "#1e293b"
chartTextColor = "#94a3b8"
chartCacheHitColor = "#10b981"
chartCacheMissColor = "#ef4444"

# Additional UI Elements
buttonBg = "#3b82f6"
buttonText = "#ffffff"
buttonHoverBg = "#2563eb"
buttonDisabledBg = "#475569"

inputBg = "#1e293b"
inputBorder = "#334155"
inputFocusBorder = "#3b82f6"
inputText = "#f8fafc"
inputPlaceholder = "#64748b"

modalBg = "#1e293b"
modalOverlay = "rgba(0, 0, 0, 0.75)"

tooltipBg = "#0f172a"
tooltipText = "#f8fafc"

scrollbarBg = "#1e293b"
scrollbarThumb = "#475569"
scrollbarHover = "#64748b"

[css]
content = """
/* Optional custom CSS */
"""
```

#### Uploading the File
1. Save your TOML file
2. Go to **Management → Theme Management**
3. Click **Import Theme** or drag file to upload area
4. File is validated and imported
5. Theme appears in theme selector

### Method 3: Duplicate and Edit

#### Duplicating a Theme
1. Select an existing theme you like
2. Click **Duplicate Theme** button
3. New theme created with "_copy" suffix
4. Edit the duplicated theme

#### Editing Process
1. Click **Edit** button on custom theme
2. Modify any colors or settings
3. Preview changes in real-time
4. Click **Update Theme** to save
5. Changes apply immediately

#### Version Control
- Increment version number when editing
- Add changelog in description
- Export backup before major changes

## Design Guidelines

### Dark Theme Best Practices

#### Background Hierarchy
```
Base Background:    #0f172a - #111827 (deepest)
Card Background:    #1e293b - #1f2937 (elevated)
Nested Elements:    #334155 - #374151 (highest)
Hover States:       #475569 - #4b5563 (interaction)
```

#### Text Contrast
- Primary text: `#f8fafc` - `#ffffff` (15:1 ratio minimum)
- Secondary text: `#cbd5e1` - `#d1d5db` (7:1 ratio minimum)
- Muted text: `#94a3b8` - `#9ca3af` (4.5:1 ratio minimum)
- Never use pure black (`#000000`) for backgrounds

#### Border Visibility
- Use `#334155` - `#374151` for visible borders
- Use `#1e293b` - `#1f2937` for subtle borders
- Add 1px width for clarity
- Consider rgba for semi-transparent borders

### Light Theme Best Practices

#### Background Hierarchy
```
Base Background:    #ffffff - #fafafa (brightest)
Card Background:    #f9fafb - #f3f4f6 (subtle depth)
Nested Elements:    #e5e7eb - #d1d5db (deeper)
Hover States:       #d1d5db - #9ca3af (interaction)
```

#### Text Contrast
- Primary text: `#111827` - `#0f172a` (21:1 ratio)
- Secondary text: `#4b5563` - `#6b7280` (7:1 ratio)
- Muted text: `#9ca3af` - `#94a3b8` (4.5:1 ratio)
- Never use pure white (`#ffffff`) for text

#### Border Definition
- Use `#d1d5db` - `#e5e7eb` for main borders
- Use `#f3f4f6` - `#f9fafb` for subtle separation
- Consider shadows for additional depth

### Accessibility Requirements

#### WCAG 2.1 Compliance
| Element Type | Minimum Contrast | Recommended |
|--------------|------------------|-------------|
| Normal text (< 18pt) | 4.5:1 | 7:1 |
| Large text (≥ 18pt) | 3:1 | 4.5:1 |
| UI components | 3:1 | 4.5:1 |
| Placeholder text | 3:1 | 4.5:1 |
| Focus indicators | 3:1 | 4.5:1 |

#### Testing Your Theme
1. **Contrast Checker**: Use online tools to verify ratios
2. **Color Blindness**: Test with simulators
3. **Different Displays**: Check on various monitors
4. **Brightness Levels**: Test at different screen brightnesses
5. **Print Preview**: Ensure printability if needed

## Advanced Customization

### Custom CSS Implementation

#### Adding Custom Styles
Extend your theme beyond colors with custom CSS:

```toml
[css]
content = """
/* Global Style Overrides */
:root {
  --border-radius: 12px;
  --transition-speed: 0.2s;
  --shadow-sm: 0 1px 2px rgba(0,0,0,0.05);
  --shadow-md: 0 4px 6px rgba(0,0,0,0.1);
  --shadow-lg: 0 10px 15px rgba(0,0,0,0.15);
}

/* Card Styling */
.themed-card {
  border-radius: var(--border-radius);
  backdrop-filter: blur(10px);
  transition: all var(--transition-speed) ease;
  box-shadow: var(--shadow-sm);
}

.themed-card:hover {
  transform: translateY(-2px);
  box-shadow: var(--shadow-md);
}

/* Button Enhancements */
button {
  border-radius: 8px;
  font-weight: 500;
  transition: all var(--transition-speed) ease;
  text-transform: uppercase;
  letter-spacing: 0.025em;
}

button:hover {
  transform: scale(1.02);
  box-shadow: var(--shadow-md);
}

button:active {
  transform: scale(0.98);
}

/* Input Field Styling */
input, select, textarea {
  border-radius: 6px;
  transition: all var(--transition-speed) ease;
}

input:focus, select:focus, textarea:focus {
  box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
}

/* Stat Card Animations */
.stat-card {
  position: relative;
  overflow: hidden;
}

.stat-card::before {
  content: '';
  position: absolute;
  top: 0;
  left: -100%;
  width: 100%;
  height: 100%;
  background: linear-gradient(
    90deg,
    transparent,
    rgba(255,255,255,0.05),
    transparent
  );
  transition: left 0.5s ease;
}

.stat-card:hover::before {
  left: 100%;
}

/* Chart Enhancements */
.chart-container {
  border-radius: var(--border-radius);
  padding: 1rem;
  background: rgba(0,0,0,0.2);
}

/* Table Styling */
table {
  border-radius: var(--border-radius);
  overflow: hidden;
}

tbody tr {
  transition: background-color var(--transition-speed) ease;
}

tbody tr:hover {
  background-color: rgba(255,255,255,0.02);
}

/* Modal Improvements */
.modal-content {
  border-radius: var(--border-radius);
  animation: slideIn 0.3s ease;
}

@keyframes slideIn {
  from {
    opacity: 0;
    transform: translateY(-20px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

/* Tooltip Styling */
.tooltip {
  border-radius: 6px;
  backdrop-filter: blur(10px);
  box-shadow: var(--shadow-lg);
}

/* Scrollbar Customization */
::-webkit-scrollbar {
  width: 10px;
  height: 10px;
}

::-webkit-scrollbar-track {
  background: var(--theme-bg-secondary);
  border-radius: 5px;
}

::-webkit-scrollbar-thumb {
  background: var(--theme-border-primary);
  border-radius: 5px;
  transition: background var(--transition-speed) ease;
}

::-webkit-scrollbar-thumb:hover {
  background: var(--theme-primary);
}

/* Loading Animations */
.loading-spinner {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* Responsive Adjustments */
@media (max-width: 768px) {
  :root {
    --border-radius: 8px;
  }

  .themed-card {
    border-radius: var(--border-radius);
  }
}
"""
```

### CSS Variable Reference

All theme colors are available as CSS variables for use in custom styles:

#### Color Variables
```css
/* Core Colors */
var(--theme-primary)
var(--theme-secondary)
var(--theme-accent)

/* Backgrounds */
var(--theme-bg-primary)
var(--theme-bg-secondary)
var(--theme-bg-tertiary)
var(--theme-bg-hover)

/* Text */
var(--theme-text-primary)
var(--theme-text-secondary)
var(--theme-text-muted)
var(--theme-text-accent)

/* Borders */
var(--theme-border-primary)
var(--theme-border-secondary)
var(--theme-border-focus)

/* Status Colors */
var(--theme-success)
var(--theme-warning)
var(--theme-error)
var(--theme-info)

/* Service Colors */
var(--theme-steam)
var(--theme-epic)
var(--theme-origin)
var(--theme-blizzard)
```

#### Using Variables in Custom CSS
```css
/* Example: Custom gradient button */
.custom-button {
  background: linear-gradient(
    135deg,
    var(--theme-primary),
    var(--theme-secondary)
  );
  color: var(--theme-text-primary);
  border: 1px solid var(--theme-border-primary);
}

/* Example: Glassmorphism effect */
.glass-panel {
  background: rgba(var(--theme-bg-secondary-rgb), 0.7);
  backdrop-filter: blur(10px);
  border: 1px solid rgba(var(--theme-border-primary-rgb), 0.2);
}
```

## Theme Distribution

### Exporting Themes

#### Export Process
1. Navigate to **Theme Management**
2. Find your theme in the list
3. Click the **Export** button (download icon)
4. Theme downloads as `themename.toml`
5. File includes all colors and custom CSS

#### Sharing Options
- **GitHub**: Create a theme repository
- **Discord**: Share in LANCache community
- **Direct**: Send file to other users
- **Gist**: Create GitHub Gist for easy sharing

### Importing Themes

#### Import Methods
1. **Drag and Drop**:
   - Drag `.toml` file onto upload area
   - Theme validates and imports automatically

2. **File Browser**:
   - Click **Import Theme** button
   - Select `.toml` file from system
   - Click Open to import

3. **URL Import** (if supported):
   - Paste theme URL
   - System fetches and imports

#### Import Validation
- File must be valid TOML format
- Required fields are checked
- Color values are validated
- Duplicate IDs are handled
- Invalid themes show error messages

### Theme Repository

Consider creating a community theme repository:

```markdown
# LANCache Manager Themes

## Dark Themes
- [Midnight Blue](themes/midnight-blue.toml)
- [Neon Glow](themes/neon-glow.toml)
- [Deep Ocean](themes/deep-ocean.toml)

## Light Themes
- [Clean White](themes/clean-white.toml)
- [Soft Pastel](themes/soft-pastel.toml)
- [Paper](themes/paper.toml)

## High Contrast
- [OLED Black](themes/oled-black.toml)
- [High Contrast Light](themes/high-contrast-light.toml)
```

## Troubleshooting

### Common Issues and Solutions

#### Theme Not Applying
**Symptoms**: Theme selected but colors don't change

**Solutions**:
1. **Force Refresh**: Press `Ctrl+F5` (Windows) or `Cmd+Shift+R` (Mac)
2. **Clear Cache**:
   - Open browser DevTools (F12)
   - Go to Application/Storage tab
   - Clear Local Storage for the site
3. **Check Console**:
   - Open DevTools Console
   - Look for error messages
   - Report errors with screenshot
4. **Validate TOML**:
   - Use online TOML validator
   - Check for syntax errors
   - Ensure all required fields present

#### Colors Look Wrong
**Symptoms**: Colors appear different than expected

**Solutions**:
1. **Theme Type**:
   - Verify `isDark` setting matches theme
   - Dark themes need `isDark = true`
   - Light themes need `isDark = false`
2. **Color Format**:
   - Use 6-digit hex: `#ffffff`
   - Avoid 3-digit hex: `#fff`
   - No alpha channel: `#ffffff80` (not supported)
3. **Monitor Settings**:
   - Check display color profile
   - Test on different monitors
   - Adjust monitor brightness/contrast
4. **Browser Issues**:
   - Test in different browser
   - Disable browser extensions
   - Check for forced dark mode

#### Can't Edit System Themes
**Symptoms**: Edit button disabled for default themes

**Solutions**:
1. **Expected Behavior**: System themes are read-only
2. **Workaround**:
   - Export the system theme
   - Rename in the TOML file
   - Import as new custom theme
   - Edit the custom version

#### Theme Import Fails
**Symptoms**: Error when importing TOML file

**Solutions**:
1. **File Validation**:
   ```bash
   # Check TOML syntax
   python -m pip install toml
   python -c "import toml; toml.load('theme.toml')"
   ```
2. **Required Fields**:
   - Ensure [meta] section exists
   - Check all color fields present
   - Verify field names match exactly
3. **Encoding Issues**:
   - Save file as UTF-8
   - Remove BOM if present
   - Use LF line endings

#### Custom CSS Not Working
**Symptoms**: CSS rules don't apply

**Solutions**:
1. **Specificity**:
   - Use more specific selectors
   - Add `!important` if needed
   - Check cascade order
2. **Syntax**:
   - Validate CSS syntax
   - Check for unclosed brackets
   - Verify property names
3. **Escaping**:
   ```toml
   [css]
   content = """
   /* Use triple quotes for multi-line CSS */
   .class {
     property: value;
   }
   """
   ```

#### Performance Issues
**Symptoms**: UI slow after applying theme

**Solutions**:
1. **Reduce Animations**:
   - Limit transition effects
   - Avoid complex animations
   - Use `will-change` sparingly
2. **Optimize CSS**:
   - Remove unused rules
   - Combine similar selectors
   - Minimize custom CSS size
3. **Browser Performance**:
   - Update browser
   - Disable hardware acceleration
   - Close other tabs

### Getting Help

#### Debug Information
When reporting issues, include:
1. Browser and version
2. Theme TOML file
3. Console error messages
4. Screenshots of issue
5. Steps to reproduce

#### Support Channels
- **GitHub Issues**: Bug reports and features
- **Discord**: Community help
- **Wiki**: Documentation updates

## Example Themes

### Complete Theme Examples

#### Cyberpunk Neon
```toml
[meta]
name = "Cyberpunk Neon"
id = "cyberpunk-neon"
description = "Vibrant neon colors with dark backgrounds"
author = "LANCache Community"
version = "1.0.0"
isDark = true

[colors]
# Core Colors
primaryColor = "#00ffff"      # Cyan
secondaryColor = "#ff00ff"    # Magenta
accentColor = "#ffff00"       # Yellow

# Backgrounds
bgPrimary = "#0a0a0a"
bgSecondary = "#1a0a1a"
bgTertiary = "#2a1a2a"
bgHover = "#3a2a3a"

# Text
textPrimary = "#ffffff"
textSecondary = "#00ffff"
textMuted = "#8a8a8a"
textAccent = "#ff00ff"

# Borders
borderPrimary = "#00ffff"
borderSecondary = "#ff00ff"
borderFocus = "#ffff00"

# Status
success = "#00ff00"
successBg = "#002200"
successText = "#00ff00"

warning = "#ffff00"
warningBg = "#222200"
warningText = "#ffff00"

error = "#ff0000"
errorBg = "#220000"
errorText = "#ff0000"

[css]
content = """
/* Neon glow effects */
.themed-card {
  box-shadow:
    0 0 20px rgba(0, 255, 255, 0.3),
    inset 0 0 20px rgba(255, 0, 255, 0.1);
}

button:hover {
  box-shadow: 0 0 30px rgba(255, 255, 0, 0.5);
  text-shadow: 0 0 10px currentColor;
}
"""
```

#### Minimalist Light
```toml
[meta]
name = "Minimalist Light"
id = "minimalist-light"
description = "Clean and simple light theme"
author = "LANCache Community"
version = "1.0.0"
isDark = false

[colors]
# Core Colors
primaryColor = "#000000"
secondaryColor = "#666666"
accentColor = "#0066cc"

# Backgrounds
bgPrimary = "#ffffff"
bgSecondary = "#fafafa"
bgTertiary = "#f5f5f5"
bgHover = "#eeeeee"

# Text
textPrimary = "#000000"
textSecondary = "#333333"
textMuted = "#666666"
textAccent = "#0066cc"

# Borders
borderPrimary = "#dddddd"
borderSecondary = "#eeeeee"
borderFocus = "#000000"

# Status
success = "#00aa00"
successBg = "#f0fff0"
successText = "#006600"

warning = "#ff9900"
warningBg = "#fff9f0"
warningText = "#cc6600"

error = "#cc0000"
errorBg = "#fff0f0"
errorText = "#990000"

[css]
content = """
/* Minimalist styling */
* {
  border-radius: 0 !important;
}

.themed-card {
  border: 1px solid var(--theme-border-primary);
  box-shadow: none;
}

button {
  text-transform: none;
  font-weight: normal;
  letter-spacing: normal;
}
"""
```

#### Dracula Pro
```toml
[meta]
name = "Dracula Pro"
id = "dracula-pro"
description = "Popular Dracula color scheme"
author = "LANCache Community"
version = "1.0.0"
isDark = true

[colors]
# Core Colors
primaryColor = "#bd93f9"      # Purple
secondaryColor = "#ff79c6"    # Pink
accentColor = "#8be9fd"       # Cyan

# Backgrounds
bgPrimary = "#282a36"
bgSecondary = "#44475a"
bgTertiary = "#6272a4"
bgHover = "#ff79c6"

# Text
textPrimary = "#f8f8f2"
textSecondary = "#bd93f9"
textMuted = "#6272a4"
textAccent = "#8be9fd"

# Borders
borderPrimary = "#44475a"
borderSecondary = "#6272a4"
borderFocus = "#ff79c6"

# Status
success = "#50fa7b"
successBg = "#282a36"
successText = "#50fa7b"

warning = "#f1fa8c"
warningBg = "#282a36"
warningText = "#f1fa8c"

error = "#ff5555"
errorBg = "#282a36"
errorText = "#ff5555"

# Services
steamColor = "#8be9fd"
epicColor = "#bd93f9"
originColor = "#ffb86c"
blizzardColor = "#ff79c6"
```

## Best Practices

### Theme Development Workflow

1. **Planning Phase**
   - Define color palette
   - Choose base theme (dark/light)
   - Consider target audience
   - Plan accessibility compliance

2. **Development Phase**
   - Start with a template
   - Test incrementally
   - Use version control
   - Document changes

3. **Testing Phase**
   - Test all UI components
   - Verify contrast ratios
   - Check responsive design
   - Validate across browsers

4. **Release Phase**
   - Update version number
   - Write changelog
   - Export and backup
   - Share with community

### Color Selection Guidelines

#### Color Harmony
- **Monochromatic**: Use shades of single color
- **Analogous**: Use adjacent colors on color wheel
- **Complementary**: Use opposite colors for contrast
- **Triadic**: Use three evenly spaced colors

#### Psychological Impact
- **Blue**: Trust, stability, professionalism
- **Green**: Growth, success, harmony
- **Purple**: Creativity, luxury, mystery
- **Orange**: Energy, enthusiasm, warmth
- **Red**: Urgency, importance, attention

### Performance Optimization

1. **CSS Efficiency**
   - Minimize custom CSS size (<10KB)
   - Use CSS variables over hardcoded values
   - Avoid expensive selectors
   - Limit animation complexity

2. **Color Optimization**
   - Use consistent color palette
   - Reuse colors across elements
   - Limit total unique colors
   - Consider color blindness

3. **Testing Performance**
   - Monitor render time
   - Check memory usage
   - Test on lower-end devices
   - Verify smooth scrolling

### Version Control

```toml
[meta]
# Semantic Versioning
version = "MAJOR.MINOR.PATCH"
# MAJOR: Breaking changes
# MINOR: New features
# PATCH: Bug fixes

# Example changelog in description
description = """
A beautiful dark theme with cyan accents.

Changelog:
v2.0.0 - Complete redesign with new color system
v1.2.0 - Added custom CSS animations
v1.1.3 - Fixed contrast issues in light mode
v1.1.2 - Updated service colors
v1.1.1 - Fixed border visibility
v1.1.0 - Added glassmorphism effects
v1.0.0 - Initial release
"""
```

## Resources

### Tools and Utilities
- **Color Palette Generators**:
  - [Coolors.co](https://coolors.co)
  - [Adobe Color](https://color.adobe.com)
  - [Paletton](https://paletton.com)

- **Contrast Checkers**:
  - [WebAIM Contrast Checker](https://webaim.org/resources/contrastchecker/)
  - [Colorable](https://colorable.jxnblk.com)
  - [Stark](https://www.getstark.co)

- **TOML Validators**:
  - [TOML Lint](https://www.toml-lint.com)
  - [TOML Online Parser](https://toml-parser.com)

- **CSS Tools**:
  - [CSS Gradient Generator](https://cssgradient.io)
  - [Cubic Bezier](https://cubic-bezier.com)
  - [CSS Box Shadow Generator](https://cssgenerator.org/box-shadow-css-generator.html)

### Community Resources
- **Theme Repository**: Share and discover themes
- **Discord Channel**: Get help and feedback
- **GitHub Issues**: Report bugs and request features
- **Wiki Updates**: Contribute to documentation

### Learning Resources
- **Color Theory**: Understanding color relationships
- **Accessibility Guidelines**: WCAG 2.1 standards
- **CSS Best Practices**: Modern CSS techniques
- **TOML Specification**: File format documentation

## Need Help?

If you encounter issues or need assistance:

1. **Check this guide** for troubleshooting steps
2. **Search existing issues** on GitHub
3. **Ask in Discord** for community help
4. **Create an issue** with detailed information
5. **Contribute** improvements to this documentation
