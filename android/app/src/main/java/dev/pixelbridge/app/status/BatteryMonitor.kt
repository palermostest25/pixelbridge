package dev.pixelbridge.app.status

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import dev.pixelbridge.app.transport.Frame
import org.json.JSONObject

/** ACTION_BATTERY_CHANGED plus a 15 s periodic sample (current draw is not broadcast). */
class BatteryMonitor(private val context: Context, private val sink: (Frame) -> Unit) {
    private val bm = context.getSystemService(BatteryManager::class.java)
    private val handler = Handler(Looper.getMainLooper())
    private var last: Intent? = null
    @Volatile var lastTempC: Double = Double.NaN
        private set
    private var running = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, intent: Intent) {
            last = intent
            publish()
        }
    }
    private val tick = object : Runnable {
        override fun run() {
            publish()
            handler.postDelayed(this, 15000)
        }
    }

    fun start() {
        if (running) return
        running = true
        last = context.registerReceiver(receiver, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        handler.post(tick)
    }

    fun stop() {
        if (!running) return
        running = false
        handler.removeCallbacks(tick)
        context.unregisterReceiver(receiver)
    }

    fun snapshot(): JSONObject? {
        val i = last ?: context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return null
        val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
        val temp = i.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE)
        val status = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val plugged = i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0)
        val health = i.getIntExtra(BatteryManager.EXTRA_HEALTH, -1)
        val currentUa = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CURRENT_NOW)
        lastTempC = if (temp == Int.MIN_VALUE) Double.NaN else temp / 10.0
        return JSONObject()
            .put("percent", if (level >= 0 && scale > 0) level * 100 / scale else -1)
            .put("tempC", if (lastTempC.isNaN()) JSONObject.NULL else lastTempC)
            .put("charging", status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL)
            .put("plugged", when (plugged) {
                BatteryManager.BATTERY_PLUGGED_USB -> "usb"; BatteryManager.BATTERY_PLUGGED_AC -> "ac"
                BatteryManager.BATTERY_PLUGGED_WIRELESS -> "wireless"; 0 -> "none"; else -> "other"
            })
            .put("voltageMv", i.getIntExtra(BatteryManager.EXTRA_VOLTAGE, -1))
            .put("currentMa", if (currentUa == Int.MIN_VALUE) JSONObject.NULL else currentUa / 1000)
            .put("health", when (health) {
                BatteryManager.BATTERY_HEALTH_GOOD -> "good"; BatteryManager.BATTERY_HEALTH_OVERHEAT -> "overheat"
                BatteryManager.BATTERY_HEALTH_COLD -> "cold"; BatteryManager.BATTERY_HEALTH_DEAD -> "dead"
                BatteryManager.BATTERY_HEALTH_OVER_VOLTAGE -> "over-voltage"; else -> "unknown"
            })
            .put("status", status)
    }

    private fun publish() {
        val j = snapshot() ?: return
        sink(Frame.json(Frame.BATTERY_STATUS, j.toString(), SystemClock.elapsedRealtimeNanos()))
    }
}
