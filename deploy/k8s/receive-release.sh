#!/usr/bin/env bash
set -euo pipefail

command=${SSH_ORIGINAL_COMMAND:-}
directory=$(cd -- "$(dirname -- "$0")" && pwd)
export KUBECONFIG="$HOME/.kube/config"

if [[ "$command" == check ]]; then
    for tool in kubectl jq curl flock; do
        command -v "$tool" >/dev/null || { echo "Required command not found: $tool" >&2; exit 2; }
    done
    kubectl -n habit-tracker auth can-i patch deployment/webapi --quiet
    kubectl -n habit-tracker get deployments webapi worker notifications frontend
    exit 0
fi

if [[ ! "$command" =~ ^deploy[[:space:]]([0-9a-f]{40})$ ]]; then
    echo "Supported commands: check, deploy <commit SHA>." >&2
    exit 2
fi
commit=${BASH_REMATCH[1]}

umask 077
releases="$HOME/habit-tracker/releases"
mkdir -p -- "$releases"
exec 9>"$releases/.deploy.lock"
if ! flock -n 9; then
    echo "Another release is already being applied." >&2
    exit 1
fi

release=$(mktemp -d "$releases/$commit.XXXXXX")
cd -- "$release"
cat > habit-tracker-release.yaml
if [[ ! -s habit-tracker-release.yaml ]]; then
    echo "Release manifest is empty." >&2
    exit 2
fi
bash "$directory/apply-release.sh" ./habit-tracker-release.yaml
