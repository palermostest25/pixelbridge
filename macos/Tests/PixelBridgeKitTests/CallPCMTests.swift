import XCTest
@testable import PixelBridgeKit

final class CallPCMTests: XCTestCase {
    func testRealMicrophoneRatesAcrossIrregularCallbacks() {
        for rate in [44100.0, 48000.0] {
            let input = (0..<Int(rate)).map { Float(sin(2 * .pi * 500 * Double($0) / rate) * 0.25) }
            var whole = CallPCMResampler(inputRate: rate)
            let expected = whole.convert(input)
            var streaming = CallPCMResampler(inputRate: rate), actual: [Int16] = []
            for start in stride(from: 0, to: input.count, by: 997) { actual += streaming.convert(Array(input[start..<min(start + 997, input.count)])) }
            XCTAssertEqual(actual.count, expected.count)
            XCTAssertLessThanOrEqual(abs(actual.count - 16000), 1)
            XCTAssertTrue(zip(actual, expected).allSatisfy { pair in abs(Int(pair.0) - Int(pair.1)) <= 1 })
            XCTAssertGreaterThan(actual.map { abs(Int($0)) }.max()!, 8000)
        }
    }
    func testClippingAndInvalidSamplesCannotCrashCapture() {
        var converter = CallPCMResampler(inputRate: 16000)
        XCTAssertEqual(converter.convert([2, -2, .nan, 0]), [32767, -32768, 0])
        var invalid = CallPCMResampler(inputRate: 0)
        XCTAssertTrue(invalid.convert([1, 1]).isEmpty)
    }
}
