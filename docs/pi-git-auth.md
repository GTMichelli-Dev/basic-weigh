# Pi git auth — moved

This now lives in its own repo:

**https://github.com/GTMichelli-Dev/pi-git-auth**

It moved because it is fleet infrastructure, not part of the web app. Every
service repo needs to point at it, and pointing them all into a monorepo meant
the bootstrap cloned an entire ASP.NET application onto a Pi OS Lite box just
to install a credential helper.

The scripts moved with it — `setup-pi-github-app.sh`,
`michelli-github-app-token.sh`, `git-credential-michelli.sh` and
`pi-connect-github-auth.sh` are no longer in this repo's `scripts/` folder.

## Bootstrapping a Pi

Paste-only (Raspberry Pi Connect's web shell):

```bash
curl -fsSL -o /tmp/gh-auth.sh https://raw.githubusercontent.com/GTMichelli-Dev/pi-git-auth/main/scripts/pi-connect-github-auth.sh
bash /tmp/gh-auth.sh </dev/tty
```

From a release, when the Pi has `curl` but not `git`:

```bash
curl -fsSL -o pga.tar.gz https://github.com/GTMichelli-Dev/pi-git-auth/releases/latest/download/pi-git-auth.tar.gz
mkdir -p /tmp/pga && tar -xzf pga.tar.gz -C /tmp/pga
sudo bash /tmp/pga/setup-pi-github-app.sh --install-id 145563826 --pem /path/to/michelli-app.pem
```

Take the tarball rather than a single script: `setup-pi-github-app.sh` installs
the two helpers from alongside itself and stops if they are not there.

Everything else — how the App works, PEM rotation, the perms tradeoff, and
troubleshooting — is in that repo's
[README](https://github.com/GTMichelli-Dev/pi-git-auth/blob/main/README.md).

This file is kept as a pointer because links to it are in circulation.
