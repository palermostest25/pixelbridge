package dev.pixelbridge.app.phone

import org.json.JSONObject

/** Resolve the user's phone line without guessing between two SIMs. IDs are opaque status tokens. */
internal object CallRules {
    fun chooseAccount(ids: Collection<String>, defaultId: String, args: JSONObject): String? {
        val requested = if (args.isNull("accountId")) "" else args.optString("accountId")
        if (requested.isNotBlank()) {
            require(requested in ids) { "This phone line is no longer available; refresh and choose a SIM" }
            return requested
        }
        if (defaultId.isNotBlank() && defaultId in ids) return defaultId
        if (ids.size == 1) return ids.first()
        require(ids.size <= 1) { "Choose the SIM/phone line in Calls before dialing" }
        return null
    }
}
