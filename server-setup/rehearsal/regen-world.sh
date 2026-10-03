#!/usr/bin/env bash
# Rehearsal: a normal world like the VM's, saved once so level.dat exists (owner-only 0600, as on the VM).
set -uo pipefail
LOG=/opt/minecraft/server/logs/latest.log
# Wait for THIS run's "For help": the old latest.log still holds the previous run's line until Paper
# rotates it, so it is removed before every start.
start_mc() { rm -f "$LOG"; systemctl start minecraft; for i in $(seq 1 300); do grep -q "For help, type" "$LOG" 2>/dev/null && return 0; sleep 2; done; echo "minecraft did not start"; return 1; }
systemctl stop minecraft
sed -i '/^level-type=/d;/^generator-settings=/d' /opt/minecraft/server/server.properties
rm -rf /opt/minecraft/server/world /opt/minecraft/server/world_nether /opt/minecraft/server/world_the_end
start_mc
systemctl stop minecraft            # the stop saves level.dat
start_mc
echo "errors: $(grep -c ERROR "$LOG")"
stat -c '%U:%G %a %n' /opt/minecraft/server/world/level.dat /opt/minecraft/server/world_nether/level.dat /opt/minecraft/server/plugins-backup
