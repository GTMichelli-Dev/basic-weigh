# Service Ports

Every Foundation service that listens owns its own port. This is the list they
are allocated from — **check it before giving a new service a default**, and
update it when one moves.

A scale house commonly runs several of these on one Pi or one PC, and the
services do not know about each other. Two sharing a default is not caught at
build time or at runtime by anything except the port itself: the second one to
start cannot bind, dies with an `AddressInUseException`, and systemd reports
`code=killed, signal=ABRT` in a restart loop. Nothing in that message says
"port", so it reads as a crashing binary. That is why this file exists.

## Allocated

| Port | Service | Bind | Notes |
|------|---------|------|-------|
| `80` | Foundation.Web | all | Cloud/Debian server install |
| `5110` | Foundation.Web | all | Raspberry Pi / LAN install |
| `5210` | Camera Capture Service | all | Swagger + config API |
| `5220` | Scale Reader Service | all | Swagger + config API |
| `5230` | Web Print Service (PiPrintService) | all | Swagger + config API |
| `5240` | Gate Controller Service | all | Swagger + config API |
| `5250` | RFID Reader Service | all | Swagger + config API |

Next free in the sequence: **5260**.

### Live view (Camera Capture Service, optional)

Installed only with `--with-live-view`, and only on the box running the Camera
Capture Service.

| Port | Process | Bind | Notes |
|------|---------|------|-------|
| `1984` | go2rtc HTTP API | **loopback only** | `127.0.0.1:1984`. Camera Capture Service is the only client; nothing on the site network can reach it |
| `8555` | go2rtc WebRTC | all (UDP + TCP) | Media path to the operator's browser. Not an inbound requirement — go2rtc dials out through STUN/TURN — but it is the local port it binds |

## No listener

These connect out to the web app over SignalR and never bind a port. Do not
allocate them one.

- QuickBooks Sync Service
- Kiosk Print Agent
- Scale Simulator
- Generic Scale Controller (`5137` appears in `launchSettings.json`; that is the
  Visual Studio debug profile, not a deployed listener)

## Rules

1. **One port per service, allocated here first.** Take the next free number in
   the 10-step sequence.
2. **Installers should refuse a taken port.** Kestrel's failure is invisible in
   systemd's output, so the installer has to catch it while someone is watching
   and name the process holding it — via `ss` on Linux, `Get-NetTCPConnection`
   on Windows.
3. **Updates should keep the port the install is already on.** The installers
   rewrite `Urls` in `appsettings.json` from their default on every run, so
   without this a routine update silently moves a service that was deliberately
   placed elsewhere — and a machine put on a custom port to dodge a conflict
   gets moved back into it. An explicit `--port` / `-Port` always wins.

### Where rules 2 and 3 are implemented

Now that the defaults are all distinct these are defence in depth, not the thing
standing between a site and a collision. They still matter: a Pi can be running
anything, a config can be hand-edited, and a second instance collides with the
first.

| Installer | Refuses a taken port | Keeps the existing port |
|-----------|----------------------|-------------------------|
| RFID Reader Service | yes | yes |
| Camera Capture Service | yes | yes |
| Gate Controller Service | no | no |
| Web Print Service | no | no |
| Scale Reader Service | no | no |

The three without it are worth bringing up to the same standard. Two of them are
submodules, so each needs its own branch and release.

## History

The RFID Reader Service defaulted to `5230`, the same as the Web Print Service,
and a scale house running both could only install the second one with an
explicit `--port`. It moved to `5250` in August 2026. Installs still on `5230`
or `5231` keep their port across updates; pass `--port 5250` to move one onto
the new default.
