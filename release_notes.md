## What's New

### Cache Size Configuration Detection

The dashboard now reads your configured `CACHE_DISK_SIZE` from the lancache-monolithic container, showing your actual cache limit instead of the full drive capacity.

**How it works:**
- Automatically reads `CACHE_DISK_SIZE` environment variable from the running lancache-monolithic Docker container
- Falls back to reading from `.env` file if container inspection fails
- Supports all common size formats: `4000g`, `500G`, `2t`, `1.5T`, etc.

**Dashboard changes:**
- "Total Cache" stat card now shows your configured cache limit (e.g., 4 TB)
- Subtitle shows actual drive capacity (e.g., "Drive: 78.9 TB")
- Cache Growth widget handles temporary over-limit scenarios during nginx cache eviction

**Log output:**
```
info: LancacheManager.Core.Services.CacheManagementService[0]
      Configured cache size: 4000g (3.91 TB) from container lancache-monolithic-1
```

### Code Improvements

- Added shared `FormattingUtils.FormatBytes()` utility to reduce code duplication
- Improved Docker container detection with priority-based selection (image → name → env var scan)
- Fixed locale-dependent parsing for decimal cache sizes (e.g., `1.5T` now works on all systems)
