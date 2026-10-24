package dev.pixelbridge.app.phone

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Person
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Handler
import android.os.Looper
import android.os.Build
import android.os.OutcomeReceiver
import android.telecom.CallEndpoint
import android.telecom.CallEndpointException
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
        CallRegistry.canAddCall = canAddCall()
        val callback = object : Call.Callback() {
            override fun onStateChanged(call: Call, state: Int) { CallRegistry.publish(); updateNotification() }
            override fun onDetailsChanged(call: Call, details: Call.Details) { CallRegistry.publish(); updateNotification() }
            override fun onConferenceableCallsChanged(call: Call, calls: MutableList<Call>) { CallRegistry.publish() }
            override fun onChildrenChanged(call: Call, children: MutableList<Call>) { CallRegistry.publish() }
            override fun onParentChanged(call: Call, parent: Call?) { CallRegistry.publish() }
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
        if (Build.VERSION.SDK_INT < 34) {
            CallRegistry.muted = audioState?.isMuted == true
            CallRegistry.route = audioState?.route ?: CallAudioState.ROUTE_EARPIECE
        }
        CallRegistry.legacyRoutes = audioState?.supportedRouteMask ?: 0
        CallRegistry.publish()
    }
    override fun onMuteStateChanged(isMuted: Boolean) {
        super.onMuteStateChanged(isMuted); CallRegistry.muted = isMuted; CallRegistry.publish()
    }
    override fun onCallEndpointChanged(endpoint: CallEndpoint) {
        super.onCallEndpointChanged(endpoint); CallRegistry.endpoint = endpoint; CallRegistry.publish()
    }
    override fun onAvailableCallEndpointsChanged(endpoints: MutableList<CallEndpoint>) {
        super.onAvailableCallEndpointsChanged(endpoints); CallRegistry.endpoints = endpoints.toList(); CallRegistry.publish()
    }
    override fun onCanAddCallChanged(canAddCall: Boolean) {
        super.onCanAddCallChanged(canAddCall); CallRegistry.canAddCall = canAddCall; CallRegistry.publish()
    }
    override fun onBringToForeground(showDialpad: Boolean) {
        startActivity(Intent(this, CallActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP))
    }
    override fun onDestroy() { if (CallRegistry.service === this) CallRegistry.service = null; callbacks.forEach { (call, callback) -> call.unregisterCallback(callback) }; callbacks.clear(); CallRegistry.clear(); super.onDestroy() }
    private fun updateNotification() {
        val manager = getSystemService(NotificationManager::class.java)
        val call = CallRegistry.firstCall()
        if (call == null) { stopForeground(STOP_FOREGROUND_REMOVE); manager.cancel(82); return }
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
        try {
            // Ongoing CallStyle notifications must be attached to the call foreground service.
            startForeground(82, builder.build(), ServiceInfo.FOREGROUND_SERVICE_TYPE_PHONE_CALL)
        } catch (e: RuntimeException) {
            // A rejected notification must never take down Android's default Phone app.
            android.util.Log.w("PixelBridge", "Call notification unavailable: ${e.javaClass.simpleName}")
            try {
                val fallback = builder.setStyle(null).setContentText("Open PixelBridge to manage this call")
                    .addAction(Notification.Action.Builder(null, "End call", action("phone.end")).build()).build()
                manager.notify(82, fallback)
            } catch (_: RuntimeException) { }
        }
    }
}

