#!/usr/bin/env bash
# Rehearsal: what is running, as whom, and the ports in use.
systemctl is-active minecraft velocity aubscraft_admin 2>&1 | paste -sd' '
echo "panel runs as: $(ps -o user= -p "$(pgrep -f /srv/aubscraft/AubsCraft.Admin.Server | head -1)")"
grep -E '^server-port|^server-ip|^online-mode' /opt/minecraft/server/server.properties | paste -sd' '
ls -la /opt/minecraft/backups/*/ 2>/dev/null | tail -n +2
ss -ltnup 2>/dev/null | grep -E ':(25565|25566|19132|24454|24455|25575|25576|5080) ' | awk '{print $1, $5}' | sort -u
