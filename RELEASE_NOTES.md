## Highlights

- **[Scheduled Prefill](https://github.com/regix1/lancache-manager/blob/main/README.MD#scheduled-prefill)** - prefill Steam, Epic, Xbox, Battle.net, and Riot games automatically, each on its own schedule. Every platform runs in a persistent container that keeps its login between runs, so a scheduled run no longer needs you there to sign in.
- **[Bare-metal LANCache](https://github.com/regix1/lancache-manager/blob/main/README.MD#bare-metal)** - a bare-metal cache is now read natively, per-service log layout and all. Monitoring, log management, and every disk feature work with no nginx changes, and after a removal the manager can ask the host nginx to reopen its logs itself. Xbox is the one exception, since bare-metal writes no Xbox log.
- **[Xbox prefill](https://github.com/regix1/lancache-manager/blob/main/README.MD#prefill-steam--epic)** - Xbox joins as the fifth prefill platform, signing in with a Microsoft device code. Its game mapping also runs on its own, so you can name Xbox downloads without ever prefilling.
- **[Status Check](https://github.com/regix1/lancache-manager/blob/main/README.MD#status-check)** - a new diagnostics tab that answers "is my LANCache actually working?" without a terminal: per-domain DNS resolution, whether the cache answers, and whether recent downloads really went through it.
- **Structural corruption detection** - cache integrity scanning gains a second method alongside repeated-miss detection. Rather than inferring from log patterns, it opens each cache file and validates its framing, flagging only proven failures such as a truncated header or a cache key that does not match its path. Run it as a **Full** scan or an **Incremental** scan that only re-inspects what changed, and the last three scans per method stay browsable in a history panel.
- **Quieter notifications** - each scheduled service can report as a thin bar under the navigation instead of a full card. Compact notifications merge into a single strip where every service keeps its own colour segment, and hovering or tapping expands the real cards. Routine chores like history cleanup and artwork now default to notifying on manual runs only; heavy scans and logins still get a full card.
- **[Refreshed interface](https://github.com/regix1/lancache-manager/blob/main/README.MD#screenshots)** - redesigned Dashboard, Downloads, Schedules, Clients, Users, and Logs & Cache pages on one shared visual system, with genuine expand and collapse animations throughout and a new app-wide font.
- **[AGPL-3.0](https://github.com/regix1/lancache-manager/blob/main/README.MD#support-and-license)** - the first release under the AGPL-3.0 license; 1.10.3 and earlier remain MIT.

## Also new

- **An "After a restart" setting** for persistent prefill containers, as a global default with per-platform overrides: stop and log out, keep running (the new default), or full persistence, which also recreates a container that died while the manager was down and quietly signs it back in from its saved login. Login validity defaults to 90 days and is capped at whatever the platform's own token allows, so the displayed re-login date never promises longer than the token lives, and a **Clear stored logins** button wipes a saved account outright.
- **Multi-select batch removal** across the cache management lists, so clearing several games or services is one action instead of one at a time.
- **Per-client exclusion modes.** An exclusion is now a rule with a mode: **Stats only** keeps a client visible in downloads and live speeds but drops it from totals, hit rate, and leaderboards, while **Hidden** removes it from every view and every statistic.
- **A Hit/Miss/All filter** on Downloads, so a blended hit rate that looks low right after a prefill can be split apart. The Retro view adds **Group by service**, and Compact, Normal, and Card views now show real dates instead of only "2h ago".
- **Live in-progress downloads** appear in the recent lists instead of only showing up once finished, and Recent Downloads rows carry a cache hit rate coloured by band.
- **A manual per-datasource cache size limit**, for setups where the real disk size cannot be detected, with a reset back to auto-detect. The interface labels which source is in effect.
- **An opt-in orphan prune** on the eviction scan, for download records with no log history left to verify them. Off by default.
- **Battle.net coverage** - Black Ops 6 and the other new product codes map to real game names instead of raw codes, and already-ingested unmapped Blizzard downloads get renamed at startup rather than waiting for the next batch.
- **An operation queue** - heavy scans, removals, and resets run one at a time and queue rather than colliding, with a waiting card showing where a request sits.
- **Theme colours for 17 more cache services**, wired through the analytics chart, legend, badges, and the theme editor. The colourblind themes were rebuilt to stay distinguishable, and every built-in theme version was bumped.
- **One live signal behind every status dot.** Schedules, daemons, containers, and sessions all read from a shared activity registry, and the presence heartbeat now runs app-wide, so a session no longer ages to "away" while you sit on another page.
- **Clipped text reveals itself on tap.** Game names, client names, container paths, and event titles that get truncated now show their full value in the shared tooltip on touch as well as hover.
- Progress for the Rust-backed scans is pushed as it happens rather than polled, so bars move smoothly and land on accurate final counts, and a **write-blocked cache or logs directory now names the real cause** - a read-only mount, an NFS export problem, or a user-namespace remap - instead of always blaming file ownership.
- Noticeably lower memory use across the backend and the Rust scan binaries, and faster structural scans on network storage.

## Fixes

- A per-session **thread-count override** could not be saved or read back. The editor always showed "no override" and anything set was silently dropped. Steam and Epic now have their own control and both persist.
- **Retro downloads** grouped by game or service reported one member's speed instead of the group's, and could never show the Evicted badge. Both now come from the server.
- **Ubisoft, GOG, and Rockstar** had a brand colour in the dashboard chart and plain grey everywhere else. They are now consistent, and Rockstar no longer renders in the warning colour.
- **Cancelling log processing** during setup called a superseded endpoint, so the run kept going. It now stops, and reports through the normal channel instead of as an error.
- **Xbox sign-ins** failed with `invalid_grant`, and a restart could quietly lose the session. Xbox now stays signed in across restarts.
- **Eviction detection** missed any game that had only ever been downloaded once, so it never appeared as evicted. Evicted services could also fail to appear in Evicted Items at all.
- **Cache size scans** could be killed the moment they started by a stale cancel flag left in browser storage, and answered with a server error instead of a result.
- **Recent Downloads** could empty itself when a single dashboard query failed, and Windows Update polling filled it with sessions stuck at 0 B. The list now keeps its previous data and marks itself stale, and sessions that never moved a byte are hidden.
- **The Downloads service filter listed Xbox twice**, and a Steam depot rollover showed the same game twice in live speeds.
- **Notification progress bars froze after a tab switch**, so a quiet operation like scheduled prefill could sit with a vanished bar for as long as half an hour.
- **Client groups** stayed on screen after a database reset until the page was reloaded.
- Help notes rendered untinted, so info, warning, success, and tip all looked the same.
- Reduced-motion settings were ignored on the dashboard and in edit mode.
- A rejected prefill credential was reported as accepted when the daemon was reached over TCP.
- The notification strip could paint as a solid coloured bar under the navigation instead of a thin line.
- GC settings could be lost if the process stopped mid-write.

## Removed

- The **Steam Game Info** schedule is gone. It rebuilt an in-memory depot lookup every six hours that nothing read.
- The manual Epic and Xbox **Refresh Catalog** cards are gone from the Data page. Mapping runs on every log ingest and the catalogs refresh on login and on schedule; sign-in stays in Integrations.
- The duplicate **Steam Game Mapping** card is gone from the Data page, and its Configure Steam API button moved onto the depot mapping card in Schedules.

## Upgrading

Pull and recreate as usual. Three new settings are available, all optional: `Prefill__XboxDockerImage` for the new Xbox platform, the advanced `Prefill__StallTimeoutSeconds`, which backs a watchdog that now fails a stuck prefill session instead of letting it hang forever, and the per-datasource `LanCache__DataSources__<n>__SchemeOverride`, for bare-metal installs whose log filenames defeat auto-detection. Nothing was renamed or removed, so existing Compose files keep working. One cleanup: `Security__MaxAdminDevices` is an old no-op that current code ignores and can be deleted.

Two things to expect on first start. Corruption evidence moved to a new contract for the structural method, so **saved corruption scan results from 1.10.3 need a fresh scan** before you can act on them. And if you are pointing at a bare-metal cache, read [Bare-Metal LANCache](https://github.com/regix1/lancache-manager/blob/main/README.MD#bare-metal) first: reopening the host nginx logs after a removal needs `pid: host`, and root is still not required.

## Housekeeping

Roughly 8,750 lines of duplicated and dead code came out across the frontend and backend, deleting about 20,700 lines against 12,000 added. The bulk of it was a duplicated daemon client whose socket and TCP halves were near-identical copies, hand-rolled per-service status cards that collapsed onto one shared component, a Status Check cache detector that existed twice, an obsolete operation-polling path superseded by the queue, and a handful of dead services, endpoints, and UI surfaces. Error handling was standardised across the backend, the Rust processor, and the frontend. No intended behaviour changes, though a few latent defects the consolidation exposed were fixed alongside it.
