package dev.pixelbridge.app.phone

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.telecom.TelecomManager
import android.view.Gravity
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import org.json.JSONObject

/** Full local fallback UI, including when USB is unplugged. Required for the optional default Phone role. */
class CallActivity : Activity() {
    private val listener: () -> Unit = { runOnUiThread { render() } }
    override fun onCreate(savedInstanceState: Bundle?) { super.onCreate(savedInstanceState); setShowWhenLocked(true); setTurnScreenOn(true); render() }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); render() }
    override fun onResume() { super.onResume(); CallRegistry.listeners.add(listener); render() }
    override fun onPause() { CallRegistry.listeners.remove(listener); super.onPause() }
    private fun render() {
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(32, 64, 32, 32); gravity = Gravity.CENTER_HORIZONTAL }
        root.addView(TextView(this).apply { text = "PixelBridge Phone"; textSize = 26f })
        val state = CallRegistry.snapshot(); val calls = state.getJSONArray("calls")
        fun button(text: String, action: () -> Unit) { root.addView(Button(this).apply { this.text = text; setOnClickListener { action() } }) }
        if (calls.length() > 0) {
            for (i in 0 until calls.length()) {
                val call = calls.getJSONObject(i); val id = call.getString("id")
                root.addView(TextView(this).apply { text = "${call.optString("number")}\n${call.optString("state")}"; textSize = 22f; setPadding(0, 24, 0, 24) })
                fun action(op: String, args: JSONObject = JSONObject()) { CallRegistry.request(op, args.put("callId", id)) { runOnUiThread { android.widget.Toast.makeText(this@CallActivity, it.optString("message"), android.widget.Toast.LENGTH_LONG).show() } } }
                if (call.optString("state") == "ringing") button("Answer") { action("phone.answer") }
                button("End call") { action("phone.end") }
                if (call.optBoolean("canHold")) button(if (call.optString("state") == "held") "Resume" else "Hold") { action(if (call.optString("state") == "held") "phone.unhold" else "phone.hold") }
                button(if (state.optBoolean("muted")) "Unmute" else "Mute") { action("phone.mute", JSONObject().put("enabled", !state.optBoolean("muted"))) }
                button(if (state.optString("route") == "speaker") "Use phone audio" else "Speaker") { action("phone.route", JSONObject().put("route", if (state.optString("route") == "speaker") "phone" else "speaker")) }
                val tone = EditText(this).apply { hint = "Keypad digit"; inputType = android.text.InputType.TYPE_CLASS_PHONE }; root.addView(tone)
                button("Send keypad tone") { val digit = tone.text.toString().take(1); if (digit.isNotEmpty()) action("phone.dtmf", JSONObject().put("digit", digit)) }
            }
        } else {
            val number = EditText(this).apply { hint = "Phone number"; inputType = android.text.InputType.TYPE_CLASS_PHONE; setText(intent?.data?.schemeSpecificPart ?: "") }; root.addView(number)
            button("Call") {
                if (checkSelfPermission(Manifest.permission.CALL_PHONE) != PackageManager.PERMISSION_GRANTED) requestPermissions(arrayOf(Manifest.permission.CALL_PHONE), 51)
                else try { getSystemService(TelecomManager::class.java).placeCall(Uri.fromParts("tel", number.text.toString(), null), Bundle()) }
                catch (e: Exception) { android.widget.Toast.makeText(this, e.message, android.widget.Toast.LENGTH_LONG).show() }
            }
        }
        setContentView(android.widget.ScrollView(this).apply { addView(root) })
    }
}
