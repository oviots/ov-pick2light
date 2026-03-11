#!/usr/bin/env bash
# publish.sh — Build and package Pick2Light as a single-file native binary.
# Usage: ./deploy/publish.sh [linux-x64 | linux-arm64 | win-x64 | osx-arm64]

set -euo pipefail

RID="${1:-linux-x64}"
PROJECT="src/P2L.CLI/P2L.CLI.csproj"
OUT="artifacts/$RID"

echo "==> Publishing P2L for $RID..."
dotnet publish "$PROJECT" \
  --configuration Release \
  --runtime "$RID" \
  --output "$OUT" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishAot=true \
  -p:InvariantGlobalization=true

echo "==> Output: $OUT/p2l"
ls -lh "$OUT/p2l" 2>/dev/null || ls -lh "$OUT/"
