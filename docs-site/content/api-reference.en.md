# API Reference

The web UI runs entirely on LANCache Manager's own HTTP API, so anything you can click, you can script. There are 283 endpoints across six groups.

## Download the endpoint list

[Download api-reference.txt](api-reference.txt){ download } (about 88 KB)

Every endpoint with its method, path, auth requirement, purpose, and request and response shapes. It is generated from the running app, so it matches the code rather than an intended design.

## Browse it interactively

A running instance serves an interactive reference at **`/scalar`**, on whatever port you published. If your compose file maps `8081:80`, that is `http://<host>:8081/scalar`. Each endpoint can call your instance for real.

`/scalar` requires a signed-in account. Signed in to the app already, it loads straight away, and those calls run on that same session, so there is nothing to fill in. If you are not signed in, it sends you to the sign-in screen.

The API key field at the top of the page takes the API key, which is what opens the reference and the document behind it. The greyed-out value in that field is placeholder text, not a working key.

The raw OpenAPI document is at **`/openapi/v1.json`**, and also requires a signed-in account. Point Postman, Insomnia, or a client generator at it.

!!! warning "`/swagger` and `/scaler` do not work"

    This app uses Scalar, so there is no Swagger UI, and `/scaler` is a misspelling. Neither errors: unrecognised paths go to the single-page app, so you land on the dashboard and it looks as though the docs vanished. Check the spelling first.

## Authenticating

Scripts sign in once and send the session cookie with every request after that. The API key on its own does not sign a script in.

!!! warning "Changed in v1.10.6: `X-Api-Key` alone returns `401` on most routes"
    Before v1.10.6, sending `X-Api-Key` signed a request in as an administrator on every endpoint. A script that still polls `/api/cache` or `/api/dashboard/batch` with only that header gets `401` after the upgrade. Switch it to the sign-in steps below.

### Where the API key still works

| Route | How the key is sent |
|---|---|
| `GET /scalar` and `GET /openapi/v1.json` | `X-Api-Key` header |
| `/metrics`, when `Security:RequireAuthForMetrics` is on | `X-Api-Key` header. Prometheus scrapers need no change. |
| `POST /api/setup/credentials`, `POST /api/setup/external` | `X-Api-Key` header |
| `POST /api/account-setup/first-admin`, `POST /api/account-setup/open-main-admin-recovery`, `POST /api/account-setup/recover-main-admin` | `apiKey` field in the request body |
| Every other route | Not accepted. The route answers `401`. |

The setup and recovery calls keep the key because they must work before any account can sign in.

### Why the key no longer signs scripts in

- The key is one shared secret for the whole install, and it appears in the container log when it is created or rotated.
- A request that carried only the key left no session, so the app had no record of who made it.
- A leaked key could not be taken back from one caller. The only fix was to regenerate it, which affects every user.
- A signed-in session is listed under **Users → Sessions** and can be ended there on its own.

### Signing in from a script

What the sign-in call needs depends on the access mode (see [Access and sign-in](sign-in.md)):

| Access mode | Body for `POST /api/auth/login` |
|---|---|
| Username and password | `username`, `password` |
| API key, username and password | `apiKey`, `username`, `password` |
| Unauthenticated | `username`, `password` of the primary administrator only |
| API key and single sign-on | Not available. The call is refused. |
| Single sign-on | Not available. The call is refused. |

In the two single sign-on modes, a script has no way to sign in.

The steps, for **API key, username and password**:

1. Request an antiforgery token. Signing in changes state, so it needs one.
2. Sign in and save the cookies to a file.
3. Send that cookie file with every later request.

```bash
# 1. Antiforgery token
curl -c jar.txt http://cache.lan:8080/api/auth/status

# 2. Sign in. In Username and password mode, leave out apiKey.
curl -b jar.txt -c jar.txt -X POST http://cache.lan:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -H "X-Antiforgery-Token: $(awk '/LancacheManager.Antiforgery/ {print $7}' jar.txt)" \
  -d "{\"apiKey\":\"$LANCACHE_KEY\",\"username\":\"operator\",\"password\":\"$LANCACHE_PASSWORD\"}"

# 3. Read with the cookie file
curl -b jar.txt http://cache.lan:8080/api/dashboard/batch
```

Replace `operator`, `$LANCACHE_KEY`, and `$LANCACHE_PASSWORD` with an account created in the app and its password. The last command returns the dashboard data as JSON.

### Requests that change something

Reads need only the cookie file. `POST`, `PUT`, `PATCH`, and `DELETE` also need an antiforgery token:

1. Call `GET /api/auth/status` with the same cookie file.
2. Take the value of the `LancacheManager.Antiforgery` cookie it sets.
3. Send that value in an `X-Antiforgery-Token` header.

The token belongs to one session, and signing in starts a new session. Request a fresh token after you sign in, before the first write.

### Getting the key

```bash
docker exec lancache-manager cat /data/security/api_key.txt
```

- **Regenerate API Key** under **Management → Settings** replaces the key. The old key stops working.
- The full key appears in the container log only when it is created or rotated. Later starts print only a hint.
- If the primary administrator password is lost, run `./data/scripts/reset-main-admin-password.sh` on the host. See [Password Recovery](password-recovery.md).

!!! note "The endpoints that answer without a session"

    Twenty-three of the 283 have to work before a caller has credentials: signing in, guest-mode configuration, first-run setup, password recovery, game artwork, the version banner, and the container health probe. They are marked **public** in the downloadable reference, and the rest are marked **requires a signed-in session**.

## What is in each group

| Group | Endpoints | What it covers |
|---|---:|---|
| Access | 65 | Signing in, sessions, accounts, API keys, guest mode, per-user settings |
| Cache and Games | 58 | Cached content, game and depot identification, corruption scans, artwork |
| Clients | 10 | Cache clients, client groups, hostname mappings |
| Downloads and Reporting | 40 | Download history, dashboard figures, statistics, speeds, events, logs |
| Prefill | 62 | Steam, Epic, Battle.net, Riot and Xbox prefill daemons and their schedules |
| System | 48 | Service health, metrics, database maintenance, migrations, background operations |

## A few calls to start with

```bash
# Cache size and contents
curl -b jar.txt http://cache.lan:8080/api/cache

# Everything the dashboard shows, in one request
curl -b jar.txt http://cache.lan:8080/api/dashboard/batch

# First-run setup state, no session needed
curl http://cache.lan:8080/api/system/setup
```

Prefills, cache scans and log processing return an operation id immediately instead of blocking. Poll the matching status endpoint, or subscribe to the SignalR hubs the UI uses.

!!! note "The API is not separately versioned"

    Endpoints get added, and removed when a feature is replaced. There is no `/v2` and no compatibility promise, so pin the image tag you script against and re-download the reference after an upgrade.

## Regenerating the reference

For maintainers, with an instance running:

```bash
node docs-site/generate-api-reference.mjs --key "$LANCACHE_KEY" --url http://localhost:5000
```

That rewrites `docs-site/assets/api-reference.txt`. Nothing runs it automatically, so re-run it whenever endpoints change and commit the result.
