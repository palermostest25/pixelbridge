package dev.pixelbridge.app

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.widget.Button
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import dev.pixelbridge.app.service.BridgeService
import dev.pixelbridge.app.transport.PairingStore

/**
 * Permission, status and start/stop UI. Usable through scrcpy because the phone's own display is dead.
 * Launching this activity (e.g. `am start -n dev.pixelbridge.app/.MainActivity`) starts the bridge
 * service while the app is legitimately in the foreground.
 */
class MainActivity : Activity() {
    private lateinit var status: TextView
    private val handler = Handler(Looper.getMainLooper())
    private val refresh = object : Runnable {
        override fun run() { render(); handler.postDelayed(this, 1000) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 60, 40, 40) }
        root.addView(TextView(this).apply { text = "PixelBridge"; textSize = 28f })
        status = TextView(this).apply { textSize = 14f; setPadding(0, 20, 0, 20) }
        root.addView(status)

        fun button(label: String, onClick: () -> Unit) =
            root.addView(Button(this).apply { text = label; setOnClickListener { onClick() } })

        button("Start bridge") { BridgeService.start(this) }
        button("Allow camera") { askPermission(Manifest.permission.CAMERA) }
        button("Allow messages, calling, contacts and call history") {
            askPermissions(Manifest.permission.READ_CONTACTS, Manifest.permission.READ_SMS, Manifest.permission.SEND_SMS,
                Manifest.permission.READ_CALL_LOG, Manifest.permission.READ_PHONE_STATE, Manifest.permission.CALL_PHONE)
        }
        button("Enable call controls (default Phone app)…") {
            val roles = getSystemService(android.app.role.RoleManager::class.java)
            if (roles.isRoleAvailable(android.app.role.RoleManager.ROLE_DIALER)) startActivityForResult(roles.createRequestRoleIntent(android.app.role.RoleManager.ROLE_DIALER), 52)
        }
        button("Phone / ongoing call") { startActivity(Intent(this, dev.pixelbridge.app.phone.CallActivity::class.java)) }
        button("Allow microphone") { askPermission(Manifest.permission.RECORD_AUDIO) }
        button("Allow notifications (status icon)") { askPermission(Manifest.permission.POST_NOTIFICATIONS) }
        button("Allow Bluetooth (nearby devices)") {
            askPermissions(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT)
        }
        button("Allow Wi-Fi scan (nearby Wi-Fi + location)") {
            askPermissions(Manifest.permission.NEARBY_WIFI_DEVICES, Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)
        }
        button("Notification access settings…") {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        val prefs = getSharedPreferences("settings", Context.MODE_PRIVATE)
        root.addView(CheckBox(this).apply {
            text = "Keep the USB bridge available after restarting"
            isChecked = prefs.getBoolean("resume_after_restart", true)
            setOnCheckedChangeListener { _, c -> prefs.edit().putBoolean("resume_after_restart", c).apply() }
        })
        root.addView(CheckBox(this).apply {
            text = "Send notification titles/text to PC (off = app name only)"
            isChecked = prefs.getBoolean("notif_content", false)
            setOnCheckedChangeListener { _, c -> prefs.edit().putBoolean("notif_content", c).apply(); BridgeService.start(this@MainActivity) }
        })
        button("Stop bridge") {
            startService(Intent(this, BridgeService::class.java).setAction(BridgeService.ACTION_STOP))
        }
        button("Unpair this PC") { PairingStore(this).clear(); render() }

        setContentView(ScrollView(this).apply { addView(root) })
        BridgeService.start(this)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        BridgeService.start(this) // re-launch via adb re-asserts the foreground service with current permissions
    }

    override fun onResume() {
        super.onResume()
        BridgeService.uiListener = { runOnUiThread { render() } }
        handler.post(refresh)
    }

    override fun onPause() {
        super.onPause()
        BridgeService.uiListener = null
        handler.removeCallbacks(refresh)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        BridgeService.start(this) // pick up newly granted camera/microphone FGS types while visible
        render()
    }

    private fun askPermission(p: String) = askPermissions(p)

    private fun askPermissions(vararg ps: String) {
        val missing = ps.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isNotEmpty()) requestPermissions(missing.toTypedArray(), 1)
    }

    private fun render() {
        fun g(p: String) = if (checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED) "granted" else "not granted"
        status.text = buildString {
            append("Camera: ").append(g(Manifest.permission.CAMERA)).append('\n')
            append("Microphone: ").append(g(Manifest.permission.RECORD_AUDIO)).append('\n')
            append("Notifications: ").append(g(Manifest.permission.POST_NOTIFICATIONS)).append('\n')
            append("Bluetooth: ").append(g(Manifest.permission.BLUETOOTH_CONNECT)).append('\n')
            append("Wi-Fi scan: ").append(g(Manifest.permission.NEARBY_WIFI_DEVICES)).append(" / location ")
                .append(g(Manifest.permission.ACCESS_FINE_LOCATION)).append("\n\n")
            append(BridgeService.instance?.summary() ?: "Bridge service not running\n${BridgeService.lastError ?: ""}")
        }
    }
}
