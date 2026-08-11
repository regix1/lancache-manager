## Highlights

- **Signing in now takes a username and a password as well as the API key.** The key on its own is no longer a sign-in. On a fresh install the setup wizard has a new step that creates the first account, and that account becomes the main admin. On an installation that already exists, the first start after this upgrade ends every session and drops you on that same account-creation step, so nobody is left signed in under the old rules and nobody is locked out. Accounts live on the Users page under a new Accounts tab.
- **A third role sits between admin and guest.** A user reaches every page and every action an admin does, with two differences: the account list shows them other users but not admins, and only the main admin can create an admin or promote anyone to one. A guest is still read-only, and reads a little less than it used to. See Upgrading below for the exact list.
- **The main admin cannot be deleted, disabled, or demoted**, by anyone, including itself. Its row on the Accounts tab shows those actions greyed out rather than hiding them, so it is clear why they are unavailable.
- **Rotating the API key ends every session, guests included.** The new key comes back in the response before your own session is cut, so read it and keep it. Everyone signs back in with the new key plus their own username and password. Account rows are untouched by a rotation. A refused sign-in now asks, in its own notice beside the refusal, whether somebody regenerated the key, so nobody spends an hour retyping a password that was always correct.
- **A forgotten main-admin password can be reset with the API key.** This app sends no email, so the key is the recovery path. `POST /api/account-setup/recover-main-admin` takes the key, the main admin's username and a new password in the request body. It changes the password and nothing else: it cannot change a role, cannot move the main-admin flag to somebody else, and cannot target any other account. It also ends the main admin's existing sessions, so a stolen session does not survive the reset.

## Upgrading

Pull and recreate as usual. The database migrations for the account tables run on their own at first start. Everyone is signed out once, by design, and the first person back in creates the first account.

### Passwords now cross the network

Every sign-in sends a password to the server. Before this release the sign-in screen sent only an API key. If your installation is reached over plain HTTP, anyone who can watch the traffic between the browser and the server can now read a password out of it.

Put the app behind a reverse proxy that terminates HTTPS. Once that is working, set `Security__ForceSecureCookies=true` (or `Security:ForceSecureCookies` in `appsettings.json`) so the session cookie is only ever sent over an encrypted connection.

That setting is off by default and this release deliberately does not turn it on. A secure cookie is never sent at all over plain HTTP, so switching it on before HTTPS is in place does not protect the password, it stops you signing in. Turn it on after HTTPS works, not before.

The account-creation step warns you on screen when the page is not being served over HTTPS.

### Twenty routes stopped answering the way they used to

This is the part that matters if you run a read-only LAN dashboard.

Thirteen routes now return 403 to a guest. A signed-in user or admin still gets 200.

Client list, 6 routes:

- `GET /api/client-groups`
- `GET /api/client-groups/{id}`
- `GET /api/client-groups/mapping`
- `GET /api/clients/hostnames`
- `GET /api/stats/clients`
- `GET /api/stats/exclusions`

Cache health, 4 routes:

- `GET /api/cache`
- `GET /api/cache/size/scan/status`
- `GET /api/stats/eviction`
- `GET /api/stats/eviction/scan/status`

Schedules, 3 routes:

- `GET /api/system/schedules`
- `GET /api/system/schedules/{serviceKey}`
- `GET /api/system/schedules/{serviceKey}/run-status`

Seven more answered anybody, with no session at all. They now need a session, and a guest session is enough:

- `GET /api/auth/guest/status`
- `GET /api/auth/guest/config`
- `GET /api/auth/guest/prefill/config`
- `GET /api/auth/guest/epic-prefill/config`
- `GET /api/auth/guest/battlenet-prefill/config`
- `GET /api/auth/guest/riot-prefill/config`
- `GET /api/auth/guest/xbox-prefill/config`

Twenty routes in total: thirteen that a guest can no longer read, and seven that now need somebody to be signed in first.

**The five LAN event routes did not change.** A guest still reads all of them, on purpose, because that is the read-only dashboard everybody puts on a TV at an event:

- `GET /api/events`
- `GET /api/events/active`
- `GET /api/events/calendar`
- `GET /api/events/{id}`
- `GET /api/events/{id}/downloads`

**One cache route was left open on purpose**, `GET /api/stats/cache-snapshot`. It is the only one of the five cache reads that a guest-visible screen calls: the cache growth card on the dashboard reads it. Closing it would blank that card for every guest rather than protect anything, so it stayed open.

### If you script against the API

`POST /api/auth/login` now needs `apiKey`, `username` and `password` in the body. A script that posts the key on its own gets the same refusal as a wrong password, and every failure mode returns the same message and the same status, so the response will not tell you which of the three was wrong.

Anything that reads one of the twenty routes above has to sign in first, and for the thirteen a guest session is no longer enough.

## Housekeeping

Sign-in failures are counted per account and per client IP. Too many wrong passwords for one account lock that account for a while, and the count includes wrong current-passwords on the change-password screen, not just the sign-in screen. Too many attempts from one address are throttled regardless of which account they name. Account creation, deletion, role changes, sign-ins, key rotations and password recoveries are all written to an audit table.

Thanks for using LANCache Manager!
