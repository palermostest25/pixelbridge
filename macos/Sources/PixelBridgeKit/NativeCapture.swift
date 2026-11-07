import Combine
import Foundation

/// One explicit capture session. Pixel Camera creates the originals; we only copy them.
@MainActor public final class NativeCapture: ObservableObject {
    @Published public private(set) var running = false
    @Published public private(set) var status = ""
    private var mirror: Process?
    private var cancelled = false

    public init() {}

    public func stop() {
        cancelled = true
        if let mirror, mirror.isRunning { mirror.terminate() }
    }

    public func run(_ session: DeviceSession, video: Bool, allowed: () -> Bool) async {
        guard !running else { return }
        guard allowed(), session.isConnected, let adb = session.adb else { return }
        let serial = session.serial
        guard let scrcpy = ["/opt/homebrew/bin/scrcpy", "/usr/local/bin/scrcpy"].first(where: FileManager.default.isExecutableFile(atPath:)) else {
            status = "Install scrcpy to open Pixel Camera."; return
        }
        running = true; cancelled = false
        status = "Opening Pixel Camera…"
        defer { stop(); mirror = nil; running = false }
        var saved = 0
        func check() throws {
            guard !cancelled, !Task.isCancelled, allowed() else { throw CancellationError() }
            guard session.isConnected, session.serial == serial else { throw ProtocolError.malformed("The phone disconnected. Any original stays on the phone.") }
        }
        do {
            let downloads = try FileManager.default.url(for: .downloadsDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            let clock = await adb.shell(serial, "date +%s%3N", timeout: 5)
            guard clock.ok, let since = Int64(clock.stdout.trimmingCharacters(in: .whitespacesAndNewlines)), since > 0 else {
                throw ProtocolError.malformed("Could not read the phone clock. Reconnect and try again.")
            }
            try check()
            let mode = video ? "VIDEO_CAMERA" : "STILL_IMAGE_CAMERA"
            let launch = await adb.shell(serial, "am start -W -a android.media.action.\(mode) -p com.google.android.GoogleCamera", timeout: 12)
            guard launch.ok, launch.stdout.contains("Status: ok") else {
                throw ProtocolError.malformed("Pixel Camera couldn’t open. Unlock the phone and check that Pixel Camera is installed.")
            }
            try check()
            let process = Process()
            process.executableURL = URL(fileURLWithPath: scrcpy)
            process.arguments = ["-s", serial, "--no-audio", "--force-adb-forward", "--max-size=1280", "--max-fps=15", "--turn-screen-off", "--window-title", "PixelBridge Capture"]
            var env = ProcessInfo.processInfo.environment
            env["ADB"] = adb.path
            process.environment = env
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            try process.run()
            mirror = process
            status = "Use Pixel Camera’s shutter. Stop any recording and wait for Saved before closing the phone window."
            var copied: Set<Int64> = []
            var lastPass = false
            while true {
                try await Task.sleep(for: .seconds(2))
                try check()
                let result = await adb.shell(serial, Self.query(since: since, copied: copied), timeout: 8)
                try check()
                guard result.ok, result.stderr.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                    throw ProtocolError.malformed("Could not check for finished captures. Originals stay on the phone.")
                }
                for file in Self.files(result.stdout) where !copied.contains(file.id) {
                    try check()
                    status = "Saving \(file.name) to Downloads…"
                    let temp = downloads.appendingPathComponent(".pixelbridge-\(UUID().uuidString).part")
                    defer { try? FileManager.default.removeItem(at: temp) }
                    let pull = await adb.run(["-s", serial, "pull", "/sdcard/DCIM/Camera/" + file.name, temp.path], timeout: 900)
                    try check()
                    let bytes = (try? temp.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init)
                    guard pull.ok, bytes == file.bytes else {
                        throw ProtocolError.malformed("Couldn’t save \(file.name). The original stays on the phone.")
                    }
                    let target = try Self.save(temp, named: file.name, in: downloads)
                    copied.insert(file.id); saved += 1
                    status = "Saved \(target.lastPathComponent) to Downloads."
                }
                if lastPass { break }
                lastPass = !process.isRunning
            }
            if saved == 0 {
                status = process.terminationStatus == 0 ? "Capture closed. No new files were saved." : "The phone window couldn’t stay open. Check scrcpy and reconnect the phone."
            } else {
                status = "Saved \(saved) \(saved == 1 ? "file" : "files") to Downloads."
            }
        } catch is CancellationError {
            status = "Capture closed. Originals stay on the phone."
        } catch {
            status = error.localizedDescription
        }
    }

    nonisolated static func query(since: Int64, copied: Set<Int64>) -> String {
        let skip = copied.isEmpty ? "" : " AND _id NOT IN (" + copied.sorted().map(String.init).joined(separator: ",") + ")"
        return "content query --uri content://media/external_primary/file --projection _id:_display_name:_size --where \"relative_path='DCIM/Camera/' AND owner_package_name='com.google.android.GoogleCamera' AND is_pending=0 AND is_trashed=0 AND media_type IN (1,3) AND _size>0 AND datetaken>=\(since)\(skip)\" --sort '_id ASC' | head -n 24"
    }

    nonisolated static func files(_ output: String) -> [(id: Int64, name: String, bytes: Int64)] {
        let row = #/^Row: [0-9]+ _id=([0-9]+), _display_name=(PXL_[A-Za-z0-9_.-]{1,150}\.(?:jpg|jpeg|heic|heif|dng|mp4|mov)), _size=([0-9]+)$/#
        return output.split(whereSeparator: \.isNewline).compactMap { line in
            guard let m = line.wholeMatch(of: row), let id = Int64(m.1), id > 0, let bytes = Int64(m.3), bytes > 0 else { return nil }
            return (id, String(m.2), bytes)
        }
    }

    nonisolated static func save(_ temp: URL, named name: String, in downloads: URL) throws -> URL {
        let base = name as NSString
        for n in 0..<1000 {
            let target = downloads.appendingPathComponent(n == 0 ? name : "\(base.deletingPathExtension) (\(n)).\(base.pathExtension)")
            do { try FileManager.default.moveItem(at: temp, to: target); return target }
            catch let error as NSError where error.domain == NSCocoaErrorDomain && error.code == NSFileWriteFileExistsError { continue }
        }
        throw ProtocolError.malformed("Too many copies of this file in Downloads.")
    }
}
