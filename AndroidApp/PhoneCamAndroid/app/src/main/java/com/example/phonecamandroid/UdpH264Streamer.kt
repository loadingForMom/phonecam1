package com.example.phonecamandroid

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.ImageFormat
import android.hardware.camera2.*
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Range
import android.view.Surface
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.ceil
import kotlin.math.min

/**
 * Camera2 -> MediaCodec(H264) -> UDP (PCAM v1 framing, 32-byte header)
 */
class UdpH264Streamer(private val ctx: Context) {

    var onLog: ((String) -> Unit)? = null
    var onStats: ((StreamStats) -> Unit)? = null

    private var previewSurface: Surface? = null

    private val running = AtomicBoolean(false)

    private var encoder: MediaCodec? = null
    private var inputSurface: Surface? = null

    private var udp: DatagramSocket? = null
    private var remote: InetSocketAddress? = null

    private var camera: Camera2Controller? = null

    private var drainThread: Thread? = null

    // Stats
    private var startedAtMs: Long = 0
    private var bytesSent: Long = 0
    private var framesSent: Long = 0
    private var droppedFrames: Long = 0
    private var lastStatsAtMs: Long = 0

    // PCAM counters
    private var seq: Int = 1
    private var frameId: Int = 1

    fun setPreviewSurface(surface: Surface?) {
        previewSurface = surface
        camera?.setPreviewSurface(surface)
    }

    fun start(host: String, port: Int, width: Int, height: Int, fps: Int, bitrate: Int): Boolean {
        if (running.getAndSet(true)) return true

        try {
            remote = InetSocketAddress(host, port)
            udp = DatagramSocket().apply {
                // connect helps performance + ICMP errors surface as exceptions on send()
                connect(remote)
            }

            val codec = setupEncoder(width, height, fps, bitrate)
            encoder = codec

            camera = Camera2Controller(ctx).also { cam ->
                cam.onLog = { log(it) }
                cam.start(
                    input = inputSurface!!,
                    preview = previewSurface,
                    width = width,
                    height = height,
                    fps = fps
                )
            }

            startedAtMs = System.currentTimeMillis()
            bytesSent = 0
            framesSent = 0
            droppedFrames = 0
            lastStatsAtMs = 0

            startDrainLoop(codec, host, port)

            updateStats(
                connectionState = "Streaming",
                remoteStr = "$host:$port"
            )
            log("Streamer started UDP->$host:$port, ${width}x$height@$fps bitrate=$bitrate")

            return true
        } catch (t: Throwable) {
            log("Start failed: ${Log.getStackTraceString(t)}")
            updateStats("Failed: ${t.javaClass.simpleName}", "$host:$port")
            stop()
            return false
        }
    }

    fun stop() {
        running.set(false)

        try { drainThread?.join(1200) } catch (_: Throwable) {}
        drainThread = null

        try { camera?.stop() } catch (_: Throwable) {}
        camera = null

        try { encoder?.stop() } catch (_: Throwable) {}
        try { encoder?.release() } catch (_: Throwable) {}
        encoder = null

        try { inputSurface?.release() } catch (_: Throwable) {}
        inputSurface = null

        try { udp?.close() } catch (_: Throwable) {}
        udp = null
        remote = null
    }

    // ---------------- Encoder ----------------

    private fun setupEncoder(width: Int, height: Int, fps: Int, bitrate: Int): MediaCodec {
        val format = MediaFormat.createVideoFormat(MIME, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1) // keyframe every 1 sec
        }

