## What's New

### Prefill Network Auto-Detection
Fixed HTTP 400 errors during prefill downloads. The prefill container now automatically detects your lancache-dns configuration.

- If lancache-dns uses host networking, prefill containers use host networking
- If lancache-dns uses bridge networking, its IP is used for DNS resolution
- Manual override available via `Prefill__NetworkMode` and `Prefill__LancacheDnsIp` environment variables
- Logs show which network configuration was detected

### Download Size Estimate
Prefill confirmation dialog now shows estimated download size before starting.

- Fetches size from Steam depot metadata via the daemon
- Shows loading spinner while calculating
- Displays formatted size (GB/MB) in the confirmation dialog
- Button disabled until size is calculated

### Prefill Confirmation Dialogs
All prefill operations now show a confirmation dialog before starting.

- "Prefill Selected" shows game count and estimated download size
- "Prefill All" warns about potentially hundreds of gigabytes
- "Top 50" warns about several hundred gigabytes of data

### UI Cleanup
- Removed redundant "View Selected" button from prefill panel (game selection modal already shows this)

### Documentation
- Updated README with prefill network configuration section
- Added troubleshooting guide for HTTP 400 errors during prefill
- Updated docker-compose.yml with new prefill environment variables

Thanks for using LANCache Manager!
