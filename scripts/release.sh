#!/bin/bash
# Builds a DevSweep release DMG and publishes it on GitHub.
#
#   scripts/release.sh 0.1.0           # build, tag, publish
#   scripts/release.sh 0.1.0 --draft   # same, as a draft release
#
# Signing: with a "Developer ID Application" certificate and notary
# credentials (NOTARY_PROFILE, default "devsweep") the build is signed and
# notarized, so it opens normally everywhere. Without them it is ad-hoc
# signed, and people have to allow it once in Privacy & Security.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${1:?usage: scripts/release.sh <version> [--draft]}"
DRAFT=""; [ "${2:-}" = "--draft" ] && DRAFT="--draft"
PROFILE="${NOTARY_PROFILE:-devsweep}"
NOTES="docs/release-notes/$VERSION.md"
OUT="build/release"
APP="$OUT/DevSweep.app"
DMG="$OUT/DevSweep-$VERSION.dmg"

fail() { echo "error: $*" >&2; exit 1; }

[ -f "$NOTES" ] || fail "write $NOTES first"
[ -z "$(git status --porcelain)" ] || fail "commit or stash your changes first"
git rev-parse -q --verify "refs/tags/v$VERSION" >/dev/null && fail "tag v$VERSION already exists"

NOTARIZE=false
TEAM=$(sed -n 's/^DEVELOPMENT_TEAM *= *//p' Config/Local.xcconfig 2>/dev/null | tr -d ' ')
if security find-identity -v -p codesigning | grep -q "Developer ID Application" \
   && xcrun notarytool history --keychain-profile "$PROFILE" >/dev/null 2>&1; then
  NOTARIZE=true
  SIGN_ARGS=(CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="Developer ID Application" DEVELOPMENT_TEAM="$TEAM"
             OTHER_CODE_SIGN_FLAGS=--timestamp)
  echo "==> Signing with Developer ID and notarizing"
else
  SIGN_ARGS=(CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY=- DEVELOPMENT_TEAM=)
  echo "==> No Developer ID certificate: ad-hoc signing (not notarized)"
fi

echo "==> Building $VERSION"
./generate.sh >/dev/null
rm -rf "$OUT" && mkdir -p "$OUT"
xcodebuild -project DevSweep.xcodeproj -scheme DevSweep -configuration Release \
  -destination 'platform=macOS' -derivedDataPath build/derived \
  MARKETING_VERSION="$VERSION" ENABLE_HARDENED_RUNTIME=YES "${SIGN_ARGS[@]}" \
  build 2>&1 | grep -E "error:|BUILD (SUCCEEDED|FAILED)"
cp -R build/derived/Build/Products/Release/DevSweep.app "$APP"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
if $NOTARIZE; then
  codesign --force --deep --options runtime --timestamp --sign "Developer ID Application" "$APP"
else
  codesign --force --deep --options runtime --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"

echo "==> Packaging"
STAGE="$OUT/dmg" && mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/" && ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "DevSweep $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
if $NOTARIZE; then
  codesign --sign "Developer ID Application" --timestamp "$DMG"
  echo "==> Notarizing (usually a few minutes)"
  xcrun notarytool submit "$DMG" --keychain-profile "$PROFILE" --wait
  xcrun stapler staple "$DMG"
fi
(cd "$OUT" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
cat "$DMG.sha256"

echo "==> Publishing v$VERSION on GitHub"
git tag -a "v$VERSION" -m "DevSweep $VERSION"
git push origin "v$VERSION"
gh release create "v$VERSION" "$DMG" "$DMG.sha256" $DRAFT \
  --title "DevSweep $VERSION" --notes-file "$NOTES"
