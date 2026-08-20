#!/usr/bin/env bash
# setup-pi-github-app.sh — install the Michelli GitHub App credential helper
# on a Raspberry Pi. After this runs once, plain `git clone https://github.com/
# GTMichelli-Dev/...` and `git pull` work silently against any private repo
# the App is installed on (foundation, web-print-service, pi-network-setup,
# camera-capture-service, scale-reader-service, qb-sync-service, ...).
#
# Full walkthrough, including creating the GitHub App the first time:
# docs/pi-git-auth.md in the foundation repo.
#
# Bootstrap order on a fresh Pi:
#   1. scp the App's PEM private key onto the Pi (e.g. to /tmp/michelli-app.pem)
#   2. Run this script as root:
#        sudo bash setup-pi-github-app.sh \
#            --install-id <INSTALL_ID> \
#            --pem /tmp/michelli-app.pem
#   3. Verify: `git ls-remote https://github.com/GTMichelli-Dev/pi-network-setup.git`
#      succeeds with no prompt. Test against a PRIVATE repo — foundation and
#      the service repos are public and succeed without any auth at all.
#   4. Shred the source PEM: `shred -u /tmp/michelli-app.pem`
#
# Idempotent. Re-running re-installs the helpers (picks up newer versions
# from the repo) and updates the config without disturbing the PEM if it's
# already in place. Pass `--pem` again to replace the PEM.
#
# Perms model: /etc/michelli is 0755 (traversable), the conf is 0644 (public
# IDs), the PEM is 0644 (readable by any local user). On a single-user
# kiosk/scale Pi that's fine — anyone with a shell on the box already has
# sudo. On a multi-tenant host you'd want group-restricted perms instead.
#
# Requires: bash, openssl (preinstalled on Pi OS), curl + jq (apt-installed
# by this script if missing).

set -euo pipefail

# Baked-in App identity. FILL THESE IN once the Michelli GitHub App is
# created (see docs/pi-git-auth.md) so operators only need to supply
# --install-id and --pem the first time. Until then, pass --client-id.
CLIENT_ID_DEFAULT="Iv23livFZQOXhMbgSTed"   # GTMichelli-Dev michelli-fleet App Client ID
APP_ID_DEFAULT="4260960"                    # GTMichelli-Dev michelli-fleet App numeric App ID

CLIENT_ID=""
APP_ID=""
INSTALL_ID=""
PEM_SRC=""

CONF_DIR="/etc/michelli"
PEM_DST="$CONF_DIR/github-app.pem"
CONF_DST="$CONF_DIR/github-app.conf"

TOKEN_BIN="/usr/local/bin/michelli-github-app-token"
HELPER_BIN="/usr/local/bin/git-credential-michelli"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --client-id)    CLIENT_ID="$2"; shift 2 ;;
    --app-id)       APP_ID="$2"; shift 2 ;;
    --install-id)   INSTALL_ID="$2"; shift 2 ;;
    --pem)          PEM_SRC="$2"; shift 2 ;;
    -h|--help)
      cat <<EOF
Usage: sudo bash setup-pi-github-app.sh [options]

