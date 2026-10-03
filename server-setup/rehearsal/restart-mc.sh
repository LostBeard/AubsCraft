#!/usr/bin/env bash
# Rehearsal: enable both services like the VM and restart Minecraft, waiting for THIS start's "For help".
LOG=/opt/minecraft/server/logs/latest.log
systemctl enable minecraft aubscraft_admin >/dev/null 2>&1
systemctl stop minecraft; rm -f "$LOG"; systemctl start minecraft
for i in $(seq 1 200); do grep -q "For help, type" "$LOG" 2>/dev/null && break; sleep 2; done
stat -c '%U %a %n' /opt/minecraft/server/world/level.dat
ls /opt/minecraft/server/plugins | grep -c jar
