#!/bin/bash
# =============================================================================
# RFID Reader Service - Self-Install Script for Raspberry Pi / Linux
# =============================================================================
# Run directly on the machine the card reader is plugged into:
#
#   git clone https://github.com/GTMichelli-Dev/rfid-reader-service.git /tmp/rrs
#   bash /tmp/rrs/deploy/install.sh <web-server-url>
#   rm -rf /tmp/rrs
#
# Examples:
#   # Production: web app on a real hostname
#   bash /tmp/rrs/deploy/install.sh https://basicscale.scaledata.net
#
#   # LAN Pi: web app on the same Pi listening on port 80
#   bash /tmp/rrs/deploy/install.sh http://localhost
#
#   # Local dev: web app launched via `dotnet run` on port 5110
#   bash /tmp/rrs/deploy/install.sh http://localhost:5110 --service-id kiosk-1
#
# The URL must match the web app's *actual* listen port — a wrong URL puts the
# service into an endless "Connection refused" reconnect loop, visible with
# `journalctl -u rfid-reader-service -f`.
#
# Re-run the same command to update: the service stops, files are replaced, the
# reader database is preserved, and the service restarts.
#
# Until this lives in its own repo, install from the monorepo instead:
#   git clone https://github.com/GTMichelli-Dev/foundation.git /tmp/fnd
#   bash /tmp/fnd/RfidReaderService/deploy/install.sh <url> --local /tmp/fnd/RfidReaderService
# =============================================================================

set -e

# ---- Defaults ----
SERVICE_ID="default"
SERVICE_PORT="5230"
INSTALL_DIR="/opt/rfid-reader-service"
SERVICE_NAME="rfid-reader-service"
DOTNET_CHANNEL="10.0"
GITHUB_REPO="GTMichelli-Dev/rfid-reader-service"
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
            echo "Usage: install.sh <web-server-url> [options]"
            echo ""
            echo "  <web-server-url>       Required. URL of the BasicWeigh web server."
            echo "                         Must match the web app's actual listen port."
            echo ""
            echo "Options:"
            echo "  --service-id <id>      Unique ID for this service (default: default)"
            echo "                         Kiosks map to readers as 'serviceId:readerId'."
            echo "  --port <port>          API port (default: 5230)"
            echo "  --branch <branch>      Git branch to install (default: main)"
            echo "  --install-dir <path>   Install location (default: /opt/rfid-reader-service)"
            echo "  --local <path>         Build from a local source folder instead of cloning"
            echo "  --help                 Show this help"
            exit 0
            ;;
        -*)
            echo "Unknown option: $1 (use --help for usage)"
            exit 1
            ;;
        *)
            if [ -z "$WEB_URL" ]; then
                WEB_URL="$1"
            else
                echo "Unknown argument: $1"
                exit 1
            fi
            shift
            ;;
    esac
done

if [ -z "$WEB_URL" ]; then
    echo "ERROR: Web server URL is required."
    echo ""
    echo "  bash install.sh https://basicscale.scaledata.net   # production"
    echo "  bash install.sh http://localhost                   # LAN Pi, web on :80"
    echo ""
    echo "Run with --help for all options."
    exit 1
fi

echo ""
echo "============================================"
echo "  RFID Reader Service - Install"
echo "============================================"
echo "  Web Server:   ${WEB_URL}"
echo "  Service ID:   ${SERVICE_ID}"
echo "  Port:         ${SERVICE_PORT}"
echo "  Install Dir:  ${INSTALL_DIR}"
if [ -n "$LOCAL_SRC" ]; then
    echo "  Source:       ${LOCAL_SRC} (local)"
else
    echo "  Source:       ${GITHUB_REPO} (${BRANCH})"
fi
echo "============================================"
echo ""

# ---- Detect architecture ----
echo "[1/5] Detecting system..."
ARCH=$(uname -m)
case "$ARCH" in
    aarch64) RID="linux-arm64" ;;
    armv7l)  RID="linux-arm" ;;
    x86_64)  RID="linux-x64" ;;
    *)       echo "WARNING: Unknown arch '${ARCH}', trying linux-x64"; RID="linux-x64" ;;
esac
echo "  OS: $(uname -s) $(uname -r)"
echo "  Architecture: ${ARCH} (${RID})"

