#!/bin/sh
# Smoke test for the compose infrastructure (plan step s2.3). Run from the repository root after
# `docker compose --profile infra up -d --wait`. Uses the same defaults/.env values as docker-compose.yml.
set -eu
[ -f .env ] && set -a && . ./.env && set +a

RABBITMQ_USER="${RABBITMQ_USER:-lean}"; RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-lean-dev-rabbitmq}"
RABBITMQ_VHOST="${RABBITMQ_VHOST:-lean}"; RABBITMQ_MANAGEMENT_PORT="${RABBITMQ_MANAGEMENT_PORT:-15672}"
MINIO_API_PORT="${MINIO_API_PORT:-9000}"
NETWORK="${COMPOSE_PROJECT_NAME:-lean}_lean"
VOLUME="${COMPOSE_PROJECT_NAME:-lean}_lean-data"
MINIO_IMAGE="${MINIO_IMAGE:-pgsty/minio:RELEASE.2026-08-04T00-00-00Z}"
SEED_IMAGE="${SEED_IMAGE:-alpine:3.22.8}"
compose() { docker compose --profile infra "$@"; }
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; exit 1; }

echo "== containers"
compose ps -a --format '{{.Service}}|{{.Status}}' | sort | while IFS='|' read -r svc status; do
    case "$status" in
        *"(healthy)"*|"Exited (0)"*) pass "$svc: $status" ;;
        *) fail "$svc: $status" ;;
    esac
done

echo "== published ports (127.0.0.1 only)"
ports=$(compose ps --format '{{.Ports}}' | tr ',' '\n' | grep -- '->' || true)
echo "$ports" | sed 's/^ */      /'
echo "$ports" | grep -- '->' | grep -qv '127.0.0.1:' && fail "a port is published on a non-loopback address"
pass "every published port binds 127.0.0.1"

echo "== rabbitmq"
api="http://127.0.0.1:${RABBITMQ_MANAGEMENT_PORT}/api"
queues=$(curl -fsS -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" "${api}/queues/${RABBITMQ_VHOST}?columns=name" | grep -o '"name":"[^"]*"' | wc -l)
exchanges=$(curl -fsS -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" "${api}/exchanges/${RABBITMQ_VHOST}?columns=name" | grep -o '"name":"lean[^"]*"' | wc -l)
[ "$queues" -gt 0 ] && [ "$exchanges" -gt 0 ] || fail "topology not loaded"
pass "management API as ${RABBITMQ_USER}: vhost ${RABBITMQ_VHOST} has ${queues} queues, ${exchanges} lean.* exchanges"
curl -fsS -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" -o /dev/null "${api}/aliveness-test/${RABBITMQ_VHOST}" \
    && pass "aliveness test (AMQP publish/consume) on vhost ${RABBITMQ_VHOST}"

echo "== postgres"
for db in jobs results gateway marketdata; do
    var="POSTGRES_$(echo "$db" | tr '[:lower:]' '[:upper:]')_PASSWORD"
    eval "password=\${$var:-lean-dev-$db}"
    owner=$(compose exec -T -e PGPASSWORD="$password" postgres psql -q -h 127.0.0.1 -U "$db" -d "$db" -tAc \
        "CREATE TABLE smoke_check(id int); DROP TABLE smoke_check; SELECT current_user || '@' || current_database() || ' ' || split_part(version(), ' ', 2)")
    pass "role $db owns database $db and can create tables ($owner)"
    other=jobs; [ "$db" = jobs ] && other=results
    if compose exec -T -e PGPASSWORD="$password" postgres psql -h 127.0.0.1 -U "$db" -d "$other" -tAc 'select 1' >/dev/null 2>&1; then
        fail "role $db can connect to database $other"
    fi
    pass "role $db cannot connect to database $other"
done

echo "== minio"
docker run --rm --network "$NETWORK" --entrypoint /bin/sh \
    -e AK="${MINIO_SERVICE_ACCESS_KEY:-lean-services}" -e SK="${MINIO_SERVICE_SECRET_KEY:-lean-dev-minio-services}" \
    -e BUCKETS="${MINIO_BUCKET_ALGORITHMS:-lean-algorithms} ${MINIO_BUCKET_RESULTS:-lean-results} ${MINIO_BUCKET_REPORTS:-lean-reports}" \
    "$MINIO_IMAGE" -c '
set -eu
mc alias set svc http://minio:9000 "$AK" "$SK" >/dev/null
echo smoke > /tmp/smoke.txt
for b in $BUCKETS; do
  mc cp -q /tmp/smoke.txt "svc/$b/smoke/smoke.txt" >/dev/null
  [ "$(mc cat "svc/$b/smoke/smoke.txt")" = smoke ]
  mc rm -q "svc/$b/smoke/smoke.txt" >/dev/null
  echo "PASS  service account $AK can write/read/delete in $b"
done
if mc mb svc/not-a-lean-bucket >/dev/null 2>&1; then echo "FAIL  service account can create buckets"; exit 1; fi
echo "PASS  service account $AK cannot create other buckets"'
curl -fsS -o /dev/null "http://127.0.0.1:${MINIO_API_PORT}/minio/health/ready" && pass "S3 API ready on 127.0.0.1:${MINIO_API_PORT}"

echo "== lean-data"
compose logs --no-log-prefix lean-data-seed | tail -1 | sed 's/^/      /'
docker run --rm --user 1654:1654 -v "$VOLUME:/Lean/Data:ro" "$SEED_IMAGE" sh -c '
set -eu
test -s /Lean/Data/market-hours/market-hours-database.json
test -s /Lean/Data/symbol-properties/symbol-properties-database.csv
test -s /Lean/Data/equity/usa/daily/spy.zip
echo "PASS  uid 1654 reads market-hours, symbol-properties and equity/usa/daily/spy.zip ($(find /Lean/Data -type f | wc -l) files)"
if touch /Lean/Data/write-test 2>/dev/null; then echo "FAIL  read-only mount is writable"; exit 1; fi
echo "PASS  read-only mount rejects writes"'
docker run --rm --user 1654:1654 -v "$VOLUME:/Lean/Data" "$SEED_IMAGE" sh -c \
    'touch /Lean/Data/.staging/w && rm /Lean/Data/.staging/w && echo "PASS  uid 1654 (market-data) can write .staging on a read-write mount"'
echo "== all checks passed"