        val codec = MediaCodec.createEncoderByType(MIME)
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)

        inputSurface = codec.createInputSurface()

        // IMPORTANT: don't use setCallback (crashes on some devices)
        codec.start()
        return codec
    }

    private fun startDrainLoop(codec: MediaCodec, host: String, port: Int) {
        val thread = Thread({
            val info = MediaCodec.BufferInfo()

            var sentCsd = false

            while (running.get()) {
                try {
                    val outIndex = codec.dequeueOutputBuffer(info, 10_000)

                    when {
                        outIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> {
                            // no-op
                        }

                        outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                            val fmt = codec.outputFormat
                            val csd0 = fmt.getByteBuffer("csd-0")
                            val csd1 = fmt.getByteBuffer("csd-1")

                            if (!sentCsd) {
                                val au = buildCsdAccessUnit(csd0, csd1)
                                if (au != null) {
                                    // Mark as CONFIG (flag 0x02) + also treated as "key"
                                    sendAccessUnit(au, flags = FLAG_KEYFRAME or FLAG_CODEC_CONFIG)
                                    sentCsd = true
                                    log("Sent CSD (SPS/PPS), bytes=${au.size}")
                                } else {
                                    log("CSD not present in INFO_OUTPUT_FORMAT_CHANGED")
                                }
                            }
                        }

                        outIndex >= 0 -> {
                            val outBuf = codec.getOutputBuffer(outIndex)
                            if (outBuf != null && info.size > 0) {
                                outBuf.position(info.offset)
                                outBuf.limit(info.offset + info.size)

                                val data = ByteArray(info.size)
                                outBuf.get(data)

                                val isConfig = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0
                                val isKey = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0

                                val au = ensureAnnexB(data)

                                val flags = when {
                                    isConfig -> FLAG_CODEC_CONFIG
                                    isKey -> FLAG_KEYFRAME
                                    else -> 0
                                }

                                if (isConfig) {
                                    // some encoders still output config here; send it as отдельный AU
                                    sendAccessUnit(au, flags = flags or FLAG_KEYFRAME)
                                } else {
                                    sendAccessUnit(au, flags = flags)
                                }

                                framesSent++
                                bytesSent += au.size.toLong()
                                maybeUpdateStats("$host:$port")
                            }

                            codec.releaseOutputBuffer(outIndex, false)
                        }
                    }
                } catch (t: Throwable) {
                    log("Drain loop error: ${Log.getStackTraceString(t)}")
                    updateStats("Failed: drain", "$host:$port")
                    running.set(false)
                }
            }
        }, "PhoneCam-EncoderDrain")

        drainThread = thread
        thread.start()
    }

    // Convert to AnnexB if encoder outputs AVCC (length-prefixed)
    private fun ensureAnnexB(data: ByteArray): ByteArray {
        if (data.size >= 4 &&
            data[0] == 0.toByte() && data[1] == 0.toByte() && data[2] == 0.toByte() && data[3] == 1.toByte()
        ) {
            return data // already AnnexB
        }

        // Try AVCC: [len][nal][len][nal]...
        val out = ByteArray(data.size + 64) // small extra; if insufficient we'll re-alloc
        var outPos = 0
        var i = 0
        var tmp = out

        fun ensureCap(need: Int) {
            if (outPos + need <= tmp.size) return
            val grown = ByteArray((tmp.size + need) * 2)
            System.arraycopy(tmp, 0, grown, 0, outPos)
            tmp = grown
        }

        while (i + 4 <= data.size) {
            val nalLen =
                ((data[i].toInt() and 0xFF) shl 24) or
                        ((data[i + 1].toInt() and 0xFF) shl 16) or
                        ((data[i + 2].toInt() and 0xFF) shl 8) or
                        (data[i + 3].toInt() and 0xFF)

            i += 4
            if (nalLen <= 0 || i + nalLen > data.size) {
                // give up, return original
                return data
            }

            ensureCap(4 + nalLen)
            // start code
            tmp[outPos++] = 0
            tmp[outPos++] = 0
            tmp[outPos++] = 0
            tmp[outPos++] = 1
            System.arraycopy(data, i, tmp, outPos, nalLen)
            outPos += nalLen
            i += nalLen
        }

        return tmp.copyOf(outPos)
    }

    private fun buildCsdAccessUnit(csd0: ByteBuffer?, csd1: ByteBuffer?): ByteArray? {
        val a = csd0?.duplicate()?.let { bbToArray(it) }
        val b = csd1?.duplicate()?.let { bbToArray(it) }

        if (a == null && b == null) return null

        // Many devices give AnnexB SPS/PPS already; if not - try to prefix start codes.
        fun normalize(buf: ByteArray): ByteArray {
            return if (buf.size >= 4 && buf[0] == 0.toByte() && buf[1] == 0.toByte() && buf[2] == 0.toByte() && buf[3] == 1.toByte()) {
                buf
            } else {
                // add start code
                byteArrayOf(0, 0, 0, 1) + buf
            }
        }

        val na = a?.let { normalize(it) }
        val nb = b?.let { normalize(it) }

        return when {
            na != null && nb != null -> na + nb
            na != null -> na
            else -> nb
        }
    }

    private fun bbToArray(bb: ByteBuffer): ByteArray {
        val b = ByteArray(bb.remaining())
        bb.get(b)
        return b
    }

    // ---------------- UDP PCAM v1 ----------------

    private fun sendAccessUnit(accessUnit: ByteArray, flags: Int) {
        val sock = udp ?: return
        val r = remote ?: return

        val fid = frameId++
        val fragPayloadMax = MAX_DATAGRAM - HEADER_SIZE
        if (fragPayloadMax <= 0) return

        val fragCount = maxOf(1, ceil(accessUnit.size / fragPayloadMax.toDouble()).toInt())
        var offset = 0

        for (fragIndex in 0 until fragCount) {
            val take = min(fragPayloadMax, accessUnit.size - offset)
            val packet = ByteArray(HEADER_SIZE + take)

            // header (Little Endian)
            val bb = ByteBuffer.wrap(packet).order(ByteOrder.LITTLE_ENDIAN)
            bb.putInt(MAGIC_PCAM)              // 0..3
            bb.put(PCAM_VERSION.toByte())      // 4
            bb.put(flags.toByte())             // 5
            bb.putShort(HEADER_SIZE.toShort()) // 6..7 headerSize = 32
            bb.putInt(seq++)                   // 8..11 packetSeq
            bb.putInt(fid)                     // 12..15 frameId
            bb.putInt((System.currentTimeMillis() and 0x7FFFFFFF).toInt()) // 16..19 timestampMs (int)
            bb.putShort(fragIndex.toShort())   // 20..21
            bb.putShort(fragCount.toShort())   // 22..23
            bb.putInt(take)                    // 24..27 payloadLen
            bb.putInt(0)                       // 28..31 streamId

            // payload
            System.arraycopy(accessUnit, offset, packet, HEADER_SIZE, take)
            offset += take

            try {
                sock.send(DatagramPacket(packet, packet.size, r))
            } catch (t: Throwable) {
                droppedFrames++
                log("UDP send failed: ${t.javaClass.simpleName}: ${t.message}")
                // don't crash; keep trying
            }
        }
    }

    private fun maybeUpdateStats(remoteStr: String) {
        val now = System.currentTimeMillis()
        if (now - lastStatsAtMs < 500) return
        lastStatsAtMs = now

        val elapsed = (now - startedAtMs).coerceAtLeast(1)
        val fps = framesSent * 1000.0 / elapsed
        val kbps = (bytesSent * 8.0 / elapsed) // bits per ms
        val kbps2 = kbps // already kbps (since bits/ms == kbps)

        updateStats(
            connectionState = "Streaming",
            remoteStr = remoteStr,
            fps = fps,
            bitrateKbps = kbps2,
            droppedFrames = droppedFrames
        )
    }

    // ---------------- Stats + log ----------------

    private fun log(s: String) {
        onLog?.invoke(s)
        Log.d("PhoneCam", s)
    }

    private fun updateStats(
        connectionState: String,
        remoteStr: String,
        fps: Double? = null,
        bitrateKbps: Double? = null,
        droppedFrames: Long? = null,
        queueDepth: Int? = null,
        localIp: String? = null
    ) {
        // keep previous values if not provided
        val prev = StreamState.stats.value
        onStats?.invoke(
            StreamStats(
                connectionState = connectionState,
                remote = remoteStr,
                fps = fps ?: prev.fps,
                bitrateKbps = bitrateKbps ?: prev.bitrateKbps,
                droppedFrames = droppedFrames ?: prev.droppedFrames,
                queueDepth = queueDepth ?: prev.queueDepth,
                localIp = localIp ?: prev.localIp
            )
        )
    }

    companion object {
        private const val MIME = "video/avc"

        // PCAM constants (must match Windows FrameAssembler.cs)
        private const val MAGIC_PCAM = 0x4D414350  // 'P''C''A''M' little-endian int
        private const val PCAM_VERSION = 1
        private const val HEADER_SIZE = 32

        // Flags (must match protocol)
        private const val FLAG_KEYFRAME = 0x01
        private const val FLAG_CODEC_CONFIG = 0x02

        // Keep below typical MTU (1500). 32 header + 1368 payload ~= 1400 bytes.
        private const val MAX_DATAGRAM = 1400
    }

    // ---------------- Camera2 controller (local) ----------------

    private class Camera2Controller(private val ctx: Context) {
        var onLog: ((String) -> Unit)? = null

        private var thread: HandlerThread? = null
        private var handler: Handler? = null

        private var device: CameraDevice? = null
        private var session: CameraCaptureSession? = null

        private var previewSurface: Surface? = null

        fun setPreviewSurface(surface: Surface?) {
            previewSurface = surface
            // if already running - restart session
            if (device != null) {
                try { rebuildSession() } catch (_: Throwable) {}
            }
        }

        @SuppressLint("MissingPermission")
        fun start(input: Surface, preview: Surface?, width: Int, height: Int, fps: Int) {
            previewSurface = preview

            thread = HandlerThread("PhoneCam-Camera").also { it.start() }
            handler = Handler(thread!!.looper)

            val cm = ctx.getSystemService(Context.CAMERA_SERVICE) as CameraManager
            val camId = chooseBackCamera(cm)

            cm.openCamera(camId, object : CameraDevice.StateCallback() {
                override fun onOpened(camera: CameraDevice) {
                    device = camera
                    try {
                        createSession(input, width, height, fps)
                        onLog?.invoke("Camera opened: $camId")
                    } catch (t: Throwable) {
                        onLog?.invoke("Camera session create failed: ${Log.getStackTraceString(t)}")
                        stop()
                    }
                }

                override fun onDisconnected(camera: CameraDevice) {
                    onLog?.invoke("Camera disconnected")
                    stop()
                }

                override fun onError(camera: CameraDevice, error: Int) {
                    onLog?.invoke("Camera error=$error")
                    stop()
                }
            }, handler)
        }

        fun stop() {
            try { session?.close() } catch (_: Throwable) {}
            session = null

            try { device?.close() } catch (_: Throwable) {}
            device = null

            try { thread?.quitSafely() } catch (_: Throwable) {}
            thread = null
            handler = null
        }

        private fun rebuildSession() {
            val dev = device ?: return
            // We can't rebuild without knowing input surface; so just ignore here.
            // In this project previewSurface is optional; simplest is to do nothing.
            // Preview will be applied on next start().
            onLog?.invoke("Preview surface updated (will apply on next start)")
        }

        private fun chooseBackCamera(cm: CameraManager): String {
            cm.cameraIdList.forEach { id ->
                val chars = cm.getCameraCharacteristics(id)
                val facing = chars.get(CameraCharacteristics.LENS_FACING)
                if (facing == CameraCharacteristics.LENS_FACING_BACK) return id
            }
            return cm.cameraIdList.first()
        }

        private fun createSession(input: Surface, width: Int, height: Int, fps: Int) {
            val dev = device ?: return
            val h = handler ?: return

            val surfaces = ArrayList<Surface>(2)
            surfaces.add(input)
            previewSurface?.let { surfaces.add(it) }

            dev.createCaptureSession(surfaces, object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    session = s
                    val req = dev.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                        addTarget(input)
                        previewSurface?.let { addTarget(it) }

                        // FPS range (best effort)
                        set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(fps, fps))
                        set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
                        set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
                    }

                    s.setRepeatingRequest(req.build(), null, h)
                    onLog?.invoke("Capture session running")
                }

                override fun onConfigureFailed(s: CameraCaptureSession) {
                    onLog?.invoke("Capture session configure failed")
                }
            }, h)
        }
    }
}