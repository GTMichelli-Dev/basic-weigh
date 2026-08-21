#!/usr/bin/env bash
# install.sh — one-time setup for a Raspberry Pi kiosk pointed at Foundation.
#
# Run on the Pi (the same Pi that will display the kiosk):
#   chmod +x install.sh && ./install.sh
#
# Prompts for the Foundation server URL (e.g. http://truckscale.local),
# verifies that <url>/Kiosk is reachable, then installs an autostart entry
# that launches Chromium in kiosk mode on every desktop login. A watchdog
# restarts Chromium if the page is unreachable for 30 seconds.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_DIR="$HOME/.config/foundation-kiosk"
CONFIG_FILE="$CONFIG_DIR/config"
AUTOSTART_DIR="$HOME/.config/autostart"
AUTOSTART_FILE="$AUTOSTART_DIR/foundation-kiosk.desktop"

say()  { printf '\n\033[1;36m%s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m%s\033[0m\n' "$*" >&2; }
die()  { printf '\033[1;31m%s\033[0m\n' "$*" >&2; exit 1; }

# ---------- sanity ----------
[[ "$(uname -s)" == "Linux" ]] || die "This installer must be run on the Raspberry Pi (Linux), not Windows/macOS."

# ---------- dependencies ----------
say "Checking dependencies…"
need_install=()
command -v curl >/dev/null 2>&1 || need_install+=(curl)
if   command -v chromium-browser >/dev/null 2>&1; then CHROMIUM_BIN="chromium-browser"
elif command -v chromium         >/dev/null 2>&1; then CHROMIUM_BIN="chromium"
else
    need_install+=(chromium-browser)
    CHROMIUM_BIN="chromium-browser"
fi
# unclutter hides the mouse cursor after a few seconds of inactivity — nice for a kiosk
command -v unclutter >/dev/null 2>&1 || need_install+=(unclutter)

