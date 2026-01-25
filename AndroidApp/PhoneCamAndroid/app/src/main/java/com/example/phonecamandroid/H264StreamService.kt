package com.example.phonecamandroid

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Binder
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import kotlin.concurrent.thread

class H264StreamService : Service() {

    private var streamer: UdpH264Streamer? = null
    private var previewSurface: android.view.Surface? = null

    private var wakeLock: PowerManager.WakeLock? = null

    inner class LocalBinder : Binder() {
        val service: H264StreamService
            get() = this@H264StreamService
    }

    override fun onBind(intent: Intent?): IBinder = LocalBinder()

    override fun onCreate() {
        super.onCreate()
        StreamState.log("Service created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> startStreaming(intent)
            ACTION_STOP -> stopStreaming("Stopped")
        }
        return START_STICKY
    }

    private fun startStreaming(intent: Intent) {
        // foreground notification
        startFgSafe(buildNotification("Starting…"))

        val host = intent.getStringExtra(EXTRA_HOST) ?: "192.168.137.1"
        val port = intent.getIntExtra(EXTRA_PORT, 39010)
        val width = intent.getIntExtra(EXTRA_WIDTH, 1280)
        val height = intent.getIntExtra(EXTRA_HEIGHT, 720)
        val fps = intent.getIntExtra(EXTRA_FPS, 30)
        val bitrate = intent.getIntExtra(EXTRA_BITRATE, 2_000_000)

        stopStreamerIfAny()

        StreamState.updateStats(StreamStats(connectionState = "Starting", remote = "$host:$port"))

        acquireWakeLock()

        // (опционально) проверка root — НЕ обязательна для камеры/кодека,
        // но ты хотел видеть что root реально есть
        runSuCheck()

        val worker = thread(start = true, name = "PhoneCam-Control") {
            try {
                val negotiated = try {
                    TcpControlClient.negotiate(host, 39000, width, height, fps, bitrate)
                } catch (ex: Throwable) {
                    logException("Control failed to connect", ex)
                    null
                }

                if (negotiated == null) {
                    failAndStop("Control failed", host, port)
                    return@thread
                }

                val udpPort = negotiated.udpPort

                streamer = UdpH264Streamer(this).also { st ->
                    st.setPreviewSurface(previewSurface)
                    st.onLog = { StreamState.log(it) }
                    st.onStats = { stats ->
                        StreamState.updateStats(stats)

                        val bad =
                            stats.connectionState.startsWith("Failed", ignoreCase = true) ||
                                    stats.connectionState.startsWith("Camera", ignoreCase = true) ||
                                    stats.connectionState.contains("error", ignoreCase = true)

                        if (bad) {
                            failAndStop("Streaming failed: ${stats.connectionState}", host, udpPort)
                        }
                    }
                }

                val started = streamer?.start(host, udpPort, width, height, fps, bitrate) == true
                if (!started) {
                    failAndStop("Stream init failed", host, udpPort)
                    return@thread
                }

                notifyStatus("Streaming → $host:$udpPort")
            } catch (ex: Throwable) {
                logException("Streaming start failed", ex)
                failAndStop("Streaming failed", host, port)
            }
        }

        worker.uncaughtExceptionHandler = Thread.UncaughtExceptionHandler { _, ex ->
            logException("Control thread crashed", ex)
            failAndStop("Streaming failed", host, port)
        }
    }

    private fun failAndStop(msg: String, host: String, port: Int) {
        StreamState.updateStats(StreamStats(connectionState = msg, remote = "$host:$port"))
        stopStreamerIfAny()
        releaseWakeLock()
        stopForeground(STOP_FOREGROUND_REMOVE)
        notifyStatus(msg)
        stopSelf()
    }

    private fun stopStreaming(state: String) {
        stopStreamerIfAny()
        StreamState.updateStats(StreamStats(connectionState = state))
        releaseWakeLock()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        stopStreamerIfAny()
        releaseWakeLock()
        StreamState.log("Service destroyed")
        super.onDestroy()
    }

    private fun stopStreamerIfAny() {
        try { streamer?.stop() } catch (_: Throwable) {}
        streamer = null
    }

    private fun acquireWakeLock() {
        try {
            val pm = getSystemService(PowerManager::class.java)
            val wl = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PhoneCam:stream")
            wl.setReferenceCounted(false)
            wl.acquire(60 * 60 * 1000L) // 1 час, потом можно продлевать
            wakeLock = wl
            StreamState.log("WakeLock acquired")
        } catch (t: Throwable) {
            StreamState.log("WakeLock failed: ${t.message}")
        }
    }

    private fun releaseWakeLock() {
        try {
            wakeLock?.let { if (it.isHeld) it.release() }
        } catch (_: Throwable) { }
        wakeLock = null
    }

    private fun runSuCheck() {
        thread(name = "PhoneCam-RootCheck") {
            try {
                val p = Runtime.getRuntime().exec(arrayOf("su", "-c", "id"))
                val out = p.inputStream.bufferedReader().readText().trim()
                val err = p.errorStream.bufferedReader().readText().trim()
                val code = p.waitFor()
                StreamState.log("su -c id => code=$code out=$out err=$err")
            } catch (t: Throwable) {
                StreamState.log("su check failed: ${t.message}")
            }
        }
    }

    private fun logException(prefix: String, ex: Throwable) {
        val stack = Log.getStackTraceString(ex)
        StreamState.log("$prefix: $stack")
    }

    fun setPreviewSurface(surface: android.view.Surface?) {
        previewSurface = surface
        streamer?.setPreviewSurface(surface)
    }

    private fun startFgSafe(notification: Notification) {
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(
                NOTIF_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            )
        } else {
            startForeground(NOTIF_ID, notification)
        }
    }

    private fun notifyStatus(text: String) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.notify(NOTIF_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26) {
            val ch = NotificationChannel(
                CHANNEL_ID,
                "PhoneCam streaming",
                NotificationManager.IMPORTANCE_LOW
            )
            nm.createNotificationChannel(ch)
        }

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.presence_video_online)
            .setContentTitle("PhoneCam")
            .setContentText(text)
            .setOngoing(true)
            .build()
    }

    companion object {
        const val ACTION_START = "com.example.phonecamandroid.action.START"
        const val ACTION_STOP = "com.example.phonecamandroid.action.STOP"

        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_WIDTH = "w"
        const val EXTRA_HEIGHT = "h"
        const val EXTRA_FPS = "fps"
        const val EXTRA_BITRATE = "bitrate"

        private const val CHANNEL_ID = "phonecam_stream"
        private const val NOTIF_ID = 101
    }
}