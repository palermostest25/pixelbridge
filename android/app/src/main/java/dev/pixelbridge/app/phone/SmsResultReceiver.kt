package dev.pixelbridge.app.phone

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.telephony.SmsMessage

class SmsResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val token = intent.getStringExtra("token") ?: return
        val delivery = intent.getBooleanExtra("delivery", false)
        var ok = resultCode == Activity.RESULT_OK
        if (delivery && ok) {
            val pdu = intent.getByteArrayExtra("pdu")
            if (pdu != null) {
                val status = SmsMessage.createFromPdu(pdu, intent.getStringExtra("format") ?: "3gpp")?.status ?: -1
                if (status in 1..63 || status == -1) return // pending/unknown is not delivery confirmation
                ok = status == 0
            } else return
        }
        val prefs = context.getSharedPreferences("sms-receipts", Context.MODE_PRIVATE)
        val ledger = SendLedger({ prefs.getString("ledger", "{}") ?: "{}" }, { prefs.edit().putString("ledger", it).commit() })
        val receipt = ledger.result(token, intent.getIntExtra("part", 0), delivery, ok, if (ok) null else "Phone/carrier error $resultCode") ?: return
        PhoneBridge.events?.invoke(receipt.put("event", "sms.receipt"))
    }
}
