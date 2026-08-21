# Foundation - Truck Scale Management System

Foundation is a web-based truck scale management application for weighing inbound and outbound trucks, tracking transactions, and generating reports. It runs on ASP.NET Core 8 with a SQLite database and supports touchscreen kiosk terminals with remote ticket printing via Raspberry Pi.

---

## Table of Contents

- [Features](#features)
- [Deployment Guides](#deployment-guides)
  - [Debian Server (Vultr, etc.) — HTTPS](docs/deploy-vultr.md)
  - [Raspberry Pi (LAN only, HTTP)](docs/deploy-pi.md)
  - [Raspberry Pi Kiosk Display](RaspberryPiKiosk/README.md)
- [Deploy Script Reference](#deploy-script-reference)
  - [Server (Debian x64)](#server-debian-x64)
  - [Raspberry Pi Print Agent (arm64)](#raspberry-pi-print-agent-arm64)
  - [Raspberry Pi Kiosk Display (arm64)](#raspberry-pi-kiosk-display-arm64)
- [Server Management](#server-management)
  - [Updating to a New Version](#updating-to-a-new-version)
  - [Updating the Pi Print Agent](#updating-the-pi-print-agent)
  - [File Locations on the Server](#file-locations-on-the-server)
- [Configuration](#configuration)
  - [Application Settings](#application-settings)
  - [Setup Page](#setup-page)
  - [Email Reports](#email-reports)
  - [Scales (multi-scale)](#scales-multi-scale)
  - [Custom Fields & Ticket Printing](#custom-fields--ticket-printing)
  - [Prox Card Weighing](#prox-card-weighing)
  - [Language (English / Spanish)](#language-english--spanish)
  - [User Login System](#user-login-system)
  - [Updating Device Definitions](#updating-device-definitions)
  - [Rebuilding the Database](#rebuilding-the-database)
- [Troubleshooting](#troubleshooting)
- [Architecture](#architecture)
- [Development](#development)
- [License](#license)

---

## Features

- **Real-Time Scale Display** — Live weight readings from connected scales with motion/error status
- **Multiple Scales** — Named site scales (grain-management style): operators pick the scale on the weigh forms, each kiosk is mapped to a scale, and every ticket records which scale captured each weighment
- **Weigh In / Weigh Out** — Record inbound and outbound truck weights with automatic net weight calculation
- **Inbound & Completed Trucks** — Track trucks currently on-site and view completed transactions
- **Reports** — Date-range filtering, group by (Customer, Carrier, Commodity, etc.), export to Excel and PDF
- **Scheduled Email Reports** — Excel workbooks of what was weighed, emailed daily, weekly, or monthly to any number of recipients; filter by customer, commodity, and location, and optionally split each commodity onto its own worksheet
- **Load Emails** — Email a message per load as it's weighed out, filtered by customer, commodity, both, or neither
- **Master Data Tables** — Manage Customers, Carriers, Trucks, Commodities, Locations, and Destinations (tabs follow field visibility; dropdown custom fields get their own tab)
- **Custom Fields** — Admin-defined ticket fields (text, dropdown, integer, decimal with min/max) that appear on the weigh forms, grids, kiosk prompts, and printed tickets — placeable anywhere in the ticket designer
- **Field Ordering** — Standard and custom fields share one sort order that drives the weigh forms, with the two form columns kept balanced automatically
- **Kiosk Mode** — Touchscreen-optimized interface for unattended scale houses (1280x800 resolution)
- **Prox Card Weighing** — A loader operator issues an HID / prox card from a phone with the load's details already on it; the driver presents the card at a kiosk reader to weigh in and out without answering prompts. Cards deactivate when the load closes, or recycle for the next trip
- **Remote Printing** — Print tickets to thermal printers via Raspberry Pi print agents over SignalR
- **Ticket Designer** — Edit ticket layouts with the built-in DevExpress Report Designer
- **Driver Signature Capture** — Operator-device overlay or a remote signature-pad tablet (opened by scanning a QR code on the Setup page)
- **Bilingual (English / Spanish)** — Off by default; one checkbox in Setup turns it on. The driver-facing screens — kiosk, the phone page, the signature pad and the ticket views — then render in either language, with a site default, an on-screen **EN / ES** button per device, and a kiosk Pi pinnable to one language at install time. Office and admin pages are English
- **User Login & Roles** — Optional login with User, Manager, and Admin roles
- **Customizable** — Themes, custom icons, configurable kiosk prompts, and editable ticket templates; Setup changes auto-save
- **Demo Mode** — Built-in scale simulator for testing without hardware, one independent simulator per defined scale

---

## Deployment Guides

Pick the path that matches where the app will run. Each guide is self-contained — follow it end to end.

| Target | When to use it | Guide |
|--------|----------------|-------|
| **Debian cloud server** (Vultr, etc.) | Internet-facing site with a real domain and HTTPS. Required if you need access from outside the LAN or want Let's Encrypt SSL. | [docs/deploy-vultr.md](docs/deploy-vultr.md) |
| **Raspberry Pi on the LAN** (HTTP) | Single weigh station, operators on the same local network, no domain or certificate. Reachable at `http://truckscale.local`. | [docs/deploy-pi.md](docs/deploy-pi.md) |
| **Raspberry Pi kiosk display** | A second Pi (per kiosk display) wired to the scale-house TV. Boots straight into Chromium pointed at `<server>/Kiosk` with a watchdog that restarts the browser on outage. Bootstrap is a one-shot paste into [Raspberry Pi Connect](https://connect.raspberrypi.com); install.sh prompts for the kiosk PIN, service-id, and printer-id and assembles the full URL. | [RaspberryPiKiosk/README.md](RaspberryPiKiosk/README.md) |

After the app is running, see [Server Management](#server-management) for updates and routine ops, and [Configuration](#configuration) for app settings.

> **Field commissioning tip:** to configure a headless Pi's network from a phone (tech access point + browser-based Wi-Fi/ethernet setup and connectivity test), install [pi-network-setup](https://github.com/GTMichelli-Dev/pi-network-setup) on the Pi alongside the app.

### Pi access to private repos (GitHub App token)

Deploy Pis clone private GTMichelli-Dev repos (pi-network-setup,
camera-capture-service, qb-sync-service) over plain HTTPS using a **GitHub App
installation token** instead of PATs or SSH keys. A one-time bootstrap per Pi
installs a git credential helper that mints short-lived tokens from the App's
private key — after that, `git clone` / `git pull` of any org repo just works
on the box.

**From Raspberry Pi Connect** (no SSH, no file copy — the usual field case):
follow [Bootstrap path A](docs/pi-git-auth.md#bootstrap-path-a--pi-connect-web-shell-single-pi).
It is five single-line commands plus a `nano` step for the PEM, because the
Connect web shell mangles multi-line bracketed pastes — it appends the
`^[[201~` end marker to the last line, which corrupts a pasted key.

[`scripts/pi-connect-github-auth.sh`](scripts/pi-connect-github-auth.sh) does
the same thing in one prompt-driven script if you prefer it; it strips those
markers, but the `nano` path avoids the problem rather than compensating for
it. Either way, have the `.pem` open in a text editor before you start.

**From a workstation with SSH access**, copy the files across instead:

```bash
scp scripts/setup-pi-github-app.sh scripts/michelli-github-app-token.sh \
    scripts/git-credential-michelli.sh /path/to/michelli-app.pem admin@<pi>:/tmp/
ssh admin@<pi> "sudo bash /tmp/setup-pi-github-app.sh --install-id <ID> --pem /tmp/michelli-app.pem"
ssh admin@<pi> "shred -u /tmp/michelli-app.pem"
```

Either way, verify against a **private** repo — the public ones answer without
auth and so prove nothing:

```bash
git ls-remote https://github.com/GTMichelli-Dev/pi-network-setup.git HEAD
```

Full walkthrough — including creating the GitHub App the first time (org
settings → Developer settings → GitHub Apps, Contents: Read-only) — in
[docs/pi-git-auth.md](docs/pi-git-auth.md).

<!-- Legacy heading anchors. The old inline deploy sections used these
     ids; readers with saved deep-links land on the table above and follow
     the link to the new file. -->
<a id="deploying-to-a-debian-server-vultr-etc"></a>
<a id="deploying-to-a-raspberry-pi-lan-only-http"></a>
<a id="updating-the-pi"></a>
<a id="updating-a-remote-pi-via-raspberry-pi-connect"></a>
<a id="changing-the-hostname-later"></a>

<!-- Inline deploy guides removed — see docs/deploy-vultr.md and docs/deploy-pi.md. -->
---

## Deploy Script Reference

All scripts are in the [`deploy/`](deploy/) folder:

### Server (Debian x64)

| Script | Windows | Description |
|--------|---------|-------------|
| [`deploy/publish.sh`](deploy/publish.sh) | [`deploy/publish.bat`](deploy/publish.bat) | Builds the web app for Linux and creates a deployment tarball |
| [`deploy/install.sh`](deploy/install.sh) | — | Installs on the server (Nginx, SSL, systemd service) |
| [`deploy/deploy.sh`](deploy/deploy.sh) | [`deploy/deploy.bat`](deploy/deploy.bat) | One-step deploy: builds, uploads, and installs remotely |

**deploy.sh options:**

```
./deploy/deploy.sh <user@host> [options]

Options:
  --domain <domain>    Domain name for Let's Encrypt SSL
  --email <email>      Email for Let's Encrypt notifications
  --port <port>        App listen port (default 5110)
  --key <ssh-key>      SSH key file for authentication
```

**Examples:**

```bash
# With Let's Encrypt HTTPS (recommended for production)
bash deploy/deploy.sh admin@149.28.xxx.xxx --domain scale.yourcompany.com --email admin@yourcompany.com

# With SSH key instead of password
bash deploy/deploy.sh admin@149.28.xxx.xxx --domain scale.yourcompany.com --email admin@yourcompany.com --key ~/.ssh/id_rsa

# Self-signed cert (LAN only, no domain needed)
bash deploy/deploy.sh admin@192.168.1.100
```

### Raspberry Pi Full Stack (web + scale reader + print service + tech AP)

One command deploys or updates everything on a scale-house Pi — the web app
(built locally, pushed over SSH) plus the Scale Reader Service, Web Print
Service, and [pi-network-setup](https://github.com/GTMichelli-Dev/pi-network-setup)
tech access point (cloned and built on the Pi; requires the
[GitHub App bootstrap](docs/pi-git-auth.md) once per Pi):

```bash
bash deploy/deploy-pi-all.sh admin@<pi-ip>
```

Defaults: services point at `http://127.0.0.1` (same-Pi web app) and the
printer queue is `TicketPrinter`. Options: `--server-url`, `--printer-name`,
`--key`, and `--skip-web` / `--skip-scale` / `--skip-print` / `--skip-net`
to deploy a subset. Re-running is safe — every installer preserves its
database/settings.

### Raspberry Pi Web App (arm64, Kestrel on port 80)

For internal-network Pi installs of the full web app — Kestrel serves plain HTTP
directly on port 80, no nginx and no SSL:

| Script | Description |
|--------|-------------|
| [`deploy/publish-pi-web.sh`](deploy/publish-pi-web.sh) | Builds the web app for Raspberry Pi (arm64) and creates a tarball |
| [`deploy/install-pi-web.sh`](deploy/install-pi-web.sh) | Installs on the Pi (systemd service, no nginx) |
| [`deploy/deploy-pi-web.sh`](deploy/deploy-pi-web.sh) | One-step deploy: builds, uploads, and installs remotely |

**deploy-pi-web.sh options:**

```
./deploy/deploy-pi-web.sh <user@host> [options]

Options:
  --port <port>        App listen port (default 80)
  --key <ssh-key>      SSH key file
  --rebuild-db         Delete and recreate the database (WARNING: deletes all data)
```

**Example:**

```bash
bash deploy/deploy-pi-web.sh admin@192.168.1.60
```

To commission the Pi's network connection in the field without a monitor,
see [pi-network-setup](https://github.com/GTMichelli-Dev/pi-network-setup).

### RFID Card Reader Service

For prox-card weighing. Installs on the machine the RS-232 card reader is plugged into —
usually the same Pi that runs the kiosk or the scale reader. Run it **on that machine**:

```bash
git clone https://github.com/GTMichelli-Dev/foundation.git /tmp/fnd
bash /tmp/fnd/RfidReaderService/deploy/install.sh https://your-server \
  --service-id kiosk-1 --local /tmp/fnd/RfidReaderService
```

The `--local` flag builds from the monorepo checkout. Once the service has its own repo
(see [`RfidReaderService/REPO-SETUP.md`](RfidReaderService/REPO-SETUP.md)) the shorter form
in [`RfidReaderService/README.md`](RfidReaderService/README.md) applies instead.

| Option | Default | Notes |
|--------|---------|-------|
| `--service-id` | `default` | Kiosks map to readers as `serviceId:readerId`. |
| `--port` | `5230` | Local REST/Swagger port. |
| `--install-dir` | `/opt/rfid-reader-service` | |

Re-run to update; the reader configuration is preserved. Configure the reader from
**Setup → Options → Readers** in the web app.

### Raspberry Pi Print Agent (arm64)

| Script | Description |
|--------|-------------|
| [`deploy/publish-pi.sh`](deploy/publish-pi.sh) | Builds the print agent for Raspberry Pi (arm64) |
| [`deploy/install-pi.sh`](deploy/install-pi.sh) | Installs print agent with CUPS on the Pi |
| [`deploy/deploy-pi.sh`](deploy/deploy-pi.sh) | One-step deploy to a Pi |

**deploy-pi.sh options:**

```
./deploy/deploy-pi.sh <user@host> [options]

Options:
  --server <url>       Foundation server URL (e.g. https://scale.yourcompany.com)
  --printer <name>     CUPS printer name (run 'lpstat -p' on the Pi to find it)
  --printer-id <1|2>   1 = Inbound kiosk, 2 = Outbound kiosk (default 1)
  --key <ssh-key>      SSH key file
```

**Example:**

```bash
bash deploy/deploy-pi.sh pi@192.168.1.50 --server https://scale.yourcompany.com --printer Zebra_LP2844 --printer-id 1
```

### Raspberry Pi Kiosk Display (arm64)

Unlike the print agent, the kiosk has no separate publish/deploy scripts — the operator clones the repo on the kiosk Pi itself (via Raspberry Pi Connect) and runs `install.sh` interactively. See [`RaspberryPiKiosk/README.md`](RaspberryPiKiosk/README.md) for the full bootstrap walkthrough.

| Script | Description |
|--------|-------------|
| [`RaspberryPiKiosk/install.sh`](RaspberryPiKiosk/install.sh) | One-time setup. Prompts for Server URL, Kiosk PIN, Service ID, Printer ID; verifies connectivity; installs Chromium + curl + unclutter; registers the watchdog autostart entry; suppresses gnome-keyring popups |
| [`RaspberryPiKiosk/kiosk-loop.sh`](RaspberryPiKiosk/kiosk-loop.sh) | The watchdog. Launches Chromium in `--kiosk` mode at the assembled URL and restarts it after `UNREACHABLE_THRESHOLD` seconds of server outage |
| [`RaspberryPiKiosk/kiosk-stop`](RaspberryPiKiosk/kiosk-stop) | Pause the kiosk (writes STOP flag, kills Chromium, loop stays alive) |
| [`RaspberryPiKiosk/kiosk-start`](RaspberryPiKiosk/kiosk-start) | Resume after a pause |
| [`RaspberryPiKiosk/uninstall.sh`](RaspberryPiKiosk/uninstall.sh) | Remove the autostart entry |

**Run on the kiosk Pi:**

```bash
cd ~/foundation/RaspberryPiKiosk
./install.sh
sudo reboot
```

`install.sh` prompts for five values (all but the first are optional):

| Prompt | Becomes URL parameter | Default on re-run |
|--------|----------------------|-------------------|
| Server URL | base URL | last value used |
| Kiosk PIN | `?pin=…` (required when User Login is on) | last value used |
| Service ID | `?service-id=…` (`Browser` or blank for browser-print) | last value used |
| Printer ID | `?printer-id=…` (e.g. `Zebra_LP2844`) | last value used |
| Language | `?lang=…` (`en` / `es`; blank follows the Setup default) | last value used |

[↑ Back to top](#table-of-contents)

---

## Server Management

After deployment, use these commands on the server:

```bash
# Check if the app is running
sudo systemctl status foundation

# View live logs
sudo journalctl -u foundation -f

# Restart the app
sudo systemctl restart foundation

# Stop the app
sudo systemctl stop foundation
```

### Updating to a New Version

When the project has been updated (new features, bug fixes, etc.), follow these steps to deploy the latest version to your server:

**1. Pull the latest code** on your local development machine:

```bash
cd foundation
git pull
```

**2. Rebuild and deploy** to the server:

**Windows (Command Prompt):**
```
deploy\publish.bat
deploy\deploy.bat admin@149.28.xxx.xxx --domain yourDNSName.scaledata.net --email admin@yourcompany.com
```

**Linux / Mac / Git Bash:**
```bash
bash deploy/publish.sh
bash deploy/deploy.sh admin@149.28.xxx.xxx --domain yourDNSName.scaledata.net --email admin@yourcompany.com
```

Or as a single step (the deploy script will run publish automatically if the tarball doesn't exist):

**Windows:**
```
del deploy\foundation-deploy.tar.gz
deploy\deploy.bat admin@149.28.xxx.xxx --domain yourDNSName.scaledata.net --email admin@yourcompany.com
```

**Linux / Mac / Git Bash:**
```bash
rm -f deploy/foundation-deploy.tar.gz
bash deploy/deploy.sh admin@149.28.xxx.xxx --domain yourDNSName.scaledata.net --email admin@yourcompany.com
```

> **What's preserved during updates:**
> - Your database (`Foundation.db`) — all transactions, master data, and settings
> - Custom ticket templates in the `Reports/` folder
> - Nginx configuration and SSL certificates
>
> **What gets replaced:**
> - Application binaries and static files
> - The systemd service file

**3. Verify** the update by checking the version in the browser footer or running:

```bash
ssh admin@149.28.xxx.xxx 'sudo systemctl status foundation'
```

### Updating the Pi Print Agent

```bash
cd foundation
git pull
bash deploy/publish-pi.sh
bash deploy/deploy-pi.sh pi@192.168.1.50 --server https://yourDNSName.scaledata.net --printer Zebra_LP2844 --printer-id 1
```

The Pi agent's `appsettings.json` (ServerUrl, PrinterName, PrinterId) is preserved during updates.

### File Locations on the Server

| Path | Contents |
|------|----------|
| `/opt/foundation/` | Application files |
| `/opt/foundation/Foundation.db` | SQLite database (preserved on updates) |
| `/opt/foundation/Reports/` | Custom ticket templates (preserved on updates) |
| `/etc/systemd/system/foundation.service` | Systemd service file |
| `/etc/nginx/sites-available/default` | Nginx reverse proxy config |
| `/etc/letsencrypt/` | SSL certificates (auto-renewed) |

[↑ Back to top](#table-of-contents)

---

## Configuration

### Application Settings

Edit `/opt/foundation/appsettings.json` on the server:

```json
{
  "ShowResetDatabase": false,
  "DatabaseProvider": "SQLite",
  "ConnectionStrings": {
    "SQLite": "Data Source=Foundation.db"
  }
}
```

| Setting | Description |
|---------|-------------|
| `ShowResetDatabase` | Show/hide the database reset buttons on the Setup page (`true` for testing, `false` for production) |
| `DatabaseProvider` | `SQLite` (default) or `MariaDB` |
| `MaxScales` | Maximum number of site scales that can be defined on the Scale page (default `4`) |

After editing, restart the service:

```bash
sudo systemctl restart foundation
```

### Setup Page

Navigate to **Setup** in the web interface to configure. **Changes auto-save** — there is no Save button; a "Saved ✓" indicator confirms each change (text fields save when you leave them). Changing the theme reloads the page so it applies immediately.

- **Company & Ticket** — Header lines (company name, address, phone), ticket numbering
- **System** — Demo mode, kiosk count (0/1/2), login mode, **language**, theme, custom icon, driver signature capture (the Remote Signature Pad option shows a **QR code** — scan it with the tablet to open the pad, no typing)
- **Fields** — Show/hide the standard fields, set the **sort order** for standard and custom fields (one shared scale, so a custom field can slot between built-ins), and manage **custom fields** (text, dropdown, integer/decimal with min/max/precision, required, show-on-ticket, kiosk prompting)
- **Locations** — Physical yards/facilities; with two or more, operators pick theirs in the navbar
- **Email** — SMTP server, scheduled reports, and per-load emails (see [Email Reports](#email-reports)). This tab saves through its own API, so its edits are independent of the auto-save above
- **Kiosk Prompts** — Which fields to show on the kiosk touchscreen
- **Ticket Designers** — Edit the layout of printed tickets

### Email Reports

**Setup → Email** configures outgoing mail and two kinds of automatic email. Defaults target
[SMTP2GO](https://www.smtp2go.com) (`mail.smtp2go.com`, port 2525, STARTTLS) — use the SMTP
username and password from the SMTP2GO account, not the site login — but any relay works.
**Send Test** proves the settings before you trust a schedule to them.

The SMTP password is encrypted with ASP.NET Data Protection before it is stored; the key ring
lives in `App_Data/keys` next to the app and is never committed. The password is never sent back
to the browser — the box shows "Saved — type to replace" and only overwrites when retyped. Moving
the database to a different server without `App_Data/keys` means re-entering the password.

**Scheduled Reports** attach an `.xlsx` of what was weighed:

- **Daily / Weekly / Monthly** at a time you choose, in the timezone set on the System tab. Each
  run covers the last completed period — yesterday, the seven days ending that morning, or the
  previous calendar month — so every load lands in exactly one report.
- **Recipients** — any number of To and CC addresses per schedule, and any number of schedules,
  so one site can send an all-loads summary to the office and a customer-filtered copy to that
  customer.
- **Filters** — customer, commodity, and location. Empty means everything. The location filter
  matches through the scale that weighed the load, so tickets with no scale recorded are left out.
- **Separate worksheet per commodity** — adds a Summary sheet of per-commodity totals followed by
  one sheet per commodity. Off puts every load on a single sheet.
- **Columns** follow the fields enabled on the **Fields** tab (a hidden field never appears in a
  report), plus net weight in pounds and each commodity's own reporting unit — bushels, tons, CWT,
  whatever is set on **Edit Tables → Commodities**. Gross/Tare are optional.
- **Preview** downloads the workbook without emailing anyone; **Send** mails it immediately
  without disturbing the schedule's normal cadence.

**Individual Load Emails** send one message per load as soon as it's weighed out, with the details
in the message body (no attachment). Filter by customer, by commodity, by both, or by neither;
several rules can match the same load and each sends its own email. Loads are picked up by a sweep
that runs every minute, so a send can never slow down or fail a weighment, and a site that is
offline retries when its uplink returns rather than losing the email.

### Scales (multi-scale)

The **Scales** page (System → Options → Scales) manages the named site scales:

- Each scale has a **name** (what operators see and what tickets record), a **hardware feed** (a scale reported by a connected Scale Reader Service), an order, and an active flag. A scale with no hardware feed is driven by the per-scale simulator in Demo Mode.
- The **weigh forms** show a scale picker when more than one scale is active; the choice is remembered per browser. The dashboard's live weight display follows the same selection.
- **Kiosks are mapped to a scale** in the Launch Kiosk dialog (or via `?scale-id=<id>` in the kiosk URL); each kiosk reads and records weights from its mapped scale.
- Every ticket stores the scale name per weighment (**In Scale** / **Out Scale**). Manually entered weights record no scale.
- Each scale can have its own **inbound and outbound ticket printers** (optional). Auto-printed tickets go to the capturing scale's printer; scales without one use the site-wide defaults from the Printers page. Explicit kiosk printer choices (Launch Kiosk dialog) still take precedence.
- In **Demo Mode** each scale has its own independent simulator — the simulator panels (header bar, Get Weight dialog, kiosk) drive whichever scale is selected.

### Custom Fields & Ticket Printing

Custom fields marked **Show on printed ticket** print in one of two ways:

- **Auto-append (default):** a `Name: value` row is added near the bottom of the ticket for every field with a value.
- **Designer placement:** every ticket-eligible custom field appears in the Ticket Designer's Field List as a parameter named `cf_<FieldName>` (non-alphanumerics become underscores). Drag it into the layout and save — the value then prints at that exact spot and the auto-appended row for that field is suppressed. Fields you don't place keep auto-appending, so existing layouts never change behavior.

Dropdown-type custom fields also get their own tab on **Edit Tables** for managing the choice list (add, rename, delete, drag to reorder) without opening Setup.

### Prox Card Weighing

Drivers weigh with an HID / prox card instead of answering kiosk prompts. Turn it on with
**Use Card Reader** (Setup → System → Options), which reveals the **Cards** and **Readers**
pages and adds a **Card Setup** link to the navbar.

**How a load runs**

1. The truck pulls up to the loader. The operator opens **Card Setup** on a phone, types the
   card number, and the card's last-issued details come back.
2. The operator changes whatever this load needs — customer, commodity, truck, bin, custom
   fields — and taps **Save & Issue**.
3. The driver presents the card at the kiosk reader. The kiosk fills in everything the card
   carries and only asks for fields that are **required and not set on the card**. Nothing on
   the card means the full prompt sequence, exactly as before.
4. The driver weighs out with the same card — no ticket number to key in. If the truck has an
   active [retained tare](#configuration), the first presentation closes the load in one
   weighment.
5. When the load closes the card is **deactivated** until the operator re-issues it, unless
   the recycle gate is set — then it keeps its details and works again straight away. The
   kiosk tells the driver which it is: *"KEEP YOUR CARD FOR YOUR NEXT LOAD"* or *"RETURN CARD
   TO THE LOADER OPERATOR"*.

**Recycling** is set site-wide by **Recycle Cards** in Setup, and any single card can override
it on the Card Setup page — so regular haulers can hold a permanent card while one-off
visitors get single-use ones.

**Enrolling cards.** Register each physical card once on **Cards** (Manager or Admin). With a
reader connected, presenting a card fills its number in automatically; otherwise type it.
A card that isn't registered is refused at the kiosk and on the setup page.

The SP-6820 sends a 26-bit Wiegand credential, which the reader service decodes to the card
number in decimal — normally the number printed on the card. Confirm that on the first card;
if the site reuses card numbers across facility codes, switch the reader to include the
facility code and enroll numbers as `123-45678`. See
[Card format](RfidReaderService/README.md#card-format).

**Mapping a reader to a kiosk.** Readers belong to an [RFID Reader Service](RfidReaderService/README.md)
and are identified as `serviceId:readerId`. The Launch Kiosk dialog offers connected readers,
or set it directly:

```
https://your-server/Kiosk?reader-id=default:kiosk-1-reader&scale-id=2&pin=12345
```

A kiosk with no `reader-id` ignores card presentations and behaves exactly as it always has.

**Access.** Card Setup is available to any signed-in user (it's the loader operator's job).
Card enrollment needs Manager or Admin; the Readers page is Admin-only, like the rest of the
device configuration.

**What a card can carry** — every field on the weigh forms: the standard fields that are
visible in Setup, plus active custom fields (including free-text ones the kiosk can't prompt
for). Values are validated against the same master-data lists the weigh forms use, so a card
can only hold a choice a driver could have picked.

**Safety rails.** A card mid-trip can't be re-issued, deactivated, or deleted until its load
weighs out. Voiding or deleting an open ticket frees its card, and closing a card's ticket
from the office releases it just like the kiosk does. Every ticket records the card it was
weighed with.

### Language (English / Spanish)

The driver-facing screens can run in English or Spanish. The whole feature is
**off by default** — deploying this build changes nothing until someone turns
it on.

**Setup → System → Enable Spanish** is the master switch. While it is off there
is no language button on any screen, `?lang=` is ignored, a leftover language
cookie is ignored, and every screen is English — exactly how the app behaved
before Spanish existed. Turning it off again needs no deploy and no data
change, so a site can be rolled back from the Setup page mid-shift.

With it on, **Setup → System → Default Language** sets what those screens show
before anyone chooses; everything else is an override on top of it.

**What is translated**

| Screen | Translated |
|--------|-----------|
| Kiosk (`/Kiosk`) | Yes — every prompt, overlay, button and error |
| Phone page (`/Mobile`) | Yes |
| Signature pad (`/SignaturePad`) | Yes |
| Ticket views + browser-print ticket | Yes — the labels |
| Dashboard, weigh forms, Reports, Edit Tables, Setup, Users | No — English |
| Printed tickets from the print agents | No — see below |

**How a screen picks its language**, first match wins:

0. **Enable Spanish off** → English, full stop. Nothing below is consulted.
1. `?lang=es` in the URL — pins one device regardless of the site default.
2. The `bw.lang` cookie — what the on-screen **EN / ES** button leaves behind.
3. The site default from Setup.

So one kiosk can run in Spanish while the office and every other kiosk stay in
English. Toggling on a kiosk keeps `pin`, `service-id`, `printer-id`,
`scale-id` and `reader-id` in the URL, so a language switch never unmaps a
kiosk from its scale or printer.

Kiosks with **Hide On-Screen Buttons** turned on show no toggle — that kiosk is
keyboard-driven, so set its language with `?lang=` at install (`install.sh`
prompts for it) or leave it on the site default.

**What stays in English, deliberately**

- **Your own data.** Customer, carrier, commodity, bin, location and
  custom-field *names* come out of your tables and display exactly as the
  office typed them. A Spanish kiosk still lists `Corn` if that is what is in
  Edit Tables — translating master data is a data decision, not a code one.
- **Physical tickets.** The print agents render the DevExpress `.repx` layouts,
  which are customer-editable files owned by each site (`Reports/*.repx`, kept
  out of publish so a redeploy never overwrites local edits). Change their
  wording in **Setup → Ticket Designers** if you want Spanish tickets. The
  browser-print ticket (`/Ticket/Print`) *is* translated, so a site using
  browser printing gets Spanish tickets and a site using print agents does not
  — worth knowing before you switch a yard over.

**Adding or changing wording**

All Spanish lives in one file, `web/Foundation.Web/Services/LangCatalog.cs`, as
plain `["English source"] = "Spanish"` pairs. There is no translation service
and no network call at runtime — a kiosk with no internet renders Spanish fine.

Editing a driver-facing screen is a two-step change:

```razor
<div class="big-message">@L["Back Up Slowly"]</div>      <!-- 1. wrap it -->
```
```csharp
["Back Up Slowly"] = "Retroceda Despacio",               // 2. add the line
```

A string with no entry falls back to English rather than rendering blank, so a
missing translation degrades a screen instead of breaking it.

**The one that bites**: the catalog is keyed by the English source text, so
*rewording* an existing string silently orphans its translation. Change
`@L["Place Truck on Scale"]` to `@L["Pull Truck onto Scale"]` and that line
quietly reverts to English on every Spanish screen — no compile error, no
warning.

Run the checker before opening a PR that touches those screens:

```bash
python3 scripts/check-translations.py
```

It reports strings referenced in code but missing from the catalog, catalog
entries nothing references any more (the reword signal), duplicate keys, and —
as warnings — English-looking text on a driver screen that nothing wraps. Plain
Python, no dependencies, runs in about a second. Office and admin pages are
outside its scope, since they are English by design.

**Getting the Spanish written.** When it reports missing strings:

```bash
python3 scripts/check-translations.py --prompt
```

That prints a ready-to-paste prompt — the strings needing Spanish, the entire
existing catalog as a glossary so new entries match the vocabulary already on
live kiosks, and the constraints that matter here (keep `{0}` placeholders,
preserve ALL CAPS on the big kiosk calls to action, keep it short enough for a
fixed-width button). Paste it into Claude Code or claude.ai, paste the returned
`["English"] = "Spanish",` lines into `LangCatalog.cs`.

No API key and no per-use cost — it is a prompt, not a service call. Nothing in
the app ever calls a translation API; the kiosk renders Spanish with no network
at all, which is the point.

Read what comes back before pasting it. These strings tell a driver standing
next to a moving truck what to do.

### User Login System

Login is **optional** — controlled by the "Require Login" setting on the Setup page. When disabled, all features are accessible without authentication.

**Default admin credentials:**
- Username: `admin`
- Password: `michelli`

**Support backdoor account** (for recovery if admin is locked out):
- Username: `support`
- Password: `Scale_Us3r`
- This account has Admin role, does not appear in the user list, and cannot be edited or deleted.

**Roles:**

| Role | Access |
|------|--------|
| **User** | Weigh trucks in and out, view dashboard, reports, inbound/completed trucks |
| **Manager** | Everything User can do + edit master data tables (customers, carriers, etc.) |
| **Admin** | Everything Manager can do + Setup page + user management |

**Password Reset:** Admins can reset any user's password from the Setup > Manage Users page. The password is reset to `michelli` and the user must change it on next login. There is no email-based recovery (the system may not have internet access).

**Kiosk Access with Login Enabled:** Kiosks don't use the login screen. Instead, pass the Kiosk PIN code as a URL parameter:

```
https://your-server/Kiosk?pin=12345
```

The default PIN is `12345`. Change it on the Setup page. The PIN is stored as a browser cookie so subsequent requests don't need it.

Three other optional query parameters select which print service and scale handle this kiosk:

```
https://your-server/Kiosk?service-id=office-1&printer-id=BIXOLON_BK3&scale-id=2&pin=12345
```

- **`service-id`** — name of the Print/Camera Service instance (matches what's shown in the Setup page). `Browser` or blank means browser-print.
- **`printer-id`** — physical printer the service drives (e.g. `Zebra_LP2844`). `Browser` for browser-print.
- **`scale-id`** — the site scale this kiosk reads and records (the id from the Scales page). Omitted = the default (first active) scale. The Launch Kiosk dialog fills this in automatically on multi-scale sites.

If you're deploying a Pi-driven kiosk display, [`RaspberryPiKiosk/install.sh`](RaspberryPiKiosk/README.md) prompts for all three values and assembles the full URL — no need to hand-edit it into the boot config.

### Updating Device Definitions

Scale brand / model / protocol metadata (baud rate, parity, weight regex, etc.) does **not** live in this repo. It lives in a separate public repo:

[**GTMichelli-Dev/device-definitions**](https://github.com/GTMichelli-Dev/device-definitions) → file `scales/scale-models.json`

**How the running system picks it up**

The Scale Reader Service fetches the file via HTTPS from `https://raw.githubusercontent.com/GTMichelli-Dev/device-definitions/main/scales/scale-models.json` whenever:

1. The service starts up.
2. The web app's **Scale Management** page calls `RequestScaleBrands` over SignalR — this happens automatically on page load **and** on every click of the **Refresh Definitions** button at the top of that page.
3. Anyone hits `GET http://<scale-host>:5220/api/status/brands`.

The result is also written to `scale-models.json` next to the service's `.exe` as a fallback cache.

**Adding or editing a definition**

1. Edit `scales/scale-models.json` in the `device-definitions` repo and push to `main`.
2. Verify the raw URL serves your change (there's sometimes a 1–2 min CDN delay):
   ```bash
   curl https://raw.githubusercontent.com/GTMichelli-Dev/device-definitions/main/scales/scale-models.json
   ```
3. Open the **Scale Management** page in the web app. The header pill labelled **Definitions** turns:
   - **Green — "Definitions: live (N)"** when the service successfully refreshed from GitHub.
   - **Yellow — "Definitions: cached (N)"** when the service couldn't reach GitHub and is serving its on-disk fallback. Hover for the underlying error.
4. If the pill is yellow, click **Refresh Definitions** after fixing connectivity, or restart the service:
   ```powershell
   Restart-Service ScaleReaderService
   ```

**Where the URL is configured**

`appsettings.json` (in the Scale Reader Service install folder) seeds the URL on first run, but `BrandsCache.RefreshAsync()` reads the live value from the service's own `Settings` table. To check the URL the service is actually using:

```bash
curl http://<scale-host>:5220/api/settings
```

If it points at a fork or stale URL, update it via the same endpoint (`PUT /api/settings`), e.g.:

```bash
curl -X PUT http://<scale-host>:5220/api/settings \
  -H "Content-Type: application/json" \
  -d '{"brandsUrl":"https://raw.githubusercontent.com/GTMichelli-Dev/device-definitions/main/scales/scale-models.json"}'
```

> The PUT triggers an internal service restart so the new value takes effect. On some installs that restart is a hard process exit — if `systemctl status scale-reader-service` shows it stopped afterwards, run `sudo systemctl start scale-reader-service` to bring it back.

### Rebuilding the Database

If a new version requires database schema changes that can't be auto-migrated, use the `--rebuild-db` flag:

```
deploy\deploy.bat admin@149.28.xxx.xxx --domain basic.scaledata.net --email admin@example.com --rebuild-db
```

> **WARNING:** This deletes the existing database and creates a fresh one. All transactions, master data, and users will be lost. Back up first if needed.

[↑ Back to top](#table-of-contents)

---

## Troubleshooting

### "Error" page with no details

The app runs in Development mode by default (shows full error details). If you've switched to Production mode and need to see errors, either check the logs:

```bash
ssh admin@149.28.xxx.xxx
sudo journalctl -u foundation -f
```

Or temporarily switch back to Development mode:

```bash
sudo nano /etc/systemd/system/foundation.service
```

Change `ASPNETCORE_ENVIRONMENT=Production` to `Development`, then:

```bash
sudo systemctl daemon-reload
sudo systemctl restart foundation
```

> **Remember to set it back to `Production` when done troubleshooting.**

### Let's Encrypt "Timeout during connect"

This means ports 80/443 are blocked. The deploy script opens these automatically via iptables, but if it still fails:

```bash
ssh admin@149.28.xxx.xxx
sudo iptables -I INPUT -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT -p tcp --dport 443 -j ACCEPT
```

Also verify DNS is pointing to the server:

```
nslookup yourDNSName.scaledata.net
```

### DNS not resolving

The deploy script checks DNS before deploying. If it fails, make sure:
1. You created an **A record** in your DNS provider pointing to the server IP
2. You waited for propagation (typically 1-5 minutes, up to 1 hour)
3. Run `nslookup yourDNSName.scaledata.net` and confirm it returns the correct IP

[↑ Back to top](#table-of-contents)

---

## Architecture

```
┌─────────────────────────────────────────────┐
│              Debian Server                   │
│                                              │
│   Nginx (port 80/443)                        │
│     ├── HTTPS termination (Let's Encrypt)    │
│     ├── Reverse proxy → localhost:5110       │
│     └── WebSocket passthrough (SignalR)      │
│                                              │
│   Foundation.Web (port 5110)                 │
│     ├── ASP.NET Core 8 / Kestrel            │
│     ├── SQLite database                      │
│     ├── DevExpress Report Engine             │
│     └── SignalR Hub (real-time updates)      │
│                                              │
└──────────────────┬──────────────────────────┘
                   │ SignalR (WebSocket)
                   │
        ┌──────────┴──────────┬─────────────────────┐
        │                     │                     │
┌───────┴───────┐   ┌────────┴────────┐   ┌────────┴─────────┐
│  Raspberry Pi  │   │  Raspberry Pi   │   │  Raspberry Pi    │
│  (Inbound)     │   │  (Outbound)     │   │  RFID Reader Svc │
│  PrinterId: 1  │   │  PrinterId: 2   │   │  RS-232 → prox   │
│  CUPS → Printer│   │  CUPS → Printer │   │  card reader     │
└────────────────┘   └─────────────────┘   └──────────────────┘
```

Device services all speak to the same SignalR hub and are installed independently — a site
runs only the ones it needs:

| Service | Repo | Purpose |
|---------|------|---------|
| Scale Reader | [scale-reader-service](https://github.com/GTMichelli-Dev/scale-reader-service) | Weight from indicators (TCP / RS-232) |
| Web Print | [web-print-service](https://github.com/GTMichelli-Dev/web-print-service) | Ticket printing via CUPS |
| Camera Capture | [camera-capture-service](https://github.com/GTMichelli-Dev/camera-capture-service) | Ticket images |
| QB Sync | [qb-sync-service](https://github.com/GTMichelli-Dev/qb-sync-service) | QuickBooks export |
| RFID Reader | [`RfidReaderService/`](RfidReaderService/README.md) | Prox card reads (RS-232) — not yet split into its own repo |

---

## Development

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Git

### Run Locally

```bash
git clone https://github.com/GTMichelli-Dev/foundation.git
cd foundation/web/Foundation.Web
dotnet run
```

Open `http://localhost:5110` in your browser.

Enable **Demo Mode** in Setup to use the built-in scale simulator.

---

## License

Copyright 2026 Michelli Weighing & Measurement. All rights reserved.
