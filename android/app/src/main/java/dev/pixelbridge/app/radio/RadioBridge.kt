package dev.pixelbridge.app.radio

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.content.pm.PackageManager
import android.location.LocationManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.util.Base64
import java.util.UUID

/**
 * Phone radios exposed to the PC over the authenticated USB control channel (protocol section 9):
 * a Bluetooth LE GATT bridge, bonded-device list, Wi-Fi status/scan and network status.
 * Every operation is gated on Android runtime permissions the user granted in the app; nothing
 * here can pair, change Wi-Fi networks or toggle radios.
 */
@SuppressLint("MissingPermission") // each entry point checks permissions explicitly via need()
class RadioBridge(
    private val context: Context,
    private val respond: (id: Int, json: JSONObject) -> Unit,
    private val event: (json: JSONObject) -> Unit,
) {
    private val thread = HandlerThread("pb-radio").also { it.start() }
    private val handler = Handler(thread.looper)
    private val btManager = context.getSystemService(BluetoothManager::class.java)
    private val wifi = context.getSystemService(WifiManager::class.java)
    private val cm = context.getSystemService(ConnectivityManager::class.java)
    private val gatts = HashMap<String, GattSession>()
    private var scanning = false
    private val lastScanEmit = HashMap<String, Long>()

    fun handle(req: JSONObject) {
        val id = req.optInt("id")
        val op = req.optString("op")
        val args = req.optJSONObject("args") ?: JSONObject()
        handler.post {
            try {
                dispatch(id, op, args)
            } catch (e: SecurityException) {
                fail(id, "permission", e.message ?: "permission denied")
            } catch (e: Exception) {
                Log.w(TAG, "radio op $op failed: ${e.message}")
                fail(id, "internal", e.message ?: e.javaClass.simpleName)
            }
        }
    }

    private fun dispatch(id: Int, op: String, a: JSONObject) {
        when (op) {
            "caps" -> ok(id, capabilities())
            "net.status" -> ok(id, networkStatus())
            "wifi.status" -> ok(id, wifiStatus())
            "wifi.scan" -> wifiScan(id)
            "bt.bonded" -> bonded(id)
            "ble.scan.start" -> bleScanStart(id, a.optInt("durationMs", 15000))
            "ble.scan.stop" -> { bleScanStop(); ok(id, JSONObject()) }
            "ble.connect" -> bleConnect(id, a.getString("address"))
            "ble.disconnect" -> { gatts.remove(a.getString("address"))?.close(); ok(id, JSONObject()) }
            "ble.read", "ble.write", "ble.notify" -> bleCharOp(id, op, a)
            else -> fail(id, "unsupported", "unknown op $op")
        }
    }

    // ------------------------------------------------------------------ capabilities / permissions

    private fun granted(p: String) = context.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED

    private fun need(id: Int, vararg perms: String): Boolean {
        val missing = perms.filterNot(::granted)
        if (missing.isEmpty()) return true
        fail(id, "permission", "Grant ${missing.joinToString { it.substringAfterLast('.') }} in the PixelBridge app (Remote → open phone screen)")
        return false
    }

    fun capabilities(): JSONObject {
        val adapter = btManager?.adapter
        val lm = context.getSystemService(LocationManager::class.java)
        return JSONObject()
            .put("bluetoothPresent", adapter != null)
            .put("bluetoothOn", adapter?.isEnabled == true)
            .put("bleSupported", context.packageManager.hasSystemFeature(PackageManager.FEATURE_BLUETOOTH_LE))
            .put("wifiOn", wifi?.isWifiEnabled == true)
            .put("locationOn", lm?.isLocationEnabled == true)
            .put("permissions", JSONObject()
                .put("bluetoothScan", granted(Manifest.permission.BLUETOOTH_SCAN))
                .put("bluetoothConnect", granted(Manifest.permission.BLUETOOTH_CONNECT))
                .put("fineLocation", granted(Manifest.permission.ACCESS_FINE_LOCATION))
                .put("nearbyWifi", granted(Manifest.permission.NEARBY_WIFI_DEVICES)))
    }

    // ------------------------------------------------------------------ network / Wi-Fi

    private fun networkStatus(): JSONObject {
        val nets = JSONArray()
        for (n in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(n) ?: continue
            val lp = cm.getLinkProperties(n)
            val t = mutableListOf<String>()
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) t += "wifi"
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) t += "cellular"
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) t += "vpn"
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) t += "ethernet"
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_BLUETOOTH)) t += "bluetooth"
            nets.put(JSONObject().put("transports", JSONArray(t)).put("interface", lp?.interfaceName ?: JSONObject.NULL)
                .put("validated", caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED))
                .put("metered", !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED))
                .put("downKbps", caps.linkDownstreamBandwidthKbps).put("upKbps", caps.linkUpstreamBandwidthKbps)
                .put("default", n == cm.activeNetwork))
        }
        return JSONObject().put("networks", nets)
    }

    private fun wifiStatus(): JSONObject {
        val j = JSONObject().put("enabled", wifi.isWifiEnabled)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        val info = (caps?.transportInfo as? WifiInfo)
            ?: cm.allNetworks.mapNotNull { cm.getNetworkCapabilities(it)?.transportInfo as? WifiInfo }.firstOrNull()
        if (info != null) {
            j.put("connected", true).put("rssi", info.rssi).put("linkMbps", info.linkSpeed)
                .put("frequencyMhz", info.frequency).put("standard", info.wifiStandard)
            // SSID is only readable with location permission; never fabricate it.
            // NetworkCapabilities redacts the SSID; WifiManager.connectionInfo still returns it when location is granted.
            @Suppress("DEPRECATION")
            val raw = info.ssid?.takeUnless { it == WifiManager.UNKNOWN_SSID }
                ?: if (granted(Manifest.permission.ACCESS_FINE_LOCATION)) wifi.connectionInfo?.ssid else null
            val ssid = raw?.trim('"')
            j.put("ssid", if (ssid.isNullOrEmpty() || ssid == WifiManager.UNKNOWN_SSID.trim('"')) JSONObject.NULL else ssid)
        } else j.put("connected", false)
        return j
    }

    private fun wifiScan(id: Int) {
        if (!need(id, Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.NEARBY_WIFI_DEVICES)) return
        @Suppress("DEPRECATION") val started = wifi.startScan() // throttled by Android (4 per 2 min); results may be cached
        handler.postDelayed({
            val arr = JSONArray()
            for (r in wifi.scanResults.sortedByDescending { it.level }.take(60)) {
                arr.put(JSONObject().put("ssid", r.wifiSsid?.toString()?.trim('"') ?: "").put("bssid", r.BSSID)
                    .put("rssi", r.level).put("frequencyMhz", r.frequency).put("security", r.capabilities)
                    .put("ageMs", SystemClock.elapsedRealtime() - r.timestamp / 1000))
            }
            ok(id, JSONObject().put("freshScan", started).put("results", arr))
        }, if (started) 4000 else 0)
    }

    private fun bonded(id: Int) {
        if (!need(id, Manifest.permission.BLUETOOTH_CONNECT)) return
        val arr = JSONArray()
        btManager.adapter?.bondedDevices?.forEach { d ->
            arr.put(JSONObject().put("name", d.name ?: "").put("address", d.address).put("type", typeName(d.type)))
        }
        ok(id, JSONObject().put("devices", arr))
    }

    // ------------------------------------------------------------------ BLE scan

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, r: ScanResult) {
            val now = SystemClock.elapsedRealtime()
            val addr = r.device.address
            if (now - (lastScanEmit[addr] ?: 0) < 500) return // ≤ 2 events/s per device
            lastScanEmit[addr] = now
            val rec = r.scanRecord
            val uuids = JSONArray(rec?.serviceUuids?.map { it.uuid.toString() } ?: emptyList<String>())
            val mfr = JSONArray()
            rec?.manufacturerSpecificData?.let { for (i in 0 until it.size()) mfr.put(it.keyAt(i)) }
            event(JSONObject().put("event", "ble.scanResult").put("address", addr)
                .put("name", rec?.deviceName ?: r.device.name ?: JSONObject.NULL).put("rssi", r.rssi)
                .put("connectable", r.isConnectable).put("serviceUuids", uuids).put("manufacturerIds", mfr)
                .put("txPower", rec?.txPowerLevel?.takeIf { it != Int.MIN_VALUE } ?: JSONObject.NULL))
        }

        override fun onScanFailed(errorCode: Int) {
            scanning = false
            event(JSONObject().put("event", "ble.scanStopped").put("error", errorCode))
        }
    }

    private fun bleScanStart(id: Int, durationMs: Int) {
        if (!need(id, Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT)) return
        val adapter = btManager.adapter
        if (adapter == null || !adapter.isEnabled) { fail(id, "unavailable", "Bluetooth is off on the phone"); return }
        if (scanning) bleScanStop()
        lastScanEmit.clear()
        val scanner = adapter.bluetoothLeScanner ?: run { fail(id, "unavailable", "BLE scanner unavailable"); return }
        scanner.startScan(null, ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(), scanCallback)
        scanning = true
        val ms = durationMs.coerceIn(1000, 30000) // bounded: scans always stop on their own
        handler.postDelayed({ if (scanning) bleScanStop() }, ms.toLong())
        ok(id, JSONObject().put("durationMs", ms))
    }

    private fun bleScanStop() {
        if (!scanning) return
        scanning = false
        try { btManager.adapter?.bluetoothLeScanner?.stopScan(scanCallback) } catch (_: Exception) {}
        event(JSONObject().put("event", "ble.scanStopped"))
    }

    // ------------------------------------------------------------------ BLE GATT

    private fun bleConnect(id: Int, address: String) {
        if (!need(id, Manifest.permission.BLUETOOTH_CONNECT)) return
        if (gatts.size >= 4 && !gatts.containsKey(address)) { fail(id, "limit", "At most 4 BLE connections"); return }
        gatts.remove(address)?.close()
        val device = btManager.adapter.getRemoteDevice(address)
        val s = GattSession(address)
        gatts[address] = s
        s.gatt = device.connectGatt(context, false, s, BluetoothDevice.TRANSPORT_LE, BluetoothDevice.PHY_LE_1M_MASK, handler)
        ok(id, JSONObject().put("state", "connecting"))
    }

    private fun bleCharOp(id: Int, op: String, a: JSONObject) {
        if (!need(id, Manifest.permission.BLUETOOTH_CONNECT)) return
        val s = gatts[a.getString("address")] ?: run { fail(id, "not-connected", "connect first"); return }
        val ch = s.gatt?.getService(UUID.fromString(a.getString("service")))?.getCharacteristic(UUID.fromString(a.getString("characteristic")))
            ?: run { fail(id, "not-found", "service/characteristic not found"); return }
        s.enqueue(id, op, ch, a)
    }

    private inner class GattSession(val address: String) : BluetoothGattCallback() {
        var gatt: BluetoothGatt? = null
        private val queue = ArrayDeque<Triple<Int, String, Pair<BluetoothGattCharacteristic, JSONObject>>>()
        private var inFlight: Pair<Int, String>? = null

        fun enqueue(id: Int, op: String, ch: BluetoothGattCharacteristic, a: JSONObject) {
            queue.addLast(Triple(id, op, ch to a))
            if (inFlight == null) next()
        }

        private fun next() {
            val (id, op, p) = queue.removeFirstOrNull() ?: run { inFlight = null; return }
            val (ch, a) = p
            val g = gatt ?: run { fail(id, "not-connected", "disconnected"); next(); return }
            inFlight = id to op
            val started = when (op) {
                "ble.read" -> g.readCharacteristic(ch)
                "ble.write" -> {
                    val v = Base64.getDecoder().decode(a.getString("valueB64"))
                    val type = if (a.optBoolean("withResponse", true)) BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
                        else BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE
                    g.writeCharacteristic(ch, v, type) == BluetoothGatt.GATT_SUCCESS
                }
                else -> { // ble.notify
                    val enable = a.optBoolean("enable", true)
                    g.setCharacteristicNotification(ch, enable)
                    val cccd = ch.getDescriptor(CCCD)
                    if (cccd == null) { ok(id, JSONObject().put("enabled", enable).put("note", "no CCCD")); inFlight = null; next(); return }
                    val value = if (!enable) BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE
                        else if (ch.properties and BluetoothGattCharacteristic.PROPERTY_NOTIFY != 0) BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE
                        else BluetoothGattDescriptor.ENABLE_INDICATION_VALUE
                    g.writeDescriptor(cccd, value) == BluetoothGatt.GATT_SUCCESS
                }
            }
            if (!started) { fail(id, "busy", "GATT operation rejected"); inFlight = null; next() }
            else handler.postDelayed({ if (inFlight?.first == id) { fail(id, "timeout", "no GATT response"); inFlight = null; next() } }, 8000)
        }

        private fun complete(status: Int, result: JSONObject) {
            val (id, _) = inFlight ?: return
            inFlight = null
            if (status == BluetoothGatt.GATT_SUCCESS) ok(id, result) else fail(id, "gatt", "GATT status $status")
            next()
        }

        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            val state = when (newState) { BluetoothProfile.STATE_CONNECTED -> "connected"; BluetoothProfile.STATE_DISCONNECTED -> "disconnected"; else -> "connecting" }
            event(JSONObject().put("event", "ble.connectionState").put("address", address).put("state", state).put("status", status))
            if (newState == BluetoothProfile.STATE_CONNECTED) g.discoverServices()
            if (newState == BluetoothProfile.STATE_DISCONNECTED) { g.close(); if (gatts[address] === this) gatts.remove(address) }
        }

        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
            val services = JSONArray()
            for (s in g.services) {
                val chars = JSONArray()
                for (c in s.characteristics) chars.put(JSONObject().put("uuid", c.uuid.toString()).put("properties", JSONArray(props(c.properties))))
                services.put(JSONObject().put("uuid", s.uuid.toString()).put("characteristics", chars))
            }
            event(JSONObject().put("event", "ble.services").put("address", address).put("status", status).put("services", services))
        }

        override fun onCharacteristicRead(g: BluetoothGatt, c: BluetoothGattCharacteristic, value: ByteArray, status: Int) =
            complete(status, JSONObject().put("valueB64", Base64.getEncoder().encodeToString(value)))

        override fun onCharacteristicWrite(g: BluetoothGatt, c: BluetoothGattCharacteristic, status: Int) = complete(status, JSONObject())

        override fun onDescriptorWrite(g: BluetoothGatt, d: BluetoothGattDescriptor, status: Int) = complete(status, JSONObject())

        override fun onCharacteristicChanged(g: BluetoothGatt, c: BluetoothGattCharacteristic, value: ByteArray) {
            event(JSONObject().put("event", "ble.notification").put("address", address).put("service", c.service.uuid.toString())
                .put("characteristic", c.uuid.toString()).put("valueB64", Base64.getEncoder().encodeToString(value)))
        }

        fun close() {
            try { gatt?.disconnect(); gatt?.close() } catch (_: Exception) {}
            gatt = null
        }
    }

    /** Host went away: stop scans and drop every BLE connection. */
    fun reset() = handler.post {
        bleScanStop()
        gatts.values.forEach { it.close() }
        gatts.clear()
    }

    fun shutdown() { reset(); thread.quitSafely() }

    private fun ok(id: Int, result: JSONObject) = respond(id, JSONObject().put("id", id).put("ok", true).put("result", result))
    private fun fail(id: Int, code: String, msg: String) = respond(id, JSONObject().put("id", id).put("ok", false).put("error", code).put("message", msg))

    private fun props(p: Int) = buildList {
        if (p and BluetoothGattCharacteristic.PROPERTY_READ != 0) add("read")
        if (p and BluetoothGattCharacteristic.PROPERTY_WRITE != 0) add("write")
        if (p and BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE != 0) add("writeNoResponse")
        if (p and BluetoothGattCharacteristic.PROPERTY_NOTIFY != 0) add("notify")
        if (p and BluetoothGattCharacteristic.PROPERTY_INDICATE != 0) add("indicate")
    }

    private fun typeName(t: Int) = when (t) {
        BluetoothDevice.DEVICE_TYPE_CLASSIC -> "classic"; BluetoothDevice.DEVICE_TYPE_LE -> "le"
        BluetoothDevice.DEVICE_TYPE_DUAL -> "dual"; else -> "unknown"
    }

    private companion object {
        const val TAG = "PixelBridge"
        val CCCD: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
    }
}
