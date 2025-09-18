# Community Themes

A curated collection of beautiful themes for LanCache Manager. Customize your cache monitoring interface with themes created by the community.

## Featured Themes

Browse and download themes created by the community. Each theme is carefully crafted to provide optimal readability and visual appeal.

### Available Themes
- Built-in Themes: 20+ themes included with the application
- Community Themes: Additional themes created by users
- Custom Themes: Create your own using the Theme Editor

## Installing Themes

### Method 1: One-Click Install (Recommended)
1. Browse themes in Management → Theme Management
2. Preview themes instantly
3. Click "Apply" to activate

### Method 2: Import Custom Themes
1. Download a `.toml` theme file from this repository
2. Go to Management → Theme Management
3. Click "Import Theme"
4. Select the downloaded `.toml` file
5. The theme will be added to your collection
6. Click "Apply" to activate it

## Creating Your Own Theme

### Using the Visual Theme Editor
1. Go to Management → Theme Management
2. Click "Create New Theme"
3. Set theme metadata:
   - Name: Your theme's display name
   - ID: Unique identifier (no spaces)
   - Mode: Dark or Light theme
4. Customize colors using the color picker interface
5. Preview changes in real-time as you edit
6. Save theme when satisfied
7. Export to share with others

### Manual Theme Creation
Create a `.toml` file with this structure:

```toml
[meta]
name = "My Awesome Theme"
id = "my-awesome-theme"
isDark = true

[colors]
# Primary colors
primaryColor = "#3b82f6"
secondaryColor = "#8b5cf6"
accentColor = "#10b981"

# Background layers
bgPrimary = "#0f172a"
bgSecondary = "#1e293b"
bgTertiary = "#334155"
bgHover = "#475569"

# Text colors
textPrimary = "#f8fafc"
textSecondary = "#cbd5e1"
textMuted = "#94a3b8"
textAccent = "#06b6d4"

# ... (see wiki/THEMING.MD for complete color list)

[css]
content = """
/* Optional custom CSS */
.themed-card {
  box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
}
"""
```

See the [Theming Guide](../wiki/THEMING.MD) for complete documentation and color reference.

## Contributing Themes

Share your themes with the community!

### Submission Guidelines
1. Create a high-quality theme with:
   - Proper contrast ratios for accessibility
   - Consistent color scheme
   - Complete color definitions
   - Unique theme ID

2. Test thoroughly across:
   - Light and dark environments
   - Different screen sizes
   - All application tabs

3. Submit via Pull Request including:
   - Theme `.toml` file
   - Brief description of the theme
   - Screenshot (optional but recommended)
   - Any special notes about the theme

### File Naming Convention
- Use kebab-case: `my-theme-name.toml`
- Keep names descriptive but concise
- Avoid special characters

### Theme Quality Standards
Required:
- All color definitions present
- Valid TOML syntax
- Unique theme ID
- Proper accessibility contrast ratios

Recommended:
- Descriptive theme name
- Consistent color palette
- Custom CSS enhancements
- Responsive design considerations

## Theme Categories

### Dark Themes
- High contrast for low-light environments
- Easy on the eyes during long monitoring sessions
- Popular for gaming setups

### Light Themes
- Clean and professional appearance
- Great for office environments
- High readability in bright conditions

### Colorful Themes
- Vibrant and energetic designs
- Gaming-inspired color schemes
- Creative and unique palettes

### Professional Themes
- Corporate-friendly designs
- Subdued, business-appropriate colors
- Clean and minimal aesthetics

## Mobile Compatibility

All themes are tested for mobile responsiveness. Ensure your custom themes work well on:
- Smartphones (portrait/landscape)
- Tablets
- Touch interfaces
- Various screen densities

## Troubleshooting

**Theme not applying**: Clear browser cache (Ctrl+F5)
**Colors look wrong**: Verify theme mode (dark/light) matches your preference
**Import fails**: Check TOML syntax and ensure all required colors are defined
**Performance issues**: Reduce complex CSS animations

## Tips for Theme Creators

- **Start with an existing theme** as a base template
- **Use online color palette generators** for harmonious colors
- **Test accessibility** with tools like WebAIM's contrast checker
- **Consider different use cases**: 24/7 monitoring, presentation mode, mobile use
- **Keep it simple**: Clean designs age better than complex ones

---

<div align="center">

Ready to create your own theme?

[Read the Theming Guide](../wiki/THEMING.MD) • [Open Theme Editor](http://localhost:8080)

</div>