#!/usr/bin/env bash
# Entry point of the LEAN toolbox batch-job image. Runs one ToolBox or DownloaderDataProvider
# command to completion and exits with its status, so it can run as a compose "run", a Kubernetes Job or a CronJob.
#   lean-job toolbox    --app=RandomDataGenerator --start=20200101 --end=20200201 --tickers=RNDA ...
#   lean-job downloader --data-type=Trade --resolution=Daily --security-type=Equity --tickers=SPY ...
# Output goes to /Lean/Data/ (toolbox --destination-dir defaults to $LEAN_DATA_FOLDER); mount a volume there to keep it.
set -euo pipefail

command="${1:-help}"
shift || true

has_data_folder=false
for arg in "$@"; do
    case "$arg" in --destination-dir*) has_data_folder=true ;; esac
done

case "$command" in
    toolbox)
        cd /Lean/ToolBox/bin/Debug
        extra=()
        $has_data_folder || extra+=("--destination-dir=${LEAN_DATA_FOLDER:-/Lean/Data/}")
        exec dotnet QuantConnect.ToolBox.dll "$@" "${extra[@]}"
        ;;
    downloader)
        # the downloader reads data-folder from config, which resolves to /Lean/Data/ from its bin folder
        cd /Lean/DownloaderDataProvider/bin/Debug
        exec dotnet QuantConnect.DownloaderDataProvider.Launcher.dll "$@"
        ;;
    help|--help|-h)
        sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'
        ;;
    *)
        echo "unknown command '$command', expected 'toolbox' or 'downloader'" >&2
        exit 2
        ;;
esac
