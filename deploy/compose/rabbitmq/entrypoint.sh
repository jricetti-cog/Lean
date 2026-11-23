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

# RabbitMQ sizes its memory watermark (and quorum-queue memory tables) from the host's RAM, not the container's
# limit: under load it grows past RABBITMQ_MEMORY and is OOM-killed, then cannot even replay its quorum WAL. Size
# it from the cgroup limit instead.
limit=$(cat /sys/fs/cgroup/memory.max 2>/dev/null || cat /sys/fs/cgroup/memory/memory.limit_in_bytes 2>/dev/null || echo max)
case "$limit" in
    ''|max|*[!0-9]*) ;;
    *)
        if [ "$limit" -lt 1099511627776 ]; then
            mkdir -p /etc/rabbitmq/conf.d
            {
                echo "total_memory_available_override_value = ${limit}"
                # Quorum queues keep up to the WAL size in memory before flushing: an eighth of the limit.
                echo "raft.wal_max_size_bytes = $((limit / 8))"
            } > /etc/rabbitmq/conf.d/20-lean-container-memory.conf
            chown -R rabbitmq:rabbitmq /etc/rabbitmq/conf.d
        fi
        ;;
esac

exec docker-entrypoint.sh rabbitmq-server
