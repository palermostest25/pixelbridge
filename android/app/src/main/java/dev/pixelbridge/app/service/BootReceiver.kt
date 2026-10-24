package dev.pixelbridge.app.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import dev.pixelbridge.app.transport.PairingStore

/** Resume the idle USB listener after first unlock; camera/mic acquisition waits for a visible activity. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action !in listOf(Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED)) return
        if (!context.getSharedPreferences("settings", Context.MODE_PRIVATE).getBoolean("resume_after_restart", true)) return
        if (!PairingStore(context).isPaired()) return
        try { context.startForegroundService(Intent(context, BridgeService::class.java).setAction(BridgeService.ACTION_BOOT)) }
        catch (_: Exception) { /* The desktop will open the companion again at the next authorised USB connection. */ }
    }
}
