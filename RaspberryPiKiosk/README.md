# Raspberry Pi Kiosk

Pi-side scripts that launch Chromium in kiosk mode pointed at the Foundation web app's `/Kiosk` page, with a watchdog that restarts the browser when the server has been unreachable for 30 seconds. One Pi per kiosk display — one TV/monitor, one Chromium, one URL.

Unlike the device-side .NET services, this has no dist repo of its own — the scripts live in the monorepo. A Pi gets them either from the `kiosk-pi.tar.gz` release asset (no git required) or by cloning the monorepo through a one-shot bootstrap pasted into [Raspberry Pi Connect](https://connect.raspberrypi.com/)'s web shell. Operate the Pi through that same shell once it's running.

## How it works

```
Pi boots → desktop autostart → kiosk-loop.sh
                                    │
                                    ├─ probes <server>/Kiosk every 5 s
                                    │
                                    ├─ launches Chromium in --kiosk mode
                                    │
                                    └─ if /Kiosk unreachable for 30 s:
                                         kill Chromium, relaunch when back
```

- The loop tracks Chromium's PID; if it crashes on its own, it's relaunched immediately.
- The loop also watches for `~/.config/foundation-kiosk/STOP` so an operator can pause it over SSH (see *Operating* below).
- All log lines go to `~/.config/foundation-kiosk/kiosk.log`.

## Deploy

### From a release (no git needed)

Simplest path, and the one to use on a Pi OS Lite image with no git installed:

```bash
curl -fsSL -o kiosk.tar.gz [https://github.com/GTMichelli-Dev/foundation/releases/latest/download/kiosk-pi.tar.gz](https://github.com/GTMichelli-Dev/foundation/releases/latest/download/kiosk-pi.tar.gz)
mkdir -p ~/foundation-kiosk && tar -xzf kiosk.tar.gz -C ~/foundation-kiosk
~/foundation-kiosk/install.sh
sudo reboot
```

Unpack it somewhere permanent. The autostart entry points at wherever the scripts land, so extracting to `/tmp` gives you a kiosk that stops working at the next reboot.

To update, re-download over the same folder and re-run `install.sh`.

### From a checkout

Useful when you want to `git pull` updates rather than re-download. The Pi clones `foundation` directly via a one-shot bootstrap pasted into Pi Connect's web shell, using partial + sparse checkout so only the `RaspberryPiKiosk/` folder ends up on disk (a few MiB total instead of the full repo). `foundation` is a public repo, so no GitHub credentials are needed for the clone.

### Bootstrap

Open the Pi at `https://connect.raspberrypi.com/devices` → **Shell**, then paste this whole block:

```bash
cat > /tmp/foundation-bootstrap.sh <<'BOOTSTRAP_EOF'
#!/usr/bin/env bash
set -e

# Install git/curl if missing (Pi OS Lite often doesn't have git)
missing=()
command -v git  >/dev/null 2>&1 || missing+=(git)
command -v curl >/dev/null 2>&1 || missing+=(curl)
if (( ${#missing[@]} > 0 )); then
  echo "Installing: ${missing[*]}"
  sudo apt-get update -y
  sudo apt-get install -y "${missing[@]}"
fi

# Clone with partial + sparse checkout — fetches commits & trees for the whole repo
# (small), but only the file blobs for RaspberryPiKiosk/. Public repo, no auth.
cd ~
if [[ -d foundation/.git ]]; then
  echo "foundation already cloned — pulling latest."
  cd foundation
  git sparse-checkout set RaspberryPiKiosk
  git pull --ff-only
else
  git clone --filter=blob:none --sparse https://github.com/GTMichelli-Dev/foundation.git
  cd foundation
  git sparse-checkout set RaspberryPiKiosk
fi

echo
echo "Done. Next:"
echo "  cd ~/foundation/RaspberryPiKiosk && ./install.sh && sudo reboot"
BOOTSTRAP_EOF

bash /tmp/foundation-bootstrap.sh </dev/tty
rm -f /tmp/foundation-bootstrap.sh
```

### Run the installer

```bash
cd ~/foundation-kiosk/RaspberryPiKiosk
chmod + install.sh &&
./install.sh
sudo reboot
```

`install.sh` prompts for:

1. **Server URL** — required. e.g. `http://truckscale.local`. Verified before saving.
2. **Language** — optional. Pins this kiosk to English or Spanish regardless of the site default.

The **kiosk PIN is not asked for here**. When the server has *User Login* enabled, the kiosk shows its own numpad the first time it loads; the PIN is tapped in once and remembered in the Chromium profile from then on. That keeps the credential off the Pi's disk, and means a replaced Pi asks for it again — along with re-running kiosk setup, since both live in the same profile.

An existing `KIOSK_PIN` in the config is carried forward untouched, so re-running the installer never makes a working kiosk start asking. Delete that line from the config to move the kiosk onto the on-screen prompt.

Hardware is **not** asked for here. The first time the kiosk loads it does not recognise itself, so it runs a short setup on its own screen:

```
  Which scale?        → the site scales, from the web app
  Which printer?      → print on this screen, a connected printer, or none
  Which card reader?  → a connected reader, or none
  Ready to finish     → review, then Finish
```

The printer and reader lists are whatever the Print and RFID Reader Services are announcing at that moment, so the kiosk can only be pointed at hardware the site is actually running — and both may be skipped outright. The kiosk saves the answers against a device id of its own and comes back fully configured, with nothing but the server address in its URL:

```
http://truckscale.local/Kiosk
```

To change any of it later, press and hold the logo at the top of the kiosk screen for three seconds to run setup again, or edit the kiosk from the web app under **Setup → Kiosks**.

Re-running `install.sh` re-prompts the URL, PIN and language with the previous answers as defaults; it leaves the kiosk's own hardware choices alone.

After the prompts, the script installs Chromium + curl + unclutter and writes a desktop autostart entry that launches the watchdog at every login.

The Pi must auto-login to the desktop (standard Raspberry Pi OS kiosk setup — `sudo raspi-config` → *System Options* → *Boot / Auto Login* → *Desktop Autologin*). Without that, the autostart entry never fires.

### Updates

```bash
cd ~/foundation
git pull
~/foundation/RaspberryPiKiosk/kiosk-stop
~/foundation/RaspberryPiKiosk/kiosk-start
```

Re-run `./install.sh` only if you need to change the server URL or `install.sh` itself was updated.

### If `foundation` ever becomes a private repo

Add a fine-grained GitHub PAT (Contents=Read, scoped to `GTMichelli-Dev/foundation`, SSO-authorized if the org enforces it) to `~/.git-credentials` once:

```bash
read -rsp "GitHub PAT: " PAT; echo
git config --global credential.helper store
umask 077
printf 'https://x-access-token:%s@github.com\n' "$PAT" > ~/.git-credentials
unset PAT
```

After that, `git clone` / `git pull` of the now-private repo will be auth-free.

## Operating

All of these run on the Pi — usually over SSH.

| Action | Command |
|---|---|
| Pause the kiosk (kills Chromium, leaves loop alive) | `~/foundation/RaspberryPiKiosk/kiosk-stop` |
| Resume after pause | `~/foundation/RaspberryPiKiosk/kiosk-start` |
| Tail the log | `tail -f ~/.config/foundation-kiosk/kiosk.log` |
| Re-prompt for the server URL | re-run `./install.sh` |
| Remove autostart entirely | `./uninstall.sh` |

`kiosk-stop` writes a flag at `~/.config/foundation-kiosk/STOP` that the watchdog notices on its next probe, then kills Chromium so the screen frees up. The loop stays running and resumes the moment `kiosk-start` clears the flag.

## Configuration

After install, the config file is:

```
nano ~/.config/foundation-kiosk/config
```

```bash
SERVER_URL="http://truckscale.local"
KIOSK_PIN="12345"                # blank when UseLogin is off
SERVICE_ID="office-1"            # blank or 'Browser' for browser-print
PRINTER_ID="BIXOLON_BK3"         # blank or 'Browser' for browser-print
KIOSK_URL="http://truckscale.local/Kiosk?service-id=office-1&printer-id=BIXOLON_BK3&pin=12345"
CHROMIUM_BIN="chromium-browser"
HEALTH_INTERVAL=5            # seconds between probes
UNREACHABLE_THRESHOLD=30     # seconds of unreachable before restarting Chromium
```

Only `KIOSK_URL` is what the watchdog actually reads; the individual params are saved so re-running `install.sh` can default each prompt. The config file is `chmod 600` because `KIOSK_PIN` is a credential.

Edit the file and run `kiosk-stop && kiosk-start` to apply changes without rebooting. To change just one parameter (e.g. add a printer-id), re-run `./install.sh` instead — it will re-assemble `KIOSK_URL` correctly with proper URL-encoding of any special characters.

## Files

| Path | Purpose |
|---|---|
| `install.sh` | One-time setup. Prompts for URL, verifies `/Kiosk`, installs deps, registers autostart |
| `kiosk-loop.sh` | The watchdog. Autostarted at desktop login. Launches Chromium + restarts on outage |
| `kiosk-stop` | Pauses the loop (writes STOP flag, kills Chromium) |
| `kiosk-start` | Resumes after a pause (clears STOP flag) |
| `uninstall.sh` | Removes the autostart entry |
| `~/.config/foundation-kiosk/config` | Generated config (URL, intervals) |
| `~/.config/foundation-kiosk/kiosk.log` | Watchdog log |
| `~/.cache/foundation-kiosk-profile/` | Chromium profile (cookies, PWA install) — persisted across reboots |

## Troubleshooting

- **Chromium doesn't appear after reboot** — confirm the Pi is auto-logging in to the desktop (`raspi-config` → Boot/Auto Login). The autostart entry only fires once a desktop session is up. Then check `~/.config/foundation-kiosk/kiosk.log` for errors.
- **"Server unreachable" forever** — from the Pi: `curl -v "$KIOSK_URL"`. Most often a DNS issue with `*.local` (mDNS) — try the server's IP in the config instead.
- **Screen blanks after a while** — `install.sh` runs `xset s off -dpms` best-effort, but some images need it baked into the desktop session. The simplest fix is to also disable blanking via `raspi-config` → *Display Options* → *Screen Blanking* → Disable.
- **Chromium shows "didn't shut down cleanly" prompt** — `kiosk-loop.sh` rewrites the profile's `Preferences` to suppress it before each launch; if you still see it, delete `~/.cache/foundation-kiosk-profile/Default/Preferences` and let it regenerate.
- **"Unlock keyring" dialog covers the kiosk** — shouldn't happen after install: `kiosk-loop.sh` launches Chromium with `--password-store=basic` (so it never asks libsecret), and `install.sh` drops `Hidden=true` overrides for `gnome-keyring-{pkcs11,secrets,ssh}.desktop` in `~/.config/autostart/` to keep the daemon from starting at all. If you still see a prompt, confirm those three override files exist and have `Hidden=true`.
