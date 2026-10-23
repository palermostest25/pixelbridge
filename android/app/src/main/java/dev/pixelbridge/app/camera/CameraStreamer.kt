package dev.pixelbridge.app.camera

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Range
import android.util.Size
import android.view.Surface
import dev.pixelbridge.app.transport.Frame
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Executor
import kotlin.concurrent.thread

/** Camera2 -> MediaCodec input Surface -> H.264 Annex-B access units (protocol section 6). One camera at a time. */
class CameraStreamer(
    private val context: Context,
    private val sink: (Frame) -> Boolean,
    private val onEvent: (type: Int, json: JSONObject) -> Unit,
) {
    private val cm = context.getSystemService(CameraManager::class.java)
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    @Volatile private var draining = false
    @Volatile private var idrPending = false
    @Volatile private var codecConfig: ByteArray? = null
    @Volatile private var configJson: JSONObject? = null

    @Volatile var running = false
        private set
    @Volatile var framesSent = 0L
        private set
    @Volatile var framesDropped = 0L
        private set
    @Volatile var bytesSent = 0L
        private set

    fun capabilities(): JSONArray {
        val arr = JSONArray()
        try {
            for (id in cm.cameraIdList) {
                val ch = cm.getCameraCharacteristics(id)
                val map = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: continue
                val sizes = map.getOutputSizes(MediaCodec::class.java)
                    .filter { it.width <= 1920 && it.height <= 1080 }
                    .sortedByDescending { it.width * it.height }
                    .map { "${it.width}x${it.height}" }
                val fps = ch.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
                    ?.map { "${it.lower}-${it.upper}" } ?: emptyList()
                arr.put(
                    JSONObject().put("id", id).put("facing", facingName(ch))
                        .put("sensorOrientation", ch.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 0)
                        .put("sizes", JSONArray(sizes)).put("fpsRanges", JSONArray(fps))
                )
            }
        } catch (e: Exception) {
            Log.w(TAG, "camera enumeration failed: ${e.message}")
        }
        return arr
    }

    fun requestIdr() {
        idrPending = true
        try {
            codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
        } catch (_: Exception) {}
    }

    /** Called when a new host video socket authenticates mid-stream. */
    fun resendConfig() {
        configJson?.let { sink(Frame.json(Frame.VIDEO_CONFIG, it.toString())) }
        codecConfig?.let { sink(Frame(Frame.VIDEO_FRAME, it, Frame.FLAG_CODEC_CONFIG)) }
        requestIdr()
    }

    @SuppressLint("MissingPermission")
    @Synchronized
    fun start(cameraId: String, reqW: Int, reqH: Int, fps: Int, bitrate: Int) {
        stop("restart")
        if (context.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            error("permission", "CAMERA permission not granted on the phone")
            return
        }
        val ch = try { cm.getCameraCharacteristics(cameraId) } catch (e: Exception) {
            error("unsupported", "camera $cameraId not available: ${e.message}"); return
        }
        val map = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)!!
        val size = chooseSize(map.getOutputSizes(MediaCodec::class.java), reqW, reqH)
        val fpsRange = chooseFps(ch.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES), fps)

        val ht = HandlerThread("pb-camera").also { it.start() }
        thread = ht
        val h = Handler(ht.looper)
        handler = h
        val exec = Executor { h.post(it) }

        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, size.width, size.height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate.coerceIn(500_000, 12_000_000))
            setInteger(MediaFormat.KEY_FRAME_RATE, fpsRange.upper)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            setInteger(MediaFormat.KEY_PRIORITY, 0)
            setInteger(MediaFormat.KEY_LATENCY, 1)
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000)
            setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileHigh)
            setInteger(MediaFormat.KEY_LEVEL, MediaCodecInfo.CodecProfileLevel.AVCLevel4)
        }
        val enc = try {
            MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).also {
                try {
                    it.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                } catch (e: Exception) {
                    // Fall back to the encoder's default profile.
                    format.removeKey(MediaFormat.KEY_PROFILE); format.removeKey(MediaFormat.KEY_LEVEL)
                    it.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                }
            }
        } catch (e: Exception) {
            error("codec", "encoder configure failed: ${e.message}"); cleanup(); return
        }
        codec = enc
        val inSurface = enc.createInputSurface()
        surface = inSurface
        enc.start()

        val facing = facingName(ch)
        val cfg = JSONObject().put("codec", "h264").put("width", size.width).put("height", size.height)
            .put("fps", fpsRange.upper).put("bitrate", bitrate).put("cameraId", cameraId).put("facing", facing)
            .put("sensorOrientation", ch.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 0)
            .put("timestampSource", "boottime") // frame timestamps are normalised to elapsedRealtimeNanos
        configJson = cfg
        codecConfig = null
        framesSent = 0; framesDropped = 0; bytesSent = 0
        running = true
        sink(Frame.json(Frame.VIDEO_CONFIG, cfg.toString()))
        startDrain(enc)

        try {
            cm.openCamera(cameraId, exec, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) {
                    camera = device
                    createSession(device, inSurface, fpsRange, exec)
                }
                override fun onDisconnected(device: CameraDevice) {
                    device.close()
                    if (camera === device) { error("in-use", "camera disconnected (taken by another client)"); stop("disconnected") }
                }
                override fun onError(device: CameraDevice, code: Int) {
                    device.close()
                    val c = if (code == ERROR_CAMERA_IN_USE || code == ERROR_MAX_CAMERAS_IN_USE) "in-use"
                        else if (code == ERROR_CAMERA_DISABLED) "permission" else "internal"
                    error(c, "camera device error $code")
                    stop("error")
                }
            })
        } catch (e: CameraAccessException) {
            error(if (e.reason == CameraAccessException.CAMERA_IN_USE) "in-use" else "internal", e.message ?: "camera access")
            stop("error")
            return
        } catch (e: SecurityException) {
            error("permission", e.message ?: "security"); stop("error"); return
        }
        onEvent(Frame.STREAM_STARTED, JSONObject(cfg.toString()).put("kind", "video"))
    }

    private fun createSession(device: CameraDevice, target: Surface, fps: Range<Int>, exec: Executor) {
        val cb = object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(s: CameraCaptureSession) {
                session = s
                try {
                    val req = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                        addTarget(target)
                        set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fps)
                        set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO)
                        set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
                    }
                    s.setRepeatingRequest(req.build(), null, handler)
                } catch (e: Exception) {
                    error("internal", "repeating request failed: ${e.message}"); stop("error")
                }
            }
            override fun onConfigureFailed(s: CameraCaptureSession) {
                error("unsupported", "capture session configuration failed"); stop("error")
            }
        }
        device.createCaptureSession(
            SessionConfiguration(SessionConfiguration.SESSION_REGULAR, listOf(OutputConfiguration(target)), exec, cb)
        )
    }

    private fun startDrain(enc: MediaCodec) {
        draining = true
        thread(name = "pb-encoder", isDaemon = true) {
            val info = MediaCodec.BufferInfo()
            // Surface-input PTS may be CLOCK_MONOTONIC or CLOCK_BOOTTIME depending on the BufferQueue path.
            // Detect once and convert to elapsedRealtimeNanos (boottime), the protocol's device clock.
            var toBoottimeNs: Long? = null
            try {
                while (draining) {
                    val idx = enc.dequeueOutputBuffer(info, 100_000)
                    if (idx < 0) continue
                    val buf = enc.getOutputBuffer(idx)
                    if (buf != null && info.size > 0) {
                        val bytes = ByteArray(info.size)
                        buf.position(info.offset); buf.get(bytes)
                        var flags = 0
                        if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) { flags = Frame.FLAG_CODEC_CONFIG; codecConfig = bytes }
                        if (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0) { flags = flags or Frame.FLAG_KEYFRAME; idrPending = false }
                        val ptsNs = info.presentationTimeUs * 1000
                        if (toBoottimeNs == null && flags and Frame.FLAG_CODEC_CONFIG == 0) {
                            val mono = System.nanoTime(); val boot = android.os.SystemClock.elapsedRealtimeNanos()
                            toBoottimeNs = if (Math.abs(mono - ptsNs) < Math.abs(boot - ptsNs)) boot - mono else 0L
                        }
                        val f = Frame(Frame.VIDEO_FRAME, bytes, flags, ptsNs + (toBoottimeNs ?: 0L))
                        if (idrPending && flags == 0) {
                            framesDropped++ // waiting for a keyframe after a drop; sending deltas would corrupt decode
                        } else if (sink(f)) {
                            framesSent++; bytesSent += bytes.size
                        } else {
                            framesDropped++
                            if (flags and Frame.FLAG_CODEC_CONFIG == 0) requestIdr()
                        }
                    }
                    enc.releaseOutputBuffer(idx, false)
                }
            } catch (e: Exception) {
                if (draining) Log.w(TAG, "encoder drain ended: ${e.message}")
            }
        }
    }

    @Synchronized
    fun stop(reason: String) {
        if (!running && thread == null) return
        val wasRunning = running
        running = false
        cleanup()
        if (wasRunning && reason != "restart") onEvent(Frame.STREAM_STOPPED, JSONObject().put("kind", "video").put("reason", reason))
    }

    private fun cleanup() {
        draining = false
        try { session?.close() } catch (_: Exception) {}
        try { camera?.close() } catch (_: Exception) {}
        try { codec?.stop() } catch (_: Exception) {}
        try { codec?.release() } catch (_: Exception) {}
        try { surface?.release() } catch (_: Exception) {}
        session = null; camera = null; codec = null; surface = null
        thread?.quitSafely(); thread = null; handler = null
        configJson = null; codecConfig = null
    }

    private fun error(code: String, message: String) {
        Log.w(TAG, "video error $code: $message")
        onEvent(Frame.STREAM_ERROR, JSONObject().put("kind", "video").put("code", code).put("message", message))
    }

    private fun facingName(ch: CameraCharacteristics) = when (ch.get(CameraCharacteristics.LENS_FACING)) {
        CameraCharacteristics.LENS_FACING_BACK -> "back"; CameraCharacteristics.LENS_FACING_FRONT -> "front"; else -> "external"
    }

    private fun chooseSize(sizes: Array<Size>, w: Int, h: Int): Size {
        sizes.firstOrNull { it.width == w && it.height == h }?.let { return it }
        val aspect = w.toDouble() / h
        return sizes.filter { it.width * it.height <= w * h && Math.abs(it.width.toDouble() / it.height - aspect) < 0.01 }
            .maxByOrNull { it.width * it.height }
            ?: sizes.filter { it.width * it.height <= w * h }.maxByOrNull { it.width * it.height }
            ?: sizes.minBy { it.width * it.height }
    }

    private fun chooseFps(ranges: Array<Range<Int>>?, fps: Int): Range<Int> {
        if (ranges.isNullOrEmpty()) return Range(fps, fps)
        return ranges.firstOrNull { it.lower == fps && it.upper == fps }
            ?: ranges.filter { it.upper == fps }.maxByOrNull { it.lower }
            ?: ranges.minBy { Math.abs(it.upper - fps) }
    }

    private companion object { const val TAG = "PixelBridge" }
}