# ---- Serial port access ----
# The SP-6820 reaches the Pi through a USB-serial adapter (/dev/ttyUSB0) owned
# by group 'dialout'. Without membership every port open fails with
# "Access to the port is denied" and the reader looks dead.
if ! id -nG "$USER" 2>/dev/null | tr ' ' '\n' | grep -qx 'dialout'; then
    sudo usermod -aG dialout "$USER"
    echo "  Added $USER to dialout group (serial port access)."
    echo "  NOTE: applies to the systemd service immediately (SupplementaryGroups below);"
    echo "        interactive shells need a logout/login."
else
    echo "  $USER already in dialout group (serial port access)."
fi

# ---- Firewall ----
if command -v ufw &> /dev/null && sudo ufw status | grep -q "active"; then
    sudo ufw allow 22/tcp > /dev/null
    sudo ufw allow "${SERVICE_PORT}"/tcp > /dev/null
    echo "  Firewall: ufw — ports 22 and ${SERVICE_PORT} opened."
fi
if command -v iptables &> /dev/null; then
    sudo iptables -C INPUT -p tcp --dport "${SERVICE_PORT}" -j ACCEPT 2>/dev/null || \
        sudo iptables -I INPUT -p tcp --dport "${SERVICE_PORT}" -j ACCEPT
    if command -v netfilter-persistent &> /dev/null; then
        sudo netfilter-persistent save 2>/dev/null || true
    elif command -v iptables-save &> /dev/null; then
        sudo mkdir -p /etc/iptables
        sudo sh -c 'iptables-save > /etc/iptables/rules.v4' 2>/dev/null || true
    fi
    echo "  Firewall: iptables — port ${SERVICE_PORT} opened and persisted."
fi

# ---- Install .NET ----
echo "[2/5] Installing .NET runtime..."
DOTNET_ROOT="$HOME/.dotnet"

if [ -x "$DOTNET_ROOT/dotnet" ]; then
    echo "  .NET already installed: $("$DOTNET_ROOT/dotnet" --version 2>/dev/null || echo unknown)"
elif command -v dotnet &> /dev/null; then
    DOTNET_ROOT=$(dirname "$(which dotnet)")
    echo "  .NET already installed: $(dotnet --version 2>/dev/null || echo unknown)"
else
    echo "  Downloading .NET ${DOTNET_CHANNEL} ASP.NET Core runtime..."
    sudo apt-get update -qq 2>/dev/null || true
    sudo apt-get install -y -qq curl libicu-dev 2>/dev/null || true
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin \
        --channel ${DOTNET_CHANNEL} \
        --runtime aspnetcore \
        --install-dir "$DOTNET_ROOT"
    echo "  .NET installed: $($DOTNET_ROOT/dotnet --version)"
fi

export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_ROOT

if ! grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null; then
    {
        echo ""
        echo "# .NET"
        echo "export DOTNET_ROOT=$DOTNET_ROOT"
        echo 'export PATH=$DOTNET_ROOT:$PATH'
    } >> "$HOME/.bashrc"
    echo "  Added .NET to PATH in .bashrc"
fi

# ---- Download and build ----
echo "[3/5] Building RFID Reader Service..."

sudo systemctl stop ${SERVICE_NAME} 2>/dev/null || true

sudo mkdir -p "${INSTALL_DIR}"
sudo chown "$USER:$USER" "${INSTALL_DIR}"

# Preserve the reader configuration across updates.
DB_BACKUP=""
if [ -f "${INSTALL_DIR}/rfidreaderservice.db" ]; then
    DB_BACKUP="/tmp/rfidreaderservice-db-backup.db"
    cp "${INSTALL_DIR}/rfidreaderservice.db" "$DB_BACKUP"
    echo "  Backed up existing reader database."
fi

CLEANUP_CLONE=false
if [ -n "$LOCAL_SRC" ]; then
    SRC_DIR="$LOCAL_SRC"
    echo "  Using local source: ${SRC_DIR}"
else
    SRC_DIR=$(mktemp -d)
    CLEANUP_CLONE=true
    echo "  Cloning ${GITHUB_REPO} (${BRANCH})..."
    sudo apt-get install -y -qq git 2>/dev/null || true
    git clone --depth 1 --branch "${BRANCH}" "https://github.com/${GITHUB_REPO}.git" "${SRC_DIR}"
fi

# The SDK is needed to build; match the major version of DOTNET_CHANNEL.
DOTNET_MAJOR="${DOTNET_CHANNEL%%.*}"
if dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_MAJOR}\."; then
    echo "  .NET SDK already installed."
