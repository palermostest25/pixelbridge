package dev.pixelbridge.app.notifications

import android.content.Context
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.app.Notification
import android.app.RemoteInput
import android.content.Intent
import android.os.Bundle
import org.json.JSONArray
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import org.json.JSONObject

/**
 * Optional relay, active only after the user enables it in Android's Notification access settings.
 * Default mode sends app + time only; title/text are sent only if the user opted in on the phone.
 */
class NotificationRelayService : NotificationListenerService() {
    override fun onListenerConnected() { connected = true; instance = this; activeNotifications?.forEach { onNotificationPosted(it) } }
    override fun onListenerDisconnected() { connected = false; instance = null; targets.clear() }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.isOngoing || sbn.packageName == packageName) return
        val includeContent = getSharedPreferences("settings", Context.MODE_PRIVATE).getBoolean("notif_content", false)
        val extras = sbn.notification.extras
        val label = try {
            packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
        } catch (_: Exception) { sbn.packageName }
        targets.entries.removeAll { it.value.key == sbn.key }
        while (targets.size >= 200) targets.keys.firstOrNull()?.let { targets.remove(it) }
        val key = UUID.randomUUID().toString(); targets[key] = sbn
        val canReply = includeContent && sbn.notification.actions?.any { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true } == true
        val j = JSONObject().put("key", key).put("canReply", canReply).put("app", sbn.packageName).put("appLabel", label).put("postedMs", sbn.postTime)
            .put("category", sbn.notification.category ?: JSONObject.NULL)
            .put("redacted", !includeContent)
        if (includeContent) {
            j.put("senderDisplay", extras.getCharSequence(Notification.EXTRA_TITLE)?.toString() ?: "")
            j.put("text", extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()?.take(500) ?: "")
        }
        sink?.invoke(j)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        val removed = targets.entries.filter { it.value.key == sbn.key }.map { it.key }
        removed.forEach { targets.remove(it); dev.pixelbridge.app.phone.PhoneBridge.events?.invoke(JSONObject().put("event", "notifications.removed").put("key", it)) }
    }
    companion object {
        @Volatile private var instance: NotificationRelayService? = null
        private val targets = ConcurrentHashMap<String, StatusBarNotification>()
        fun reply(key: String, text: String) {
            require(text.isNotBlank() && text.length <= 4000) { "Write a reply of 1–4000 characters" }
            val service = instance ?: throw IllegalStateException("Enable PixelBridge notification access on the phone")
            require(service.getSharedPreferences("settings", Context.MODE_PRIVATE).getBoolean("notif_content", false)) { "Allow notification content on the phone before replying" }
            val target = targets[key] ?: throw IllegalArgumentException("This notification has expired")
            val current = service.activeNotifications?.firstOrNull { it.key == target.key && it.postTime == target.postTime }
                ?: throw IllegalArgumentException("This notification changed or was dismissed; refresh before replying")
            val action = current.notification.actions?.firstOrNull { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true }
                ?: throw IllegalArgumentException("This app does not offer a text reply")
            val results = Bundle()
            action.remoteInputs.filter { it.allowFreeFormInput }.forEach { results.putCharSequence(it.resultKey, text) }
            val fillIn = Intent(); RemoteInput.addResultsToIntent(action.remoteInputs, fillIn, results)
            action.actionIntent.send(service, 0, fillIn)
        }
        fun dismiss(key: String) {
            val service = instance ?: throw IllegalStateException("Notification access is off")
            val target = targets[key] ?: throw IllegalArgumentException("This notification has expired")
            service.cancelNotification(target.key); targets.remove(key)
        }
        fun currentNotifications(): JSONObject {
            val service = instance ?: throw IllegalStateException("Enable notification access on the phone")
            val include = service.getSharedPreferences("settings", Context.MODE_PRIVATE).getBoolean("notif_content", false)
            val rows = JSONArray()
            targets.entries.sortedByDescending { it.value.postTime }.take(50).forEach { (key, sbn) ->
                val row = JSONObject().put("key", key).put("app", sbn.packageName).put("appLabel", sbn.packageName).put("postedMs", sbn.postTime).put("redacted", !include)
                    .put("canReply", include && sbn.notification.actions?.any { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true } == true)
                if (include) row.put("senderDisplay", sbn.notification.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.take(200) ?: "")
                    .put("text", sbn.notification.extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()?.take(500) ?: "")
                if (rows.toString().toByteArray(Charsets.UTF_8).size + row.toString().toByteArray(Charsets.UTF_8).size < 48000) rows.put(row)
            }
            return JSONObject().put("notifications", rows)
        }
        @Volatile var sink: ((JSONObject) -> Unit)? = null
        @Volatile var connected = false
    }
}
