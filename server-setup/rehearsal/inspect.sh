#!/usr/bin/env bash
ls -la /opt/minecraft/server | head -30
grep -iE "error|exception|warn.*level|Stopping|Saving|Done \(" /opt/minecraft/server/logs/latest.log | tail -15 | cut -c1-180
journalctl -u minecraft --no-pager -n 15 | cut -c1-180
