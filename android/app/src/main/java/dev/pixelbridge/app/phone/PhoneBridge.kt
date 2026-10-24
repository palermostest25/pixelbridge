package dev.pixelbridge.app.phone

import android.Manifest
import android.annotation.SuppressLint
import android.app.PendingIntent
import android.app.role.RoleManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.database.ContentObserver
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.AudioManager
import android.media.Ringtone
import android.media.RingtoneManager
import android.net.Uri
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.VibratorManager
import android.provider.CallLog
import android.provider.ContactsContract
import android.provider.Telephony
import android.telecom.TelecomManager
import android.telephony.SmsManager
import android.telephony.SubscriptionManager
import dev.pixelbridge.app.notifications.NotificationRelayService
import org.json.JSONArray
import org.json.JSONObject

/** Explicit, permission-gated phone operations over the already authenticated USB channel. */
@SuppressLint("MissingPermission")
class PhoneBridge(private val context: Context, private val respond: (JSONObject) -> Unit, private val event: (JSONObject) -> Unit) {
    private val worker = HandlerThread("pb-phone").also { it.start() }
    private val handler = Handler(worker.looper)
    private val main = Handler(Looper.getMainLooper())
    private val resolver = context.contentResolver
    private val prefs = context.getSharedPreferences("sms-receipts", Context.MODE_PRIVATE)
    private val ledger = SendLedger({ prefs.getString("ledger", "{}") ?: "{}" }, { check(prefs.edit().putString("ledger", it).commit()) { "Could not save SMS receipt; no new message was submitted" } })
    private var watching = false
    private var ringtone: Ringtone? = null
    private val stopRing = Runnable { ringtone?.stop(); ringtone = null }
    private var torchCamera: String? = null
    private val refreshNotice = Runnable { event(JSONObject().put("event", "phone.changed")) }
    private val observer = object : ContentObserver(handler) {
        override fun onChange(selfChange: Boolean) { handler.removeCallbacks(refreshNotice); handler.postDelayed(refreshNotice, 500) }
    }
    init { events = event }
    fun handle(request: JSONObject) {
        val id = request.optInt("id"); val op = request.optString("op"); val a = request.optJSONObject("args") ?: JSONObject()
        handler.post {
            try {
                val result = dispatch(op, a)
                if (result != null) respond(JSONObject().put("id", id).put("ok", true).put("result", result))
            } catch (e: SecurityException) { fail(id, "permission", e.message ?: "Allow this feature on the phone first") }
            catch (e: IllegalArgumentException) { fail(id, "invalid", e.message ?: "Invalid request") }
            catch (e: Exception) { fail(id, "unavailable", e.message ?: "This feature is unavailable on this phone") }
        }
    }
    private fun fail(id: Int, code: String, message: String) = respond(JSONObject().put("id", id).put("ok", false).put("error", code).put("message", message))
    private fun supportsSms() = context.packageManager.hasSystemFeature(if (android.os.Build.VERSION.SDK_INT >= 33) PackageManager.FEATURE_TELEPHONY_MESSAGING else PackageManager.FEATURE_TELEPHONY)
    private fun granted(permission: String) = context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED
    private fun need(vararg permissions: String) {
        val missing = permissions.filterNot(::granted)
        if (missing.isNotEmpty()) throw SecurityException("On the phone, open PixelBridge → Allow communications (${missing.joinToString { it.substringAfterLast('.') }}). Android may require the installer to allow SMS/call-log permissions.")
    }
    fun capabilities(): JSONObject {
        val role = context.getSystemService(RoleManager::class.java)
        return JSONObject().put("smsSupported", supportsSms())
            .put("dialer", role.isRoleHeld(RoleManager.ROLE_DIALER)).put("calls", CallRegistry.snapshot())
            .put("permissions", JSONObject().put("readSms", granted(Manifest.permission.READ_SMS)).put("sendSms", granted(Manifest.permission.SEND_SMS))
                .put("contacts", granted(Manifest.permission.READ_CONTACTS)).put("callLog", granted(Manifest.permission.READ_CALL_LOG))
                .put("callPhone", granted(Manifest.permission.CALL_PHONE)).put("phoneState", granted(Manifest.permission.READ_PHONE_STATE)))
            .put("callAudio", "phone").put("notificationReplies", NotificationRelayService.connected)
    }
    private fun dispatch(op: String, a: JSONObject): JSONObject? = when (op) {
        "phone.status" -> capabilities()
        "phone.watch" -> { setWatching(a.optBoolean("enabled")); capabilities() }
        "contacts.list" -> contacts(a.optString("query"), a.optInt("limit", 100))
        "sms.threads" -> threads()
        "sms.messages" -> messages(a.getLong("threadId"), a.optLong("before", Long.MAX_VALUE))
        "sms.send" -> sendSms(a)
        "sms.receipt" -> ledger.status(a.getString("token")) ?: JSONObject().put("state", "unknown")
        "phone.history" -> history()
        "phone.call" -> {
            need(Manifest.permission.CALL_PHONE)
            val number = a.getString("number").trim(); require(number.matches(Regex("[+0-9*#() .-]{1,80}"))) { "Enter a phone number" }
            main.post { try { context.getSystemService(TelecomManager::class.java).placeCall(Uri.fromParts("tel", number, null), android.os.Bundle()) }
                catch (e: Exception) { event(JSONObject().put("event", "phone.error").put("message", e.message)) } }
            JSONObject().put("state", "requested")
        }
        "phone.answer", "phone.end", "phone.hold", "phone.unhold", "phone.dtmf", "phone.mute", "phone.route" -> {
            require(context.getSystemService(RoleManager::class.java).isRoleHeld(RoleManager.ROLE_DIALER)) { "Enable call controls in PixelBridge on the phone, then choose PixelBridge as the default Phone app" }
            CallRegistry.request(op, a, event); JSONObject().put("state", "requested")
        }
        "notifications.list" -> NotificationRelayService.currentNotifications()
        "notifications.reply" -> { NotificationRelayService.reply(a.getString("key"), a.getString("text")); JSONObject().put("state", "requested") }
        "notifications.dismiss" -> { NotificationRelayService.dismiss(a.getString("key")); JSONObject() }
        "tools.status" -> toolsStatus()
        "tools.flashlight" -> { setTorch(a.optBoolean("enabled")); toolsStatus() }
        "tools.volume" -> {
            val audio = context.getSystemService(AudioManager::class.java)
            val value = a.getInt("value").coerceIn(0, audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC))
            audio.setStreamVolume(AudioManager.STREAM_MUSIC, value, 0); toolsStatus()
        }
        "tools.find" -> {
            val play = a.optBoolean("enabled", true)
            main.post {
                main.removeCallbacks(stopRing); stopRing.run()
                if (play) {
                    ringtone = RingtoneManager.getRingtone(context, RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM))?.also { it.play() }
                    main.postDelayed(stopRing, 15_000)
                }
            }; JSONObject().put("playing", play).put("maximumSeconds", 15)
        }
        "tools.vibrate" -> {
            context.getSystemService(VibratorManager::class.java).defaultVibrator.vibrate(VibrationEffect.createOneShot(500, VibrationEffect.DEFAULT_AMPLITUDE)); JSONObject()
        }
        "tools.apps" -> apps()
        else -> throw IllegalArgumentException("Unknown phone operation $op")
    }
    private fun setWatching(enabled: Boolean) {
        resolver.unregisterContentObserver(observer); watching = enabled
        if (enabled) {
            if (granted(Manifest.permission.READ_SMS)) resolver.registerContentObserver(Telephony.Sms.CONTENT_URI, true, observer)
            if (granted(Manifest.permission.READ_CALL_LOG)) resolver.registerContentObserver(CallLog.Calls.CONTENT_URI, true, observer)
            if (granted(Manifest.permission.READ_CONTACTS)) resolver.registerContentObserver(ContactsContract.Contacts.CONTENT_URI, true, observer)
        }
    }
    private fun contacts(query: String, limit: Int): JSONObject {
        need(Manifest.permission.READ_CONTACTS)
        val uri = ContactsContract.CommonDataKinds.Phone.CONTENT_URI
        val columns = arrayOf("contact_id", "display_name", "data1", "data2")
        val search = query.trim().take(100)
        val selection = if (search.isEmpty()) null else "display_name LIKE ? OR data1 LIKE ?"
        val args = if (search.isEmpty()) null else arrayOf("%$search%", "%$search%")
        val rows = JSONArray(); val seen = HashSet<String>()
        resolver.query(uri, columns, selection, args, "display_name COLLATE LOCALIZED ASC")?.use { c ->
            while (rows.length() < limit.coerceIn(1, 100) && c.moveToNext()) {
                val number = c.getString(2) ?: continue
                if (!seen.add("${c.getLong(0)}:$number")) continue
                if (!appendBounded(rows, JSONObject().put("id", c.getLong(0)).put("name", (c.getString(1) ?: number).take(200)).put("number", number.take(100)))) break
            }
        }; return JSONObject().put("contacts", rows)
    }
    private fun threads(): JSONObject {
        need(Manifest.permission.READ_SMS)
        val rows = JSONArray(); val seen = HashSet<Long>()
        resolver.query(Telephony.Sms.CONTENT_URI, arrayOf("_id", "thread_id", "address", "body", "date", "type", "read"), null, null, "date DESC")?.use { c ->
            var visited = 0
            while (rows.length() < 60 && visited++ < 2000 && c.moveToNext()) {
                val thread = c.getLong(1); if (!seen.add(thread)) continue
                val row = JSONObject().put("id", thread).put("address", (c.getString(2) ?: "Unknown").take(150)).put("preview", (c.getString(3) ?: "").take(250))
                    .put("dateMs", c.getLong(4)).put("incoming", c.getInt(5) == 1).put("unread", c.getInt(6) == 0)
                if (!appendBounded(rows, row)) break
            }
        }; return JSONObject().put("threads", rows).put("format", "sms")
    }
    private fun messages(thread: Long, before: Long): JSONObject {
        need(Manifest.permission.READ_SMS); require(thread >= 0)
        val rows = JSONArray()
        resolver.query(Telephony.Sms.CONTENT_URI, arrayOf("_id", "address", "body", "date", "type", "status"), "thread_id=? AND date<?", arrayOf(thread.toString(), before.toString()), "date DESC")?.use { c ->
            while (rows.length() < 75 && c.moveToNext()) {
                val row = JSONObject().put("id", c.getLong(0)).put("address", (c.getString(1) ?: "").take(150)).put("body", (c.getString(2) ?: "").take(12_000))
                    .put("dateMs", c.getLong(3)).put("incoming", c.getInt(4) == 1).put("type", c.getInt(4)).put("status", c.getInt(5))
                if (!appendBounded(rows, row)) break
            }
        }; return JSONObject().put("messages", rows).put("threadId", thread)
    }
    private fun sendSms(a: JSONObject): JSONObject {
        need(Manifest.permission.SEND_SMS)
        require(supportsSms()) { "This device cannot send SMS" }
        val number = MessageRules.destination(a.getString("number")); val body = a.getString("text")
        require(body.isNotBlank() && body.length <= 12_000) { "Write a message of 1–12000 characters" }
        val token = a.getString("token")
        val sub = a.optInt("subscriptionId", SmsManager.getDefaultSmsSubscriptionId())
        require(SubscriptionManager.isValidSubscriptionId(sub)) { "Choose a default SMS SIM in Android Settings before sending" }
        val manager = context.getSystemService(SmsManager::class.java).createForSubscriptionId(sub)
        val parts = manager.divideMessage(body)
        if (!ledger.claim(token, MessageRules.fingerprint(number, body, sub), parts.size)) return ledger.status(token)!!
        fun receipt(part: Int, delivery: Boolean): PendingIntent {
            val intent = Intent(context, SmsResultReceiver::class.java).setData(Uri.parse("pixelbridge://sms/$token/$part/$delivery"))
                .putExtra("token", token).putExtra("part", part).putExtra("delivery", delivery)
            return PendingIntent.getBroadcast(context, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
        }
        try { manager.sendMultipartTextMessage(number, null, parts,
            ArrayList(parts.indices.map { receipt(it, false) }), ArrayList(parts.indices.map { receipt(it, true) })) }
        catch (e: Exception) { ledger.result(token, 0, false, false, e.message); throw e }
        return ledger.status(token)!!
    }
    private fun history(): JSONObject {
        need(Manifest.permission.READ_CALL_LOG)
        val rows = JSONArray()
        resolver.query(CallLog.Calls.CONTENT_URI, arrayOf("_id", "number", "name", "date", "duration", "type"), null, null, "date DESC")?.use { c ->
            while (rows.length() < 75 && c.moveToNext()) {
                if (!appendBounded(rows, JSONObject().put("id", c.getLong(0)).put("number", (c.getString(1) ?: "Unknown").take(150))
                    .put("name", (c.getString(2) ?: "").take(200)).put("dateMs", c.getLong(3)).put("durationSeconds", c.getLong(4)).put("type", c.getInt(5)))) break
            }
        }; return JSONObject().put("calls", rows)
    }
    private fun toolsStatus(): JSONObject {
        val audio = context.getSystemService(AudioManager::class.java)
        return JSONObject().put("volume", audio.getStreamVolume(AudioManager.STREAM_MUSIC)).put("maximumVolume", audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC))
            .put("flashlight", torchCamera != null).put("uptimeMs", SystemClock.elapsedRealtime())
            .put("vibrator", context.getSystemService(VibratorManager::class.java).defaultVibrator.hasVibrator())
    }
    private fun setTorch(on: Boolean) {
        need(Manifest.permission.CAMERA)
        val cm = context.getSystemService(CameraManager::class.java)
        if (!on) { torchCamera?.let { cm.setTorchMode(it, false) }; torchCamera = null; return }
        val camera = cm.cameraIdList.firstOrNull { val c = cm.getCameraCharacteristics(it); c.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true && c.get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK }
            ?: throw IllegalArgumentException("This phone has no rear flashlight")
        cm.setTorchMode(camera, true); torchCamera = camera
    }
    private fun apps(): JSONObject {
        val rows = JSONArray()
        context.packageManager.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0)
            .sortedBy { it.loadLabel(context.packageManager).toString() }.take(150).forEach { info ->
                appendBounded(rows, JSONObject().put("name", info.loadLabel(context.packageManager).toString().take(150))
                    .put("component", "${info.activityInfo.packageName}/${info.activityInfo.name}"))
            }
        return JSONObject().put("apps", rows)
    }
    fun refreshPermissions() { handler.post { if (watching) setWatching(true) } }
    fun reset() { handler.post { setWatching(false); handler.removeCallbacks(refreshNotice); try { setTorch(false) } catch (_: Exception) {} }; main.post { main.removeCallbacks(stopRing); stopRing.run() } }
    fun close() { reset(); handler.post { worker.quitSafely() }; if (events === event) events = null }
    companion object {
        @Volatile var events: ((JSONObject) -> Unit)? = null
        fun handles(op: String) = listOf("phone.", "sms.", "contacts.", "tools.", "notifications.").any(op::startsWith)
        private fun appendBounded(rows: JSONArray, row: JSONObject): Boolean {
            if (rows.toString().toByteArray(Charsets.UTF_8).size + row.toString().toByteArray(Charsets.UTF_8).size > 48_000) return false
            rows.put(row); return true
        }
    }
}
