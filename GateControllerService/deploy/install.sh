#!/bin/bash
# =============================================================================
# Gate Controller Service - Self-Install Script for Raspberry Pi
# =============================================================================
# Run directly on the Pi wired to the gate/light relay board.
#
# From a release tarball (no git, no .NET SDK needed):
#
#   curl -fsSL -o gate.tar.gz https://github.com/GTMichelli-Dev/foundation/releases/latest/download/gate-controller-linux-arm64.tar.gz
#   mkdir -p /tmp/gcs && tar -xzf gate.tar.gz -C /tmp/gcs
#   bash /tmp/gcs/install.sh <web-server-url>
#
# From the monorepo:
#
#   git clone https://github.com/GTMichelli-Dev/foundation.git /tmp/fnd
#   bash /tmp/fnd/GateControllerService/deploy/install.sh <url> --local /tmp/fnd/GateControllerService
#
# Examples:
#   bash install.sh https://basicscale.scaledata.net
#   bash install.sh http://localhost                  # web app on this Pi, port 80
#   bash install.sh http://192.168.1.50:5110 --service-id north-gate
#
# The URL must match the web app's *actual* listen port. A wrong one leaves the
# service reconnecting forever — watch it with
# `journalctl -u gate-controller-service -f`.
#
# Re-run the same command to update: the service stops, files are replaced, the
# gate configuration is preserved, and the service restarts.
# =============================================================================

set -e

# ---- Defaults ----
SERVICE_ID=""      # defaults to $(hostname) below
SERVICE_PORT="5240"   # allocation lives in docs/service-ports.md
INSTALL_DIR="/opt/gate-controller-service"
SERVICE_NAME="gate-controller-service"
DOTNET_CHANNEL="10.0"
GITHUB_REPO="GTMichelli-Dev/foundation"
BRANCH="main"
WEB_URL=""
LOCAL_SRC=""

# ---- Parse arguments ----
while [[ $# -gt 0 ]]; do
    case "$1" in
        --service-id)  SERVICE_ID="$2"; shift 2 ;;
        --port)        SERVICE_PORT="$2"; shift 2 ;;
        --branch)      BRANCH="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        --local)       LOCAL_SRC="$2"; shift 2 ;;
        --help|-h)
            cat <<'USAGE'
Usage: install.sh <web-server-url> [options]

  <web-server-url>    Required. URL of the BasicWeigh web app, matching its
                      real scheme and listen port.

Options:
  --service-id <id>   Names this box on the web app. Gates are addressed as
                      "serviceId:gateId", so two Pis at one site must differ.
                      Defaults to the hostname.
  --port <n>          Local API port (default 5240).
  --install-dir <p>   Install location (default /opt/gate-controller-service).
  --local <path>      Build from a local source folder instead of downloading.
  --branch <name>     Branch to fetch when building from source (default main).
USAGE
            exit 0 ;;
        -*) echo "Unknown option: $1"; exit 1 ;;
        *)  WEB_URL="$1"; shift ;;
    esac
done

if [ -z "$WEB_URL" ]; then
    echo "ERROR: the web server URL is required."
    echo "       install.sh <web-server-url> [--service-id <id>]"
    echo "       Run with --help for the full list."
    exit 1
fi

# A URL without a scheme silently produces a service that never connects.
case "$WEB_URL" in
    http://*|https://*) ;;
    *) echo "ERROR: the URL must start with http:// or https:// (got '$WEB_URL')."; exit 1 ;;
esac

[ -z "$SERVICE_ID" ] && SERVICE_ID="$(hostname)"

echo "============================================"
echo "  Gate Controller Service - Install"
echo "============================================"
echo "  Web app:     ${WEB_URL}"
echo "  Service ID:  ${SERVICE_ID}"
echo "  Install dir: ${INSTALL_DIR}"
echo "  API port:    ${SERVICE_PORT}"
echo ""

