#!/usr/bin/env bash
set -euo pipefail

PLUGIN_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_ROOT="$(cd "$PLUGIN_ROOT/../.." && pwd)"
UNITY_EDITOR_ROOT="${UNITY_EDITOR_ROOT:-/home/nate/Unity/Hub/Editor/2022.3.62f3/Editor}"
DJI_DEPS_DIR="${DJI_DEPS_DIR:-$PLUGIN_ROOT/.deps}"

DJI_SDK_AAR="$DJI_DEPS_DIR/dji-sdk-4.18.aar"
DJI_PROVIDED_JAR="$DJI_DEPS_DIR/dji-sdk-provided-4.18.jar"
ANDROID_JAR="$UNITY_EDITOR_ROOT/Data/PlaybackEngines/AndroidPlayer/SDK/platforms/android-36/android.jar"
UNITY_CLASSES="$UNITY_EDITOR_ROOT/Data/PlaybackEngines/AndroidPlayer/Variations/il2cpp/Development/Classes/classes.jar"
OUTPUT_JAR="$WORKSPACE_ROOT/src/QuestCommandCenter/Assets/Plugins/Android/DjiMsdkBridge.jar"
DJI_SDK_SHA256="6ec0b866057aba2b874bcbfabe7f3c991d00081b6093de88b9317412cd881897"
DJI_PROVIDED_SHA256="f253d8ef033423132d35c28d5c0da0aade2e660d18f1994c56dec630e3f48f20"

mkdir -p "$DJI_DEPS_DIR" "$(dirname "$OUTPUT_JAR")"

if [[ ! -f "$DJI_SDK_AAR" ]]; then
    curl -fsSL "https://repo1.maven.org/maven2/com/dji/dji-sdk/4.18/dji-sdk-4.18.aar" -o "$DJI_SDK_AAR"
fi

if [[ ! -f "$DJI_PROVIDED_JAR" ]]; then
    curl -fsSL "https://repo1.maven.org/maven2/com/dji/dji-sdk-provided/4.18/dji-sdk-provided-4.18.jar" -o "$DJI_PROVIDED_JAR"
fi

verify_sha256() {
    local file_path="$1"
    local expected_hash="$2"
    local actual_hash
    actual_hash="$(sha256sum "$file_path" | cut -d ' ' -f 1)"
    if [[ "$actual_hash" != "$expected_hash" ]]; then
        echo "SHA-256 mismatch for $file_path" >&2
        exit 1
    fi
}

verify_sha256 "$DJI_SDK_AAR" "$DJI_SDK_SHA256"
verify_sha256 "$DJI_PROVIDED_JAR" "$DJI_PROVIDED_SHA256"

PLUGIN_BUILD_DIR="$(mktemp -d)"
trap 'rm -rf "$PLUGIN_BUILD_DIR"' EXIT
mkdir -p "$PLUGIN_BUILD_DIR/classes" "$PLUGIN_BUILD_DIR/dji"
unzip -q "$DJI_SDK_AAR" classes.jar -d "$PLUGIN_BUILD_DIR/dji"

find "$PLUGIN_ROOT/src/main/java" -name '*.java' -print0 | xargs -0 javac \
    -source 8 -target 8 \
    -classpath "$ANDROID_JAR:$UNITY_CLASSES:$DJI_PROVIDED_JAR:$PLUGIN_BUILD_DIR/dji/classes.jar" \
    -d "$PLUGIN_BUILD_DIR/classes"

jar cf "$OUTPUT_JAR" -C "$PLUGIN_BUILD_DIR/classes" .
echo "Built $OUTPUT_JAR"
