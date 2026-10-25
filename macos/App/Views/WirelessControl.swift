import PixelBridgeKit
import SwiftUI

struct WirelessControl: View {
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var model: AppModel
    @State private var confirming = false
    @State private var busy = false
    @State private var message = ""
    @State private var address = ""
    @State private var code = ""
    @State private var connectAddress = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SettingRow(icon: "wifi", title: "Connect over Wi-Fi", subtitle: session.wirelessEndpoint.isEmpty
                ? "Use a pairing code on Android 11+, or set up your trusted phone over USB. Keep both devices on the same private Wi-Fi network."
                : "Saved phone: \(session.wirelessEndpoint) · Reconnects automatically when available.") {
                HStack {
                    Button(busy ? "Connecting…" : "Set up over USB…") { confirming = true }
                        .disabled(busy || !WirelessAdb.isUsbSerial(session.serial) || !model.phone.calls.isEmpty)
                    if !session.wirelessEndpoint.isEmpty {
                        Button(session.wirelessSecure ? "Disconnect Wi-Fi" : "Turn off wireless") { Task { await change(enable: false) } }
                            .disabled(busy || !model.phone.calls.isEmpty)
                    }
                }
            }
            DisclosureGroup("Pair without a cable") {
                VStack(alignment: .leading, spacing: 12) {
                    Text("On Android, open Settings → System → Developer options → Wireless debugging → Pair device with pairing code. Keep that dialog open.")
                        .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    TextField("Pairing address, e.g. 192.168.1.20:37123", text: $address).textFieldStyle(.roundedBorder)
                    SecureField("Six-digit pairing code", text: $code).textFieldStyle(.roundedBorder)
                    TextField("Connection address (optional if discovered automatically)", text: $connectAddress).textFieldStyle(.roundedBorder)
                    Text("The connection port is shown on the main Wireless debugging page and differs from the pairing port. If discovery fails, enter it and choose Connect paired phone.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Button("Pair & connect") { Task { await pair(useCode: true) } }
                            .disabled(busy || !WirelessAdb.validEndpoint(address.trimmingCharacters(in: .whitespacesAndNewlines)) || !WirelessAdb.validCode(code) || !model.phone.calls.isEmpty)
                        Button("Connect paired phone") { Task { await pair(useCode: false) } }
                            .disabled(busy || !WirelessAdb.validEndpoint(connectAddress.trimmingCharacters(in: .whitespacesAndNewlines)) || !model.phone.calls.isEmpty)
                        if busy { ProgressView().controlSize(.small) }
                    }
                }.padding(.top, 8)
            }
            if !message.isEmpty { Text(message).font(.callout).foregroundStyle(.secondary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true) }
        }
        .alert("Enable wireless connection?", isPresented: $confirming) {
            Button("Cancel", role: .cancel) { }
            Button("Enable wireless") { Task { await change(enable: true) } }
        } message: { Text(WirelessAdb.consent) }
    }

    private func pair(useCode: Bool) async {
        busy = true; message = "Pairing your wireless phone…"
        let secret = useCode ? code : ""; code = ""
        let connection = connectAddress.trimmingCharacters(in: .whitespacesAndNewlines)
        defer { busy = false }
        do { message = try await session.pairWireless(address: useCode ? address.trimmingCharacters(in: .whitespacesAndNewlines) : connection, code: secret, connectAddress: connection) }
        catch { message = error.localizedDescription }
    }

    private func change(enable: Bool) async {
        busy = true; message = enable ? "Setting up your Wi-Fi connection…" : "Turning off wireless debugging…"
        defer { busy = false }
        do { message = try await (enable ? session.enableWireless() : session.disableWireless()) }
        catch { message = error.localizedDescription }
    }
}
