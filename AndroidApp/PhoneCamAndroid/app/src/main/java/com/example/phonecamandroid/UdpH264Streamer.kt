package com.example.phonecamandroid

import android.content.Context
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicBoolean

class UdpH264Streamer(private val ctx: Context) {

    var onLog: ((String) -> Unit)? = null
    var onStats: ((StreamStats) -> Unit)? = null

    private var previewSurface: Surface? = null

    private var camera: Camera2Controller? = null
    private var encoder: MediaCodec? = null
    private var inputSurface: Surface? = null

    private var udp: DatagramSocket? = null
    private var remoteAddr: InetAddress? = null
    private var remotePort: Int = 0

    private var startedAtMs: Long = 0
    private var bytesSent: Long = 0
    private var framesSent: Long = 0
    private var droppedFrames: Long = 0

    private val running = AtomicBoolean(false)
    private var drainThread: Thread? = null

    // Camera thread
    private var cameraThread: HandlerThread? = null
    private var cameraHandler: Handler? = null

    fun setPreviewSurface(surface: Surface?) {
        previewSurface = surface
        camera?.setPreviewSurface(surface)
    }

    fun start(
        host: String,
        port: Int,
        width: Int,
        height: Int,
        fps: Int,
        bitrate: Int
    ): Boolean {
        stop()

        try {
            running.set(true)

            remoteAddr = InetAddress.getByName(host)
            remotePort = port
            udp = DatagramSocket()

            cameraThread = HandlerThread("PhoneCam-Camera").also { it.start() }
            cameraHandler = Handler(cameraThread!!.looper)

            val enc = setupEncoder(width, height, fps, bitrate)
            encoder = enc

            val cam = Camera2Controller(ctx, cameraHandler!!).also {
                it.onLog = { s: String -> log(s) }
                it.setPreviewSurface(previewSurface)
                it.start(width, height, fps, inputSurface!!)
            }
            camera = cam

            startedAtMs = System.currentTimeMillis()
            bytesSent = 0
            framesSent = 0
            droppedFrames = 0

            startDrainLoop(enc)

            updateStats(
                connectionState = "Streaming",
                remote = "$host:$port",
                localIp = udp?.localAddress?.hostAddress ?: "-"
            )
            log("Streamer started: $host:$port, $width x $height @ $fps, bitrate=$bitrate")

            return true
        } catch (t: Throwable) {
            log("Start failed: ${Log.getStackTraceString(t)}")
            updateStats(
                connectionState = "Failed: ${t.javaClass.simpleName}",
                remote = "$host:$port",
                localIp = udp?.localAddress?.hostAddress ?: "-"
            )
            stop()
            return false
        }
    }

    fun stop() {
        running.set(false)

        try { drainThread?.join(800) } catch (_: Throwable) { }
        drainThread = null

        try { camera?.stop() } catch (_: Throwable) { }
        camera = null

        try { encoder?.stop() } catch (_: Throwable) { }
        try { encoder?.release() } catch (_: Throwable) { }
        encoder = null

        try { inputSurface?.release() } catch (_: Throwable) { }
        inputSurface = null

        try { udp?.close() } catch (_: Throwable) { }
        udp = null

        try { cameraThread?.quitSafely() } catch (_: Throwable) { }
        cameraThread = null
        cameraHandler = null
    }

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

