#!/bin/bash
set -euo pipefail

# Deploy Foundation to a remote Debian server
# Usage: ./deploy.sh <user@host> [options]
#
# Options:
#   --domain <domain>    Domain name for Let's Encrypt SSL (e.g. scale.example.com)
#   --email <email>      Email for Let's Encrypt notifications
#   --port <port>        App listen port (default 5110)
#   --key <ssh-key>      SSH key file
#   --skip-build         Reuse the existing tarball instead of publishing fresh
#   --rebuild-db         Delete and recreate the database (CAUTION: destroys data!)
#   --reset-reports      Take the shipped ticket templates instead of keeping
#                        the site's saved ones (discards Report Designer edits)
#
# Examples:
#   ./deploy.sh admin@192.168.1.100
#   ./deploy.sh admin@192.168.1.100 --domain scale.example.com --email admin@example.com
#   ./deploy.sh admin@10.0.0.5 --port 8080 --key ~/.ssh/id_rsa

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARBALL="$SCRIPT_DIR/foundation-deploy.tar.gz"

# Parse arguments
REMOTE=""
DOMAIN=""
EMAIL=""
APP_PORT="5110"
SSH_KEY=""
SKIP_BUILD="0"
REBUILD_DB="0"
RESET_REPORTS="0"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --domain)  DOMAIN="$2";   shift 2 ;;
    --email)   EMAIL="$2";    shift 2 ;;
    --port)    APP_PORT="$2"; shift 2 ;;
    --skip-build) SKIP_BUILD="1"; shift ;;
    --rebuild-db) REBUILD_DB="1"; shift ;;
    --reset-reports) RESET_REPORTS="1"; shift ;;
    --key)     SSH_KEY="$2";  shift 2 ;;
    -*)        echo "Unknown option: $1"; exit 1 ;;
    *)         REMOTE="$1";   shift ;;
  esac
done

if [[ -z "$REMOTE" ]]; then
  echo "Usage: ./deploy.sh <user@host> [options]"
  echo ""
  echo "Options:"
  echo "  --domain <domain>    Domain for Let's Encrypt SSL"
  echo "  --email <email>      Email for Let's Encrypt"
  echo "  --port <port>        App port (default 5110)"
  echo "  --key <ssh-key>      SSH key file"
  echo ""
  echo "Examples:"
  echo "  ./deploy.sh admin@192.168.1.100"
  echo "  ./deploy.sh admin@192.168.1.100 --domain scale.example.com --email admin@example.com"
  exit 1
fi

# Build SSH options
SSH_OPTS="-o StrictHostKeyChecking=no"
SCP_OPTS="-o StrictHostKeyChecking=no"
if [[ -n "$SSH_KEY" ]]; then
  SSH_OPTS="$SSH_OPTS -i $SSH_KEY"
  SCP_OPTS="$SCP_OPTS -i $SSH_KEY"
fi

# Strip protocol prefix from domain if present
if [[ -n "$DOMAIN" ]]; then
  DOMAIN="${DOMAIN#https://}"
  DOMAIN="${DOMAIN#http://}"
  DOMAIN="${DOMAIN%/}"
fi

# Verify DNS resolves before deploying (if domain specified)
if [[ -n "$DOMAIN" ]]; then
  echo "==> Verifying DNS for $DOMAIN..."
  if ! nslookup "$DOMAIN" > /dev/null 2>&1; then
    echo "ERROR: DNS lookup failed for $DOMAIN"
    echo "       Create an A record pointing $DOMAIN to your server IP."
    echo "       Then wait for propagation and try again."
    exit 1
  fi
  echo "  DNS OK: $DOMAIN resolves."
fi

# Always publish a fresh build — a cached tarball silently deploys stale
# code. Pass --skip-build to reuse the existing tarball.
if [[ "$SKIP_BUILD" == "1" && -f "$TARBALL" ]]; then
  echo "==> Reusing existing tarball (--skip-build)."
else
  echo "==> Publishing fresh build..."
  bash "$SCRIPT_DIR/publish.sh"
fi

echo "==> Uploading to $REMOTE..."
scp $SCP_OPTS "$TARBALL" "$REMOTE:/tmp/foundation-deploy.tar.gz"

# Build install command with parameters
INSTALL_CMD="cd /tmp"
INSTALL_CMD="$INSTALL_CMD && mkdir -p /tmp/foundation-install"
INSTALL_CMD="$INSTALL_CMD && tar -xzf /tmp/foundation-deploy.tar.gz -C /tmp/foundation-install"
INSTALL_CMD="$INSTALL_CMD && cd /tmp/foundation-install"
INSTALL_CMD="$INSTALL_CMD && sed -i 's/\r$//' install.sh"
INSTALL_CMD="$INSTALL_CMD && sudo DOMAIN='$DOMAIN' EMAIL='$EMAIL' PORT='$APP_PORT' REBUILD_DB='$REBUILD_DB' RESET_REPORTS='$RESET_REPORTS' bash install.sh"
INSTALL_CMD="$INSTALL_CMD && rm -rf /tmp/foundation-install /tmp/foundation-deploy.tar.gz"

echo "==> Installing on remote server..."
ssh $SSH_OPTS "$REMOTE" "$INSTALL_CMD"

echo ""
echo "=========================================="
echo "  Deploy complete!"
echo "=========================================="
echo "  Server: $REMOTE"
if [[ -n "$DOMAIN" ]]; then
echo "  URL:    https://$DOMAIN"
else
echo "  URL:    https://${REMOTE#*@}"
fi
echo "  Check:  ssh $REMOTE 'systemctl status foundation'"
echo "=========================================="
