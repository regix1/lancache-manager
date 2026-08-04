## Highlights

- **The 1.10.4 image shipped without a database inside it.** A Windows line ending in the `VERSION` file cut the Docker build arguments short and dropped the two that switch PostgreSQL on, so the image published as `:latest` had no database at all. A fresh pull met "An unexpected error occurred" at the login screen, and pinning to 1.10.3 was the only way around it. The file now keeps Unix line endings, the build refuses to produce a full image without PostgreSQL in it, and the release job checks the finished image before anything is tagged.
- **A container that cannot reach its database no longer looks broken.** Background jobs stay quiet instead of retrying every few seconds and burying the log, a failed request gives the real reason instead of a blank error, and the health check stays green during setup so Docker does not restart you mid-wizard.
- **You can fix a broken database without logging in first.** Saving the PostgreSQL password takes your admin API key now, since a login session needs the very database that is broken. The error screen grew a recovery panel for exactly that.
- **[A rebuilt Clients page](https://regix1.github.io/lancache-manager/client-hostnames/)** - nicknames show each machine's hostname next to its address, you can add to one by typing an address or a machine name, and each nickname reports either as one combined row or one row per member IP.
- **A new theme called Graphite**, a warm charcoal dark rather than the blue-black of the existing one, and a single slider for choosing between the built-in themes. A theme that sets only its own accent colour now gets matching buttons, sliders, and chart lines instead of leaving them blue.
- **[The documentation moved to its own site](https://regix1.github.io/lancache-manager/)** in English and Chinese, split into separate guides. Both READMEs keep the quick start and link out for the rest.

## Also new

- **A service filter on the Games on Disk chart**, plus Service and Client labels on the two dashboard download filters.
- **Each prefill service explains itself** in the guest settings, instead of all five sharing one generic line.
- **Games from any prefill service can borrow Steam store artwork**, not just Blizzard, which brings in art for a set of Epic and Xbox titles.
- **A custom theme whose id clashes with a built-in one is rejected** with a message telling you to rename it, instead of appearing to upload and then never being selectable.
- Section descriptions moved into the help popover next to each title, and the repeated page headings on Dashboard, Clients, Events, and Users are gone.

## Fixes

- **PostgreSQL startup failures used to vanish**, leaving only "did not become ready in time". The real server log is printed now, along with warnings for a data folder that is not on a mounted volume, a database found somewhere unexpected, and a corrupt credentials file.
- **A misleading "your data will be lost" warning** when the Postgres volume was mounted one level up, which protects your data just as well.
- **External database setup skipped checks the embedded one already had**, so a bad username or a weak password could be saved and then break on the next restart.
- **A `POSTGRES_USER` or `POSTGRES_DB` containing a hyphen** could silently fail to create the role or database, and an embedded password containing an apostrophe could end up different on the database than in the saved file.
- **The Clients hostname lookup failed silently.** It now says whether it is still looking, found no DNS server, timed out, has no records for those addresses, or had more clients than one pass can check.
- **Popovers, tooltips, and calendar pickers ran off the edge of the screen**, phones especially. They stay inside the visible area now and reposition when the window changes.
- **The prefill login window could stick on "Authenticating"** long after the login had finished, failed, or timed out. Closing it with the X cancels properly and the countdown matches the time actually left.
- **Waiting notifications now name what is blocking them**, and the dot on a schedule row lights up for runs that start in the background.
- **Deleting an event the dashboard was filtered to** left it stuck until a page reload, and **clearing a prefill game selection to zero** failed with an error the screen never showed.
- **Leaving a theme preview reloaded the whole page**, and Retro column widths could come out too narrow after a theme change.
- **Game mapping progress cards vanished** on a reload or tab switch, and Battle.net and Riot could not be cancelled from their card. All five services recover and cancel now.
- **Dashboard, Downloads, and Clients could show stale totals** right after a cache clear or an eviction scan.
- **Epic games found through prefill had no banner art** until a later login refresh, and editing a scheduled prefill then closing the tab could leave a container signed in with nobody using it.
- Compact notifications opened as an overlay that could cover a card underneath, the downloads sort dropdown resized itself with every option, a queued detection could hang instead of failing on a genuine refusal, and a long tail of phone-width layout problems across Log Processing, Scan history, Users, Retro pagination, and Prefill Sessions.

## Upgrading

Pull and recreate as usual. No settings were added, renamed, or removed, so existing Compose files keep working, and two database migrations run on their own at first start.

Two things matter if you script against the API. `/health` now always answers 200 while the process is up, rather than failing during first-time setup, so a container waiting on the wizard no longer gets killed and restarted before anyone can finish it. Read `setupRequired` in the response body to tell the two states apart. And `POST /api/setup/external` and `POST /api/setup/credentials` both need your admin API key in an `X-Api-Key` header now. Any bookmark pointing at a README anchor needs to move to the matching page on [the docs site](https://regix1.github.io/lancache-manager/).

## Housekeeping

Around 340 code files changed, with 21 new test files covering the database outage paths, the health endpoint, and client hostnames. The Rust programs now resolve their Postgres connection the same way the rest of the app does, honouring `POSTGRES_MODE` and ignoring stray variables like an ambient `PGPORT` that could point them at the wrong server.
