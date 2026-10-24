import PixelBridgeKit
import SwiftUI

struct PhoneFeatureNotice: View {
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @EnvironmentObject var session: DeviceSession
    @EnvironmentObject var model: AppModel
    var body: some View {
        if !phone.error.isEmpty {
            VStack(alignment: .leading, spacing: 10) {
                Label(phone.error, systemImage: "info.circle").font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                HStack { Button("Set up on phone…") { Task { await phone.openSetup(phone.page == "notifications" ? "notifications" : "communications"); model.openPhoneScreen() } }; Button("Show phone screen") { model.openPhoneScreen() } }
                    .disabled(!session.isConnected)
            }.padding(16).frame(maxWidth: .infinity, alignment: .leading).background(.quaternary, in: RoundedRectangle(cornerRadius: 12))
        } else if !session.isConnected {
            Label("Connect your phone over USB to load this view.", systemImage: "cable.connector").foregroundStyle(.secondary)
        }
    }
}

struct MessagesView: View {
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @EnvironmentObject var session: DeviceSession
    @State private var number = ""
    @State private var draft = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text("SMS conversations").font(.headline)
                Spacer()
                if phone.busy { ProgressView().controlSize(.small) }
                Button("Refresh", systemImage: "arrow.clockwise") { Task { await phone.refresh() } }.disabled(!session.isConnected || phone.busy)
                Button("New message", systemImage: "square.and.pencil") { phone.newMessage(); number = ""; draft = "" }
            }
            PhoneFeatureNotice()
            HSplitView {
                VStack(alignment: .leading, spacing: 0) {
                    if phone.threads.isEmpty { Text("Your recent conversations appear here after SMS access is enabled.").foregroundStyle(.secondary).padding(18) }
                    List(phone.threads, selection: $phone.selectedThread) { row in
                        VStack(alignment: .leading, spacing: 6) {
                            HStack { Text(row.text("address")).fontWeight(row.flag("unread") ? .semibold : .regular); Spacer(); if row.flag("unread") { Circle().fill(BridgeStyle.accent).frame(width: 6, height: 6) } }
                            Text(row.text("preview")).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                            Text(row.date, style: .date).font(.caption2).foregroundStyle(.tertiary)
                        }.padding(.vertical, 6).tag(row.id)
                    }.listStyle(.sidebar)
                }.frame(minWidth: 220, idealWidth: 250, maxWidth: 290)
                VStack(spacing: 14) {
                    HStack { Text("To").foregroundStyle(.secondary); TextField("Phone number", text: $number).textFieldStyle(.plain) }.padding(.horizontal, 8)
                    Divider()
                    ScrollViewReader { proxy in
                        ScrollView {
                            LazyVStack(alignment: .leading, spacing: 12) {
                                if phone.messages.isEmpty { ContentUnavailableView("Start a conversation", systemImage: "message", description: Text("Choose an SMS thread or enter a number above.")) }
                                ForEach(phone.messages) { row in
                                    HStack {
                                        if !row.flag("incoming") { Spacer(minLength: 32) }
                                        VStack(alignment: .leading, spacing: 5) {
                                            Text(row.text("body")).font(.body).textSelection(.enabled)
                                            Text(row.date, style: .time).font(.caption2).foregroundStyle(.secondary)
                                        }.padding(.horizontal, 14).padding(.vertical, 10).frame(maxWidth: 460, alignment: .leading)
                                            .background(row.flag("incoming") ? Color.secondary.opacity(0.1) : BridgeStyle.accent.opacity(0.15), in: RoundedRectangle(cornerRadius: 14))
                                        if row.flag("incoming") { Spacer(minLength: 32) }
                                    }.id(row.id)
                                }
                            }.padding(8)
                        }.onChange(of: phone.messages.last?.id) { _, id in if let id { proxy.scrollTo(id, anchor: .bottom) } }
                    }
                    Divider()
                    HStack(alignment: .bottom, spacing: 12) {
                        TextField("Write a text message", text: $draft, axis: .vertical).lineLimit(2...5).textFieldStyle(.roundedBorder)
                        Button { Task { if await phone.send(number: number, text: draft) { draft = "" } } } label: { Label(phone.sending ? "Sending…" : "Send SMS", systemImage: "arrow.up") }
                            .buttonStyle(.borderedProminent).keyboardShortcut(.return, modifiers: .command)
                            .disabled(!session.isConnected || phone.sending || number.trimmingCharacters(in: .whitespaces).isEmpty || draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                    if !phone.sendStatus.isEmpty { Text(phone.sendStatus).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading) }
                }.padding(16).frame(minWidth: 350)
            }.frame(height: 530).background(BridgeStyle.surface, in: RoundedRectangle(cornerRadius: 12)).clipShape(RoundedRectangle(cornerRadius: 12))
            Text("SMS uses your phone’s carrier plan. RCS and other messaging apps can be replied to from Notifications when they provide Android’s reply action.").font(.caption).foregroundStyle(.secondary)
        }.task { await phone.enter("messages") }.onDisappear { phone.leave() }
            .onChange(of: phone.selectedThread) { _, id in guard let id else { return }; number = phone.threads.first { $0.id == id }?.text("address") ?? ""; Task { await phone.loadMessages(id) } }
    }
}

