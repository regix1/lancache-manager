1.10.7 rebuilds the Users page, adds single sign-on (Google, GitHub, Microsoft, Apple or any OpenID Connect provider), and makes Cancel, prefill schedules and notifications dependable. It also fixes the memory growth from issue #30. **Update your prefill daemon images together with the manager**, and read Action required if you run with sign-in off or script against the API.

## Action required

Pull and recreate as usual. Database migrations run at first start.

**Everyone using prefill:** update all five prefill daemon images (Steam, Epic, Xbox, Battle.net, Riot) at the same time as the manager. Older daemons send status words this version no longer reads, so a cancel would end as a failure.

**If you run with sign-in off (Unauthenticated):** sign-in off now means no accounts. Switching to Unauthenticated in the app deletes every account, after a confirm that shows how many. An install that has finished setup and starts in Unauthenticated mode also deletes its accounts, with no confirm. Turning sign-in back on starts the create-first-admin wizard.

**If you run LANCache on bare metal behind a reverse proxy:** replace each site's `access_log` line with the standard line in the bare-metal setup guide. Do not add a second line. Update the manager first, because older versions drop lines with the forwarded field filled in.

**If you script against the API:**

1. `POST /api/auth/setup` no longer takes `confirmAccountWipe`. Send `confirmedAccountCount` with the current number of accounts, or the call answers 409.
2. On operation and notification events, `entity` is now `target`, `blockedByEntity` is now `blockedByTarget`, and the new `scheduleKey` field identifies the schedule. Queue replies keep `Status` and drop `Queued` and `AlreadyRunning`.
3. Status values use the American spelling: `cancelled` is now `Canceled`.
4. The schedule edit-session routes and `POST persistent/prefill` are gone.
5. Without a session, `/api/system/config` and `/api/system/setup` no longer return database details or folder paths.

**If you use the Unraid template:** the guest session length and metrics sign-in settings now default to empty, which means the built-in defaults (6 hours, and `/metrics` open). Existing containers keep their saved values.

**If `Security__RequireAuthForMetrics` is set but empty:** `/metrics` used to answer 500. It is now open, the documented default. Set it to `true` to require the API key.

**If your API key dates from before October 2025:** regenerate it. A copy of an old key sat in the project's public git history.

**Rolling back to 1.10.6 is not supported.** Two of this release's database migrations cannot be undone. Games on Disk and Services on Disk show their old totals until game detection runs once.

## Highlights

- **A rebuilt Users page.** One list with search and a filter, and one detail pane for whatever you pick. Guest access and Sign-in are pinned at the top. On a phone the list is the screen and a tap opens the details. Session preferences are rows in the pane, and Guest access saves with one Save button that stays on screen.
- **Single sign-on and sign-in modes.** Sign in with Google, GitHub, Microsoft, Apple or any OpenID Connect provider. It works over plain HTTP and with self-signed certificates. Pick Password, API key and password, API key and OIDC, OIDC only, or Unauthenticated in the app, or fix the mode with `Security__SignInMode`.
- **Cancel works on every job.** The first X cancels and a second X force-stops after a confirm. A job stopped partway repairs its cache data on the same card, and the repair finishes even across a restart. Cards turn green when everything worked, amber when some of it did, and red when it failed, and they name what was left undone.
- **Scheduled prefill grew up.** Run All starts every service at once, each with its own card and Cancel. Keep several named schedules per platform, and let them reuse the login that is already connected. Daemons recover by themselves when the internet comes back, and Steam signs in again from the saved login.
- **Notifications rebuilt.** The server keeps one list of runs, so duplicate and stuck cards are gone. Jobs asked for while something else is busy wait their turn instead of being dropped. The waiting card names what it is waiting for, in your language.
- **Lower memory.** The dashboard no longer keeps copies of the whole Downloads table (issue #30). Downloads now page on the server, and scans no longer hold every cached file path.

## Also new

- **Services on Disk**, a dashboard card beside Games on Disk, so games, services and unmapped space add up to the cache total.
- **Cache Settings**, a new card for the cache slice size. Auto or manual, with mixed slice sizes detected.
- **Client exclusion rules choose what each rule hides**, including keeping prefill traffic out of your stats and `/metrics`.
- **Steam depot mapping sources combine.** GitHub, Full and the new Hybrid mode add and correct mappings without deleting what another source found.
- **New prefill products:** World of Warcraft: Forever, Burning Crusade Anniversary, DOOM: The Dark Ages, and Teamfight Tactics as its own Riot product.
- **Schedule History** lists every scheduled run with search filters, and shows why a run failed.
- **Clients behind a reverse proxy** are counted under their real address.
- **LANCache JSON logs** are read as well as the text format.
- **Failed Xbox sign-ins say why:** no Xbox profile, region, age check or child account.
- **Reset a forgotten main admin password** from the setup screen or with the bundled script.
- **Zoom the bandwidth chart** with Ctrl+scroll or a pinch.
- **A rebuilt theme creator and editor**, with a live preview and working undo.
- **Client hostnames** are looked up through more of your DNS servers.

## Security

- **Guests and visitors without a session no longer see database or folder details.**
- **A user can change only their own account and sessions.**
- **Saved prefill logins are encrypted**, and guests cannot use them.
- The Steam Web API key can no longer be read back once saved.
- Regenerating the API key also clears the saved Epic and Xbox sign-ins.

## Fixes

**Cache and removals**

- Removing evicted content checks the disk first, so a stale flag can no longer delete files or leave orphans.
- A removal's confirm button waits for the real file count, and removals reach files in linked cache folders.
- A Steam full scan no longer wipes the game names of the other services.
- Cache scans wait while a download is writing, instead of counting half-written files.
- Depots that showed as "Unknown Game" show their names.

**Prefill and sign-in**

- A failed prefill run says failed, with the reason, instead of "already cached".
- Progress no longer sticks at 1%.
- Sign-in dialogs close on the server's answer, and an expired code says expired.
- Slow Docker at startup no longer removes containers or erases saved logins.
- Canceling an Xbox download answers quickly.

**Dashboard and pages**

- Pages recover after a dropped connection instead of staying stale until a reload.
- Banners update without a reload and fade in as they load.
- Database Management clears everything it lists, including the Xbox tables.
- One bad row no longer empties your operation history.

**Interface**

- Dark themes drop their shadows, and neutral buttons in light themes are visible before you hover.
- Buttons keep their size while they save, and dialogs no longer flicker.
- Server messages and sign-in errors are translated.

**Full changelog:** https://github.com/regix1/lancache-manager/compare/v1.10.6...v1.10.7

Thanks for using LANCache Manager!
