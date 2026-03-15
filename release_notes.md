## What's New

<details>
<summary><strong>Epic Games Integration</strong></summary>

- Cached Epic game downloads now get mapped to actual game names instead of showing as unknown depot IDs. A new mapping service connects to the Epic Games API, identifies which game each cached download belongs to, and pulls in cover art. The mapping runs on a schedule or can be triggered manually from the Epic section in the Management tab.

- You can now remove specific Epic games from the cache the same way you already could with Steam games. Select the game from the game detection list, confirm, and the associated cache files get cleaned up.

- Admins can ban specific Epic accounts from using prefill. Prefill sessions now track which platform they belong to, so you can see at a glance whether a session is Steam or Epic.

- Epic game images are proxied through the backend to avoid browser CORS issues. The Rust log processor was updated to recognize Epic CDN patterns so Epic downloads show up correctly in the logs.

</details>

<details>
<summary><strong>Downloads Page</strong></summary>

- The downloads page now has filter dropdowns for narrowing results by platform, session, and other criteria. Pagination no longer flickers or causes layout shift when switching pages, and the page scrolls smoothly back to the top on each page change.

- Active downloads got a layout refresh with better spacing and readability. Download card banners now have consistent heights across all cards, and game images display correctly across normal, compact, and retro view modes.

</details>

<details>
<summary><strong>Setup Wizard</strong></summary>

- The initialization wizard now includes a platform setup step where you pick which services to configure during first-time setup. An Epic authentication step was added so you can connect your Epic account right from the wizard instead of configuring it later in the Management tab.

</details>

<details>
<summary><strong>Theme Editor</strong></summary>

- The create and edit theme modals now share the same editor form, so the experience is consistent whether you're building a new theme or tweaking an existing one. The color picker remembers your recently used colors.

- All hardcoded color values were replaced with CSS custom properties from the active theme, and `color-mix()` was removed entirely from the stylesheets. Community themes were updated to match the new theme schema.

</details>

<details>
<summary><strong>Code Quality</strong></summary>

- Steam and Epic daemon controllers now share a common base class instead of duplicating 350+ lines each. The same deduplication was done for the SignalR hubs. Cache management logic was moved out of the controller and into its own service, and around 650 lines of unused service code was removed.

- React context hooks were extracted into separate files across 20+ contexts so that component files only export React components. This eliminates Fast Refresh warnings during development.

</details>

<details>
<summary><strong>Bug Fixes</strong></summary>

- Fixed SignalR console spam from prefill progress events flooding the browser. Fixed session pagination returning incorrect page counts. Fixed byte formatting inconsistencies between the backend and frontend. Fixed a duplicate notification firing from game merging. Docker GitHub Actions were updated to Node.js 24 compatible versions.

</details>

---

Thanks for using LANCache Manager!
