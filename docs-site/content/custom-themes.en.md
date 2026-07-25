# Custom Themes { #custom-themes }

Open **Management → Theme → Theme Management** to build a theme from scratch with a live preview of every color group, browse and install community themes, or import and export themes as TOML.

Themes live in `/data/themes/`. Here's the minimum format:

```toml
[meta]
name = "My Theme"
id = "my-theme"
isDark = true
version = "1.0.0"
author = "Your Name"

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
textPrimary = "#ffffff"
```

-----
