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
cp ../assets/CallRing.wav "$app_dir/Contents/Resources/CallRing.wav"
# Bundle the matching companion for an in-place update on the next USB connection.
apk_path="${PIXELBRIDGE_ANDROID_APK:-$PWD/../android/app/build/outputs/apk/debug/app-debug.apk}"
if [[ -f "$apk_path" ]]; then cp "$apk_path" "$app_dir/Contents/Resources/PixelBridge.apk"; fi
printf 'APPL????' > "$app_dir/Contents/PkgInfo"
signing_identity="${PIXELBRIDGE_SIGN_IDENTITY:--}"
# Local builds use an ad-hoc signature unless an identity is explicitly supplied.
# A keychain listing can include revoked Apple certificates; never select one implicitly.
codesign --force --sign "$signing_identity" "$app_dir"
codesign --verify --deep --strict "$app_dir"
printf 'Built %s\n' "$app_dir"
if [[ "${1:-}" == "--install" ]]; then
  stage=$(mktemp -d /Applications/.pixelbridge-install.XXXXXX)
  trap 'rm -rf "$stage"' EXIT
  ditto "$app_dir" "$stage/PixelBridge.app"
  codesign --verify --deep --strict "$stage/PixelBridge.app"
  if [[ -e /Applications/PixelBridge.app ]]; then mv /Applications/PixelBridge.app "$stage/previous.app"; fi
  if ! mv "$stage/PixelBridge.app" /Applications/PixelBridge.app; then
    if [[ -e "$stage/previous.app" ]]; then mv "$stage/previous.app" /Applications/PixelBridge.app; fi
    exit 1
  fi
  printf 'Installed /Applications/PixelBridge.app\n'
fi
