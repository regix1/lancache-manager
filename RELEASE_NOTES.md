## What's new

**User accounts.** Signing in now takes a username and password as well as the API key. A fresh install creates the first account during the setup wizard, and that account becomes the main admin. An existing install signs everyone out once at first start and asks for that same account step, so nobody is left signed in under the old rules. Accounts live on the Users page under a new Accounts tab.

**A user role between admin and guest.** A user reaches every page and action an admin does, with two differences: the account list shows them other users but not admins, and only the main admin can create or promote an admin. Guests stay read-only. The Primary account is hidden from every other account, including other admins. The main admin cannot be deleted, disabled or demoted from the account list, including by itself. Wipe All Accounts is the exception: only that account can empty the table, and the next screen is create the first administrator.

**Custom schedules.** Tasks can now run at times you pick instead of only the built-in intervals. The schedule builder opens at midnight, and its Advanced drawer scrolls itself into view when you open it.

**Xbox in the setup wizard.** Xbox is now its own setup step, and xboxlive downloads are labelled Xbox everywhere in the app.

**Per-game data in the metrics endpoint.** `/metrics` now breaks its numbers down per game, so a Grafana dashboard can chart individual titles instead of only totals.

**An API reference.** `/scalar` documents every endpoint. Endpoints nothing was calling were removed at the same time.

**Real progress during a database import.** An import reports how far along it is instead of spinning until it finishes.

**Bandwidth and Compare Events on the dashboard.** One full-width card with a tab for each. Bandwidth plots cache hits and total served across the header time range, and picks a bucket width from the hours that actually have downloads so a short event is still a line. Compare Events overlays the parties you pick, lined up from each event's start and clipped to that same range.

## Security

- **The API key on its own is no longer a sign-in.** It authenticates the API reference and nothing else. See Upgrading if you script against this API.
- The session token is kept out of JavaScript and out of URLs, and any request that changes data needs an antiforgery token.
- Stored prefill credentials are encrypted, and any that were found stored unencrypted are discarded so you are asked for them again. Steam and Xbox credential formats are covered alongside the others.
- A client's public IP is worked out locally instead of by calling a third party.
- Rotating the API key ends every session, guests included, and deletes every account except the main admin. The new key comes back in the response before your own session is cut, so read it and keep it, and expect to create the other accounts again afterwards.
- Guests no longer read client, cache or schedule data, and cannot write to the schedule or setup routes.
- Every npm advisory is cleared, including two rated high severity.

## Fixes

**Depot mapping and scans**

- The depot crawl no longer miscounts the work it did, and scan modes are offered only when the server can actually run them.
- Incremental scans are no longer gated on a key they never used.
- The Full Scan Required prompt can be reopened after you dismiss it, and stays closed while a download is running.
- New installs default to GitHub depot mapping.
- Cached downloads are resolved on mapping runs even when you are signed out.

**Corruption scans**

- The scan baseline survives a remount, so a scan after a restart is not treated as a first run.
- An incremental scan now says what it cannot see.
- A cancelled removal reports as cancelled instead of as finished.

**Operations and notifications**

- Cancelling a running operation now works reliably.
- A run that did nothing reports as skipped rather than completed, and game mapping runs no longer misreport what they did.
- A cancellation is no longer attributed to a user who did not ask for it.
- Starting something already running no longer reports a fresh start.
- A job carries one name across every notification reporter.
- Every sign-in gets a real operation id, so its card can be cancelled.

**Prefill and sign-in**

- Login errors appear in the modal instead of behind it, and a refused sign-in on the upgrade form says why.
- The dead Continue button on the Epic sign-in prompt works.
- Every login step has the same shape, so the modal stops resizing as you move through it.
- Each service offers the login validity window its own backend really allows, and the new re-login date updates as you type.
- A failed cleanup no longer leaves the prefill config modal dead.
- The prefill socket keeps reconnecting past thirty seconds.

**Interface**

- Copy buttons work on a page served over plain HTTP.
- The downloads list is no longer blank when you switch view after a refresh.
- Tooltips appear only when they say something not already on screen, and swap straight over when you move between neighbouring controls.
- A long session name is trimmed with an ellipsis instead of overflowing its row.
- A number box can be cleared and retyped instead of only appended to.
- The setup and account screens work on a phone.
- Several controls line up at one width: the Off/On pill, the Grafana polling dropdowns and the Steam depot mapping toggle.
- The dashboard line-chart legend keeps each color square on the same line as its label, with space from the plot edge.
- Bandwidth and Compare tabs match the height of the other dashboard controls.
- Multi-select checkboxes are square with a slight corner, so they no longer look like radio buttons.
- Cache Files and Games on Disk cards that are out of date show a warning outline instead of looking current.
- Demo Mode still lets you change the header time range.

**Docs and translations**

- Three missing Chinese translations added, and the Chinese docs pages show their screenshots again.
- Server refusal messages are shown in the browser's language.
- The database privileges an external install needs are documented.
- The product name is spelled LANCache everywhere, and lint keeps it that way.

## Upgrading

Pull and recreate as usual. The account table migrations run on their own at first start. Everyone is signed out once, by design, and the first person back in creates the first account.

**If you script against the API, read this.** Sending `X-Api-Key` used to authenticate you as an admin on every endpoint. It now works on `GET /scalar` and `GET /openapi/v1.json` and nowhere else, so anything that polls an endpoint with only that header stops answering the moment you upgrade. Sign in instead with `POST /api/auth/login`, which needs `apiKey`, `username` and `password` in the body, and keep the session cookie. A request that changes something also needs an antiforgery token: call `GET /api/auth/status` with the same cookie jar and send the value back as an `X-Antiforgery-Token` header.

Four setup calls still take the API key, because they have to work before anyone can sign in: `POST /api/setup/credentials`, `POST /api/setup/external`, `POST /api/account-setup/first-admin` and `POST /api/account-setup/recover-main-admin`.

`/metrics` is unchanged. It keeps its own `Security:RequireAuthForMetrics` setting and still takes the key in the header, so Prometheus scrapers need no change.

**Passwords now cross the network.** Before this release the sign-in screen sent only an API key. If your installation is reached over plain HTTP, anyone watching that traffic can now read a password out of it. Put the app behind a reverse proxy that terminates HTTPS, then set `Security__ForceSecureCookies=true`. Turn that on after HTTPS is working, not before, because a secure cookie is never sent over plain HTTP at all and switching it on early just stops you signing in.

**Forgot the main admin password?** This app sends no email, so the API key is the recovery path. `POST /api/account-setup/recover-main-admin` takes the key, the main admin's username and a new password. It changes that password only, and ends the main admin's existing sessions.

Thanks for using LANCache Manager!
