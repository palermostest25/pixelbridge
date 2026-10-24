import CoreImage
import CoreVideo
import Foundation
import ImageIO

/// Applies the same geometry to the preview and the webcam output. Portrait frames
/// fit inside the 16:9 output with side bars, preserving the entire field of view.
final class VideoFrameProcessor {
    private let context = CIContext(options: [.cacheIntermediates: false])
    private let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    private var pool: CVPixelBufferPool?

    func process(_ source: CVPixelBuffer, rotation: Int, mirrored: Bool) -> CVPixelBuffer? {
        if rotation % 360 == 0 && !mirrored && CVPixelBufferGetWidth(source) == 1280 && CVPixelBufferGetHeight(source) == 720 { return source }
        let orientation: CGImagePropertyOrientation
        switch ((rotation % 360) + 360) % 360 {
        case 90: orientation = .right
        case 180: orientation = .down
        case 270: orientation = .left
        default: orientation = .up
        }
        var image = CIImage(cvPixelBuffer: source).oriented(orientation)
        image = image.transformed(by: CGAffineTransform(translationX: -image.extent.minX, y: -image.extent.minY))
        if mirrored {
            image = image.transformed(by: CGAffineTransform(a: -1, b: 0, c: 0, d: 1, tx: image.extent.width, ty: 0))
        }
        let canvas = CGRect(x: 0, y: 0, width: 1280, height: 720)
        let scale = min(canvas.width / image.extent.width, canvas.height / image.extent.height)
        image = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        image = image.transformed(by: CGAffineTransform(translationX: (canvas.width - image.extent.width) / 2,
                                                       y: (canvas.height - image.extent.height) / 2))
        let composed = image.composited(over: CIImage(color: .black).cropped(to: canvas))
        if pool == nil {
            let attributes: [CFString: Any] = [kCVPixelBufferWidthKey: 1280, kCVPixelBufferHeightKey: 720,
                                              kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA,
                                              kCVPixelBufferIOSurfacePropertiesKey: [:]]
            guard CVPixelBufferPoolCreate(nil, nil, attributes as CFDictionary, &pool) == kCVReturnSuccess else { return nil }
        }
        guard let pool else { return nil }
        var output: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &output) == kCVReturnSuccess, let output else { return nil }
        context.render(composed, to: output, bounds: canvas, colorSpace: colorSpace)
        return output
    }
}
