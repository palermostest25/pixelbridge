import CoreMediaIO
import CoreVideo
import Foundation
import PixelBridgeKit
import VideoToolbox

/// Sends decoded phone frames to a native camera-extension sink. Prefers PixelBridge's
/// extension, with the already-installed OBS 30+ camera as an interoperable output.
final class CameraSink {
    static let deviceName = "PixelBridge Camera"
    static let obsName = "OBS Virtual Camera"
    private static let obsUID = "7626645E-4425-469E-9D8B-97E0FA59AC75"
    private var queue: CMSimpleQueue?
    private var deviceID: CMIODeviceID = 0
    private var streamID: CMIOStreamID = 0
    private var started = false
    private var usesOBS = false
    private var transfer: VTPixelTransferSession?
    private var pool: CVPixelBufferPool?
    private var nextAttachAttempt = Date.distantPast
    private(set) var framesSent = 0
    private(set) var outputName: String?
    private(set) var lastError: String?
    var isAttached: Bool { started }

    deinit { detach() }

    static func availableOutputName() -> String? { findDevice()?.name }

    @discardableResult
    func attach() -> Bool {
        if started { return true }
        guard Date() >= nextAttachAttempt else { return false }
        nextAttachAttempt = Date().addingTimeInterval(2)
        guard let target = Self.findDevice(), let stream = Self.sinkStream(of: target.id) else {
            lastError = "No compatible webcam output is available."
            return false
        }
        var copiedQueue: Unmanaged<CMSimpleQueue>?
        let callback: CMIODeviceStreamQueueAlteredProc = { _, _, _ in }
        let copied = CMIOStreamCopyBufferQueue(stream, callback, nil, &copiedQueue)
        guard copied == noErr, let copiedQueue else {
            lastError = "Could not open the webcam output (\(copied))."
            return false
        }
        let ownedQueue = copiedQueue.takeRetainedValue()
        let status = CMIODeviceStartStream(target.id, stream)
        guard status == noErr else {
            lastError = "Webcam output is busy or unavailable (\(status)). Stop OBS's virtual camera and try again."
            return false
        }
        deviceID = target.id; streamID = stream; queue = ownedQueue
        usesOBS = target.name == Self.obsName
        outputName = target.name
        started = true; lastError = nil
        Log.info("webcam output attached: \(target.name)")
        return true
    }

    func detach() {
        if started { CMIODeviceStopStream(deviceID, streamID) }
        started = false; queue = nil; pool = nil
        if let transfer { VTPixelTransferSessionInvalidate(transfer) }
        transfer = nil
        nextAttachAttempt = .distantPast
    }

    func send(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        guard started || attach(), let queue else { return }
        guard CMSimpleQueueGetCount(queue) < CMSimpleQueueGetCapacity(queue) else { return }
        let output: CVPixelBuffer
        if usesOBS {
            // OBS's sink advertises UYVY at 1280×720. VideoToolbox handles scaling and
            // conversion from decoder BGRA without a Python process or screen capture.
            guard let converted = convertForOBS(pixelBuffer) else { return }
            output = converted
        } else { output = pixelBuffer }
        var format: CMFormatDescription?
        guard CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: output, formatDescriptionOut: &format) == noErr,
              let format else { return }
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 30), presentationTimeStamp: CMClockGetTime(CMClockGetHostTimeClock()), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        guard CMSampleBufferCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: output, dataReady: true, makeDataReadyCallback: nil,
                                                refcon: nil, formatDescription: format, sampleTiming: &timing, sampleBufferOut: &sample) == noErr,
              let sample else { return }
        let retained = Unmanaged.passRetained(sample)
        if CMSimpleQueueEnqueue(queue, element: retained.toOpaque()) == noErr {
            framesSent += 1; lastError = nil
        } else { retained.release() }
    }

    private func convertForOBS(_ source: CVPixelBuffer) -> CVPixelBuffer? {
        if transfer == nil {
            guard VTPixelTransferSessionCreate(allocator: kCFAllocatorDefault, pixelTransferSessionOut: &transfer) == noErr else {
                lastError = "Could not prepare webcam video conversion."; return nil
            }
        }
        if pool == nil {
            let attributes: [CFString: Any] = [kCVPixelBufferWidthKey: 1280, kCVPixelBufferHeightKey: 720,
                                               kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_422YpCbCr8,
                                               kCVPixelBufferIOSurfacePropertiesKey: [:]]
            guard CVPixelBufferPoolCreate(nil, nil, attributes as CFDictionary, &pool) == kCVReturnSuccess else {
                lastError = "Could not allocate webcam video buffers."; return nil
            }
        }
        guard let pool, let transfer else { return nil }
        var output: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &output) == kCVReturnSuccess, let output else { return nil }
        let status = VTPixelTransferSessionTransferImage(transfer, from: source, to: output)
        guard status == noErr else {
            lastError = "Webcam video conversion failed (\(status))."; return nil
        }
        return output
    }

    private static func findDevice() -> (id: CMIODeviceID, name: String)? {
        var address = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIOHardwarePropertyDevices),
                                               mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal), mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        guard CMIOObjectGetPropertyDataSize(CMIOObjectID(kCMIOObjectSystemObject), &address, 0, nil, &size) == noErr, size > 0 else { return nil }
        var devices = [CMIODeviceID](repeating: 0, count: Int(size) / MemoryLayout<CMIODeviceID>.size)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(CMIOObjectID(kCMIOObjectSystemObject), &address, 0, nil, size, &used, &devices) == noErr else { return nil }
        if let native = devices.first(where: { name(of: $0) == deviceName }) { return (native, deviceName) }
        if let obs = devices.first(where: { stringProperty($0, kCMIODevicePropertyDeviceUID) == obsUID }) { return (obs, obsName) }
        return nil
    }

    private static func stringProperty(_ object: CMIOObjectID, _ selector: Int) -> String? {
        var address = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(selector),
                                               mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal), mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var value: Unmanaged<CFString>?
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(object, &address, 0, nil, UInt32(MemoryLayout<Unmanaged<CFString>?>.size), &used, &value) == noErr else { return nil }
        return value?.takeRetainedValue() as String?
    }

    private static func name(of object: CMIOObjectID) -> String? { stringProperty(object, kCMIOObjectPropertyName) }

    private static func sinkStream(of device: CMIODeviceID) -> CMIOStreamID? {
        var address = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIODevicePropertyStreams),
                                               mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal), mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        guard CMIOObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr, size > 0 else { return nil }
        var streams = [CMIOStreamID](repeating: 0, count: Int(size) / MemoryLayout<CMIOStreamID>.size)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(device, &address, 0, nil, size, &used, &streams) == noErr else { return nil }
        return streams.first { name(of: $0)?.hasSuffix("Sink") == true }
    }
}
