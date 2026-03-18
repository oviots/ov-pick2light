#!/usr/bin/env bash
# deploy/build-msi.sh — Build MSI from macOS/Linux via GitHub Actions.
#
# MSI compilation requires Windows (WiX 5 limitation).
# This script triggers the GitHub Actions workflow remotely.
#
# Prerequisites:
#   brew install gh        (GitHub CLI)
#   gh auth login
#
# Usage:
#   ./deploy/build-msi.sh                  # x64, version 1.0.0
#   ./deploy/build-msi.sh 1.2.3            # custom version, x64
#   ./deploy/build-msi.sh 1.2.3 arm64      # custom version, arm64
#   ./deploy/build-msi.sh --download       # download last built MSI

set -euo pipefail

VERSION="${1:-1.0.0}"
PLATFORM="${2:-x64}"
DIST_DIR="$(cd "$(dirname "$0")/../dist" 2>/dev/null && pwd || echo "$(dirname "$0")/../dist")"

# Download-only mode
if [[ "${1:-}" == "--download" ]]; then
    echo "Downloading last MSI artifact..."
    mkdir -p "$DIST_DIR"
    gh run download --dir "$DIST_DIR" \
        "$(gh run list --workflow=build-msi.yml --limit=1 --json databaseId -q '.[0].databaseId')"
    echo "Downloaded to $DIST_DIR"
    ls "$DIST_DIR"
    exit 0
fi

echo "=== Triggering GitHub Actions MSI build ==="
echo "Version  : $VERSION"
echo "Platform : $PLATFORM"
echo ""

if ! command -v gh &>/dev/null; then
    echo "ERROR: GitHub CLI not found."
    echo "  brew install gh"
    echo "  gh auth login"
    exit 1
fi

gh workflow run build-msi.yml \
    -f version="$VERSION" \
    -f platform="$PLATFORM"

echo ""
echo "Build triggered. Monitor at:"
echo "  gh run watch"
echo "  gh run list --workflow=build-msi.yml"
echo ""
echo "Download MSI when complete:"
echo "  ./deploy/build-msi.sh --download"
