#!/usr/bin/env bash
systemctl is-active minecraft@spooky; systemctl is-enabled minecraft@spooky
cat /opt/minecraft/servers/spooky/aubscraft.env
ss -ltnup | grep -E ':(25567|25577|24456) ' | awk '{print $1, $5}'
stat -c '%U:%G %a %n' /opt/minecraft/servers/spooky /opt/minecraft/servers/spooky/world/level.dat
grep -A3 '^\[servers\]' /opt/minecraft/velocity/velocity.toml
echo "errors: $(grep -E '/ERROR\]' /opt/minecraft/servers/spooky/logs/latest.log | grep -vcE 'THIS IS NOT A BUG|tc_freeze|tc_unfreeze')"