# ---- Prebuilt release package? ----
#
# When this script sits beside an "app" folder — the layout of the release
# tarball — the binaries are already built for this architecture, so both the
# .NET download and the build are skipped.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -d "${SCRIPT_DIR}/app" ] && [ -f "${SCRIPT_DIR}/app/GateControllerService" ]; then
    PREBUILT=true
else
    PREBUILT=false
fi

# ---- Stop an existing install ----
if systemctl list-unit-files 2>/dev/null | grep -q "^${SERVICE_NAME}.service"; then
    echo "[1/5] Stopping the running service..."
    # Its own shutdown drops every gate before the process exits.
    sudo systemctl stop ${SERVICE_NAME} || true
else
    echo "[1/5] No existing service to stop."
fi

# ---- GPIO access ----
echo "[2/5] Checking GPIO access..."
if getent group gpio > /dev/null 2>&1; then
    sudo usermod -aG gpio "$USER" || true
    echo "  Added ${USER} to the gpio group."
else
    # Non-Pi Linux, or an OS that exposes the chip differently. The service
    # detects the missing chip and says so on /api/status rather than failing.
    echo "  No 'gpio' group on this machine — the service will report"
    echo "  gpioAvailable=false and drive nothing. Fine for a dry run."
fi

# ---- Build or copy ----
sudo mkdir -p "${INSTALL_DIR}"

if [ "$PREBUILT" = true ]; then
    echo "[3/5] Installing prebuilt binaries..."
    sudo cp -r "${SCRIPT_DIR}/app/." "${INSTALL_DIR}/"
else
    echo "[3/5] Building from source..."

    if ! command -v dotnet > /dev/null 2>&1; then
        echo "  Installing the .NET SDK (${DOTNET_CHANNEL})..."
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel "${DOTNET_CHANNEL}" --install-dir "$HOME/.dotnet"
        export DOTNET_ROOT="$HOME/.dotnet"
        export PATH="$DOTNET_ROOT:$PATH"
    fi

    if [ -n "$LOCAL_SRC" ]; then
        SRC_DIR="$LOCAL_SRC"
        echo "  Using local source: ${SRC_DIR}"
    else
        # mktemp, not a fixed path: a re-run must not trip over the last clone.
        CLONE_DIR=$(mktemp -d)
        echo "  Cloning ${GITHUB_REPO} (${BRANCH})..."
        sudo apt-get install -y -qq git 2>/dev/null || true
        git clone --depth 1 --branch "${BRANCH}" "https://github.com/${GITHUB_REPO}.git" "${CLONE_DIR}"
        SRC_DIR="${CLONE_DIR}/GateControllerService"
    fi

    BUILD_OUT=$(mktemp -d)
    dotnet publish "${SRC_DIR}/GateControllerService.csproj" \
        -c Release -r linux-arm64 --self-contained true \
        -o "${BUILD_OUT}"
    sudo cp -r "${BUILD_OUT}/." "${INSTALL_DIR}/"
    rm -rf "${BUILD_OUT}" ${CLONE_DIR:+"${CLONE_DIR}"}
fi

# A database must never arrive from a package or a build — it would replace the
# site's own gate wiring. Drop stale write-ahead files with it.
sudo rm -f "${INSTALL_DIR}/gatecontrollerservice.db-wal" \
           "${INSTALL_DIR}/gatecontrollerservice.db-shm"
sudo chmod +x "${INSTALL_DIR}/GateControllerService" 2>/dev/null || true

# Urls has to be written into appsettings.json, not left to ASPNETCORE_URLS in
# the unit file. appsettings.json sits later in the configuration chain than the
# host's environment variables, so its value wins: with --port 5241 the unit
# would say 5241 while the app kept listening on the 5240 baked into the config,
# and the health poll below - which uses SERVICE_PORT - would then never get an
# answer, so the ServiceId and ServerUrl would silently never be applied.
if command -v python3 &> /dev/null; then
    sudo python3 -c "
