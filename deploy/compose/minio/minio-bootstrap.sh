#!/bin/sh
# Idempotent MinIO bootstrap (ADR 0003): the artifact buckets and one service account limited to them.
set -eu

mc alias set lean http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null

resources=""
for bucket in $MINIO_BUCKETS; do
    mc mb --ignore-existing "lean/${bucket}"
    resources="${resources:+${resources}, }\"arn:aws:s3:::${bucket}\", \"arn:aws:s3:::${bucket}/*\""
done

policy=/tmp/lean-services-policy.json
cat > "$policy" <<JSON
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["s3:*"], "Resource": [${resources}] }
  ]
}
JSON
mc admin policy create lean lean-services "$policy" >/dev/null

if mc admin user info lean "$MINIO_SERVICE_ACCESS_KEY" >/dev/null 2>&1; then
    echo "service user ${MINIO_SERVICE_ACCESS_KEY} exists"
else
    mc admin user add lean "$MINIO_SERVICE_ACCESS_KEY" "$MINIO_SERVICE_SECRET_KEY" >/dev/null
    echo "created service user ${MINIO_SERVICE_ACCESS_KEY}"
fi
mc admin policy attach lean lean-services --user "$MINIO_SERVICE_ACCESS_KEY" >/dev/null 2>&1 || true

echo "buckets ready: ${MINIO_BUCKETS}"
