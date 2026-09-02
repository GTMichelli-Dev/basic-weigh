#!/bin/bash
set -euo pipefail

# Install Foundation on a Debian server
# Run as root: sudo bash install.sh
#
# Expects to be run from the extracted tarball directory, or
# pass the tarball path: sudo bash install.sh /tmp/foundation-deploy.tar.gz
#
# Environment variables (optional):
#   DOMAIN=scale.example.com   — enables Let's Encrypt SSL
#   EMAIL=admin@example.com    — required for Let's Encrypt
#   PORT=5110                  — app listen port (default 5110)
#   REBUILD_DB=1               — delete and recreate the database
#   RESET_REPORTS=1            — take the shipped ticket templates instead of
#                                keeping the site's saved .repx files

APP_DIR="/opt/foundation"
SERVICE_USER="admin"
DOMAIN="${DOMAIN:-}"
# Strip protocol prefix from domain if present
DOMAIN="${DOMAIN#https://}"
DOMAIN="${DOMAIN#http://}"
DOMAIN="${DOMAIN%/}"
EMAIL="${EMAIL:-}"
APP_PORT="${PORT:-5110}"
REBUILD_DB="${REBUILD_DB:-0}"
RESET_REPORTS="${RESET_REPORTS:-0}"

#--------------------------------------------------
# 0. If a tarball was passed, extract it first
#--------------------------------------------------
if [[ "${1:-}" == *.tar.gz ]] && [[ -f "${1:-}" ]]; then
  echo "==> Extracting tarball..."
  WORK_DIR="$(mktemp -d)"
  tar -xzf "$1" -C "$WORK_DIR"
  cd "$WORK_DIR"
fi

#--------------------------------------------------
# 1. Install required packages
#--------------------------------------------------
echo "==> Installing required packages..."
apt-get update -qq
apt-get install -y -qq nginx rsync curl openssl libfontconfig1 fonts-dejavu-core > /dev/null
echo "  nginx, rsync, curl, openssl, libfontconfig1 installed."

# Install certbot if domain is specified
if [[ -n "$DOMAIN" ]]; then
  apt-get install -y -qq certbot python3-certbot-nginx > /dev/null
  echo "  certbot installed."
fi

# Open firewall ports
echo "==> Configuring firewall..."
if command -v ufw &>/dev/null && ufw status | grep -q "active"; then
  ufw allow 22/tcp > /dev/null
  ufw allow 80/tcp > /dev/null
  ufw allow 443/tcp > /dev/null
  echo "  Firewall: ufw — ports 22, 80, 443 opened."
fi

# Always add iptables rules to ensure ports are open
if command -v iptables &>/dev/null; then
  # Only add if not already present
  iptables -C INPUT -p tcp --dport 80 -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport 80 -j ACCEPT
  iptables -C INPUT -p tcp --dport 443 -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport 443 -j ACCEPT
  echo "  Firewall: iptables — ports 80 and 443 opened."

  # Persist iptables rules across reboots
  if command -v netfilter-persistent &>/dev/null; then
    netfilter-persistent save 2>/dev/null || true
  elif command -v iptables-save &>/dev/null; then
    mkdir -p /etc/iptables
    iptables-save > /etc/iptables/rules.v4 2>/dev/null || true
  fi
  echo "  Firewall: rules saved for reboot persistence."
fi

# Create service user if it doesn't exist
if ! id "$SERVICE_USER" &>/dev/null; then
  useradd -r -m -s /bin/bash "$SERVICE_USER"
  echo "  Created user: $SERVICE_USER"
fi

#--------------------------------------------------
# 2. Stop existing service
#--------------------------------------------------
echo "==> Stopping service (if running)..."
systemctl stop foundation 2>/dev/null || true

# Migrate a pre-rename "Basic Weigh" install: same app, old name.
# Moves /opt/basicweigh (including the database and captured ticket
# images) to $APP_DIR and retires the old systemd unit.
if [[ -d /opt/basicweigh && ! -d "$APP_DIR" ]]; then
  echo "==> Migrating existing Basic Weigh install to Foundation..."
  systemctl stop basicweigh 2>/dev/null || true
  systemctl disable basicweigh 2>/dev/null || true
  rm -f /etc/systemd/system/basicweigh.service
  systemctl daemon-reload
  mv /opt/basicweigh "$APP_DIR"
  if [[ -f "$APP_DIR/BasicWeigh.db" && ! -f "$APP_DIR/Foundation.db" ]]; then
    mv "$APP_DIR/BasicWeigh.db" "$APP_DIR/Foundation.db"
    echo "  Database renamed: BasicWeigh.db -> Foundation.db"
  fi
  echo "  Migration complete."
fi

#--------------------------------------------------
# 3. Install Foundation web app
#--------------------------------------------------
echo "==> Installing Foundation.Web to $APP_DIR..."
mkdir -p "$APP_DIR"

