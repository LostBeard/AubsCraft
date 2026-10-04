#!/bin/bash
# One-time update of minecraft@.service so each server can name its own Java in its aubscraft.env (JAVA=...).
# Forge for 1.21.x needs Java 21 (its Mixin cannot read Java 25 classes); every other server keeps /usr/bin/java.
# Safe for running servers: they keep running, and a server without a JAVA= line starts exactly as before.
# Usage (on the VM): sudo bash java-per-server.sh
set -e
UNIT=/etc/systemd/system/minecraft@.service
[ -f "$UNIT" ] || { echo "No $UNIT"; exit 1; }
[ -x /usr/lib/jvm/java-21-openjdk-amd64/bin/java ] || { echo "Java 21 is not installed (apt install openjdk-21-jre-headless)"; exit 1; }
if grep -q 'JAVA:-/usr/bin/java' "$UNIT"; then echo "Already updated."; exit 0; fi
cp "$UNIT" "$UNIT.bak-$(date +%Y%m%d-%H%M%S)"
# $$ passes a literal $ to sh, which expands the variables from the EnvironmentFile.
sed -i "s|^ExecStart=/usr/bin/java \$JAVA_OPTS \$LAUNCH nogui\$|# JAVA (optional, per server): the java to run - Forge needs Java 21.\nExecStart=/bin/sh -c 'exec \"\$\${JAVA:-/usr/bin/java}\" \$\$JAVA_OPTS \$\$LAUNCH nogui'|" "$UNIT"
grep -q 'JAVA:-/usr/bin/java' "$UNIT" || { echo "The ExecStart line was not the expected one; nothing changed."; exit 1; }
systemctl daemon-reload
echo "Updated:"; grep -n "ExecStart" "$UNIT"
