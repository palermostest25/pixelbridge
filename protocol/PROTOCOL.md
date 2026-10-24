# PixelBridge USB protocol v1.0

All traffic travels over ADB-forwarded TCP sockets (`adb -s SERIAL forward tcp:H tcp:D`).
The phone listens **only** on `127.0.0.1`. Each logical channel is its own TCP connection, so a
slow video socket can never block control.

| Device port | Channel     | Direction            | Max payload |
|-------------|-------------|----------------------|-------------|
| 27183       | `control`   | both                 | 65 536 B    |
| 27184       | `video`     | device → host (+IDR) | 2 097 152 B |
| 27185       | `audio`     | device → host        | 16 384 B    |
| 27186       | `telemetry` | device → host        | 65 536 B    |
| 27187       | `proxy`     | reserved (Tier 2)    | n/a         |

## 1. Frame format

Every message is one frame. All integers are **big-endian (network order)**.

| Offset | Size | Field            | Notes |
|--------|------|------------------|-------|
| 0      | 4    | `payloadLength`  | uint32, bytes after the 24-byte header |
| 4      | 1    | `version`        | uint8, `1` |
| 5      | 1    | `type`           | uint8, see table below |
| 6      | 2    | `flags`          | uint16 bit-field |
| 8      | 8    | `sequence`       | uint64, per socket per direction, starts at 0, +1 per frame |
| 16     | 8    | `timestampNs`    | int64. Device frames: `SystemClock.elapsedRealtimeNanos()` domain unless stated. Host frames: host monotonic ns (`Stopwatch`). 0 = not applicable |
| 24     | n    | payload          | |

A receiver **must** close the socket if `version != 1`, `payloadLength` exceeds the channel limit,
or the sequence number is not exactly previous+1. No fragmentation: one frame = one message.

Flags: bit0 `KEYFRAME`, bit1 `CODEC_CONFIG`, bit2 `END_OF_STREAM`. Other bits must be 0.

JSON payloads are UTF-8 objects. Unknown fields are ignored; missing required fields are a protocol error.

## 2. Message types

| Type | Name              | Payload | Channel |
|------|-------------------|---------|---------|
| 0x01 | `HELLO`           | JSON | all (device → host, first frame; carries the auth challenge) |
| 0x02 | `CAPABILITIES`    | JSON | control |
| 0x04 | `AUTH_RESPONSE`   | JSON | all (host → device) |
| 0x05 | `AUTH_OK`         | JSON | all |
| 0x06 | `AUTH_FAIL`       | JSON | all, then close |
| 0x10 | `START_STREAM`    | JSON `{kind, options}` | control |
| 0x11 | `STOP_STREAM`     | JSON `{kind}` | control |
| 0x12 | `STREAM_STARTED`  | JSON `{kind, ...}` | control |
| 0x13 | `STREAM_STOPPED`  | JSON `{kind, reason}` | control |
| 0x14 | `STREAM_ERROR`    | JSON `{kind, code, message}` | control |
| 0x15 | `STATUS`          | JSON `{modules:{name:state}}` | control |
| 0x20 | `VIDEO_CONFIG`    | JSON | video |
| 0x21 | `VIDEO_FRAME`     | H.264 Annex-B access unit | video |
| 0x22 | `REQUEST_IDR`     | empty | control (host → device) |
| 0x30 | `AUDIO_CONFIG`    | JSON | audio |
| 0x31 | `AUDIO_FRAME`     | PCM samples | audio |
| 0x40 | `SENSOR_SAMPLE`   | binary, see §5 | telemetry |
| 0x41 | `BATTERY_STATUS`  | JSON | telemetry |
| 0x42 | `NETWORK_STATUS`  | JSON | telemetry |
| 0x43 | `NOTIFICATION_EVENT` | JSON | telemetry |
| 0x50 | `HEARTBEAT`       | JSON `{hostNs}` | control (host → device) |
| 0x51 | `PONG`            | JSON `{hostNs, deviceNs}` | control |
| 0x7F | `ERROR`           | JSON `{code, message, recoverable}` | any |

Module states: `ready`, `streaming`, `waiting-permission`, `in-use`, `unsupported`, `failed`, `paused`, `off`.

## 3. Pairing and authentication

**Pairing (one time).** The host generates a random 32-byte key `K` and delivers it through the
already-authorised ADB transport:

```
adb -s SERIAL shell am start -n dev.pixelbridge.app/.PairActivity --es key <base64url K> --es host <name>
```

