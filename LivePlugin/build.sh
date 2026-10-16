#!/bin/bash
# Builds the AubsCraft Live Paper plugin into AubsCraft.Admin.Server/ServerPlugins/aubscraft-live.jar (shipped with the panel; the panel
# installs it into a server's plugins folder). It compiles against the Paper API of the server it will run on, taken from that server's own
# bundle (server.jar), so it always matches.
# Usage: ./build.sh <path to a JDK 21+ bin folder> <path to the server's server.jar (the Paper bundle)>
set -e
JDK_BIN="$1"
BUNDLE_JAR="$2"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/build"
rm -rf "$OUT" && mkdir -p "$OUT/classes" "$OUT/bundle"

# The bundle holds the API jar and its libraries under META-INF/libraries
(cd "$OUT/bundle" && "$JDK_BIN/jar" xf "$BUNDLE_JAR" META-INF/libraries)

# A classpath of this machine's own kind of paths: native Windows tools need Windows paths joined with ';'
JARS="$(find "$OUT/bundle/META-INF/libraries" -name '*.jar')"
if command -v cygpath >/dev/null 2>&1; then
    CP="$(echo "$JARS" | while read -r j; do cygpath -m "$j"; done | tr '\n' ';' | sed 's/;$//')"
    SRC="$(cygpath -m "$HERE/src/com/spawndev/aubscraftlive/LivePlugin.java")"
    SRC2="$(cygpath -m "$HERE/src/com/spawndev/aubscraftlive/Frames.java")"
    CLASSES="$(cygpath -m "$OUT/classes")"
else
    CP="$(echo "$JARS" | tr '\n' ':' | sed 's/:$//')"
    SRC="$HERE/src/com/spawndev/aubscraftlive/LivePlugin.java"
    SRC2="$HERE/src/com/spawndev/aubscraftlive/Frames.java"
    CLASSES="$OUT/classes"
fi
[ -n "$CP" ] || { echo "No libraries found in $BUNDLE_JAR (is it the Paper bundle?)"; exit 1; }

SRCS="$(find "$HERE/src" -name "*.java" | while read -r f; do if command -v cygpath >/dev/null 2>&1; then cygpath -m "$f"; else echo "$f"; fi; done)"
"$JDK_BIN/javac" --release 21 -cp "$CP" -d "$CLASSES" $SRCS
cp "$HERE/resources/plugin.yml" "$HERE/resources/config.yml" "$OUT/classes/"
mkdir -p "$HERE/../AubsCraft.Admin.Server/ServerPlugins"
(cd "$OUT/classes" && "$JDK_BIN/jar" --create --file "$HERE/../AubsCraft.Admin.Server/ServerPlugins/aubscraft-live.jar" --date=2026-10-15T00:00:00Z .)
rm -rf "$OUT/bundle"
echo "Built AubsCraft.Admin.Server/ServerPlugins/aubscraft-live.jar"
