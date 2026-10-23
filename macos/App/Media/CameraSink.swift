import CoreMediaIO
import CoreVideo
import Foundation

/// Client side of the PixelBridge Camera extension: finds the virtual device and pushes decoded frames
/// into its sink stream (the CMIO "sink stream" pattern Apple recommends for app → extension video).
final class CameraSink {
    static let deviceName = "PixelBridge Camera"
    private var queue: CMSimpleQueue?
    private var deviceID: CMIODeviceID = 0
    private var streamID: CMIOStreamID = 0
    private var started = false
    private(set) var framesSent = 0
    var isAttached: Bool { started }

    /// Locate the extension's device and its sink (input) stream; returns false until the extension is installed and approved.
    @discardableResult
    func attach() -> Bool {
        if started { return true }
        guard let dev = Self.findDevice(), let sink = Self.sinkStream(of: dev) else { return false }
        deviceID = dev; streamID = sink
        let callback: CMIODeviceStreamQueueAlteredProc = { _, _, _ in }
        var q: Unmanaged<CMSimpleQueue>?
        guard CMIOStreamCopyBufferQueue(streamID, callback, nil, &q) == noErr, let q else { return false }
        queue = q.takeUnretainedValue()
        guard CMIODeviceStartStream(deviceID, streamID) == noErr else { return false }
        started = true
        Log.info("camera extension sink attached")
        return true
    }

    func detach() {
        if started { CMIODeviceStopStream(deviceID, streamID) }
        started = false; queue = nil
    }

    func send(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        guard started || attach(), let queue else { return }
        guard CMSimpleQueueGetCount(queue) < CMSimpleQueueGetCapacity(queue) else { return } // drop when the extension is behind
        var fmt: CMFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixelBuffer, formatDescriptionOut: &fmt)
        guard let fmt else { return }
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: CMClockGetTime(CMClockGetHostTimeClock()), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        CMSampleBufferCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixelBuffer, dataReady: true, makeDataReadyCallback: nil,
                                           refcon: nil, formatDescription: fmt, sampleTiming: &timing, sampleBufferOut: &sample)
        guard let sample else { return }
        CMSimpleQueueEnqueue(queue, element: Unmanaged.passRetained(sample).toOpaque())
        framesSent += 1
    }

    // MARK: CMIO lookups

    private static func findDevice() -> CMIODeviceID? {
        var addr = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIOHardwarePropertyDevices),
                                             mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal), mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        CMIOObjectGetPropertyDataSize(CMIOObjectID(kCMIOObjectSystemObject), &addr, 0, nil, &size)
        var devices = [CMIODeviceID](repeating: 0, count: Int(size) / MemoryLayout<CMIODeviceID>.size)
        var used: UInt32 = 0
        CMIOObjectGetPropertyData(CMIOObjectID(kCMIOObjectSystemObject), &addr, 0, nil, size, &used, &devices)
        return devices.first { name(of: $0) == deviceName }
    }

    private static func name(of obj: CMIOObjectID) -> String? {
        var addr = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIOObjectPropertyName),
                                             mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal), mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var name: Unmanaged<CFString>?
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(obj, &addr, 0, nil, UInt32(MemoryLayout<Unmanaged<CFString>?>.size), &used, &name) == noErr else { return nil }
        return name?.takeRetainedValue() as String?
    }

    /// The extension exposes two streams; the sink is the one whose name ends in "Sink".
    private static func sinkStream(of dev: CMIODeviceID) -> CMIOStreamID? {
        var addr = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIODevicePropertyStreams),
                                             mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal), mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        CMIOObjectGetPropertyDataSize(dev, &addr, 0, nil, &size)
        var streams = [CMIOStreamID](repeating: 0, count: Int(size) / MemoryLayout<CMIOStreamID>.size)
        var used: UInt32 = 0
        CMIOObjectGetPropertyData(dev, &addr, 0, nil, size, &used, &streams)
        return streams.first { name(of: $0)?.hasSuffix("Sink") == true } ?? (streams.count > 1 ? streams[1] : nil)
    }
}