else
    echo "  Installing .NET SDK (reused on future updates)..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin \
        --channel ${DOTNET_CHANNEL} \
        --install-dir "$DOTNET_ROOT"
fi

echo "  Building..."
dotnet publish "${SRC_DIR}/RfidReaderService.csproj" \
    -c Release \
    -r "${RID}" \
    --self-contained true \
    -o "${INSTALL_DIR}" \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false

if [ "$CLEANUP_CLONE" = true ]; then
    rm -rf "${SRC_DIR}"
fi

if [ -n "$DB_BACKUP" ] && [ -f "$DB_BACKUP" ]; then
    cp "$DB_BACKUP" "${INSTALL_DIR}/rfidreaderservice.db"
    rm "$DB_BACKUP"
    echo "  Restored existing reader database."
fi

chmod +x "${INSTALL_DIR}/RfidReaderService" 2>/dev/null || true

# ---- Configure ----
echo "[4/5] Configuring..."
if [ -f "${INSTALL_DIR}/appsettings.json" ] && command -v python3 &> /dev/null; then
    python3 -c "
import json
with open('${INSTALL_DIR}/appsettings.json', 'r') as f:
    config = json.load(f)
config.setdefault('Rfid', {})
config['Rfid']['ServerUrl'] = '${WEB_URL}'
config['Rfid']['ServiceId'] = '${SERVICE_ID}'
with open('${INSTALL_DIR}/appsettings.json', 'w') as f:
    json.dump(config, f, indent=2)
"
    echo "  Updated appsettings.json"
fi

# ---- systemd ----
echo "[5/5] Setting up systemd service..."

if [ -f "${INSTALL_DIR}/RfidReaderService" ]; then
    EXEC="${INSTALL_DIR}/RfidReaderService"
else
    EXEC="${DOTNET_ROOT}/dotnet ${INSTALL_DIR}/RfidReaderService.dll"
fi

sudo tee /etc/systemd/system/${SERVICE_NAME}.service > /dev/null << UNIT
[Unit]
Description=RFID Reader Service
After=network.target

[Service]
Type=simple
ExecStart=${EXEC}
WorkingDirectory=${INSTALL_DIR}
Restart=always
RestartSec=5
User=${USER}
# Explicit so /dev/ttyUSB* access never depends on when the user was added to
# dialout relative to this unit being (re)started.
SupplementaryGroups=dialout
Environment=DOTNET_ROOT=${DOTNET_ROOT}
Environment=ASPNETCORE_URLS=http://0.0.0.0:${SERVICE_PORT}
Environment=DOTNET_ENVIRONMENT=Production

NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable ${SERVICE_NAME}
sudo systemctl start ${SERVICE_NAME}

sleep 3

IP=$(hostname -I | awk '{print $1}')
echo ""
if sudo systemctl is-active --quiet ${SERVICE_NAME}; then
    echo "============================================"
    echo "  Install Complete!"
    echo "============================================"
    echo "  Service URL:  http://${IP}:${SERVICE_PORT}"
    echo "  Swagger:      http://${IP}:${SERVICE_PORT}/swagger"
    echo "  Web Server:   ${WEB_URL}"
    echo "  Service ID:   ${SERVICE_ID}"
    echo ""
    echo "  Commands:"
    echo "    sudo systemctl status ${SERVICE_NAME}"
    echo "    sudo systemctl restart ${SERVICE_NAME}"
    echo "    sudo journalctl -u ${SERVICE_NAME} -f"
    echo ""
    echo "  Next: configure the reader from the web app"
    echo "    Setup -> Options -> Card Readers"
    echo "  or locally:"
    echo "    curl http://localhost:${SERVICE_PORT}/api/serialports"
    echo "    curl http://localhost:${SERVICE_PORT}/api/status"
    echo ""
    echo "  Present a card, then check what the reader actually sent:"
    echo "    curl http://localhost:${SERVICE_PORT}/api/readers/<reader-id>/frames"
    echo ""
    echo "  Verify the hub connection (no 'Connection refused'):"
    echo "    sudo journalctl -u ${SERVICE_NAME} -n 20 --no-pager | grep -E 'Connect|refused'"
    echo "============================================"
else
    echo "============================================"
    echo "  WARNING: Service may not have started."
    echo "============================================"
    echo "    sudo journalctl -u ${SERVICE_NAME} -n 30 --no-pager"
    echo "============================================"
fi
