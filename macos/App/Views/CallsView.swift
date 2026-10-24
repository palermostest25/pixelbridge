import PixelBridgeKit
import SwiftUI

struct CallsView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @EnvironmentObject var session: DeviceSession
    @State private var number = ""
    @State private var accountID = ""
    @State private var placing = false
    private let keys = [("1", ""), ("2", "ABC"), ("3", "DEF"), ("4", "GHI"), ("5", "JKL"), ("6", "MNO"), ("7", "PQRS"), ("8", "TUV"), ("9", "WXYZ"), ("*", ""), ("0", "+"), ("#", "")]
    private var canDial: Bool {
        session.isConnected && !model.privacy && !placing && !number.trimmingCharacters(in: .whitespaces).isEmpty
            && (phone.calls.isEmpty || ((phone.status["calls"] as? [String: Any])?["canAddCall"] as? Bool == true))
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            PhoneFeatureNotice()
            HStack(alignment: .top, spacing: 24) {
                dialer.frame(width: 280)
                callArea.frame(minWidth: 270, maxWidth: .infinity, alignment: .topLeading)
            }
            HStack {
                Text("Recent calls").font(.headline)
                Spacer()
                Button("Refresh", systemImage: "arrow.clockwise") { Task { await phone.refresh() } }.disabled(!session.isConnected || phone.busy)
            }
            if phone.history.isEmpty {
                Text("Enable call-history access on your Pixel to see recent calls here.").foregroundStyle(.secondary).padding(.vertical, 12)
            }
            LazyVStack(spacing: 0) {
                ForEach(phone.history) { row in
                    HStack(spacing: 14) {
                        Image(systemName: Int(row.number("type")) == 3 ? "phone.badge.xmark" : "phone.arrow.up.right")
                            .foregroundStyle(Int(row.number("type")) == 3 ? .orange : .secondary).frame(width: 26)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(row.text("name").isEmpty ? row.text("number") : row.text("name")).fontWeight(.medium)
                            Text(row.text("number")).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text(row.date, format: .dateTime.month(.abbreviated).day().hour().minute()).font(.caption).foregroundStyle(.secondary)
                        Button("Use number") { number = row.text("number") }.disabled(model.privacy)
                    }.padding(.vertical, 12)
                    Divider()
                }
            }
        }
        .task { await phone.enter("calls"); accountID = phone.defaultAccountID }
        .onChange(of: phone.accounts.map(\.id)) { _, ids in if !ids.contains(accountID) { accountID = phone.defaultAccountID } }
        .onDisappear { phone.leave("calls") }
    }

    private var dialer: some View {
        Card {
            VStack(spacing: 16) {
                Text("Dial a number").font(.headline).frame(maxWidth: .infinity, alignment: .leading)
                TextField("Phone number", text: $number).font(.title2).textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Phone number").onSubmit { dial() }
                if phone.accounts.count > 1 {
                    Picker("Call using", selection: $accountID) {
                        Text("Choose SIM / phone line").tag("")
                        ForEach(phone.accounts) { Text($0.text("name")).tag($0.id) }
                    }.labelsHidden().accessibilityLabel("Call using SIM")
                }
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10), count: 3), spacing: 10) {
                    ForEach(keys, id: \.0) { key in
                        Button { number += key.0 } label: {
                            VStack(spacing: 1) {
                                Text(key.0).font(.system(size: 25, weight: .medium, design: .rounded))
                                Text(key.1.isEmpty ? " " : key.1).font(.system(size: 9, weight: .semibold)).foregroundStyle(Color.primary.opacity(0.7))
                            }.frame(maxWidth: .infinity, minHeight: 48)
                        }.buttonStyle(.bordered).accessibilityLabel(key.0)
                    }
                }
                HStack {
                    Button("+") { number += "+" }.accessibilityLabel("Add international plus")
                    Spacer()
                    Button { if !number.isEmpty { number.removeLast() } } label: { Image(systemName: "delete.left") }
                        .accessibilityLabel("Delete last digit").disabled(number.isEmpty)
                }
                Button(action: dial) {
                    Label(placing ? "Starting call…" : "Call", systemImage: "phone.fill").font(.headline).frame(maxWidth: .infinity).padding(.vertical, 5)
                }.buttonStyle(.borderedProminent).disabled(!canDial)
            }
        }.disabled(model.privacy)
    }

    @ViewBuilder private var callArea: some View {
        VStack(alignment: .leading, spacing: 18) {
            if !phone.dialer {
                VStack(alignment: .leading, spacing: 12) {
                    Label("Enable call controls", systemImage: "phone.badge.checkmark").font(.headline)
                    Text("Choose PixelBridge as your Pixel’s Phone app to answer, mute, hold and end calls here.").foregroundStyle(.secondary)
                    Button("Set up on Pixel…") { Task { await phone.openSetup("calling"); model.openPhoneScreen() } }.disabled(!session.isConnected)
                }
            }
            if phone.calls.isEmpty {
                VStack(alignment: .leading, spacing: 16) {
                    Image(systemName: "phone.connection").font(.system(size: 34, weight: .light)).foregroundStyle(BridgeStyle.accent)
                    Text("Ready when you are.").font(.title2.weight(.semibold))
                    Text("Dial here, paste a number, or choose one from recent calls. Incoming calls appear here automatically.").font(.callout).foregroundStyle(.secondary)
                }.padding(.vertical, 24)
            }
            ForEach(phone.calls) { call in
                activeCall(call)
            }
            Label("Call audio uses your Pixel’s speaker or connected headset.", systemImage: "speaker.wave.2")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func activeCall(_ call: PhoneEntry) -> some View {
        Card {
            VStack(alignment: .leading, spacing: 18) {
                Text(call.text("number")).font(.title2.weight(.semibold)).textSelection(.enabled)
                HStack {
                    Text(stateLabel(call.text("state"))).font(.callout.weight(.medium)).foregroundStyle(BridgeStyle.accent)
                    if call.text("state") == "active", call.number("connectedAtMs") > 0 {
                        Text(Date(timeIntervalSince1970: call.number("connectedAtMs") / 1000), style: .timer).monospacedDigit().font(.callout).foregroundStyle(.secondary)
                    }
                }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 115), spacing: 10)], spacing: 10) {
                    if call.text("state") == "ringing" {
                        actionButton("Answer", "phone.fill", "phone.answer", call).tint(BridgeStyle.accent)
                    } else {
                        Button { Task { await phone.callAction("phone.mute", args: ["enabled": !phone.muted]) } } label: {
                            Label(phone.muted ? "Unmute" : "Mute", systemImage: phone.muted ? "mic.slash.fill" : "mic.fill").frame(maxWidth: .infinity, minHeight: 30)
                        }.tint(phone.muted ? .orange : BridgeStyle.accent).accessibilityLabel(phone.muted ? "Unmute microphone" : "Mute microphone")
                        if call.flag("canHold") { actionButton(call.text("state") == "held" ? "Resume" : "Hold", "pause.fill", call.text("state") == "held" ? "phone.unhold" : "phone.hold", call) }
                        Menu { ForEach(phone.callEndpoints) { endpoint in
                            Button { Task { await phone.callAction("phone.route", args: ["endpointId": endpoint.id, "route": endpoint.text("route")]) } } label: {
                                Label(endpoint.text("name"), systemImage: endpoint.flag("selected") ? "checkmark" : "speaker.wave.2")
                            }
                        } } label: { Label(routeName, systemImage: "speaker.wave.2").frame(maxWidth: .infinity, minHeight: 30) }
                        .disabled(phone.callEndpoints.isEmpty)
                        if call.flag("canMerge") { actionButton("Merge calls", "arrow.triangle.merge", "phone.merge", call) }
                        if call.flag("canSwap") { actionButton("Swap calls", "arrow.triangle.swap", "phone.swap", call) }
                    }
                    actionButton(call.text("state") == "ringing" ? "Decline" : "End call", "phone.down.fill", "phone.end", call).tint(.red)
                }.buttonStyle(.bordered).disabled(!phone.dialer || !session.isConnected || model.privacy || ["ended", "ending"].contains(call.text("state")))
                if call.text("state") == "active" {
                    DisclosureGroup("Keypad") {
                        LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 8) {
                            ForEach(keys, id: \.0) { key in
                                Button(key.0) { Task { await phone.callAction("phone.dtmf", call: call, args: ["digit": key.0]) } }
                                    .font(.title3).frame(maxWidth: .infinity, minHeight: 30).buttonStyle(.bordered).accessibilityLabel("Send keypad tone \(key.0)")
                            }
                        }.padding(.top, 12)
                    }.disabled(!phone.dialer || model.privacy)
                }
            }
        }
    }
    private var routeName: String { phone.callEndpoints.first(where: { $0.flag("selected") })?.text("name") ?? "Audio" }
    private func stateLabel(_ state: String) -> String {
        switch state { case "ringing": "Incoming call"; case "dialing": "Calling…"; case "active": "Connected"; case "held": "On hold"; case "choose-sim": "Choose a SIM on the phone"; case "ending": "Ending call…"; case "ended": "Call ended"; default: "Connecting…" }
    }
    private func actionButton(_ title: String, _ icon: String, _ op: String, _ call: PhoneEntry) -> some View {
        Button { Task { await phone.callAction(op, call: call) } } label: { Label(title, systemImage: icon).frame(maxWidth: .infinity, minHeight: 30) }
    }
    private func dial() {
        guard canDial else { return }
        placing = true
        Task {
            await phone.call(number, accountID: accountID)
            if phone.error.isEmpty { try? await Task.sleep(nanoseconds: 1_500_000_000) }
            placing = false
        }
    }
}
