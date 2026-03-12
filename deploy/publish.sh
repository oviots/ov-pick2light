#!/usr/bin/env bash
# publish.sh — Build Pick2Light as a Native AOT single-file binary.
#
# Usage:
#   ./deploy/publish.sh                   # linux-x64 (default)
#   ./deploy/publish.sh linux-arm64
#   ./deploy/publish.sh win-x64
#   ./deploy/publish.sh osx-arm64
#   ./deploy/publish.sh all               # build all four RIDs in sequence

set -euo pipefail

DOTNET="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
PROJECT="src/P2L.CLI/P2L.CLI.csproj"
SOLUTION_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

build_rid() {
    local RID="$1"
    local OUT="$SOLUTION_ROOT/artifacts/$RID"

    echo ""
    echo "==> Publishing P2L for $RID..."
    "$DOTNET" publish "$SOLUTION_ROOT/$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --output "$OUT" \
        --self-contained true \
        -p:PublishAot=true \
        -p:InvariantGlobalization=true \
        -p:StripSymbols=true

    local BIN="$OUT/p2l"
    [[ "$RID" == win-* ]] && BIN="$OUT/p2l.exe"

    echo "==> Done: $BIN"
    ls -lh "$BIN" 2>/dev/null || ls -lh "$OUT/"
}

ARG="${1:-linux-x64}"

if [[ "$ARG" == "all" ]]; then
    for RID in linux-x64 linux-arm64 win-x64 osx-arm64; do
        build_rid "$RID"
    done
else
    build_rid "$ARG"
fi
