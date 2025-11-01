# What's New

## Image Caching for Downloads Tab

The Downloads Tab now caches game images locally for faster loading.

### What Changed

**Before:** Game images were downloaded from Steam servers on every page load and scroll.

**Now:** Images are downloaded once and cached locally. Subsequent page loads serve images from the cache.

### Improvements

**Faster Loading** - Game headers appear immediately when scrolling through downloads.

**Reduced Bandwidth** - Each image is downloaded once instead of repeatedly.

**Automatic Updates** - When Steam updates game artwork, the cache automatically refreshes.

**Smart Error Handling** - Games without images are remembered to avoid repeated failed lookups.

**Storage Location:** Cached images are stored in `data/cached-img`. You can delete this folder to clear the cache.

---

## Theme Management Updates

Theme management has been expanded with new features for customization and sharing.

### Community Themes

Browse and install themes created by the community directly from the UI. Themes can be previewed before installation.

**Auto-Updates:** Themes can receive updates automatically when enabled.

**Live Previews:** Hover over theme cards to preview the theme before installing.

### Theme Editor Updates

**Improved Interface** - Redesigned controls for easier color and accent customization.

**Real-Time Preview** - Changes are visible immediately as you adjust settings.

**Export & Share** - Themes can be exported as TOML files for sharing.

**Extended Options** - Additional customization for service icons, borders, and hover states.

**Startup Updates:** Installed themes automatically check for updates when the application starts.

---

## Optional Memory Management Controls

Added optional garbage collection management for advanced users who need control over memory usage.

### Overview

A new **Optimizations** section is available in the Management tab (disabled by default) that provides manual control over .NET garbage collection.

### Use Cases

**Recommended for:**
- Low-memory systems (less than 4GB RAM)
- Environments where memory usage needs to be tightly controlled
- Systems experiencing memory growth over time

**Not recommended for:**
- Systems with adequate RAM (8GB or more)
- Situations where performance is prioritized over memory usage
- Most standard deployments (default .NET memory management is sufficient)

### Enabling the Feature

Add to your docker-compose environment variables:

```yaml
- Optimizations__EnableGarbageCollectionManagement=true
```

Once enabled, the following controls are available:
- Garbage collection aggressiveness settings (disabled to every second)
- Memory threshold configuration (2GB - 16GB)
- Manual garbage collection trigger for testing

This is an advanced feature intended for specific use cases.

---

## Additional Improvements

**Improved Shutdown Process:** The application now shuts down more gracefully, properly closing connections and cleaning up resources.

**Log Cleanup:** Removed excessive log messages to improve log readability.
