## What's new

- **User accounts.** Sign-in now takes a username and password as well as the API key. The setup wizard creates the first account, which becomes the main admin. Existing installs sign everyone out once at first start. Accounts live on the Users page.
- **A user role between admin and guest.** Users get every page and action, but cannot see or manage admins. Guests stay read-only.
- **The main admin is protected.** Hidden from every other account and cannot be deleted, disabled or demoted. Only it can use Wipe All Accounts.
- **Custom schedules.** Tasks can run at times you pick, not just the built-in intervals.
- **Xbox in the setup wizard**, and xboxlive downloads are labelled Xbox everywhere.
- **Per-game data in `/metrics`** for Grafana dashboards.
- **An API reference at `/scalar`.** Unused endpoints were removed.
- **Real progress during a database import.**
- **Bandwidth and Compare Events on the dashboard.** One card, one tab each. Both plot total served, cache hits and cache misses over the header time range.
- **Peak Hours can shade by download count** instead of only data served.
- **Dashboard cards got a footer** with the time range chip, and a Live chip on live-only cards.
- **Drawers swipe closed on a phone.**

## Security

- **The API key alone is no longer a sign-in.** It only opens the API reference. See Upgrading if you script against the API.
- Password recovery and database repointing answer only in the hour after a start, so a leaked key alone is no longer a takeover.
- Session tokens stay out of JavaScript and URLs; writes need an antiforgery token.
- Stored prefill credentials are encrypted; any found unencrypted are discarded.
- A client's public IP is worked out locally, not by a third-party service.
- Rotating the API key ends every session and deletes every account except the main admin. The new key is in the response, so read it before your session ends.
- Guests can no longer read client, cache or schedule data.
- All npm advisories cleared, including two high severity.

## Fixes

**Charts and times**

- Download bytes spread across the hours they were served, so a long download no longer draws one spike and then zeros.
- Hourly charts group on a real time zone, so times land in the right hour.
- The dashboard no longer goes stale after a reconnect.

**Depot mapping and scans**

- The depot crawl counts its work correctly; scan modes only appear when the server can run them.
- Incremental scans no longer gated on a key they never used.
- The Full Scan Required prompt can be reopened, and stays closed during a download.
- New installs default to GitHub depot mapping.
- Cached downloads resolve on mapping runs even when signed out.

**Corruption scans**

- Scan state moved to PostgreSQL. Clearing a cache deletes its rows; state idle thirty days is swept.
- Unknown state versions rebuild instead of blocking every scan.
- Baselines survive a remount, so a restart is not a first run.
- Incremental scans say what they cannot see; a cancelled removal reports as cancelled.

**Operations and notifications**

- Cancelling a running operation works reliably.
- Do-nothing runs report as skipped, and mapping runs no longer misreport what they did.
- Cancellations are no longer pinned on the wrong user.
- Starting something already running no longer reports a fresh start.
- A job carries one name across every notification, and every sign-in gets a cancellable card.

**Prefill and sign-in**

- Login errors show in the modal, not behind it, and a refused sign-in says why.
- The dead Continue button on the Epic sign-in prompt works.
- Login steps share one shape, so the modal stops resizing.
- Each service offers the login validity window its backend really allows.
- A failed cleanup no longer leaves the config modal dead.
- The prefill socket keeps reconnecting past thirty seconds.
- The guest button works even when the setup check cannot be read.
- The Steam button reads Login with Steam, like Epic and Xbox.

**Interface**

- Tooltips close when a drawer or modal opens, never paint over it, and only appear when they add something.
- Copy buttons work over plain HTTP.
- The downloads list is no longer blank after switching view.
- Long session names trim with an ellipsis instead of overflowing.
- Number boxes can be cleared and retyped.
- Badges are capitals everywhere, centred, and long text shrinks without changing the pill height.
- Confirmation warnings are down to a sentence.
- Multi-select stops printing its count twice; its checkboxes look like checkboxes.
- Calendar controls stop shifting as you navigate months, and the day popover corners are fixed.
- The live event label sits on a bar across the frame's top edge.
- The Service Analytics chip stays when the panel has no data.
- The Off/On pill, Grafana dropdowns and depot mapping toggle line up at one width.
- The line-chart legend keeps each color square beside its label.
- Out-of-date Cache Files and Games on Disk cards show a warning outline.
- Demo Mode still lets you change the time range.
- Every corner sits on one radius scale.
- Three themes no longer paint a stray shadow under buttons.

**On a phone**

- Setup, accounts, Events and the event modal all fit.
- The Clients sort controls stay on one row.
- Controls and buttons line up at one height.
- The Epic and Xbox login buttons align right.

**Docs and translations**

- Docs match the current settings and defaults, and the external database privileges are documented.
- Three missing Chinese translations added; Chinese docs show their screenshots again.
- Month names follow the app language; server refusals show in the browser's language.
- The product name is spelled LANCache everywhere, and lint keeps it that way.

## Upgrading

Pull and recreate as usual. Migrations run at first start. Everyone is signed out once, and the first person back in creates the first account.

**Still on a SQLite build?** This release does not import the old `LancacheManager.db` and **deletes it on first start**. To keep pre-PostgreSQL history, upgrade to 1.10.5 first, let the import run, then upgrade again.

**If you script against the API:** `X-Api-Key` now works only on `GET /scalar` and `GET /openapi/v1.json`. Sign in with `POST /api/auth/login` (`apiKey`, `username`, `password`), keep the session cookie, and send the antiforgery token from `GET /api/auth/status` as `X-Antiforgery-Token` on writes. The four setup calls still take the key; all but `/api/setup/credentials` only answer in the hour after a start. `/metrics` is unchanged.

**Passwords now cross the network.** On plain HTTP they are readable in transit. Terminate HTTPS at a reverse proxy, then set `Security__ForceSecureCookies=true` (only after HTTPS works).

**Forgot the main admin password?** Restart the container, then within the hour `POST /api/account-setup/recover-main-admin` with the API key, the username and a new password.

Thanks for using LANCache Manager!
