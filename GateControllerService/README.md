# Gate Controller Service

Opens a gate — or turns on a light — when a truck finishes weighing, and closes
it again once the truck has driven off the scale.

Runs on a Raspberry Pi wired to a relay board on the GPIO header. It connects to
the Foundation web app over SignalR, the same way the print, camera and scale
reader services do, and needs no inbound firewall rule.

---

## How a cycle runs

1. A ticket completes on a scale. The web app looks up that scale's **Gate**
   (`serviceId:gateId`, set on the Scale page) and sends an open command to the
   Pi that owns it.
2. This service energises that gate's **gate pin** and **light pin**.
3. It watches the scale's live weight, which it reads off the ordinary weight
   broadcast every kiosk already receives.
4. The output releases on whichever comes first:
   - the scale reads below **Release Weight Threshold** *after* having been
     loaded during this cycle — the truck has left; or
   - **Max Open Seconds** elapses.

The timeout is not optional. Weight arrives over the network from another
process and can simply stop — a dead feed must not leave a barrier standing.
For the same reason the sweep that enforces it runs even while the connection
to the web app is down, and every gate is driven closed when the service stops.

The "after having been loaded" part matters: without it, a gate opening while
the deck is already empty would slam shut in the same instant.

---

## Configuration

One row per controlled exit, held in `gatecontrollerservice.db` next to the
binary and edited through the local API (Swagger UI at `http://<pi>:5240/`).

| Field | Meaning |
|---|---|
| `gateId` | Unique on this box. The web app addresses `serviceId:gateId`. |
| `gatePin` | BCM pin driving the gate relay. Null if no gate is wired. |
| `lightPin` | BCM pin driving the light. Null if no light is wired. |
| `invertOutputs` | True for active-low relay boards (most opto-isolated ones). |
| `scaleHardwareId` | The scale to watch, as `serviceId:scaleId` exactly as the reader service reports it. Null means this gate is released by its timeout alone. |
| `releaseWeightThreshold` | Below this, the deck counts as clear. Default 1000 lb. |
| `maxOpenSeconds` | Hard limit on how long the output stays energised. Default 120. |
| `triggerOn` | `WeighOut` (default), `WeighIn`, or `Both`. |
| `active` | Inactive gates are ignored without being deleted. |

A load closed in one weighment on a retained tare counts as a **weigh-out** — as
far as the yard is concerned the truck is leaving.

### Wiring check

```bash
curl -X POST http://<pi>:5240/api/gates/gate-1/test
```

Runs a real cycle so you can watch the relay pull in, then release on weight or
the timeout like any other. `GET /api/status` reports `gpioAvailable` — false
means the service is running but cannot physically move anything, which is what
you get on any machine without a GPIO chip.

---

## Install

Raspberry Pi (64-bit), from a release — no git and no .NET SDK needed on the Pi:

```bash
curl -fsSL -o gate.tar.gz https://github.com/GTMichelli-Dev/foundation/releases/latest/download/gate-controller-linux-arm64.tar.gz
mkdir -p /tmp/gcs && tar -xzf gate.tar.gz -C /tmp/gcs
bash /tmp/gcs/install.sh https://your-web-app-url --service-id north-gate
```

Or from a checkout of the monorepo:

```bash
bash GateControllerService/deploy/install.sh http://localhost:5110 --local "$PWD/GateControllerService"
```

The installer adds the service account to the `gpio` group, writes the systemd
unit, starts the service, and applies the URL and service id through the local
API. Re-run the same command to update — the gate configuration is preserved.

`--service-id` names this box on the web app. Gates are addressed as
`serviceId:gateId`, so two Pis at one site must not share it; it defaults to the
hostname.

The URL must match the web app's real scheme and port. A wrong one leaves the
service reconnecting forever — watch it with
`journalctl -u gate-controller-service -f`.

Released as `gate-controller-linux-arm64.tar.gz` alongside the web app, built by
the repo's `Release` workflow on a `v*` tag. Pi only, deliberately: the whole
point of this service is the GPIO header.

### After installing

1. Add the gates wired to this Pi at `http://<pi>:5240/` (Swagger).
2. On the web app, set each Scale's **Gate** to `<service-id>:<gate-id>`.
3. Confirm the wiring with the test endpoint below before a truck is involved.

---

## Safety

This service drives a physical barrier. Two things follow from that, and both
are deliberate in the code:

- **Failing to close is worse than failing to open.** A write that throws is
  logged as an error rather than swallowed, the timeout sweep is kept alive
  through any exception, and shutdown drives every gate closed.
- **Inversion is applied per gate, at the point of writing.** There is no
  blanket "set all pins low" anywhere, because on an active-low board low is
  *energised* — a well-meant reset like that would open every gate on the site.

The gate is an accessory to the weighment, never a gate on it: if this service
is down, or the command fails, tickets are still written normally.
