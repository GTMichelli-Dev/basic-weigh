# Pi git auth — Michelli GitHub App installation tokens

Every Raspberry Pi that needs to `git clone` or `git pull` from a private
GTMichelli-Dev repo (`pi-network-setup`, `camera-capture-service`,
`qb-sync-service`) authenticates through **one GitHub App** installed on the
org. No personal access tokens, no SSH keys, no per-Pi GitHub accounts.

`foundation`, `web-print-service`, and `scale-reader-service` are public and
clone with no credentials at all — worth knowing, because it makes them
useless for testing whether any of this works.

## The App

| Field | Value |
|---|---|
| Name | michelli-fleet |
| Owned by | @GTMichelli-Dev (org-owned) |
| App ID | `4260960` |
| Client ID | `Iv23livFZQOXhMbgSTed` *(preferred JWT issuer per GitHub)* |
| Installation ID | `145563826` |
| Permissions | Contents: Read-only |
| PEM | downloaded once when the App was created (kept off-repo) |

## How it works

The App has a private key (`.pem` file). On each Pi:

- The `.pem` lives at `/etc/michelli/github-app.pem` (mode `0644` — see
  "Why so loose" below).
- A token-mint helper (`/usr/local/bin/michelli-github-app-token`) signs a
  short-lived JWT with the PEM (using the Client ID as the `iss` claim),
  swaps it for a 1-hour **installation access token**, and caches the token
  to `/tmp/michelli-gh-token-$UID`.
- A git credential helper (`/usr/local/bin/git-credential-michelli`) is
  registered for `https://github.com/GTMichelli-Dev/*` in `/etc/gitconfig`.
  Git calls it before every operation that needs auth; it returns
  `username=x-access-token` plus the freshly-minted token.

End result: plain `git clone https://github.com/GTMichelli-Dev/<anything>.git`
and `git pull` work silently from any user on the Pi. No PAT, no env vars, no
SSH config, no group memberships to chase.

### Why so loose on the PEM perms

`/etc/michelli/github-app.pem` is mode `0644` — readable by any local user on
the box. On a single-user scale-house or kiosk Pi the security benefit of
tightening it is nil: anyone with shell access already has `sudo`, which can
`cat` the PEM regardless of group. Group-restricted perms also interact badly
with Pi Connect's web shell, where sessions are sticky and don't reliably pick
up new group membership without a full browser-tab restart — real time lost
per Pi, for nothing.

On a multi-tenant Linux host this would be a downgrade and the group
mechanism would be worth having back. The Michelli fleet doesn't have any of
those.

## What you need before bootstrapping a Pi

- **The PEM** — the `.pem` file downloaded when the App was created. Keep it
  on your laptop or in a password manager, and paste its contents into the Pi
  during the bootstrap below. It is never committed to a repo: `foundation`
  is public, so a PEM committed there would be published to the world.
- **The Installation ID** — `145563826` for the current GTMichelli-Dev
  installation (visible in the URL
  `https://github.com/organizations/GTMichelli-Dev/settings/installations/145563826`).
  If you install the App on additional repos later, the same Installation ID
  covers them, as long as the new repos are checked into the existing
  installation.

The App ID and Client ID are already baked into
[`scripts/setup-pi-github-app.sh`](../scripts/setup-pi-github-app.sh) — you
don't pass them.

## Bootstrap path A — Pi Connect web shell (single Pi)

The Pi Connect web shell mangles multi-line bracketed pastes (the `^[[201~`
end marker gets appended to the last line), so this path is structured as
single-line commands plus a `nano` step for the multi-line PEM. Run each step
in order. Each one is a single line, safe to copy-paste end to end.

**Step 1 — install deps, create the config dir.**

```bash
sudo apt-get update -y && sudo apt-get install -y git curl jq openssl && sudo install -d -m 0755 /etc/michelli
```

**Step 2 — get the helper scripts.** `foundation` is public, so the Pi can
clone it before it can authenticate to anything — no nano-pasting of helper
scripts needed. Sparse checkout keeps it to the `scripts/` folder:

```bash
git clone --filter=blob:none --sparse https://github.com/GTMichelli-Dev/foundation.git ~/foundation && git -C ~/foundation sparse-checkout set scripts
```

**Step 3 — paste the PEM.** Open nano and paste the whole
`-----BEGIN ... -----END ...` block, every line. Save with `Ctrl+O` + Enter,
exit with `Ctrl+X`:

```bash
sudo nano /etc/michelli/github-app.pem
```

Then set its mode:

```bash
sudo chmod 0644 /etc/michelli/github-app.pem
```

**Step 4 — run the installer.** It writes the conf, installs both helpers,
registers the credential helper in `/etc/gitconfig`, and smoke-tests with a
real token mint. The PEM is already in place from step 3, so no `--pem` here:

```bash
sudo bash ~/foundation/scripts/setup-pi-github-app.sh --install-id 145563826
```

**Step 5 — smoke test:**

```bash
git ls-remote https://github.com/GTMichelli-Dev/pi-network-setup.git HEAD
```

