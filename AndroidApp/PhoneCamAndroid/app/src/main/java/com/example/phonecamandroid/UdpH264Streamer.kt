package com.example.phonecamandroid

import android.content.Context
import android.hardware.camera2.*
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.ceil
import kotlin.math.min

class UdpH264Streamer(
    private val context: Context
) {
    // FrameAssembler ожидает magic = "PCAM" в little-endian => 0x4D414350
    private val MAGIC_PCAM = 0x4D414350
    private val HEADER_SIZE = 24

    // Держим датаграммы небольшими (без фрагментации)
    private val MAX_DATAGRAM_SIZE = 1400
    private val MAX_PAYLOAD_SIZE = MAX_DATAGRAM_SIZE - HEADER_SIZE

    private var remoteAddress: InetAddress? = null
    private var remotePort: Int = 0
    private var socket: DatagramSocket? = null

    private var cameraThread: HandlerThread? = null
    private var cameraHandler: Handler? = null

    private var cameraDevice: CameraDevice? = null
    private var captureSession: CameraCaptureSession? = null

    private var encoder: MediaCodec? = null
    private var encoderInputSurface: android.view.Surface? = null

    private var seq: Int = 0
    private var frameId: Int = 0

    // SPS/PPS
    private var codecConfigAnnexB: ByteArray? = null
    private var sentConfigOnce = false

    fun start(host: String, port: Int, width: Int, height: Int, fps: Int, bitrate: Int) {
        remoteAddress = InetAddress.getByName(host)
        remotePort = port
        socket = DatagramSocket()

        cameraThread = HandlerThread("PhoneCam-CameraThread").also { it.start() }
        cameraHandler = Handler(cameraThread!!.looper)

        setupEncoder(width, height, fps, bitrate)
        openCamera()
    }

    fun stop() {
        try { captureSession?.close() } catch (_: Throwable) {}
        captureSession = null

        try { cameraDevice?.close() } catch (_: Throwable) {}
        cameraDevice = null

        try { encoder?.stop() } catch (_: Throwable) {}
        try { encoder?.release() } catch (_: Throwable) {}
        encoder = null

        try { encoderInputSurface?.release() } catch (_: Throwable) {}
        encoderInputSurface = null

        try { socket?.close() } catch (_: Throwable) {}
        socket = null

        cameraThread?.quitSafely()
        cameraThread = null
        cameraHandler = null

        codecConfigAnnexB = null
        sentConfigOnce = false
        seq = 0
        frameId = 0
    }

    private fun setupEncoder(width: Int, height: Int, fps: Int, bitrate: Int) {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
        }

        encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).apply {
            configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            encoderInputSurface = createInputSurface()
            start()

            setCallback(object : MediaCodec.Callback() {
                override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {}

                override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
                    val out = codec.getOutputBuffer(index) ?: run {
                        codec.releaseOutputBuffer(index, false)
                        return
                    }

                    if (info.size <= 0) {
                        codec.releaseOutputBuffer(index, false)
                        return
                    }

                    val data = ByteArray(info.size)
                    out.position(info.offset)
                    out.limit(info.offset + info.size)
                    out.get(data)
                    codec.releaseOutputBuffer(index, false)

                    if ((info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                        codecConfigAnnexB = normalizeToAnnexB(data)
                        sentConfigOnce = false
                        return
                    }

                    val isKeyFrame = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                    val cfg = codecConfigAnnexB
                    if (cfg != null && (isKeyFrame || !sentConfigOnce)) {
                        sendFrame(cfg)
                        sentConfigOnce = true
                    }

                    sendFrame(normalizeToAnnexB(data))
                }

                override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
                    val csd0 = format.getByteBuffer("csd-0")?.let { bbToByteArray(it) }
                    val csd1 = format.getByteBuffer("csd-1")?.let { bbToByteArray(it) }
                    if (csd0 != null && csd1 != null) {
                        val merged = ByteArray(csd0.size + csd1.size)
                        System.arraycopy(csd0, 0, merged, 0, csd0.size)
                        System.arraycopy(csd1, 0, merged, csd0.size, csd1.size)
                        codecConfigAnnexB = normalizeToAnnexB(merged)
                        sentConfigOnce = false
                    }
                }

                override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
                    stop()
                }
            }, cameraHandler)
        }
    }

    private fun bbToByteArray(bb: ByteBuffer): ByteArray {
        val dup = bb.duplicate()
        val arr = ByteArray(dup.remaining())
        dup.get(arr)
        return arr
    }

    private fun normalizeToAnnexB(src: ByteArray): ByteArray {
        if (src.size >= 4) {
            val isAnnexB =
                (src[0].toInt() == 0 && src[1].toInt() == 0 && src[2].toInt() == 0 && src[3].toInt() == 1) ||
                (src[0].toInt() == 0 && src[1].toInt() == 0 && src[2].toInt() == 1)
            if (isAnnexB) return src
        }

        // AVCC -> AnnexB
        return try {
            val out = ArrayList<ByteArray>()
            var i = 0
            while (i + 4 <= src.size) {
                val len =
                    ((src[i].toInt() and 0xFF) shl 24) or
                    ((src[i + 1].toInt() and 0xFF) shl 16) or
                    ((src[i + 2].toInt() and 0xFF) shl 8) or
                    (src[i + 3].toInt() and 0xFF)
                i += 4
                if (len <= 0 || i + len > src.size) break

                val nal = src.copyOfRange(i, i + len)
                i += len

                val startCode = byteArrayOf(0, 0, 0, 1)
                val chunk = ByteArray(startCode.size + nal.size)
                System.arraycopy(startCode, 0, chunk, 0, startCode.size)
                System.arraycopy(nal, 0, chunk, startCode.size, nal.size)
                out.add(chunk)
            }

            if (out.isEmpty()) src
            else {
                val total = out.sumOf { it.size }
                val merged = ByteArray(total)
                var p = 0
                for (a in out) {
                    System.arraycopy(a, 0, merged, p, a.size)
                    p += a.size
                }
                merged
            }
        } catch (_: Throwable) {
            src
        }
    }

    private fun sendFrame(frameBytes: ByteArray) {
        val addr = remoteAddress ?: return
        val port = remotePort
        val sock = socket ?: return

        val totalChunks = ceil(frameBytes.size / MAX_PAYLOAD_SIZE.toDouble()).toInt().coerceAtLeast(1)
        val thisFrameId = frameId++
        var offset = 0

        for (chunkIndex in 0 until totalChunks) {
            val remaining = frameBytes.size - offset
            val chunkLen = min(MAX_PAYLOAD_SIZE, remaining)
            val payload = frameBytes.copyOfRange(offset, offset + chunkLen)
            offset += chunkLen

            val packetBytes = ByteBuffer
                .allocate(HEADER_SIZE + payload.size)
                .order(ByteOrder.LITTLE_ENDIAN)
                .apply {
                    putInt(MAGIC_PCAM)       // 0..3
                    putLong(0L)              // 4..11 reserved
                    putInt(seq++)            // 12..15
                    putInt(thisFrameId)      // 16..19
                    putShort(chunkIndex.toShort())     // 20..21
                    putShort(totalChunks.toShort())    // 22..23
                    put(payload)
                }
                .array()

            try {
                sock.send(DatagramPacket(packetBytes, packetBytes.size, addr, port))
            } catch (_: Throwable) { }
        }
    }

    @android.annotation.SuppressLint("MissingPermission")
    private fun openCamera() {
        val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val cameraId = chooseBackCamera(manager) ?: manager.cameraIdList.firstOrNull() ?: run {
            stop(); return
        }

        manager.openCamera(cameraId, object : CameraDevice.StateCallback() {
            override fun onOpened(camera: CameraDevice) {
                cameraDevice = camera
                createSession()
            }

            override fun onDisconnected(camera: CameraDevice) {
                try { camera.close() } catch (_: Throwable) {}
                stop()
            }

            override fun onError(camera: CameraDevice, error: Int) {
                try { camera.close() } catch (_: Throwable) {}
                stop()
            }
        }, cameraHandler)
    }

    private fun chooseBackCamera(manager: CameraManager): String? {
        for (id in manager.cameraIdList) {
            val chars = manager.getCameraCharacteristics(id)
            val facing = chars.get(CameraCharacteristics.LENS_FACING)
            if (facing == CameraCharacteristics.LENS_FACING_BACK) return id
        }
        return null
    }

    private fun createSession() {
        val camera = cameraDevice ?: return
        val surface = encoderInputSurface ?: return

        val requestBuilder = camera.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
            addTarget(surface)
            set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
        }

        camera.createCaptureSession(
            listOf(surface),
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(session: CameraCaptureSession) {
                    captureSession = session
                    try {
                        session.setRepeatingRequest(requestBuilder.build(), null, cameraHandler)
                    } catch (_: Throwable) {
                        stop()
                    }
                }

                override fun onConfigureFailed(session: CameraCaptureSession) {
                    stop()
                }
            },
            cameraHandler
        )
    }
}