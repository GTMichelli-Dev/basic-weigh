#!/usr/bin/env bash
# kiosk-loop.sh — the watchdog. Loaded by the desktop autostart entry.
#
# Behavior:
#   1. Reads ~/.config/foundation-kiosk/config for SERVER_URL / KIOSK_URL.
#   2. Waits for the server to be reachable.
#   3. Launches Chromium in kiosk mode at $KIOSK_URL.
#   4. Probes the server every HEALTH_INTERVAL seconds. If it stays
#      unreachable for UNREACHABLE_THRESHOLD seconds, kills Chromium
#      and starts the whole cycle over.
#   5. Honors a stop flag at ~/.config/foundation-kiosk/STOP so an operator
#      can pause the loop over SSH (see kiosk-stop / kiosk-start).
#
# Logs are written to ~/.config/foundation-kiosk/kiosk.log .

set -u

CONFIG_DIR="$HOME/.config/foundation-kiosk"
CONFIG_FILE="$CONFIG_DIR/config"
STOP_FLAG="$CONFIG_DIR/STOP"
PID_FILE="$CONFIG_DIR/kiosk.pid"
LOG_FILE="$CONFIG_DIR/kiosk.log"
PROFILE_DIR="$HOME/.cache/foundation-kiosk-profile"

mkdir -p "$CONFIG_DIR" "$PROFILE_DIR"
exec >>"$LOG_FILE" 2>&1

log() { printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }

[[ -f "$CONFIG_FILE" ]] || { log "FATAL: $CONFIG_FILE missing — run install.sh first."; exit 1; }
# shellcheck disable=SC1090
source "$CONFIG_FILE"

: "${SERVER_URL:?SERVER_URL not set in $CONFIG_FILE}"
: "${KIOSK_URL:?KIOSK_URL not set in $CONFIG_FILE}"
: "${CHROMIUM_BIN:=chromium-browser}"
: "${HEALTH_INTERVAL:=5}"
: "${UNREACHABLE_THRESHOLD:=30}"

# Record our own PID so kiosk-stop can find us
echo $$ > "$PID_FILE"

# Make sure we don't leave Chromium running if this script dies
CHROMIUM_PID=""
cleanup() {
    if [[ -n "$CHROMIUM_PID" ]] && kill -0 "$CHROMIUM_PID" 2>/dev/null; then
        log "cleanup: killing chromium pid $CHROMIUM_PID"
        kill "$CHROMIUM_PID" 2>/dev/null || true
        sleep 1
        kill -9 "$CHROMIUM_PID" 2>/dev/null || true
    fi
    rm -f "$PID_FILE"
}
trap cleanup EXIT INT TERM

probe() {
    # Returns 0 if KIOSK_URL responds with a non-5xx status within 5s.
    local code
    code="$(curl --silent --max-time 15 --output /dev/null --write-out '%{http_code}' "$KIOSK_URL" 2>/dev/null || echo 000)"
    [[ "$code" =~ ^[123] ]]
}

launch_chromium() {
    log "launching chromium → $KIOSK_URL"

    # Best-effort: disable screen blanking
    if command -v xset >/dev/null 2>&1; then
        DISPLAY="${DISPLAY:-:0}" xset s off s noblank -dpms 2>/dev/null || true
    fi
    # Hide the cursor after 1s of inactivity
    if command -v unclutter >/dev/null 2>&1 && ! pgrep -x unclutter >/dev/null; then
        unclutter -idle 1 -root >/dev/null 2>&1 &
    fi

    # Clear Chromium's "didn't shut down cleanly" prompt
    if [[ -f "$PROFILE_DIR/Default/Preferences" ]]; then
        sed -i 's/"exit_type":"Crashed"/"exit_type":"Normal"/; s/"exited_cleanly":false/"exited_cleanly":true/' \
            "$PROFILE_DIR/Default/Preferences" 2>/dev/null || true
    fi

    "$CHROMIUM_BIN" \
        --kiosk \
        --noerrdialogs \
        --disable-infobars \
        --disable-session-crashed-bubble \
        --disable-features=TranslateUI,InfiniteSessionRestore \
        --no-first-run \
        --start-fullscreen \
        --autoplay-policy=no-user-gesture-required \
        --check-for-update-interval=31536000 \
        --password-store=basic \
        --user-data-dir="$PROFILE_DIR" \
        "$KIOSK_URL" >/dev/null 2>&1 &
    CHROMIUM_PID=$!
    log "chromium pid: $CHROMIUM_PID"
}

kill_chromium() {
    if [[ -n "$CHROMIUM_PID" ]] && kill -0 "$CHROMIUM_PID" 2>/dev/null; then
        log "killing chromium pid $CHROMIUM_PID"
        kill "$CHROMIUM_PID" 2>/dev/null || true
        # Give it a moment, then force
        for _ in 1 2 3 4 5; do
            kill -0 "$CHROMIUM_PID" 2>/dev/null || break
            sleep 1
        done
        kill -9 "$CHROMIUM_PID" 2>/dev/null || true
    fi
    # Any stray chromium processes from the same profile
    pkill -f "user-data-dir=$PROFILE_DIR" 2>/dev/null || true
    CHROMIUM_PID=""
}

check_stop_flag() {
    if [[ -f "$STOP_FLAG" ]]; then
        log "STOP flag present — pausing. Remove $STOP_FLAG (or run kiosk-start) to resume."
        kill_chromium
        while [[ -f "$STOP_FLAG" ]]; do
            sleep 2
        done
        log "STOP flag cleared — resuming."
    fi
}

log "=========================================="
log "kiosk-loop starting (pid $$) → $KIOSK_URL"
log "=========================================="

# ---------- outer loop: keep the kiosk running forever ----------
while :; do
    check_stop_flag

    # Wait for the server to be reachable before launching the browser.
    # No backoff cap — just keep trying so a flapping network heals on its own.
    if ! probe; then
        log "server unreachable on startup — waiting…"
        while ! probe; do
            check_stop_flag
            sleep "$HEALTH_INTERVAL"
        done
        log "server reachable — proceeding."
    fi

    launch_chromium

    # ---------- inner watchdog: monitor while chromium runs ----------
    unreachable_since=0
    while :; do
        sleep "$HEALTH_INTERVAL"
        check_stop_flag

        # If chromium died on its own (e.g. crash), restart it.
        if ! kill -0 "$CHROMIUM_PID" 2>/dev/null; then
            log "chromium exited unexpectedly — relaunching"
            break
        fi

        if probe; then
            if (( unreachable_since != 0 )); then
                log "server reachable again"
                unreachable_since=0
            fi
        else
            now="$(date +%s)"
            if (( unreachable_since == 0 )); then
                unreachable_since=$now
                log "server unreachable — starting $UNREACHABLE_THRESHOLD-second grace timer"
            elif (( now - unreachable_since >= UNREACHABLE_THRESHOLD )); then
                log "server unreachable for ${UNREACHABLE_THRESHOLD}s — restarting chromium"
                break
            fi
        fi
    done

    kill_chromium
    sleep 2
done
