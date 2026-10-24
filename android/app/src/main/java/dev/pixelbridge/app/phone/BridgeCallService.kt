package dev.pixelbridge.app.phone

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Person
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.telecom.Call
import android.telecom.CallAudioState
import android.telecom.InCallService
import android.telecom.VideoProfile
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.CopyOnWriteArraySet

/** Optional default-dialer integration. Audio stays in Android Telecom, never captured as microphone PCM. */
@Suppress("DEPRECATION")
class BridgeCallService : InCallService() {
    private val callbacks = HashMap<Call, Call.Callback>()
    override fun onCreate() { super.onCreate(); CallRegistry.service = this }
    override fun onCallAdded(call: Call) {
        super.onCallAdded(call)
        CallRegistry.add(call)
        val callback = object : Call.Callback() {
            override fun onStateChanged(call: Call, state: Int) { CallRegistry.publish(); updateNotification() }
            override fun onDetailsChanged(call: Call, details: Call.Details) { CallRegistry.publish(); updateNotification() }
        }
        callbacks[call] = callback; call.registerCallback(callback, Handler(Looper.getMainLooper()))
        CallRegistry.publish(); updateNotification()
    }
    override fun onCallRemoved(call: Call) {
        callbacks.remove(call)?.let { call.unregisterCallback(it) }; CallRegistry.remove(call); updateNotification()
        super.onCallRemoved(call)
    }
    override fun onCallAudioStateChanged(audioState: CallAudioState?) {
        super.onCallAudioStateChanged(audioState)
        CallRegistry.muted = audioState?.isMuted == true; CallRegistry.route = audioState?.route ?: CallAudioState.ROUTE_EARPIECE
        CallRegistry.publish()
    }
    override fun onBringToForeground(showDialpad: Boolean) {
        startActivity(Intent(this, CallActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP))
    }
    override fun onDestroy() { if (CallRegistry.service === this) CallRegistry.service = null; callbacks.forEach { (call, callback) -> call.unregisterCallback(callback) }; callbacks.clear(); CallRegistry.clear(); super.onDestroy() }
    private fun updateNotification() {
        val manager = getSystemService(NotificationManager::class.java)
        val call = CallRegistry.firstCall()
        if (call == null) { manager.cancel(82); return }
        manager.createNotificationChannel(NotificationChannel("calls", "Phone calls", NotificationManager.IMPORTANCE_HIGH).apply { setSound(null, null) })
        val show = PendingIntent.getActivity(this, 0, Intent(this, CallActivity::class.java), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        fun action(op: String) = PendingIntent.getBroadcast(this, op.hashCode(), Intent(this, CallActionReceiver::class.java)
            .putExtra("op", op).putExtra("callId", CallRegistry.idFor(call)), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val person = Person.Builder().setName(call.details?.handle?.schemeSpecificPart ?: "Phone call").setImportant(true).build()
        val incoming = call.state == Call.STATE_RINGING
        val style = if (incoming) Notification.CallStyle.forIncomingCall(person, action("phone.end"), action("phone.answer"))
            else Notification.CallStyle.forOngoingCall(person, action("phone.end"))
        val builder = Notification.Builder(this, "calls").setSmallIcon(dev.pixelbridge.app.R.drawable.ic_bridge)
            .setContentTitle(if (incoming) "Incoming call" else "Phone call").setContentIntent(show).setOngoing(true)
            .setCategory(Notification.CATEGORY_CALL).setStyle(style)
        if (incoming) builder.setFullScreenIntent(show, true)
        try { manager.notify(82, builder.build()) } catch (_: SecurityException) { /* Call remains available in the phone activity and USB UI. */ }
    }
}

@Suppress("DEPRECATION")
object CallRegistry {
    @Volatile var service: BridgeCallService? = null
    @Volatile var muted = false
    @Volatile var route = CallAudioState.ROUTE_EARPIECE
    private val calls = LinkedHashMap<String, Call>()
    private val main = Handler(Looper.getMainLooper())
    private var current = JSONObject().put("calls", JSONArray()).put("audioOn", "phone")
    val listeners = CopyOnWriteArraySet<() -> Unit>()
    @Synchronized fun add(call: Call) { calls[UUID.randomUUID().toString()] = call }
    @Synchronized fun remove(call: Call) { calls.entries.removeAll { it.value === call }; publish() }
    @Synchronized fun clear() { calls.clear(); publish() }
    @Synchronized fun idFor(call: Call) = calls.entries.firstOrNull { it.value === call }?.key ?: ""
    @Synchronized fun firstCall(): Call? = calls.values.firstOrNull { it.state == Call.STATE_RINGING } ?: calls.values.firstOrNull()
    @Synchronized fun snapshot(): JSONObject = JSONObject(current.toString())
    @Synchronized fun publish() {
        val rows = JSONArray()
        for ((id, call) in calls) {
            val details = call.details
            rows.put(JSONObject().put("id", id).put("number", details?.handle?.schemeSpecificPart?.take(150) ?: "Unknown")
                .put("state", when (call.state) { Call.STATE_RINGING -> "ringing"; Call.STATE_ACTIVE -> "active"; Call.STATE_HOLDING -> "held";
                    Call.STATE_DIALING, Call.STATE_CONNECTING -> "dialing"; Call.STATE_DISCONNECTED -> "ended"; else -> "connecting" })
                .put("canHold", details?.can(Call.Details.CAPABILITY_HOLD) == true).put("connectedAtMs", details?.connectTimeMillis ?: 0))
        }
        current = JSONObject().put("calls", rows).put("muted", muted).put("route", when(route) { CallAudioState.ROUTE_SPEAKER -> "speaker"; CallAudioState.ROUTE_BLUETOOTH -> "bluetooth"; else -> "phone" }).put("audioOn", "phone")
        PhoneBridge.events?.invoke(JSONObject(current.toString()).put("event", "phone.calls"))
        listeners.forEach { runCatching { it() } }
    }
    fun request(op: String, args: JSONObject, event: (JSONObject) -> Unit) {
        main.post {
            try {
                val service = service ?: throw IllegalStateException("Android has not connected call controls yet. Enable PixelBridge as the default Phone app.")
                val call = synchronized(this) { val id = args.optString("callId"); if (id.isNotBlank()) calls[id] else firstCall() }
                when(op) {
                    "phone.mute" -> service.setMuted(args.optBoolean("enabled"))
                    "phone.route" -> {
                        val wanted = when(args.getString("route")) { "speaker" -> CallAudioState.ROUTE_SPEAKER; "bluetooth" -> CallAudioState.ROUTE_BLUETOOTH; "phone" -> CallAudioState.ROUTE_WIRED_OR_EARPIECE; else -> throw IllegalArgumentException("Unknown audio route") }
                        require((service.callAudioState?.supportedRouteMask ?: 0) and wanted != 0) { "This audio route is not available on the phone" }
                        service.setAudioRoute(wanted)
                    }
                    else -> {
                        requireNotNull(call) { "This call has ended" }
                        when(op) {
                            "phone.answer" -> { require(call.state == Call.STATE_RINGING) { "This call is no longer ringing" }; call.answer(VideoProfile.STATE_AUDIO_ONLY) }
                            "phone.end" -> call.disconnect()
                            "phone.hold" -> { require(call.details.can(Call.Details.CAPABILITY_HOLD)); call.hold() }
                            "phone.unhold" -> call.unhold()
                            "phone.dtmf" -> { val digit = args.getString("digit"); require(digit.matches(Regex("[0-9*#]"))); call.playDtmfTone(digit[0]); main.postDelayed({ call.stopDtmfTone() }, 200) }
                        }
                    }
                }
            } catch (e: Exception) { event(JSONObject().put("event", "phone.error").put("message", e.message ?: "Call action failed")) }
        }
    }
}

class CallActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val args = JSONObject().put("callId", intent.getStringExtra("callId") ?: "")
        CallRegistry.request(intent.getStringExtra("op") ?: "", args) { PhoneBridge.events?.invoke(it) }
    }
}
