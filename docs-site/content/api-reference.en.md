# API Reference

The web UI runs entirely on LancacheManager's own HTTP API, so anything you can click, you can script. There are 273 endpoints across six groups.

## Hand the whole API to an AI assistant

[Download api-reference.txt](api-reference.txt){ download } (about 84 KB)

Every endpoint with its method, path, auth requirement, purpose, and request and response shapes. Paste it into a chat and ask for what you want:

```text
Attached is the API reference for LancacheManager, a LANCache monitoring tool.
My instance is at http://cache.lan:8080 and my API key is in the environment
variable LANCACHE_KEY. Write me a bash script that starts a Steam prefill for
a list of app IDs and waits for it to finish.
```

It is generated from the running app, so it matches the code rather than an intended design.

## Browse it interactively

A running instance serves an interactive reference at **`/scalar`**, on whatever port you published. If your compose file maps `8081:80`, that is `http://<host>:8081/scalar`. Each endpoint has a **Test Request** button that calls your instance for real.

`/scalar` is admin-only. Signed in to the app already, it loads straight away; if not, it sends you to the sign-in screen. You can also paste your key into the **Authentication** panel at the top of the page, where the greyed-out value is placeholder text, not a working key.

The raw OpenAPI document is at **`/openapi/v1.json`**, also admin-only. Point Postman, Insomnia, or a client generator at it.

!!! warning "`/swagger` and `/scaler` do not work"

    This app uses Scalar, so there is no Swagger UI, and `/scaler` is a misspelling. Neither errors: unrecognised paths go to the single-page app, so you land on the dashboard and it looks as though the docs vanished. Check the spelling first.

## Authenticating

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache
```

Get the key with `docker exec lancache-manager cat /data/security/api_key.txt`, or from **Management → Integrations**, where you can also regenerate it.

!!! warning "Most endpoints require the key"

    Anything that reads your data or changes your installation returns `401` without it, browsers included.

    The few that answer without one have to work before a caller has credentials: signing in, guest-mode configuration, first-run setup, and the container health probe. They are marked **public** in the downloadable reference.

    The app's own pages use a session cookie instead of the header. Scripts should send the header.

## What is in each group

| Group | Endpoints | What it covers |
|---|---:|---|
| Access | 53 | Signing in, sessions, API keys, guest mode, per-user settings |
| Cache and Games | 58 | Cached content, game and depot identification, corruption scans, artwork |
| Clients | 10 | Cache clients, client groups, hostname mappings |
| Downloads and Reporting | 40 | Download history, dashboard figures, statistics, speeds, events, logs |
| Prefill | 64 | Steam, Epic, Battle.net, Riot and Xbox prefill daemons and their schedules |
| System | 48 | Service health, metrics, database maintenance, migrations, background operations |

## A few calls to start with

```bash
# Cache size and contents
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache

# Everything the dashboard shows, in one request
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/dashboard/batch

# First-run setup state, no key needed
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
