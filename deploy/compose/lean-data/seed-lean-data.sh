#!/bin/sh
# Seeds the lean-data volume (ADR 0004) from the repo's Data/ folder, once. The marker is written last, so
# an interrupted copy is redone on the next `up`. To reseed: `docker volume rm lean_lean-data`.
set -eu

target=/Lean/Data
marker="${target}/.lean-data-seeded"

if [ -f "$marker" ]; then
    echo "lean-data already seeded: $(cat "$marker")"
    exit 0
fi

echo "seeding ${target} from Data/"
cp -R /source/. "${target}/"
# market-data publishes atomically through a staging directory on the same volume
mkdir -p "${target}/.staging"
chown -R "${LEAN_DATA_UID}:${LEAN_DATA_GID}" "$target"
echo "seeded $(date -u +%Y-%m-%dT%H:%M:%SZ), $(find "$target" -type f | wc -l) files" > "$marker"
cat "$marker"
