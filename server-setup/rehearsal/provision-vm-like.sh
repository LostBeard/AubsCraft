#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Turns a FRESH Ubuntu 24.04 (a throwaway WSL distro) into a copy of the aubscraft VM as it is BEFORE
# setup-multiserver.sh: same users/groups, same unit files, same sudo rules, same folder ownership and modes
# (including the leftovers that broke the first cutover: owner-only level.dat/playerdata, zed-owned 0700
# files in the server folder). Used to rehearse setup + cutover + deploy on the real OS, not Windows.
#   Run as root:  bash provision-vm-like.sh <windows-cache-dir-as-/mnt/c/...> <panel-publish-dir>
# Expects in the cache dir: paper-1.21.5-114.jar and servers/cutover/plugins/*.jar (from the local tests).
# -----------------------------------------------------------------------------
set -euo pipefail
CACHE="$1"
PUBLISH="$2"

echo "[1] Packages (Java 25 from Ubuntu, like the VM)"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq openjdk-25-jre-headless sudo python3 unzip curl >/dev/null
java -version 2>&1 | head -1

echo "[2] Users and groups as on the VM"
getent group minecraft >/dev/null || groupadd -r minecraft
id minecraft >/dev/null 2>&1 || useradd -r -g minecraft -d /opt/minecraft -m -s /bin/bash minecraft
id zed >/dev/null 2>&1 || useradd -m -s /bin/bash -G sudo zed
usermod -aG minecraft zed
echo 'zed:rehearsal' | chpasswd
# The VM: zed may run ANY systemctl without a password (/etc/sudoers) plus the old 4-command rule.
cat > /etc/sudoers.d/vm-zed <<'SUDO'
zed ALL=(ALL) NOPASSWD: /usr/bin/systemctl
SUDO
chmod 0440 /etc/sudoers.d/vm-zed

echo "[3] The Minecraft server, laid out and owned like /opt/minecraft/server on the VM"
install -d -o minecraft -g minecraft -m 2777 /opt/minecraft/server /opt/minecraft/server/plugins
cp "$CACHE/paper-1.21.5-114.jar" /opt/minecraft/server/server.jar
# The cache may hold them as .jar.disabled (the cutover test disables Geyser/Via): restore the VM's .jar names.
for j in "$CACHE"/servers/cutover/plugins/*.jar "$CACHE"/servers/cutover/plugins/*.jar.disabled; do
    [ -e "$j" ] || continue
    b=$(basename "$j"); cp "$j" "/opt/minecraft/server/plugins/${b%.disabled}"
done
echo "eula=true" > /opt/minecraft/server/eula.txt
cat > /opt/minecraft/server/server.properties <<'PROPS'
enable-rcon=true
rcon.port=25575
rcon.password=rehearsal-rcon
server-port=25565
online-mode=true
max-players=10
motd=AubsCraft rehearsal
PROPS
chown -R minecraft:minecraft /opt/minecraft
chmod -R a+rwX /opt/minecraft/server   # the VM's server folder is world-writable (drwxrwsrwx, -rw-rw-rw-)

# The VM's minecraft.service, verbatim.
cat > /etc/systemd/system/minecraft.service <<'UNIT'
[Unit]
Description=AubsCraft Minecraft Server
After=network.target

[Service]
User=minecraft
Group=minecraft
WorkingDirectory=/opt/minecraft/server
ExecStart=/usr/bin/java -Xmx3G -Xms1G -XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 -jar server.jar nogui
ExecStop=/bin/kill -SIGTERM $MAINPID
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
UNIT

echo "[4] The panel, as deployed: /srv/aubscraft owned by zed, running as zed"
install -d -o zed -g zed -m 775 /srv/aubscraft
cp -r "$PUBLISH"/. /srv/aubscraft/
chown -R zed:zed /srv/aubscraft
chmod +x /srv/aubscraft/AubsCraft.Admin.Server
echo '{"Rcon":{"Password":"rehearsal-rcon"}}' > /srv/aubscraft/appsettings.Production.json
chmod 600 /srv/aubscraft/appsettings.json /srv/aubscraft/appsettings.Production.json
chown zed:zed /srv/aubscraft/appsettings.Production.json
cat > /etc/systemd/system/aubscraft_admin.service <<'UNIT'
[Unit]
Description=AubsCraft Admin Panel
After=network.target

[Service]
User=zed
Group=zed
WorkingDirectory=/srv/aubscraft
ExecStart=/srv/aubscraft/AubsCraft.Admin.Server
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_URLS=http://0.0.0.0:5080
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
echo "[5] Start Minecraft (first start makes the world: level.dat and playerdata come out 0600)"
rm -f /opt/minecraft/server/logs/latest.log
systemctl start minecraft
for i in $(seq 1 180); do grep -q 'For help, type' /opt/minecraft/server/logs/latest.log 2>/dev/null && break; sleep 2; done
grep -m1 'For help, type' /opt/minecraft/server/logs/latest.log

echo "[6] The leftovers from copies over the M: drive: zed-owned, owner-only files in the server folder"
sudo -u zed mkdir -p -m 700 /opt/minecraft/server/plugins-backup
sudo -u zed cp /opt/minecraft/server/plugins/ViaVersion*.jar /opt/minecraft/server/plugins-backup/ViaVersion.jar.bak
sudo -u zed chmod 600 /opt/minecraft/server/plugins-backup/ViaVersion.jar.bak

echo "[7] Start the panel"
systemctl start aubscraft_admin
for i in $(seq 1 60); do curl -s -o /dev/null http://127.0.0.1:5080/api/public/status && break; sleep 1; done
curl -s http://127.0.0.1:5080/api/public/status; echo
echo "--- state the cutover failed on ---"
stat -c '%U:%G %a %n' /opt/minecraft/server/world/level.dat /opt/minecraft/server/plugins-backup
ps -o user= -p "$(pgrep -f /srv/aubscraft/AubsCraft.Admin.Server | head -1)"
