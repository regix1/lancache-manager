# Custom Themes { #custom-themes }

Open **Management → Theme → Theme Management** to build a theme from scratch with a live preview of every color group, browse and install community themes, or import and export themes as TOML.

Themes live in `/data/themes/`. Here's the minimum format:

```toml
[meta]
name = "My Theme"
id = "my-theme"

[colors]
primaryColor = "#3b82f6"
bgPrimary = "#111827"
bgSecondary = "#1f2937"
textPrimary = "#ffffff"
```

That is the whole requirement: `meta.name`, `meta.id`, and those four colors. A theme missing any of them is rejected when it loads. Optional extras like `isDark`, `version`, and `author` can be added on top.

-----
