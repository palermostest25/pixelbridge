using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixelBridge.Core;

public sealed class PhoneFeatureException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public sealed record TextThread(long Id, string Address, string Preview, long DateMs, bool Unread);
public sealed record PhoneText(long Id, string Address, string Body, long DateMs, bool Incoming, int Type);
public sealed record PhoneContact(long Id, string Name, string Number) { public override string ToString() => $"{Name} · {Number}"; }
public sealed record PhoneCall(string Id, string Number, string State, bool CanHold, long ConnectedAtMs);
public sealed record RecentCall(long Id, string Number, string Name, long DateMs, long DurationSeconds, int Type);
public sealed record PhoneApp(string Name, string Component) { public override string ToString() => Name; }
public sealed record PhoneNotification(string Key, string App, string Sender, string? Text, bool Redacted, bool CanReply, long PostedMs);

/// <summary>Ephemeral phone data. Reads happen on demand; sending is explicit and never retried automatically.</summary>
public sealed class PhoneWorkspace
{
    private readonly Func<string, object?, Task<JsonObject>> _rpc;
    private int _messageGeneration, _contactGeneration, _sending, _dataGeneration;
    private bool _enabled = true, _watching;
    private string? _pendingToken, _pendingFingerprint;
    public IReadOnlyList<TextThread> Threads { get; private set; } = [];
    public IReadOnlyList<PhoneText> Messages { get; private set; } = [];
    public IReadOnlyList<PhoneContact> Contacts { get; private set; } = [];
    public IReadOnlyList<PhoneCall> Calls { get; private set; } = [];
    public IReadOnlyList<RecentCall> History { get; private set; } = [];
    public IReadOnlyList<PhoneApp> Apps { get; private set; } = [];
    public IReadOnlyList<PhoneNotification> Notifications { get; private set; } = [];
    public JsonObject? Status { get; private set; }
    public JsonObject? Tools { get; private set; }
    public string Error { get; private set; } = "";
    public string SendStatus { get; private set; } = "";
    public long? SelectedThread { get; private set; }
    private bool _notificationsEnabled = true, _notificationPreviews = true;
    public bool NotificationsEnabled => _notificationsEnabled;
    public bool NotificationPreviews => _notificationPreviews;
    public bool Sending => Volatile.Read(ref _sending) != 0;
    public bool Dialer => (bool?)Status?["dialer"] == true;
    public bool Muted { get; private set; }
    public string CallRoute { get; private set; } = "phone";
    public event Action? Changed;
    public event Action? PhoneDataChanged;
    public PhoneWorkspace(DeviceSession session) : this(async (op, args) => await session.RadioAsync(op, args, TimeSpan.FromSeconds(20)))
    {
        session.RadioEvent += OnEvent;
        session.Notification += AddNotification;
        session.StateChanged += (state, detail) => { if (state != LinkState.Connected) Clear(); };
    }
    public PhoneWorkspace(Func<string, object?, Task<JsonObject>> rpc) { _rpc = rpc; }
    public async Task<JsonObject> RequestAsync(string op, object? args = null)
    {
        if (!_enabled) throw new PhoneFeatureException("privacy", "Turn off privacy mode to use phone features");
        var generation = Volatile.Read(ref _dataGeneration);
        var response = await _rpc(op, args).ConfigureAwait(false);
        if (!_enabled || generation != Volatile.Read(ref _dataGeneration)) throw new PhoneFeatureException("changed", "The phone connection changed; refresh this view");
        if ((bool?)response["ok"] != true) throw new PhoneFeatureException((string?)response["error"] ?? "unavailable", (string?)response["message"] ?? "The phone could not complete this request");
        return response["result"]?.AsObject() ?? new JsonObject();
    }
    private async Task<bool> Perform(Func<Task> action)
    {
        try { Error = ""; await action().ConfigureAwait(false); return true; }
        catch (Exception e) { Error = e.Message; return false; }
        finally { Changed?.Invoke(); }
    }
    public Task<bool> RefreshStatusAsync() => Perform(async () => { Status = await RequestAsync("phone.status"); ApplyCalls(Status["calls"] as JsonObject); });
    public Task<bool> WatchAsync(bool on) => Perform(async () => { if (_watching == on) return; await RequestAsync("phone.watch", new { enabled = on }); _watching = on; });
    public Task<bool> RefreshThreadsAsync() => Perform(async () =>
    {
        var result = await RequestAsync("sms.threads");
        Threads = Rows(result, "threads").Select(x => new TextThread(Long(x, "id"), Str(x, "address"), Str(x, "preview"), Long(x, "dateMs"), Bool(x, "unread"))).ToArray();
    });
    public Task<bool> LoadMessagesAsync(long threadId) => Perform(async () =>
    {
        int generation = Interlocked.Increment(ref _messageGeneration); SelectedThread = threadId;
        var result = await RequestAsync("sms.messages", new { threadId });
        if (generation != Volatile.Read(ref _messageGeneration) || SelectedThread != threadId) return;
        Messages = Rows(result, "messages").Select(x => new PhoneText(Long(x, "id"), Str(x, "address"), Str(x, "body"), Long(x, "dateMs"), Bool(x, "incoming"), (int)Long(x, "type"))).OrderBy(x => x.DateMs).ThenBy(x => x.Id).ToArray();
    });
    public Task<bool> SearchContactsAsync(string query = "") => Perform(async () =>
    {
        int generation = Interlocked.Increment(ref _contactGeneration);
        var result = await RequestAsync("contacts.list", new { query, limit = 100 });
        if (generation != Volatile.Read(ref _contactGeneration)) return;
        Contacts = Rows(result, "contacts").Select(x => new PhoneContact(Long(x, "id"), Str(x, "name"), Str(x, "number"))).ToArray();
    });
    public Task<bool> RefreshHistoryAsync() => Perform(async () =>
    {
        var result = await RequestAsync("phone.history");
        History = Rows(result, "calls").Select(x => new RecentCall(Long(x, "id"), Str(x, "number"), Str(x, "name"), Long(x, "dateMs"), Long(x, "durationSeconds"), (int)Long(x, "type"))).ToArray();
    });
    public async Task<bool> SendTextAsync(string number, string text)
    {
        if (Interlocked.Exchange(ref _sending, 1) != 0) return false;
        try
        {
            if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(text) || text.Length > 12000) { Error = "Enter a phone number and a message of up to 12000 characters"; return false; }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(number.Trim() + "\0" + text)));
            if (_pendingFingerprint == fingerprint && _pendingToken != null)
            {
                // A lost acknowledgement is ambiguous: query the receipt, never repeat the send.
                var known = await RequestAsync("sms.receipt", new { token = _pendingToken });
                SendStatus = "Previous send: " + Str(known, "state") + ". Check the conversation before starting another message.";
                return false;
            }
            var token = Guid.NewGuid().ToString("N"); _pendingToken = token; _pendingFingerprint = fingerprint;
            SendStatus = "Sending to the phone…";
            var result = await RequestAsync("sms.send", new { number, text, token });
            SendStatus = ReceiptLabel(result); return true;
        }
        catch (PhoneFeatureException e) { Error = e.Message; SendStatus = "Not sent by the desktop. Check the phone's message history if the connection was interrupted."; return false; }
        catch (Exception e) { Error = e.Message; SendStatus = "Send confirmation unavailable. Nothing will be retried automatically."; return false; }
        finally { Interlocked.Exchange(ref _sending, 0); Changed?.Invoke(); }
    }
    public Task<bool> CallAsync(string number) => Perform(async () => { await RequestAsync("phone.call", new { number }); });
    public Task<bool> CallActionAsync(string action, string? callId = null, object? args = null) => Perform(async () =>
    {
        var values = args == null ? new JsonObject() : JsonSerializer.SerializeToNode(args)!.AsObject();
        if (callId != null) values["callId"] = callId;
        await RequestAsync(action, values);
    });
    public Task<bool> RefreshToolsAsync() => Perform(async () => Tools = await RequestAsync("tools.status"));
    public Task<bool> ToolAsync(string op, object? args = null) => Perform(async () => { var result = await RequestAsync(op, args); if (op is "tools.volume" or "tools.flashlight") Tools = result; });
    public Task<bool> RefreshAppsAsync() => Perform(async () => { var result = await RequestAsync("tools.apps"); Apps = Rows(result, "apps").Select(x => new PhoneApp(Str(x, "name"), Str(x, "component"))).ToArray(); });
    public Task<bool> RefreshNotificationsAsync() => Perform(async () => { if (!_notificationsEnabled) throw new PhoneFeatureException("disabled", "Enable notifications in this view to load them"); var result = await RequestAsync("notifications.list"); Notifications = Rows(result, "notifications").Select(ParseNotification).ToArray(); });
    public Task<bool> ReplyAsync(string key, string text) => Perform(async () => { await RequestAsync("notifications.reply", new { key, text }); SendStatus = "Reply handed to the phone app"; });
    public Task<bool> DismissAsync(string key) => Perform(async () => { await RequestAsync("notifications.dismiss", new { key }); Notifications = Notifications.Where(n => n.Key != key).ToArray(); });
    private void AddNotification(JsonObject row)
    {
        if (!_enabled || !_notificationsEnabled) return;
        var item = ParseNotification(row); Notifications = new[] { item }.Concat(Notifications.Where(n => n.Key != item.Key)).Take(100).ToArray(); Changed?.Invoke();
    }
    private PhoneNotification ParseNotification(JsonObject x) { bool hidden = Bool(x, "redacted") || !_notificationPreviews; return new(Str(x, "key"), Str(x, "appLabel"), hidden ? "" : Str(x, "senderDisplay"), hidden ? null : Str(x, "text"), hidden, !hidden && Bool(x, "canReply"), Long(x, "postedMs")); }
    public void OnEvent(JsonObject row)
    {
        if (!_enabled) return;
        switch (Str(row, "event"))
        {
            case "phone.calls": ApplyCalls(row); break;
            case "phone.changed": PhoneDataChanged?.Invoke(); break;
            case "phone.error": Error = Str(row, "message"); break;
            case "sms.receipt": if (Str(row, "token") == _pendingToken) SendStatus = ReceiptLabel(row); break;
            case "notifications.removed": Notifications = Notifications.Where(n => n.Key != Str(row, "key")).ToArray(); break;
            default: return;
        }
        Changed?.Invoke();
    }
    private void ApplyCalls(JsonObject? row)
    {
        if (row == null) return;
        Calls = Rows(row, "calls").Select(x => new PhoneCall(Str(x, "id"), Str(x, "number"), Str(x, "state"), Bool(x, "canHold"), Long(x, "connectedAtMs"))).ToArray();
        Muted = Bool(row, "muted"); CallRoute = Str(row, "route");
    }
    private async Task StopWatchSafely() { try { await _rpc("phone.watch", new { enabled = false }); } catch { } }
    public void ConfigureNotifications(bool enabled, bool previews)
    {
        _notificationsEnabled = enabled; _notificationPreviews = previews;
        if (!enabled) Notifications = [];
        else if (!previews) Notifications = Notifications.Select(n => n with { Sender = "", Text = null, Redacted = true, CanReply = false }).ToArray();
        Changed?.Invoke();
    }
    public void SetEnabled(bool enabled) { if (!enabled && _watching) _ = StopWatchSafely(); _enabled = enabled; if (!enabled) Clear(); }
    public void BeginNewMessage() { _pendingToken = null; _pendingFingerprint = null; SendStatus = ""; Changed?.Invoke(); }
    public void Clear()
    {
        Interlocked.Increment(ref _dataGeneration); Interlocked.Increment(ref _messageGeneration); Interlocked.Increment(ref _contactGeneration);
        Threads = []; Messages = []; Contacts = []; Calls = []; History = []; Apps = []; Notifications = []; Status = null; Tools = null; _watching = false; SelectedThread = null; Changed?.Invoke();
    }
    public static string ReceiptLabel(JsonObject row) => Str(row, "state") switch { "queued" => "Queued on phone", "sent" => "Sent to carrier", "delivered" => "Delivery confirmed", "failed" => "Send failed: " + Str(row, "error"), _ => "Status unknown—check the phone" };
    private static IEnumerable<JsonObject> Rows(JsonObject node, string key) => (node[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>();
    private static string Str(JsonObject n, string k) => (string?)n[k] ?? "";
    private static long Long(JsonObject n, string k) => (long?)n[k] ?? 0;
    private static bool Bool(JsonObject n, string k) => (bool?)n[k] ?? false;
}
