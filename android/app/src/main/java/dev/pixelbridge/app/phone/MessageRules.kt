package dev.pixelbridge.app.phone

import org.json.JSONObject
import java.security.MessageDigest

/** Durable send receipts: retries reuse a token, never silently send a second SMS. */
class SendLedger(private val load: () -> String, private val save: (String) -> Unit, private val now: () -> Long = System::currentTimeMillis) {
    companion object { private val lock = Any() }
    fun claim(token: String, fingerprint: String, parts: Int): Boolean = synchronized(lock) {
        require(token.matches(Regex("[A-Za-z0-9_-]{16,80}"))) { "Invalid send token" }
        val data = read()
        if (data.has(token)) {
            require(data.getJSONObject(token).getString("fingerprint") == fingerprint) { "This send token was already used for a different message" }
            return@synchronized false
        }
        while (data.length() >= 150) data.remove(data.keys().asSequence().minByOrNull { data.getJSONObject(it).optLong("time") }!!)
        data.put(token, JSONObject().put("fingerprint", fingerprint).put("time", now()).put("parts", parts)
            .put("state", "queued").put("sent", JSONObject()).put("delivered", JSONObject()))
        save(data.toString()); true
    }
    fun result(token: String, part: Int, delivery: Boolean, success: Boolean, error: String? = null): JSONObject? = synchronized(lock) {
        val data = read(); val row = data.optJSONObject(token) ?: return@synchronized null
        if (row.optString("state") != "failed") {
            if (!success) row.put("state", "failed").put("error", error ?: "SMS failed")
            else {
                row.getJSONObject(if (delivery) "delivered" else "sent").put(part.toString(), true)
                val count = row.optInt("parts", 1)
                row.put("state", when { row.getJSONObject("delivered").length() == count -> "delivered"; row.getJSONObject("sent").length() == count -> "sent"; else -> "queued" })
            }
        }
        data.put(token, row); save(data.toString()); publicRow(token, row)
    }
    fun status(token: String): JSONObject? = synchronized(lock) { read().optJSONObject(token)?.let { publicRow(token, it) } }
    private fun read(): JSONObject = try { JSONObject(load()) } catch (e: Exception) { throw IllegalStateException("Saved SMS receipts are unavailable; check the phone before sending another message", e) }
    private fun publicRow(token: String, row: JSONObject) = JSONObject().put("token", token).put("state", row.optString("state"))
        .put("time", row.optLong("time")).put("error", row.opt("error") ?: JSONObject.NULL)
}
object MessageRules {
    fun destination(value: String): String {
        val v = value.trim()
        require(v.matches(Regex("\\+?[0-9][0-9 ()\\-.]{1,78}"))) { "Enter a phone number, including its country code when needed" }
        return v.filter { it.isDigit() || it == '+' }
    }
    fun fingerprint(number: String, body: String, subscription: Int): String = MessageDigest.getInstance("SHA-256")
        .digest("$number\u0000$body\u0000$subscription".toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
}
