#!/bin/bash
# Build VRDetect Paper plugin
# Requires: Java 21+ JDK, Paper server jar in the plugins parent directory
# Usage: ./build.sh /path/to/paper-server.jar

PAPER_JAR="${1:-/opt/minecraft/server/paper.jar}"
OUT_DIR="build"
SRC_DIR="src/main/java"
RES_DIR="src/main/resources"

if [ ! -f "$PAPER_JAR" ]; then
    echo "Paper jar not found at: $PAPER_JAR"
    echo "Usage: ./build.sh /path/to/paper-server.jar"
    exit 1
fi

echo "Building VRDetect plugin..."
mkdir -p "$OUT_DIR/classes"

# Compile
javac -cp "$PAPER_JAR" -d "$OUT_DIR/classes" \
    "$SRC_DIR/com/spawndev/vrdetect/VRDetectPlugin.java"

if [ $? -ne 0 ]; then
    echo "Compilation failed"
    exit 1
fi

# Copy resources
cp "$RES_DIR/plugin.yml" "$OUT_DIR/classes/"

# Package
cd "$OUT_DIR/classes"
jar cf ../VRDetect.jar .
cd ../..

echo "Built: $OUT_DIR/VRDetect.jar"
echo "Copy to your server's plugins/ directory and restart."
