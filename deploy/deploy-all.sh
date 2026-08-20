#!/bin/bash
# Continue past a failed site rather than aborting the whole run, so one
# unreachable box doesn't strand the rest of the fleet undeployed.
set -uo pipefail

# Deploy Foundation to every site in a manifest, in one pass.
#
# Usage: ./deploy-all.sh --email <address> [options]
#
# Options:
#   --sites <file>       Site manifest (default: deploy/sites.txt)
#   --email <email>      Let's Encrypt email, shared by every site. Required
#                        unless every manifest line is already on a valid cert.
#   --key <ssh-key>      SSH key file, used for every site
#   --port <port>        Default app port for sites that don't name one
#   --setup-keys         Install your SSH public key on every host, then exit.
#                        Prompts once per host; afterwards deploys need no
#                        password at all. Run this once per fleet.
#   --skip-build         Reuse the existing tarball instead of publishing fresh
#   --dry-run            Print what would be deployed and exit
#
# Examples:
#   ./deploy-all.sh --setup-keys
#   ./deploy-all.sh --email ssolleder@michelli.com
#   ./deploy-all.sh --email ssolleder@michelli.com --sites deploy/west.txt
#
# On passwords: there is deliberately no way to hand this script a password.
# SSH asks twice per site (once to upload, once to install), so a fleet of ten
# is twenty prompts, and any file holding that password becomes the fleet's
# single point of compromise. --setup-keys pays the prompts once and removes
# them permanently.
#
# --rebuild-db is deliberately NOT forwarded. It destroys the database, and a
# fleet-wide wipe should never be one flag away. Use deploy.sh for that, one
# site at a time, on purpose.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARBALL="$SCRIPT_DIR/foundation-deploy.tar.gz"

SITES_FILE="$SCRIPT_DIR/sites.txt"
EMAIL=""
SSH_KEY=""
DEFAULT_PORT="5110"
SETUP_KEYS=0
SKIP_BUILD=0
DRY_RUN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --sites)       SITES_FILE="$2";   shift 2 ;;
    --email)       EMAIL="$2";        shift 2 ;;
    --key)         SSH_KEY="$2";      shift 2 ;;
    --port)        DEFAULT_PORT="$2"; shift 2 ;;
    --setup-keys)  SETUP_KEYS=1;      shift ;;
    --skip-build)  SKIP_BUILD=1;      shift ;;
    --dry-run)     DRY_RUN=1;         shift ;;
    --help|-h)     sed -n '6,36p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)             echo "Unknown option: $1 (use --help)"; exit 1 ;;
  esac
done

if [[ ! -f "$SITES_FILE" ]]; then
  echo "ERROR: No site manifest at $SITES_FILE"
  echo "       Copy deploy/sites.example.txt to deploy/sites.txt and edit it,"
  echo "       or point at another file with --sites."
  exit 1
fi

# ---- Parse the manifest -------------------------------------------------
# Held in parallel arrays so the deploy loop below stays readable.
HOSTS=(); DOMAINS=(); PORTS=()

