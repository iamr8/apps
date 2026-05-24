#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$SCRIPT_DIR/apps/apps.csproj"
OUTPUT_DIR="$SCRIPT_DIR/dist"

# Allow overriding RID via argument or environment variable
RID="${1:-${RID:-}}"

if [ -z "$RID" ]; then
    ARCH="$(uname -m)"
    if [ "$ARCH" = "arm64" ]; then
        RID="osx-arm64"
    elif [ "$ARCH" = "x86_64" ]; then
        RID="osx-x64"
    else
        echo "Unsupported architecture: $ARCH" >&2
        exit 1
    fi
fi

echo "Publishing for $RID..."

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    ${VERSION:+-p:Version=$VERSION} \
    -o "$OUTPUT_DIR"

BINARY="$OUTPUT_DIR/apps"
chmod +x "$BINARY"
SIZE=$(du -h "$BINARY" | cut -f1 | xargs)

echo ""
echo "Published: $BINARY ($SIZE)"

