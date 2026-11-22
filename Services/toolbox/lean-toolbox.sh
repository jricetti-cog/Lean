#!/bin/bash
# lean-toolbox: runs one ToolBox app (converter or generator) as a one-shot job against the lean-data volume
# (plan step s3.4, ADR 0004). Entrypoint of the `toolbox` image:
#
#   docker compose run --rm toolbox <app> [--option=value ...]      # e.g. rdg --start=20240101 --end=20240131 ...
#   docker compose run --rm toolbox help                            # list apps / ToolBox options
#
# The app never writes the published data directly. It runs with `data-folder` pointing at a private staging copy on
# the same volume (/Lean/Data/.toolbox-staging/<id>/, next to the market-data service's .staging/), so in-place zip
# updates (LeanDataWriter -> ZipArchiveMode.Update) only touch the copy. When the app exits 0, every new or changed
# file is renamed into place (atomic on one filesystem); readers never see a partial zip. Generators (rdg) must only
# add files: a run that would overwrite existing data (e.g. a real ticker or coarse file) publishes nothing.
#
# Settings:
#   TOOLBOX_SEED   what the staging copy starts from: `all` (default, a reflink copy where the filesystem supports it)
#                  or space-separated paths relative to the data folder; the reference databases (market-hours,
#                  symbol-properties, */*/map_files, */*/factor_files) are always included.
#   TOOLBOX_DRY_RUN=1  run the app and list what would be published, publish nothing.
#   /input         read-only host directory for converter inputs (TOOLBOX_INPUT_DIR in docker-compose.yml).
set -euo pipefail

DATA="${LEAN_DATA_FOLDER:-/Lean/Data/}"; DATA="${DATA%/}"
TOOLBOX_DIR="${TOOLBOX_DIR:-/Lean/ToolBox/bin/Release}"
# Not .staging/: market-data clears that on start (LeanDataVolume.CleanStaging) and would remove a running job's copy.
STAGING_ROOT="${DATA}/.toolbox-staging"
SEED="${TOOLBOX_SEED:-all}"

log() { echo "lean-toolbox: $*" >&2; }
usage() {
    cat >&2 <<USAGE
usage: lean-toolbox <app> [--option=value ...]
apps (ToolBox/Program.cs):
  rdg  | randomdatagenerator      synthetic data (generator: new files only)
  cug  | coarseuniversegenerator  coarse universe files from equity/usa/daily
  kdc  | kaikodataconverter       Kaiko crypto files from --source-dir (under /input)
  asfc | algoseekfuturesconverter AlgoSeek futures (config futures-source-directory, under /input)
USAGE
}

[ $# -ge 1 ] || { usage; exit 2; }
app="$(echo "$1" | tr '[:upper:]' '[:lower:]')"; shift
case "$app" in
    help|-h|--help) usage; exec dotnet "${TOOLBOX_DIR}/QuantConnect.ToolBox.dll" --help ;;
    rdg|randomdatagenerator) kind=generator ;;
    cug|coarseuniversegenerator|kdc|kaikodataconverter|asfc|algoseekfuturesconverter) kind=converter ;;
    *) log "unknown app '$app'"; usage; exit 2 ;;
esac

[ -f "${DATA}/.lean-data-seeded" ] || { log "${DATA} is not a seeded lean-data volume: run 'docker compose --profile infra up -d --wait' first"; exit 2; }
[ -w "${DATA}" ] || { log "${DATA} is not writable: the toolbox mounts lean-data read-write"; exit 2; }

run_id="toolbox-$(date -u +%Y%m%dT%H%M%SZ)-$$"
staging="${STAGING_ROOT}/${run_id}"
work="$(mktemp -d /tmp/toolbox.XXXXXX)"
cleanup() { rm -rf "$staging" "$work"; }
trap cleanup EXIT
mkdir -p "$staging"

# --- 1. seed the staging copy (never hardlinks: LeanDataWriter rewrites zips in place) -------------------------------
copy_rel() {   # copy_rel <relative path>: file or directory, keeping the layout
    local rel="${1#/}"
    [ -e "${DATA}/${rel}" ] || { log "seed: ${rel} not found, skipped"; return 0; }
    mkdir -p "$(dirname "${staging}/${rel}")"
    cp -a --reflink=auto "${DATA}/${rel}" "${staging}/${rel}"
}
if [ "$SEED" = all ]; then
    find "$DATA" -mindepth 1 -maxdepth 1 ! -name '.*' -exec cp -a --reflink=auto {} "$staging/" \;
else
    for rel in market-hours symbol-properties $(cd "$DATA" && find . -mindepth 3 -maxdepth 3 -type d \( -name map_files -o -name factor_files \) -not -path './.*'); do
        copy_rel "$rel"
    done
    for rel in $SEED; do copy_rel "$rel"; done
fi
log "staging ${staging} seeded (${SEED}): $(find "$staging" -type f | wc -l) files"

# --- 2. run the app against the staging copy --------------------------------------------------------------------------
cat > "${work}/config.json" <<JSON
{
  "data-folder": "${staging}/",
  "composer-dll-directory": "${TOOLBOX_DIR}/",
  "log-handler": "ConsoleLogHandler",
  "futures-source-directory": "${TOOLBOX_FUTURES_SOURCE_DIRECTORY:-/input/algoseek/{0}}",
  "futures-remote-directory": "${TOOLBOX_FUTURES_REMOTE_DIRECTORY:-/input/algoseek/{0}}"
}
JSON
log "running ${app} $*"
set +e
(cd "$work" && dotnet "${TOOLBOX_DIR}/QuantConnect.ToolBox.dll" --app="$app" "$@")
status=$?
set -e
if [ "$status" -ne 0 ]; then
    log "${app} exited ${status}: nothing published"
    exit "$status"
fi

# --- 3. diff staging against the published data -----------------------------------------------------------------------
added="${work}/added"; changed="${work}/changed"; : > "$added"; : > "$changed"
(cd "$staging" && find . -type f -printf '%P\n' | LC_ALL=C sort) | while IFS= read -r rel; do
    if [ ! -e "${DATA}/${rel}" ]; then echo "$rel" >> "$added"
    elif ! cmp -s "${staging}/${rel}" "${DATA}/${rel}"; then echo "$rel" >> "$changed"
    fi
done
log "$(wc -l < "$added") new, $(wc -l < "$changed") changed file(s)"
sed 's/^/  + /' "$added" >&2
sed 's/^/  ~ /' "$changed" >&2

if [ "$kind" = generator ] && [ -s "$changed" ]; then
    log "refused: a generator may only add files, but this run would overwrite existing data (above); nothing published."
    log "use synthetic tickers (--tickers=SYN...) and/or --include-coarse=false"
    exit 3
fi
if [ "${TOOLBOX_DRY_RUN:-0}" = 1 ]; then
    log "dry run: nothing published"
    exit 0
fi

# --- 4. atomic publish: rename each file into place (same filesystem) -------------------------------------------------
cat "$added" "$changed" | while IFS= read -r rel; do
    mkdir -p "$(dirname "${DATA}/${rel}")"
    mv -f "${staging}/${rel}" "${DATA}/${rel}"
done
log "published $(cat "$added" "$changed" | wc -l) file(s) to ${DATA}"