if (( ${#need_install[@]} > 0 )); then
    say "Installing: ${need_install[*]}  (sudo apt)"
    sudo apt-get update -y
    sudo apt-get install -y "${need_install[@]}" || die "apt-get install failed."
fi

# Re-resolve chromium in case it was just installed
if   command -v chromium-browser >/dev/null 2>&1; then CHROMIUM_BIN="chromium-browser"
elif command -v chromium         >/dev/null 2>&1; then CHROMIUM_BIN="chromium"
else die "Chromium did not install. Install it manually with: sudo apt-get install chromium-browser"
fi
say "Chromium found at: $(command -v "$CHROMIUM_BIN")"

# ---------- load previous values from config (used as defaults below) ----------
default_url=""
default_pin=""
default_service_id=""
default_printer_id=""
default_lang=""
if [[ -f "$CONFIG_FILE" ]]; then
    # shellcheck disable=SC1090
    source "$CONFIG_FILE"
    default_url="${SERVER_URL:-}"
    default_pin="${KIOSK_PIN:-}"
    default_service_id="${SERVICE_ID:-}"
    default_printer_id="${PRINTER_ID:-}"
    default_lang="${KIOSK_LANG:-}"
fi

# ---------- prompt for server URL ----------
while :; do
    if [[ -n "$default_url" ]]; then
        read -r -p "Foundation server URL [$default_url]: " SERVER_URL
        SERVER_URL="${SERVER_URL:-$default_url}"
    else
        read -r -p "Foundation server URL (e.g. http://truckscale.local): " SERVER_URL
    fi

    # Strip trailing slash
    SERVER_URL="${SERVER_URL%/}"

    if [[ -z "$SERVER_URL" ]]; then
        warn "URL is required."
        continue
    fi
    if [[ ! "$SERVER_URL" =~ ^https?:// ]]; then
        warn "URL must start with http:// or https://"
        continue
    fi

    say "Verifying connectivity to $SERVER_URL/Kiosk …"
    # Accept any non-error response (200 OK, or 302 to login if UseLogin is on)
    http_code="$(curl --silent --show-error --max-time 10 --output /dev/null --write-out '%{http_code}' "$SERVER_URL/Kiosk" || echo 000)"
    if [[ "$http_code" =~ ^[123] ]]; then
        say "OK — server responded (HTTP $http_code)."
        break
    fi

    warn "Could not reach $SERVER_URL/Kiosk (HTTP $http_code)."
    read -r -p "Try a different URL? [Y/n] " ans
    [[ "${ans,,}" == "n" ]] && { warn "Saving anyway — the watchdog will keep retrying."; break; }
    default_url="$SERVER_URL"
done

# ---------- prompt for kiosk URL parameters ----------
# These all become query-string parameters on the Kiosk URL. They're all
# optional — pressing Enter at any prompt leaves the parameter off the URL.
say "Optional kiosk URL parameters (press Enter to skip any of them):"

# PIN — required only when the server has UseLogin enabled. Becomes ?pin=
echo "  PIN   — only required if the Foundation server has User Login enabled."
echo "          The server stores this as a 24-hour cookie after the first hit,"
echo "          so the URL doesn't need it again until the cookie expires."
if [[ -n "$default_pin" ]]; then
    read -r -p "  Kiosk PIN [$default_pin]: " KIOSK_PIN
    KIOSK_PIN="${KIOSK_PIN:-$default_pin}"
else
    read -r -p "  Kiosk PIN (blank to skip): " KIOSK_PIN
fi

# Service ID — identifies the print/camera service instance this kiosk uses.
echo "  SVC   — service-id picks the Print/Camera Service instance this kiosk uses."
echo "          Use 'Browser' (or leave blank) to print via the browser instead of"
echo "          a hardware printer. Otherwise enter the service ID of the Pi/PC"
echo "          running the print agent (visible in the web app's Setup page)."
if [[ -n "$default_service_id" ]]; then
    read -r -p "  Service ID [$default_service_id]: " SERVICE_ID
    SERVICE_ID="${SERVICE_ID:-$default_service_id}"
else
    read -r -p "  Service ID (blank to skip, or 'Browser' for browser-print): " SERVICE_ID
fi

# Printer ID — which physical printer to use (or 'Browser' for browser-print).
echo "  PRT   — printer-id picks which printer the service uses. Set to 'Browser'"
echo "          when Service ID is 'Browser'. Otherwise use the printer name shown"
echo "          on the print agent's web UI (e.g. Zebra_LP2844, BIXOLON_BK3)."
if [[ -n "$default_printer_id" ]]; then
    read -r -p "  Printer ID [$default_printer_id]: " PRINTER_ID
    PRINTER_ID="${PRINTER_ID:-$default_printer_id}"
else
    read -r -p "  Printer ID (blank to skip): " PRINTER_ID
fi

# Language — pins this kiosk to one language regardless of the site default set
# in Setup. Blank follows the site default. The driver can still switch with the
# EN/ES button on the kiosk screen; that choice is a cookie on this Pi and wins
# until it is cleared, at which point the URL parameter applies again.
echo "  LANG  — 'es' runs this kiosk in Spanish, 'en' in English. Leave blank to"
echo "          follow the site default from the web app's Setup page."
if [[ -n "$default_lang" ]]; then
    read -r -p "  Language (en/es) [$default_lang]: " KIOSK_LANG
    KIOSK_LANG="${KIOSK_LANG:-$default_lang}"
else
    read -r -p "  Language (en/es, blank to skip): " KIOSK_LANG
fi
# Normalise, and drop anything that isn't a language we ship rather than
# sending the server a parameter it will ignore.
KIOSK_LANG="$(echo "${KIOSK_LANG}" | tr '[:upper:]' '[:lower:]')"
case "$KIOSK_LANG" in
    en|es) ;;
    "")    ;;
    *)     say "Unknown language '$KIOSK_LANG' — following the site default instead."
           KIOSK_LANG="" ;;
esac

# ---------- assemble KIOSK_URL with query string ----------
# Minimal RFC 3986 percent-encoder for arbitrary parameter values so PINs and
# IDs containing spaces or punctuation survive the shell-to-Chromium handoff.
urlencode() {
    local s="$1" out="" i c
    for (( i=0; i<${#s}; i++ )); do
        c="${s:$i:1}"
        case "$c" in
            [a-zA-Z0-9._~-]) out+="$c" ;;
            *) printf -v hex '%%%02X' "'$c"; out+="$hex" ;;
        esac
    done
    printf '%s' "$out"
}

query=""
add_param() {
    local key="$1" val="$2"
    [[ -z "$val" ]] && return
    if [[ -z "$query" ]]; then query="?"; else query="${query}&"; fi
    query="${query}${key}=$(urlencode "$val")"
}
add_param "service-id" "$SERVICE_ID"
add_param "printer-id" "$PRINTER_ID"
add_param "pin"        "$KIOSK_PIN"
add_param "lang"       "$KIOSK_LANG"

KIOSK_URL="$SERVER_URL/Kiosk${query}"

say "Kiosk will load: $KIOSK_URL"

# ---------- save config ----------
# Persist the individual params (PIN / SERVICE_ID / PRINTER_ID) alongside the
# assembled KIOSK_URL so a subsequent install.sh run can default each prompt
# from what was used last time. The watchdog only reads KIOSK_URL.
mkdir -p "$CONFIG_DIR"
cat > "$CONFIG_FILE" <<EOF
# Foundation kiosk config — generated $(date -Iseconds)
SERVER_URL="$SERVER_URL"
KIOSK_PIN="$KIOSK_PIN"
SERVICE_ID="$SERVICE_ID"
PRINTER_ID="$PRINTER_ID"
KIOSK_LANG="$KIOSK_LANG"
KIOSK_URL="$KIOSK_URL"
CHROMIUM_BIN="$CHROMIUM_BIN"
# How often the watchdog probes the server (seconds)
HEALTH_INTERVAL=5
# After this many seconds of unreachable, restart Chromium
UNREACHABLE_THRESHOLD=30
EOF
# Tighten file perms — KIOSK_PIN is a credential.
chmod 600 "$CONFIG_FILE" 2>/dev/null || true
say "Wrote config: $CONFIG_FILE"

# ---------- make scripts executable ----------
chmod +x "$SCRIPT_DIR/kiosk-loop.sh" "$SCRIPT_DIR/kiosk-stop" "$SCRIPT_DIR/kiosk-start" "$SCRIPT_DIR/uninstall.sh"

# ---------- install autostart ----------
mkdir -p "$AUTOSTART_DIR"
cat > "$AUTOSTART_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=Foundation Kiosk
Comment=Launches Chromium in kiosk mode pointed at Foundation
Exec=$SCRIPT_DIR/kiosk-loop.sh
X-GNOME-Autostart-enabled=true
NoDisplay=false
Terminal=false
EOF
say "Wrote autostart: $AUTOSTART_FILE"

# ---------- disable screen blanking (best effort) ----------
say "Disabling screen blanking (best effort)…"
if command -v xset >/dev/null 2>&1; then
    # Will only succeed once a desktop session is up; the autostart launch picks it up too.
    DISPLAY="${DISPLAY:-:0}" xset s off s noblank -dpms 2>/dev/null || true
fi

# ---------- disable gnome-keyring ----------
# The kiosk has no human to type an unlock password, and Chromium is launched
# with --password-store=basic so it never asks libsecret for anything. The
# keyring daemon is therefore dead weight (and on autologin sessions it can
# pop an "unlock keyring" dialog on top of the kiosk). Suppress its three
# autostart entries by shadowing them with user-level copies that set
# Hidden=true — XDG honors the user-level file and skips the system one.
say "Disabling gnome-keyring autostart…"
for keyring_entry in gnome-keyring-pkcs11 gnome-keyring-secrets gnome-keyring-ssh; do
    cat > "$AUTOSTART_DIR/${keyring_entry}.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=${keyring_entry} (disabled by Foundation kiosk)
Hidden=true
NoDisplay=true
X-GNOME-Autostart-enabled=false
EOF
done

# ---------- done ----------
cat <<EOF

────────────────────────────────────────────────────────────
  Setup complete.

  Server URL : $SERVER_URL
  Kiosk URL  : $KIOSK_URL
  Service ID : ${SERVICE_ID:-<none>}
  Printer ID : ${PRINTER_ID:-<none>}
  Language   : ${KIOSK_LANG:-<site default>}
  Kiosk PIN  : $( [[ -n "$KIOSK_PIN" ]] && echo '<set>' || echo '<none>' )
  Loop script: $SCRIPT_DIR/kiosk-loop.sh
  Config     : $CONFIG_FILE

  Reboot the Pi to start the kiosk:
      sudo reboot

  To stop the kiosk (e.g. for maintenance) — SSH into the Pi and run:
      $SCRIPT_DIR/kiosk-stop

  To resume after stopping:
      $SCRIPT_DIR/kiosk-start

  To remove autostart entirely:
      $SCRIPT_DIR/uninstall.sh
────────────────────────────────────────────────────────────
EOF
