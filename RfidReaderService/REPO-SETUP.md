# Splitting this into its own repo

The other device services (Scale, Camera, Print, QB Sync) are git submodules of
`foundation`, each pointing at its own `GTMichelli-Dev` repo. This service is
currently a plain directory in the monorepo because the GitHub repo does not
exist yet.

**The `.gitmodules` entry is deliberately not committed yet.** A `.gitmodules`
entry whose path is a normally-tracked directory breaks `git submodule` commands
and makes `git status` report the whole folder as modified — so the entry has to
land in the same commit that removes the directory from the index. The exact
block is below, ready to paste when you do that.

## 1. Create the repo

Under the `GTMichelli-Dev` org, create `rfid-reader-service` (private, no
README/licence/gitignore — the first push provides them).

## 2. Push this folder as its history

From a checkout of `foundation`:

```bash
cd RfidReaderService
git init -b main
git add .
git commit -m "RFID Reader Service — RS-232 prox card reader for BasicWeigh"
git remote add origin https://github.com/GTMichelli-Dev/rfid-reader-service.git
git push -u origin main
```

## 3. Convert the monorepo directory into a submodule

Back at the repository root, in one commit:

```bash
git rm -r --cached RfidReaderService
rm -rf RfidReaderService
git submodule add https://github.com/GTMichelli-Dev/rfid-reader-service.git RfidReaderService
git add .gitmodules RfidReaderService
git commit -m "RfidReaderService: convert to submodule"
```

`git submodule add` writes the `.gitmodules` entry itself. If you would rather
write it by hand, this is the block — it belongs alongside the four existing
entries:

```ini
[submodule "RfidReaderService"]
	path = RfidReaderService
	url = https://github.com/GTMichelli-Dev/rfid-reader-service.git
```

## 4. Point the installer at the new repo

`deploy/install.sh` already defaults to
`GITHUB_REPO="GTMichelli-Dev/rfid-reader-service"` and `BRANCH="main"`, so the
documented one-liner starts working the moment the repo exists. Nothing in the
code needs to change.

Note the branch difference: the Scale Reader Service repo uses `master`, so if
you create this one with a `master` default branch instead, update `BRANCH` in
`deploy/install.sh` to match.

## 5. Pi bootstrap

Deploy Pis clone private org repos with the GitHub App credential helper — see
the [pi-git-auth](https://github.com/GTMichelli-Dev/pi-git-auth) repo. No extra setup is needed for this repo
beyond the one-time per-Pi bootstrap that is already required for the others.
