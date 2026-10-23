package dev.pixelbridge.app

import dev.pixelbridge.app.transport.AuthCrypto
import dev.pixelbridge.app.transport.Frame
import dev.pixelbridge.app.transport.FrameReader
import dev.pixelbridge.app.transport.FrameWriter
import dev.pixelbridge.app.transport.ProtocolException
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.fail
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File

/** Same vectors as the C# and Swift tests: protocol/test-vectors/auth.json. */
class ProtocolTest {
    private val vector = JSONObject(File("../../protocol/test-vectors/auth.json").readText().trimStart('\uFEFF'))

    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }

    @Test fun hmacMatchesSharedVector() {
        val key = ByteArray(32) { it.toByte() }
        assertEquals(vector.getString("clientMacHex"), hex(AuthCrypto.clientMac(key, "control", "AAAA", "BBBB")))
        assertEquals(vector.getString("serverMacHex"), hex(AuthCrypto.serverMac(key, "control", "AAAA", "BBBB")))
    }

    @Test fun encodesSharedFrameVector() {
        val out = ByteArrayOutputStream()
        val w = FrameWriter(out)
        w.write(Frame(0x10, ByteArray(0)))       // sequence 0
        w.write(Frame(0x10, ByteArray(0)))       // sequence 1
        out.reset()
        w.write(Frame(65, byteArrayOf(9), Frame.FLAG_KEYFRAME, 1)) // sequence 2
        assertEquals(vector.getJSONObject("frameExample").getString("encodedHex"), hex(out.toByteArray()))
    }

    @Test fun roundTrips() {
        val out = ByteArrayOutputStream()
        FrameWriter(out).apply { write(Frame(1, byteArrayOf(1, 2, 3), Frame.FLAG_CODEC_CONFIG, -5)) }
        val f = FrameReader(ByteArrayInputStream(out.toByteArray()), 100).read()
        assertArrayEquals(byteArrayOf(1, 2, 3), f.payload)
        assertEquals(-5L, f.timestampNs)
        assertEquals(Frame.FLAG_CODEC_CONFIG, f.flags)
    }

    @Test fun rejectsOversizeVersionAndFlags() {
        for (defect in listOf("size", "version", "flags")) {
            val out = ByteArrayOutputStream()
            FrameWriter(out).write(Frame(1, ByteArray(8)))
            val b = out.toByteArray()
            when (defect) { "size" -> b[2] = 0x7F; "version" -> b[4] = 9; "flags" -> b[7] = 0x40 }
            try { FrameReader(ByteArrayInputStream(b), 1024).read(); fail("accepted $defect") } catch (_: ProtocolException) {}
        }
    }

    @Test fun rejectsSequenceGap() {
        val out = ByteArrayOutputStream()
        val w = FrameWriter(out)
        w.write(Frame(1, ByteArray(0))); out.reset(); w.write(Frame(1, ByteArray(0)))
        try { FrameReader(ByteArrayInputStream(out.toByteArray()), 10).read(); fail() } catch (_: ProtocolException) {}
    }
}