        // ВАЖНО: не используем setCallback вообще (на твоём устройстве оно падало)
        codec.start()
        return codec
    }

    private fun startDrainLoop(codec: MediaCodec) {
        val thread = Thread({
            val info = MediaCodec.BufferInfo()

            // Don't send garbage before SPS/PPS
            var sentCsd = false
            var lastStatMs = 0L

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
                                sendCsdIfPresent(csd0, csd1)
                                sentCsd = true
                            }
                        }

                        outIndex >= 0 -> {
                            val buf = codec.getOutputBuffer(outIndex)
                            if (buf != null && info.size > 0) {
                                val data = ByteArray(info.size)
                                buf.position(info.offset)
                                buf.limit(info.offset + info.size)
                                buf.get(data)

                                val isConfig = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0
                                val isKey = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0

                                if (isConfig) {
                                    sendFrame(data, flags = 1)
                                } else {
                                    sendFrame(data, flags = if (isKey) 2 else 0)
                                }

                                framesSent++
                            }

                            codec.releaseOutputBuffer(outIndex, false)
                        }
                    }

                    val now = System.currentTimeMillis()
                    if (now - lastStatMs >= 1000) {
                        lastStatMs = now
                        val sec = ((now - startedAtMs).coerceAtLeast(1)).toDouble() / 1000.0
                        val kbps = (bytesSent * 8.0 / 1000.0 / sec).toInt()
                        val fpsNow = (framesSent / sec)

                        updateStats(
                            connectionState = "Streaming",
                            remote = "${remoteAddr?.hostAddress ?: "-"}:$remotePort",
                            fps = fpsNow,
                            bitrateKbps = kbps.toDouble(),
                            droppedFrames = droppedFrames,
                            queueDepth = 0,
                            localIp = udp?.localAddress?.hostAddress ?: "-"
                        )
                    }

                } catch (t: Throwable) {
                    log("Drain loop error: ${Log.getStackTraceString(t)}")
                    updateStats(
                        connectionState = "Failed: encoder drain",
                        remote = "${remoteAddr?.hostAddress ?: "-"}:$remotePort",
                        localIp = udp?.localAddress?.hostAddress ?: "-",
                        droppedFrames = droppedFrames
                    )
                    running.set(false)
                }
            }
        }, "PhoneCam-EncoderDrain")

        thread.isDaemon = true
        drainThread = thread
        thread.start()
    }

    private fun sendCsdIfPresent(csd0: ByteBuffer?, csd1: ByteBuffer?) {
        val b0 = csd0?.let { it.duplicate().toByteArray() }
        val b1 = csd1?.let { it.duplicate().toByteArray() }

        if (b0 != null && b0.isNotEmpty()) sendFrame(ensureAnnexB(b0), flags = 1)
        if (b1 != null && b1.isNotEmpty()) sendFrame(ensureAnnexB(b1), flags = 1)

        log("Sent CSD: csd0=${b0?.size ?: 0}, csd1=${b1?.size ?: 0}")
    }

    private fun ensureAnnexB(data: ByteArray): ByteArray {
        if (data.size >= 4 &&
            data[0] == 0.toByte() && data[1] == 0.toByte() &&
            data[2] == 0.toByte() && data[3] == 1.toByte()
        ) return data

        val out = ByteArray(data.size + 4)
        out[0] = 0
        out[1] = 0
        out[2] = 0
        out[3] = 1
        System.arraycopy(data, 0, out, 4, data.size)
        return out
    }

    private fun sendFrame(nal: ByteArray, flags: Int) {
        val sock = udp ?: return
        val addr = remoteAddr ?: return
        if (remotePort <= 0) return

        val header = ByteArray(4 + 1 + 1 + 1 + 1 + 4 + 4 + 4)
        header[0] = 'P'.code.toByte()
        header[1] = 'C'.code.toByte()
        header[2] = 'A'.code.toByte()
        header[3] = 'M'.code.toByte()
        header[4] = 1 // version
        header[5] = flags.toByte()
        header[6] = 0 // streamId
        header[7] = 0 // reserved

        val seq = framesSent.toInt()
        val ts = ((System.currentTimeMillis() - startedAtMs).coerceAtLeast(0)).toInt()
        putU32LE(header, 8, seq)
        putU32LE(header, 12, ts)
        putU32LE(header, 16, nal.size)

        val packet = ByteArray(header.size + nal.size)
        System.arraycopy(header, 0, packet, 0, header.size)
        System.arraycopy(nal, 0, packet, header.size, nal.size)

        try {
            sock.send(DatagramPacket(packet, packet.size, addr, remotePort))
            bytesSent += packet.size.toLong()
        } catch (t: Throwable) {
            droppedFrames++
            log("UDP send failed: ${t.message}")
        }
    }

    private fun putU32LE(dst: ByteArray, offset: Int, v: Int) {
        dst[offset + 0] = (v and 0xFF).toByte()
        dst[offset + 1] = ((v ushr 8) and 0xFF).toByte()
        dst[offset + 2] = ((v ushr 16) and 0xFF).toByte()
        dst[offset + 3] = ((v ushr 24) and 0xFF).toByte()
    }

    private fun ByteBuffer.toByteArray(): ByteArray {
        val b = ByteArray(remaining())
        get(b)
        return b
    }

    private fun log(s: String) {
        onLog?.invoke(s)
        Log.d("PhoneCam", s)
    }

    private fun updateStats(
        connectionState: String,
        remote: String,
        fps: Double = 0.0,
        bitrateKbps: Double = 0.0,
        droppedFrames: Long = 0L,
        queueDepth: Int = 0,
        localIp: String = "-"
    ) {
        onStats?.invoke(
            StreamStats(
                connectionState = connectionState,
                remote = remote,
                fps = fps,
                bitrateKbps = bitrateKbps,
                droppedFrames = droppedFrames,
                queueDepth = queueDepth,
                localIp = localIp
            )
        )
    }

    companion object {
        private const val MIME = "video/avc"
    }
}