#!/bin/bash
# Build a locally signed app with Command Line Tools; full Xcode is not required.
# The Developer-ID-signed virtual camera extension uses the Xcode project instead.
set -euo pipefail
cd "$(dirname "$0")"
build_dir="$PWD/.build/local-app"
app_dir="$build_dir/PixelBridge.app"
mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
architecture="$(uname -m)"
swiftc -emit-library -static -emit-module -module-name PixelBridgeKit \
  -swift-version 5 -O -target "${architecture}-apple-macosx14.0" \
  Sources/PixelBridgeKit/*.swift \
  -emit-module-path "$build_dir/PixelBridgeKit.swiftmodule" \
  -o "$build_dir/libPixelBridgeKit.a"
swiftc -O -swift-version 5 -I "$build_dir" -L "$build_dir" -lPixelBridgeKit \
  App/Views/*.swift App/PixelBridgeApp.swift App/AppModel.swift App/CameraControls.swift App/Media/*.swift \
  -target "${architecture}-apple-macosx14.0" \
  -o "$app_dir/Contents/MacOS/PixelBridge"
cp Resources/LocalInfo.plist "$app_dir/Contents/Info.plist"
cp Resources/PixelBridge.icns "$app_dir/Contents/Resources/PixelBridge.icns"
printf 'APPL????' > "$app_dir/Contents/PkgInfo"
codesign --force --sign - "$app_dir"
codesign --verify --deep --strict "$app_dir"
printf 'Built %s\n' "$app_dir"
if [[ "${1:-}" == "--install" ]]; then
  ditto "$app_dir" /Applications/PixelBridge.app
  printf 'Installed /Applications/PixelBridge.app\n'
fi
