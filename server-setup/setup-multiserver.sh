#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# AubsCraft multi-server: ONE-TIME root setup on the aubscraft VM.
#   Run:  sudo bash setup-multiserver.sh
# Safe to re-run (every step is idempotent). Does NOT touch the existing server
# (/opt/minecraft/server, minecraft.service) or start/stop anything.
#
# After this, the admin panel runs as user minecraft (the user that owns the servers' files: Minecraft
# writes level.dat and playerdata as owner-only 0600, so only that user can back up, restore or reset them)
# and creates, starts and stops servers itself:
#   /opt/minecraft/servers/<id>/   one folder per added server (owned minecraft, group-writable)
#   minecraft@<id>.service         systemd template: runs a server from its folder
#   /opt/minecraft/velocity/       the Velocity proxy, velocity.service
#   /opt/minecraft/backups/        world backups made from the panel
# -----------------------------------------------------------------------------
set -euo pipefail

MC_USER=minecraft
MC_GROUP=minecraft
PANEL_USER=zed
ROOT=/opt/minecraft

if [[ $EUID -ne 0 ]]; then echo "Run with sudo." >&2; exit 1; fi

echo "[1/7] Folders (setgid + group-writable: deploys run as zed, in the minecraft group)"
for d in "$ROOT/servers" "$ROOT/velocity" "$ROOT/backups"; do
    install -d -o "$MC_USER" -g "$MC_GROUP" -m 2775 "$d"
done
usermod -aG "$MC_GROUP" "$PANEL_USER"

echo "[2/7] minecraft@.service template"
cat > /etc/systemd/system/minecraft@.service <<'UNIT'
[Unit]
Description=AubsCraft Minecraft server %i
After=network.target

[Service]
User=minecraft
Group=minecraft
# Files the server creates stay writable by the panel (group minecraft).
UMask=0002
WorkingDirectory=/opt/minecraft/servers/%i
# Written by the admin panel per server:
#   JAVA_OPTS = heap and JVM flags, e.g. -Xms1G -Xmx2G -XX:+UseG1GC
#   LAUNCH    = how this loader starts: "-jar server.jar" (Paper, Vanilla),
#               "-jar fabric-server-launch.jar" (Fabric), "@libraries/.../unix_args.txt" (Forge / NeoForge)
EnvironmentFile=/opt/minecraft/servers/%i/aubscraft.env
# systemd splits the unbraced $VARS on whitespace into separate arguments.
# JAVA (optional, per server): the java to run - Forge needs Java 21; everything else uses /usr/bin/java.
# $$ passes a literal $ to sh, which expands the variables from the EnvironmentFile.
ExecStart=/bin/sh -c 'exec "$${JAVA:-/usr/bin/java}" $$JAVA_OPTS $$LAUNCH nogui'
# Minecraft saves and exits on SIGTERM; 143 is that clean exit.
SuccessExitStatus=143
TimeoutStopSec=120
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
UNIT

echo "[3/7] velocity.service"
cat > /etc/systemd/system/velocity.service <<'UNIT'
[Unit]
Description=AubsCraft Velocity proxy
After=network.target

[Service]
User=minecraft
Group=minecraft
UMask=0002
WorkingDirectory=/opt/minecraft/velocity
# Velocity's recommended flags (docs.papermc.io/velocity/tuning).
ExecStart=/usr/bin/java -Xms512M -Xmx512M -XX:+UseG1GC -XX:G1HeapRegionSize=4M -XX:+UnlockExperimentalVMOptions -XX:+ParallelRefProcEnabled -XX:+AlwaysPreTouch -XX:MaxInlineLevel=15 -jar velocity.jar
SuccessExitStatus=143
TimeoutStopSec=60
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
UNIT

echo "[4/7] The panel runs as minecraft (drop-in for aubscraft_admin)"
install -d /etc/systemd/system/aubscraft_admin.service.d
rm -f /etc/systemd/system/aubscraft_admin.service.d/umask.conf
cat > /etc/systemd/system/aubscraft_admin.service.d/run-as-minecraft.conf <<'UNIT'
[Service]
User=minecraft
Group=minecraft
UMask=0002
UNIT

echo "[5/7] Ownership: everything under /opt/minecraft and /srv/aubscraft belongs to minecraft"
# Earlier copies over the M: drive left zed-owned 0700 files in the server folder (plugins-backup/, some jars).
chown -R "$MC_USER:$MC_GROUP" "$ROOT" /srv/aubscraft
find /srv/aubscraft -type d -exec chmod 2775 {} +
find /srv/aubscraft -type f -exec chmod g+rw {} +
# Secrets and state stay owner-only (RCON passwords, accounts).
for f in appsettings.json appsettings.Production.json appsettings.Development.json servers.json users.json \
         invite-codes.json whitelist-audit.json bans.json activity-log.json admin.json admin.json.migrated; do
    if [[ -f "/srv/aubscraft/$f" ]]; then chmod 600 "/srv/aubscraft/$f"; fi
done

echo "[6/7] minecraft.service: exit 143 (SIGTERM, saved and stopped) is a clean stop"
install -d /etc/systemd/system/minecraft.service.d
cat > /etc/systemd/system/minecraft.service.d/clean-stop.conf <<'UNIT'
[Service]
SuccessExitStatus=143
UNIT

echo "[7/7] Narrow sudo rule for the panel (minecraft) + deploys (zed): systemctl on these units only"
# sudo 1.9.10+ regex arguments: exactly one action and one Minecraft unit, nothing else.
cat > /etc/sudoers.d/aubscraft-panel <<'SUDO'
zed, minecraft ALL=(root) NOPASSWD: /usr/bin/systemctl ^(start|stop|restart|status|enable|disable|is-active) (minecraft|velocity|aubscraft_admin|minecraft@[a-z0-9-]+)(\.service)?$
SUDO
chmod 0440 /etc/sudoers.d/aubscraft-panel
visudo -cf /etc/sudoers.d/aubscraft-panel

systemctl daemon-reload
echo
echo "Done. Restart the panel so it runs as minecraft:  sudo systemctl restart aubscraft_admin"
echo "Optional hardening: /etc/sudoers also grants zed ALL of systemctl without a password."
echo "With the rule above in place, remove that line with 'sudo visudo' so the internet-facing panel"
echo "cannot use systemctl beyond the Minecraft units."
