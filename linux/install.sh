#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
destination="${XDG_DATA_HOME:-$HOME/.local/share}/pixelbridge"
mkdir -p "$destination" "${XDG_DATA_HOME:-$HOME/.local/share}/applications"
cp -a app/. "$destination/"
chmod +x "$destination/pixelbridge"
cat > "${XDG_DATA_HOME:-$HOME/.local/share}/applications/pixelbridge.desktop" <<ENTRY
[Desktop Entry]
Type=Application
Name=PixelBridge
Comment=Your Android camera, microphone and sensors over USB
Exec="$destination/pixelbridge"
Icon=$destination/Assets/pixelbridge.ico
Terminal=false
Categories=Utility;AudioVideo;
ENTRY
printf 'Installed PixelBridge. Open it from your application launcher.\n'
printf 'Dependencies: adb, ffmpeg, scrcpy; v4l2loopback for webcam output; pulseaudio-utils (pactl, pacat, parec, paplay) for microphone output and calls.\n'
