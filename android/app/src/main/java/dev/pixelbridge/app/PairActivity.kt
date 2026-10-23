package dev.pixelbridge.app

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.widget.TextView
import android.widget.Toast
import dev.pixelbridge.app.transport.PairingStore
import java.util.Base64

/**
 * Receives the pairing key from the ADB-authorised PC. Guarded by android.permission.DUMP in the
 * manifest, so only the ADB shell (or system) can start it. Shows a visible confirmation.
 */
class PairActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val host = intent.getStringExtra("host")?.take(64) ?: "PC"
        val key = try { Base64.getUrlDecoder().decode(intent.getStringExtra("key") ?: "") } catch (_: Exception) { null }
        val msg = if (key != null && key.size == 32) {
            PairingStore(this).save(key, host)
            Log.i("PixelBridge", "paired with host '$host'")
            "Paired with $host"
        } else {
            Log.w("PixelBridge", "pairing rejected: invalid key")
            "Pairing failed: invalid key"
        }
        key?.fill(0)
        setContentView(TextView(this).apply { text = msg; textSize = 24f; setPadding(40, 80, 40, 40) })
        Toast.makeText(this, msg, Toast.LENGTH_LONG).show()
        window.decorView.postDelayed({ finish() }, 1500)
    }
}