@Suppress("DEPRECATION")
object CallRegistry {
    @Volatile var service: BridgeCallService? = null
    @Volatile var muted = false
    @Volatile var route = CallAudioState.ROUTE_EARPIECE
    @Volatile var legacyRoutes = 0
    @Volatile var endpoint: CallEndpoint? = null
    @Volatile var endpoints: List<CallEndpoint> = emptyList()
    @Volatile var canAddCall = false
    private val calls = LinkedHashMap<String, Call>()
    private val main = Handler(Looper.getMainLooper())
    private var current = JSONObject().put("calls", JSONArray()).put("audioOn", "phone")
    val listeners = CopyOnWriteArraySet<() -> Unit>()
    @Synchronized fun add(call: Call) { calls[UUID.randomUUID().toString()] = call }
    @Synchronized fun remove(call: Call) { calls.entries.removeAll { it.value === call }; publish() }
    @Synchronized fun clear() { calls.clear(); muted = false; route = CallAudioState.ROUTE_EARPIECE; endpoint = null; endpoints = emptyList(); legacyRoutes = 0; canAddCall = false; publish() }
    @Synchronized fun idFor(call: Call) = calls.entries.firstOrNull { it.value === call }?.key ?: ""
    @Synchronized fun firstCall(): Call? = calls.values.firstOrNull { it.state == Call.STATE_RINGING } ?: calls.values.firstOrNull()
    @Synchronized fun snapshot(): JSONObject = JSONObject(current.toString())
    @Synchronized fun publish() {
        val rows = JSONArray()
        for ((id, call) in calls) {
            val details = call.details
            rows.put(JSONObject().put("id", id).put("number", details?.handle?.schemeSpecificPart?.take(150) ?: "Unknown")
                .put("state", when (call.state) { Call.STATE_RINGING -> "ringing"; Call.STATE_ACTIVE -> "active"; Call.STATE_HOLDING -> "held";
                    Call.STATE_DIALING, Call.STATE_CONNECTING -> "dialing"; Call.STATE_DISCONNECTED -> "ended"; Call.STATE_SELECT_PHONE_ACCOUNT -> "choose-sim"; Call.STATE_DISCONNECTING -> "ending"; else -> "connecting" })
                .put("canHold", details?.can(Call.Details.CAPABILITY_HOLD) == true)
                .put("canMerge", details?.can(Call.Details.CAPABILITY_MERGE_CONFERENCE) == true || call.conferenceableCalls.isNotEmpty())
                .put("canSwap", details?.can(Call.Details.CAPABILITY_SWAP_CONFERENCE) == true)
                .put("isConference", details?.hasProperty(Call.Details.PROPERTY_CONFERENCE) == true)
                .put("connectedAtMs", details?.connectTimeMillis ?: 0))
        }
        val available = JSONArray()
        if (Build.VERSION.SDK_INT >= 34) endpoints.forEach { e ->
            available.put(JSONObject().put("id", e.identifier.toString()).put("name", e.endpointName.toString())
                .put("route", endpointRoute(e)).put("selected", e.identifier == endpoint?.identifier))
        } else listOf("phone" to CallAudioState.ROUTE_WIRED_OR_EARPIECE, "speaker" to CallAudioState.ROUTE_SPEAKER, "bluetooth" to CallAudioState.ROUTE_BLUETOOTH).forEach { (name, mask) ->
            if (legacyRoutes and mask != 0) available.put(JSONObject().put("id", name).put("name", name.replaceFirstChar { it.uppercase() }).put("route", name))
        }
        val selectedRoute = if (Build.VERSION.SDK_INT >= 34 && endpoint != null) endpointRoute(endpoint!!) else when(route) { CallAudioState.ROUTE_SPEAKER -> "speaker"; CallAudioState.ROUTE_BLUETOOTH -> "bluetooth"; else -> "phone" }
        current = JSONObject().put("calls", rows).put("muted", muted).put("route", selectedRoute).put("endpoints", available)
            .put("canAddCall", canAddCall).put("audioOn", "phone")
        PhoneBridge.events?.invoke(JSONObject(current.toString()).put("event", "phone.calls"))
        listeners.forEach { runCatching { it() } }
    }
    private fun endpointRoute(e: CallEndpoint): String = when(e.endpointType) {
        CallEndpoint.TYPE_SPEAKER -> "speaker"; CallEndpoint.TYPE_BLUETOOTH -> "bluetooth"; CallEndpoint.TYPE_WIRED_HEADSET -> "wired"; CallEndpoint.TYPE_STREAMING -> "streaming"; else -> "phone"
    }
    fun request(op: String, args: JSONObject, event: (JSONObject) -> Unit) {
        main.post {
            try {
                val service = service ?: throw IllegalStateException("Android has not connected call controls yet. Enable PixelBridge as the default Phone app.")
                val call = synchronized(this) { val id = args.optString("callId"); if (id.isNotBlank()) calls[id] else firstCall() }
                when(op) {
                    "phone.mute" -> service.setMuted(args.optBoolean("enabled"))
                    "phone.route" -> {
                        val name = args.optString("route")
                        if (Build.VERSION.SDK_INT >= 34) {
                            val id = args.optString("endpointId")
                            val target = endpoints.firstOrNull { if (id.isNotBlank()) it.identifier.toString() == id else endpointRoute(it) == name }
                                ?: throw IllegalArgumentException("This audio route is not available on the phone")
                            service.requestCallEndpointChange(target, service.mainExecutor, object : OutcomeReceiver<Void, CallEndpointException> {
                                override fun onResult(result: Void?) { publish() }
                                override fun onError(error: CallEndpointException) { event(JSONObject().put("event", "phone.error").put("message", "Audio route could not change: ${error.message}")) }
                            })
                        } else {
                            val wanted = when(name) { "speaker" -> CallAudioState.ROUTE_SPEAKER; "bluetooth" -> CallAudioState.ROUTE_BLUETOOTH; "phone" -> CallAudioState.ROUTE_WIRED_OR_EARPIECE; else -> throw IllegalArgumentException("Unknown audio route") }
                            require(legacyRoutes and wanted != 0) { "This audio route is not available on the phone" }; service.setAudioRoute(wanted)
                        }
                    }
                    else -> {
                        requireNotNull(call) { "This call has ended" }
                        when(op) {
                            "phone.answer" -> { require(call.state == Call.STATE_RINGING) { "This call is no longer ringing" }; call.answer(VideoProfile.STATE_AUDIO_ONLY) }
                            "phone.end" -> if (call.state == Call.STATE_RINGING) call.reject(false, null) else call.disconnect()
                            "phone.reject" -> { require(call.state == Call.STATE_RINGING) { "This call is no longer ringing" }; call.reject(false, null) }
                            "phone.merge" -> if (call.conferenceableCalls.isNotEmpty()) call.conference(call.conferenceableCalls.first()) else { require(call.details.can(Call.Details.CAPABILITY_MERGE_CONFERENCE)); call.mergeConference() }
                            "phone.swap" -> { require(call.details.can(Call.Details.CAPABILITY_SWAP_CONFERENCE)); call.swapConference() }
                            "phone.split" -> { require(call.details.can(Call.Details.CAPABILITY_SEPARATE_FROM_CONFERENCE)); call.splitFromConference() }
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
