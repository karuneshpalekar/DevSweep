#!/bin/bash
# Regenerates DevSweep.xcodeproj from project.yml.
#
# xcodegen writes objectVersion=77 (the Xcode 16+ project format), which
# crashes Xcode 15.x when opening Signing & Capabilities. Nothing here needs
# the newer format, so downgrade it to 56 after generating. Harmless on
# newer Xcode.
set -euo pipefail
cd "$(dirname "$0")"

command -v xcodegen >/dev/null || { echo "Install XcodeGen first: brew install xcodegen"; exit 1; }

xcodegen generate
sed -i '' \
  -e 's/objectVersion = 77;/objectVersion = 56;/' \
  -e 's/preferredProjectObjectVersion = 77;/preferredProjectObjectVersion = 56;/' \
  DevSweep.xcodeproj/project.pbxproj

echo "Generated DevSweep.xcodeproj"
