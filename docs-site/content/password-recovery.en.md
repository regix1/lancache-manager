# Reset a Lost Admin Password { #password-recovery }

Use this procedure when the **main administrator** password was forgotten.

The reset:

- keeps the account, settings, downloads, and database;
- changes only the main administrator's password;
- signs that administrator out of every existing session;
- clears a sign-in lockout caused by failed attempts.

It cannot reset a secondary administrator or regular user.

## Docker Compose

LANCache Manager places a recovery script in the persistent data folder every time the container starts. With the supplied Compose file, run:

```bash
./data/scripts/reset-main-admin-password.sh
```

Username and password are optional. If you do not enter them in the command, the script prompts for them.

The script then:

1. restarts the LANCache Manager container to open the one-hour recovery window;
2. waits for the app to become ready;
3. reads the API key inside the container;
4. uses the username and password you passed, or prompts for any that are missing;
5. sends the reset request and reports the result.

The API key is never placed in the command. The host only needs Docker and an interactive terminal; `curl`, `jq`, and the app's published port are handled inside the container.

You can pass the username if you want:

```bash
./data/scripts/reset-main-admin-password.sh --username admin
```

The script always asks for the password at a prompt. It is never taken as an argument, because a command line is kept in shell history and is readable in the process list while the command runs. To use the script from another script, pipe the password in:

```bash
printf %s "$NEW_PASSWORD" | ./data/scripts/reset-main-admin-password.sh --username admin --password-stdin
```

The new password must:

- be 12 to 256 characters long;
- use at least three of these four groups: lowercase letters, uppercase letters, digits, and other characters;
- not be the same as the username.

## A different container name

The default container name is `lancache-manager`. Pass the actual name when it differs:

```bash
./data/scripts/reset-main-admin-password.sh --container my-lancache-manager
```

## Unraid or a custom data path

Open a host terminal and find the host folder mapped to `/data` in the container configuration. Run the script from its `scripts` subfolder:

```bash
/path/mapped/to/data/scripts/reset-main-admin-password.sh
```

Add `--container NAME` if the container is not named `lancache-manager`.

## The script is missing

The script is installed when a current container image starts. Pull and recreate the container, then run it:

```bash
docker compose pull
docker compose up -d
./data/scripts/reset-main-admin-password.sh
```

Recreating the container does not delete data stored in the `/data` mount.

If `/data` uses a named Docker volume instead of a host folder, restart and run the installed script inside the container:

```bash
docker restart lancache-manager
docker exec -it lancache-manager /data/scripts/reset-main-admin-password.sh
```

## Bare-metal or source installation

Restart LANCache Manager first to open the one-hour recovery window. Place the supplied `scripts/reset-main-admin-password.sh` in the data directory's `scripts` folder, then run:

```bash
/path/to/data/scripts/reset-main-admin-password.sh --local --url http://127.0.0.1:8080
```

Change the URL if the app listens elsewhere. Local mode requires `curl` and `jq` on that machine. `--username` and `--password-stdin` work the same way as they do for Docker.

## Fix a failed reset

### `401` or `apiKeyRequired`

The API key in the data directory could not authenticate the request. Confirm that the script is inside the same data directory the running app uses.

### `403` or `recoveryWindowClosed`

The one-hour recovery window is closed. Run the host-side script again so it restarts the container, or restart a bare-metal installation before using `--local`.

### `404` or `mainAdminNotFound`

The username does not belong to the main administrator. This recovery cannot reset another account.

### `400`

The username or password failed the rules shown above. The response states which rule failed.

### `429`

Too many recovery attempts were sent from the same address. Wait before trying again.

!!! danger "Do not delete recovery files"

    Do not delete the database or `/data/security/api_key.txt` to recover a password. Deleting the API key only creates a different installation key; it does not reset any account password.
