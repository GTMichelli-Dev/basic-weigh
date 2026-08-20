#!/usr/bin/env bash
# pi-connect-github-auth.sh — bootstrap GitHub App git auth on a Pi that you
# can only reach through a terminal.
#
# The scp-based flow in docs/pi-git-auth.md assumes you can copy files onto
# the Pi. Raspberry Pi Connect's web shell gives you a terminal and nothing
# else, so this script takes the one thing a browser terminal can deliver —
# pasted text — and asks for the PEM directly.
#
# Paste into the Pi Connect shell (https://connect.raspberrypi.com/devices):
#
#   curl -fsSL -o /tmp/gh-auth.sh https://raw.githubusercontent.com/GTMichelli-Dev/foundation/main/scripts/pi-connect-github-auth.sh
#   bash /tmp/gh-auth.sh </dev/tty
#
# The `</dev/tty` matters: without it the prompts below would eat whatever
# else is sitting in the terminal's paste buffer instead of waiting for you.
#
# Idempotent — re-run it to refresh the helpers or replace the key.

set -euo pipefail

REPO_DIR="$HOME/foundation"
PEM_TMP="/tmp/michelli-app-$$.pem"

cleanup() {
  if [[ -f "$PEM_TMP" ]]; then
    shred -u "$PEM_TMP" 2>/dev/null || rm -f "$PEM_TMP"
  fi
}
trap cleanup EXIT

echo "== Michelli GitHub App auth bootstrap =="
echo

# 1. git — Pi OS Lite often ships without it.
if ! command -v git >/dev/null 2>&1; then
  echo "Installing git..."
  sudo apt-get update -y -qq
  sudo apt-get install -y -qq git
fi

# 2. Fetch the helper scripts. foundation is public, so this clone needs no
# credentials — which is the point: it is how the Pi gets the tooling it
# needs *before* it can authenticate to anything. Sparse checkout keeps it to
# the scripts folder rather than the whole monorepo.
echo "Fetching helper scripts from foundation (public repo, no auth needed)..."
if [[ -d "$REPO_DIR/.git" ]]; then
  git -C "$REPO_DIR" sparse-checkout set scripts
  git -C "$REPO_DIR" pull --ff-only --quiet
else
  git clone --quiet --filter=blob:none --sparse \
    https://github.com/GTMichelli-Dev/foundation.git "$REPO_DIR"
  git -C "$REPO_DIR" sparse-checkout set scripts
fi

# 3. Installation ID. Looping on empty tolerates the stray newline a paste
# usually leaves behind.
echo
echo "Installation ID — the number at the end of the App's install URL:"
echo "  https://github.com/organizations/GTMichelli-Dev/settings/installations/<NUMBER>"
INSTALL_ID=""
while [[ -z "$INSTALL_ID" ]]; do
  read -rp "Installation ID: " INSTALL_ID || { echo "No input." >&2; exit 1; }
  INSTALL_ID="${INSTALL_ID%$'\r'}"
done

# 4. The PEM, pasted. Everything before the BEGIN line is ignored, and the
# capture stops on its own at END — so a tech pastes the key and is done,
# with no terminator to remember.
echo
echo "Now paste the App's private key (.pem), BEGIN and END lines included."
echo "It stops by itself at the END line — nothing else to type."
echo
umask 077
: > "$PEM_TMP"
started=0
while IFS= read -r line; do
  line="${line%$'\r'}"        # a key copied on Windows arrives CRLF-terminated
  line="${line//$'\e'/}"        # ESC byte from a bracketed paste
  line="${line//[[]200~/}"      # ...and the markers Pi Connect leaves behind
  line="${line//[[]201~/}"
  if [[ $started -eq 0 ]]; then
    case "$line" in *"-----BEGIN"*) started=1 ;; *) continue ;; esac
  fi
  printf '%s\n' "$line" >> "$PEM_TMP"
  case "$line" in *"-----END"*"PRIVATE KEY-----") break ;; esac
done

if [[ $started -eq 0 ]]; then
  echo "No key found in what you pasted (no BEGIN line)." >&2
  exit 1
fi
if ! grep -q -- "-----END" "$PEM_TMP"; then
  echo "The key looks truncated — no END line arrived." >&2
  exit 1
fi
if command -v openssl >/dev/null 2>&1; then
  if ! openssl rsa -in "$PEM_TMP" -noout -check >/dev/null 2>&1; then
    echo "That is not a valid RSA private key. Re-copy the .pem and try again." >&2
    exit 1
  fi
  echo "Key looks valid."
fi

# 5. Hand off to the real installer.
echo
sudo bash "$REPO_DIR/scripts/setup-pi-github-app.sh" \
  --install-id "$INSTALL_ID" \
  --pem "$PEM_TMP"

echo
echo "Done. This Pi can now clone private GTMichelli-Dev repos."
echo "The pasted key was shredded from /tmp; the installed copy lives at"
echo "/etc/michelli/github-app.pem."
