# PixelBridge for macOS (SwiftUI)

The macOS app can now be compiled, installed, and launched using Command Line Tools.
Live Pixel 7 capture, lens switching, zoom and stabilization feedback have been checked locally, including video read back through OBS Virtual Camera. Other hardware integrations still need their own checks.

For a local app without the virtual camera system extension:

```bash
cd macos
./build-local.sh --install
open /Applications/PixelBridge.app
```

This installs an app with a local signature and includes a Dock icon. Camera preview works independently
of the virtual camera extension. If OBS 30 or later's virtual camera is already installed and enabled,
PixelBridge sends frames directly into it: start the phone camera in PixelBridge, then select
**OBS Virtual Camera** in your calling app. OBS itself can remain closed; its virtual camera must not
be running as another producer. PixelBridge prefers its own extension if that is installed.

The separate **PixelBridge Camera** extension requires the signed build below and an Apple-issued
provisioning profile authorizing System Extension installation. The local build hides its install
control rather than sending an installation request that macOS must reject. Protocol tests require
Xcode's XCTest framework.

A hardware smoke check for the installed OBS output can be run without Xcode (after a local build):

```bash
swiftc -I .build/local-app -L .build/local-app -lPixelBridgeKit \
  App/Media/CameraSink.swift Tests/CameraSinkSmoke/RoundTrip.swift \
  -o .build/local-app/check-webcam
.build/local-app/check-webcam
```

This sends synthetic frames, reads them back from the virtual camera, and checks that stopping restores
OBS's placeholder. Run it with PixelBridge's camera stopped and OBS's virtual camera stopped.
The sink compatibility uses the same public CoreMediaIO queue interface described by
[pyvirtualcam's OBS backend](https://github.com/letmaik/pyvirtualcam/tree/main/pyvirtualcam/native_macos_obs_cmioextension).

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
| USB tethering | Android NCM; recent macOS includes an NCM driver. PixelBridge detects the interface and can toggle USB sharing |
| Bluetooth LE bridge, Wi-Fi scan | the phone radio ops from protocol §9 |
| Menu-bar extra | camera/mic/privacy, power saving and USB sharing controls |

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
   Use OBS Virtual Camera with the local build if this provisioning is unavailable.
4. Plug in the Pixel and allow USB debugging for this Mac on the phone (use `scrcpy` to see the prompt).

## Known gaps / things to verify on first build
- `CameraSink` uses the CMIO C API to find the sink stream by name ("… Sink"). OBS output has a synthetic round-trip check; PixelBridge’s own signed extension still needs runtime verification.
- `ExtensionProvider.swift` follows Apple's *Create camera extensions with Core Media IO* (WWDC22) sink-stream pattern.
- macOS NCM tethering with the Pixel has not been tried. If no interface appears, check System Settings › Network for an "Pixel 7" service.
- No installer or notarisation yet. Archive → Developer ID → notarize with `notarytool` for distribution.

## Camera controls and phone screen

The camera page derives its lenses, zoom limits, and stabilization options from the phone's live capabilities.
On Pixel 7, Ultra wide uses the rear logical camera's minimum zoom (about 0.67×), Wide starts at 1×, and Front uses camera 1.
Zoom and stabilization change the repeating capture request without restarting video. The status reports actual Camera2
capture results; OIS is available on the main rear lens, while Auto uses electronic stabilization for the ultrawide.
Auto orientation follows physical phone rotation even without a display. Manual quarter-turn overrides and mirroring
apply to both the preview and OBS webcam output. Preferences persist across app restarts.

The menu bar and Internet Sharing page include a USB internet sharing switch. Switching USB functions briefly reconnects
the device; ADB stays enabled and PixelBridge reconnects. This controls USB sharing, not the phone's Wi-Fi radio or hotspot.

Phone Screen uses scrcpy installed by Homebrew. Its launcher supplies the ADB executable and search path explicitly,
brings an existing mirror window forward, and reports launch errors in the page.
