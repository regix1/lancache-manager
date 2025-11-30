# Release v1.8.0

## What's New

### Docker Socket Detection
The application now detects whether the Docker socket is mounted and warns you when it's not available. Log removal and corruption management require signaling nginx to reopen logs after modifications, which needs access to the Docker socket.

When the socket isn't mounted, you'll see a clear warning explaining why these features are disabled, along with the exact volume mount you need to add:

```yaml
- /var/run/docker.sock:/var/run/docker.sock:ro
```

The permissions endpoint now reports Docker socket availability alongside cache and logs directory permissions.

### Modal Layout Shift Fix
Fixed an annoying layout shift that happened when opening modals. The page no longer jumps when the scrollbar disappears - the modal now calculates and compensates for the scrollbar width automatically.

## Bug Fixes
- Fixed page content shifting when modals open/close

## Under the Hood

### Removed "Hide About Sections" Preference
Cleaned up the unused "Hide About Sections" toggle from user preferences. This setting wasn't doing much - removed from the UI, API models, and preference services.

Thanks for using Lancache Manager!
