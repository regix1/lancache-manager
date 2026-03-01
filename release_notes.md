## What's New

<details>
<summary><strong>Epic Games Prefill</strong></summary>

You can now prefill Epic Games alongside Steam. A new Epic prefill daemon handles authentication through Epic's OAuth login, lets you browse your owned games, check what's already cached, and schedule downloads with configurable thread counts.

The prefill tab now opens to a home page where you pick between Steam and Epic before starting a session. Each service runs independently with its own connection, so you can have both active at the same time. Guest users who only have access to one service skip the home page and go straight to their session.

</details>

<details>
<summary><strong>Per-Service Guest Permissions</strong></summary>

Admins can now manage guest prefill access separately for Steam and Epic. Each service has its own enable toggle, session duration, and max thread count. The guest configuration page was reorganized into cleaner sections for security, per-service prefill settings, and appearance preferences.

The active sessions editor shows permission controls for each service individually, so you can grant a guest access to Steam prefill without giving them Epic access or vice versa.

</details>

<details>
<summary><strong>Corruption Detection</strong></summary>

Corruption detection now has a configurable sensitivity threshold with three presets: High, Medium, and Low. The descriptions update to reflect your chosen threshold so you can see exactly how many misses trigger a flag. You can also toggle between "Cache + Logs" and "Logs Only" detection modes, with "Cache + Logs" as the default.

</details>

<details>
<summary><strong>Directory Permission Monitoring</strong></summary>

The system now checks cache and log directory permissions every 30 seconds in the background. When permissions change, the UI updates automatically without needing a page reload. A "Recheck Permissions" button was added to the Storage section, and error messages now clearly distinguish between directories that are read-only versus ones that don't exist at all.

</details>

<details>
<summary><strong>Code Quality</strong></summary>

The entire frontend went through a full lint and formatting pass. Loose types were replaced with proper ones, unused code was cleaned up, and consistent formatting was enforced across all files. A pre-commit hook now runs automatically to catch issues before they're committed, and dead code detection was added to the build tooling.

</details>

<details>
<summary><strong>UI Improvements</strong></summary>

Dropdown menus now float as rounded panels with a small gap below the trigger instead of appearing flush against it. Settings panel accent colors now follow your active theme instead of using hardcoded colors. Scrollbar padding no longer takes up space when the content doesn't need to scroll.

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

Fixed database reset accidentally clearing game detections and service detections together instead of handling them separately. Prefill history can now be cleared independently from the database management page. The Epic Prefill Daemon project now shows up in the GitHub Projects dropdown.

</details>

---

Thanks for using LANCache Manager!
