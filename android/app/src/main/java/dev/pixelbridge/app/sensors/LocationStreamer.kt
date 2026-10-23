package dev.pixelbridge.app.sensors

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.location.GnssStatus
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import dev.pixelbridge.app.transport.Frame
import org.json.JSONObject

/**
 * GNSS fixes + satellite summary as LOCATION_SAMPLE frames (protocol section 5a). Off by default;
 * started only by an explicit START_STREAM "location" after the user granted location permission.
 * Coordinates are never written to logcat.
 */
class LocationStreamer(private val context: Context, private val sink: (Frame) -> Unit) {
    private val lm = context.getSystemService(LocationManager::class.java)
    private var thread: HandlerThread? = null
    @Volatile var running = false
        private set
    @Volatile private var used = 0
    @Volatile private var visible = 0
    @Volatile private var meanCn0 = 0.0
    @Volatile var fixes = 0L
        private set

    private val listener = LocationListener { publish(it) }
    private val gnss = object : GnssStatus.Callback() {
        override fun onSatelliteStatusChanged(status: GnssStatus) {
            var u = 0; var sum = 0.0
            for (i in 0 until status.satelliteCount) {
                if (status.usedInFix(i)) u++
                sum += status.getCn0DbHz(i)
            }
            visible = status.satelliteCount; used = u
            meanCn0 = if (status.satelliteCount > 0) sum / status.satelliteCount else 0.0
        }
    }

    fun permitted() = context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

    /** Returns null on success, or an error message. */
    @SuppressLint("MissingPermission")
    @Synchronized
    fun start(intervalMs: Long): String? {
        stop()
        if (!permitted()) return "Location permission not granted on the phone"
        if (!lm.isLocationEnabled) return "Location is turned off on the phone"
        val ht = HandlerThread("pb-location").also { it.start() }
        thread = ht
        val h = Handler(ht.looper)
        val ms = intervalMs.coerceIn(500, 60_000)
        if (lm.isProviderEnabled(LocationManager.GPS_PROVIDER))
            lm.requestLocationUpdates(LocationManager.GPS_PROVIDER, ms, 0f, listener, ht.looper)
        if (lm.isProviderEnabled(LocationManager.NETWORK_PROVIDER))
            lm.requestLocationUpdates(LocationManager.NETWORK_PROVIDER, ms, 0f, listener, ht.looper)
        lm.registerGnssStatusCallback({ h.post(it) }, gnss)
        // Publish the last known fix immediately so the PC isn't blank while GNSS warms up.
        (lm.getLastKnownLocation(LocationManager.GPS_PROVIDER) ?: lm.getLastKnownLocation(LocationManager.NETWORK_PROVIDER))
            ?.let { publish(it, stale = true) }
        running = true
        return null
    }

    @Synchronized
    fun stop() {
        if (thread == null) return
        lm.removeUpdates(listener)
        lm.unregisterGnssStatusCallback(gnss)
        thread?.quitSafely()
        thread = null
        running = false
    }

    private fun publish(l: Location, stale: Boolean = false) {
        val j = JSONObject()
            .put("lat", l.latitude).put("lon", l.longitude)
            .put("altM", if (l.hasAltitude()) l.altitude else JSONObject.NULL)
            .put("accM", if (l.hasAccuracy()) l.accuracy.toDouble() else JSONObject.NULL)
            .put("speedMps", if (l.hasSpeed()) l.speed.toDouble() else JSONObject.NULL)
            .put("bearingDeg", if (l.hasBearing()) l.bearing.toDouble() else JSONObject.NULL)
            .put("timeMs", l.time).put("provider", l.provider ?: "unknown").put("stale", stale)
            .put("ageMs", (SystemClock.elapsedRealtimeNanos() - l.elapsedRealtimeNanos) / 1_000_000)
            .put("satellitesUsed", used).put("satellitesVisible", visible).put("meanCn0", meanCn0)
        fixes++
        sink(Frame.json(Frame.LOCATION_SAMPLE, j.toString(), l.elapsedRealtimeNanos))
    }
}
