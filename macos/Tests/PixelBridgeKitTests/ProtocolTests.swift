import XCTest
@testable import PixelBridgeKit

final class ProtocolTests: XCTestCase {
    func testRoundTrip() throws {
        let f = Frame(type: MsgType.videoFrame, payload: Data([1, 2, 3]), flags: [.keyframe], timestampNs: -5)
        var d = FrameDecoder(maxPayload: 100)
        d.feed(f.encode(sequence: 0))
        let g = try XCTUnwrap(try d.next())
        XCTAssertEqual(g.type, MsgType.videoFrame)
        XCTAssertEqual(g.payload, Data([1, 2, 3]))
        XCTAssertEqual(g.flags, [.keyframe])
        XCTAssertEqual(g.timestampNs, -5)
    }

    func testHeaderLayoutIsBigEndian() {
        let bytes = [UInt8](Frame(type: 0x41, payload: Data([9]), timestampNs: 1).encode(sequence: 2))
        XCTAssertEqual(Array(bytes[0..<4]), [0, 0, 0, 1])   // length
        XCTAssertEqual(bytes[4], 1)                          // version
        XCTAssertEqual(bytes[5], 0x41)                       // type
        XCTAssertEqual(Array(bytes[8..<16]), [0, 0, 0, 0, 0, 0, 0, 2])
        XCTAssertEqual(bytes[23], 1)
    }

    func testRejectsOversizeAndSequenceGaps() {
        var d = FrameDecoder(maxPayload: 2)
        d.feed(Frame(type: 1, payload: Data([1, 2, 3])).encode(sequence: 0))
        XCTAssertThrowsError(try d.next())
        var e = FrameDecoder(maxPayload: 10)
        e.feed(Frame(type: 1, payload: Data()).encode(sequence: 1))
        XCTAssertThrowsError(try e.next())
    }

    func testPartialFeeds() throws {
        let bytes = Frame(type: 2, payload: Data(repeating: 7, count: 50)).encode(sequence: 0)
        var d = FrameDecoder(maxPayload: 100)
        d.feed(bytes.prefix(10)); XCTAssertNil(try d.next())
        d.feed(bytes.dropFirst(10)); XCTAssertNotNil(try d.next())
    }

    /// Shared vector: protocol/test-vectors/auth.json (same values as the C# and Kotlin tests).
    func testAuthVector() {
        let key = Data((0..<32).map { UInt8($0) })
        let hex = { (d: Data) in d.map { String(format: "%02x", $0) }.joined() }
        XCTAssertEqual(hex(AuthCrypto.clientMac(key: key, channel: "control", serverNonce: "AAAA", clientNonce: "BBBB")),
                       "1c77d25cebd43dd60ef671a001e6a21078dbc5e2673aceceb09041674c4c29f4")
        XCTAssertEqual(hex(AuthCrypto.serverMac(key: key, channel: "control", serverNonce: "AAAA", clientNonce: "BBBB")),
                       "02523cfca8cf26e1d049ae5f8fad69da42cb31b623c70b7254d2c5320fd347fc")
    }

    func testFrameVector() {
        let f = Frame(type: 65, payload: Data([9]), flags: [.keyframe], timestampNs: 1)
        XCTAssertEqual(f.encode(sequence: 2).map { String(format: "%02x", $0) }.joined(), "00000001014100010000000000000002000000000000000109")
    }

    func testSensorRejectsNaN() {
        var p = Data(); p.appendBE(UInt32(4)); p.append(3); p.append(1); p.appendBE(UInt16(0)); p.appendBE(Float.nan.bitPattern)
        XCTAssertThrowsError(try SensorSample(frame: Frame(type: MsgType.sensorSample, payload: p)))
    }
}
