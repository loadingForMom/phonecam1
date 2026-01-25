package com.example.phonecamandroid

import android.annotation.SuppressLint
import android.content.Context
import android.hardware.camera2.*
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.util.Range
import android.view.Surface
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Minimal Camera2 wrapper: outputs frames to encoder surface (+ optional preview surface).
 * All camera work happens on [handler]'s looper.
 */
class Camera2Controller(
    private val ctx: Context,
    private val handler: Handler
) {
    var onLog: ((String) -> Unit)? = null

    private val cameraManager = ctx.getSystemService(Context.CAMERA_SERVICE) as CameraManager

    private var cameraId: String? = null
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null

    private var encoderSurface: Surface? = null
    private var previewSurface: Surface? = null

    private var width: Int = 0
    private var height: Int = 0
    private var fps: Int = 30

    fun setPreviewSurface(surface: Surface?) {
        previewSurface = surface
        // if already running — restart session with new targets
        runOnHandler {
            if (device != null && encoderSurface != null) {
                restartSession()
            }
        }
    }

    @SuppressLint("MissingPermission")
    fun start(width: Int, height: Int, fps: Int, encoderSurface: Surface) {
        this.width = width
        this.height = height
        this.fps = fps
        this.encoderSurface = encoderSurface

        val latch = CountDownLatch(1)
        var error: Throwable? = null

        runOnHandler {
            try {
                cameraId = chooseCameraId()
                    ?: throw IllegalStateException("No camera found")

                val id = cameraId!!

                log("Opening camera: $id")

                cameraManager.openCamera(
                    id,
                    object : CameraDevice.StateCallback() {
                        override fun onOpened(camera: CameraDevice) {
                            device = camera
                            try {
                                createSession()
                                latch.countDown()
                            } catch (t: Throwable) {
                                error = t
                                latch.countDown()
                            }
                        }

                        override fun onDisconnected(camera: CameraDevice) {
                            log("Camera disconnected")
                            safeClose()
                            latch.countDown()
                        }

                        override fun onError(camera: CameraDevice, err: Int) {
                            error = RuntimeException("Camera error=$err")
                            log("Camera error=$err")
                            safeClose()
                            latch.countDown()
                        }
                    },
                    handler
                )
            } catch (t: Throwable) {
                error = t
                latch.countDown()
            }
        }

        // Wait a bit so start() can fail fast if camera is dead/unavailable
        latch.await(4, TimeUnit.SECONDS)
        error?.let { throw it }
    }

    fun stop() {
        runOnHandler {
            try {
                session?.stopRepeating()
            } catch (_: Throwable) { }
            try {
                session?.abortCaptures()
            } catch (_: Throwable) { }

            safeClose()
        }
    }

    private fun restartSession() {
        try {
            session?.stopRepeating()
        } catch (_: Throwable) { }
        try {
            session?.close()
        } catch (_: Throwable) { }
        session = null

        createSession()
    }

    private fun createSession() {
        val cam = device ?: throw IllegalStateException("CameraDevice is null")
        val enc = encoderSurface ?: throw IllegalStateException("Encoder surface is null")

        val targets = ArrayList<Surface>(2)
        targets.add(enc)
        previewSurface?.let { targets.add(it) }

        cam.createCaptureSession(
            targets,
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    session = s
                    try {
                        val req = cam.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                            addTarget(enc)
                            previewSurface?.let { addTarget(it) }

                            set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
                            set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)

                            pickFpsRange()?.let { range ->
                                set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, range)
                            }
                        }

                        s.setRepeatingRequest(req.build(), null, handler)
                        log("Camera session configured ($width x $height @ $fps)")
                    } catch (t: Throwable) {
                        log("Camera session start failed: ${Log.getStackTraceString(t)}")
                        safeClose()
                    }
                }

                override fun onConfigureFailed(s: CameraCaptureSession) {
                    log("Camera session configure failed")
                    safeClose()
                }
            },
            handler
        )
    }

    private fun pickFpsRange(): Range<Int>? {
        val id = cameraId ?: return null
        val chars = cameraManager.getCameraCharacteristics(id)
        val ranges = chars.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES) ?: return null

        // Prefer ranges containing desired fps, closest narrow range first
        val containing = ranges.filter { it.lower <= fps && fps <= it.upper }
        if (containing.isNotEmpty()) {
            return containing.minByOrNull { (it.upper - it.lower) }
        }

        // Otherwise pick closest upper bound
        return ranges.minByOrNull { kotlin.math.abs(it.upper - fps) }
    }

    private fun chooseCameraId(): String? {
        val ids = cameraManager.cameraIdList.toList()
        if (ids.isEmpty()) return null

        // Prefer back camera
        for (id in ids) {
            val chars = cameraManager.getCameraCharacteristics(id)
            val facing = chars.get(CameraCharacteristics.LENS_FACING)
            if (facing == CameraCharacteristics.LENS_FACING_BACK) return id
        }
        return ids.first()
    }

    private fun safeClose() {
        try { session?.close() } catch (_: Throwable) { }
        session = null

        try { device?.close() } catch (_: Throwable) { }
        device = null
    }

    private fun runOnHandler(block: () -> Unit) {
        if (Looper.myLooper() == handler.looper) block() else handler.post(block)
    }

    private fun log(msg: String) {
        onLog?.invoke(msg)
        Log.d("PhoneCam", msg)
    }
}