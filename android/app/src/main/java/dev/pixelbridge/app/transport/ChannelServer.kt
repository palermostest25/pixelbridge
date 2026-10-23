package dev.pixelbridge.app.transport

import android.util.Log
import org.json.JSONObject
import java.io.BufferedOutputStream
import java.io.Closeable
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.Base64
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/**
 * Loopback-only TCP listener for one logical channel. Every accepted socket must complete the HMAC
 * handshake (protocol section 3) before any other frame is exchanged. One authenticated peer at a time;
 * a newer authenticated peer replaces the older one.
 */
class ChannelServer(
    val name: String,
    private val port: Int,
    private val maxPayload: Int,
    queueCapacity: Int,
    private val dropOldest: Boolean,
    private val keyProvider: () -> ByteArray?,
    private val helloExtras: () -> Map<String, Any> = { emptyMap() },
    private val listener: Listener,
) : Closeable {

    interface Listener {
        fun onConnected(channel: ChannelServer) {}
        fun onDisconnected(channel: ChannelServer) {}
        fun onFrame(channel: ChannelServer, frame: Frame) {}
    }

    private val capacity = queueCapacity
    @Volatile private var server: ServerSocket? = null
    @Volatile private var active: Connection? = null
    @Volatile private var closed = false

    val isConnected get() = active != null

    fun start() {
        val ss = ServerSocket()
        ss.reuseAddress = true
        ss.bind(java.net.InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), 4)
        server = ss
        thread(name = "pb-$name-accept", isDaemon = true) {
            while (!closed) {
                val s = try { ss.accept() } catch (e: Exception) { if (!closed) Log.w(TAG, "$name accept: ${e.message}"); break }
                thread(name = "pb-$name-conn", isDaemon = true) { handle(s) }
            }
        }
        Log.i(TAG, "$name listening on 127.0.0.1:$port")
    }

    /** Queue a frame for the authenticated peer. Returns false if nobody is connected or the frame was dropped. */
    fun send(frame: Frame): Boolean {
        val c = active ?: return false
        if (dropOldest) {
            while (!c.queue.offer(frame)) c.queue.poll()
            return true
        }
        return c.queue.offer(frame)
    }

    fun queueDepth(): Int = active?.queue?.size ?: 0

    fun disconnectPeer() { active?.close() }

    override fun close() {
        closed = true
        try { server?.close() } catch (_: Exception) {}
        active?.close()
    }

    private fun handle(socket: Socket) {
        val conn = Connection(socket, capacity)
        try {
            socket.tcpNoDelay = true
            socket.soTimeout = AUTH_TIMEOUT_MS
            val reader = FrameReader(socket.getInputStream(), maxPayload)
            val writer = FrameWriter(BufferedOutputStream(socket.getOutputStream(), 64 * 1024))
            conn.writer = writer

            val key = keyProvider()
            val serverNonce = AuthCrypto.nonce()
            val hello = JSONObject()
                .put("protocolMajor", 1).put("protocolMinor", 0)
                .put("channel", name).put("serverNonce", serverNonce)
                .put("paired", key != null)
            helloExtras().forEach { (k, v) -> hello.put(k, v) }
            writer.write(Frame.json(Frame.HELLO, hello.toString()))
            if (key == null) {
                writer.write(Frame.json(Frame.AUTH_FAIL, """{"code":"not-paired"}"""))
                return
            }

            val resp = reader.read()
            if (resp.type != Frame.AUTH_RESPONSE) throw ProtocolException("expected AUTH_RESPONSE")
            val j = JSONObject(resp.text())
            val clientNonce = j.getString("clientNonce")
            val mac = Base64.getDecoder().decode(j.getString("mac"))
            val ok = j.getString("channel") == name &&
                Base64.getDecoder().decode(clientNonce).size == 32 &&
                AuthCrypto.constantTimeEquals(mac, AuthCrypto.clientMac(key, name, serverNonce, clientNonce))
            if (!ok) {
                Log.w(TAG, "$name: authentication failed")
                writer.write(Frame.json(Frame.AUTH_FAIL, """{"code":"bad-mac"}"""))
                return
            }
            val proof = Base64.getEncoder().encodeToString(AuthCrypto.serverMac(key, name, serverNonce, clientNonce))
            writer.write(Frame.json(Frame.AUTH_OK, JSONObject().put("mac", proof).toString()))
            socket.soTimeout = 0

            active?.close()
            active = conn
            conn.startWriter(name)
            Log.i(TAG, "$name: peer authenticated")
            listener.onConnected(this)
            while (!conn.closed) listener.onFrame(this, reader.read())
        } catch (e: Exception) {
            if (!conn.closed) Log.i(TAG, "$name: connection ended: ${e.javaClass.simpleName} ${e.message}")
        } finally {
            conn.close()
            if (active === conn) {
                active = null
                listener.onDisconnected(this)
            }
        }
    }

    private class Connection(val socket: Socket, capacity: Int) : Closeable {
        val queue = ArrayBlockingQueue<Frame>(capacity)
        lateinit var writer: FrameWriter
        @Volatile var closed = false
        private var writerThread: Thread? = null

        fun startWriter(name: String) {
            writerThread = thread(name = "pb-$name-writer", isDaemon = true) {
                try {
                    while (!closed) {
                        val f = queue.take()
                        writer.write(f)
                    }
                } catch (e: Exception) {
                    close()
                }
            }
        }

        override fun close() {
            closed = true
            writerThread?.interrupt()
            queue.clear()
            try { socket.close() } catch (_: Exception) {}
        }
    }

    companion object {
        private const val TAG = "PixelBridge"
        private const val AUTH_TIMEOUT_MS = 5000
    }
}
