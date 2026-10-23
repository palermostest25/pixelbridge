import Foundation
import Network

public enum AuthError: Error, CustomStringConvertible {
    case notPaired, noKey, rejected(String), badServerMac
    public var description: String {
        switch self {
        case .notPaired: "the phone is not paired with this Mac"
        case .noKey: "no pairing key stored on this Mac"
        case .rejected(let c): "the phone rejected authentication (\(c)); pair again"
        case .badServerMac: "the phone failed to prove the pairing key"
        }
    }
}

/// One authenticated TCP connection to a forwarded device channel (protocol section 3), on Network.framework.
public final class ChannelClient: @unchecked Sendable {
    public let channel: String
    private let connection: NWConnection
    private let queue: DispatchQueue
    private var decoder: FrameDecoder
    private var sendSeq: UInt64 = 0
    private let sendLock = NSLock()
    public var onFrame: ((Frame) -> Void)?
    public var onClose: ((Error?) -> Void)?
    public private(set) var isOpen = true

    private init(port: Int, channel: String) {
        self.channel = channel
        queue = DispatchQueue(label: "pb.\(channel)")
        decoder = FrameDecoder(maxPayload: ChannelLimits.maxPayload(channel))
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        connection = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(integerLiteral: UInt16(port)), using: NWParameters(tls: nil, tcp: tcp))
    }

    public static func connect(port: Int, channel: String, key: Data?) async throws -> ChannelClient {
        let c = ChannelClient(port: port, channel: channel)
        try await c.start()
        do { try await c.handshake(key: key) } catch { c.close(); throw error }
        return c
    }

    private func start() async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            var resumed = false
            connection.stateUpdateHandler = { state in
                guard !resumed else { return }
                switch state {
                case .ready: resumed = true; cont.resume()
                case .failed(let e), .waiting(let e): resumed = true; cont.resume(throwing: e)
                case .cancelled: resumed = true; cont.resume(throwing: ProtocolError.closed)
                default: break
                }
            }
            connection.start(queue: queue)
        }
    }

    /// Receive until one full frame is decoded (used during the handshake, before the read loop starts).
    private func readFrame() async throws -> Frame {
        while true {
            if let f = try decoder.next() { return f }
            let chunk: Data = try await withCheckedThrowingContinuation { cont in
                connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { data, _, complete, err in
                    if let err { cont.resume(throwing: err) }
                    else if let data, !data.isEmpty { cont.resume(returning: data) }
                    else if complete { cont.resume(throwing: ProtocolError.closed) }
                    else { cont.resume(returning: Data()) }
                }
            }
            decoder.feed(chunk)
        }
    }

    private func handshake(key: Data?) async throws {
        let hello = try await withTimeout(6) { try await self.readFrame() }
        guard hello.type == MsgType.hello, let j = hello.jsonObject else { throw ProtocolError.malformed("expected HELLO") }
        guard (j["protocolMajor"] as? Int) == 1 else { throw ProtocolError.malformed("unsupported protocol") }
        guard (j["channel"] as? String) == channel else { throw ProtocolError.malformed("wrong channel") }
        guard (j["paired"] as? Bool) == true else { throw AuthError.notPaired }
        guard let key else { throw AuthError.noKey }
        guard let serverNonce = j["serverNonce"] as? String else { throw ProtocolError.malformed("no nonce") }
        let clientNonce = AuthCrypto.nonce()
        let mac = AuthCrypto.clientMac(key: key, channel: channel, serverNonce: serverNonce, clientNonce: clientNonce)
        send(.json(MsgType.authResponse, ["channel": channel, "clientNonce": clientNonce, "mac": mac.base64EncodedString()]))
        let reply = try await withTimeout(6) { try await self.readFrame() }
        if reply.type == MsgType.authFail { throw AuthError.rejected(reply.jsonObject?["code"] as? String ?? "auth") }
        guard reply.type == MsgType.authOk, let proofB64 = reply.jsonObject?["mac"] as? String, let proof = Data(base64Encoded: proofB64),
              AuthCrypto.equal(proof, AuthCrypto.serverMac(key: key, channel: channel, serverNonce: serverNonce, clientNonce: clientNonce))
        else { throw AuthError.badServerMac }
    }

    public func startReading() {
        queue.async { self.drain() }
    }

    private func drain() {
        do { while let f = try decoder.next() { onFrame?(f) } } catch { fail(error); return }
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { [weak self] data, _, complete, err in
            guard let self else { return }
            if let err { self.fail(err); return }
            if let data, !data.isEmpty { self.decoder.feed(data) }
            if complete && (data?.isEmpty ?? true) { self.fail(nil); return }
            self.drain()
        }
    }

    private func fail(_ e: Error?) {
        guard isOpen else { return }
        isOpen = false
        connection.cancel()
        onClose?(e)
    }

    @discardableResult
    public func send(_ f: Frame) -> Bool {
        guard isOpen else { return false }
        sendLock.lock()
        let bytes = f.encode(sequence: sendSeq)
        sendSeq += 1
        connection.send(content: bytes, completion: .contentProcessed { _ in })
        sendLock.unlock()
        return true
    }

    public func close() {
        isOpen = false
        connection.cancel()
    }
}

func withTimeout<T: Sendable>(_ seconds: Double, _ op: @escaping @Sendable () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { g in
        g.addTask { try await op() }
        g.addTask { try await Task.sleep(nanoseconds: UInt64(seconds * 1e9)); throw ProtocolError.malformed("timeout") }
        let r = try await g.next()!
        g.cancelAll()
        return r
    }
}
