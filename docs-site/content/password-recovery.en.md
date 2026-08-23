# Reset a Lost Admin Password { #password-recovery }

Use this procedure when you cannot sign in because the **main administrator** password was forgotten.

This reset:

- keeps the account, settings, downloads, and database;
- changes only the main administrator's password;
- signs that administrator out of every existing session;
- clears a sign-in lockout caused by failed attempts.

It cannot reset a secondary administrator or regular user.

## What you need

Run the commands on the machine that runs LANCache Manager. You need:

1. the main administrator's username;
2. access to Docker on the host;
3. `curl` and `jq`.

Check the required commands before starting:

```bash
docker compose version
curl --version
jq --version
```

If `curl` or `jq` says `command not found`, install it with the package manager for your operating system before continuing.

## Docker Compose: step by step

### 1. Open the Compose folder

Change into the folder that contains the `docker-compose.yml` used to run LANCache Manager:

```bash
cd /path/to/your/lancache-manager-folder
```

Confirm that Compose can see the service:

```bash
docker compose config --services
```

The output should include:

```text
lancache-manager
```

### 2. Restart LANCache Manager

```bash
docker compose restart lancache-manager
```

The password-reset endpoint is available for **one hour after the restart**. Restarting does not delete data.

### 3. Read the API key

```bash
LCM_API_KEY="$(docker compose exec -T lancache-manager cat /data/security/api_key.txt)"
```

The key is stored in the `LCM_API_KEY` shell variable. The command intentionally prints nothing.

### 4. Set the address of the app

The supplied Compose file publishes LANCache Manager on port `8080`:

```bash
LCM_URL="http://127.0.0.1:8080"
```

If your Compose file maps another port, replace `8080` with that host port. For example, a mapping of `9090:80` uses `http://127.0.0.1:9090`.

Wait for the restarted app to become ready:

```bash
until curl --fail --silent "$LCM_URL/health" >/dev/null; do sleep 2; done
```

This command finishes silently when the app is ready. Press `Ctrl+C` if it keeps waiting; that usually means `LCM_URL` has the wrong port.

### 5. Enter the username and new password

```bash
read -r -p "Main administrator username: " LCM_USERNAME
read -r -s -p "New password: " LCM_PASSWORD
printf '\n'
```

Nothing appears while you type the password. That is normal.

The new password must:

- be 12 to 256 characters long;
- use at least three of these four groups: lowercase letters, uppercase letters, digits, and other characters;
- not be the same as the username.

For example, `FreshPassword2026` has lowercase letters, uppercase letters, and digits. Do not use that example as your real password.

### 6. Send the reset request

Copy and run this block without changing the endpoint:

```bash
jq -n \
  --arg apiKey "$LCM_API_KEY" \
  --arg username "$LCM_USERNAME" \
  --arg password "$LCM_PASSWORD" \
  '{apiKey: $apiKey, username: $username, password: $password}' |
curl --fail-with-body --silent --show-error \
  -X POST "$LCM_URL/api/account-setup/recover-main-admin" \
  -H 'Content-Type: application/json' \
  --data-binary @-
```

`jq` safely creates the JSON request. `curl` sends it to LANCache Manager.

A successful reset prints:

```json
{"success":true,"message":"Password reset"}
```

Clear the temporary shell variables:

```bash
unset LCM_API_KEY LCM_USERNAME LCM_PASSWORD
```

You can now sign in with the new password. The old password no longer works.

## Docker without Compose or Unraid

Follow steps 3 through 6 above, but restart and read the key with the container name:

```bash
docker restart lancache-manager
LCM_API_KEY="$(docker exec lancache-manager cat /data/security/api_key.txt)"
```

Replace `lancache-manager` if your container uses another name. Set `LCM_URL` to the host port assigned to that container.

On Windows Git Bash, read the key with:

```bash
LCM_API_KEY="$(MSYS_NO_PATHCONV=1 docker exec lancache-manager cat /data/security/api_key.txt)"
```

## Bare-metal or source installation

1. Restart the LANCache Manager process or system service.
2. Read `security/api_key.txt` inside the application's data directory.
3. Store that value in `LCM_API_KEY`.
4. Set `LCM_URL` to the address where the API listens.
5. Follow steps 5 and 6 above.

## Fix a failed request

### `401` or `apiKeyRequired`

Read the current key from `/data/security/api_key.txt` again. The key must be inside the JSON request. Adding an `X-Api-Key` header does not work for password recovery.

### `403` or `recoveryWindowClosed`

More than one hour has passed since the app started. Restart LANCache Manager and send the request again.

### `404` or `mainAdminNotFound`

The username does not belong to the main administrator. Check the username and try again. This endpoint cannot reset another account.

### `400`

The username or password failed the rules shown in step 5. The response body states which rule failed.

### `429`

Too many recovery attempts were sent from the same address. Wait before trying again.

!!! danger "Do not delete recovery files"

    Do not delete the database or `/data/security/api_key.txt` to recover a password. Deleting the API key only creates a different installation key; it does not reset any account password.
