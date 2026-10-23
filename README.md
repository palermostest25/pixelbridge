# PixelBridge

Use an Android phone (built and tested on a Pixel 7 with a broken screen) as a **webcam, microphone, motion sensors, GPS, Bluetooth LE radio and USB network adapter** for your computer, over **one USB cable**. No root.

| | Windows | Linux | macOS |
|---|---|---|---|
| App | WPF Fluent (tested) | Avalonia (builds and runs; phone untested) | SwiftUI (**not yet compiled**) |
| Webcam | Media Foundation virtual camera (tested) | v4l2loopback | CoreMediaIO extension |
| Mic | VB-CABLE | PipeWire/Pulse virtual source | BlackHole |
| Internet | USB tethering (NCM, tested) | cdc_ncm | NCM |

See `HANDOFF.md` for status and next steps, `protocol/PROTOCOL.md` for the wire format, and `BUILD_ALL.ps1` / `macos/README.md` for building.
