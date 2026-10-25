#!/usr/bin/env bash
# NowFound publish script (same output as deploy/publish.ps1, no PowerShell needed)
#   tools/publish.sh                Run tests, publish, package into dist/ (tar.gz, plus zip if `zip` exists)
#   tools/publish.sh --skip-tests   Skip dotnet test
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dist="${root}/dist"
out="${dist}/NowFound"
keep_runtimes=(linux-x64 win-x64 win unix)   # win/unix: RID-neutral assets (ServiceController for the Windows service)

usage() {
  cat <<'USAGE'
Usage: tools/publish.sh [--skip-tests]

Runs the tests, publishes Web and Indexer and packages dist/NowFound-publish.tar.gz
(plus dist/NowFound-publish.zip when the `zip` command exists). No PowerShell needed.

Options:
  --skip-tests        Skip dotnet test
  -h, --help, -help   Show this usage
USAGE
}

skip_tests=false
for arg in "$@"; do
  case "${arg}" in
    --skip-tests) skip_tests=true ;;
    -h|--help|-help) usage; exit 0 ;;
    *) echo "Unknown argument: ${arg}" >&2; echo >&2; usage >&2; exit 1 ;;
  esac
done

if [ "${skip_tests}" = false ]; then
  dotnet test "${root}/NowFound.slnx" -c Release
fi

rm -rf "${out}" "${dist}/NowFound-publish.zip" "${dist}/NowFound-publish.tar.gz"

dotnet publish "${root}/src/NowFound.Web/NowFound.Web.csproj" -c Release -o "${out}/web"
dotnet publish "${root}/src/NowFound.Indexer/NowFound.Indexer.csproj" -c Release -o "${out}/indexer"

# Keep only the supported runtimes (runtimes/<rid>/...).
for app in web indexer; do
  [ -d "${out}/${app}/runtimes" ] || continue
  for dir in "${out}/${app}/runtimes"/*/; do
    name="$(basename "${dir}")"
    keep=false
    for k in "${keep_runtimes[@]}"; do [ "${name}" = "${k}" ] && keep=true; done
    if [ "${keep}" = false ]; then
      echo "Removing ${app}/runtimes/${name}"
      rm -rf "${dir}"
    fi
  done
done

# Not needed in production.
rm -f "${out}/web/appsettings.Development.json"
find "${out}" -name '*.pdb' -delete

for f in INSTALL.md run-web.bat run-indexer.bat run-indexer.sh register-indexer-task.ps1 register-web-service.ps1 \
         install-linux.sh nowfound.service nowfound-indexer.service nowfound-indexer.timer; do
  cp "${root}/deploy/${f}" "${out}/"
done
# HTTPS: Production Kestrel endpoint. The certificate is generated at install time (no private key is shipped; see INSTALL.md).
cp "${root}/deploy/appsettings.Production.json" "${out}/web/"
cp "${root}/tools/new-cert.ps1" "${out}/"
chmod +x "${out}/install-linux.sh" "${out}/run-indexer.sh"

tar -czf "${dist}/NowFound-publish.tar.gz" -C "${dist}" NowFound
echo "Done: ${dist}/NowFound-publish.tar.gz"
if command -v zip >/dev/null; then
  (cd "${dist}" && zip -qr NowFound-publish.zip NowFound)
  echo "Done: ${dist}/NowFound-publish.zip"
fi
