#!/usr/bin/env bash
# Rehearsal: run a setup script as root, restart the panel, show the state.
set -uo pipefail
bash "$1" | tail -4
systemctl restart aubscraft_admin
for i in $(seq 1 60); do curl -s -o /dev/null http://127.0.0.1:5080/api/public/status && break; sleep 1; done
bash /mnt/d/users/tj/Projects/AubsCraft/server-setup/rehearsal/state.sh
