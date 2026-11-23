#!/usr/bin/env bash
set -euo pipefail
umask 077

namespace=habit-tracker
scratch=$(mktemp -d)
trap 'rm -f -- "$scratch"/password "$scratch"/uri "$scratch"/keyfile "$scratch"/secret "$scratch"/key; rmdir -- "$scratch"' EXIT

create_secret() {
    local name=$1
    shift
    local existing
    existing=$(kubectl -n "$namespace" get secret "$name" --ignore-not-found -o name)
    if [[ -z "$existing" ]]; then
        kubectl -n "$namespace" create secret generic "$name" "$@"
    fi
}

write_hex_secret() {
    local value
    value=$(openssl rand -hex 32)
    printf '%s' "$value" > "$1"
}

for name in mongo-admin rabbitmq-admin rabbitmq-app; do
    write_hex_secret "$scratch/password"
    create_secret "$name" --from-file="password=$scratch/password"
done

openssl rand -base64 756 > "$scratch/keyfile"
create_secret mongo-keyfile --from-file="keyfile=$scratch/keyfile"

create_mongo_secret() {
    local user=$1
    local database=$2
    local password
    password=$(openssl rand -hex 32)
    printf '%s' "$password" > "$scratch/password"
    printf 'mongodb://%s:%s@mongo:27017/%s?replicaSet=rs0&authSource=%s' \
        "$user" "$password" "$database" "$database" > "$scratch/uri"
    create_secret "mongo-$user" --from-file="password=$scratch/password" --from-file="uri=$scratch/uri"
}

create_mongo_secret webapi habit_tracker_auth
create_mongo_secret worker habit_tracker_worker
create_mongo_secret notifications habit_tracker_notifications

write_hex_secret "$scratch/secret"
create_secret jwt --from-file="secret=$scratch/secret"
write_hex_secret "$scratch/key"
create_secret worker-api --from-file="key=$scratch/key"
