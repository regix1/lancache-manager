# API Reference

The web UI runs entirely on LANCache Manager's own HTTP API, so anything you can click, you can script. There are 284 endpoints across six groups.

## Hand the whole API to an AI assistant

[Download api-reference.txt](api-reference.txt){ download } (about 88 KB)

Every endpoint with its method, path, auth requirement, purpose, and request and response shapes. Paste it into a chat and ask for what you want:

```text
Attached is the API reference for LANCache Manager, a LANCache monitoring tool.
My instance is at http://cache.lan:8080. My API key, username and password are
in the environment variables LANCACHE_KEY, LANCACHE_USER and LANCACHE_PASSWORD.
Write me a bash script that starts a Steam prefill for a list of app IDs and
waits for it to finish.
```

It is generated from the running app, so it matches the code rather than an intended design.

## Browse it interactively

A running instance serves an interactive reference at **`/scalar`**, on whatever port you published. If your compose file maps `8081:80`, that is `http://<host>:8081/scalar`. Each endpoint has a **Test Request** button that calls your instance for real.

`/scalar` is admin-only. Signed in to the app already, it loads straight away, and the **Test Request** buttons run on that same session, so there is nothing to fill in. If you are not signed in, it sends you to the sign-in screen.

The **Authentication** panel at the top of the page takes the API key, which is what opens the reference and the document behind it. The greyed-out value in that field is placeholder text, not a working key.

The raw OpenAPI document is at **`/openapi/v1.json`**, also admin-only. Point Postman, Insomnia, or a client generator at it.

!!! warning "`/swagger` and `/scaler` do not work"

    This app uses Scalar, so there is no Swagger UI, and `/scaler` is a misspelling. Neither errors: unrecognised paths go to the single-page app, so you land on the dashboard and it looks as though the docs vanished. Check the spelling first.

## Authenticating

Sign in once, keep the cookie jar, and use the jar for everything after that:

```bash
# Take an antiforgery token first. Signing in changes something, so it needs one too.
curl -c jar.txt http://cache.lan:8080/api/auth/status

# Sign in with the token out of the jar. All three fields are required.
curl -b jar.txt -c jar.txt -X POST http://cache.lan:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -H "X-Antiforgery-Token: $(awk '/LancacheManager.Antiforgery/ {print $7}' jar.txt)" \
  -d "{\"apiKey\":\"$LANCACHE_KEY\",\"username\":\"operator\",\"password\":\"$LANCACHE_PASSWORD\"}"

# Then read with the cookie jar.
curl -b jar.txt http://cache.lan:8080/api/dashboard/batch
```

Get the key with `docker exec lancache-manager cat /data/security/api_key.txt`, or from **Management → Integrations**, where you can also regenerate it. Later container logs print only a hint; the full key is written to the logs only when it is first created or rotated. The username and password are those of an account created in the app.

If the main administrator password is lost, use the host-only recovery procedure in [Password Recovery](password-recovery.md). It includes the complete `recover-main-admin` request; no signed-in session or antiforgery token is needed for that endpoint.

Reads need nothing more than the jar. A request that changes something (`POST`, `PUT`, `PATCH`, `DELETE`) also needs an antiforgery token: call `GET /api/auth/status` with the same jar, take the value of the `LancacheManager.Antiforgery` cookie it sets, and send it back as an `X-Antiforgery-Token` header. That is why the sign-in above starts with the status call. The token belongs to the session it was issued to, and signing in gives you a new one, so call the status endpoint again before your first write.

!!! warning "`X-Api-Key` on its own is not a way into the API"

    The header opens `/scalar` and `/openapi/v1.json`, and nothing else. Every other endpoint answers `401` to a request whose only credential is that header, so a script that polls something like `/api/cache` or `/api/dashboard/batch` with it stops working the moment you upgrade.

    Four setup calls still read the key themselves, because they have to answer before anyone can sign in: `POST /api/setup/credentials` and `POST /api/setup/external` take it in the `X-Api-Key` header, and `POST /api/account-setup/first-admin` and `POST /api/account-setup/recover-main-admin` take it in the request body.

    `/metrics` is unchanged as well. It has its own setting, `Security:RequireAuthForMetrics`, and when that is on it still takes the key in the header, so Prometheus scrapers need no change.

!!! note "The endpoints that answer without a session"

    Twenty-two of the 284 have to work before a caller has credentials: signing in, guest-mode configuration, first-run setup, game artwork, the version banner, and the container health probe. They are marked **public** in the downloadable reference, and the rest are marked **requires a signed-in session**.

## What is in each group

| Group | Endpoints | What it covers |
|---|---:|---|
| Access | 64 | Signing in, sessions, accounts, API keys, guest mode, per-user settings |
| Cache and Games | 58 | Cached content, game and depot identification, corruption scans, artwork |
| Clients | 10 | Cache clients, client groups, hostname mappings |
| Downloads and Reporting | 40 | Download history, dashboard figures, statistics, speeds, events, logs |
| Prefill | 64 | Steam, Epic, Battle.net, Riot and Xbox prefill daemons and their schedules |
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
