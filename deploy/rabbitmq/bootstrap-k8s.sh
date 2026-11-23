#!/bin/sh
set -eu

topology=/etc/rabbitmq/habit-tracker
RABBITMQ_DEFINITIONS_FILE=$topology/definitions.json sh "$topology/bootstrap.sh"

user=habit-tracker-app
: "${HABIT_RABBIT_APP_PASSWORD:?Missing RabbitMQ application password}"
users=$(rabbitmqctl -q list_users --no-table-headers)
if printf '%s\n' "$users" | cut -f 1 | grep -Fxq "$user"; then
    rabbitmqctl -q change_password "$user" "$HABIT_RABBIT_APP_PASSWORD"
else
    rabbitmqctl -q add_user "$user" "$HABIT_RABBIT_APP_PASSWORD"
fi

# The application account uses AMQP and has no management tags.
rabbitmqctl -q set_user_tags "$user"
rabbitmqctl -q set_permissions -p habit-tracker "$user" '.*' '.*' '.*'
