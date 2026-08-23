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
        "  reset-main-admin-password.sh [--container NAME] [--username NAME]" \
        "  reset-main-admin-password.sh --local [--url URL] [--username NAME]" \
        "" \
        "The script prompts for anything you do not supply." \
        "" \
        "The new password is never read from the command line, because a command line is kept in" \
        "shell history and is readable in the process list. Type it at the prompt, or pipe it in" \
        "with --password-stdin when scripting." \
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
    if [ -n "$username" ]; then
        inner_args+=(--username "$username")
    fi

    # The password reaches the container on stdin, never in the command line the container runs.
    # Piping needs stdin attached and no TTY; prompting needs the TTY, so the two modes differ only
    # in that flag.
    if [ "$password_from_stdin" -eq 1 ]; then
        inner_args+=(--password-stdin)
        docker_flags=(-i)
    else
        docker_flags=(-it)
    fi

    echo "Restarting $container_name to open the password recovery window..."
    docker restart "$container_name" >/dev/null
    exec env MSYS_NO_PATHCONV=1 docker exec "${docker_flags[@]}" "$container_name" \
        /data/scripts/reset-main-admin-password.sh "${inner_args[@]}"
fi

for command_name in curl jq; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "$command_name is required in local mode." >&2
        exit 1
    fi
done

if [ "$inside_container" -eq 1 ]; then
    app_url="http://127.0.0.1"
    api_key_path="/data/security/api_key.txt"
else
    script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
    api_key_path="$script_directory/../security/api_key.txt"
fi

if [ ! -r "$api_key_path" ]; then
    echo "The API key could not be read at $api_key_path." >&2
    exit 1
fi

api_key="$(<"$api_key_path")"
confirmation=""
response=""
trap 'unset api_key username password confirmation response' EXIT

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

if [ -z "$username" ]; then
    echo "The script will prompt you for anything you did not supply."
    read -r -p "Main administrator username: " username
    if [ -z "$username" ]; then
        echo "The username cannot be empty." >&2
        exit 1
    fi
fi

if [ "$password_from_stdin" -eq 1 ]; then
    IFS= read -r password || true
    if [ -z "$password" ]; then
        echo "No password arrived on standard input." >&2
        exit 1
    fi
else
    while true; do
        read -r -s -p "New password: " password
        printf '\n'
        read -r -s -p "Confirm new password: " confirmation
        printf '\n'

        if [ -z "$password" ]; then
            echo "The password cannot be empty." >&2
            continue
        fi

        if [ "$password" = "$confirmation" ]; then
            break
        fi

        echo "The passwords did not match. Try again." >&2
        password=""
        confirmation=""
    done
fi

if response="$(
    jq -n \
        --arg apiKey "$api_key" \
        --arg username "$username" \
        --arg password "$password" \
        '{apiKey: $apiKey, username: $username, password: $password}' |
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
