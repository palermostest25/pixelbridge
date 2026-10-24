import AppKit
import Combine
import CryptoKit
import Foundation
import PixelBridgeKit

struct PhoneEntry: Identifiable {
    let values: [String: Any]
    var id: String { let base = text("id").isEmpty ? text("key").isEmpty ? text("component") : text("key") : text("id"); return values["state"] == nil && values["number"] != nil ? base + ":" + text("number") : base }
    func text(_ key: String) -> String { if let s = values[key] as? String { return s }; if let n = values[key] as? NSNumber { return n.stringValue }; return "" }
    func number(_ key: String) -> Double { (values[key] as? NSNumber)?.doubleValue ?? 0 }
    func flag(_ key: String) -> Bool { values[key] as? Bool ?? false }
    var date: Date { Date(timeIntervalSince1970: number("dateMs") / 1000) }
}

@MainActor final class PhoneWorkspaceModel: ObservableObject {
    private let session: DeviceSession
    private var bag = Set<AnyCancellable>()
    private var epoch = 0, messageGeneration = 0, contactGeneration = 0
    private var enabled = true, watching = false
    private var pendingToken: String?, pendingFingerprint: String?
    @Published var threads: [PhoneEntry] = []
    @Published var messages: [PhoneEntry] = []
    @Published var contacts: [PhoneEntry] = []
    @Published var calls: [PhoneEntry] = []
    @Published var history: [PhoneEntry] = []
    @Published var apps: [PhoneEntry] = []
    @Published var notifications: [PhoneEntry] = []
    @Published var status: [String: Any] = [:]
    @Published var tools: [String: Any] = [:]
    @Published var error = ""
    @Published var sendStatus = ""
    @Published var busy = false
    @Published var sending = false
    @Published var selectedThread: String?
    @Published var downloads: [String] = []
    @Published var transferStatus = ""
    @Published var page = ""
    var dialer: Bool { status["dialer"] as? Bool ?? false }
    var muted: Bool { (status["calls"] as? [String: Any])?["muted"] as? Bool ?? false }
    var callRoute: String { (status["calls"] as? [String: Any])?["route"] as? String ?? "phone" }
    init(session: DeviceSession) {
        self.session = session
        session.radioEvent.sink { [weak self] in self?.onEvent($0) }.store(in: &bag)
        session.notification.sink { [weak self] row in
            guard let self, self.enabled else { return }
            let entry = PhoneEntry(values: row)
            self.notifications.removeAll { $0.id == entry.id }; self.notifications.insert(entry, at: 0)
            self.notifications = Array(self.notifications.prefix(100))
        }.store(in: &bag)
        session.$state.sink { [weak self] state in
            guard let self else { return }
            if state != .connected { clear() }
            else if !page.isEmpty { Task { await self.enter(self.page) } }
        }.store(in: &bag)
    }
    private func rows(_ value: [String: Any], _ key: String) -> [PhoneEntry] { (value[key] as? [[String: Any]] ?? []).map { PhoneEntry(values: $0) } }
    private func request(_ op: String, _ args: [String: Any] = [:]) async throws -> [String: Any] {
        guard enabled else { throw PhoneError("Turn off privacy mode to use phone features") }
        guard session.isConnected else { throw PhoneError("Connect your phone over USB to use this feature.") }
        let generation = epoch
        let response = try await session.radio(op, args, timeout: 20)
        guard enabled, generation == epoch else { throw PhoneError("The phone connection changed; refresh this view") }
        guard response["ok"] as? Bool == true else { throw PhoneError(response["message"] as? String ?? "The phone could not complete this request") }
        return response["result"] as? [String: Any] ?? [:]
    }
    @discardableResult private func perform(_ work: () async throws -> Void) async -> Bool {
        error = ""
        do { try await work(); return true } catch { self.error = error.localizedDescription; return false }
    }
    func enter(_ page: String) async {
        self.page = page
        guard session.isConnected, enabled else { return }
        await perform {
            let watch = ["messages", "calls", "contacts"].contains(page)
            if watching != watch { _ = try await request("phone.watch", ["enabled": watch]); watching = watch }
            status = try await request("phone.status"); applyCalls(status["calls"] as? [String: Any] ?? [:])
        }
        await refresh()
    }
    func leave() {
        page = ""
        if watching { watching = false; Task { _ = try? await session.radio("phone.watch", ["enabled": false]) } }
    }
    func refresh() async {
        guard session.isConnected, enabled else { return }
        busy = true; defer { busy = false }
        switch page {
        case "messages": await refreshThreads(); if let selectedThread { await loadMessages(selectedThread) }
        case "calls": await perform { status = try await request("phone.status"); applyCalls(status["calls"] as? [String: Any] ?? [:]); history = rows(try await request("phone.history"), "calls") }
        case "contacts": await searchContacts("")
        case "tools": await perform { tools = try await request("tools.status"); apps = rows(try await request("tools.apps"), "apps") }; await refreshDownloads()
        case "notifications": await perform { notifications = rows(try await request("notifications.list"), "notifications") }
        default: break
        }
    }
    func refreshThreads() async { await perform { threads = rows(try await request("sms.threads"), "threads") } }
    func loadMessages(_ thread: String) async {
        guard let id = Int64(thread) else { return }
        messageGeneration += 1; let generation = messageGeneration; selectedThread = thread
        await perform {
            let result = try await request("sms.messages", ["threadId": id])
            guard generation == messageGeneration, selectedThread == thread else { return }
            messages = rows(result, "messages").sorted { $0.number("dateMs") < $1.number("dateMs") }
        }
    }
    func searchContacts(_ query: String) async {
        contactGeneration += 1; let generation = contactGeneration
        await perform { let result = try await request("contacts.list", ["query": query, "limit": 100]); if generation == contactGeneration { contacts = rows(result, "contacts") } }
    }
    func newMessage() { selectedThread = nil; messages = []; pendingToken = nil; pendingFingerprint = nil; sendStatus = "" }
    func send(number: String, text: String) async -> Bool {
        guard !sending, !number.trimmingCharacters(in: .whitespaces).isEmpty, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, text.count <= 12000 else { return false }
        sending = true; defer { sending = false }
        return await perform {
            let fingerprint = SHA256.hash(data: Data((number.trimmingCharacters(in: .whitespaces) + "\0" + text).utf8)).map { String(format: "%02x", $0) }.joined()
            if pendingFingerprint == fingerprint, let token = pendingToken {
                let receipt = try await request("sms.receipt", ["token": token]); sendStatus = "Previous send: " + receiptLabel(receipt) + ". Check the conversation before starting a new message."; throw PhoneError("This message has already been submitted. It will not be sent twice.")
            }
            let token = UUID().uuidString.replacingOccurrences(of: "-", with: "")
            pendingToken = token; pendingFingerprint = fingerprint; sendStatus = "Sending to phone…"
            let response = try await request("sms.send", ["number": number, "text": text, "token": token]); sendStatus = receiptLabel(response)
        }
    }
    func call(_ number: String) async { await perform { _ = try await request("phone.call", ["number": number]) } }
    func callAction(_ op: String, call: PhoneEntry? = nil, args: [String: Any] = [:]) async {
        var a = args; if let call { a["callId"] = call.id }
        await perform { _ = try await request(op, a) }
    }
    func tool(_ op: String, _ args: [String: Any] = [:]) async {
        await perform { let result = try await request(op, args); if ["tools.volume", "tools.flashlight"].contains(op) { tools = result } }
    }
    func reply(_ key: String, text: String) async -> Bool { await perform { _ = try await request("notifications.reply", ["key": key, "text": text]); sendStatus = "Reply handed to the phone app" } }
    func dismiss(_ key: String) async { await perform { _ = try await request("notifications.dismiss", ["key": key]); notifications.removeAll { $0.id == key } } }
    private func applyCalls(_ row: [String: Any]) { calls = rows(row, "calls"); status["calls"] = row }
    private func onEvent(_ row: [String: Any]) {
        guard enabled else { return }
        switch row["event"] as? String {
        case "phone.calls": applyCalls(row)
        case "phone.error": error = row["message"] as? String ?? "Phone action failed"
        case "phone.changed": if ["messages", "calls", "contacts"].contains(page) { Task { await refresh() } }
        case "sms.receipt": if row["token"] as? String == pendingToken { sendStatus = receiptLabel(row) }
        case "notifications.removed": notifications.removeAll { $0.id == row["key"] as? String }
        default: break
        }
    }
    private func receiptLabel(_ receipt: [String: Any]) -> String {
        switch receipt["state"] as? String { case "queued": "Queued on phone"; case "sent": "Sent to carrier"; case "delivered": "Delivery confirmed"; case "failed": "Send failed: \(receipt["error"] as? String ?? "Check phone")"; default: "Status unknown—check the phone" }
    }
    func setEnabled(_ value: Bool) {
        enabled = value
        if !value { let selectedPage = page; leave(); page = selectedPage; clear() }
        else if session.isConnected, !page.isEmpty { Task { await self.enter(self.page) } }
    }
    private func clear() { epoch += 1; messageGeneration += 1; contactGeneration += 1; watching = false; error = ""; threads = []; messages = []; contacts = []; calls = []; history = []; apps = []; notifications = []; status = [:]; tools = [:]; downloads = []; transferStatus = "" }
    private func adb(_ arguments: [String], timeout: TimeInterval = 120) async throws -> String {
        guard let adb = session.adb, !session.serial.isEmpty else { throw PhoneError("Connect the phone first") }
        let r = await adb.run(["-s", session.serial] + arguments, timeout: timeout)
        guard r.ok else { throw PhoneError((r.stderr + r.stdout).trimmingCharacters(in: .whitespacesAndNewlines)) }
        return r.stdout
    }
    func openSetup(_ feature: String = "communications") async { guard ["communications", "calling", "notifications"].contains(feature) else { return }; await perform { _ = try await adb(["shell", "am start -n \(DeviceSession.package)/.phone.SetupActivity --es feature \(feature)"]) } }
    func openCompanion() async { await perform { _ = try await adb(["shell", "am start -n \(DeviceSession.package)/.MainActivity"]); } }
    func restartPhone() async { await perform { _ = try await adb(["reboot"]); transferStatus = "Restart requested. PixelBridge will reconnect after Android starts and is unlocked." } }
    func display(_ awake: Bool) async { await perform { _ = try await adb(["shell", awake ? "input keyevent KEYCODE_WAKEUP" : "input keyevent KEYCODE_SLEEP"]) } }
    func launch(_ app: PhoneEntry) async {
        let component = app.text("component")
        guard component.range(of: "^[A-Za-z0-9_.]+/[A-Za-z0-9_.$]+$", options: .regularExpression) != nil else { return }
        await perform { _ = try await adb(["shell", "am start -n '\(component)'"]) }
    }
    func refreshDownloads() async { await perform { let output = try await adb(["shell", "ls -1p /sdcard/Download"]); downloads = output.split(separator: "\n").map(String.init).filter(Self.validFileName).prefix(200).map { $0 } } }
    static func validFileName(_ name: String) -> Bool { !name.isEmpty && name.count <= 255 && name != "." && name != ".." && !name.contains("/") && !name.contains("\\") && name.unicodeScalars.allSatisfy { !CharacterSet.controlCharacters.contains($0) } }
    func uploadFile() async {
        let panel = NSOpenPanel(); panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let file = panel.url, Self.validFileName(file.lastPathComponent) else { return }
        let remote = "/sdcard/Download/" + file.lastPathComponent
        let quoted = "'" + remote.replacingOccurrences(of: "'", with: "'\\''") + "'"
        let checked = await perform {
            let exists = try await adb(["shell", "if [ -e \(quoted) ]; then echo exists; else echo free; fi"])
            guard exists.trimmingCharacters(in: .whitespacesAndNewlines) == "free" else { throw PhoneError("A file with this name is already in phone Downloads. Rename the file before sending.") }
        }
        guard checked else { return }
        transferStatus = "Sending \(file.lastPathComponent)…"
        let ok = await perform { _ = try await adb(["push", file.path, "/sdcard/Download/" + file.lastPathComponent], timeout: 600) }
        transferStatus = ok ? "Sent to the phone's Downloads folder" : "Transfer failed"; if ok { await refreshDownloads() }
    }
    func downloadFile(_ name: String) async {
        guard Self.validFileName(name) else { return }
        let panel = NSSavePanel(); panel.nameFieldStringValue = name
        guard panel.runModal() == .OK, let target = panel.url else { return }
        transferStatus = "Receiving \(name)…"
        let ok = await perform { _ = try await adb(["pull", "/sdcard/Download/" + name, target.path], timeout: 600) }
        transferStatus = ok ? "Saved \(name)" : "Transfer failed"
    }
}
private struct PhoneError: LocalizedError { let message: String; init(_ value: String) { message = value }; var errorDescription: String? { message } }
