# Contributing Translations { #contributing-translations }

LANCache Manager supports internationalization (i18n) and welcomes community translations. Every UI string is already externalized - there's nothing to refactor before you can translate.

### How to contribute

1. **Fork the repository** on GitHub.
2. Open `Web/src/i18n/locales/`.
3. Copy `en.json` to a file named for your language (e.g., `de.json`, `fr.json`, `es.json`, `pt-BR.json`).
4. Translate the values. Leave the keys alone.
5. Submit a **pull request**.

### File layout

```
Web/src/i18n/locales/
├── en.json          ← English (reference)
├── de.json          ← German (your contribution)
├── fr.json          ← French (your contribution)
└── ...
```

### Guidelines

- **Don't change JSON keys** - only translate the string values.
- **Preserve placeholders** - keep `{{variable}}` intact (e.g., `{{name}}`).
- **Preserve formatting** - leave HTML tags like `<strong>` alone.
- **Test locally** - run the app and verify your translations render correctly.

### Example

```json
// en.json
{
  "dashboard": {
    "title": "Dashboard",
    "recentDownloads": "Recent Downloads",
    "totalCache": "Total Cache: {{size}}"
  }
}

// de.json
{
  "dashboard": {
    "title": "Übersicht",
    "recentDownloads": "Letzte Downloads",
    "totalCache": "Gesamter Cache: {{size}}"
  }
}
```

-----
