#!/bin/bash
# Builds "PixelPet Focus.app" for Apple Silicon and Intel with the Swift compiler from Xcode or the
# Command Line Tools (xcode-select --install). No Xcode project, no dependencies.
set -euo pipefail
cd "$(dirname "$0")"
APP="build/PixelPet Focus.app"
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

for arch in arm64 x86_64; do
  xcrun --sdk macosx swiftc -O -swift-version 5 -parse-as-library -target "$arch-apple-macos12.0" \
    -o "build/PixelPetFocus-$arch" Core.swift Pet.swift Main.swift
done
lipo -create -output "$APP/Contents/MacOS/PixelPetFocus" build/PixelPetFocus-arm64 build/PixelPetFocus-x86_64
cp Info.plist "$APP/Contents/"
python3 make_icns.py build/AppIcon.iconset
iconutil -c icns build/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc signature: required to run on Apple Silicon. It isn't notarized, so the first launch needs
# right-click > Open (or System Settings > Privacy & Security > Open Anyway).
codesign --force --sign - "$APP"
ditto -c -k --keepParent "$APP" build/PixelPetFocus-mac.zip
echo "Built $APP"
