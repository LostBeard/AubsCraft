#!/usr/bin/env bash
# Rehearsal: services enabled and running as on the VM; one stop/start so the world's level.dat exists
# (Minecraft writes it at the first save - on the VM it has existed for months).
set -uo pipefail
wait_mc() { for i in $(seq 1 150); do grep -q "For help, type" /opt/minecraft/server/logs/latest.log 2>/dev/null && return 0; sleep 2; done; return 1; }
systemctl enable --now minecraft aubscraft_admin >/dev/null 2>&1
wait_mc
systemctl stop minecraft
systemctl start minecraft
wait_mc
stat -c '%U:%G %a %n' /opt/minecraft/server/world/level.dat /opt/minecraft/server/world_nether/level.dat /opt/minecraft/server/plugins-backup
systemctl is-active minecraft aubscraft_admin
echo "panel runs as: $(ps -o user= -p "$(pgrep -f /srv/aubscraft/AubsCraft.Admin.Server | head -1)")"
