package dev.pixelbridge.app.notifications

import android.content.Context
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.app.Notification
import org.json.JSONObject

/**
 * Optional relay, active only after the user enables it in Android's Notification access settings.
 * Default mode sends app + time only; title/text are sent only if the user opted in on the phone.
 */
class NotificationRelayService : NotificationListenerService() {
    override fun onListenerConnected() { connected = true }
    override fun onListenerDisconnected() { connected = false }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.isOngoing || sbn.packageName == packageName) return
        val includeContent = getSharedPreferences("settings", Context.MODE_PRIVATE).getBoolean("notif_content", false)
        val extras = sbn.notification.extras
        val label = try {
            packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
        } catch (_: Exception) { sbn.packageName }
        val j = JSONObject().put("app", sbn.packageName).put("appLabel", label).put("postedMs", sbn.postTime)
            .put("category", sbn.notification.category ?: JSONObject.NULL)
            .put("redacted", !includeContent)
        if (includeContent) {
            j.put("senderDisplay", extras.getCharSequence(Notification.EXTRA_TITLE)?.toString() ?: "")
            j.put("text", extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()?.take(500) ?: "")
        }
        sink?.invoke(j)
    }

    companion object {
        @Volatile var sink: ((JSONObject) -> Unit)? = null
        @Volatile var connected = false
    }
}
