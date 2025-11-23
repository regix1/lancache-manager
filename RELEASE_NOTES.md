# What's New

## DeveLanCacheUI Data Import
Added ability to import historical download data from [DeveLanCacheUI_Backend](https://github.com/DeveloperJosh/DeveLanCacheUI_Backend) directly through the web interface.

**What's New:**
- New "Import from DeveLanCacheUI_Backend" card in the Management tab
- Built-in file browser to easily locate your DeveLanCacheUI database
- **Automatic database backup** before every import - your data is safe even if something goes wrong
- Two import modes:
  - **Append-Only (default):** Adds new records, skips duplicates - safe for repeated imports
  - **Merge/Sync:** Updates existing records with new data - useful for syncing changes
- Proper timezone conversion - timestamps are converted from UTC to your configured timezone
- Progress tracking shows exactly what was imported, skipped, or had errors

**Why This Matters:** If you're migrating from DeveLanCacheUI, you can now bring all your historical download data with you. The automatic backup means you can safely experiment with imports without risk of data loss.

**How It Works:**
1. Navigate to Management tab → Import from DeveLanCacheUI_Backend
2. Use the file browser to select your `lancache.db` database file
3. Click "Validate Connection" to verify the database and see record count
4. Click "Import Data" to start the migration
5. Review the backup location and import statistics when complete

**Backup Location:** Backups are saved as `LancacheManager.backup.YYYYMMDD_HHMMSS.db` in your `/data` directory. You can restore from backup by simply replacing the main database file.

**For Docker Users:** The data_migrator binary is automatically included in all Docker builds (amd64, arm64). No additional setup required.

# Migration Notes

This is a minor update with new features - no breaking changes.

**If you're migrating from DeveLanCacheUI:**
1. Your old database remains untouched - LancacheManager creates a backup before importing
2. You can import multiple times safely - duplicates are automatically detected and skipped
3. All timestamps are properly converted to your configured timezone
4. The import can be run repeatedly to sync new data as it arrives
