#!/bin/bash
# Points the Homebrew cask at a published release.
#
#   scripts/update-tap.sh 0.8.3
#
# Reads the DMG's sha256 from the release, edits Casks/devsweep.rb in
# karuneshpalekar/homebrew-tap, and pushes. release.sh calls this at the end.
set -euo pipefail

VERSION="${1:?usage: scripts/update-tap.sh <version>}"
REPO="karuneshpalekar/DevSweep"
TAP="karuneshpalekar/homebrew-tap"

SHA=$(gh release download "v$VERSION" -R "$REPO" -p "*.dmg.sha256" -O - | awk '{print $1}')
[[ "$SHA" =~ ^[0-9a-f]{64}$ ]] || { echo "error: no sha256 on release v$VERSION" >&2; exit 1; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
gh repo clone "$TAP" "$WORK/tap" -- -q
cd "$WORK/tap"
git config user.name "karuneshpalekar"
git config user.email "karuneshpalekar.kp66@gmail.com"

sed -i '' -E "s/^  version \".*\"/  version \"$VERSION\"/; s/^  sha256 \".*\"/  sha256 \"$SHA\"/" Casks/devsweep.rb
if git diff --quiet; then echo "Tap already at $VERSION"; exit 0; fi
git commit -qam "devsweep $VERSION"
git push -q
echo "Tap updated to $VERSION"
