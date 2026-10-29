# Prefill { #prefill-steam--epic }

Prefill downloads games into your cache *before* people connect. When guests show up, every install reads from your cache instead of the public internet - full LAN speed, no bandwidth bottleneck.

Steam, Epic, Battle.net, Riot, and Xbox each run in their own container, so you can prefill all of them at the same time without them interfering. Progress streams live to the UI.

<div align="center" markdown>
<img alt="Game Prefill platform picker showing Steam, Epic Games, Battle.net, Riot Games, and Xbox" src="../images/prefill-home.png" />

<em>Pick a platform to start a prefill session</em>
</div>

### Requirements

- Docker socket mounted (`/var/run/docker.sock`)
- Signed in to LANCache Manager as **Primary** or **Account** (**Guest Mode** can prefill only on platforms granted in **Guest Defaults**)
- Your cache server is reachable from the prefill container (see [Network setup](#prefill-network) below)
- For Xbox specifically, a container-based lancache. Bare-metal has no Xbox Live log, so its downloads stay invisible to the manager - see [Bare-Metal LANCache](bare-metal-lancache.md#bare-metal)

### Running a prefill

The flow is the same on every platform:

1. Open the **Prefill** tab and click **Start Session** on **Steam**, **Epic Games**, **Battle.net**, **Riot Games**, or **Xbox**
2. Sign in if the platform needs an account:

    | Platform | How you sign in |
    |---|---|
    | Steam | Username, password, and Steam Guard |
    | Epic Games | Click **Open Epic sign-in page**, paste the **Authorization Code**, then click **Submit code** |
    | Xbox | Click **Open Microsoft page** and enter **Your code** there |
    | Battle.net, Riot Games | No account needed |

3. Click **Select Apps**
4. Click **Prefill Selected**, then **Start Download**

That's it. Leave it running - when guests arrive, everything's cached.

!!! note
    Prefill builds on community daemons:

    - **Steam**: [steam-prefill-daemon](https://github.com/regix1/steam-prefill-daemon), a fork of [steam-lancache-prefill](https://github.com/tpill90/steam-lancache-prefill) by [@tpill90](https://github.com/tpill90)
    - **Epic**: [epic-prefill-daemon](https://github.com/regix1/epic-prefill-daemon) - sign in at Epic, then paste the **Authorization Code**
    - **Battle.net**: [battlenet-prefill-daemon](https://github.com/regix1/battlenet-prefill-daemon) - fully anonymous, no account needed
    - **Riot**: [riot-prefill-daemon](https://github.com/regix1/riot-prefill-daemon), built on [@tpill90](https://github.com/tpill90)'s lancache-prefill tools - fully anonymous; covers League of Legends and Valorant
    - **Xbox**: [xbox-prefill-daemon](https://github.com/regix1/xbox-prefill-daemon) - signs in with a Microsoft device code (account required, like Epic)

### Importing Steam App IDs

Have a list of App IDs from `steam-lancache-prefill` or somewhere else? Skip the library browser:

1. Click **Select Apps**
2. Click **Import App IDs**
3. Paste your IDs in any of these formats:
   - Comma-separated: `730, 570, 440`
   - JSON array: `[730, 570, 440]`
   - One per line
4. Click **Import**

The dialog tells you how many games were added, how many were already selected, and how many IDs aren't in your Steam library (those aren't added to your selection).

!!! tip
    **Coming from `steam-lancache-prefill`?** Open `selectedAppsToPrefill.json` and paste the contents straight into the import field - the JSON array is parsed as-is.

### Scheduled Prefill { #scheduled-prefill }

Set this up once and stop prefilling by hand before every event. Go to **Management → Schedules** and open the Scheduled Prefill card.

#### Setting it up

1. Click **Start** on a service to start its container.
2. For Steam, Epic, or Xbox, click **Manage** and sign in. Battle.net and Riot need no account.
3. Click **Add schedule**, pick games or a preset, and click **Save**.

The schedule runs one interval after you save it. To run it now, click **Run schedule** on its row.

#### What the buttons do

| Button | What it does |
|---|---|
| **Run all** | Starts every enabled schedule |
| **Run schedule** (on a row) | Starts only that schedule |
| **Edit schedule** | Opens the schedule. A run always uses the saved version, so save before you run. |
| **Add schedule**, **Duplicate** | Open a draft. Nothing is created until you click **Save**. |
| **Actions → View activity** | Shows active downloads and run history for every service, and lets you cancel a download |
| **Actions → Container settings** | Sets defaults for every service container, including **Login**. A saved **After a restart** override still wins for its service. |
| **Clear stored logins** | Signs out running containers and removes saved Steam, Epic, and Xbox logins |

#### When a service is skipped

Each service has one shared container for all of its schedules. A scheduled run only uses that container if it is already running, and for Steam, Epic, and Xbox, signed in. Otherwise:

- That service is skipped and the notification says it needs login. Other services still run.
- If nothing ran at all, the run is reported as unsuccessful, with the reason.
- The schedule keeps its interval either way.

Only the LANCache account that saved a Steam, Epic, or Xbox login can reuse it.

#### Deleting

- Deleting a schedule does not stop its container or remove cached games.
- To delete cached games, use **Game Cache Detection and Removal** under **Management → Logs & Cache**.

#### Other schedule settings

- **Intervals and state.** Every saved schedule has its own "run every" interval. You can pause a schedule or set it to run only on startup. The first automatic run is one interval after you save; saving does not start a run.
- **Presets or hand-picked games.** Presets are **All**, **Recent**, and **Top**. Epic has no Recent because its API exposes no last-played data. Battle.net and Riot are All-only. Games selected for a schedule override its preset.
- **New schedule history.** "Last run: Never" remains until that saved schedule finishes its first run. Its next run is predicted from its interval.
- **Target platforms.** Only Steam supports the Windows, Linux, and macOS depot filter. Windows is the default.
- **Download controls.** Force re-download is off by default. Connections can be **Auto** or **Fixed** from 1 through 256. Run notifications are **All runs**, **Manual only**, **Silent**, or **Hidden**.

Defaults and limits:

| Setting | Default |
|---|---|
| Run every (per schedule) | 24 hours |
| Preset | All (Top uses the top 50 games) |
| Persistent login validity | 90 days |
| No-progress cutoff (per scheduled run) | 30 minutes |
| Force download | Off |
| Max concurrency | Auto (fixed: 1-256) |
| Longest single service run | 12 hours |

The rest of the Schedules page works the same way for every background service - log rotation, eviction scans, game detection, cache snapshots, and more. Each service is a row with its own interval and a run control at the end. There's an **Xbox Game Mapping** row too, so the Xbox catalog can refresh on its own schedule.

### Network setup { #prefill-network }

**Most installs need zero config.** If you run the standard `lancache` + `lancache-dns` containers, lancache-manager auto-detects them and prefill works without further setup.

If your DNS isn't a stock `lancache-dns` (you use AdGuard Home, Pi-hole, public DNS, etc.) or your routing is unusual, set one env var and you're done:

| Your setup | What to set |
|---|---|
| Stock `lancache` + `lancache-dns` containers | nothing |
| Single-box install (lancache on the same host as lancache-manager) | nothing |
| AdGuard Home, Pi-hole, or any DNS replacement | `Prefill__LancacheIp=<your-cache-ip>` |
| Host networking, host's DNS doesn't route CDN to your cache | usually nothing - the cache is auto-detected via the bridge gateway and heartbeat-verified; set `Prefill__LancacheIp=<your-cache-ip>` if the network panel still warns |
| Caddy/Squid/non-nginx cache that routes by `Host:` header | `Prefill__LancacheIp=<your-cache-ip>` |
| You want predictable behavior regardless of environment | always set `Prefill__LancacheIp` |

!!! tip
    **`Prefill__LancacheIp` is the universal override.** When set, prefill talks to your cache by IP and never asks DNS where the cache lives. Network mode and DNS server settings stop mattering for CDN traffic.

Full descriptions and defaults for `Prefill__LancacheIp`, `Prefill__LancacheDnsIp`, and `Prefill__NetworkMode` live in the [Configuration → Prefill](configuration-reference.md#prefill-config) reference table.

!!! important
    **`LancacheIp` and `LancacheDnsIp` are different services, even on the same machine.**

    | | What it is | Port | Job |
    |---|---|---|---|
    | `LancacheIp` | The **cache server** (`lancachenet/monolithic`, or any HTTP cache) | HTTP / 80 | Holds the actual cached game files |
    | `LancacheDnsIp` | The **DNS server** (`lancachenet/lancache-dns`, AdGuard Home, Pi-hole, etc.) | DNS / 53 | Translates `lancache.steamcontent.com` into the cache's IP |

    Think of a small town: the **cache** is the library where the books live, and the **DNS server** is the information booth you ask for directions. They can share a building (same IP, different ports) but they do different jobs. Setting `LancacheIp` walks straight to the library, which is why DNS stops mattering for cache traffic.

!!! important
    `LANCACHE_IP` only redirects CDN chunk traffic, which is all lancache caches anyway. Steam (`api.steampowered.com`) and Epic (`*.epicgames.com`) auth and manifest endpoints still use normal DNS, and are unaffected.

#### Examples

**Most reliable** - `LancacheIp` makes CDN routing DNS-independent:

```yaml
environment:
  - Prefill__NetworkMode=host
  - Prefill__LancacheIp=192.168.1.10
```

**Bridge mode with a non-standard DNS** (e.g., AdGuard Home replacing lancache-dns):

```yaml
environment:
  - Prefill__NetworkMode=bridge
  - Prefill__LancacheIp=192.168.1.10        # cache server
  - Prefill__LancacheDnsIp=192.168.1.20     # DNS server
```

**Bridge mode, stock lancache-dns, no IP override** (legacy DNS-driven path):

```yaml
environment:
  - Prefill__NetworkMode=bridge
  - Prefill__LancacheDnsIp=192.168.1.20
```

!!! tip
    **Prefill container has no internet?** Try `Prefill__NetworkMode=bridge`.

#### Network diagnostics

Each prefill session runs a connectivity test on startup and writes the result to logs:

```
═══════════════════════════════════════════════════════════════════════
  PREFILL CONTAINER NETWORK DIAGNOSTICS - prefill-daemon-abc123
═══════════════════════════════════════════════════════════════════════
  Internet connectivity: OK (reached api.steampowered.com)
  lancache.steamcontent.com resolved to 192.168.1.10
  DNS looks correct (private IP - likely your lancache server)
═══════════════════════════════════════════════════════════════════════
```

If the resolved IP is a public address (Steam's real CDN IPs look like `162.254.x.x`), traffic is bypassing your cache. Set `Prefill__LancacheIp` and restart the session.

??? tip "How routing works (advanced) — which path a request takes"

    ```mermaid
    ---
    config:
      flowchart:
        curve: basis
        padding: 12
    ---
    flowchart TD
      Start([Need a game chunk<br/>from a CDN hostname])
      HasIp{LANCACHE_IP available?<br/>Prefill__LancacheIp or<br/>auto-detected + verified}

      Start --> HasIp

      HasIp -->|yes| Direct[Talk to that IP directly<br/>Host header = CDN name]
      Direct --> Hit([Served from your cache])

      HasIp -->|no| AskDns[Ask DNS where the<br/>CDN hostname points]
      AskDns --> Mode{NetworkMode?}

      Mode -->|host| HostDns[Use the host machine DNS<br/>Prefill__LancacheDnsIp is ignored]
      Mode -->|bridge| Bridge{Prefill__LancacheDnsIp set?}

      Bridge -->|yes| Forced[Query that DNS server]
      Bridge -->|no| Probe[Daemon probes CDN name,<br/>localhost, then gateway]

      HostDns --> Resolved{DNS returned<br/>your cache IP?}
      Forced --> Resolved
      Probe --> Resolved

      Resolved -->|yes| Hit
      Resolved -->|no| Miss([Public CDN IP<br/>traffic skips your cache])
    ```

    Every combination:

    | `NetworkMode` | `LancacheIp` | `LancacheDnsIp` | Outcome |
    |:---:|:---:|:---:|---|
    | `host` | set | (any) | Reliable. `LANCACHE_IP` injected; DNS irrelevant. |
    | `host` | unset | (any) | Usually fine. Auto-detect + heartbeat injects `LANCACHE_IP`; otherwise host DNS is used. DnsIp is dropped in host mode. |
    | `bridge` | set | unset | Reliable. `LANCACHE_IP` injected; DNS irrelevant. |
    | `bridge` | set | set | Reliable. `LANCACHE_IP` for CDN, DnsIp for auth/manifest. |
    | `bridge` | unset | set | Works if DnsIp resolves CDN to your cache. |
    | `bridge` | unset | unset | Usually fine. Auto-detect injects `LANCACHE_IP`; otherwise the daemon probes localhost/gateway. |

    **Why `LancacheIp` always works:** with it set, the daemon requests `GET http://192.168.1.10/depot/...` with `Host: lancache.steamcontent.com`. Your cache routes on `Host:` and serves from cache. DNS is never asked for the CDN domain.

-----