struct CallsView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @EnvironmentObject var session: DeviceSession
    @State private var number = ""
    @State private var tone = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            PhoneFeatureNotice()
            HStack(spacing: 14) {
                Image(systemName: "phone").font(.title2).foregroundStyle(BridgeStyle.accent)
                TextField("Phone number", text: $number).textFieldStyle(.roundedBorder).font(.title3).frame(maxWidth: 420)
                Button("Call") { Task { await phone.call(number) } }.buttonStyle(.borderedProminent).disabled(!session.isConnected || number.trimmingCharacters(in: .whitespaces).isEmpty)
                Spacer()
                Button("Refresh", systemImage: "arrow.clockwise") { Task { await phone.refresh() } }.disabled(!session.isConnected || phone.busy)
            }
            Text("Call audio stays on the phone, its speaker or a Bluetooth headset. USB microphone streaming cannot carry a cellular call’s audio.").font(.callout).foregroundStyle(.secondary)
            if !phone.dialer {
                SettingRow(icon: "phone.badge.checkmark", title: "Enable incoming-call controls", subtitle: "On the phone, choose PixelBridge as the default Phone app to answer, end, hold and mute calls here.") {
                    Button("Open setup") { Task { await phone.openSetup("calling"); model.openPhoneScreen() } }.disabled(!session.isConnected)
                }
            }
            ForEach(phone.calls) { call in
                Card {
                    VStack(alignment: .leading, spacing: 14) {
                        HStack { Label(call.text("number"), systemImage: call.text("state") == "ringing" ? "phone.arrow.down.left" : "phone.fill").font(.title2.weight(.semibold)); Spacer(); Text(call.text("state").capitalized).foregroundStyle(BridgeStyle.accent) }
                        HStack(spacing: 12) {
                            if call.text("state") == "ringing" { Button("Answer") { Task { await phone.callAction("phone.answer", call: call) } }.buttonStyle(.borderedProminent) }
                            Button("End call", role: .destructive) { Task { await phone.callAction("phone.end", call: call) } }
                            if call.flag("canHold") { Button(call.text("state") == "held" ? "Resume" : "Hold") { Task { await phone.callAction(call.text("state") == "held" ? "phone.unhold" : "phone.hold", call: call) } } }
                            Button(phone.muted ? "Unmute" : "Mute") { Task { await phone.callAction("phone.mute", args: ["enabled": !phone.muted]) } }
                            Menu("Audio: \(phone.callRoute)") { ForEach(["phone", "speaker", "bluetooth"], id: \.self) { route in Button(route.capitalized) { Task { await phone.callAction("phone.route", args: ["route": route]) } } } }
                        }.disabled(!phone.dialer)
                        HStack { TextField("Keypad digit (0–9, * or #)", text: $tone).frame(maxWidth: 240); Button("Send tone") { Task { await phone.callAction("phone.dtmf", call: call, args: ["digit": String(tone.prefix(1))]) } }.disabled(!phone.dialer || tone.range(of: "^[0-9*#]$", options: .regularExpression) == nil) }
                    }
                }
            }
            HStack { Text("Recent calls").font(.headline); Spacer(); Text("Loaded from the phone").font(.caption).foregroundStyle(.secondary) }
            if phone.history.isEmpty { Text("Your call history appears here after call-log access is enabled.").foregroundStyle(.secondary).padding(.vertical, 16) }
            ForEach(phone.history) { row in
                HStack(spacing: 14) {
                    Image(systemName: Int(row.number("type")) == 3 ? "phone.badge.xmark" : "phone.arrow.up.right").foregroundStyle(Int(row.number("type")) == 3 ? .orange : .secondary).frame(width: 26)
                    VStack(alignment: .leading, spacing: 4) { Text(row.text("name").isEmpty ? row.text("number") : row.text("name")).fontWeight(.medium); Text(row.text("number")).font(.caption).foregroundStyle(.secondary) }
                    Spacer(); Text(row.date, format: .dateTime.month(.abbreviated).day().hour().minute()).font(.caption).foregroundStyle(.secondary)
                    Button("Call") { number = row.text("number"); Task { await phone.call(number) } }.disabled(!session.isConnected)
                }.padding(.vertical, 9)
                Divider()
            }
        }.task { await phone.enter("calls") }.onDisappear { phone.leave() }
    }
}

