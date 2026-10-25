#!/usr/bin/env bash
# NowFound launcher
#   ./run.sh debug          Development mode (HTTP/HTTPS allowed, seed data on)
#   ./run.sh release        Production mode (HTTPS enforced, seed data off)
#   ./run.sh release seed   Production mode + seed the initial admin (first run only)
# Listening URL: set ASPNETCORE_URLS (default http://localhost:5000).
set -euo pipefail
cd "$(dirname "$0")"

usage() {
  cat <<'USAGE'
Usage: ./run.sh [debug | release [seed]]

Starts NowFound Web from source with dotnet run.

Modes:
  debug          Development mode: HTTP/HTTPS allowed, seed data on (default)
  release        Production mode: HTTPS enforced, seed data off
  release seed   Production mode and create the initial administrator (first run only)

Options:
  -h, --help, -help   Show this usage

Environment:
  ASPNETCORE_URLS     Listening URL (default: http://localhost:5000)
USAGE
}

mode="${1:-debug}"
case "${mode}" in
  -h|--help|-help)
    usage
    exit 0
    ;;
  debug)
    export ASPNETCORE_ENVIRONMENT=Development
    configuration=Debug
    ;;
  release)
    export ASPNETCORE_ENVIRONMENT=Production
    configuration=Release
    if [ "${2:-}" = "seed" ]; then
      export Database__SeedData=true
    fi
    ;;
  *)
    echo "Unknown mode: ${mode}" >&2
    echo >&2
    usage >&2
    exit 1
    ;;
esac

export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:5000}"

echo "Mode=${ASPNETCORE_ENVIRONMENT} Urls=${ASPNETCORE_URLS}"
exec dotnet run --project src/NowFound.Web -c "${configuration}" --no-launch-profile