# Preserve database and reports if they exist (unless rebuild requested).
# The -wal/-shm sidecars ride along: if the service didn't checkpoint
# cleanly, the newest writes live in the WAL — restoring the .db alone
# would silently lose them.
if [[ "$REBUILD_DB" == "1" ]]; then
  echo "  --rebuild-db: Database will be recreated from scratch."
  rm -f "$APP_DIR"/Foundation.db* /tmp/Foundation.db.bak /tmp/Foundation.db-wal.bak /tmp/Foundation.db-shm.bak
else
  if [[ -f "$APP_DIR/Foundation.db" ]]; then
    cp "$APP_DIR/Foundation.db" /tmp/Foundation.db.bak
    rm -f /tmp/Foundation.db-wal.bak /tmp/Foundation.db-shm.bak
    if [[ -f "$APP_DIR/Foundation.db-wal" ]]; then cp "$APP_DIR/Foundation.db-wal" /tmp/Foundation.db-wal.bak; fi
    if [[ -f "$APP_DIR/Foundation.db-shm" ]]; then cp "$APP_DIR/Foundation.db-shm" /tmp/Foundation.db-shm.bak; fi
    echo "  Database backed up to /tmp/Foundation.db.bak"
  fi
fi
# Saved ticket templates are per-site: an operator can edit them in the Report
# Designer, so a deploy must not silently overwrite them. The build ships no
# Reports/ directory, so the rsync --delete below would wipe the whole folder —
# this backup is the only reason a site keeps its templates across a deploy.
#
# RESET_REPORTS=1 opts out: the folder goes, and the app re-seeds
# TicketReport.repx / KioskTicketReport.repx from the built-in report classes on
# first use. That is how a fix to the stock template (page width, say) reaches a
# site that has ever opened the designer. It also discards any site-only .repx,
# since there is no backup to restore them from.
#
# The stale backup is cleared first. "cp -r src dest" NESTS when dest already
# exists, so without this the second deploy produced /tmp/Reports.bak/Reports/
# alongside the first deploy's files, and the restore below copied those
# first-deploy templates back over the site's current ones — every time.
rm -rf /tmp/Reports.bak
if [[ "$RESET_REPORTS" == "1" ]]; then
  echo "  RESET_REPORTS=1 — shipped ticket templates will be used."
elif [[ -d "$APP_DIR/Reports" ]]; then
  cp -r "$APP_DIR/Reports" /tmp/Reports.bak
  echo "  Reports backed up to /tmp/Reports.bak/"
fi

# Copy new files. Exclude runtime state from the --delete sweep — these are
# written by the running app, not produced by the build, so without an
# exclusion --delete wipes them on every deploy:
#   wwwroot/images/tickets/  captured ticket photos and driver signatures
#   App_Data/                Data Protection key ring, which decrypts the saved
#                            SMTP password (Setup → Email). Lose it and the
#                            operator has to retype the password after every
#                            update.
rsync -a --delete \
  --exclude='wwwroot/images/tickets/' \
  --exclude='App_Data/' \
  foundation/ "$APP_DIR/"

# Make sure the runtime directories exist for fresh installs and that the
# service user can write to them.
mkdir -p "$APP_DIR/wwwroot/images/tickets"
mkdir -p "$APP_DIR/App_Data"

# Restore database and reports
if [[ "$REBUILD_DB" != "1" ]] && [[ -f /tmp/Foundation.db.bak ]]; then
  cp /tmp/Foundation.db.bak "$APP_DIR/Foundation.db"
  if [[ -f /tmp/Foundation.db-wal.bak ]]; then cp /tmp/Foundation.db-wal.bak "$APP_DIR/Foundation.db-wal"; fi
  if [[ -f /tmp/Foundation.db-shm.bak ]]; then cp /tmp/Foundation.db-shm.bak "$APP_DIR/Foundation.db-shm"; fi
  echo "  Database restored."
