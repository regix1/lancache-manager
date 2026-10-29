# Access and sign-in { #sign-in }

On first run you choose how people get in. An upgraded install asks you to confirm the choice once; your accounts, cache, and prefill settings are kept. Change it later at **Users → Accounts → Access and sign-in**.

## The five modes

| Mode | People enter | Good for |
|---|---|---|
| Username and password | Username and password | Most setups |
| API key, username and password | API key, then username and password | An extra check on the sign-in screen |
| API key and single sign-on | API key, then a single sign-on account | The same, with an outside provider |
| Single sign-on | A single sign-on account | Google, GitHub, Microsoft, Apple, or OpenID Connect |
| Unauthenticated | Nothing | A trusted LAN only |

- **Unauthenticated** lets anyone who can reach the app use its management features.
- The two single sign-on modes need HTTPS.
- Password and API key modes work over plain HTTP. Use HTTPS on any network you do not trust, so passwords and session cookies are not sent in the clear.

### Default before you choose

Until a mode is saved, the `Security__EnableAuthentication` compose variable picks the default:

| Value | Default mode |
|---|---|
| `true` (built-in default) | API key, username and password |
| `false` | Unauthenticated |

Once you save a mode, the variable no longer changes it.

!!! note "The API key always works"
    The key belongs to the first account, the primary administrator. It stays valid for ownership and recovery in every mode. Only the two API key modes ask for it at sign-in.

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
| Other (OpenID Connect) | HTTPS issuer URL, client ID, and client secret |

- **Microsoft:** use the tenant GUID, or `consumers` for personal accounts. The values `common` and `organizations` are rejected.
- **Apple:** the app creates Apple's client secret for you. Apple needs an HTTPS domain and rejects localhost and IP addresses.

### Set it up

1. Open the app at the address people will use to reach it. The callback URLs are built from that address.
2. Copy the **Sign-in callback URL** and the **Test callback URL** into the provider's application registration. You never open them yourself.
3. Click **Test connection**. It runs a real sign-in.
    - If it passes, the account you signed in with is linked to the primary administrator and the settings go live.
    - If it fails, nothing changes, so a working setup is never broken by a bad attempt.

### Allowing other people

You allow people by their account ID from the provider, not by email address or display name.

| Provider | ID format |
|---|---|
| GitHub | Numeric account ID |
| Microsoft | `tenant GUID:object GUID` |

### Callback addresses

- Single sign-on needs HTTPS, even on localhost.
- The domain does not have to be public. A name that only resolves on your LAN or VPN is enough.

## Switching modes

- Existing sessions stay signed in. End them at **Users → Sessions**.
- Picking any sign-in mode ends shared unauthenticated access right away.
- An account created through single sign-on has no password yet. Set one before you switch to a password mode.

### Locked out by single sign-on

Run the recovery script on the host:

```bash
./data/scripts/reset-main-admin-password.sh
```

It opens a one-hour recovery window. In the browser, enter a **Username**, **Password**, **Confirm Password**, and the **API key**. You are then signed in as the primary administrator and can fix the sign-in method. Full steps are in [Password Recovery](password-recovery.md).
