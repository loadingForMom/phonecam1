package com.example.phonecamandroid

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat

/**
 * Foreground-service: Camera2 → MediaCodec(H.264) → UDP (PCAM framing).
 */
class H264StreamService : Service() {

    private var streamer: UdpH264Streamer? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        startForeground(NOTIF_ID, buildNotification("Starting…"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                val host = intent.getStringExtra(EXTRA_HOST) ?: "192.168.137.1"
                val port = intent.getIntExtra(EXTRA_PORT, 39010)
                val width = intent.getIntExtra(EXTRA_WIDTH, 1280)
                val height = intent.getIntExtra(EXTRA_HEIGHT, 720)
                val fps = intent.getIntExtra(EXTRA_FPS, 30)
                val bitrate = intent.getIntExtra(EXTRA_BITRATE, 2_000_000)

                stopStreamerIfAny()

                // IMPORTANT: UdpH264Streamer принимает параметры в start(...)
                streamer = UdpH264Streamer(this).also {
                    it.start(
                        host = host,
                        port = port,
                        width = width,
                        height = height,
                        fps = fps,
                        bitrate = bitrate
                    )
                }

                val nm = getSystemService(NotificationManager::class.java)
                nm.notify(NOTIF_ID, buildNotification("Streaming → $host:$port"))
            }

            ACTION_STOP -> {
                stopStreamerIfAny()
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }

        return START_STICKY
    }

    override fun onDestroy() {
        stopStreamerIfAny()
        super.onDestroy()
    }

    private fun stopStreamerIfAny() {
        streamer?.stop()
        streamer = null
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
