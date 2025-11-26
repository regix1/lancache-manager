## What's New

### GitHub Depot Mapping Improvements

GitHub mode now does a full replace instead of incremental updates. This means your depot mappings will always match what's on GitHub exactly - no more stale or orphaned mappings hanging around.

Also added better progress updates during the import process so you actually know what's happening.

### V2 API Availability Check

Steam account login now checks if the V2 API is available first. If it's not, the option is disabled with a helpful message explaining why. Your V1 API key still works fine for playtest/restricted games.

### Default Docker Permissions

Changed the default PUID/PGID from 1006 to 1000. This matches the standard first user on most Linux systems so fewer permission headaches out of the box.

## Bug Fixes

- Steam logout now actually persists across app restarts (was re-logging in automatically before)
- Fixed depot mapping notifications getting stuck at 100% when they were actually done
- Progress updates during post-processing phases (saving to JSON, importing to database) so you're not staring at a stuck progress bar
- Cleaned up stale localStorage state when SignalR disconnects mid-operation

## Under the Hood

Removed auto-sync between the manual and automatic depot source dropdowns. They're independent now - you can run a different source manually than what's scheduled.

---

Thanks for using Lancache Manager!
