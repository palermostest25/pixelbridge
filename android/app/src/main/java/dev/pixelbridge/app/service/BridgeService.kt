package dev.pixelbridge.app.service

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.SystemClock
import android.util.Log
import dev.pixelbridge.app.BuildConfig
import dev.pixelbridge.app.MainActivity
import dev.pixelbridge.app.audio.MicStreamer
import dev.pixelbridge.app.camera.CameraStreamer
import dev.pixelbridge.app.notifications.NotificationRelayService
import dev.pixelbridge.app.sensors.SensorStreamer
import dev.pixelbridge.app.status.BatteryMonitor
import dev.pixelbridge.app.transport.ChannelServer
import dev.pixelbridge.app.transport.Frame
import dev.pixelbridge.app.transport.PairingStore
import org.json.JSONObject

/**
 * Foreground service owning the four loopback channels. Started only from the visible MainActivity
 * (or its notification), so camera/microphone FGS types are legitimately acquired while in use.
 */
class BridgeService : Service() {
    private lateinit var pairing: PairingStore
    private lateinit var control: ChannelServer
    private lateinit var video: ChannelServer
    private lateinit var audio: ChannelServer
    private lateinit var telemetry: ChannelServer
    private lateinit var sensors: SensorStreamer
    private lateinit var battery: BatteryMonitor
    private lateinit var camera: CameraStreamer
    private lateinit var mic: MicStreamer
    private lateinit var location: dev.pixelbridge.app.sensors.LocationStreamer
    private lateinit var phone: dev.pixelbridge.app.phone.PhoneBridge
    private lateinit var radio: dev.pixelbridge.app.radio.RadioBridge
    private var started = false
    private var mediaForegroundAllowed = false
    @Volatile private var paused = false
    @Volatile private var lastHeartbeatMs = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        pairing = PairingStore(this)
        val key = { pairing.load() }
        val extras = { mapOf<String, Any>("appVersion" to BuildConfig.VERSION_NAME, "deviceModel" to Build.MODEL) }
        control = ChannelServer("control", 27183, 65536, 64, false, key, extras, controlListener)
        video = ChannelServer("video", 27184, 2 * 1024 * 1024, 8, false, key, extras, object : ChannelServer.Listener {
            override fun onConnected(channel: ChannelServer) { if (camera.running) camera.resendConfig() }
        })
        audio = ChannelServer("audio", 27185, 16384, 16, true, key, extras, object : ChannelServer.Listener {
            override fun onConnected(channel: ChannelServer) { if (mic.running) mic.resendConfig() }
        })
        telemetry = ChannelServer("telemetry", 27186, 65536, 512, true, key, extras, object : ChannelServer.Listener {})
        sensors = SensorStreamer(this) { telemetry.send(it) }
        battery = BatteryMonitor(this) { f ->
            telemetry.send(f)
            thermalGuard()
        }
        camera = CameraStreamer(this, { video.send(it) }, ::controlEvent)
        mic = MicStreamer(this, { audio.send(it) }, ::controlEvent)
        location = dev.pixelbridge.app.sensors.LocationStreamer(this) { telemetry.send(it) }
        radio = dev.pixelbridge.app.radio.RadioBridge(this,
            { _, j -> control.send(Frame.json(Frame.RADIO_RESPONSE, j.toString())) },
            { j -> telemetry.send(Frame.json(Frame.RADIO_EVENT, j.toString(), SystemClock.elapsedRealtimeNanos())) })
        phone = dev.pixelbridge.app.phone.PhoneBridge(this,
            { j -> control.send(Frame.json(Frame.RADIO_RESPONSE, j.toString())) },
            { j -> if (!paused) telemetry.send(Frame.json(Frame.RADIO_EVENT, j.toString(), SystemClock.elapsedRealtimeNanos())) })
        NotificationRelayService.sink = { j ->
            if (!paused) telemetry.send(Frame.json(Frame.NOTIFICATION_EVENT, j.toString(), SystemClock.elapsedRealtimeNanos()))
        }
        instance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_BOOT && started) return START_NOT_STICKY
        if (intent?.action == ACTION_BOOT) mediaForegroundAllowed = false
        else if (intent?.action == null) mediaForegroundAllowed = true
        when (intent?.action) {
            ACTION_STOP -> { stopEverything(); stopSelf(); return START_NOT_STICKY }
            ACTION_PAUSE, ACTION_RESUME -> {
                // Already foreground; just update state and the notification (no new FGS type request).
                if (intent.action == ACTION_PAUSE) pauseAll() else { paused = false; publishStatus() }
                getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification())
                notifyUi()
                return START_NOT_STICKY
            }
        }
        try {
            startForeground(NOTIFICATION_ID, buildNotification(), fgsTypes())
        } catch (e: Exception) {
            // Typically ForegroundServiceStartNotAllowedException when not started from a visible activity.
            Log.w(TAG, "startForeground failed: ${e.message}")
            lastError = "startForeground: ${e.javaClass.simpleName}"
            stopSelf()
            return START_NOT_STICKY
        }
        if (!started) {
            try {
                control.start(); video.start(); audio.start(); telemetry.start()
                started = true
            } catch (e: Exception) {
                lastError = "listen failed: ${e.message}"
                Log.e(TAG, lastError!!)
            }
        }
        phone.refreshPermissions()
        notifyUi()
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stopEverything()
        radio.shutdown()
        phone.close()
        control.close(); video.close(); audio.close(); telemetry.close()
        NotificationRelayService.sink = null
        if (instance === this) instance = null
        notifyUi()
        super.onDestroy()
    }

    private fun fgsTypes(): Int {
        var t = ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        if (mediaForegroundAllowed && granted(Manifest.permission.CAMERA)) t = t or ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
        if (mediaForegroundAllowed && granted(Manifest.permission.RECORD_AUDIO)) t = t or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
        if (mediaForegroundAllowed && granted(Manifest.permission.ACCESS_FINE_LOCATION)) t = t or ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION
        return t
    }

    private fun granted(p: String) = checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED

    private val controlListener = object : ChannelServer.Listener {
        override fun onConnected(channel: ChannelServer) {
            lastHeartbeatMs = SystemClock.elapsedRealtime()
            battery.start()
            channel.send(Frame.json(Frame.CAPABILITIES, capabilities().toString()))
            publishStatus()
            notifyUi()
        }

        override fun onDisconnected(channel: ChannelServer) {
            // Host gone (unplug, ADB reset or app exit): release camera/mic/sensors immediately.
            stopStreams("host-disconnected")
            radio.reset()
            phone.reset()
            notifyUi()
        }

        override fun onFrame(channel: ChannelServer, frame: Frame) {
            when (frame.type) {
                Frame.HEARTBEAT -> {
                    lastHeartbeatMs = SystemClock.elapsedRealtime()
                    val host = JSONObject(frame.text()).optLong("hostNs")
                    channel.send(Frame.json(Frame.PONG, JSONObject().put("hostNs", host)
                        .put("deviceNs", SystemClock.elapsedRealtimeNanos()).toString()))
                }
                Frame.START_STREAM -> handleStart(JSONObject(frame.text()))
                Frame.STOP_STREAM -> handleStop(JSONObject(frame.text()).getString("kind"))
                Frame.REQUEST_IDR -> camera.requestIdr()
                Frame.RADIO_REQUEST -> if (paused) channel.send(Frame.json(Frame.RADIO_RESPONSE, JSONObject().put("id", JSONObject(frame.text()).optInt("id"))
                    .put("ok", false).put("error", "paused").put("message", "Paused from the phone").toString())) else { val request = JSONObject(frame.text()); if (dev.pixelbridge.app.phone.PhoneBridge.handles(request.optString("op"))) phone.handle(request) else radio.handle(request) }
                else -> channel.send(Frame.json(Frame.ERROR, JSONObject().put("code", "protocol")
                    .put("message", "unexpected type ${frame.type}").put("recoverable", true).toString()))
            }
        }
    }

    private fun handleStart(req: JSONObject) {
        val kind = req.getString("kind")
        val o = req.optJSONObject("options") ?: JSONObject()
        if (paused && kind != "sensors") {
            controlEvent(Frame.STREAM_ERROR, JSONObject().put("kind", kind).put("code", "paused")
                .put("message", "Paused from the phone notification"))
            return
        }
        when (kind) {
            "sensors" -> {
                val types = o.optJSONArray("types")?.let { a -> (0 until a.length()).map { a.optInt(it) }.toSet() }
                sensors.start(o.optInt("rateHz", 50), types)
                controlEvent(Frame.STREAM_STARTED, JSONObject().put("kind", "sensors").put("rateHz", o.optInt("rateHz", 50)))
            }
            "video" -> {
                if (thermalHot()) { controlEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "video").put("code", "thermal")
                    .put("message", "Battery above ${THERMAL_LIMIT_C}C")); return }
                if (fgsTypes() and ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA == 0) {
                    controlEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "video").put("code", "permission")
                        .put("message", "Grant camera permission in the PixelBridge app")); return
                }
                if (o.optBoolean("updateOnly", false)) camera.updateControls(o)
                else camera.start(o.optString("cameraId", "0"), o.optInt("width", 1280), o.optInt("height", 720),
                    o.optInt("fps", 30), o.optInt("bitrate", 4_000_000),
                    o.optDouble("zoomRatio", 1.0).toFloat(), o.optString("stabilization", "auto"))
            }
            "audio" -> mic.start(o.optInt("rateHz", 48000))
            "location" -> {
                val err = location.start(o.optLong("intervalMs", 1000))
                if (err == null) controlEvent(Frame.STREAM_STARTED, JSONObject().put("kind", "location"))
                else controlEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "location").put("code", "permission").put("message", err))
            }
            else -> controlEvent(Frame.STREAM_ERROR, JSONObject().put("kind", kind).put("code", "unsupported").put("message", "unknown kind"))
        }
        publishStatus()
        notifyUi()
    }

    private fun handleStop(kind: String) {
        when (kind) {
            "sensors" -> { sensors.stop(); controlEvent(Frame.STREAM_STOPPED, JSONObject().put("kind", "sensors").put("reason", "host")) }
            "video" -> camera.stop("host")
            "audio" -> mic.stop("host")
            "location" -> { location.stop(); controlEvent(Frame.STREAM_STOPPED, JSONObject().put("kind", "location").put("reason", "host")) }
        }
        publishStatus()
        notifyUi()
    }

    private fun controlEvent(type: Int, j: JSONObject) {
        control.send(Frame.json(type, j.toString(), SystemClock.elapsedRealtimeNanos()))
        if (type == Frame.STREAM_ERROR) lastError = "${j.optString("kind")}: ${j.optString("message")}"
        notifyUi()
    }

    private fun capabilities() = JSONObject()
        .put("cameras", camera.capabilities())
        .put("sensors", sensors.capabilities())
        .put("cameraPermission", granted(Manifest.permission.CAMERA))
        .put("audio", JSONObject().put("micPermission", granted(Manifest.permission.RECORD_AUDIO)))
        .put("locationAvailable", location.permitted())
        .put("notificationAccess", NotificationRelayService.connected)
        .put("tetheringStatus", "not-evaluated-by-app")
        .put("radio", radio.capabilities())
        .put("phone", phone.capabilities())
        .put("android", JSONObject().put("sdk", Build.VERSION.SDK_INT).put("release", Build.VERSION.RELEASE)
            .put("build", Build.ID).put("model", Build.MODEL).put("device", Build.DEVICE))

    fun moduleStates(): JSONObject = JSONObject()
        .put("control", if (control.isConnected) "streaming" else "ready")
        .put("sensors", if (sensors.running) "streaming" else "off")
        .put("video", when {
            paused -> "paused"; camera.running -> "streaming"
            !granted(Manifest.permission.CAMERA) -> "waiting-permission"; else -> "off" })
        .put("audio", when {
            paused -> "paused"; mic.running -> "streaming"
            !granted(Manifest.permission.RECORD_AUDIO) -> "waiting-permission"; else -> "off" })
        .put("location", when { paused -> "paused"; location.running -> "streaming"; !location.permitted() -> "waiting-permission"; else -> "off" })
        .put("notifications", if (NotificationRelayService.connected) "ready" else "waiting-permission")

    private fun publishStatus() {
        control.send(Frame.json(Frame.STATUS, JSONObject().put("modules", moduleStates()).put("paused", paused).toString()))
    }

    private fun thermalHot() = battery.snapshot()?.optDouble("tempC", 0.0)?.let { it >= THERMAL_LIMIT_C } ?: false

    private fun thermalGuard() {
        val t = battery.lastTempC
        if (!t.isNaN() && t >= THERMAL_LIMIT_C && (camera.running || mic.running)) {
            Log.w(TAG, "thermal guard: battery $t C, stopping camera/mic")
            camera.stop("thermal"); mic.stop("thermal")
            control.send(Frame.json(Frame.ERROR, JSONObject().put("code", "thermal")
                .put("message", "Battery reached $t C; camera and mic stopped").put("recoverable", true).toString()))
            publishStatus()
        }
    }

    private fun stopStreams(reason: String) {
        camera.stop(reason); mic.stop(reason); sensors.stop(); battery.stop(); location.stop()
    }

    private fun pauseAll() {
        paused = true
        phone.reset(); radio.reset()
        camera.stop("paused"); mic.stop("paused"); sensors.stop(); location.stop()
        publishStatus()
    }

    private fun stopEverything() {
        stopStreams("service-stopped")
        control.disconnectPeer(); video.disconnectPeer(); audio.disconnectPeer(); telemetry.disconnectPeer()
    }

    private fun buildNotification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "PixelBridge", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        fun action(a: String, req: Int) = PendingIntent.getService(this, req,
            Intent(this, BridgeService::class.java).setAction(a), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(dev.pixelbridge.app.R.drawable.ic_bridge)
            .setContentTitle("PixelBridge active")
            .setContentText(if (paused) "Paused — camera and mic blocked" else "USB bridge listening on loopback")
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, if (paused) "Resume" else "Pause all",
                action(if (paused) ACTION_RESUME else ACTION_PAUSE, 1)).build())
            .addAction(Notification.Action.Builder(null, "Stop", action(ACTION_STOP, 2)).build())
            .build()
    }

    private fun notifyUi() { uiListener?.invoke() }

    fun summary(): String = buildString {
        append("Host: ").append(if (control.isConnected) "connected" else "not connected").append('\n')
        append("Paired: ").append(if (pairing.isPaired()) "yes (${pairing.hostName()})" else "no").append('\n')
        append("Modules: ").append(moduleStates().toString(1)).append('\n')
        append("Video frames sent/dropped: ${camera.framesSent}/${camera.framesDropped}\n")
        append("Audio frames: ${mic.framesSent}   Sensor samples: ${sensors.samplesSent}\n")
        lastError?.let { append("Last error: ").append(it) }
    }

    companion object {
        private const val TAG = "PixelBridge"
        private const val CHANNEL = "bridge"
        private const val NOTIFICATION_ID = 1
        const val THERMAL_LIMIT_C = 43.0
        const val ACTION_BOOT = "dev.pixelbridge.app.BOOT"
        const val ACTION_STOP = "dev.pixelbridge.app.STOP"
        const val ACTION_PAUSE = "dev.pixelbridge.app.PAUSE"
        const val ACTION_RESUME = "dev.pixelbridge.app.RESUME"
        @Volatile var instance: BridgeService? = null
        @Volatile var uiListener: (() -> Unit)? = null
        @Volatile var lastError: String? = null

        fun start(context: Context) {
            context.startForegroundService(Intent(context, BridgeService::class.java))
        }
    }
}
