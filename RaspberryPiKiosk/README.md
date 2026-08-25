# Raspberry Pi Kiosk

Pi-side scripts that launch Chromium in kiosk mode pointed at the Foundation web app's `/Kiosk` page, with a watchdog that restarts the browser when the server has been unreachable for 30 seconds. One Pi per kiosk display — one TV/monitor, one Chromium, one URL.

Unlike the device-side .NET services, this is **not** distributed via a standalone dist repo. The scripts live in the monorepo and the Pi clones it directly via a one-shot bootstrap pasted into [Raspberry Pi Connect](https://connect.raspberrypi.com/)'s web shell. Operate the Pi through that same shell once it's running.

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

The Pi clones `foundation` directly via a one-shot bootstrap pasted into Pi Connect's web shell, using partial + sparse checkout so only the `RaspberryPiKiosk/` folder ends up on disk (a few MiB total instead of the full repo). `foundation` is a public repo, so no GitHub credentials are needed for the clone.

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
cd ~/foundation/RaspberryPiKiosk
chmod + install.sh &&
./install.sh
sudo reboot
```

`install.sh` prompts for:

1. **Server URL** — required. e.g. `http://truckscale.local`. Verified before saving.
2. **Kiosk PIN** — optional. Required only when the Foundation server has *User Login* enabled. Becomes `?pin=<value>` on the kiosk URL; the server stores it as a 24-hour cookie so it's only read once per cookie lifetime.
3. **Service ID** — optional. Selects which Print/Camera Service instance handles this kiosk's tickets and camera captures. Enter `Browser` (or leave blank) to print via the browser instead of hardware. Otherwise enter the service ID shown on the print agent's Setup page (e.g. `office-1`).
4. **Printer ID** — optional. Picks which physical printer the service uses (e.g. `Zebra_LP2844`, `BIXOLON_BK3`). Set to `Browser` when Service ID is `Browser`.

The installer assembles these into a single URL like:

```
http://truckscale.local/Kiosk?service-id=office-1&printer-id=BIXOLON_BK3&pin=12345
```

…and writes it to the config as `KIOSK_URL`. The watchdog launches Chromium against that full URL on every restart. Re-running `install.sh` re-prompts each value with the previous answer as the default, so changing just one parameter is a couple of Enter-presses.

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
