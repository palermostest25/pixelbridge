import Foundation

public struct AdbResult: Sendable {
    public let exitCode: Int32, stdout: String, stderr: String
    public var ok: Bool { exitCode == 0 }
}

public struct AdbDevice: Sendable, Equatable {
    public let serial: String, state: String, model: String
    public var isEmulator: Bool { serial.hasPrefix("emulator-") }
}

/// adb wrapper. Every device command is qualified with `-s SERIAL`; never uses tcpip or key management.
public struct AdbClient: Sendable {
    public let path: String

    /// Homebrew (`brew install android-platform-tools`), the Android SDK, or an explicit path.
    public static func locate(configured: String?) -> String? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let candidates = [configured, "/opt/homebrew/bin/adb", "/usr/local/bin/adb", "\(home)/Library/Android/sdk/platform-tools/adb",
                          Bundle.main.resourcePath.map { "\($0)/platform-tools/adb" }].compactMap { $0 }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    public init(path: String) { self.path = path }

    public func run(_ args: [String], timeout: TimeInterval = 20) async -> AdbResult {
        await withCheckedContinuation { cont in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: path)
            p.arguments = args
            let out = Pipe(), err = Pipe()
            p.standardOutput = out; p.standardError = err
            let lock = NSLock()
            var done = false
            func finish(_ r: AdbResult) {
                lock.lock(); defer { lock.unlock() }
                if done { return }
                done = true
                cont.resume(returning: r)
            }
            p.terminationHandler = { proc in
                let o = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                let e = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                finish(AdbResult(exitCode: proc.terminationStatus, stdout: o, stderr: e))
            }
            do { try p.run() } catch { finish(AdbResult(exitCode: -1, stdout: "", stderr: "\(error)")); return }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                if p.isRunning { p.terminate(); finish(AdbResult(exitCode: -1, stdout: "", stderr: "timeout")) }
            }
        }
    }

    public func shell(_ serial: String, _ command: String, timeout: TimeInterval = 15) async -> AdbResult {
        await run(["-s", serial, "shell", command], timeout: timeout)
    }

    public func devices() async -> [AdbDevice] {
        AdbClient.parseDevices(await run(["devices", "-l"], timeout: 10).stdout)
    }

    public static func parseDevices(_ output: String) -> [AdbDevice] {
        output.split(separator: "\n").compactMap { raw in
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, !line.hasPrefix("List of devices"), !line.hasPrefix("*") else { return nil }
            let parts = line.split(whereSeparator: \.isWhitespace).map(String.init)
            guard parts.count >= 2 else { return nil }
            let model = parts.first { $0.hasPrefix("model:") }?.dropFirst(6) ?? ""
            return AdbDevice(serial: parts[0], state: parts[1], model: String(model))
        }
    }

    /// `adb forward tcp:0 tcp:DEVICE` lets adb pick a free loopback port; returns it.
    public func forward(_ serial: String, devicePort: Int) async throws -> Int {
        let r = await run(["-s", serial, "forward", "tcp:0", "tcp:\(devicePort)"], timeout: 10)
        guard r.ok, let port = Int(r.stdout.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            throw ProtocolError.malformed("adb forward failed: \(r.stderr)")
        }
        return port
    }

    public func removeForward(_ serial: String, hostPort: Int) async {
        _ = await run(["-s", serial, "forward", "--remove", "tcp:\(hostPort)"], timeout: 10)
    }

    public func getprop(_ serial: String, _ prop: String) async -> String {
        await shell(serial, "getprop \(prop)", timeout: 10).stdout.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    public func packageVersion(_ serial: String, _ pkg: String) async -> String? {
        let out = await shell(serial, "dumpsys package \(pkg)").stdout
        guard let r = out.range(of: #"versionName=(\S+)"#, options: .regularExpression) else { return nil }
        return String(out[r].dropFirst("versionName=".count))
    }
}
