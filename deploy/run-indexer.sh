#!/usr/bin/env bash
# NowFound Indexer. DBs live in the data directory next to web / indexer.
base="$(cd "$(dirname "$0")" && pwd)"

case "${1:-}" in
  -h|--help|-help)
    cat <<USAGE
Usage: run-indexer.sh [options]

Runs NowFound.Indexer with --main-db and --files-db pointing at the data directory
next to this script (${base}/data/main.db, files.db). Options are passed to the Indexer:

USAGE
    exec dotnet "${base}/indexer/NowFound.Indexer.dll" --help
    ;;
esac

exec dotnet "${base}/indexer/NowFound.Indexer.dll" --main-db "${base}/data/main.db" --files-db "${base}/data/files.db" "$@"
