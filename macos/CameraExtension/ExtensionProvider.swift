import CoreMediaIO
import CoreVideo
import Foundation
import IOKit.audio
import os.log

// PixelBridge Camera: a CoreMediaIO camera extension (macOS 12.3+) with one source stream (what apps see)
// and one sink stream (the PixelBridge app pushes decoded phone frames into it). With no fresh frames it
// shows a dark "camera unavailable" image, never a stale frame.

private let width: Int32 = 1280, height: Int32 = 720, fps = 30
private let log = Logger(subsystem: "dev.pixelbridge.camera", category: "extension")

final class DeviceSource: NSObject, CMIOExtensionDeviceSource {
    private(set) var device: CMIOExtensionDevice!
    private var source: StreamSource!
    private var sink: SinkSource!
    private var timer: DispatchSourceTimer?
    private let timerQueue = DispatchQueue(label: "pb.camera.timer", qos: .userInteractive)
    private var lastSinkFrame: CVPixelBuffer?
    private var lastSinkTime = Date.distantPast
    private var placeholder: CVPixelBuffer?
    private var format: CMFormatDescription!
    private var streamingClients = 0
    private let lock = NSLock()

    init(localizedName: String) {
        super.init()
        device = CMIOExtensionDevice(localizedName: localizedName, deviceID: UUID(uuidString: "8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F61")!,
                                     legacyDeviceID: nil, source: self)
        CMVideoFormatDescriptionCreate(allocator: kCFAllocatorDefault, codecType: kCVPixelFormatType_32BGRA, width: width, height: height,
                                       extensions: nil, formatDescriptionOut: &format)
        let streamFormat = CMIOExtensionStreamFormat(formatDescription: format, maxFrameDuration: CMTime(value: 1, timescale: Int32(fps)),
                                                     minFrameDuration: CMTime(value: 1, timescale: Int32(fps)), validFrameDurations: nil)
        source = StreamSource(localizedName: "PixelBridge Camera", streamID: UUID(), format: streamFormat, device: self)
        sink = SinkSource(localizedName: "PixelBridge Camera Sink", streamID: UUID(), format: streamFormat, device: self)
        try? device.addStream(source.stream)
        try? device.addStream(sink.stream)
        placeholder = Self.makePlaceholder()
    }

    var availableProperties: Set<CMIOExtensionProperty> { [.deviceTransportType, .deviceModel] }

    func deviceProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionDeviceProperties {
        let p = CMIOExtensionDeviceProperties(dictionary: [:])
        if properties.contains(.deviceTransportType) { p.transportType = kIOAudioDeviceTransportTypeVirtual }
        if properties.contains(.deviceModel) { p.model = "PixelBridge (Pixel over USB)" }
        return p
    }

    func setDeviceProperties(_ deviceProperties: CMIOExtensionDeviceProperties) throws {}

    // Source stream: emit at a fixed rate while any client is streaming.
    func startStreaming() {
        lock.lock(); defer { lock.unlock() }
        streamingClients += 1
        guard timer == nil else { return }
        let t = DispatchSource.makeTimerSource(flags: .strict, queue: timerQueue)
        t.schedule(deadline: .now(), repeating: 1.0 / Double(fps), leeway: .milliseconds(2))
        t.setEventHandler { [weak self] in self?.emit() }
        t.resume()
        timer = t
    }

    func stopStreaming() {
        lock.lock(); defer { lock.unlock() }
        streamingClients = max(0, streamingClients - 1)
        if streamingClients == 0 { timer?.cancel(); timer = nil }
    }

    private func emit() {
        lock.lock()
        let fresh = Date().timeIntervalSince(lastSinkTime) < 1.0
        let pb = fresh ? lastSinkFrame : placeholder
        lock.unlock()
        guard let pb else { return }
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: Int32(fps)),
                                        presentationTimeStamp: CMClockGetTime(CMClockGetHostTimeClock()), decodeTimeStamp: .invalid)
        var fmt: CMFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pb, formatDescriptionOut: &fmt)
        var sample: CMSampleBuffer?
        guard let fmt, CMSampleBufferCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pb, dataReady: true, makeDataReadyCallback: nil,
                                                          refcon: nil, formatDescription: fmt, sampleTiming: &timing, sampleBufferOut: &sample) == noErr,
              let sample else { return }
        source.stream.send(sample, discontinuity: [], hostTimeInNanoseconds: UInt64(timing.presentationTimeStamp.seconds * 1e9))
    }

    // Sink stream: frames from the PixelBridge app.
    func receive(_ sample: CMSampleBuffer) {
        guard let pb = CMSampleBufferGetImageBuffer(sample) else { return }
        lock.lock()
        lastSinkFrame = pb
        lastSinkTime = Date()
        lock.unlock()
    }

    func sinkStopped() {
        lock.lock(); lastSinkFrame = nil; lastSinkTime = .distantPast; lock.unlock()
    }

    private static func makePlaceholder() -> CVPixelBuffer? {
        var pb: CVPixelBuffer?
        CVPixelBufferCreate(kCFAllocatorDefault, Int(width), Int(height), kCVPixelFormatType_32BGRA,
                            [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &pb)
        guard let pb else { return nil }
        CVPixelBufferLockBaseAddress(pb, [])
        let base = CVPixelBufferGetBaseAddress(pb)!.assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(pb)
        for y in 0..<Int(height) {
            for x in 0..<Int(width) {
                let p = base + y * stride + x * 4
                // Dark background with a crossed-out camera outline in the centre.
                let cx = x - Int(width) / 2, cy = y - Int(height) / 2
                let onBox = (abs(cx) <= 128 && abs(cy) <= 72) && (abs(cx) >= 124 || abs(cy) >= 68)
                let onLens = abs(Int((Double(cx * cx + cy * cy)).squareRoot()) - 28) <= 2
                let onSlash = abs(cy - cx * 9 / 16) <= 2 && abs(cx) <= 170
                let v: UInt8 = (onBox || onLens || onSlash) ? 200 : 32
                p[0] = v; p[1] = v; p[2] = v; p[3] = 255
            }
        }
        CVPixelBufferUnlockBaseAddress(pb, [])
        return pb
    }
}

