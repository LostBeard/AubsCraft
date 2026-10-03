#!/usr/bin/env bash
# Rehearsal: provision-vm-like.sh steps 6-7 (zed-owned leftovers, start the panel as zed).
sudo -u zed mkdir -p -m 700 /opt/minecraft/server/plugins-backup
sudo -u zed cp /opt/minecraft/server/plugins/ViaVersion*.jar /opt/minecraft/server/plugins-backup/ViaVersion.jar.bak
sudo -u zed chmod 600 /opt/minecraft/server/plugins-backup/ViaVersion.jar.bak
systemctl restart aubscraft_admin
for i in $(seq 1 60); do curl -s -o /dev/null http://127.0.0.1:5080/api/public/status && break; sleep 1; done
stat -c '%U:%G %a %n' /opt/minecraft/server/plugins-backup
bash /mnt/d/users/tj/Projects/AubsCraft/server-setup/rehearsal/state.sh
