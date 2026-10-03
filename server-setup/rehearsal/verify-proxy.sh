#!/usr/bin/env bash
echo "proxy log errors: $(grep -cE ' ERROR\]|Exception' /opt/minecraft/velocity/logs/latest.log)"
grep -E "Loaded plugin|Voice chat proxy server started|Started Geyser on UDP|Listening on" /opt/minecraft/velocity/logs/latest.log | cut -c1-140
ls /opt/minecraft/server/plugins | grep -E "disabled"
grep -E "Voice chat server started" /opt/minecraft/server/logs/latest.log | tail -1 | cut -c1-120
grep -E "^\[servers\]" -A3 /opt/minecraft/velocity/velocity.toml
