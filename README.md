# PixelBridge

Turn an Android phone into a USB webcam, microphone, motion sensor, GPS receiver, Bluetooth LE radio and network adapter. No root. Built with a screenless Pixel 7 in mind; initial USB debugging authorization and Android permissions are still required.

| | Windows 11 | Linux | macOS 14+ |
|---|---|---|---|
| Desktop | Native WPF | Avalonia | Native SwiftUI |
| Webcam output | PixelBridge Camera / Media Foundation | PixelBridge Camera / v4l2loopback | OBS Virtual Camera, or a provisioned PixelBridge extension |
| Microphone output | VB-CABLE | PipeWire/PulseAudio virtual source | BlackHole |
| Camera controls | Lens, zoom, OIS/EIS, rotation, mirror | Same | Same |
| Internet control | System tray | System tray | Menu bar |

## Install

Download a platform package and the Android APK from [Releases](https://github.com/palermostest25/pixelbridge/releases). Before a release, successful [Build and verify](https://github.com/palermostest25/pixelbridge/actions/workflows/build.yml) runs provide the same packages as downloadable artifacts.

- **Windows:** extract `PixelBridge-Setup.zip`, run `Install.cmd`. Webcam registration asks for administrator approval once. Install Android platform-tools (`adb`) and scrcpy; set their paths in Settings if they are not on PATH. VB-CABLE is required to expose microphone audio to other apps.
- **Linux x64:** extract the archive, run `bash install.sh`, then open PixelBridge from the application launcher. Install `adb`, `ffmpeg`, `scrcpy`, and `pulseaudio-utils` / PipeWire's PulseAudio compatibility layer. For webcam output install your distribution's `v4l2loopback` package; the Camera page's setup button loads it with a password prompt. Secure Boot may require signing that distribution's kernel module. Desktop environments without a system tray can use the window controls.
- **macOS:** extract the app to Applications. Install `adb` and `scrcpy` (Homebrew: `brew install android-platform-tools scrcpy`). For webcam output, install OBS and enable its signed virtual camera extension once. Select **OBS Virtual Camera** in your call app; OBS itself can remain closed. The downloadable app is locally signed, not notarized. See [macOS build notes](macos/README.md).

On the phone, enable USB debugging and authorize this computer. Install `PixelBridge-Android.apk` with `adb install -r PixelBridge-Android.apk`. Open the phone app once, grant the capture permissions you need, then pair from the desktop. A phone without a working display needs an already authorized computer/key or another way to grant these permissions; PixelBridge cannot bypass Android authorization.

**Existing installs:** Android updates must use the original signing key. CI APKs have a fresh debug signature and are intended for fresh installations. To preserve pairing and permissions on an existing phone, build using its original debug keystore. Never commit or upload that key. Do not uninstall an existing installation just to bypass a signing mismatch.

## Camera and power

Choose **Ultra wide**, **Wide/Back**, or **Front** from the phone's reported cameras. Ultra wide appears only when the phone exposes it. Zoom updates without restarting capture. Automatic stabilization selects optical stabilization where supported, otherwise electronic stabilization; actual capture feedback is displayed. Optical stabilization cannot be enabled on a lens without OIS. Automatic orientation follows the phone; manual quarter turns and mirroring apply to both preview and webcam. Portrait images fit within the webcam canvas without stretching.

**Save power** defaults on: capture is capped at 15 fps. Turn it off for the configured 30 fps capture. Previews are limited to 15 fps independently of call output, and hidden previews stop updating. On macOS, frames go directly to the preview layer without rebuilding the SwiftUI interface. Motion sensors run only while their page is visible or a sensor API/export/OSC/tilt consumer needs them. Battery and thermal monitoring remain available without motion sensors. The Android app no longer holds the display awake; phone mirroring is opt-in, limited to 15 fps and switches the physical display off. Stop camera, mic and USB internet sharing when unused to reduce the remaining phone/USB/network load.

The **USB internet sharing** control changes the phone's USB tethering mode, including sharing its Wi-Fi connection over USB. It does not disable the phone's Wi-Fi. The USB link briefly reconnects when the mode changes. This requires Android to permit the authorized ADB shell to change USB functions.

## Build and verify

- Android: JDK 21, SDK platform `android-37.0`, build-tools `37.0.0`; `cd android && bash gradlew assembleDebug testDebugUnitTest`.
- Windows: .NET 10 SDK, Visual Studio C++ tools and Windows SDK 10.0.26100. Run `./BUILD_ALL.ps1 -SkipAndroid`; the default toolset is v143, overridable with `-PlatformToolset v145`.
- Linux: .NET 10 SDK; `dotnet publish linux/PixelBridge.Linux -c Release -r linux-x64 --self-contained true`. `dotnet run --project linux/PixelBridge.MediaSmoke` exercises the real FFmpeg pipeline with generated H.264 across all eight rotation/mirror combinations.
- macOS: `bash macos/build-local.sh --install`; `swift test --package-path macos` requires full Xcode for XCTest. The local build itself works with Command Line Tools.

CI builds all four components, runs protocol and camera geometry tests, exercises the Windows virtual camera in-process, decodes real generated H.264 through the Linux pipeline, and renders every Windows/Linux page. Tagged builds publish packages only after all jobs pass. Physical USB/phone permissions, external call-app selection, Linux kernel module installation and real-world battery runtime still require hardware testing; a successful build is not proof of those integrations.

See [protocol/PROTOCOL.md](protocol/PROTOCOL.md) for the wire format. Pairing keys and signing keys remain local.