Should print a SHA and `HEAD` with no prompt. Test against a **private** repo
— `foundation` is public and answers without the credential helper being
involved at all, so it would succeed even on a Pi where this all failed. If
it prompts for a username, see [Troubleshooting](#troubleshooting).

You can now `git clone` any GTMichelli-Dev repo on this Pi.

## Bootstrap path B — scp from your laptop (fleet rollout)

When you have shell and scp access from a workstation that has this repo and
the PEM already on disk:

```bash
PI=admin@pi-hostname.local
PEM=/path/to/michelli-app.pem
INSTALL_ID=145563826

scp scripts/setup-pi-github-app.sh \
    scripts/michelli-github-app-token.sh \
    scripts/git-credential-michelli.sh \
    "$PEM" "$PI:/tmp/"

ssh "$PI" "sudo bash /tmp/setup-pi-github-app.sh \
    --install-id $INSTALL_ID \
    --pem /tmp/$(basename "$PEM")"

ssh "$PI" "shred -u /tmp/$(basename "$PEM") && rm -f /tmp/setup-pi-github-app.sh /tmp/michelli-github-app-token.sh /tmp/git-credential-michelli.sh"
```

Same end state as path A, and no nano step — scp doesn't mangle the PEM the
way a browser terminal does.

## Re-runs and ongoing ops

Once `foundation` is on the Pi:

```bash
sudo bash ~/foundation/scripts/setup-pi-github-app.sh                      # refresh helpers from repo
sudo bash ~/foundation/scripts/setup-pi-github-app.sh --pem /tmp/new.pem   # rotate PEM
sudo bash ~/foundation/scripts/setup-pi-github-app.sh --install-id <N>     # re-install or repoint installation
```

Each run is idempotent. Re-running with no flags refreshes the helper scripts
(`scripts/michelli-github-app-token.sh` / `scripts/git-credential-michelli.sh`)
from the current checkout — useful after a `git pull` that updates them.

## Operational notes

- **Token lifetime is 1 hour.** The cache file `/tmp/michelli-gh-token-$UID`
  holds the token until 60s before expiry, then `michelli-github-app-token`
  mints a new one transparently.
- **Pis need internet to mint tokens.** A fully offline Pi can't clone or
  pull regardless of auth model — same as PATs and deploy keys.
- **PEM rotation.** GitHub Apps let you generate a new private key without
  invalidating the old. Generate a new PEM in the App settings, distribute it
  to the fleet (`--pem` flag), then revoke the old one. No Installation ID
  change needed.
- **Off-boarding a stolen Pi.** If you can't recover it, generate a new App
  PEM, revoke the old, and re-bootstrap the fleet. Any cached token on the
  lost Pi keeps working for up to its remaining ~1h validity, but no new ones
  can be minted.
- **Audit.** Every git operation through the App shows up in the org's audit
  log under the App's identity — cleaner than a PAT model where every clone
  looks like a user action.

## One-time: creating the App (org owner)

Already done for GTMichelli-Dev; recorded here for rebuilds. Org **Settings**
→ **Developer settings** → **GitHub Apps** → **New GitHub App**, with
**Contents: Read-only** and nothing else, **Webhook → Active** unchecked, and
installable **only on this account**. Create it, note the App ID and Client
ID, **Generate a private key** (that is the `.pem` — the only secret in this
scheme), then **Install App** on the org across all repositories. The number
ending the resulting URL is the Installation ID.

Bake the App ID and Client ID into the `CLIENT_ID_DEFAULT` and
`APP_ID_DEFAULT` lines near the top of `scripts/setup-pi-github-app.sh` and
commit, so per-Pi bootstraps only need the Installation ID and the PEM.

## Troubleshooting

**`git ls-remote` prompts for a username** — the credential helper isn't
being called, or is failing silently. Run the minter directly to see the
error:

```bash
michelli-github-app-token
```

Common failures:

- `cannot read /etc/michelli/github-app.conf` — the conf isn't readable.
  Check `ls -l /etc/michelli/`; it should be `0644`.
- `cannot read /etc/michelli/github-app.pem` — same fix, mode `0644`.
- `token mint failed: {"message": "Bad credentials" ...}` — the PEM doesn't
  match the App ID / Client ID. Confirm `CLIENT_ID` in
  `/etc/michelli/github-app.conf` matches the App's settings page. A PEM
  pasted through a browser terminal can also arrive corrupted — verify it
  with `sudo openssl rsa -in /etc/michelli/github-app.pem -noout -check`.
- `token mint failed: {"message": "Not Found" ...}` — the Installation ID is
  wrong, or the App isn't installed on the repo. Check
  `https://github.com/organizations/GTMichelli-Dev/settings/installations/145563826`.

**`ls-remote` succeeds but proves nothing** — check you didn't test against
`foundation`, `web-print-service`, or `scale-reader-service`. They are public
and answer without any credential helper involved.

**`/etc/gitconfig` permission denied** — the system gitconfig was created
with too-tight perms by an earlier bootstrap. Fix:

```bash
sudo chmod 0644 /etc/gitconfig
```

## Files this drops onto a Pi

| Path | Mode | Purpose |
|---|---|---|
| `/etc/michelli/` | 0755 | Config directory |
| `/etc/michelli/github-app.pem` | 0644 | App private key (single-user-Pi tradeoff — see "Why so loose") |
| `/etc/michelli/github-app.conf` | 0644 | `CLIENT_ID=` / `APP_ID=` / `INSTALL_ID=` (public IDs only, not secret) |
| `/usr/local/bin/michelli-github-app-token` | 0755 | Token minter — signs the JWT, exchanges it for an installation token, caches it |
| `/usr/local/bin/git-credential-michelli` | 0755 | Git credential helper — calls the minter, formats output for git |
| `/etc/gitconfig` | 0644 | Registers the helper for `https://github.com/GTMichelli-Dev/*` (system-wide) |
| `/tmp/michelli-gh-token-$UID` | 0600 | Per-user token cache (regenerated as needed; ephemeral) |
