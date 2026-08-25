#!/usr/bin/env bash
# list-serial-ports.sh — show the serial ports this Linux box offers, with the
# stable /dev/serial/by-id names that survive a reboot or a replug.
#
# Commissioning a Pi means telling the Scale Reader / RFID service which port
# the indicator or card reader is on. /dev/ttyUSB0 is the obvious answer and
# the wrong one: the kernel hands those numbers out in enumeration order, so a
# second adapter or a replug can silently move the scale to ttyUSB1. The by-id
# link is built from the adapter's own vendor/model/serial, so it always points
# at the same physical adapter — use it in the port field.
#
# Run it on the machine the hardware is plugged into:
#
#   bash scripts/list-serial-ports.sh
#   ssh admin@<pi-ip> 'bash -s' < scripts/list-serial-ports.sh
#
# --plain prints one path per line (by-id where there is one) for scripting.
#
# The Windows counterpart is scripts/list-serial-ports.ps1.

set -euo pipefail

PLAIN=0

usage() {
  cat <<'USAGE'
Usage: list-serial-ports.sh [--plain]

  --plain   Print one port path per line and nothing else. Prefers the
            /dev/serial/by-id name when the port has one.
  -h        Show this help.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --plain) PLAIN=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

# dev path -> /dev/serial/by-id link pointing at it.
declare -A STABLE=()
if [[ -d /dev/serial/by-id ]]; then
  for link in /dev/serial/by-id/*; do
    [[ -e "$link" ]] || continue
    target=$(readlink -f "$link" 2>/dev/null) || continue
    STABLE["$target"]="$link"
  done
fi

# ttyUSB/ttyACM are USB adapters, ttyAMA is the Pi's own UART, ttyS* is the
# legacy set — mostly phantom entries on a PC, so those are kept only when the
# kernel has an actual device bound behind them.
devices=()
for pattern in '/dev/ttyUSB*' '/dev/ttyACM*' '/dev/ttyAMA*' '/dev/ttyS*'; do
  for dev in $pattern; do
    [[ -e "$dev" ]] || continue
    if [[ "$dev" == /dev/ttyS* && ! -e "/sys/class/tty/$(basename "$dev")/device" ]]; then
      continue
    fi
    devices+=("$dev")
  done
done

if [[ ${#devices[@]} -eq 0 ]]; then
  if [[ $PLAIN -eq 1 ]]; then exit 1; fi
  echo "No serial ports found."
  echo
  echo "  - USB adapter plugged in? Re-run after plugging it in and check 'dmesg | tail'."
  echo "  - Using the Pi's own GPIO UART? It needs enable_uart=1 in /boot/firmware/config.txt"
  echo "    and the serial console released (sudo raspi-config → Interface Options → Serial)."
  exit 1
fi

# Vendor/model/serial for a device, straight from udev. Absent on a minimal
# container image, which is not an error — the port still works.
describe() {
  local dev="$1" vendor='' model='' serial=''
  command -v udevadm >/dev/null 2>&1 || return 0
  while IFS='=' read -r key value; do
    case "$key" in
      ID_VENDOR_FROM_DATABASE) [[ -z "$vendor" ]] && vendor="$value" ;;
      ID_VENDOR) [[ -z "$vendor" ]] && vendor="${value//_/ }" ;;
      ID_MODEL_FROM_DATABASE) model="$value" ;;
      ID_MODEL) [[ -z "$model" ]] && model="${value//_/ }" ;;
      ID_SERIAL_SHORT) serial="$value" ;;
    esac
  done < <(udevadm info -q property -n "$dev" 2>/dev/null || true)

  local text="$vendor $model"
  text="$(echo "$text" | sed 's/^ *//; s/ *$//')"
  [[ -n "$serial" ]] && text="$text (serial $serial)"
  echo "$text" | sed 's/^ *//'
}

if [[ $PLAIN -eq 1 ]]; then
  for dev in "${devices[@]}"; do
    echo "${STABLE[$dev]:-$dev}"
  done
  exit 0
fi

echo "== Serial ports on $(hostname) =="
echo

for dev in "${devices[@]}"; do
  echo "$dev"

  if [[ -n "${STABLE[$dev]:-}" ]]; then
    echo "  use this  : ${STABLE[$dev]}"
  elif [[ "$dev" == /dev/ttyUSB* || "$dev" == /dev/ttyACM* ]]; then
    echo "  use this  : $dev   (no by-id link — number can move on replug)"
  fi

  desc="$(describe "$dev")"
  [[ -n "$desc" ]] && echo "  adapter   : $desc"

  if [[ ! -r "$dev" || ! -w "$dev" ]]; then
    group="$(stat -c '%G' "$dev" 2>/dev/null || echo dialout)"
    echo "  access    : NO — ${USER:-$(id -un)} cannot open it. sudo usermod -aG $group ${USER:-$(id -un)}, then log out and back in."
  fi

  echo
done

cat <<'NOTE'
Paste the "use this" path into the port field on the service's setup screen
(Setup → Options → Scales, or → Readers for a card reader).
NOTE
