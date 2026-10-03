#!/usr/bin/env bash
# Rehearsal of deploy-aubscraft.bat steps 2-4 AFTER setup (panel folder owned by minecraft), run as zed with
# the bat's exact remote commands. $1 = the linux-x64 publish folder.
set -uo pipefail
STAGING=/home/zed/aubscraft-staging; REMOTE_DIR=/srv/aubscraft; SERVICE=aubscraft_admin
rm -rf $STAGING && mkdir -p $STAGING && (cd "$1" && tar -cf - .) | tar -C $STAGING -xf -
bash -c "set -e; chmod +x $STAGING/AubsCraft.Admin.Server; rm -f $STAGING/appsettings.json $STAGING/appsettings.Production.json; sudo systemctl stop $SERVICE; cp -r --remove-destination $STAGING/. $REMOTE_DIR/" || { echo "INSTALL FAILED"; sudo systemctl start $SERVICE; exit 1; }
sudo systemctl start $SERVICE && sleep 3 && systemctl is-active $SERVICE && rm -rf $STAGING
for i in $(seq 1 60); do curl -s -o /dev/null http://127.0.0.1:5080/api/public/status && break; sleep 1; done
curl -s http://127.0.0.1:5080/api/public/status; echo
echo "panel runs as: $(ps -o user= -p "$(pgrep -f /srv/aubscraft/AubsCraft.Admin.Server | head -1)")"
stat -c '%U:%G %a %n' $REMOTE_DIR/AubsCraft.Admin.Server $REMOTE_DIR/appsettings.json $REMOTE_DIR/servers.json
