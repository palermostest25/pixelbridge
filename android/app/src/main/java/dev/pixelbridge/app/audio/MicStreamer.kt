package dev.pixelbridge.app.audio

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.AudioTimestamp
import android.media.MediaRecorder
import android.os.SystemClock
import android.util.Log
import dev.pixelbridge.app.transport.Frame
import org.json.JSONObject
import kotlin.concurrent.thread

/** AudioRecord PCM16 mono 48 kHz in 20 ms frames (protocol section 7). Runs only after RECORD_AUDIO consent. */
class MicStreamer(
    private val context: Context,
    private val sink: (Frame) -> Boolean,
    private val onEvent: (type: Int, json: JSONObject) -> Unit,
) {
    @Volatile var running = false
        private set
    @Volatile var framesSent = 0L
        private set
    private var worker: Thread? = null
    private var config: JSONObject? = null

    fun resendConfig() { config?.let { sink(Frame.json(Frame.AUDIO_CONFIG, it.toString())) } }

    @SuppressLint("MissingPermission")
    @Synchronized
    fun start(rateHz: Int = 48000) {
        stop("restart")
        if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            onEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "audio").put("code", "permission")
                .put("message", "RECORD_AUDIO permission not granted on the phone"))
            return
        }
        val frameSamples = rateHz / 50
        val minBuf = AudioRecord.getMinBufferSize(rateHz, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val rec = try {
            AudioRecord.Builder()
                .setAudioSource(MediaRecorder.AudioSource.MIC)
                .setAudioFormat(AudioFormat.Builder().setSampleRate(rateHz)
                    .setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(maxOf(minBuf, frameSamples * 2 * 8))
                .build()
        } catch (e: Exception) {
            onEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "audio").put("code", "unsupported").put("message", e.message ?: "AudioRecord"))
            return
        }
        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            rec.release()
            onEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "audio").put("code", "in-use").put("message", "AudioRecord failed to initialise"))
            return
        }
        val cfg = JSONObject().put("rateHz", rateHz).put("channels", 1).put("encoding", "pcm_s16le").put("frameMs", 20)
        config = cfg
        running = true
        framesSent = 0
        rec.startRecording()
        sink(Frame.json(Frame.AUDIO_CONFIG, cfg.toString()))
        onEvent(Frame.STREAM_STARTED, JSONObject(cfg.toString()).put("kind", "audio"))
        worker = thread(name = "pb-mic", isDaemon = true) {
            val buf = ByteArray(frameSamples * 2)
            val ts = AudioTimestamp()
            var framePos = 0L
            try {
                while (running) {
                    var off = 0
                    while (off < buf.size && running) {
                        val n = rec.read(buf, off, buf.size - off)
                        if (n < 0) throw IllegalStateException("AudioRecord.read=$n")
                        off += n
                    }
                    if (!running) break
                    val captureNs = if (rec.getTimestamp(ts, AudioTimestamp.TIMEBASE_BOOTTIME) == AudioRecord.SUCCESS)
                        ts.nanoTime + (framePos - ts.framePosition) * 1_000_000_000L / rateHz
                    else SystemClock.elapsedRealtimeNanos() - 20_000_000L
                    framePos += frameSamples
                    if (sink(Frame(Frame.AUDIO_FRAME, buf.copyOf(), 0, captureNs))) framesSent++
                }
            } catch (e: Exception) {
                Log.w("PixelBridge", "mic ended: ${e.message}")
                onEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "audio").put("code", "internal").put("message", e.message ?: "mic"))
            } finally {
                try { rec.stop() } catch (_: Exception) {}
                rec.release()
            }
        }
    }

    @Synchronized
    fun stop(reason: String) {
        if (!running) return
        running = false
        worker?.join(500)
        worker = null
        config = null
        if (reason != "restart") onEvent(Frame.STREAM_STOPPED, JSONObject().put("kind", "audio").put("reason", reason))
    }
}
