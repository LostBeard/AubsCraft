#!/usr/bin/env bash
ls -la /opt/minecraft/server/world | head
ls /opt/minecraft/server/logs/
z=$(ls -t /opt/minecraft/server/logs/*.log.gz 2>/dev/null | head -1); echo "previous log: $z"
zcat "$z" | grep -n -iE "No key layers|Saving|Stopping|level.dat|exception|ERROR" | head -20 | cut -c1-200
zcat "$z" | grep -n -B3 -A12 "No key layers" | head -40 | cut -c1-200
