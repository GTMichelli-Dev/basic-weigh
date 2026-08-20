#!/bin/bash
set -euo pipefail

# One-shot deploy of the full scale-house stack to a Raspberry Pi:
#
#   1. Foundation web app   — built here, Kestrel direct on port 80
#   2. Scale Reader Service — cloned + built on the Pi (serial/TCP scales)
#   3. Web Print Service    — cloned + built on the Pi (CUPS / Bixolon)
#   4. Pi Network Setup     — tech Wi-Fi access point + phone config page
#
# Usage: ./deploy-pi-all.sh <user@host> [options]
#
# Options:
#   --server-url <url>     URL the services use to reach the web app
#                          (default http://127.0.0.1 — same-Pi install)
#   --printer-name <name>  CUPS queue name (default TicketPrinter)
#   --key <ssh-key>        SSH key file
#   --skip-web             Don't deploy the web app
#   --skip-scale           Don't install the scale reader service
#   --skip-print           Don't install the print service
#   --skip-net             Don't install the network setup helper (tech AP)
#
# Prerequisites:
#   - The Pi is bootstrapped with the GitHub App credential helper
#     (scripts/setup-pi-github-app.sh) so it can clone the service repos.
#   - Re-running is safe: every installer preserves its database/settings.
#
# Tip: with password auth you'll be prompted several times. Run
#   ssh-copy-id <user@host>
# once first and the whole deploy goes through with no prompts.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

REMOTE=""
SSH_KEY=""
SERVER_URL="http://127.0.0.1"
PRINTER_NAME="TicketPrinter"
SKIP_WEB=0
SKIP_SCALE=0
SKIP_PRINT=0
SKIP_NET=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server-url)   SERVER_URL="$2";   shift 2 ;;
    --printer-name) PRINTER_NAME="$2"; shift 2 ;;
    --key)          SSH_KEY="$2";      shift 2 ;;
    --skip-web)     SKIP_WEB=1;   shift ;;
    --skip-scale)   SKIP_SCALE=1; shift ;;
    --skip-print)   SKIP_PRINT=1; shift ;;
    --skip-net)     SKIP_NET=1;   shift ;;
    --help|-h)
      sed -n '4,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    -*)             echo "Unknown option: $1 (use --help)"; exit 1 ;;
    *)              REMOTE="$1";       shift ;;
  esac
done

if [[ -z "$REMOTE" ]]; then
  echo "Usage: ./deploy-pi-all.sh <user@host> [--server-url <url>] [--printer-name <name>] [--key <ssh-key>]"
  echo "Run with --help for all options."
  exit 1
fi

SSH_OPTS="-o StrictHostKeyChecking=no"
if [[ -n "$SSH_KEY" ]]; then
  SSH_OPTS="$SSH_OPTS -i $SSH_KEY"
fi

echo "============================================"
echo "  Foundation full-stack Pi deploy"
echo "============================================"
echo "  Pi:           $REMOTE"
echo "  Services URL: $SERVER_URL"
echo "  Printer:      $PRINTER_NAME"
echo "  Steps:        web=$((1-SKIP_WEB)) scale=$((1-SKIP_SCALE)) print=$((1-SKIP_PRINT)) net=$((1-SKIP_NET))"
echo "============================================"
echo ""

#--------------------------------------------------
# 0. Pre-flight: the Pi must be able to clone private repos
#--------------------------------------------------
if [[ "$SKIP_SCALE" == "0" || "$SKIP_PRINT" == "0" || "$SKIP_NET" == "0" ]]; then
  echo "==> [0/4] Checking GitHub access on the Pi..."
  GIT_CHECK_OUT=$(ssh $SSH_OPTS "$REMOTE" "git ls-remote https://github.com/GTMichelli-Dev/foundation.git HEAD" 2>&1) && GIT_CHECK_OK=1 || GIT_CHECK_OK=0
  if [[ "$GIT_CHECK_OK" == "1" ]]; then
    echo "  Git auth OK."
  else
    echo "ERROR: the Pi cannot clone GTMichelli-Dev repos. Underlying error:"
    echo "----------------------------------------"
    echo "$GIT_CHECK_OUT" | tail -5
    echo "----------------------------------------"
    echo "       Bootstrap it first — see docs/pi-git-auth.md:"
    echo "         scp scripts/setup-pi-github-app.sh scripts/michelli-github-app-token.sh \\"
    echo "             scripts/git-credential-michelli.sh <pem> $REMOTE:/tmp/"
    echo "         ssh $REMOTE 'sudo bash /tmp/setup-pi-github-app.sh --install-id <ID> --pem /tmp/<pem>'"
    exit 1
  fi
