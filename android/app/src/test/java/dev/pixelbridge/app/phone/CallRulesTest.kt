package dev.pixelbridge.app.phone

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class CallRulesTest {
    @Test fun explicitSimWinsOverDefault() {
        assertEquals("work", CallRules.chooseAccount(listOf("personal", "work"), "personal", JSONObject().put("accountId", "work")))
    }
    @Test fun missingAndJsonNullUseDefault() {
        for (args in listOf(JSONObject(), JSONObject().put("accountId", JSONObject.NULL), JSONObject().put("accountId", ""))) {
            assertEquals("personal", CallRules.chooseAccount(listOf("personal", "work"), "personal", args))
        }
    }
    @Test fun singleSimDoesNotNeedDefault() {
        assertEquals("only", CallRules.chooseAccount(listOf("only"), "", JSONObject()))
        assertNull(CallRules.chooseAccount(emptyList(), "", JSONObject()))
    }
    @Test fun twoSimsWithoutDefaultMustNotGuess() {
        assertThrows(IllegalArgumentException::class.java) { CallRules.chooseAccount(listOf("personal", "work"), "", JSONObject()) }
    }
    @Test fun staleSelectionMustNotDialAnotherLine() {
        assertThrows(IllegalArgumentException::class.java) { CallRules.chooseAccount(listOf("personal"), "personal", JSONObject().put("accountId", "removed")) }
    }
}
