# RFID Reader Service

Reads HID / prox cards from an RS-232 card reader and reports each presentation
to the BasicWeigh web app over SignalR. It is the card-reading sibling of the
Scale Reader Service: one small .NET service per site, one read loop per
reader, configured from the web app rather than on the box.

Built for the **AWID Sentinel-Prox SP-6820** wired to the Pi controller over
RS-232, which sends a 26-bit Wiegand credential as 7 ASCII hex characters (see
[Card format](#card-format)). Nothing else in it is model-specific — other
readers are handled by configuration, not code (see
[Commissioning an unknown reader](#commissioning-an-unknown-reader)).

---

## What it does

```
  SP-6820 reader ──RS-232──> Pi (this service) ──SignalR──> BasicWeigh web app
                                                              │
                                                              └─> Kiosk mapped to
                                                                  "serviceId:readerId"
```

- Holds each configured serial port open and frames incoming bytes into card reads.
- Debounces the repeat reads a prox reader emits while a card is held near it.
- Publishes `CardRead { serviceId, readerId, cardNumber }` to the hub.
- Publishes `ReaderDiagnostic` for **every** frame, parsed or not, so an
  unknown reader can be worked out from the web app's Reader Management page.
- Accepts reader CRUD from the web app, so baud rate and port changes never
  require an SSH session.

The web app decides what a card *means* (which truck, which fields, weigh in vs
weigh out). This service only reports that a card was presented.

Which kiosk listens to which reader is chosen **on the kiosk**: a display that
does not recognise itself runs a short setup on its own screen and picks its
scale, printer and reader from what is connected at that moment. Nothing here
needs to know about kiosks.

---

## Install (Raspberry Pi / Linux)

From a release — no git and no .NET SDK needed on the Pi:

```bash
curl -fsSL -o rrs.tar.gz https://github.com/GTMichelli-Dev/foundation/releases/latest/download/rfid-reader-linux-arm64.tar.gz
mkdir -p /tmp/rrs && tar -xzf rrs.tar.gz -C /tmp/rrs
bash /tmp/rrs/install.sh https://your-server --service-id kiosk-1
```

Or from a checkout:

```bash
git clone https://github.com/GTMichelli-Dev/rfid-reader-service.git /tmp/rrs
bash /tmp/rrs/deploy/install.sh https://your-server --service-id kiosk-1
rm -rf /tmp/rrs
```

Until the code lives in its own repo (see `REPO-SETUP.md`), install it from the
monorepo checkout instead:

```bash
git clone https://github.com/GTMichelli-Dev/foundation.git /tmp/fnd
bash /tmp/fnd/RfidReaderService/deploy/install.sh https://your-server \
  --service-id kiosk-1 --local /tmp/fnd/RfidReaderService
```

| Option | Default | Notes |
|--------|---------|-------|
| `<web-server-url>` | — | Required. Must match the web app's real listen port. |
| `--service-id` | `default` | Kiosks map to readers as `serviceId:readerId`. |
| `--port` | `5250` | Local REST/Swagger port. See [service ports](../docs/service-ports.md). |
| `--install-dir` | `/opt/rfid-reader-service` | |
| `--local <path>` | — | Build from a local folder instead of cloning. |

Re-run the same command to update — the reader database is preserved.

The installer adds the service user to `dialout` and pins
`SupplementaryGroups=dialout` on the unit, because without it every serial open
fails with "Access to the port is denied" and the reader simply looks dead.

```bash
sudo systemctl status rfid-reader-service
sudo journalctl -u rfid-reader-service -f
```

---

## Install (Windows)

The reader is often wired to the weigh PC rather than to a Pi. Download
`rfid-reader-win-x64.zip` from the
[latest release](https://github.com/GTMichelli-Dev/foundation/releases/latest),
unzip it, and from an **admin** command prompt in that folder:

```
INSTALL.bat https://your-server -SerialPort COM3
```

Installs the Windows service with automatic startup, keeps the existing
database, and applies the URL and service id. Re-run the same command to update.

| Option | Default | Notes |
|--------|---------|-------|
| `-SerialPort` | — | Seeds one reader on that COM port. Omit it and none is seeded — add the reader from the web app instead. |
| `-ServiceId` | computer name | Kiosks map to readers as `serviceId:readerId`. |
| `-Port` | `5250` | See below. |
| `-InstallDir` | `C:\Services\RfidReaderService` | |
| `-ResetDb` | — | Start clean. A timestamped backup is taken regardless. |

**This service listens on 5250.** Every Foundation service owns its own port
(see [service ports](../docs/service-ports.md)), so several can share a Pi or a
scale-house PC without being told about each other. It defaulted to 5230 until
that collided with the Web Print Service — if you have an install still on 5230
or moved to 5231, it keeps that port across updates; pass `--port 5250` to bring
it onto the new default.

A service that cannot bind its port fails to start and stops, which looks
exactly like a crash on startup. The installer checks the port first and names
the process holding it.

The shipped `appsettings.json` seeds a reader on `/dev/ttyUSB0` — right for the
Pi, meaningless here — so the installer rewrites that seed rather than leaving
it. A seeded reader that can never open its port still publishes itself to the
web app, where it looks like a broken reader rather than one that was never
there.

`deploy/package-README.txt` ships inside the zip and covers the rest.

---

## Configuration

Readers are normally configured from the web app: **Setup → Options → Card
Readers**. Everything there is also available locally at
`http://<host>:5250/swagger`:

| Endpoint | Purpose |
|----------|---------|
| `GET /api/health` | Liveness. |
| `GET /api/status` | Per-reader state: last card, frame counts, port status. |
| `GET /api/serialports` | Serial ports this machine offers. |
| `GET /api/readers` | Reader configuration. |
| `POST /api/readers` | Add a reader. |
| `PUT /api/readers/{readerId}` | Update a reader. |
| `DELETE /api/readers/{readerId}` | Remove a reader. |
| `GET /api/readers/{readerId}/frames` | The last 25 frames, parsed or rejected. |
| `POST /api/readers/{readerId}/testparse` | Parse a captured frame without a card. |
| `GET /api/settings` / `PUT /api/settings` | Service id, server URL, hub path. |

### Reader settings

| Setting | Default | What it does |
|---------|---------|--------------|
| `serialPortName` | — | `/dev/ttyUSB0`, `COM3`. |
| `baudRate` / `dataBits` / `parity` / `stopBits` | 9600 / 8 / None / 1 | Line settings. |
| `format` | `Wiegand26` | `Wiegand26` (the SP-6820's format), `Auto`, `Digits`, `Hex`, `Raw`. |
| `includeFacilityCode` | `false` | Wiegand only: report `facility-card` instead of the bare card number. |
| `cardNumberRegex` | — | Group 1 is the card number. Overrides `format`. |
| `stripLeadingZeros` | `false` | Text formats only. Off by default — leading zeros are usually part of the printed number. |
| `minLength` | `4` | Text formats only. Shorter frames are treated as line noise. |
| `debounceMs` | `3000` | Repeats of the same card inside this window count as one presentation. |
| `idleFrameMs` | `60` | For readers that send no terminator: silence ends the frame. |

---

## Card format

The SP-6820 sends a **26-bit Wiegand credential as 7 ASCII hex characters**.
That is the `Wiegand26` format, and it is the default for a new reader.

Seven hex characters carry 28 bits. The last two are padding; the remaining 26
are the standard HID H10301 layout:

```
  bit  0        even parity
  bits 1..8     facility (site) code   —  8 bits, 0..255
  bits 9..24    card number            — 16 bits, 0..65535
  bit  25       odd parity

  "3DD9370"  ->  facility 123, card number 45678
```

The **card number is reported in decimal** — `45678` — which is normally the
number printed on the card, so enrollment is a matter of reading the card.
Confirm that on the first card rather than assuming it.

Parity bits are not verified. A card that works on existing access-control
hardware must not be rejected here over a parity rule.

Set `includeFacilityCode` only if the site has cards whose numbers repeat across
different facility codes. Numbers are then reported as `123-45678` and must be
enrolled in that form.

Framing is separate from decoding, and stays permissive: bytes accumulate until
a terminator (CR, LF, ETX) **or** until the line falls silent for
`idleFrameMs`. The same decode therefore works whether the reader sends
`3DD9370\r`, `<STX>3DD9370<ETX>`, or bare characters with no terminator at all.

## Commissioning an unknown reader

For a different reader, or an SP-6820 configured for some other output:

1. Add the reader with the port and 9600/8/None/1. If AWID support gives you
   different line settings, use those.
2. Open **Setup → Options → Readers** in the web app and watch the Live Reads
   panel. Every frame appears with its raw text and hex, parsed or not.
3. Present a card and compare the result with the number printed on it:
   - Correct → done.
   - 7 hex characters but the wrong number → the credential is probably not
     26-bit. Capture the hex and the printed number, and the format can be added.
   - Number embedded in other text → set `cardNumberRegex`, e.g. `C(\d+)` for
     `F1234,C0056789`.
   - Plain digits or plain hex → set `format` to `Digits`, `Hex`, or `Auto`.
   - Nothing arrives at all → wrong port, wrong baud, or wrong line settings.
     Check `GET /api/serialports` and the journal.
4. `POST /api/readers/{id}/testparse` replays a captured frame against the
   current settings, so a format or regex can be iterated without walking back
   to the reader with a card in hand.

Whatever this service reports is what must be enrolled in the web app.

---

## Development

```bash
dotnet run
```

Swagger opens at `http://localhost:5250/swagger`. Without a reader attached the
service still starts, connects to the hub, and reports zero active readers.
