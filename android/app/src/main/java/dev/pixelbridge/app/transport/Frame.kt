package dev.pixelbridge.app.transport

import java.io.DataInputStream
import java.io.IOException
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** One protocol frame. See protocol/PROTOCOL.md section 1. */
class Frame(
    val type: Int,
    val payload: ByteArray,
    val flags: Int = 0,
    val timestampNs: Long = 0L,
) {
    val isKeyframe get() = flags and FLAG_KEYFRAME != 0
    val isCodecConfig get() = flags and FLAG_CODEC_CONFIG != 0

    fun text(): String = String(payload, Charsets.UTF_8)

    companion object {
        const val VERSION = 1
        const val HEADER_SIZE = 24

        const val FLAG_KEYFRAME = 0x1
        const val FLAG_CODEC_CONFIG = 0x2
        const val FLAG_END_OF_STREAM = 0x4
        const val KNOWN_FLAGS = FLAG_KEYFRAME or FLAG_CODEC_CONFIG or FLAG_END_OF_STREAM

        const val HELLO = 0x01
        const val CAPABILITIES = 0x02
        const val AUTH_RESPONSE = 0x04
        const val AUTH_OK = 0x05
        const val AUTH_FAIL = 0x06
        const val START_STREAM = 0x10
        const val STOP_STREAM = 0x11
        const val STREAM_STARTED = 0x12
        const val STREAM_STOPPED = 0x13
        const val STREAM_ERROR = 0x14
        const val STATUS = 0x15
        const val VIDEO_CONFIG = 0x20
        const val VIDEO_FRAME = 0x21
        const val REQUEST_IDR = 0x22
        const val AUDIO_CONFIG = 0x30
        const val AUDIO_FRAME = 0x31
        const val SENSOR_SAMPLE = 0x40
        const val BATTERY_STATUS = 0x41
        const val NETWORK_STATUS = 0x42
        const val NOTIFICATION_EVENT = 0x43
        const val LOCATION_SAMPLE = 0x44
        const val HEARTBEAT = 0x50
        const val PONG = 0x51
        const val ERROR = 0x7F
        const val RADIO_REQUEST = 0x60
        const val RADIO_RESPONSE = 0x61
        const val RADIO_EVENT = 0x62

        fun json(type: Int, json: String, timestampNs: Long = 0L) =
            Frame(type, json.toByteArray(Charsets.UTF_8), 0, timestampNs)
    }
}

class ProtocolException(message: String) : IOException(message)

/** Writes frames with a monotonically increasing sequence number. Not thread-safe; one writer thread per socket. */
class FrameWriter(private val out: OutputStream) {
    private var sequence = 0L
    private val header = ByteBuffer.allocate(Frame.HEADER_SIZE).order(ByteOrder.BIG_ENDIAN)

    fun write(frame: Frame) {
        header.clear()
        header.putInt(frame.payload.size)
        header.put(Frame.VERSION.toByte())
        header.put(frame.type.toByte())
        header.putShort(frame.flags.toShort())
        header.putLong(sequence++)
        header.putLong(frame.timestampNs)
        out.write(header.array(), 0, Frame.HEADER_SIZE)
        out.write(frame.payload)
        out.flush()
    }
}

/** Reads and validates frames. Throws [ProtocolException] on any violation; caller must close the socket. */
class FrameReader(input: java.io.InputStream, private val maxPayload: Int) {
    private val din = DataInputStream(input)
    private var expectedSequence = 0L
    private val header = ByteArray(Frame.HEADER_SIZE)

    fun read(): Frame {
        din.readFully(header)
        val bb = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN)
        val length = bb.int.toLong() and 0xFFFFFFFFL
        val version = bb.get().toInt() and 0xFF
        val type = bb.get().toInt() and 0xFF
        val flags = bb.short.toInt() and 0xFFFF
        val seq = bb.long
        val ts = bb.long
        if (version != Frame.VERSION) throw ProtocolException("unsupported version $version")
        if (length > maxPayload) throw ProtocolException("payload $length exceeds $maxPayload")
        if (flags and Frame.KNOWN_FLAGS.inv() != 0) throw ProtocolException("unknown flags $flags")
        if (seq != expectedSequence) throw ProtocolException("sequence $seq != $expectedSequence")
        expectedSequence++
        val payload = ByteArray(length.toInt())
        din.readFully(payload)
        return Frame(type, payload, flags, ts)
    }
}
