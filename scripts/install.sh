#!/usr/bin/env bash
# One-command setup on a fresh machine. Run from anywhere:  ./scripts/install.sh
#
# Does everything needed after a `git clone`:
#   1. checks for the .NET 10 SDK
#   2. builds (release publish)
#   3. downloads headless Chromium (Dashboard mode renderer)
#   4. installs the udev rule for sudo-free USB access (needs sudo)
#   5. installs + enables + starts the systemd user service (starts at login)
set -euo pipefail

cd "$(cd "$(dirname "$0")/.." && pwd)"
echo "==> KrakenEliteScreenManager installer ($PWD)"

# 1. .NET 10 SDK ------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: .NET 10 SDK not found. Install it, then re-run:" >&2
  echo "  https://dotnet.microsoft.com/download/dotnet/10.0" >&2
  exit 1
fi

# 2. Build ------------------------------------------------------------------
PUB="$PWD/bin/Release/net10.0/publish"
echo "==> Publishing to $PUB"
dotnet publish -c Release -o "$PUB" >/dev/null

if [[ -x "$PUB/KrakenEliteScreenManager" ]]; then
  APP=("$PUB/KrakenEliteScreenManager")
else
  APP=(dotnet "$PUB/KrakenEliteScreenManager.dll")
fi

# 3. Headless Chromium (Dashboard mode) -------------------------------------
echo "==> Installing Playwright Chromium (for Dashboard mode)"
"${APP[@]}" playwright-install

# 4. udev rule (sudo-free USB) ----------------------------------------------
echo "==> Installing udev rule (you may be prompted for sudo)"
sudo ./scripts/install-udev.sh

# 5. systemd user service ---------------------------------------------------
echo "==> Installing + enabling the user service (starts at login)"
"${APP[@]}" install

cat <<EOF

Done. The service is running (stock coolant screen by default).

If the Kraken was already plugged in and you are NOT in the 'wheel' group,
unplug/replug its USB once so the new permissions apply.

Open the GUI to pick a mode (GIF Loop / Dashboard / Coolant):
  dotnet run -c Release
EOF
