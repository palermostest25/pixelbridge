# PixelBridge for macOS (SwiftUI)

> **Status: written but NOT yet compiled or tested.** It was developed on a Windows PC with no Mac or Xcode available.
> The protocol logic (`PixelBridgeKit`) mirrors the Windows and Android code that *is* tested on the real Pixel, and it
> includes unit tests against the shared vectors in `protocol/test-vectors/auth.json`. Expect some compile fixes on
> first build. Report anything you change in `HANDOFF.md`.

## What it does
The same features as the Windows app, over the same single USB cable and the same protocol:

| Feature | macOS implementation |
|---|---|
| Discovery, pairing, 4 authenticated channels | `PixelBridgeKit` (Network.framework, CryptoKit HMAC, Keychain for the pairing key) |
| Live camera preview | VideoToolbox hardware H.264 decode → IOSurface BGRA |
| **Webcam for other apps** | `PixelBridgeCamera` CoreMediaIO **camera extension** (system extension). The app pushes frames into its sink stream; apps see "PixelBridge Camera". Shows a "camera unavailable" image when idle, never a stale frame |
| Microphone | AVAudioEngine → any output device. Install **BlackHole 2ch** to make it selectable as a microphone in Zoom/FaceTime |
| Sensors | Swift Charts plots + SceneKit 3D orientation |
| GPS | MapKit map with accuracy circle (macOS Location Services can't use an external source without a driver) |
| USB tethering | Android NCM; recent macOS includes an NCM driver. PixelBridge detects the interface (read-only) |
| Bluetooth LE bridge, Wi-Fi scan | the phone radio ops from protocol §9 |
| Menu-bar extra | camera/mic/privacy toggles |

## Build
Requirements: macOS 14+, Xcode 15.3+ (Swift 5.10), Homebrew.

```bash
brew install xcodegen android-platform-tools scrcpy
cd macos
swift test                       # PixelBridgeKit protocol tests (no device needed)
xcodegen generate                # creates PixelBridge.xcodeproj from project.yml
open PixelBridge.xcodeproj
```

In Xcode:
1. Set your **Team** on both targets (`PixelBridge` and `PixelBridgeCamera`). A camera extension must be signed
   with an Apple Developer ID; a free personal team can run the app but **cannot activate a system extension**.
2. Keep the App Group `$(TeamIdentifierPrefix)dev.pixelbridge` on both targets.
3. Run. Use **Camera → Install camera**, then approve it in **System Settings › Privacy & Security** (one time).
   For development you can instead run `systemextensionsctl developer on` (needs SIP adjustments, so it isn't recommended).
4. Plug in the Pixel and allow USB debugging for this Mac on the phone (use `scrcpy` to see the prompt).

## Known gaps / things to verify on first build
- `CameraSink` uses the CMIO C API to find the sink stream by name ("… Sink"). Verify the stream order and names with the extension installed.
- `ExtensionProvider.swift` follows Apple's *Create camera extensions with Core Media IO* (WWDC22) sink-stream pattern.
- macOS NCM tethering with the Pixel has not been tried. If no interface appears, check System Settings › Network for an "Pixel 7" service.
- No installer or notarisation yet. Archive → Developer ID → notarize with `notarytool` for distribution.
