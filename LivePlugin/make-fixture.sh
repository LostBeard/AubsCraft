#!/bin/bash
# Regenerates AubsCraft.Admin.Server.Tests/TestData/live-frame-v1.bin with the plugin's own encoder (no Paper needed).
# Usage: ./make-fixture.sh <path to a JDK 21+ bin folder>
set -e
JDK_BIN="$1"
HERE="$(cd "$(dirname "$0")" && pwd)"
TMP="$HERE/build/fixture"
rm -rf "$TMP" && mkdir -p "$TMP"
if command -v cygpath >/dev/null 2>&1; then
    A="$(cygpath -m "$HERE/src/com/spawndev/aubscraftlive/Frames.java")"; B="$(cygpath -m "$HERE/test/FixtureGen.java")"; T="$(cygpath -m "$TMP")"
    OUT="$(cygpath -m "$HERE/../AubsCraft.Admin.Server.Tests/TestData/live-frame-v1.bin")"
else
    A="$HERE/src/com/spawndev/aubscraftlive/Frames.java"; B="$HERE/test/FixtureGen.java"; T="$TMP"
    OUT="$HERE/../AubsCraft.Admin.Server.Tests/TestData/live-frame-v1.bin"
fi
"$JDK_BIN/javac" --release 21 -d "$T" "$A" "$B"
"$JDK_BIN/java" -cp "$T" com.spawndev.aubscraftlive.FixtureGen "$OUT"