`PairActivity` is exported but guarded by `android:permission="android.permission.DUMP"`, which only
the ADB shell and system hold, so other phone apps cannot re-pair. The phone encrypts `K` with an
AES-GCM key in the Android Keystore and shows "Paired with <host>". The host stores `K` with DPAPI
(CurrentUser) in `%APPDATA%\PixelBridge\pairing.bin`. Unpair = delete both copies.

**Per-socket handshake** (every channel, 5 s timeout, nothing else is sent before success):

1. device → `HELLO` `{protocolMajor:1, protocolMinor:0, channel, serverNonce:b64(32 random bytes), appVersion, deviceModel, paired:bool}`
2. host → `AUTH_RESPONSE` `{channel, clientNonce:b64(32), mac:b64(HMAC-SHA256(K, "PB-AUTH-v1|"+channel+"|"+serverNonce+"|"+clientNonce))}`
3. device verifies (constant-time) → `AUTH_OK` `{mac:b64(HMAC-SHA256(K, "PB-AUTH-OK-v1|"+channel+"|"+serverNonce+"|"+clientNonce))}`
   which the host verifies (mutual authentication), or `AUTH_FAIL {code}` and close.

`serverNonce`/`clientNonce` strings are standard base64 exactly as transmitted. A fresh server nonce
per socket defeats replay. A newly authenticated socket on a channel replaces the previous one.

## 4. Control

After `AUTH_OK` on control the device sends `CAPABILITIES`:

```json
{"cameras":[{"id":"0","facing":"back","sizes":["1280x720","1920x1080"],"fpsRanges":["30-30"]}],
 "sensors":[{"type":4,"name":"gyroscope","vendor":"...","maxRateHz":400}],
 "audio":{"micPermission":true}, "cameraPermission":true, "locationAvailable":false,
 "notificationAccess":false, "android":{"sdk":37,"release":"17"}}
```

`START_STREAM` kinds and options:

* `sensors` `{rateHz:5|10|50|100, types?:[Android sensor type ints]}`. Battery monitoring is independent and starts with the authenticated control link.
* `video` `{cameraId:"0", width:1280, height:720, fps:30, bitrate:4000000, zoomRatio:1.0, stabilization:"auto"}`
  Optional camera controls default to `zoomRatio:1.0` and `stabilization:"auto"`.
  Stabilization accepts `auto`, `optical`, `electronic`, or `off`; unsupported modes are never enabled.
  Auto requests OIS on the main rear lens and electronic stabilization when OIS is unavailable (including ultrawide).
  `video` with `{updateOnly:true, zoomRatio, stabilization}` changes the repeating capture request without restarting the encoder.
  `CAPABILITIES.cameras[]` additionally reports `minZoom`, `maxZoom`, `opticalStabilization`, `videoStabilization`, and `physicalIds`.
  Zoom below 1× on a logical rear camera selects its wider physical lens where supported.
* `audio` `{rateHz:48000}`

`HEARTBEAT` every 1 s; device answers `PONG` with `deviceNs = elapsedRealtimeNanos`. The host
estimates offset = `deviceNs - (t0+t1)/2` over the min-RTT sample of the last 16.
Host treats 5 s without PONG as link loss.

## 5. Telemetry

`SENSOR_SAMPLE` payload, with header `timestampNs` = `SensorEvent.timestamp`:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | int32 Android sensor type (`Sensor.TYPE_*`) |
| 4 | 1 | int8 accuracy (`SensorManager.SENSOR_STATUS_*`) |
| 5 | 1 | uint8 value count `n` (≤ 16) |
| 6 | 2 | reserved, 0 |
| 8 | 4n | float32 values in Android units (m/s², rad/s, µT, lux, cm) and Android device axes |

`BATTERY_STATUS` `{percent, tempC, charging, plugged, voltageMv, currentMa, health, status}`.

## 6. Video

`VIDEO_CONFIG` `{codec:"h264", width, height, fps, bitrate, cameraId, facing, sensorOrientation, timestampSource:"boottime"}`

