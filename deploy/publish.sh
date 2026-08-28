#!/bin/bash
set -euo pipefail

# Publish Foundation for Debian server (linux-x64)
# Output goes to deploy/out/

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$SCRIPT_DIR/out"

# A publish whose model has no matching migration is fatal in production:
# Program.cs runs Migrate() at startup, EF aborts with
# PendingModelChangesWarning, and systemd restart-loops the site. Catch it here,
# before there is a tarball to deploy. Runs before the clean so a failed check
# leaves the previous good output intact.
echo "==> Checking migrations match the model..."
if [[ "${SKIP_MODEL_CHECK:-0}" == "1" ]]; then
  echo "  Skipped (SKIP_MODEL_CHECK=1)."
elif ! dotnet ef --version > /dev/null 2>&1; then
  echo "ERROR: dotnet-ef is not installed, so the check cannot run."
  echo "       Install:  dotnet tool install --global dotnet-ef"
  echo "       Bypass:   SKIP_MODEL_CHECK=1 bash deploy/publish.sh"
  exit 1
elif ! CHECK_OUT="$(dotnet ef migrations has-pending-model-changes \
       --project "$ROOT_DIR/web/Foundation.Web/Foundation.Web.csproj" \
       --context ScaleDbContext 2>&1)"; then
  echo ""
  echo "$CHECK_OUT" | tail -6
  echo ""
  echo "ERROR: Refusing to publish."
  echo "       If the model has pending changes, add the migration first:"
  echo "         cd web/Foundation.Web && dotnet ef migrations add <Name>"
  echo "       Publishing without it crashes every site at startup."
  exit 1
else
  echo "  Migrations match the model."
fi

echo "==> Cleaning previous publish..."
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/foundation"

echo "==> Publishing Foundation.Web (linux-x64, self-contained)..."
dotnet publish "$ROOT_DIR/web/Foundation.Web/Foundation.Web.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o "$OUT_DIR/foundation" \
  -p:PublishSingleFile=false

echo "==> Copying service files..."
cp "$SCRIPT_DIR/foundation.service" "$OUT_DIR/"
cp "$SCRIPT_DIR/install.sh" "$OUT_DIR/"

echo "==> Creating deploy tarball..."
cd "$OUT_DIR"
tar -czf "$SCRIPT_DIR/foundation-deploy.tar.gz" .

echo ""
echo "=========================================="
echo "  Publish complete!"
echo "=========================================="
echo "  Tarball: deploy/foundation-deploy.tar.gz"
echo "  Web App: deploy/out/foundation/"
echo ""
echo "  Deploy with:"
echo "    bash deploy/deploy.sh admin@<server> --domain your.domain.com --email you@email.com"
echo ""
echo "  To rebuild the database (WARNING: deletes all data):"
echo "    bash deploy/deploy.sh admin@<server> --domain your.domain.com --email you@email.com --rebuild-db"
echo "=========================================="