import json
p = '${INSTALL_DIR}/appsettings.json'
with open(p) as f:
    config = json.load(f)
config['Urls'] = 'http://0.0.0.0:${SERVICE_PORT}'
with open(p, 'w') as f:
    json.dump(config, f, indent=2)
" && echo "  Listening on 0.0.0.0:${SERVICE_PORT}"
elif [ "${SERVICE_PORT}" != "5240" ]; then
    echo "  WARNING: python3 not found, so --port ${SERVICE_PORT} could not be written"
    echo "           into appsettings.json. The service will keep listening on 5240."
fi

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

# ---- systemd unit ----
echo "[4/5] Setting up the systemd service..."

if [ -f "${INSTALL_DIR}/GateControllerService" ]; then
    EXEC="${INSTALL_DIR}/GateControllerService"
else
    EXEC="${DOTNET_ROOT}/dotnet ${INSTALL_DIR}/GateControllerService.dll"
fi

sudo tee /etc/systemd/system/${SERVICE_NAME}.service > /dev/null << UNIT
[Unit]
Description=BasicWeigh Gate Controller Service
After=network-online.target

[Service]
Type=simple
ExecStart=${EXEC}
WorkingDirectory=${INSTALL_DIR}
Restart=always
RestartSec=5
User=${USER}
# Explicit so GPIO access never depends on when the user was added to the
# group relative to this unit being (re)started.
SupplementaryGroups=gpio
Environment=DOTNET_ROOT=${DOTNET_ROOT}
Environment=ASPNETCORE_URLS=http://0.0.0.0:${SERVICE_PORT}
Environment=DOTNET_ENVIRONMENT=Production

# Give the service time to drive its gates closed before it is killed. A
# SIGKILL here would strand a barrier in whatever state it was in.
TimeoutStopSec=20
KillSignal=SIGINT

NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable ${SERVICE_NAME}
sudo systemctl start ${SERVICE_NAME}

# ---- Point it at the web app ----
echo "[5/5] Applying settings..."
# appsettings.json only seeds the database while ServerUrl is still the factory
# default, and ServiceId is not re-read from config at all — so on an existing
# install neither would otherwise take effect. The API triggers a soft
# reconnect, no restart needed.
APPLIED=false
for _ in $(seq 1 20); do
    if curl -fsS --max-time 2 -o /dev/null "http://localhost:${SERVICE_PORT}/api/status/health" 2>/dev/null; then
        curl -fsS -X PUT "http://localhost:${SERVICE_PORT}/api/settings" \
            -H 'Content-Type: application/json' \
            -d "{\"serviceId\": \"${SERVICE_ID}\", \"serverUrl\": \"${WEB_URL}\"}" \
            -o /dev/null 2>/dev/null && APPLIED=true
        break
    fi
    sleep 1
done

echo ""
if sudo systemctl is-active --quiet ${SERVICE_NAME}; then
    echo "============================================"
    echo "  Install complete"
    echo "============================================"
    [ "$APPLIED" = true ] \
        && echo "  Applied ServiceId=${SERVICE_ID}, ServerUrl=${WEB_URL}" \
        || echo "  WARNING: the service started but would not take its settings."
    echo ""
    echo "  Next: add the gates wired to this Pi —"
    echo "    http://$(hostname -I | awk '{print $1}'):${SERVICE_PORT}/  (Swagger)"
    echo ""
    echo "  Then on the web app, set each Scale's Gate to"
    echo "    ${SERVICE_ID}:<gateId>"
    echo ""
    echo "  Check the wiring without a truck:"
    echo "    curl -X POST http://localhost:${SERVICE_PORT}/api/gates/<gateId>/test"
    echo ""
    echo "  Logs: journalctl -u ${SERVICE_NAME} -f"
else
    echo "ERROR: the service failed to start."
    echo "       journalctl -u ${SERVICE_NAME} -n 50 --no-pager"
    exit 1
fi
