# Access and sign-in { #sign-in }

On first run you choose how users sign in. An upgraded install asks you to confirm the choice once; your accounts, cache, and prefill settings are kept, unless you choose Unauthenticated, which deletes every account. Change it later at **Users → Sign-in → Change**.

## The five modes

| Mode | Users enter | Good for |
|---|---|---|
| Username and password | Username and password | Most setups |
| API key, username and password | API key, then username and password | An extra check on the sign-in screen |
| API key and single sign-on | API key, then a single sign-on account | The same, with an outside provider |
| Single sign-on | A single sign-on account | Google, GitHub, Microsoft, Apple, or OpenID Connect |
| Unauthenticated | Nothing | A trusted LAN only |

- **Unauthenticated** lets anyone who can reach the app use its management features.
- **Switching to Unauthenticated deletes every account.** The app first asks you to confirm and shows how many accounts it will delete. To turn sign-in back on, you create a new admin account.
- Single sign-on works over plain HTTP with an OpenID Connect provider or GitHub that accepts an `http://` callback address. Google and Microsoft need HTTPS except on localhost, and Apple needs an HTTPS domain.
- Password and API key modes work over plain HTTP. Use HTTPS on any network you do not trust, so passwords and session cookies are not sent in the clear.

### Set the mode from the environment

Set `Security__SignInMode` in the Compose file to fix the mode. The mode picker is then read-only, and first-run setup works like this:

| `Security__SignInMode` | Access step of first-run setup |
|---|---|
| `Password` or `ApiKeyPassword` | Skipped. The database and first-account steps still run. |
| `Unauthenticated` | Skipped. Each start deletes every account, including one an environment login allows. No first account is created. If sign-in is turned back on while that login's variables are still set, that user's next sign-in creates the account again. |
| `Oidc` or `ApiKeyOidc` | Shown read-only. The owner signs in there through the environment login. |

If you remove the variables later, the access step comes back. The values and names are in [Sign-in from environment variables](configuration-reference.md#sign-in-environment).

### Default before you choose

Without `Security__SignInMode`, and until a mode is saved, the `Security__EnableAuthentication` compose variable picks the default:

| Value | Default mode |
|---|---|
| `true` (built-in default) | API key, username and password |
| `false` | Unauthenticated |

Once you save a mode, the variable no longer changes it.

!!! note "The API key always works"
    The key belongs to the primary administrator, or to whoever runs the installation while sign-in is off. It stays valid for ownership and recovery in every mode. Only the two API key modes ask for it at sign-in.

    ```bash
    docker exec lancache-manager cat /data/security/api_key.txt
    ```

    **Regenerate API Key** replaces the key. The old key stops working at once.

## Single sign-on

There is no central sign-in service. You register an application with each provider and paste its details into the app. You can connect several providers at once.

| Provider | You need |
|---|---|
| Google | Client ID and client secret |
| GitHub | Client ID and client secret |
| Microsoft | Application (client) ID, client secret, and directory (tenant) ID |
| Apple | Services ID, Team ID, Key ID, and the `.p8` key |
| Other (OpenID Connect) | HTTP or HTTPS issuer URL, client ID, and client secret |

- **Microsoft:** use the tenant GUID, or `consumers` for personal accounts. The values `common` and `organizations` are rejected.
- **Apple:** the app creates Apple's client secret for you. Apple needs an HTTPS domain and rejects localhost and IP addresses.

### Set it up from the environment

Set the provider's fields as `Security__` variables instead of pasting them into the app. A field set by environment wins over the value saved in the UI and cannot be edited there. An `Oidc` or `ApiKeyOidc` mode with no valid login falls back to **API key, username and password**, never to **Unauthenticated**. The full list, including the `*File` form for secrets, is in [Sign-in from environment variables](configuration-reference.md#sign-in-environment).

### Set it up

1. Open the app at the address users will use to reach it. The callback URLs are built from that address.
2. Copy the **Sign-in callback URL** and the **Test callback URL** into the provider's application registration. You never open them yourself.
3. Click **Test connection**. It runs a real sign-in.
    - If it passes, the account you signed in with is linked to the primary administrator and the settings go live.
    - If it fails, nothing changes, so a working setup is never broken by a bad attempt.

### Allowing other users

You allow users by their account ID from the provider, not by email address or display name.

| Provider | ID format |
|---|---|
| GitHub | Numeric account ID |
| Microsoft | `tenant GUID:object GUID` |

### Callback addresses

- Over plain HTTP, the provider must accept an `http://` callback URL. Many self-hosted OpenID Connect providers do. Google and Microsoft accept one only on localhost, and Apple never does.
- Over plain HTTP, the sign-in code and the provider's keys cross your network unencrypted. Use HTTPS on any network you do not trust.
- The domain does not have to be public. A name that only resolves on your LAN or VPN is enough.
- A provider with a self-signed or private certificate: put the CA certificate that signed it, in PEM form with a `.crt` file name, in `/data/certs/`, then restart the container. The app adds it to the trusted roots, and the public roots stay trusted.
- Behind a reverse proxy that ends TLS, set `Security__ForceSecureCookies=true`, list the proxy in `Security__KnownProxyNetworks`, and pass the original `Host` header (`proxy_set_header Host $host;` in nginx). The callback URL is built from that host.

## Switching modes

- Switching between sign-in modes keeps existing sessions signed in. End one by picking it in the Users list and choosing **Revoke**. Switching to Unauthenticated deletes every account and its sessions.
- Picking any sign-in mode ends shared unauthenticated access right away.
- An account created through single sign-on has no password yet. Set one before you switch to a password mode.

### Locked out by single sign-on

If an environment login fell back to **API key, username and password** and the owner has no local password, follow the numbered steps in [Password Recovery](password-recovery.md#single-sign-on-owner).

Otherwise, run the recovery script on the host:

```bash
./data/scripts/reset-main-admin-password.sh
```

It opens a one-hour recovery window. In the browser, enter a **Username**, **Password**, **Confirm Password**, and the **API key**. You are then signed in as the primary administrator and can fix the sign-in method. Full steps are in [Password Recovery](password-recovery.md).