Optional live camera metadata: `zoomRatio`, `stabilization`, `rotationDegrees` (clockwise rotation needed for upright output),
`deviceOrientation`, `appliedZoomRatio`, `opticalStabilizationActive`, `videoStabilizationActive`, and `activePhysicalCameraId`.
Applied values come from Camera2 capture results. This config is refreshed after control/orientation changes and once per second.
Hosts must tolerate additional fields. PixelBridge for macOS applies rotation and mirroring before both preview and webcam output;
portrait content is fitted into the 16:9 output without stretching or discarding the field of view.
is sent before the first frame and on every reconfiguration. The first `VIDEO_FRAME` carries flag
`CODEC_CONFIG` (SPS+PPS, Annex-B). Frames with flag `KEYFRAME` are IDR. Timestamps are codec
presentation times converted to the device `elapsedRealtimeNanos` (boottime) clock. The device drops non-keyframes when its 8-frame queue is full and then
forces an IDR, and the host may send `REQUEST_IDR` at any time.

## 7. Audio

`AUDIO_CONFIG` `{rateHz:48000, channels:1, encoding:"pcm_s16le", frameMs:20}`. `AUDIO_FRAME`
payload is little-endian PCM16 (the only little-endian data in the protocol, matching both
platforms' native layout). Header timestamp = estimated capture time of the first sample.

## 8. Errors

`ERROR` / `STREAM_ERROR` codes: `permission`, `in-use`, `unsupported`, `codec`, `protocol`, `auth`,
`internal`, `thermal`.

## 5a. Location (added in app 0.2.1)

`START_STREAM {kind:"location", options:{intervalMs}}` starts GNSS + network location, only if the
user granted location on the phone. Fixes arrive on telemetry as `LOCATION_SAMPLE` (0x44, JSON, header
timestamp = `Location.elapsedRealtimeNanos`):

```json
{"lat":0.0,"lon":0.0,"altM":10.0,"accM":12.0,"speedMps":0.0,"bearingDeg":null,"timeMs":1761200000000,
 "provider":"gps|network|fused","stale":false,"ageMs":120,"satellitesUsed":7,"satellitesVisible":21,"meanCn0":28.4}
```

Coordinates are sensitive: neither side logs them.

## 9. Phone radios (added in app 0.2.0)

Request/response on control, events on telemetry, all JSON:

* `RADIO_REQUEST` 0x60 host→device `{id:int, op:string, args:{}}`
* `RADIO_RESPONSE` 0x61 device→host `{id, ok:true, result:{}}` or `{id, ok:false, error:code, message}`
* `RADIO_EVENT` 0x62 device→host `{event:string, ...}`

| op | args | result |
|---|---|---|
| `caps` | — | `{bluetoothPresent, bluetoothOn, bleSupported, wifiOn, locationOn, permissions:{bluetoothScan, bluetoothConnect, fineLocation, nearbyWifi}}` |
| `net.status` | — | `{networks:[{transports:[wifi\|cellular\|vpn\|ethernet\|bluetooth], interface, validated, metered, downKbps, upKbps, default}]}` |
| `wifi.status` | — | `{enabled, connected, rssi, linkMbps, frequencyMhz, standard, ssid\|null}` |
| `wifi.scan` | — | `{freshScan, results:[{ssid, bssid, rssi, frequencyMhz, security, ageMs}]}` (Android throttles to 4 scans / 2 min) |
| `bt.bonded` | — | `{devices:[{name, address, type:classic\|le\|dual}]}` |
| `ble.scan.start` | `{durationMs ≤ 30000}` | `{durationMs}`, then `ble.scanResult` events and finally `ble.scanStopped` |
| `ble.scan.stop` | — | `{}` |
| `ble.connect` | `{address}` | `{state:"connecting"}`, then `ble.connectionState` and `ble.services` events |
| `ble.disconnect` | `{address}` | `{}` |
| `ble.read` | `{address, service, characteristic}` | `{valueB64}` |
| `ble.write` | `{address, service, characteristic, valueB64, withResponse}` | `{}` |
| `ble.notify` | `{address, service, characteristic, enable}` | `{}`, then `ble.notification` events `{address, service, characteristic, valueB64}` |

Events: `ble.scanResult {address, name, rssi, connectable, serviceUuids[], manufacturerIds[], txPower}`
(≤ 2/s per device), `ble.scanStopped`, `ble.connectionState {address, state, status}`,
`ble.services {address, services:[{uuid, characteristics:[{uuid, properties[]}]}]}`, `ble.notification`.
Error codes: `permission`, `unavailable`, `not-connected`, `not-found`, `busy`, `timeout`, `gatt`, `limit`, `paused`, `unsupported`.
At most 4 GATT connections. All are dropped, and scans stopped, when the host disconnects.


## 10. Phone workspace (app 0.4.0)

These operations use the authenticated `RADIO_REQUEST` / `RADIO_RESPONSE` envelope from section 9. Android checks each permission at the time of use. No outbound operation is retried automatically. Results are bounded below the 64 KiB control-frame limit.

| op | args | result |
|---|---|---|
| `phone.status` | — | `{smsSupported,dialer,permissions:{readSms,sendSms,contacts,callLog,callPhone,phoneState},accounts:[{id,name,default}],defaultAccountId,calls:{calls:[],muted,route,endpoints:[],canAddCall},callAudio:"phone",notificationReplies}` |
| `phone.watch` | `{enabled}` | Same as status; observes SMS, contacts and call-log changes only while enabled |
| `contacts.list` | `{query?,limit?:1..100}` | `{contacts:[{id,name,number}]}` |
| `sms.threads` | — | `{threads:[{id,address,preview,dateMs,incoming,unread}],format:"sms"}`; up to 60 threads from 2000 recent messages |
| `sms.messages` | `{threadId,before?:dateMs}` | `{threadId,messages:[{id,address,body,dateMs,incoming,type,status}]}`; up to 75, newest first |
| `sms.send` | `{number,text,token,subscriptionId?}` | Send receipt; token is 16–80 letters, digits, underscores or hyphens (desktop uses 32 hexadecimal characters); at most 12000 characters |
| `sms.receipt` | `{token}` | `{state:"queued|sent|delivered|failed|unknown",token?,error?}` |
| `phone.history` | — | `{calls:[{id,number,name,dateMs,durationSeconds,type}]}`; up to 75 |
| `phone.call` | `{number,accountId?}` | `{state:"requested"}`; explicit SIM required if multiple lines have no default; account IDs come from status |
| `phone.answer`, `phone.end`, `phone.reject`, `phone.hold`, `phone.unhold`, `phone.merge`, `phone.swap`, `phone.split` | `{callId}` | `{state:"requested"}`; requires default Phone role and the relevant call capability |
| `phone.dtmf` | `{callId,digit:"0..9|*|#"}` | Same; tone stops after a short interval |
| `phone.mute` | `{enabled}` | Same |
| `phone.route` | `{endpointId?,route?:"phone|speaker|bluetooth|wired|streaming"}` | Same; endpoint IDs come from the current snapshot; unavailable/stale endpoints are rejected |
| `notifications.list` | — | `{notifications:[{key,appLabel,senderDisplay,text,redacted,canReply,postedMs}]}` |
| `notifications.reply` | `{key,text}` | `{state:"requested"}`; requires notification access, phone content opt-in and an active reply action |
| `notifications.dismiss` | `{key}` | `{}` |
| `tools.status` | — | `{volume,maximumVolume,flashlight,uptimeMs,vibrator}` |
| `tools.flashlight` | `{enabled}` | Tools status; camera permission required |
| `tools.volume` | `{value}` | Tools status; media volume only |
| `tools.find` | `{enabled?:true}` | `{playing,maximumSeconds:15}` |
| `tools.vibrate` | — | `{}`; half-second vibration |
| `tools.apps` | — | `{apps:[{name,component}]}`; up to 150 launcher entries |

Events: `phone.changed` coalesces provider changes; `phone.calls` carries the current calls/mute/route snapshot; `phone.error` carries asynchronous call failures; `sms.receipt` carries multipart send/delivery progress; `notifications.removed {key}` removes stale entries. Call audio remains on the phone. Sending the same SMS token twice never submits it again, and changing its payload is rejected. The phone retains at most 150 receipts/fingerprints without message bodies. If a send acknowledgement is lost, query its receipt; do not invent a new token to retry it automatically.

From app 0.4.1, call snapshots include `endpoints:[{id,name,route,selected}]`, `canAddCall`, and per-call `canMerge`, `canSwap`, `isConference`. Android 14+ uses `InCallService` endpoint/mute callbacks and `requestCallEndpointChange`; hosts display confirmed state, not an optimistic toggle. An explicit stale `callId` never falls back to a different call. Outgoing CallStyle notifications are attached to a `phoneCall` foreground service; an OS rejection falls back to an ordinary notification and must not crash the dialer.

The desktop uses serial-scoped ADB for explicit app launch, display, file transfer and confirmed reboot actions. The protected `.phone.SetupActivity` accepts only the named setup features `communications`, `calling` and `notifications`; Android owns the resulting permission and role dialogs. Setup and pairing entry points require the shell/system `DUMP` permission.
