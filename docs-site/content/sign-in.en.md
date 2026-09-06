# Sign-in and Access { #sign-in }

On first run you pick how people get in. Upgrading installs confirm the choice once. Change it later at **Users → Accounts → Access and sign-in**.

## The five modes

| Mode | Users enter | Good for |
|---|---|---|
| Username and password | Password | Most setups |
| API key + password | Key, then password | Extra gate on the login screen |
| API key + single sign-on | Key, then SSO | Same, with an external provider |
| Single sign-on | SSO only | Google, GitHub, Microsoft, Apple, or OIDC |
| Unauthenticated | Nothing | Trusted LAN only |

Unauthenticated access gives management rights to anyone who can reach the app.

!!! note "The API key never stops working"
    It belongs to the first account, the primary administrator, and is always valid for ownership and recovery. Only the two API-key modes ask for it at login.

    ```bash
    docker exec lancache-manager cat /data/security/api_key.txt
    ```

The `Security__EnableAuthentication` compose variable no longer overrides a saved choice. HTTP is fine for password and API-key modes on a trusted network; use HTTPS anywhere else so credentials and session cookies are not sent in the clear.

## Single sign-on

There is no central login service. You register an application with the provider and paste its credentials in.

| Provider | Needs |
|---|---|
| Google, GitHub | Client ID, client secret |
| Microsoft | Client ID, client secret, tenant ID (or `consumers` for personal accounts) |
| Apple | Services ID, Team ID, Key ID, `.p8` key |
| Custom OIDC | Client ID, client secret, issuer URL |

Microsoft's tenant-independent `common` and `organizations` values are not supported. Apple requires a registered HTTPS domain and rejects localhost and IP addresses.

Setup shows two callback URLs, one for everyday sign-in and one for testing. Copy both into the provider's application registration; you never open them yourself. Open setup at the address people will actually use to reach the app, because that address is what the callbacks are built from.

Then run **Test connection**, which performs a real sign-in. Passing it links your verified identity to the primary administrator and activates the settings. Failing it changes nothing, so a working setup cannot be broken by a bad attempt. You can have several providers active at once.

Additional users are allowed by their stable identity identifier, not by email address or display name.

### Callback addresses

[Google allows HTTP on localhost and loopback](https://developers.google.com/identity/protocols/oauth2/web-server#uri-validation), so `http://localhost:8080` works when the browser and the app are on the same machine. It does not work from another device. For sign-in from elsewhere use an HTTPS domain rather than a LAN IP address. That domain can point at a server reachable only on your LAN or VPN, so HTTPS does not mean public hosting.

## Switching modes

Existing sessions survive a change; end them at **Users → Sessions**. Shared unauthenticated access stops the moment you pick a sign-in mode. An administrator created through SSO has to set local credentials before you can switch to a password mode.

If SSO breaks and locks you out, run the recovery script on the host:

```bash
./data/scripts/reset-main-admin-password.sh
```

It uses a separate server-held token, opens the recovery screen, and leaves you signed in as the primary administrator so you can repair the sign-in method. Full detail in [Password Recovery](password-recovery.md).
