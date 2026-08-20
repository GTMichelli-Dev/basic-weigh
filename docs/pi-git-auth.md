# Pi git auth — Michelli GitHub App installation tokens

Every Raspberry Pi that needs to `git clone` or `git pull` a private
GTMichelli-Dev repo authenticates through **one GitHub App** installed on the
org. No personal access tokens, no SSH keys, no per-Pi GitHub accounts.

Which repos actually need it: **pi-network-setup**, **camera-capture-service**,
and **qb-sync-service** are private. **foundation**, **web-print-service**, and
**scale-reader-service** are public and clone with no credentials at all —
worth knowing, because it makes them useless for testing whether this setup
works.

## How it works

The App has a private key (a `.pem` file). On each Pi:

- The `.pem` lives at `/etc/michelli/github-app.pem`.
- A token-mint helper (`/usr/local/bin/michelli-github-app-token`) signs a
  short-lived JWT with the PEM, swaps it for a 1-hour **installation access
  token**, and caches the token in `/tmp`.
- A git credential helper (`/usr/local/bin/git-credential-michelli`) is
  registered for `https://github.com/GTMichelli-Dev/*` in `/etc/gitconfig`.
  Git calls it before every operation that needs auth; it returns
  `username=x-access-token` plus the freshly-minted token.

End result: plain `git clone https://github.com/GTMichelli-Dev/<anything>.git`
and `git pull` work silently from any user on the Pi.

The helper scripts live in [`scripts/`](../scripts/):
`michelli-github-app-token.sh`, `git-credential-michelli.sh`, and the
installer `setup-pi-github-app.sh`.

## One-time: create the GitHub App (org owner)

1. github.com → the **GTMichelli-Dev** org → **Settings** → **Developer
   settings** → **GitHub Apps** → **New GitHub App**.
2. Fill in:
   - **Name**: `Michelli-Fleet` (anything unique)
   - **Homepage URL**: any URL (e.g. the org page) — required but unused
   - **Webhook**: uncheck **Active** (no webhook needed)
   - **Permissions** → Repository permissions → **Contents: Read-only**
     (leave everything else at No access)
   - **Where can this GitHub App be installed?**: *Only on this account*
3. **Create GitHub App**. On the App page, note the **App ID** and
   **Client ID**.
4. **Generate a private key** — this downloads the `.pem`. Keep it in a
   password manager or on your workstation; it is the only secret in this
   scheme and it is never committed to a repo.
5. **Install App** (left sidebar) → install on **GTMichelli-Dev** → select
   **All repositories** (or just the repos the Pis need). After installing,
   the browser URL looks like
   `https://github.com/organizations/GTMichelli-Dev/settings/installations/<NUMBER>`
   — that number is the **Installation ID**.
6. Bake the App ID and Client ID into
   [`scripts/setup-pi-github-app.sh`](../scripts/setup-pi-github-app.sh)
   (the `CLIENT_ID_DEFAULT` / `APP_ID_DEFAULT` lines near the top) and
   commit, so per-Pi bootstraps only need the Installation ID and the PEM.

If the App is ever compromised or the PEM lost, revoke/regenerate the key on
the App page and re-run the bootstrap on each Pi with the new `--pem`.

## Per-Pi bootstrap

Two ways in, depending on how you can reach the Pi. Both end at the same
place; pick by what you have, not by which looks more official.

| How you reach the Pi | Use |
|---|---|
| Raspberry Pi Connect web shell (terminal only, no file transfer) | [Pasted bootstrap](#pasted-bootstrap-pi-connect) |
| SSH from a workstation that has this repo and the PEM on disk | [scp bootstrap](#scp-bootstrap) |

Either way you need two things in front of you: the **Installation ID** (the
number at the end of the App's install URL) and the App's **`.pem`** private
key.

### Pasted bootstrap (Pi Connect)

Pi Connect gives you a terminal and nothing else — no scp, no drag-and-drop.
So the key arrives the only way a browser terminal allows: pasted.

Open the Pi at [connect.raspberrypi.com/devices](https://connect.raspberrypi.com/devices)
→ **Shell**, then paste:

```bash
curl -fsSL -o /tmp/gh-auth.sh https://raw.githubusercontent.com/GTMichelli-Dev/foundation/main/scripts/pi-connect-github-auth.sh
bash /tmp/gh-auth.sh </dev/tty
```

It installs git if the Pi lacks it, sparse-clones this repo's `scripts/`
folder (foundation is public — that is how the Pi gets the tooling *before*
it can authenticate to anything), then prompts for the Installation ID and
the key. Paste the whole `.pem` including the BEGIN and END lines; capture
stops at END by itself, so there is no terminator to remember. Anything you
paste before the BEGIN line is ignored, and a key copied on Windows with CRLF
endings is handled.

The pasted key is written mode 0600, checked with `openssl` before use, and
shredded from `/tmp` when the script exits — including if it fails partway.

The `</dev/tty` is not decoration: without it the prompts would consume
whatever else is still sitting in the terminal's paste buffer instead of
waiting for you.

> The key does travel through your browser and the Connect relay. That is the
> same exposure as pasting it into any remote shell. If a site's threat model
> rules that out, use the scp path over SSH on the local network instead.

### scp bootstrap

From a workstation that has this repo and the PEM on disk:

```bash
PI=admin@pi-hostname.local
PEM=/path/to/michelli-app.pem
INSTALL_ID=<number from the install URL>

scp scripts/setup-pi-github-app.sh \
    scripts/michelli-github-app-token.sh \
    scripts/git-credential-michelli.sh \
    "$PEM" "$PI:/tmp/"

ssh "$PI" "sudo bash /tmp/setup-pi-github-app.sh \
    --install-id $INSTALL_ID \
    --pem /tmp/$(basename "$PEM") && \
    shred -u /tmp/$(basename "$PEM")"
```

(If the defaults were not baked into the script yet, add
`--client-id <ClientID>`.)

### What the installer does

Either path ends in `setup-pi-github-app.sh`, which apt-installs `curl`/`jq`
if missing, installs the two helpers, writes `/etc/michelli/github-app.conf`
and the PEM, registers the credential helper in `/etc/gitconfig`, then
smoke-tests with a real token mint and a `git ls-remote` against the private
pi-network-setup repo.

It is idempotent. Re-run it to pick up newer helper scripts; the existing
conf and PEM are reused unless you pass `--pem` again.

### Verify

Should print a SHA with no prompt:

```bash
git ls-remote https://github.com/GTMichelli-Dev/pi-network-setup.git HEAD
```

Use a **private** repo here. This check used to point at `foundation`, which
has since gone public — and a public repo answers `ls-remote` without any
credential helper involved, so it passed on Pis where the App auth was
completely broken. A private repo is the only thing that proves the helper,
the PEM, and the App's repo selection all work.

If it fails, the usual causes are the App missing **Contents: Read**, or the
installation not covering that repo.

After that, every service installer that clones a GTMichelli-Dev repo — e.g.
the print service's
`git clone https://github.com/GTMichelli-Dev/web-print-service.git` — works
on that Pi with no credentials in the command.

## Security notes

- The conf (App ID / Client ID / Installation ID) is public-ish metadata;
  the **PEM is the secret**. On single-user scale/kiosk Pis it is stored
  mode 0644 — anyone with a shell has sudo anyway. On a shared host,
  tighten it to root + a dedicated group.
- The App only has **Contents: Read** — a leaked installation token can read
  code for up to an hour but can't push, manage settings, or see other orgs.
- Tokens are cached in `/tmp/michelli-gh-token-<uid>` (mode 0600) and expire
  after 1 hour; the helper re-mints automatically.
