# API Reference

Everything the web UI does, it does by calling LancacheManager's own HTTP API. That API is available to you as well, so anything you can do by clicking, you can script: trigger a prefill before a LAN party, pull cache figures into your own dashboard, kick off log processing from cron, or clear a service's cache from a shell.

There are 273 endpoints across six groups. This page covers how to reach them, and links a plain-text summary of all of them that you can hand to an AI assistant.

## Hand the whole API to an AI assistant

[Download api-reference.txt](api-reference.txt){ download } (about 84 KB)

Paste that file into a chat with Claude, ChatGPT, or whatever you use, and ask for what you want. It lists every endpoint with its method, path, whether it needs an API key, what it does, and the shape of its request and response, so the assistant can write a working script without guessing at the API or inventing endpoints that do not exist.

A prompt like this is usually enough:

```text
Attached is the API reference for LancacheManager, a LANCache monitoring tool.
My instance is at http://cache.lan:8080 and my API key is in the environment
variable LANCACHE_KEY. Write me a bash script that starts a Steam prefill for
a list of app IDs and waits for it to finish.
```

The file is generated from the running application, so it matches the app rather than describing an intended design. It is regenerated when the API changes, and it carries the version it came from at the top.

## Browse it interactively

A running instance serves an interactive reference at **`/scalar`** - for the Docker image that is `http://<host>:8080/scalar`. Every endpoint is listed with its request and response shapes, and each one has a **Test Request** button that calls your own instance for real.

`/scalar` is admin-only, so open it in a browser tab where you are already signed in to the app, or paste your API key into the **Authentication** panel at the top of the page. The value shown in that field before you type is placeholder text, not a working key.

The raw OpenAPI document behind it is at **`/openapi/v1.json`**, also admin-only. Point Postman, Insomnia, or a client generator at that if you want typed bindings.

## Authenticating

Send your key as an `X-Api-Key` header:

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache
```

Get the key from the container:

```bash
docker exec lancache-manager cat /data/security/api_key.txt
```

It is also shown under **Management → Integrations** in the app, where you can regenerate it if it leaks.

!!! warning "Most endpoints require the key, and the ones that do not are deliberate"

    Anything that reads your data or changes your installation needs the key. A request without it gets `401`, including from a browser.

    A handful of endpoints answer without one, because they have to work before a caller has any credentials: signing in, reading guest-mode configuration, the first-run setup calls, and the health probe the container uses. Those are marked **public** in the downloadable reference.

    The app's own pages authenticate with a session cookie instead of the header, which is why the UI works without you pasting a key into every request. Scripts should send the header.

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

Current cache size and contents:

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache
```

Everything the dashboard shows, in one request:

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/dashboard/batch
```

Whether the app has finished its first-run setup, which needs no key:

```bash
curl http://cache.lan:8080/api/system/setup
```

Long jobs such as prefills, cache scans and log processing return immediately with an operation id rather than blocking. Poll the matching status endpoint, or subscribe to the SignalR hubs the UI uses if you want push updates.

!!! note "The API follows the app, and is not separately versioned"

    Endpoints are added, and occasionally removed when a feature is replaced. There is no long-term compatibility promise and no `/v2`, so pin the image tag you script against and re-download the reference after an upgrade.

## Regenerating the reference

For maintainers. With an instance running and an admin key to hand:

```bash
node docs-site/generate-api-reference.mjs --key "$LANCACHE_KEY" --url http://localhost:5000
```

That rewrites `docs-site/assets/api-reference.txt` from the live OpenAPI document. Nothing runs it automatically, so it needs re-running whenever endpoints change, and the result should be committed.
