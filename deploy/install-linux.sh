#!/usr/bin/env bash
# Install NowFound as systemd services (run as root, from the extracted package directory).
#   sudo bash install-linux.sh                 Install to /opt/nowfound, enable web + daily indexer timer
#   sudo bash install-linux.sh --seed          Also create the initial admin on first start
#   sudo bash install-linux.sh --uninstall     Remove services (keeps /opt/nowfound/data)
set -euo pipefail

PREFIX=/opt/nowfound
SERVICE_USER=nowfound
src="$(cd "$(dirname "$0")" && pwd)"

usage() {
  cat <<'USAGE'
Usage: sudo bash install-linux.sh [--seed | --uninstall]

Installs NowFound to /opt/nowfound and registers it with systemd:
  nowfound.service          Web (HTTPS 5002)
  nowfound-indexer.timer    Indexer, daily at 05:30
Run as root, from the extracted package directory. Requires the .NET 10 (ASP.NET Core) runtime.

Options:
  (none)              Install or upgrade ("certs/" and "web/appsettings.Production.json" are kept)
  --seed              Also create the initial administrator (admin@nowfound.invalid) on first start
  --uninstall         Remove the services (keeps /opt/nowfound/data)
  -h, --help, -help   Show this usage

Logs: journalctl -u nowfound -f
USAGE
}

case "${1:-}" in
  -h|--help|-help) usage; exit 0 ;;
  ""|--seed|--uninstall) ;;
  *) echo "Unknown argument: $1" >&2; echo >&2; usage >&2; exit 1 ;;
esac

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
  systemctl disable --now nowfound.service nowfound-indexer.timer 2>/dev/null || true
  rm -f /etc/systemd/system/nowfound.service /etc/systemd/system/nowfound-indexer.service /etc/systemd/system/nowfound-indexer.timer
  systemctl daemon-reload
  echo "Removed. Data kept in ${PREFIX}/data."
  exit 0
fi

command -v dotnet >/dev/null || { echo ".NET 10 runtime (ASP.NET Core) is required." >&2; exit 1; }

id "${SERVICE_USER}" >/dev/null 2>&1 || useradd --system --home-dir "${PREFIX}" --shell /usr/sbin/nologin "${SERVICE_USER}"

systemctl stop nowfound.service nowfound-indexer.timer 2>/dev/null || true
mkdir -p "${PREFIX}/data"
# Keep a customized HTTPS setting across upgrades.
saved_settings=""
if [ -f "${PREFIX}/web/appsettings.Production.json" ]; then
  saved_settings="$(mktemp)"
  cp "${PREFIX}/web/appsettings.Production.json" "${saved_settings}"
fi
rm -rf "${PREFIX}/web" "${PREFIX}/indexer"
cp -r "${src}/web" "${src}/indexer" "${PREFIX}/"
if [ -n "${saved_settings}" ]; then
  cp "${saved_settings}" "${PREFIX}/web/appsettings.Production.json"
  rm -f "${saved_settings}"
fi
# Certificate: generate a self-signed one (random password) only when none exists (never overwrite your own).
mkdir -p "${PREFIX}/certs"
if [ ! -f "${PREFIX}/certs/nowfound.pfx" ]; then
  command -v openssl >/dev/null || { echo "openssl is required to generate the HTTPS certificate." >&2; exit 1; }
  pfx_password="$(openssl rand -hex 24)"
  san="DNS:nowfound.invalid,DNS:localhost,DNS:$(hostname)"
  fqdn="$(hostname -f 2>/dev/null || true)"
  [ -n "${fqdn}" ] && [ "${fqdn}" != "$(hostname)" ] && san="${san},DNS:${fqdn}"
  openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -keyout "${PREFIX}/certs/nowfound.key" -out "${PREFIX}/certs/nowfound.cer" \
    -subj "/CN=nowfound.invalid" -addext "subjectAltName=${san}" 2>/dev/null
  openssl pkcs12 -export -inkey "${PREFIX}/certs/nowfound.key" -in "${PREFIX}/certs/nowfound.cer" \
    -out "${PREFIX}/certs/nowfound.pfx" -passout "pass:${pfx_password}"
  rm -f "${PREFIX}/certs/nowfound.key"
  chmod 600 "${PREFIX}/certs/nowfound.pfx"
  # Write the generated password into the Kestrel settings.
  sed -i "s|\"Password\": \"[^\"]*\"|\"Password\": \"${pfx_password}\"|" "${PREFIX}/web/appsettings.Production.json"
  echo "Generated HTTPS certificate: ${PREFIX}/certs/nowfound.pfx"
fi
cp "${src}/run-indexer.sh" "${PREFIX}/"
chmod +x "${PREFIX}/run-indexer.sh"
chown -R "${SERVICE_USER}:${SERVICE_USER}" "${PREFIX}"

install -m 644 "${src}/nowfound.service" "${src}/nowfound-indexer.service" "${src}/nowfound-indexer.timer" /etc/systemd/system/
if [ "${1:-}" = "--seed" ]; then
  sed -i 's|^# First start only.*|Environment=Database__SeedData=true|' /etc/systemd/system/nowfound.service
fi

systemctl daemon-reload
systemctl enable --now nowfound.service nowfound-indexer.timer
command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active" && echo "Note: open the HTTPS port: sudo ufw allow 5002/tcp"
echo "Installed to ${PREFIX}. URL: https://$(hostname):5002   Status: systemctl status nowfound ; logs: journalctl -u nowfound -f"
echo "Run indexer now: systemctl start nowfound-indexer.service"