final class StreamSource: NSObject, CMIOExtensionStreamSource {
    private(set) var stream: CMIOExtensionStream!
    private let format: CMIOExtensionStreamFormat
    private unowned let device: DeviceSource

    init(localizedName: String, streamID: UUID, format: CMIOExtensionStreamFormat, device: DeviceSource) {
        self.format = format; self.device = device
        super.init()
        stream = CMIOExtensionStream(localizedName: localizedName, streamID: streamID, direction: .source, clockType: .hostTime, source: self)
    }

    var formats: [CMIOExtensionStreamFormat] { [format] }
    var availableProperties: Set<CMIOExtensionProperty> { [.streamActiveFormatIndex, .streamFrameDuration] }

    func streamProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionStreamProperties {
        let p = CMIOExtensionStreamProperties(dictionary: [:])
        if properties.contains(.streamActiveFormatIndex) { p.activeFormatIndex = 0 }
        if properties.contains(.streamFrameDuration) { p.frameDuration = CMTime(value: 1, timescale: Int32(fps)) }
        return p
    }

    func setStreamProperties(_ streamProperties: CMIOExtensionStreamProperties) throws {}
    func authorizedToStartStream(for client: CMIOExtensionClient) -> Bool { true }
    func startStream() throws { device.startStreaming() }
    func stopStream() throws { device.stopStreaming() }
}

final class SinkSource: NSObject, CMIOExtensionStreamSource {
    private(set) var stream: CMIOExtensionStream!
    private let format: CMIOExtensionStreamFormat
    private unowned let device: DeviceSource
    private var client: CMIOExtensionClient?
    private var running = false

    init(localizedName: String, streamID: UUID, format: CMIOExtensionStreamFormat, device: DeviceSource) {
        self.format = format; self.device = device
        super.init()
        stream = CMIOExtensionStream(localizedName: localizedName, streamID: streamID, direction: .sink, clockType: .hostTime, source: self)
    }

    var formats: [CMIOExtensionStreamFormat] { [format] }
    var availableProperties: Set<CMIOExtensionProperty> { [.streamActiveFormatIndex, .streamFrameDuration, .streamSinkBufferQueueSize,
                                                           .streamSinkBuffersRequiredForStartup, .streamSinkBufferUnderrunCount, .streamSinkEndOfData] }

    func streamProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionStreamProperties {
        let p = CMIOExtensionStreamProperties(dictionary: [:])
        if properties.contains(.streamActiveFormatIndex) { p.activeFormatIndex = 0 }
        if properties.contains(.streamFrameDuration) { p.frameDuration = CMTime(value: 1, timescale: Int32(fps)) }
        if properties.contains(.streamSinkBufferQueueSize) { p.sinkBufferQueueSize = 4 }
        if properties.contains(.streamSinkBuffersRequiredForStartup) { p.sinkBuffersRequiredForStartup = 1 }
        return p
    }

    func setStreamProperties(_ streamProperties: CMIOExtensionStreamProperties) throws {}

    func authorizedToStartStream(for client: CMIOExtensionClient) -> Bool {
        self.client = client
        return true
    }

    func startStream() throws {
        running = true
        pull()
    }

    func stopStream() throws {
        running = false
        device.sinkStopped()
    }

    private func pull() {
        guard running, let client else { return }
        stream.consumeSampleBuffer(from: client) { [weak self] sample, seq, _, _, _ in
            guard let self else { return }
            if let sample {
                self.device.receive(sample)
                self.stream.notifyScheduledOutputChanged(CMIOExtensionScheduledOutput(sequenceNumber: seq, hostTimeInNanoseconds: UInt64(CMClockGetTime(CMClockGetHostTimeClock()).seconds * 1e9)))
            }
            DispatchQueue.global(qos: .userInteractive).asyncAfter(deadline: .now() + 0.005) { self.pull() }
        }
    }
}

final class ProviderSource: NSObject, CMIOExtensionProviderSource {
    private(set) var provider: CMIOExtensionProvider!
    private var deviceSource: DeviceSource!

    init(clientQueue: DispatchQueue?) {
        super.init()
        provider = CMIOExtensionProvider(source: self, clientQueue: clientQueue)
        deviceSource = DeviceSource(localizedName: "PixelBridge Camera")
        do { try provider.addDevice(deviceSource.device) } catch { log.error("addDevice failed: \(error.localizedDescription)") }
    }

    func connect(to client: CMIOExtensionClient) throws {}
    func disconnect(from client: CMIOExtensionClient) {}
    var availableProperties: Set<CMIOExtensionProperty> { [.providerManufacturer] }

    func providerProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionProviderProperties {
        let p = CMIOExtensionProviderProperties(dictionary: [:])
        if properties.contains(.providerManufacturer) { p.manufacturer = "PixelBridge" }
        return p
    }

    func setProviderProperties(_ providerProperties: CMIOExtensionProviderProperties) throws {}
}
