#!/bin/bash
# Builds the AubsCraft Gate Velocity plugin into AubsCraft.Admin.Server/ProxyPlugins/aubscraft-gate.jar (shipped with
# the panel; ProxyService installs it into the proxy's plugins folder).
# Usage: ./build.sh <path to a JDK 21+ bin folder> <path to velocity.jar (4.2.0)>
set -e
JDK_BIN="$1"
VELOCITY_JAR="$2"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/build"
rm -rf "$OUT" && mkdir -p "$OUT/classes"
"$JDK_BIN/javac" --release 21 -cp "$VELOCITY_JAR" -d "$OUT/classes" "$HERE/src/com/spawndev/aubscraftgate/GatePlugin.java"
cp "$HERE/resources/velocity-plugin.json" "$OUT/classes/"
mkdir -p "$HERE/../AubsCraft.Admin.Server/ProxyPlugins"
(cd "$OUT/classes" && "$JDK_BIN/jar" --create --file "$HERE/../AubsCraft.Admin.Server/ProxyPlugins/aubscraft-gate.jar" --date=2026-10-03T00:00:00Z .)
echo "Built AubsCraft.Admin.Server/ProxyPlugins/aubscraft-gate.jar"