Required on first run (and any time the Installation changes):
  --install-id <N>       Installation ID (from the install URL on the App's page)
  --pem <path>           Copy this PEM to $PEM_DST

App identity (bake the defaults into this script after creating the App):
  --client-id <ID>       Michelli App Client ID${CLIENT_ID_DEFAULT:+ (default: $CLIENT_ID_DEFAULT)}
  --app-id <N>           Michelli App numeric App ID${APP_ID_DEFAULT:+ (default: $APP_ID_DEFAULT)}

On re-runs (e.g. after \`git pull\` updates the helper scripts), pass no flags
to refresh helpers in place — existing conf + PEM are reused.

After this:
  git ls-remote https://github.com/GTMichelli-Dev/pi-network-setup.git
should succeed silently. Then any service install script can \`git clone\` /
\`git pull\` over plain HTTPS — no PAT, no SSH config.
EOF
      exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

# Apply baked-in defaults when no override was passed.
CLIENT_ID="${CLIENT_ID:-$CLIENT_ID_DEFAULT}"
APP_ID="${APP_ID:-$APP_ID_DEFAULT}"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "This script must run as root (use sudo)." >&2
  exit 1
fi

# 1. Apt-install deps if missing.
need_apt=()
command -v curl    >/dev/null 2>&1 || need_apt+=(curl)
command -v jq      >/dev/null 2>&1 || need_apt+=(jq)
command -v openssl >/dev/null 2>&1 || need_apt+=(openssl)
if [[ ${#need_apt[@]} -gt 0 ]]; then
  echo "Installing: ${need_apt[*]}"
  apt-get update -qq
  apt-get install -y -qq "${need_apt[@]}"
fi

# 2. Conf dir — world-traversable so the helper running as a regular user can
#    read the conf + PEM.
install -d -m 0755 "$CONF_DIR"

# 3. Install helpers from the repo (this script lives in scripts/, helpers next to it).
if [[ ! -f "$SCRIPT_DIR/michelli-github-app-token.sh" || ! -f "$SCRIPT_DIR/git-credential-michelli.sh" ]]; then
  echo "Cannot find helper scripts alongside $SCRIPT_DIR — make sure you ran this from a repo checkout." >&2
  exit 1
fi
install -m 0755 "$SCRIPT_DIR/michelli-github-app-token.sh" "$TOKEN_BIN"
install -m 0755 "$SCRIPT_DIR/git-credential-michelli.sh"   "$HELPER_BIN"
echo "Installed $TOKEN_BIN and $HELPER_BIN"

# 4. Place / update conf. Write whenever an install ID was passed; otherwise
# require an existing conf.
if [[ -n "$INSTALL_ID" ]]; then
  if [[ -z "$CLIENT_ID" && -z "$APP_ID" ]]; then
    echo "No --client-id / --app-id passed and no default baked into this script." >&2
    echo "Create the App first (docs/pi-git-auth.md), then pass --client-id." >&2
    exit 1
  fi
  cat > "$CONF_DST" <<EOF
# Michelli GitHub App — installation token config. Public IDs only; the secret
# is the PEM. CLIENT_ID is the modern issuer identifier (GitHub's
# recommendation); APP_ID is kept as a legacy alternative. Either works.
CLIENT_ID=$CLIENT_ID
APP_ID=$APP_ID
INSTALL_ID=$INSTALL_ID
EOF
  chmod 0644 "$CONF_DST"
  echo "Wrote $CONF_DST"
elif [[ ! -f "$CONF_DST" ]]; then
  echo "Missing $CONF_DST and no --install-id provided. Re-run with --install-id." >&2
  exit 1
fi

# 5. Place PEM if provided. Mode 0644 — see header for the kiosk-Pi rationale.
if [[ -n "$PEM_SRC" ]]; then
  [[ -r "$PEM_SRC" ]] || { echo "Cannot read PEM at $PEM_SRC" >&2; exit 1; }
  install -m 0644 "$PEM_SRC" "$PEM_DST"
  echo "Installed PEM at $PEM_DST (mode 0644)"
  echo "Reminder: shred the source PEM at $PEM_SRC when you're done:"
  echo "  shred -u '$PEM_SRC'"
elif [[ ! -f "$PEM_DST" ]]; then
  echo "Missing $PEM_DST and no --pem provided. Place the App PEM first, then re-run." >&2
  exit 1
fi

# 6. Register the credential helper system-wide for github.com/GTMichelli-Dev.
#    --replace-all keeps the gitconfig idempotent across re-runs. Explicit
#    umask 022 in case the surrounding shell tightened it.
umask 022
git config --system --replace-all "credential.https://github.com/GTMichelli-Dev.helper" "$HELPER_BIN"
git config --system --replace-all "credential.https://github.com/GTMichelli-Dev.useHttpPath" "true"
chmod 0644 /etc/gitconfig 2>/dev/null || true
echo "Registered credential helper in /etc/gitconfig for github.com/GTMichelli-Dev"

# 7. Smoke-test by minting a token and hitting ls-remote against a PRIVATE repo.
# It must be private: foundation and the service repos went public, and
# ls-remote against a public repo succeeds even with no credential helper at
# all, so it would pass on a Pi where the App auth is entirely broken.
SMOKE_REPO="pi-network-setup"
echo
echo "Smoke-testing..."
TOKEN=$("$TOKEN_BIN")
if [[ -n "$TOKEN" ]]; then
  echo "Token minted ($(printf '%s' "$TOKEN" | wc -c) chars)."
else
  echo "Token mint returned empty output." >&2
  exit 1
fi

if git ls-remote "https://github.com/GTMichelli-Dev/$SMOKE_REPO.git" HEAD >/dev/null 2>&1; then
  echo "git ls-remote succeeded against the private $SMOKE_REPO repo. Setup done."
else
  echo "git ls-remote failed — check App permissions (Contents: Read) and Installation repo selection." >&2
  exit 1
fi
