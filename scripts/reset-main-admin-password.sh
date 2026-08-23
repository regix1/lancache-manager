#!/bin/bash

set -Eeuo pipefail

container_name="lancache-manager"
local_mode=0
inside_container=0
app_url="http://127.0.0.1"
username=""
password=""
password_from_stdin=0

usage() {
    printf '%s\n' \
        "Reset the LANCache Manager main administrator password." \
        "" \
        "Usage:" \
        "  reset-main-admin-password.sh [--container NAME]" \
        "  reset-main-admin-password.sh --local [--url URL]" \
        "  reset-main-admin-password.sh [--container NAME] --username NAME --password-stdin" \
        "" \
        "Username and password are optional. If you omit them, the script only opens the" \
        "recovery window. Open LANCache Manager in the browser; it will prompt for the" \
        "main administrator username and a new password." \
        "" \
        "The new password is never read from the command line, because a command line is kept in" \
        "shell history and is readable in the process list. To reset from this script instead of" \
        "the browser, pass --username and pipe the password in with --password-stdin." \
        "" \
        "Options:" \
        "  --container NAME  Docker container name (default: lancache-manager)" \
        "  --username NAME   Main administrator username" \
        "  --password-stdin  Read the new password from standard input" \
        "  --local           Call a non-containerized LANCache Manager" \
        "  --url URL         App address in local mode (default: http://127.0.0.1)" \
        "  --help            Show this help"
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --container)
            if [ "$#" -lt 2 ] || [ -z "$2" ]; then
                echo "--container requires a name" >&2
                exit 2
            fi
            container_name="$2"
            shift 2
            ;;
        --username)
            if [ "$#" -lt 2 ] || [ -z "$2" ]; then
                echo "--username requires a username" >&2
                exit 2
            fi
            username="$2"
            shift 2
            ;;
        --password-stdin)
            password_from_stdin=1
            shift
            ;;
        --local)
            local_mode=1
            shift
            ;;
        --url)
            if [ "$#" -lt 2 ] || [ -z "$2" ]; then
                echo "--url requires an address" >&2
                exit 2
            fi
            app_url="${2%/}"
            shift 2
            ;;
        --inside-container)
            inside_container=1
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [ "${DOTNET_RUNNING_IN_CONTAINER:-}" = "true" ]; then
    inside_container=1
fi

complete_from_command=0
if [ -n "$username" ] && [ "$password_from_stdin" -eq 1 ]; then
    complete_from_command=1
elif [ -n "$username" ] || [ "$password_from_stdin" -eq 1 ]; then
    echo "--username and --password-stdin must be used together. Omit both to finish in the browser." >&2
    exit 2
fi

if [ "$inside_container" -eq 0 ] && [ "$local_mode" -eq 0 ]; then
    if ! command -v docker >/dev/null 2>&1; then
        echo "Docker was not found. Install Docker or use --local for a source installation." >&2
        exit 1
    fi

    if ! docker inspect "$container_name" >/dev/null 2>&1; then
        echo "Container '$container_name' was not found." >&2
        echo "Use --container NAME if LANCache Manager uses another container name." >&2
        exit 1
    fi

    inner_args=(--inside-container)
    if [ "$complete_from_command" -eq 1 ]; then
        inner_args+=(--username "$username" --password-stdin)
        docker_flags=(-i)
    else
        docker_flags=(-it)
    fi

    echo "Restarting $container_name to open the password recovery window..."
    docker restart "$container_name" >/dev/null
    exec env MSYS_NO_PATHCONV=1 docker exec "${docker_flags[@]}" "$container_name" \
        /data/scripts/reset-main-admin-password.sh "${inner_args[@]}"
fi

if ! command -v curl >/dev/null 2>&1; then
    echo "curl is required." >&2
    exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
    echo "jq is required." >&2
    exit 1
fi

if [ "$inside_container" -eq 1 ]; then
    app_url="http://127.0.0.1"
    api_key_path="/data/security/api_key.txt"
else
    script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
    api_key_path="$script_directory/../security/api_key.txt"
fi

echo "Waiting for LANCache Manager to become ready..."
ready=0
for _ in $(seq 1 60); do
    if curl --fail --silent "$app_url/health" >/dev/null; then
        ready=1
        break
    fi
    sleep 2
done

if [ "$ready" -ne 1 ]; then
    echo "LANCache Manager did not become ready at $app_url." >&2
    exit 1
fi

if [ ! -r "$api_key_path" ]; then
    echo "The API key could not be read at $api_key_path." >&2
    exit 1
fi

api_key="$(<"$api_key_path")"
response=""
trap 'unset api_key username password response' EXIT

# A process restart opens the first-account claim deadline, not password recovery. Explicitly arm
# recovery with the installation key so an ordinary reboot never replaces sign-in with the setup
# screen. Values travel to jq over stdin rather than argv, keeping them out of the process list.
if response="$(
    printf '%s\n' "$api_key" |
        jq -Rn '{apiKey: input}' |
        curl --fail-with-body --silent --show-error \
            -X POST "$app_url/api/account-setup/open-main-admin-recovery" \
            -H 'Content-Type: application/json' \
            --data-binary @-
)"; then
    :
else
    status=$?
    if [ -n "$response" ]; then
        printf '%s\n' "$response" >&2
    fi
    exit "$status"
fi

if [ "$complete_from_command" -ne 1 ]; then
    echo "The recovery window is open for one hour."
    echo "Open LANCache Manager in your browser. It will prompt for the main administrator username and a new password."
    exit 0
fi

IFS= read -r password || true
if [ -z "$password" ]; then
    echo "No password arrived on standard input." >&2
    exit 1
fi

if response="$(
    printf '%s\n%s\n%s\n' "$api_key" "$username" "$password" |
        jq -Rn '{apiKey: input, username: input, password: input}' |
        curl --fail-with-body --silent --show-error \
            -X POST "$app_url/api/account-setup/recover-main-admin" \
            -H 'Content-Type: application/json' \
            --data-binary @-
)"; then
    printf '%s\n' "$response"
    echo "Password reset. Sign in with the new password."
else
    status=$?
    if [ -n "$response" ]; then
        printf '%s\n' "$response" >&2
    fi
    exit "$status"
fi
