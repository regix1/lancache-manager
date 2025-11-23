# Bug Fixes

## Docker Rust Binary Path Fix
Fixed a critical issue where Rust binaries could not be found in Docker deployments.

**The Problem**: When running in Docker, all Rust-based operations (log processing, cache management, game detection, corruption detection) failed with "binary not found" errors. The RustProcessHelper was manually constructing paths that looked in the wrong location - searching for `/rust-processor/target/release/log_manager` when Docker places binaries at `/app/rust-processor/log_manager`.

**What's Fixed**: RustProcessHelper now uses the IPathResolver interface to correctly resolve Rust binary paths for both Docker and development environments. All Rust operations now work properly in containerized deployments.

**Affected Operations**:
- Log processing (log_manager)
- Corruption detection (corruption_manager)
- Database reset operations (database_reset)
- Cache clearing (cache_cleaner)
- Game cache detection (game_cache_detector)
- Game cache removal (game_cache_remover)
- Service removal (service_remover)

If you experienced "binary not found" errors in Docker, this hotfix resolves the issue.
