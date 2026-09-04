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
say "Optional kiosk URL parameters (press Enter to skip any of them):"

# PIN — no longer asked for. A server with User Login on sends the kiosk to its
# own numpad screen, where the PIN is tapped in once and remembered in the
# browser profile for good. Keeping it out of here keeps a credential off the
# Pi's disk and out of the process list.
#
# An existing PIN is carried forward untouched, though: a kiosk that is already
# running should not start asking for a PIN just because its installer was
# re-run. Clear KIOSK_PIN from the config to move it onto the on-screen prompt.
if [[ -n "$default_pin" ]]; then
    KIOSK_PIN="$default_pin"
    echo "  PIN   — keeping the PIN already in this kiosk's config. Newer servers"
    echo "          ask for it on the kiosk screen instead; clear KIOSK_PIN from"
    echo "          $CONFIG_FILE to switch this kiosk over."
else
    KIOSK_PIN=""
    echo "  PIN   — not needed here. If the server has User Login on, the kiosk"
    echo "          asks for the PIN on its own screen the first time it loads,"
    echo "          and remembers it from then on."
fi

# Scale, printer and card reader are no longer asked for here: the kiosk asks
# for them on its own screen the first time it loads, where the installer can
# see the real hardware the site is running and skip the printer or the reader.
#
# The two prompts below therefore only appear for a kiosk that was installed
# before self-setup existed and still has them in its config — re-running the
# installer must not silently strip a working mapping. Clear them to move the
# kiosk onto on-screen setup.
if [[ -n "$default_service_id" || -n "$default_printer_id" ]]; then
    echo "  SVC   — this kiosk still has printer parameters on its URL, from an"
    echo "          install that predates on-screen setup. Clear both to let the"
    echo "          kiosk pick its own printer on screen instead."
    read -r -p "  Service ID [$default_service_id] (blank to clear): " SERVICE_ID
    read -r -p "  Printer ID [$default_printer_id] (blank to clear): " PRINTER_ID
    # An explicit empty answer clears; the defaults are not re-applied, or
    # there would be no way to drop them.
else
    SERVICE_ID=""
    PRINTER_ID=""
    echo "  HW    — scale, printer and card reader are chosen on the kiosk screen"
    echo "          the first time it loads. Nothing to enter here."
fi

# Language — pins this kiosk to one language regardless of the site default set
# in Setup. Blank follows the site default. The driver can still switch with the
# EN/ES button on the kiosk screen; that choice is a cookie on this Pi and wins
# until it is cleared, at which point the URL parameter applies again.
echo "  LANG  — 'es' runs this kiosk in Spanish, 'en' in English. Leave blank to"
echo "          follow the site default from the web app's Setup page. Requires"
echo "          'Enable Spanish' to be ticked in Setup — the server ignores this"
echo "          parameter while that switch is off."
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

# ---------- retire the old ~/foundation-kiosk folder ----------
# Releases before this one told the operator to unpack the tarball into
# ~/foundation-kiosk, while a monorepo checkout landed in ~/foundation. A Pi set
# up both ways — or updated from one to the other — ended up with two
# near-identically named folders and no way to tell which one the autostart
# entry actually points at. Everything now lives under ~/foundation, so the old
# folder is dead weight; offer to clear it rather than leaving the ambiguity in
# place. Only its own scripts are removed: anything else in there is the
# operator's and is left alone (rmdir, not rm -rf).
LEGACY_DIR="$HOME/foundation-kiosk"
if [[ -d "$LEGACY_DIR" && "$(cd "$LEGACY_DIR" && pwd -P)" != "$(cd "$SCRIPT_DIR" && pwd -P)" ]]; then
    say "Found the old kiosk folder at $LEGACY_DIR."
    echo "  This kiosk now runs from $SCRIPT_DIR, and the autostart entry written"
    echo "  above points there. The old folder is no longer used by anything."
    read -r -p "  Remove the old kiosk scripts from $LEGACY_DIR? [Y/n] " ans
    if [[ "${ans,,}" != "n" ]]; then
        rm -f "$LEGACY_DIR"/{install.sh,uninstall.sh,kiosk-loop.sh,kiosk-start,kiosk-stop,README.md}
        if rmdir "$LEGACY_DIR" 2>/dev/null; then
            say "Removed $LEGACY_DIR."
        else
            warn "Left $LEGACY_DIR in place — it still holds files this installer did not put there."
        fi
    else
        warn "Left $LEGACY_DIR alone. Nothing runs from it; delete it whenever you like."
    fi
fi

# ---------- done ----------
cat <<EOF

────────────────────────────────────────────────────────────
  Setup complete.

  Server URL : $SERVER_URL
  Kiosk URL  : $KIOSK_URL
  Service ID : ${SERVICE_ID:-<set on the kiosk screen>}
  Printer ID : ${PRINTER_ID:-<set on the kiosk screen>}
  Language   : ${KIOSK_LANG:-<site default>}
  Kiosk PIN  : $( [[ -n "$KIOSK_PIN" ]] && echo '<carried over from the old config>' || echo '<asked on the kiosk screen>' )
  Loop script: $SCRIPT_DIR/kiosk-loop.sh
  Config     : $CONFIG_FILE

  Reboot the Pi to start the kiosk:
      sudo reboot

  The first time it loads, the kiosk asks on its own screen which scale it
  weighs on, which printer to use (or none), and which card reader to listen
  to (or none). It remembers the answers; to change them later, press and
  hold the logo at the top of the kiosk screen for three seconds, or edit the
  kiosk on the web app's Setup -> Kiosks page.

  To stop the kiosk (e.g. for maintenance) — SSH into the Pi and run:
      $SCRIPT_DIR/kiosk-stop

  To resume after stopping:
      $SCRIPT_DIR/kiosk-start

  To remove autostart entirely:
      $SCRIPT_DIR/uninstall.sh
────────────────────────────────────────────────────────────
EOF
