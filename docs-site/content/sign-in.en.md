# Access and sign-in { #sign-in }

Version 1.10.7 introduces more sign-in choices. Confirm access for this installation once; your accounts, cache, and prefill settings are kept. Change it later at **Users → Accounts → Access and sign-in**.

## How should people sign in?

**Local account**

**Username and password**

Sign in with a local account. No API key entry at sign-in.

Use a unique password and keep it private.

**API key, username and password**

Require the installation key as well as a local account.

The installation key is shared. Keep both the key and your password private.

**Single sign-on**

**API key and single sign-on**

Sign in with an approved SSO account and the installation API key.

The key is shared; keep it private.

HTTPS required

**Single sign-on**

Sign in with Google, GitHub, Microsoft, Apple, or another OpenID Connect service. No local password or API key entry.

Only allowed accounts can sign in. Keep server access available for recovery.

HTTPS required

**No sign-in**

**Unauthenticated**

Open the app without a username, password, or API key.

Anyone who can reach the app can use its management features. Use only on a network where every connected person and device is trusted.

Every mode retains the installation API key for ownership and recovery; it belongs to the first account, the primary administrator.

Until a mode is saved, `Security__EnableAuthentication` selects the default. `true` (the built-in default) is **API key, username and password**. `false` is **Unauthenticated**. A saved mode is not replaced by that variable.

Local HTTP is supported. Credentials and sessions travel unencrypted, so use HTTPS on networks you do not trust.

Single sign-on is unavailable over HTTP. Connect to this app using HTTPS.

!!! note "Regenerate API Key"
    **Regenerate API Key** replaces the installation API key, and the previous key stops working.

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

Setup shows **Sign-in callback URL** and **Test callback URL**. Copy both into the provider's application registration. These return URLs are built in. Open the app at its intended sign-in address before copying them.

**Test connection.** The account used to test this connection is linked to the primary administrator automatically. Your connection is tested before activation. A failed test keeps the current sign-in settings.

Additional people are allowed by subject identifier, not by email address or display name. GitHub expects numeric account IDs. Microsoft expects `tenant GUID:object GUID`.

### Callback addresses

Single sign-on needs HTTPS. The setup screen requires it even on localhost. A name that resolves only on your LAN or VPN is enough; public hosting is not required. For providers other than Apple, the server accepts an HTTP address only when the host is localhost or a loopback address, and the setup screen still requires HTTPS. Apple requires an HTTPS domain and rejects localhost and any IP address.

## Switching modes

Existing account sessions remain active. Use Sessions on the Users page to end them. Switching to a sign-in mode removes shared unauthenticated access. An account created by single sign-on has no password yet, so set one before **Username and password** or **API key, username and password**.

If single sign-on locks you out, run this on the host:

```bash
./data/scripts/reset-main-admin-password.sh
```

The script restarts the app, which opens the hour, then sends the installation API key and the host recovery token. Enter the **Username**, **Password**, **Confirm Password**, and **API key**. Detail is in [Password Recovery](password-recovery.md).
