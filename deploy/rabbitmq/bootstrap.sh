#!/bin/sh
set -eu

vhost=${RABBITMQ_TOPOLOGY_VHOST:-habit-tracker}
definitions=${RABBITMQ_DEFINITIONS_FILE:-/etc/rabbitmq/definitions.json}

ctl() {
    if [ "$(id -u)" = 0 ]; then
        gosu rabbitmq rabbitmqctl -q "$@"
    else
        rabbitmqctl -q "$@"
    fi
}

# Return 1 for missing objects and 2 for incompatible declarations.
check_resources() {
    command=$1
    expected=$2
    kind=$3
    set -- name type durable auto_delete arguments
    if [ "$command" = list_exchanges ]; then
        set -- "$@" internal
        expected_metadata=false
    else
        set -- "$@" effective_policy_definition
        expected_metadata='[]'
    fi
    rows=$(ctl -p "$vhost" "$command" --no-table-headers "$@") || return 3
    printf '%s\n' "$rows" | awk -F '\t' -v expected="$expected" -v kind="$kind" -v metadata="$expected_metadata" '
        BEGIN {
            count = split(expected, names, " ")
            for (i = 1; i <= count; i++) required[names[i]] = 1
        }
        $1 in required {
            seen++
            valid_arguments = ($5 == "[]" || (kind == "classic" && $5 == "[{\"x-queue-type\",\"classic\"}]"))
            if ($2 != kind || $3 != "true" || $4 != "false" || !valid_arguments || $6 != metadata) {
                print "Incompatible RabbitMQ resource: " $0
                incompatible = 1
            }
        }
        END {
            if (incompatible) exit 2
            if (seen != count) exit 1
        }'
}

check_bindings() {
    rows=$(ctl -p "$vhost" list_bindings --no-table-headers source_name destination_name destination_kind routing_key arguments) || return 3
    printf '%s\n' "$rows" | awk -F '\t' '
        BEGIN {
            worker = "worker-habit-events-v1"
            notifications = "notifications-ready-v1"
            required["habit-changed-v1", worker, "exchange"] = 1
            required["summary-settings-changed-v1", worker, "exchange"] = 1
            required["notification-ready-v1", notifications, "exchange"] = 1
            required[worker, worker, "queue"] = 1
            required[notifications, notifications, "queue"] = 1
            checked["habit-changed-v1"] = checked["summary-settings-changed-v1"] = 1
            checked["notification-ready-v1"] = checked[worker] = checked[notifications] = 1
        }
        $1 in checked {
            if (!(($1, $2, $3) in required) || $4 != "" || $5 != "[]") {
                print "Incompatible RabbitMQ binding: " $0
                incompatible = 1
            } else {
                seen[$1, $2, $3] = 1
            }
        }
        END {
            if (incompatible) exit 2
            for (binding in required) if (!(binding in seen)) exit 1
        }
    '
}

check_topology() {
    queue_result=0
    exchange_result=0
    check_resources list_queues 'worker-habit-events-v1 notifications-ready-v1' classic || queue_result=$?
    check_resources list_exchanges 'habit-changed-v1 summary-settings-changed-v1 notification-ready-v1 worker-habit-events-v1 notifications-ready-v1' fanout || exchange_result=$?
    if [ "$queue_result" -ge 2 ]; then return "$queue_result"; fi
    if [ "$exchange_result" -ge 2 ]; then return "$exchange_result"; fi
    binding_result=0
    check_bindings || binding_result=$?
    if [ "$binding_result" -ge 2 ]; then return "$binding_result"; fi
    if [ "$queue_result" -ne 0 ] || [ "$exchange_result" -ne 0 ]; then return 1; fi
    return "$binding_result"
}

vhosts=$(ctl list_vhosts --no-table-headers name) || exit 3
status=1
if printf '%s\n' "$vhosts" | grep -Fxq -- "$vhost"; then
    status=0
    check_topology || status=$?
fi

case "$status" in
    0) exit 0 ;;
    1) ctl import_definitions "$definitions" || exit 3 ;;
    *) exit "$status" ;;
esac

check_topology