fi

#--------------------------------------------------
# 1. Foundation web app (built locally, pushed as tarball)
#--------------------------------------------------
if [[ "$SKIP_WEB" == "0" ]]; then
  echo ""
  echo "==> [1/4] Deploying Foundation web app..."
  if [[ -n "$SSH_KEY" ]]; then
    bash "$SCRIPT_DIR/deploy-pi-web.sh" "$REMOTE" --key "$SSH_KEY"
  else
    bash "$SCRIPT_DIR/deploy-pi-web.sh" "$REMOTE"
  fi
else
  echo ""
  echo "==> [1/4] Skipping web app (--skip-web)."
fi

#--------------------------------------------------
# 2. Scale Reader Service (cloned + built on the Pi)
#--------------------------------------------------
if [[ "$SKIP_SCALE" == "0" ]]; then
  echo ""
  echo "==> [2/4] Installing Scale Reader Service on the Pi..."
  ssh $SSH_OPTS "$REMOTE" "
    rm -rf /tmp/srs && \
    git clone -q --depth 1 https://github.com/GTMichelli-Dev/scale-reader-service.git /tmp/srs && \
    bash /tmp/srs/deploy/install.sh '$SERVER_URL' && \
    rm -rf /tmp/srs
  "
else
  echo ""
  echo "==> [2/4] Skipping scale reader (--skip-scale)."
fi

#--------------------------------------------------
# 3. Web Print Service (cloned + built on the Pi)
#--------------------------------------------------
# ssh -t allocates a TTY so the installer's interactive guards still work
# (e.g. the generic-hostname warning lets you answer y/N). The printer name
# is passed explicitly so there's no prompt for it.
if [[ "$SKIP_PRINT" == "0" ]]; then
  echo ""
  echo "==> [3/4] Installing Web Print Service on the Pi..."
  ssh -t $SSH_OPTS "$REMOTE" "
    rm -rf /tmp/wps && \
    git clone -q --depth 1 https://github.com/GTMichelli-Dev/web-print-service.git /tmp/wps && \
    bash /tmp/wps/deploy/install.sh '$SERVER_URL' --printer-name '$PRINTER_NAME' && \
    rm -rf /tmp/wps
  "
else
  echo ""
  echo "==> [3/4] Skipping print service (--skip-print)."
fi

#--------------------------------------------------
# 4. Pi Network Setup (tech Wi-Fi access point + phone config page)
#--------------------------------------------------
# Idempotent: re-running refreshes the app and recreates the AP profile with
# the SSID derived from this Pi's Wi-Fi MAC (config.env defaults).
# Pinned to a release tag (unlike the services above, which track main) so a
# field deploy installs a known AP config rather than whatever just landed.
if [[ "$SKIP_NET" == "0" ]]; then
  echo ""
  echo "==> [4/4] Installing Pi Network Setup (tech access point)..."
  ssh $SSH_OPTS "$REMOTE" "
    rm -rf /tmp/pns && \
    git clone -q --depth 1 --branch v1.0.0 https://github.com/GTMichelli-Dev/pi-network-setup.git /tmp/pns && \
    sudo bash /tmp/pns/install.sh && \
    rm -rf /tmp/pns
  "
else
  echo ""
  echo "==> [4/4] Skipping network setup helper (--skip-net)."
fi

#--------------------------------------------------
# Done
#--------------------------------------------------
PI_HOST="${REMOTE#*@}"
echo ""
echo "============================================"
echo "  Full-stack deploy complete!"
echo "============================================"
echo "  Web app:       http://$PI_HOST"
echo "  Scale reader:  http://$PI_HOST:5220/swagger"
echo "  Print service: http://$PI_HOST:5230/swagger"
echo "  CUPS admin:    http://$PI_HOST:631"
if [[ "$SKIP_NET" == "0" ]]; then
echo "  Tech AP:       SSID Michelli-<MAC suffix>, setup page http://192.168.50.1:8080"
fi
echo ""
echo "  Check services:"
echo "    ssh $REMOTE 'systemctl status foundation scale-reader-service web-print-service netsetup-web'"
echo ""
echo "  Next (first install only):"
echo "    - Register the scale:   http://$PI_HOST:5220/swagger (serial: /dev/ttyUSB0, 9600,8,N,1)"
echo "    - Assign the printer:   http://$PI_HOST -> Setup -> Printers -> $PRINTER_NAME"
echo "    - Enable remote print:  http://$PI_HOST -> Setup -> Remote Print Mode"
echo "============================================"