struct ContactsView: View {
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @EnvironmentObject var session: DeviceSession
    @State private var search = ""
    @State private var compose: PhoneEntry?
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack { TextField("Search names or numbers", text: $search).textFieldStyle(.roundedBorder).frame(maxWidth: 420); Spacer(); Button("Refresh", systemImage: "arrow.clockwise") { Task { await phone.searchContacts(search) } }.disabled(!session.isConnected) }
            PhoneFeatureNotice()
            if phone.contacts.isEmpty { ContentUnavailableView("Your people, within reach", systemImage: "person.crop.circle", description: Text("Enable contacts access on the phone, then search for a name or number.")) }
            ForEach(phone.contacts) { person in
                HStack(spacing: 14) {
                    Image(systemName: "person.crop.circle").font(.title).foregroundStyle(BridgeStyle.accent)
                    VStack(alignment: .leading, spacing: 5) { Text(person.text("name")).font(.headline); Text(person.text("number")).font(.callout).foregroundStyle(.secondary).textSelection(.enabled) }
                    Spacer()
                    Button("Message", systemImage: "message") { phone.newMessage(); compose = person }.disabled(!session.isConnected)
                    Button("Call", systemImage: "phone") { Task { await phone.call(person.text("number")) } }.disabled(!session.isConnected)
                }.padding(.vertical, 10)
                Divider()
            }
        }.task { await phone.enter("contacts") }.onDisappear { phone.leave() }
            .task(id: search) { do { try await Task.sleep(nanoseconds: 250_000_000) } catch { return }; await phone.searchContacts(search) }
            .sheet(item: $compose) { person in ContactMessageSheet(person: person).environmentObject(phone) }
    }
}
private struct ContactMessageSheet: View {
    let person: PhoneEntry
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Message \(person.text("name"))").font(.title2.weight(.semibold))
            Text(person.text("number")).foregroundStyle(.secondary)
            TextEditor(text: $draft).font(.body).frame(height: 160).border(.quaternary)
            if !phone.error.isEmpty { Text(phone.error).font(.caption).foregroundStyle(.orange) }
            HStack { Button("Cancel") { dismiss() }; Spacer(); Button("Send SMS") { Task { if await phone.send(number: person.text("number"), text: draft) { dismiss() } } }.buttonStyle(.borderedProminent).disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || phone.sending) }
        }.padding(24).frame(width: 440)
    }
}

struct PhoneNotificationsView: View {
    @EnvironmentObject var phone: PhoneWorkspaceModel
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var session: DeviceSession
    @State private var replying: PhoneEntry?
    @State private var replyText = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack { Text("Notification access and message previews are optional. Enable both on the phone to reply here.").font(.callout).foregroundStyle(.secondary); Spacer(); Button("Refresh") { Task { await phone.refresh() } }.disabled(!session.isConnected) }
            PhoneFeatureNotice()
            if phone.notifications.isEmpty { ContentUnavailableView("A quieter inbox", systemImage: "bell", description: Text("New phone notifications appear here after access is enabled.")) }
            ForEach(phone.notifications) { notice in
                Card {
                    VStack(alignment: .leading, spacing: 10) {
                        HStack { Label(notice.text("appLabel"), systemImage: "bell").font(.headline); Spacer(); Button("Dismiss") { Task { await phone.dismiss(notice.id) } }.controlSize(.small).disabled(!session.isConnected) }
                        if notice.flag("redacted") { Text("Content hidden by the phone").foregroundStyle(.secondary) }
                        else { if !notice.text("senderDisplay").isEmpty { Text(notice.text("senderDisplay")).fontWeight(.medium) }; Text(notice.text("text")).textSelection(.enabled) }
                        if notice.flag("canReply") { Button("Reply", systemImage: "arrowshape.turn.up.left") { replying = notice; replyText = "" }.disabled(!session.isConnected) }
                    }
                }
            }
        }.task { await phone.enter("notifications") }.onDisappear { phone.leave() }
            .sheet(item: $replying) { notice in
                VStack(alignment: .leading, spacing: 18) {
                    Text("Reply in \(notice.text("appLabel"))").font(.title2.weight(.semibold))
                    Text(notice.text("senderDisplay")).foregroundStyle(.secondary)
                    TextField("Your reply", text: $replyText, axis: .vertical).lineLimit(3...6).textFieldStyle(.roundedBorder)
                    if !phone.error.isEmpty { Text(phone.error).font(.caption).foregroundStyle(.orange) }
                    HStack { Button("Cancel") { replying = nil }; Spacer(); Button("Send reply") { Task { if await phone.reply(notice.id, text: replyText) { replying = nil } } }.buttonStyle(.borderedProminent).disabled(replyText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) }
                }.padding(24).frame(width: 440)
            }
    }
}
