# PixelBridge — AI handoff (keep this current)

> If you are a new agent picking this up: read this file first, then `SPEC.md` (the original requirements),
> then `protocol/PROTOCOL.md`. Update the **Status** and **Next steps** sections whenever you finish something.
> Last updated: 2026-10-23 (session 1)

## What this is
A Windows (and later macOS) companion plus an Android APK that turns a **Pixel 7 with a dead screen**
(serial `<SERIAL>`, codename `panther`, Android 17 / SDK 37, build CP41.260831.007, locked bootloader, no root)
into a webcam, mic, sensor feed and network adapter over one USB-C cable. ADB-forwarded loopback TCP sockets
carry the data. USB tethering (NCM) gives a real network adapter.

## Hard rules (from SPEC.md and the user, do not break)
- Always use `adb -s <SERIAL>`. Ignore `emulator-5562` (offline ghost). Never deploy to another device.
- No root, bootloader, flashing, test signing or unsigned kernel drivers. Never delete adb keys.
- The phone display is dead: use **scrcpy** (`%USERPROFILE%\Downloads\scrcpy-win64-*\`) for any phone UI.
  The user taps Android permission dialogs themselves. Never auto-click consent.
- The battery has a repair history: keep camera/mic tests short (≤ 60 s) and supervised, and watch the temperature.
- Admin or persistent OS changes need explicit user consent first. Everything binds to 127.0.0.1.
- Never claim a test passed without evidence. The user is blunt and hates stock-looking UI: build a **native look** (Win11 Fluent; SwiftUI on Mac).
- On PowerShell 5.1, `Start-Process -ArgumentList` with a quoted title needs the whole arg string in one string.

## Repo layout
```
android/                 Kotlin app dev.pixelbridge.app (AGP 9.4.1, Gradle 9.8.2 wrapper, compile/target 37, minSdk 31)
  .../transport/         Frame codec, HMAC auth, Keystore pairing store, ChannelServer (loopback listener)
  .../camera/            Camera2 -> MediaCodec H.264 (surface input), PTS normalised to boottime
  .../audio/ sensors/ status/ notifications/ service/BridgeService.kt (FGS connectedDevice|camera|microphone)
  MainActivity (permissions UI), PairActivity (DUMP-permission-guarded, receives pairing key via `am start`)
windows/PixelBridge.slnx
  PixelBridge.Core/      protocol, AdbClient, ChannelClient (auth), DeviceSession (discovery/forward/reconnect), SensorApiServer (ws), Log
  PixelBridge.Media/     H264Decoder (P/Invoke), VideoPipeline, VirtualCamFeed (shared memory), AudioPipeline (NAudio WASAPI)
  PixelBridge.Native/    C++ DLL: MF H.264 decoder MFT, NV12->BGRA, pb_vcam_start/stop (MFCreateVirtualCamera)
  PixelBridge.VirtualCam/ C++ COM media source loaded by the Windows Frame Server (CLSID {8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60})
  PixelBridge.VCamTest/  C++ test exe: `list`, `system <sec> <bmp>` (reads PixelBridge Camera via MF like any app), `inproc <dll>`
  PixelBridge.Cli/       pbctl.exe: status|pair|sensors|video|audio bounded hardware tests (--vcam --feed --snapshot)
  PixelBridge.App/       WPF tray app (PixelBridge.exe), BEING REDESIGNED (see Status). Personal device details live in HANDOFF.local.md / SPEC.md (gitignored, local only)
  PixelBridge.Tests/     xUnit (still empty, TODO)
macos/                   SwiftUI app (TODO, untested; no Mac available)
protocol/PROTOCOL.md     wire format v1 (24-byte BE header, HMAC handshake per socket)
tools/                   install-vcam.ps1 / uninstall-vcam.ps1 (elevated), screenshot-window.ps1, ui-drive.ps1
artifacts/               gitignored test evidence (snapshots are of the user's room, do not publish)
```

## Build commands
- Toolchain (per-user, already installed): `%LOCALAPPDATA%\PixelBridgeTools\{jdk21,android-sdk,gradle-9.8.2}`.
- APK: `cd android; $env:JAVA_HOME="$env:LOCALAPPDATA\PixelBridgeTools\jdk21"; .\gradlew.bat assembleDebug` → `app/build/outputs/apk/debug/app-debug.apk`
- Install: `adb -s <SERIAL> install -r <apk>` (keeps permissions and pairing).
- Native: `MSBuild.exe windows\PixelBridge.Native\PixelBridge.Native.vcxproj /p:Configuration=Release /p:Platform=x64` (also VirtualCam, VCamTest); output `windows\native-bin\Release\`.
  MSBuild: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe`, toolset v145, SDK 10.0.26100.
- .NET: `dotnet build windows\PixelBridge.App` (.NET SDK 10.0.201). App exe: `windows\PixelBridge.App\bin\Debug\net10.0-windows\win-x64\PixelBridge.exe`.
- After rebuilding VirtualCam.dll, re-run `tools\install-vcam.ps1` (UAC) to copy it to Program Files. Ask the user first.
- **Version lock:** `android/app/build.gradle.kts versionName` must equal `DeviceSession.ExpectedAppVersion` (currently **0.1.1**).

## Status (verified on the real hardware unless noted)
| Area | State |
|---|---|
| Pairing + HMAC auth, 4 channels, heartbeat, reconnect | PASS (unplug/replug auto-reconnects in ~10 s) |
| Sensors (accel/gyro/mag/gravity/rotvec/light/pressure/prox ~60 Hz) + battery | PASS |
| Camera rear/front 720p30 H.264 → MF decode (0 errors) | PASS |
| Virtual camera "PixelBridge Camera (Windows Virtual Camera)" via Frame Server, 30 fps, offline placeholder | PASS (vcamtest system). Windows Camera app: the log shows a consumer attached; the user didn't explicitly confirm |
| Mic PCM 48 kHz → PC, WAV record | PASS. VB-CABLE not installed, so the virtual mic is "requires driver" |
| Sensor WebSocket API ws://127.0.0.1:27200/sensors?token= | built, not yet tested with a client |
| USB tethering (NCM) + ADB at the same time | PASS. Windows "UsbNcm Host Device", internet ~11 Mbps |
| Tethering persists across reboot | PASS (`svc usb setScreenUnlockedFunctions ncm`, i.e. Developer options Default USB configuration = USB tethering) |
| Wi-Fi hotspot auto-enable | NOT POSSIBLE without root (no `cmd wifi start-softap` on this build) |
| Windows default route | Windows itself prefers the phone NCM adapter (metric 25 vs Wi-Fi 60). The user hasn't chosen yet |
| WPF UI | **Redesigned** (WPF .NET 10 `ThemeMode="System"` Fluent, sidebar nav, 11 pages in `PixelBridge.App/Pages/*`, `SettingCard` control, ToggleSwitch/Segment/NavItem styles in App.xaml). Verify pages with `PixelBridge.exe --render-pages <dir>` (offscreen RenderTargetBitmap; works while the desktop is locked; screen capture returns black when locked) |
| Radio bridge (protocol 0x60/0x61/0x62, `RadioBridge.kt`, `DeviceSession.RadioAsync`) | PASS via `pbctl radio <op>`: caps, net.status, wifi.status (SSID now returned), wifi.scan (29 APs), bt.bonded (7), ble.scan.start (live results). ble.connect/read/write/notify written, NOT yet tested on a real peripheral |
| GPS (`LocationStreamer.kt`, LOCATION_SAMPLE 0x44, `LocationFix`, `NmeaServer`) | PASS for network fixes (`pbctl location`). GNSS satellites 0 indoors (needs an outdoor/window retest). Never log coordinates |
| Permissions granted by the user via scrcpy | camera, mic, notifications, BLUETOOTH_SCAN/CONNECT, NEARBY_WIFI, FINE/COARSE location |
| App version | **0.2.1** on both sides |
| Network/Bluetooth/GPS pages in the Windows UI | TODO (part of the Fluent redesign) |
| macOS SwiftUI app + CMIO camera extension | **WRITTEN, NEVER COMPILED** (no Mac). `macos/README.md` has build steps (XcodeGen `project.yml`, `swift test` for PixelBridgeKit). Expect compile fixes on first build |
| Tests, installer, docs, HARDWARE_TEST_REPORT.md | TODO |

## >>> RESUME HERE (session 1 ended mid-task, 2026-10-23) <<<
State: code scrub for the public repo is DONE (serial/paths removed from scripts, tests and Swift; Core is now cross-platform net10.0;
22 tests pass on Windows AND Linux/WSL). NOTHING PUSHED YET. The gh CLI is logged in as `<owner>` (gh.exe at
%LOCALAPPDATA%\Microsoft\WinGet\Packages\GitHub.cli_*\bin\gh.exe). The user wants a **PUBLIC** repo.
Linux Avalonia app: `linux/PixelBridge.Linux` (builds and renders in WSLg; uses ffmpeg, v4l2loopback via pkexec, pactl/pacat virtual mic).
WSL needs libICE/libSM from `~/pb-libs/only` (LD_LIBRARY_PATH) because there's no sudo password.

Do these, in order:
1. **Before pushing:** add SPEC.md and a `HANDOFF.local.md` (move the personal bits there: serial, Telstra SIM, battery history, user paths)
   to .gitignore; scrub this file of the serial/paths. Add LICENSE (MIT), README.md (what it is, features per OS, install, build),
   NOTICE-THIRD-PARTY.md (NAudio MIT, Avalonia MIT, MS VirtualCamera sample architecture reference MIT, scrcpy Apache-2.0 not bundled).
2. Create a fresh ORPHAN branch (`git checkout --orphan public`, commit the scrubbed tree) so old commits containing the serial and
   SPEC never get pushed. Then `gh repo create pixelbridge --public --source . --push` (push the orphan branch as main).
3. Add `.github/workflows/ci.yml`: jobs android (gradle assembleDebug testDebugUnitTest, JDK 21), windows (msbuild native, dotnet test,
   publish, **Inno Setup installer**: write windows/PixelBridge.Installer/PixelBridge.iss with a proper uninstaller that removes the
   HKCU Run key, Start Menu entry and %APPDATA%\PixelBridge on request, and runs uninstall-vcam), linux (dotnet test, publish linux-x64,
   build a .deb with dpkg-deb: depends ffmpeg, adb, pulseaudio-utils|pipewire-pulse, libice6, libsm6; recommends v4l2loopback-dkms, scrcpy;
   apt remove = uninstaller; also tar.gz + uninstall.sh), macos (macos-14: `swift test` in macos/, brew xcodegen, `xcodegen generate`,
   `xcodebuild -scheme PixelBridge CODE_SIGNING_ALLOWED=NO build`). The **Mac code has never been compiled**: iterate on the CI logs
   (`gh run view --log-failed`) until it builds. Release job on tag v*: attach APK, Setup.exe, .deb, tar.gz, unsigned .app zip.
4. Windows app: also add a "Use this phone" button for WrongDevice in Settings, and apply the Linux fixes in CI.

## What the user must provide (ASK, don't work around it)
- **A Mac** (or accept CI-only builds): a real test of the macOS app and camera extension needs a Mac + an **Apple Developer ID** ($99/yr)
  to sign and notarize the CMIO system extension. Without it the Mac webcam can't be installed by end users.
- **A real Linux box** with the phone plugged in (WSL can't see the USB phone without usbipd, which would steal it from Windows), plus sudo
  for `apt install ffmpeg adb v4l2loopback-dkms`, to test the Linux webcam, mic and tether end to end.
- A **BLE peripheral** (e.g. the NuPhy keyboard) for a GATT connect/read/notify test, and an outdoor/window spot for a GNSS satellite fix.
- Optional: VB-CABLE install consent (Windows virtual mic test), and a code-signing cert to avoid SmartScreen warnings.
- Confirm the Windows Camera app test (T09): the log showed a consumer attached, but the user never explicitly confirmed seeing video there.

## Next steps (in order)
1. ~~Fluent redesign~~ DONE. ~~Network page~~ DONE (TetherProbe + route preference via an elevated Set-NetIPInterface). ~~BLE/Wi-Fi bridge~~ DONE. ~~GPS~~ DONE.
2. macOS app in `macos/` (SwiftUI, Swift port of the protocol, VideoToolbox decode, CMIO camera extension). Untested: no Mac available.
3. Test BLE connect/GATT on a real peripheral (e.g. the user's NuPhy Halo75 keyboard, LE). GNSS satellites outdoors.
4. xUnit protocol tests, Android JUnit tests, docs/, HARDWARE_TEST_REPORT.md, BUILD_ALL.ps1, RUN_AND_TEST.ps1, installer.
5. Update protocol/PROTOCOL.md with section 5a LOCATION_SAMPLE (0x44) and section 9 RADIO_* (0x60–0x62). The source of truth for now is `RadioBridge.kt` + `DeviceSession.RadioAsync`.
