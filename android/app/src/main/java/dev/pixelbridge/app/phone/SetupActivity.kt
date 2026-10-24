package dev.pixelbridge.app.phone

import android.Manifest
import android.app.Activity
import android.app.role.RoleManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.provider.Settings
import android.widget.Button
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.TextView
import dev.pixelbridge.app.service.BridgeService

/** ADB-only entry point for an explicit desktop setup action. Android still owns every permission/role dialog. */
class SetupActivity : Activity() {
    private val communications = arrayOf(Manifest.permission.READ_CONTACTS, Manifest.permission.READ_SMS, Manifest.permission.SEND_SMS,
        Manifest.permission.READ_CALL_LOG, Manifest.permission.READ_PHONE_STATE, Manifest.permission.CALL_PHONE)
    private var feature = "communications"
    override fun onCreate(state: Bundle?) {
        super.onCreate(state); feature = intent.getStringExtra("feature") ?: "communications"; render()
        if (state == null) launchSetup()
    }
    override fun onResume() { super.onResume(); BridgeService.start(this) }
    private fun launchSetup() {
        when (feature) {
            "calling" -> {
                val roles = getSystemService(RoleManager::class.java)
                if (roles.isRoleAvailable(RoleManager.ROLE_DIALER) && !roles.isRoleHeld(RoleManager.ROLE_DIALER)) startActivityForResult(roles.createRequestRoleIntent(RoleManager.ROLE_DIALER), 71)
            }
            "notifications" -> startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
            else -> { val missing = communications.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }; if (missing.isNotEmpty()) requestPermissions(missing.toTypedArray(), 72) }
        }
    }
    private fun render() {
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 60, 40, 40) }
        root.addView(TextView(this).apply { text = "PixelBridge setup"; textSize = 26f })
        root.addView(TextView(this).apply { text = when(feature) { "calling" -> "Choose PixelBridge as your Phone app to answer, end and manage calls from your computer. Audio stays on this phone or its Bluetooth headset."; "notifications" -> "Enable PixelBridge notification access in Android settings. Message content and replies are optional below."; else -> "Allow only the features you want. Contacts, SMS, calling and call history each need Android permission. Some installers restrict SMS/call history until they allow those permissions." }; setPadding(0,24,0,24) })
        if (feature == "notifications") {
            val prefs = getSharedPreferences("settings", Context.MODE_PRIVATE)
            root.addView(CheckBox(this).apply { text = "Share notification titles and text with my paired computer (enables replies)"; isChecked = prefs.getBoolean("notif_content",false); setOnCheckedChangeListener { _, value -> prefs.edit().putBoolean("notif_content",value).apply(); BridgeService.start(this@SetupActivity) } })
        } else if (feature == "calling") {
            val role = getSystemService(RoleManager::class.java).isRoleHeld(RoleManager.ROLE_DIALER)
            root.addView(TextView(this).apply { text = if (role) "Call controls enabled" else "Android still needs your default Phone app choice" })
        } else {
            communications.forEach { permission -> root.addView(TextView(this).apply { text = "${permission.substringAfterLast('.')}: ${if(checkSelfPermission(permission)==PackageManager.PERMISSION_GRANTED) "allowed" else "not allowed"}" }) }
        }
        root.addView(Button(this).apply { text = "Open setup again"; setOnClickListener { launchSetup() } })
        root.addView(Button(this).apply { text = "Done"; setOnClickListener { BridgeService.start(this@SetupActivity); finish() } })
        setContentView(android.widget.ScrollView(this).apply { addView(root) })
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) { super.onRequestPermissionsResult(requestCode, permissions, grantResults); render(); BridgeService.start(this) }
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) { super.onActivityResult(requestCode,resultCode,data);render(); BridgeService.start(this) }
}
