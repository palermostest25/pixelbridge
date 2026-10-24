package dev.pixelbridge.app.phone

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class MessageRulesTest {
    private val token = "abcdefghijklmnop12345678"
    @Test fun duplicateSendDoesNotClaimAgain() {
        var data = "{}"; val ledger = SendLedger({ data }, { data = it })
        assertTrue(ledger.claim(token, "one", 2)); assertFalse(ledger.claim(token, "one", 2))
        assertEquals("queued", ledger.status(token)!!.getString("state"))
    }
    @Test(expected = IllegalArgumentException::class) fun tokenCannotChangeRecipientOrBody() {
        var data = "{}"; val ledger = SendLedger({ data }, { data = it })
        ledger.claim(token, "one", 1); ledger.claim(token, "two", 1)
    }
    @Test fun receiptNeedsEveryPartAndNeverLosesFailure() {
        var data = "{}"; val ledger = SendLedger({ data }, { data = it })
        ledger.claim(token, "one", 2)
        assertEquals("queued",ledger.result(token,0,false,true)!!.getString("state"))
        assertEquals("sent",ledger.result(token,1,false,true)!!.getString("state"))
        assertEquals("sent",ledger.result(token,0,true,true)!!.getString("state"))
        assertEquals("delivered",ledger.result(token,1,true,true)!!.getString("state"))
        ledger.result(token,1,true,false,"carrier failure")
        assertEquals("failed",ledger.result(token,0,false,true)!!.getString("state"))
    }
    @Test fun separateReceiverAndSenderInstancesDoNotLoseUpdates() {
        var data = "{}"; val sender = SendLedger({ data }, { data = it }); val receiver = SendLedger({ data }, { data = it })
        sender.claim(token,"one",100)
        val pool=Executors.newFixedThreadPool(4)
        for(i in 0 until 100) pool.submit { receiver.result(token,i,false,true) }
        pool.shutdown(); assertTrue(pool.awaitTermination(5,TimeUnit.SECONDS))
        assertEquals("sent",sender.status(token)!!.getString("state"))
    }
    @Test fun destinationAndFingerprintAreStable() {
        assertEquals("+15550100", MessageRules.destination("+1 (555) 0100"))
        assertEquals(MessageRules.fingerprint("+15550100","hello",1),MessageRules.fingerprint("+15550100","hello",1))
        assertNotEquals(MessageRules.fingerprint("+15550100","hello",1),MessageRules.fingerprint("+15550100","hello",2))
    }
    @Test(expected=IllegalArgumentException::class) fun rejectsDestinationInjection() { MessageRules.destination("555; am start") }
}
