#!/usr/bin/env bash
set -euo pipefail

if (( $# > 1 )); then
    echo "Usage: bash $0 [release.yaml]" >&2
    exit 2
fi

directory=$(cd -- "$(dirname -- "$0")" && pwd)
release=${1:-"$directory/habit-tracker-release.yaml"}
if [[ ! -f "$release" ]]; then
    echo "Release manifest not found: $release" >&2
    exit 2
fi

for tool in kubectl jq curl; do
    command -v "$tool" >/dev/null || { echo "Required command not found: $tool" >&2; exit 2; }
done

context=$(kubectl config current-context)
kube=(kubectl --context "$context" --namespace habit-tracker)
apps=(notifications worker webapi frontend)
base_url=https://habit-tracker.duckdns.org
diagnostics="$PWD/deploy-diagnostics.log"

umask 077
scratch=$(mktemp -d)
previous="$scratch/previous.json"
changed=false

smoke() {
    local path status
    "${kube[@]}" wait --for=condition=Ready certificate/habit-tracker --timeout=180s || return 1
    status=$(curl --silent --show-error --retry 3 --max-time 15 --output /dev/null \
        --write-out '%{http_code}' "$base_url/") || return 1
    if [[ "$status" != 200 ]]; then
        echo "Expected HTTP 200 for the frontend, got $status." >&2
        return 1
    fi
    for path in /api/habits /api/notifications; do
        status=$(curl --silent --show-error --retry 3 --max-time 15 --output /dev/null \
            --write-out '%{http_code}' "$base_url$path") || return 1
        if [[ "$status" != 401 ]]; then
            echo "Expected HTTP 401 without a token at $path, got $status." >&2
            return 1
        fi
    done
}

cleanup() {
    local status=$?
    trap - EXIT
    if (( status != 0 )) && [[ "$changed" == true ]]; then
        echo "Release failed. Restoring the previous application resources." | tee "$diagnostics" || true
        "${kube[@]}" get deployments,pods -o wide | tee -a "$diagnostics" || true
        "${kube[@]}" get events --field-selector type=Warning \
            -o 'custom-columns=REASON:.reason,OBJECT:.involvedObject.name' | tee -a "$diagnostics" || true
        if "${kube[@]}" apply -f "$previous"; then
            local restored=true
            for app in "${apps[@]}"; do
                "${kube[@]}" rollout status "deployment/$app" --timeout=180s || restored=false
            done
            if [[ "$restored" == true ]] && smoke; then
                echo "Previous release is ready." | tee -a "$diagnostics" || true
            else
                echo "Previous release did not pass readiness checks." | tee -a "$diagnostics" || true
            fi
        else
            echo "Could not restore the previous application resources." | tee -a "$diagnostics" || true
        fi
    fi
    rm -f -- "$previous"
    rmdir -- "$scratch"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

echo "Applying $release to context $context, namespace habit-tracker."
"${kube[@]}" apply --dry-run=server -f "$release"

# The first installation and all stateful infrastructure use the bootstrap procedure.
for app in "${apps[@]}"; do
    "${kube[@]}" rollout status "deployment/$app" --timeout=60s
done
"${kube[@]}" get -f "$release" -o json | jq -e '
    .items |= map(del(.status, .metadata.uid, .metadata.resourceVersion,
        .metadata.generation, .metadata.creationTimestamp, .metadata.managedFields,
        .metadata.annotations["deployment.kubernetes.io/revision"],
        .metadata.annotations["kubectl.kubernetes.io/last-applied-configuration"]))
' > "$previous"

changed=true
for app in "${apps[@]}"; do
    "${kube[@]}" apply -f "$release" -l "app=$app"
    "${kube[@]}" rollout status "deployment/$app" --timeout=300s
done
"${kube[@]}" apply -f "$release" -l '!app'
smoke
echo "Release is ready at $base_url."
