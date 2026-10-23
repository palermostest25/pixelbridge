import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

/// Annex-B H.264 (as sent by the phone) → VideoToolbox hardware decode → IOSurface-backed BGRA CVPixelBuffers.
final class H264Decoder {
    private var session: VTDecompressionSession?
    private var format: CMVideoFormatDescription?
    private var sps: Data?, pps: Data?
    private(set) var framesOut = 0
    private(set) var errors = 0
    var onFrame: ((CVPixelBuffer, CMTime) -> Void)?

    deinit { invalidate() }

    func invalidate() {
        if let s = session { VTDecompressionSessionInvalidate(s) }
        session = nil
    }

    /// Feed one access unit (or codec-config SPS/PPS) with its device timestamp.
    func decode(_ data: Data, timestampNs: Int64) {
        var slices: [Data] = []
        for nal in Self.splitAnnexB(data) {
            guard let first = nal.first else { continue }
            switch first & 0x1F {
            case 7: sps = nal
            case 8: pps = nal
            case 9, 6: break // AUD / SEI are not needed by VideoToolbox
            default: slices.append(nal)
            }
        }
        if let sps, let pps, needsNewFormat(sps: sps, pps: pps) { makeSession(sps: sps, pps: pps) }
        guard !slices.isEmpty, let session, let format else { return }

        // AVCC: 4-byte big-endian length before every NAL.
        var avcc = Data()
        for s in slices { avcc.appendBE(UInt32(s.count)); avcc.append(s) }
        var block: CMBlockBuffer?
        let len = avcc.count
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: len, blockAllocator: nil,
                                                 customBlockSource: nil, offsetToData: 0, dataLength: len, flags: 0, blockBufferOut: &block) == noErr,
              let block else { errors += 1; return }
        avcc.withUnsafeBytes { _ = CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: len) }
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: CMTime(value: timestampNs, timescale: 1_000_000_000), decodeTimeStamp: .invalid)
        var sizes = [len]
        var sample: CMSampleBuffer?
        guard CMSampleBufferCreateReady(allocator: nil, dataBuffer: block, formatDescription: format, sampleCount: 1, sampleTimingEntryCount: 1,
                                        sampleTimingArray: &timing, sampleSizeEntryCount: 1, sampleSizeArray: &sizes, sampleBufferOut: &sample) == noErr,
              let sample else { errors += 1; return }
        let status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [._EnableAsynchronousDecompression], infoFlagsOut: nil) {
            [weak self] status, _, image, pts, _ in
            guard let self else { return }
            if status == noErr, let image { self.framesOut += 1; self.onFrame?(image, pts) } else { self.errors += 1 }
        }
        if status != noErr { errors += 1; if status == kVTInvalidSessionErr { invalidate() } }
    }

    private func needsNewFormat(sps: Data, pps: Data) -> Bool {
        guard let format, session != nil else { return true }
        var count = 0
        var p: UnsafePointer<UInt8>?
        var size = 0
        CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: 0, parameterSetPointerOut: &p, parameterSetSizeOut: &size,
                                                           parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
        guard let p else { return true }
        return Data(bytes: p, count: size) != sps
    }

    private func makeSession(sps: Data, pps: Data) {
        invalidate()
        var fmt: CMVideoFormatDescription?
        let status = sps.withUnsafeBytes { s in pps.withUnsafeBytes { p in
            let ptrs = [s.bindMemory(to: UInt8.self).baseAddress!, p.bindMemory(to: UInt8.self).baseAddress!]
            let sizes = [sps.count, pps.count]
            return CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: nil, parameterSetCount: 2, parameterSetPointers: ptrs,
                                                                       parameterSetSizes: sizes, nalUnitHeaderLength: 4, formatDescriptionOut: &fmt)
        } }
        guard status == noErr, let fmt else { errors += 1; return }
        format = fmt
        let attrs: [CFString: Any] = [kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA,
                                      kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary]
        var s: VTDecompressionSession?
        let spec: [CFString: Any] = [kVTVideoDecoderSpecification_EnableHardwareAcceleratedVideoDecoder: true]
        if VTDecompressionSessionCreate(allocator: nil, formatDescription: fmt, decoderSpecification: spec as CFDictionary,
                                        imageBufferAttributes: attrs as CFDictionary, outputCallback: nil, decompressionSessionOut: &s) == noErr, let s {
            VTSessionSetProperty(s, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
            session = s
        } else { errors += 1 }
    }

    /// Split an Annex-B byte stream on 00 00 01 / 00 00 00 01 start codes.
    static func splitAnnexB(_ d: Data) -> [Data] {
        let b = [UInt8](d)
        var starts: [(Int, Int)] = [] // (start of NAL, start-code length)
        var i = 0
        while i + 3 <= b.count {
            if b[i] == 0, b[i + 1] == 0 {
                if b[i + 2] == 1 { starts.append((i + 3, 3)); i += 3; continue }
                if i + 4 <= b.count, b[i + 2] == 0, b[i + 3] == 1 { starts.append((i + 4, 4)); i += 4; continue }
            }
            i += 1
        }
        return starts.enumerated().map { idx, s in
            let end = idx + 1 < starts.count ? starts[idx + 1].0 - starts[idx + 1].1 : b.count
            return Data(b[s.0..<end])
        }
    }
}

private extension Data {
    mutating func appendBE(_ v: UInt32) { var be = v.bigEndian; Swift.withUnsafeBytes(of: &be) { append(contentsOf: $0) } }
}
