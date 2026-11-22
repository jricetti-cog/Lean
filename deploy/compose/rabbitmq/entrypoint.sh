#!/bin/sh
# RabbitMQ skips its default user when definitions are loaded at boot, and the v1 topology contract
# (docs/architecture/contracts/topology/definitions.json) defines no users. Write a copy of the contract with
# the compose user added (administrator, full permissions on the LEAN vhost) to the path rabbitmq.conf loads.
set -eu

users="\"users\": [{\"name\": \"${RABBITMQ_USER}\", \"password\": \"${RABBITMQ_PASSWORD}\", \"tags\": [\"administrator\"]}],"
permissions="\"permissions\": [{\"user\": \"${RABBITMQ_USER}\", \"vhost\": \"${RABBITMQ_VHOST}\", \"configure\": \".*\", \"write\": \".*\", \"read\": \".*\"}],"

target=/etc/rabbitmq/definitions.json
sed -e "s|^  \"users\": \[\],\$|  ${users}|" \
    -e "s|^  \"permissions\": \[\],\$|  ${permissions}|" \
    /etc/lean/topology/definitions.json > "$target"

if ! grep -q "\"name\": \"${RABBITMQ_USER}\"" "$target" || ! grep -q "\"user\": \"${RABBITMQ_USER}\"" "$target"; then
    echo "could not add user ${RABBITMQ_USER} to the topology definitions (expected empty users/permissions)" >&2
    exit 1
fi
chown rabbitmq:rabbitmq "$target"
chmod 600 "$target"

exec docker-entrypoint.sh rabbitmq-server
