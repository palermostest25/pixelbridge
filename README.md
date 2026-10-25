# PixelBridge

Bring your Android phone to your desktop: messages, calls, contacts and notifications alongside a webcam, microphone, compass, motion sensors, GPS, Bluetooth LE and internet sharing. No root. Built with a screenless Pixel 7 in mind; initial Android debugging authorization and app permissions are still required.

| | Windows 11 | Linux | macOS 14+ |
|---|---|---|---|
| Desktop | Native WPF | Avalonia | Native SwiftUI |
| Webcam output | PixelBridge Camera / Media Foundation | PixelBridge Camera / v4l2loopback | OBS Virtual Camera, or a provisioned PixelBridge extension |
| Microphone output | VB-CABLE | PipeWire/PulseAudio virtual source | BlackHole |
| Camera controls | Lens, zoom, OIS/EIS, rotation, mirror | Same | Same |
| Messages, contacts, calls, notification replies, compass and phone tools | Yes | Yes | Yes |
| Computer call microphone/speakers (supported Android phones) | Yes | Yes | Yes |
| USB + Wi-Fi connection / wireless pairing | Yes | Yes | Yes |
| Internet control | System tray | System tray | Menu bar |

## Install

Download a platform package and the Android APK from [Releases](https://github.com/palermostest25/pixelbridge/releases). Before a release, successful [Build and verify](https://github.com/palermostest25/pixelbridge/actions/workflows/build.yml) runs provide the same packages as downloadable artifacts.

- **Windows:** extract `PixelBridge-Setup.zip`, run `Install.cmd`. Webcam registration asks for administrator approval once. Install Android platform-tools (`adb`) and scrcpy; set their paths in Settings if they are not on PATH. VB-CABLE is required to expose microphone audio to other apps.
- **Linux x64:** extract the archive, run `bash install.sh`, then open PixelBridge from the application launcher. Install `adb`, `ffmpeg`, `scrcpy`, and `pulseaudio-utils` / PipeWire's PulseAudio compatibility layer. For webcam output install your distribution's `v4l2loopback` package; the Camera page's setup button loads it with a password prompt. Secure Boot may require signing that distribution's kernel module. Desktop environments without a system tray can use the window controls.
- **macOS:** extract the app to Applications. Install `adb` and `scrcpy` (Homebrew: `brew install android-platform-tools scrcpy`). For webcam output, install OBS and enable its signed virtual camera extension once. Select **OBS Virtual Camera** in your call app; OBS itself can remain closed. The downloadable app is locally signed, not notarized. See [macOS build notes](macos/README.md).

On the phone, enable USB debugging and authorize this computer. Install `PixelBridge-Android.apk` with `adb install -r PixelBridge-Android.apk`. Open the phone app once, grant the capture permissions you need, then pair from the desktop. A phone without a working display needs an already authorized computer/key or another way to grant these permissions; PixelBridge cannot bypass Android authorization.

**Existing installs:** Android updates must use the original signing key. CI APKs have a fresh debug signature and are intended for fresh installations. To preserve pairing and permissions on an existing phone, build using its original debug keystore. Never commit or upload that key. Do not uninstall an existing installation just to bypass a signing mismatch.

## Conversations and phone tools

**Messages** reads recent SMS conversations and sends texts through your phone’s carrier plan. Allow SMS permissions in the phone setup. Delivery receipts distinguish queued, sent and delivered; a lost acknowledgement never triggers an automatic resend. The current view loads up to 60 recent conversations and 75 messages per conversation. RCS, MMS attachments and proprietary messaging histories are not available through Android’s SMS provider. **Notifications** can reply to RCS and messaging apps when that app exposes Android’s reply action. Enable notification access and content sharing on the phone first; content is hidden by default.

**Calls** has its own dialer, numeric keypad and recent-call history inside PixelBridge. Choose a SIM/phone line when your phone has more than one. To answer, decline, end, hold, mute, change audio output or send keypad tones, choose PixelBridge as the Pixel’s default Phone app using **Set up on Pixel** (or **Enable incoming-call controls**). Mute and phone audio output reflect Android’s confirmed state. Merge/swap controls appear when supported by the phone and carrier. The phone also includes a local dialer for when USB is disconnected. No call or message is sent merely by connecting.

**Computer call audio** uses the computer’s microphone and speakers over the already-authorized USB or Wi-Fi debugging connection, independently of iPhone calling or Bluetooth pairing. Enable **Use Mac/computer microphone and speakers** in Calls. It requires Android 13+ and phone support for Android’s system call-audio redirection interface; unsupported phones show an error and retain their phone/headset route. The authorized ADB shell runs an ephemeral helper from the installed companion APK using its existing audio-interception permission. The ordinary phone app receives no privileged permission. macOS asks for microphone access and uses voice processing for echo cancellation. Windows uses the default communications speaker and default microphone; Linux uses PulseAudio/PipeWire compatibility tools. Headphones are useful on desktops without echo cancellation. Microphone mute and speaker mute are separate. Streams stop on call end, hold, privacy mode or disconnect, and no call audio is saved.

On macOS, click **PixelBridge’s menu-bar icon** for a compact dialer and call controls, or use **⌘⇧C** while PixelBridge is active. Incoming calls ring on the Mac and open this popover; answer, decline, silence the ring, mute or hang up there. Windows/Linux ring and bring their Calls page forward. Silencing a ring does not decline the call.

**Contacts** offers name/number search, calling and SMS composition. **Compass** uses magnetic north with tilt compensation, sensor accuracy and field strength, and pauses when hidden. **Phone Tools** provides flashlight, media volume, a short find-phone ring, vibration, app launching, display wake/sleep, confirmed restart and file transfers to/from Downloads. Existing files are not intentionally overwritten. Private phone data is held in desktop memory and cleared on disconnect or privacy mode; Android stores bounded send receipts without message bodies to prevent duplicate submissions.

## Connect wirelessly

All three desktop apps have **Phone screen → Connect over Wi-Fi**. Use a current Android platform-tools installation and put the phone and computer on the same private Wi-Fi network. No Bluetooth pairing is required.

- **Android 11+ (encrypted, no cable):** enable Developer options → Wireless debugging → Pair device with pairing code. In PixelBridge expand **Pair without a cable**, enter the displayed pairing address and six-digit code, then **Pair & connect**. PixelBridge discovers the connection port automatically. If discovery is blocked, enter the separate IP address and port from Android's main Wireless debugging page and choose **Connect paired phone**. Grant the companion's permissions and choose **Pair** in PixelBridge when prompted. The code is never saved. The Android app includes a shortcut to Developer options.
- **Screenless phone / trusted USB:** connect the already-authorized phone by USB, then choose **Set up over USB…**. After accepting the network-debugging explanation, PixelBridge reads its Wi-Fi address, enables ADB on port 5555, verifies the phone, and reconnects over Wi-Fi. Unplug after the success message. This compatibility mode is **unencrypted** and exposes Android debugging to the local network; use only a trusted private network. **Turn off wireless** sends `adb usb` to stop this listener. If the phone is unreachable, reconnect USB to turn it off. A restart or changed IP can require USB setup again.

The saved connection retries at most once every 30 seconds while disconnected, and never scans the LAN. Encrypted pairing can rediscover changed addresses/ports through Android's mDNS advertisement; the phone identity is checked again before accepting a new address. The existing PixelBridge pairing is retained across the transport change. Calls and computer audio, messages, camera, microphone, sensors, tools and scrcpy use the selected transport. Wi-Fi quality affects audio/video latency. **USB internet sharing still requires a cable.**

**Disconnect Wi-Fi** removes PixelBridge's saved encrypted connection and disconnects its ADB transport. Android's Wireless debugging switch and trust list belong to Android; turn that switch off or forget this computer there to disable/revoke access. Other ADB tools can reconnect a still-authorized phone. For Android's connection and encryption details, see the [ADB guide](https://developer.android.com/tools/adb) and [ADB Wi-Fi architecture](https://android.googlesource.com/platform/packages/modules/adb/+/HEAD/docs/dev/adb_wifi.md).

Wireless transport selection, address validation and wrong-phone rejection have automated coverage. Physical wireless calling/camera behavior still needs a real phone test; platform builds alone do not establish radio performance or battery life.

## Reconnect and background use

Plugging in the selected phone brings the dashboard forward by default; disable this in Settings if preferred. PixelBridge waits through boot, opens the companion, recreates connection forwards and reconnects using the saved pairing. Android requires its first unlock after a restart before encrypted phone data is available. This cannot be skipped. On a screenless phone, open **Phone Screen** from an already authorized computer to enter that unlock yourself.

**Import ADB keys** is available on the Phone Screen page on macOS, Windows and Linux, and in Mac Settings. Copy both `adbkey` and `adbkey.pub` from the other computer’s `.android` directory, keep them together, then select either file. PixelBridge checks the private/public match and shows a fingerprint before applying it. The existing keys are backed up in `.android/pixelbridge-key-backups` with private permissions. Applying the import restarts the shared ADB server and briefly interrupts other ADB apps; canceling leaves everything unchanged. This restores an identity the phone already trusts; it cannot bypass Android’s USB-debugging approval. Android app-signing keys such as `debug.keystore` are separate and are not accepted here.

The phone’s **Available after restart** option resumes only the idle loopback listener after first unlock. Camera, mic, location and motion capture remain off until requested. Desktop packages include a matching companion APK and can update it in place on the next connection; signature mismatches stop with an explanation and never uninstall the phone app. Updates for an existing installation must use its original signing key.

On macOS, closing the dashboard removes the Dock icon and keeps the menu-bar call popover, ringing and bridge alive. Choose **Open PixelBridge** to restore the window. **Quit PixelBridge** exits completely. Windows and Linux keep their system-tray controls when the window closes.

## Camera and power

Choose **Ultra wide**, **Wide/Back**, or **Front** from the phone's reported cameras. Ultra wide appears only when the phone exposes it. Zoom updates without restarting capture. Automatic stabilization selects optical stabilization where supported, otherwise electronic stabilization; actual capture feedback is displayed. Optical stabilization cannot be enabled on a lens without OIS. Automatic orientation follows the phone; manual quarter turns and mirroring apply to both preview and webcam. Portrait images fit within the webcam canvas without stretching.

**Save power** defaults on: encoded video is capped at 15 fps even when a lens only exposes a 30 fps capture range. Turn it off for the configured 30 fps capture. Previews are limited to 15 fps independently of call output, and hidden previews stop updating. On macOS, frames go directly to the preview layer without rebuilding the SwiftUI interface. Motion sensors run only while their page is visible or a sensor API/export/OSC/tilt consumer needs them. Battery and thermal monitoring remain available without motion sensors. The Android app no longer holds the display awake; phone mirroring is opt-in, limited to 15 fps and switches the physical display off. Stop camera, mic and USB internet sharing when unused to reduce the remaining phone/USB/network load.

The **USB internet sharing** control changes the phone's USB tethering mode, including sharing its Wi-Fi connection over USB. It does not disable the phone's Wi-Fi. The USB link briefly reconnects when the mode changes. This requires Android to permit the authorized ADB shell to change USB functions.

## Build and verify

- Android: JDK 21, SDK platform `android-37.0`, build-tools `37.0.0`; `cd android && bash gradlew assembleDebug testDebugUnitTest`.
- Windows: .NET 10 SDK, Visual Studio C++ tools and Windows SDK 10.0.26100. Run `./BUILD_ALL.ps1 -SkipAndroid`; the default toolset is v143, overridable with `-PlatformToolset v145`.
- Linux: .NET 10 SDK; `dotnet publish linux/PixelBridge.Linux -c Release -r linux-x64 --self-contained true`. `dotnet run --project linux/PixelBridge.MediaSmoke` exercises the real FFmpeg pipeline with generated H.264 across all eight rotation/mirror combinations.
- macOS: `bash macos/build-local.sh --install`; `swift test --package-path macos` requires full Xcode for XCTest. The local build itself works with Command Line Tools.

CI builds all four components, runs protocol and camera geometry tests, exercises the Windows virtual camera in-process, decodes real generated H.264 through the Linux pipeline, and renders every Windows/Linux page. Tagged builds publish packages only after all jobs pass. Physical USB/phone permissions, external call-app selection, Linux kernel module installation and real-world battery runtime still require hardware testing; a successful build is not proof of those integrations.

See [protocol/PROTOCOL.md](protocol/PROTOCOL.md) for the wire format. Pairing keys and signing keys remain local.

Local macOS builds use an ad-hoc signature by default. Set `PIXELBRIDGE_SIGN_IDENTITY` explicitly to use a valid signing identity; the installer no longer automatically selects an Apple development certificate, which may have been revoked. Local installation replaces the verified app bundle as a whole instead of overwriting a signed executable in place. Public notarized distribution still requires a valid Developer ID signing identity and notarization.