fi
if [[ -d /tmp/Reports.bak ]]; then
  cp -r /tmp/Reports.bak/* "$APP_DIR/Reports/" 2>/dev/null || true
  echo "  Reports restored."
fi

chmod +x "$APP_DIR/Foundation.Web"
chown -R "$SERVICE_USER:$SERVICE_USER" "$APP_DIR"

#--------------------------------------------------
# 4. Install systemd service
#--------------------------------------------------
echo "==> Installing systemd service..."
cp foundation.service /etc/systemd/system/foundation.service
systemctl daemon-reload

#--------------------------------------------------
# 5. Configure Nginx reverse proxy
#--------------------------------------------------
echo "==> Configuring Nginx reverse proxy..."

if [[ -n "$DOMAIN" ]]; then
  #--- Let's Encrypt with real domain ---
  echo "  Domain: $DOMAIN"

  # Initial Nginx config (HTTP only, for certbot challenge)
  cat > /etc/nginx/sites-available/default <<NGINX
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name $DOMAIN;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://\$host\$request_uri;
    }
}
NGINX

  nginx -t && systemctl reload nginx

  # Get Let's Encrypt certificate
  if [[ ! -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ]]; then
    echo "  Requesting Let's Encrypt certificate..."
    CERTBOT_OPTS="--nginx -d $DOMAIN --non-interactive --agree-tos"
    if [[ -n "$EMAIL" ]]; then
      CERTBOT_OPTS="$CERTBOT_OPTS --email $EMAIL"
    else
      CERTBOT_OPTS="$CERTBOT_OPTS --register-unsafely-without-email"
    fi
    certbot $CERTBOT_OPTS
    echo "  SSL certificate obtained."
  else
    echo "  Let's Encrypt certificate already exists."
  fi

  # Full Nginx config with SSL
  cat > /etc/nginx/sites-available/default <<NGINX
# HTTP — redirect to HTTPS
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name $DOMAIN;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://\$host\$request_uri;
    }
}

# HTTPS — reverse proxy to Foundation
server {
    listen 443 ssl default_server;
    listen [::]:443 ssl default_server;
    server_name $DOMAIN;

    ssl_certificate     /etc/letsencrypt/live/$DOMAIN/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;

    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
    add_header X-Content-Type-Options nosniff always;

    location / {
        proxy_pass         http://127.0.0.1:$APP_PORT;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade \$http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host \$host;
        proxy_set_header   X-Real-IP \$remote_addr;
        proxy_set_header   X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;
        proxy_cache_bypass \$http_upgrade;

        # Buffering for large responses (Report Designer)
        proxy_buffering on;
        proxy_buffer_size 128k;
        proxy_buffers 8 256k;
        proxy_busy_buffers_size 256k;
        client_max_body_size 50m;

        # WebSocket support (SignalR)
        proxy_read_timeout 86400;
    }
}
NGINX

  # Enable auto-renewal timer
  systemctl enable certbot.timer 2>/dev/null || true
  echo "  Let's Encrypt auto-renewal enabled."

else
  #--- Self-signed cert (no domain specified) ---
  # Create self-signed SSL cert if none exists
    if [[ ! -f /etc/nginx/ssl/nginx.crt ]]; then
      echo "  Generating self-signed SSL certificate..."
      mkdir -p /etc/nginx/ssl
      openssl req -x509 -nodes -days 3650 -newkey rsa:2048 \
        -keyout /etc/nginx/ssl/nginx.key \
        -out /etc/nginx/ssl/nginx.crt \
        -subj "/CN=foundation/O=Foundation/C=US" 2>/dev/null
      echo "  SSL certificate created (self-signed)."
    fi

    cat > /etc/nginx/sites-available/default <<NGINX
# HTTP — redirect to HTTPS
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name localhost;
    return 301 https://\$host\$request_uri;
}

# HTTPS — reverse proxy to Foundation
server {
    listen 443 ssl default_server;
    listen [::]:443 ssl default_server;
    server_name localhost;

    ssl_certificate     /etc/nginx/ssl/nginx.crt;
    ssl_certificate_key /etc/nginx/ssl/nginx.key;
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;

    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
    add_header X-Content-Type-Options nosniff always;

    location / {
        proxy_pass         http://127.0.0.1:$APP_PORT;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade \$http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host \$host;
        proxy_set_header   X-Real-IP \$remote_addr;
        proxy_set_header   X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;
        proxy_cache_bypass \$http_upgrade;

        # Buffering for large responses (Report Designer)
        proxy_buffering on;
        proxy_buffer_size 128k;
        proxy_buffers 8 256k;
        proxy_busy_buffers_size 256k;
        client_max_body_size 50m;

        # WebSocket support (SignalR)
        proxy_read_timeout 86400;
    }
}
NGINX
fi

nginx -t && systemctl reload nginx
echo "  Nginx configured."

#--------------------------------------------------
# 6. Enable and start service
#--------------------------------------------------
echo "==> Enabling and starting Foundation..."
systemctl enable foundation
systemctl start foundation
echo "  Foundation: $(systemctl is-active foundation)"

#--------------------------------------------------
# 7. Done
#--------------------------------------------------
echo ""
echo "=========================================="
echo "  Installation complete!"
echo "=========================================="
echo "  Web App: $APP_DIR"
echo "  Service: systemctl status foundation"
echo "  Logs:    journalctl -u foundation -f"
if [[ -n "$DOMAIN" ]]; then
echo "  URL:     https://$DOMAIN"
echo "  SSL:     Let's Encrypt (auto-renews)"
else
echo "  URL:     https://<server-ip>"
echo "  SSL:     Self-signed (browser warning)"
echo ""
echo "  For Let's Encrypt, reinstall with:"
echo "    DOMAIN=scale.example.com EMAIL=you@example.com sudo -E bash install.sh"
fi
echo "=========================================="