while IFS= read -r line || [[ -n "$line" ]]; do
  line="${line%%#*}"          # strip comments
  line="${line//$'\r'/}"      # tolerate a CRLF manifest edited on Windows
  # shellcheck disable=SC2086
  set -- $line                # collapse whitespace into positional args
  [[ $# -eq 0 ]] && continue

  host="$1"
  domain="${2:-${host#*@}}"   # default the domain to the host we SSH to
  port="${3:-$DEFAULT_PORT}"

  HOSTS+=("$host"); DOMAINS+=("$domain"); PORTS+=("$port")
done < "$SITES_FILE"

if [[ ${#HOSTS[@]} -eq 0 ]]; then
  echo "ERROR: $SITES_FILE lists no sites."
  exit 1
fi

echo "==> ${#HOSTS[@]} site(s) from $(basename "$SITES_FILE"):"
for i in "${!HOSTS[@]}"; do
  printf '      %-40s -> https://%s (port %s)\n' "${HOSTS[$i]}" "${DOMAINS[$i]}" "${PORTS[$i]}"
done
echo ""

# ---- Key setup mode -----------------------------------------------------
# One password per host, once, and every deploy after this is silent.
if [[ "$SETUP_KEYS" == "1" ]]; then
  PUBKEY="${SSH_KEY:+${SSH_KEY}.pub}"
  if [[ -z "$PUBKEY" ]]; then
    for candidate in ~/.ssh/id_ed25519.pub ~/.ssh/id_rsa.pub; do
      [[ -f "$candidate" ]] && { PUBKEY="$candidate"; break; }
    done
  fi
  if [[ -z "$PUBKEY" || ! -f "$PUBKEY" ]]; then
    echo "ERROR: No SSH public key found. Create one with:"
    echo "         ssh-keygen -t ed25519"
    exit 1
  fi

  echo "==> Installing $(basename "$PUBKEY") on ${#HOSTS[@]} host(s)."
  echo "    You'll be asked for each host's password once."
  echo ""
  key_failures=0
  for host in "${HOSTS[@]}"; do
    echo "--- $host"
    # Done inline rather than via ssh-copy-id, which isn't present on a stock
    # Windows OpenSSH. Appending is idempotent enough: a repeat run adds a
    # duplicate line, which sshd ignores.
    if ssh -o StrictHostKeyChecking=no "$host" \
        "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys" \
        < "$PUBKEY"; then
      echo "    key installed."
    else
      echo "    FAILED."
      key_failures=$((key_failures + 1))
    fi
  done
  echo ""
  if [[ $key_failures -gt 0 ]]; then
    echo "$key_failures host(s) failed. Fix those, then re-run --setup-keys."
    exit 1
  fi
  echo "All hosts keyed. Deploys from here need no password:"
  echo "  ./deploy-all.sh --email <address>"
  exit 0
fi

if [[ -z "$EMAIL" ]]; then
  echo "ERROR: --email is required (Let's Encrypt notifications for every site)."
  exit 1
fi

if [[ "$DRY_RUN" == "1" ]]; then
  echo "Dry run — nothing deployed."
  exit 0
fi

# ---- Build once, deploy many --------------------------------------------
# deploy.sh publishes per invocation; across a fleet that's one full build per
# site for byte-identical output. Build here and hand every site --skip-build.
if [[ "$SKIP_BUILD" == "1" && -f "$TARBALL" ]]; then
  echo "==> Reusing existing tarball (--skip-build)."
else
  echo "==> Publishing one build for all ${#HOSTS[@]} site(s)..."
  if ! bash "$SCRIPT_DIR/publish.sh"; then
    echo "ERROR: Build failed — nothing deployed."
    exit 1
  fi
fi
echo ""

SUCCEEDED=(); FAILED=()

for i in "${!HOSTS[@]}"; do
  host="${HOSTS[$i]}"; domain="${DOMAINS[$i]}"; port="${PORTS[$i]}"

  echo "=============================================="
  echo "  [$((i + 1))/${#HOSTS[@]}] $host -> $domain"
  echo "=============================================="

  args=("$host" --domain "$domain" --email "$EMAIL" --port "$port" --skip-build)
  [[ -n "$SSH_KEY" ]] && args+=(--key "$SSH_KEY")

  if bash "$SCRIPT_DIR/deploy.sh" "${args[@]}"; then
    SUCCEEDED+=("$domain")
  else
    echo "!!! $host FAILED — continuing with the remaining sites."
    FAILED+=("$host")
  fi
  echo ""
done

# ---- Summary ------------------------------------------------------------
echo "=============================================="
echo "  Fleet deploy: ${#SUCCEEDED[@]} ok, ${#FAILED[@]} failed"
echo "=============================================="
for d in "${SUCCEEDED[@]}"; do echo "  ok      https://$d"; done
for h in "${FAILED[@]}";    do echo "  FAILED  $h"; done
echo "=============================================="

[[ ${#FAILED[@]} -eq 0 ]] || exit 1
