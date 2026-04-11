#!/bin/bash
# Run this ONCE on the aubscraft VM with: sudo bash /srv/aubscraft/setup-service.sh
# Sets up the systemd service and sudoers entry for deploy automation

set -e

echo "[1/4] Installing systemd service..."
cp /srv/aubscraft/aubscraft_admin.service /etc/systemd/system/aubscraft_admin.service
systemctl daemon-reload

echo "[2/4] Enabling service (auto-start on boot)..."
systemctl enable aubscraft_admin

echo "[3/4] Adding sudoers entry for zed..."
cat > /etc/sudoers.d/aubscraft_admin << 'EOF'
zed ALL=(ALL) NOPASSWD: /usr/bin/systemctl restart aubscraft_admin, /usr/bin/systemctl stop aubscraft_admin, /usr/bin/systemctl start aubscraft_admin, /usr/bin/systemctl status aubscraft_admin
EOF
chmod 440 /etc/sudoers.d/aubscraft_admin

echo "[4/4] Starting service..."
chmod +x /srv/aubscraft/AubsCraft.Admin.Server
systemctl start aubscraft_admin
systemctl status aubscraft_admin --no-pager -l

echo ""
echo "Done! Service is running. Deploy script can now manage it via SSH."
