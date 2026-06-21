## What's New

<details>
<summary><strong>Battle.net and Riot prefill</strong></summary>

Two new prefill services join Steam and Epic, and both run anonymously with no account login required. Battle.net pulls public content from the Blizzard TACT catalog (Diablo, Overwatch, World of Warcraft, StarCraft, Hearthstone, Call of Duty, and more), and Riot covers League of Legends, VALORANT, and Legends of Runeterra. Each service has its own daemon, session cards, live progress, and guest permission and duration settings, and admins can ban a user straight from a session. Downloads from both services are resolved to real game names as they arrive instead of showing up as raw CDN traffic.

</details>

<details>
<summary><strong>Per-game detection and removal for Blizzard and Riot</strong></summary>

Blizzard and Riot games now appear as individual rows in Game Cache Detection instead of only at the service level. Each game shows its own cached size and a Blizzard or Riot service badge, and you can remove a single game's cache without touching the rest of the service, mirroring the Steam and Epic removal flows. A new path keyed on service plus game name backs both full and partial-evicted removal, so cleaning up one named game's evicted records no longer falls back to removing everything.

</details>

<details>
<summary><strong>Game artwork for every service</strong></summary>

Blizzard and Riot titles now show real banner art on the dashboard and downloads page. Banners resolve to Steam header artwork first and fall back to curated images sourced from official publisher CDNs and embedded with the app, so there is no runtime fetching and no broken thumbnails. Overwatch 2 was rebranded to Overwatch across the catalog, banners, and existing detection rows to match Blizzard's naming.

</details>

<details>
<summary><strong>Cache size accuracy</strong></summary>

Range-served games like Blizzard and Riot titles used to report only a fraction of their real cached size. Detection now walks every cache slice for a URL, the same way removal already does, so a multi-gigabyte game shows its true on-disk size on both the scanner and the downloads page. Per-game size calculation is also identity-aware now, so named games that all share an internal app id of zero no longer collapse into one shared size bucket.

</details>

<details>
<summary><strong>Eviction accuracy</strong></summary>

The eviction scanner no longer false-evicts real cached files. Its probe key now matches the nginx `$uri` cache key (including the noslice variant), content is only flagged as evicted when it was actually cached, and a service whose cache lives entirely under named Blizzard or Riot games is recognized as present instead of being shown as zero files. Detected services keep their last known counts when they are missing from a single scan, and Remove-All-Evicted runs as one backend operation with pages auto-refreshing on eviction events.

</details>

<details>
<summary><strong>Dashboard and Service Analytics redesign</strong></summary>

The dashboard went through an anti-slop design pass: forced glassmorphism is gone, focus rings and reduced-motion guards are back, KPI cards are grouped by meaning and lead with bandwidth saved and hit ratio, and the trend widgets and tables now show real empty, loading, and error states. The Service Analytics card was rebuilt with an equal-height donut, a center total, a KPI footer, and a clearer Compare view, and the Management sub-tabs and Prefill Sessions surface were refreshed to match.

</details>

<details>
<summary><strong>Operations, progress, and cancellation</strong></summary>

Cancellation now works the same way across the app, including the Rust workers, which cooperatively stop on a stdin cancel signal, fixing the cache-clear cancel deadlock and state leaks on force-kill. Every operation completes through a single exactly-once path, the progress bar was rebuilt with stateful recovery and accurate per-file reporting, fast completion is the default, and a queue keeps long operations orderly.

</details>

<details>
<summary><strong>Faster live updates</strong></summary>

Live dashboard updates are near instant again after fixes to the SignalR push debounce and live-cache invalidation. Live speed and active-download counts update immediately regardless of the refresh-rate setting, the refresh-rate control drives the live update interval as expected, and Blizzard and Riot live downloads resolve their real game names through the TACT catalog and host-keyed naming.

</details>

<details>
<summary><strong>Security and authentication</strong></summary>

The `Security__EnableAuthentication` flag now fully disables authentication, bypasses the setup wizard, and mints a real admin session so prefill hubs, live updates, and preferences keep working. The prefill session list and cache-clear endpoints are admin-gated, service and thread inputs are validated, setup temp files are written owner-only, and there is a report-only CSP and a force-secure-cookies opt-in. Image-fetch redirects are bounded, translated content renders through `Trans` instead of raw HTML, and an OpenTelemetry CVE was patched.

</details>

<details>
<summary><strong>Mobile and UI polish</strong></summary>

The Logs and Cache tab's cramped mobile button grid was replaced with a clean left-aligned wrapping toolbar while the desktop layout stays unchanged. Control clusters across the toolbars and the prefill session card were made sizing-consistent so buttons, chips, and toggles share one height and icon buttons render as squares, the prefill service cards sit on a uniform grid, and the Container Network Status header has more breathing room around its controls.

</details>

<details>
<summary><strong>Chinese translation and documentation</strong></summary>

A full Chinese (zh) locale is wired into the app, with interpolation gaps fixed and the Integrations page redesigned along the way. The README was updated to cover the Riot prefill service and the current Battle.net and Riot integrations.

</details>

<details>
<summary><strong>Performance and cleanup</strong></summary>

Rust operations are faster with unified progress reporting, and a large dead-code sweep removed roughly 2,400 lines across the C#, Rust, and frontend code without changing behavior.

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

- Real cached files being false-evicted because the probe key did not match the nginx `$uri` cache key
- Blizzard and Riot games sharing one on-disk size and collapsing into a single Game Cache Detection row
- Range-served games reporting only a fraction of their true cached size
- Named-game removal routing to the Steam endpoint with app id zero, so the Removing indicator never lit and named games were merged
- Remove-mode eviction scans spamming progress and completion notifications and stale-completing on tab switch
- Detected services disappearing from the list after a scan zeroed their cache file counts
- Stuck operation notifications, a log-removal 409 self-block, and log-removal blind spots
- The cache-clear cancel deadlock and service-local state leaks on force-kill
- Live updates lagging and live speed and active-count flicker
- `Security__EnableAuthentication` not actually disabling auth and the setup wizard not being suppressed
- Battle.net and Riot session-history platforms defaulting to Steam
- The guest prefill max-thread clamp preventing users from requesting full threads
- Mobile button and layout regressions across the Logs and Cache tab
- i18n interpolation gaps and an XSS-prone `dangerouslySetInnerHTML` path
- OpenTelemetry CVE-2026-40894

</details>

---

Thanks for using LANCache Manager!
