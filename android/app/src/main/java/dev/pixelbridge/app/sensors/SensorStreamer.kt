package dev.pixelbridge.app.sensors

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Handler
import android.os.HandlerThread
import dev.pixelbridge.app.transport.Frame
import org.json.JSONArray
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Real SensorManager events encoded as SENSOR_SAMPLE frames (protocol section 5). */
class SensorStreamer(context: Context, private val sink: (Frame) -> Unit) : SensorEventListener {
    private val sm = context.getSystemService(SensorManager::class.java)
    private var thread: HandlerThread? = null
    @Volatile var running = false
        private set
    @Volatile var samplesSent = 0L
        private set

    private val wanted = listOf(
        Sensor.TYPE_ACCELEROMETER, Sensor.TYPE_GYROSCOPE, Sensor.TYPE_GRAVITY,
        Sensor.TYPE_LINEAR_ACCELERATION, Sensor.TYPE_ROTATION_VECTOR, Sensor.TYPE_GAME_ROTATION_VECTOR,
        Sensor.TYPE_MAGNETIC_FIELD, Sensor.TYPE_LIGHT, Sensor.TYPE_PROXIMITY, Sensor.TYPE_PRESSURE,
    )

    fun capabilities(): JSONArray {
        val arr = JSONArray()
        for (t in wanted) {
            val s = sm.getDefaultSensor(t) ?: continue
            arr.put(
                JSONObject().put("type", t).put("name", s.stringType.substringAfterLast('.'))
                    .put("vendor", s.vendor).put("maxRateHz", if (s.minDelay > 0) 1_000_000 / s.minDelay else 0)
            )
        }
        return arr
    }

    @Synchronized
    fun start(rateHz: Int) {
        stop()
        val hz = rateHz.coerceIn(1, 200)
        val ht = HandlerThread("pb-sensors").also { it.start() }
        thread = ht
        val handler = Handler(ht.looper)
        val periodUs = 1_000_000 / hz
        for (t in wanted) {
            val s = sm.getDefaultSensor(t) ?: continue
            // Light/proximity/pressure are on-change or slow; register at a modest rate.
            val p = if (t == Sensor.TYPE_LIGHT || t == Sensor.TYPE_PROXIMITY || t == Sensor.TYPE_PRESSURE) 200_000 else periodUs
            sm.registerListener(this, s, p, 0, handler)
        }
        running = true
    }

    @Synchronized
    fun stop() {
        if (thread == null) return
        sm.unregisterListener(this)
        thread?.quitSafely()
        thread = null
        running = false
    }

    override fun onSensorChanged(event: SensorEvent) {
        val n = minOf(event.values.size, 16)
        val bb = ByteBuffer.allocate(8 + 4 * n).order(ByteOrder.BIG_ENDIAN)
        bb.putInt(event.sensor.type)
        bb.put(event.accuracy.toByte())
        bb.put(n.toByte())
        bb.putShort(0)
        for (i in 0 until n) bb.putFloat(event.values[i])
        sink(Frame(Frame.SENSOR_SAMPLE, bb.array(), 0, event.timestamp))
        samplesSent++
    }

    override fun onAccuracyChanged(sensor: Sensor, accuracy: Int) {}
}
