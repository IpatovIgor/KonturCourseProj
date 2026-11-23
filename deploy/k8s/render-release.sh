#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "Usage: bash $0 WEBAPI_DIGEST WORKER_DIGEST NOTIFICATIONS_DIGEST FRONTEND_DIGEST" >&2
    exit 2
fi
for digest in "$@"; do
    if [[ ! $digest =~ ^sha256:[a-f0-9]{64}$ ]]; then
        echo "Each image must have a sha256 digest with 64 lowercase hex characters." >&2
        exit 2
    fi
done

directory=$(cd -- "$(dirname -- "$0")" && pwd)
release=$(kubectl kustomize "$directory" | sed \
    -e "s|\(/webapi\):release-placeholder$|\1@$1|" \
    -e "s|\(/worker\):release-placeholder$|\1@$2|" \
    -e "s|\(/notifications\):release-placeholder$|\1@$3|" \
    -e "s|\(/frontend\):release-placeholder$|\1@$4|")
if [[ $release == *:release-placeholder* ]]; then
    echo "Release still contains an image placeholder." >&2
    exit 1
fi
printf '%s\n' "$release"
