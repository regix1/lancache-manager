# What You Get { #screenshots }

A quick tour of the main pages. All screenshots use the default dark theme.

### Dashboard

<div align="center">
<img alt="Dashboard overview with bandwidth saved, cache hit ratio, service breakdown, and top clients" src="images/dashboard-overview.png" />

*Dashboard - bandwidth saved, hit ratio, service analytics, and top clients in one view*
</div>

### Downloads

<div align="center">
<img alt="Downloads Normal view showing per-game download cards with cache-hit progress" src="images/downloads-normal.png" />

*Downloads - every cached game with cover art, sizes, and per-client history*
</div>

Three view modes: **Normal** (cards, shown above), **Compact** (a dense list), and **Retro** (a per-depot table straight out of a 90s BBS). Hit/Miss and per-client filters narrow the list.

### Clients

<div align="center">
<img alt="Clients page listing devices with per-device download totals, cache hits and misses, and hit rate" src="images/clients.png" />

*Clients - which devices use the cache, and how well it serves each one*
</div>

Open it when you want to know which machines pull the most and whether their installs actually hit the cache.

### Users

<div align="center">
<img alt="Users page showing the active session list" src="images/users-sessions.png" />

*Users - active sessions and guest access*
</div>

This is where guest access lives: watch active sessions and hand out time-limited, view-only access without sharing your API key.

### Events

<div align="center">
<img alt="Events calendar with a LAN party event scheduled" src="images/events.png" />

*Events - download activity and LAN events on a calendar*
</div>

Planning a LAN party? Put it on the calendar and see download activity in date context.

### Status Check { #status-check }

<div align="center">
<img alt="Status Check tab showing DNS resolution results and cache-domain verification" src="images/status-check.png" />

*Status Check - verify DNS, cache reachability, and recent download routing*
</div>

The Status Check tab (Management → Status Check) answers "is my LANCache actually working?" without touching a terminal. It checks that game domains resolve to your cache, that the cache answers, and that recent downloads really went through it - per domain, in plain language.

<details>
<summary><strong>Why "From this device" says <em>inconclusive</em>, and how to fix it</strong></summary>

Your browser runs that one probe, not the server, so it reports what your client sees. It requests `http://lancache.steamcontent.com/lancache-heartbeat`, and a real cache node answers `204` with an `X-LanCache-Processed-By` header naming itself.

Browsers won't let a page read that header across origins unless the cache opts in, and out of the box it doesn't. So the card falls back to a request that proves *something* answered but not *what*, reports **inconclusive**, and logs a CORS error in your browser console. Nothing is broken. Every other Status Check result comes from the server and is unaffected.

To get a definitive answer, let the cache expose the header. Add these to the heartbeat location in your cache's nginx config:

```nginx
location /lancache-heartbeat {
    add_header X-LanCache-Processed-By $hostname always;
    add_header Access-Control-Allow-Origin "*" always;
    add_header Access-Control-Expose-Headers "X-LanCache-Processed-By" always;
    return 204;
}
```

`Access-Control-Expose-Headers` is the line people miss. Without it the request succeeds and the header is still unreadable, so the card stays inconclusive. `*` is safe here: the probe sends no cookies or credentials, and the response carries nothing but the node name.

Two caveats. If you serve this manager over **https**, the browser blocks the plain-http probe outright and the card cannot work regardless of CORS. And if the probe returns something other than `204` (a `403`, say), that is a real finding rather than a CORS problem: the domain resolved to something that is not your cache.

</details>

### Logs & Cache

<div align="center">
<img alt="Logs and Cache management showing log processing and cache operation controls" src="images/management-logs-cache.png" />

*Management → Logs & Cache - process logs, manage the disk cache, and detect corrupted or evicted files*
</div>

**Corruption scanning.** Two scans, for two different problems. A *repeated-miss* scan reads your logs for files that are on disk but keep missing, which usually means the cached copy is bad. A *structural* scan opens the cache files themselves and checks nginx headers, payload offsets, and recorded lengths, flagging only proven failures. Structural runs as **Full Scan** (every eligible file, rebuilds the baseline) or **Incremental Scan** (only new, changed, or previously unresolved files). Removing anything a scan finds needs the `/cache` mount without `:ro`.

That covers the daily surface. Management has more tabs behind it - expand below to see all of them.

<details>
<summary><strong>See every Management page</strong></summary>

#### Settings

<div align="center">
<img alt="Management Settings tab with API authentication, demo mode, and display preferences" src="images/management-settings.png" />

*Settings - authentication, demo mode, and display preferences.*
</div>

**Demo Mode** fills the interface with simulated data, so you can try the UI out before you have any real cache history.

#### Integrations

<div align="center">
<img alt="Integrations tab showing sign-in cards for all five game platforms and the Prometheus endpoint panel" src="images/management-integrations.png" />

*Integrations - sign in to the game platforms and configure the Prometheus endpoint. One page shows the login state of all five prefill services.*
</div>

#### Data

<div align="center">
<img alt="Data tab with the Steam game mapping card and the database import form" src="images/management-data.png" />

*Data - Steam game mapping and database import.*
</div>

#### Schedules

<div align="center">
<img alt="Schedules tab showing per-service schedule rows with intervals and Run Now controls" src="images/management-schedules-system.png" />

*Schedules - every background service on its own interval, each with a Run Now control. The Scheduled Prefill card lives at the bottom of this page and is shown in the Prefill section below.*
</div>

#### Theme

<div align="center">
<img alt="Theme gallery with installed themes, community themes, and a custom theme upload area" src="images/management-theme.png" />

*Theme - switch between installed themes, import community themes, or upload your own.*
</div>

#### Clients (aliases and exclusions)

<div align="center">
<img alt="Management Clients tab for assigning nicknames and excluding devices from stats" src="images/management-client-aliases.png" />

*Clients - give devices friendly names and exclude machines from the stats.*
</div>

One nickname can cover several IP addresses - handy when a machine dual-boots or moves between wired and wireless and would otherwise show up as separate clients.

#### Prefill Sessions

<div align="center">
<img alt="Prefill Sessions tab showing live, persistent, and past prefill container sessions" src="images/management-prefill-sessions.png" />

*Prefill Sessions - watch live and persistent prefill containers and review past runs.*
</div>

</details>

-----
