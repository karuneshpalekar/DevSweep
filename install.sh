#!/bin/bash
# Builds a Release version and installs it to /Applications.
set -euo pipefail
cd "$(dirname "$0")"

./generate.sh

BUILD_DIR="$PWD/build"
xcodebuild -project DevSweep.xcodeproj -scheme DevSweep \
  -configuration Release -destination 'platform=macOS' \
  -derivedDataPath "$BUILD_DIR" -allowProvisioningUpdates build

APP_PATH="$BUILD_DIR/Build/Products/Release/DevSweep.app"

killall DevSweep 2>/dev/null || true
rm -rf /Applications/DevSweep.app
cp -R "$APP_PATH" /Applications/
xattr -dr com.apple.quarantine /Applications/DevSweep.app 2>/dev/null || true
open /Applications/DevSweep.app

echo "Installed /Applications/DevSweep.app"
echo "For complete results, give it Full Disk Access in System Settings, Privacy & Security."
