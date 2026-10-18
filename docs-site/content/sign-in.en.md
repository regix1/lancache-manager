# Sign-in and Access { #sign-in }

On first run you choose how people sign in. Version 1.10.7 asks you to confirm that choice once; accounts, cache, and prefill settings are kept. Change it later at **Users → Accounts → Access and sign-in**.

## The five modes

| Mode | What people enter | What the app does |
|---|---|---|
| Username and password | Username and password | Local account. No API key on the sign-in screen |
| API key, username and password | Installation API key, username, and password | The installation key and a local account |
| API key and single sign-on | Installation API key, then a connected service | An approved single sign-on account and the installation API key |
| Single sign-on | A connected service | Google, GitHub, Microsoft, Apple, or another OpenID Connect service. No local password and no API key on the sign-in screen |
| Unauthenticated | Nothing | Anyone who can reach the app can use its management features. Use only where every connected person and device is trusted |

Until a mode is saved, `Security__EnableAuthentication` selects the default. `true` (the built-in default) is **API key, username and password**. `false` is **Unauthenticated**. A saved mode is not replaced by that variable.

**Username and password** and **API key, username and password** can use HTTP. Credentials and sessions are unencrypted, so use HTTPS on a network you do not trust. The setup screen will not select a single sign-on mode over HTTP.

!!! note "The installation API key stays with the primary administrator"
    Every mode keeps the installation API key for ownership and recovery. It belongs to the first account, the primary administrator. Only **API key, username and password** and **API key and single sign-on** ask for it at sign-in. **Regenerate API Key** replaces it, and the previous key stops working.

    ```bash
    docker exec lancache-manager cat /data/security/api_key.txt
    ```

## Single sign-on

There is no central login service. Register an application with the provider and paste its credentials in. Google, GitHub, Microsoft, Apple, and Other (OpenID Connect) can be connected together. Each kind is stored once; saving it again updates that service.

| Provider | Needs |
|---|---|
| Google | Client ID and client secret |
| GitHub | Client ID and client secret |
| Microsoft | Application (client) ID, client secret, and directory (tenant) ID. Use the tenant GUID, or `consumers` for personal accounts. `common` and `organizations` are rejected |
| Apple | Services ID, Team ID, Key ID, and the `.p8` key. The app creates Apple's client secret. An HTTPS domain is required; localhost and IP addresses are rejected |
| Other (OpenID Connect) | HTTPS issuer URL, client ID, and client secret |

Setup shows **Sign-in callback URL** and **Test callback URL**. Copy both into the provider's application registration. Open setup at the address people will use, because the callbacks are built from that address.

**Test connection** performs a real sign-in. The account used for the test is linked to the primary administrator, and the settings become active when the test succeeds. A failed test leaves the current sign-in settings unchanged.

Additional people are allowed by subject identifier, not by email address or display name. GitHub expects numeric account IDs. Microsoft expects `tenant GUID:object GUID`.

### Callback addresses

Single sign-on needs HTTPS. The setup screen requires it even on localhost. A name that resolves only on your LAN or VPN is enough; public hosting is not required. For providers other than Apple, the server accepts an HTTP address only when the host is localhost or a loopback address, and the setup screen still requires HTTPS. Apple requires an HTTPS domain and rejects localhost and any IP address.

## Switching modes

Existing account sessions stay active. End them at **Users → Sessions**. Switching to a sign-in mode removes shared unauthenticated access. An administrator created by single sign-on has no password yet. Set one before switching to **Username and password** or **API key, username and password**.

If single sign-on locks you out, run this on the host:

```bash
./data/scripts/reset-main-admin-password.sh
```

The script sends the installation API key and the host recovery token, then opens a one-hour window. The app asks for the API key, the primary administrator username, and a new password. Detail is in [Password Recovery](password-recovery.md).
