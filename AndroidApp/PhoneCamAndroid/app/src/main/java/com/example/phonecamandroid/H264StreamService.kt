package com.example.phonecamandroid

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.LinkAddress
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Binder
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.concurrent.thread
import kotlin.math.max

class H264StreamService : Service() {

    private var streamer: UdpH264Streamer? = null
    private var previewSurface: android.view.Surface? = null

    private var wakeLock: PowerManager.WakeLock? = null

    // Keep process pinned to a specific Wi-Fi network (hotspot) while streaming
    private var boundNetwork: Network? = null

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
        val port = intent.getIntExtra(EXTRA_PORT, 39010) // UDP port (display)
        val width = intent.getIntExtra(EXTRA_WIDTH, 1280)
        val height = intent.getIntExtra(EXTRA_HEIGHT, 720)
        val fps = intent.getIntExtra(EXTRA_FPS, 30)
        val bitrate = intent.getIntExtra(EXTRA_BITRATE, 2_000_000)
        val controlPort = intent.getIntExtra(EXTRA_TCP_PORT, 39000)

        stopStreamerIfAny()

        StreamState.updateStats(StreamStats(connectionState = "Starting", remote = "$host:$port"))

        acquireWakeLock()
        runSuCheck()

        val worker = thread(start = true, name = "PhoneCam-Control") {
            try {
                // 1) Force routing via the correct Wi-Fi network (hotspot), otherwise Android may pick mobile data
                notifyStatus("Binding network…")
                val pinned = ensureBoundToBestWifiNetworkForHost(host, controlPort, timeoutMs = 10_000)
                if (!pinned) {
                    failAndStop("Failed: no Wi-Fi route to $host", host, port)
                    return@thread
                }

                // 2) Robust negotiation with retries (handles transient ENETUNREACH during Wi-Fi transitions)
                notifyStatus("Connecting…")
                val negotiated = negotiateWithRetries(
                    host = host,
                    controlPort = controlPort,
                    width = width,
                    height = height,
                    fps = fps,
                    bitrate = bitrate,
                    attempts = 6,
                    baseDelayMs = 350
                )

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

    private fun negotiateWithRetries(
        host: String,
        controlPort: Int,
        width: Int,
        height: Int,
        fps: Int,
        bitrate: Int,
        attempts: Int,
        baseDelayMs: Long
    ): ControlNegotiation? {
        var lastErr: Throwable? = null

        for (i in 1..attempts) {
            // Re-pin network each attempt (covers cases when OS rebinds due to Wi-Fi switching)
            ensureBoundToBestWifiNetworkForHost(host, controlPort, timeoutMs = 2_500)

            try {
                StreamState.log("Control connect attempt $i/$attempts → $host:$controlPort")

                val negotiated = TcpControlClient.negotiate(host, controlPort, width, height, fps, bitrate)
                if (negotiated == null) {
                    // Treat "null" as a failed attempt too (protocol mismatch / readLine null etc.)
                    throw IllegalStateException("Negotiate returned null")
                }

                StreamState.log("Control negotiated: udpPort=${negotiated.udpPort}")
                return negotiated
            } catch (t: Throwable) {
                lastErr = t
                logException("Control connect failed (attempt $i/$attempts)", t)

                // Backoff: 350ms, 700ms, 1050ms, ...
                val sleepMs = baseDelayMs * i
                try { Thread.sleep(sleepMs) } catch (_: Throwable) {}
            }
        }

        if (lastErr != null) {
            logException("Control failed after $attempts attempts", lastErr)
        }
        return null
    }

    private fun failAndStop(msg: String, host: String, port: Int) {
        StreamState.updateStats(StreamStats(connectionState = msg, remote = "$host:$port"))
        stopStreamerIfAny()
        releaseWakeLock()
        unbindProcessNetwork()
        stopForeground(STOP_FOREGROUND_REMOVE)
        notifyStatus(msg)
        stopSelf()
    }

    private fun stopStreaming(state: String) {
        stopStreamerIfAny()
        StreamState.updateStats(StreamStats(connectionState = state))
        releaseWakeLock()
        unbindProcessNetwork()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        stopStreamerIfAny()
        releaseWakeLock()
        unbindProcessNetwork()
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
            wl.acquire(60 * 60 * 1000L) // 1 hour
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
        StreamState.log("WakeLock released")
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

    /**
     * Ensure traffic goes via the Wi-Fi network that actually has a route to [host].
     * This prevents Android from trying to connect via mobile data / other Wi-Fi and producing ENETUNREACH.
     */
    private fun ensureBoundToBestWifiNetworkForHost(host: String, controlPort: Int, timeoutMs: Long): Boolean {
        val cm = getSystemService(ConnectivityManager::class.java) ?: return false
        val deadline = System.currentTimeMillis() + max(0L, timeoutMs)

        val hostAddr: InetAddress? = try { InetAddress.getByName(host) } catch (_: Throwable) { null }
        logWifiDiagnostics(cm, hostAddr, host, controlPort)

        val immediate = findWifiNetworkForHost(cm, hostAddr, host, controlPort)
        if (immediate != null) {
            if (boundNetwork != immediate) {
                if (bindProcessNetwork(cm, immediate)) {
                    boundNetwork = immediate
                    StreamState.log("Network bound to Wi-Fi: $immediate")
                } else {
                    StreamState.log("Failed to bind process to Wi-Fi network")
                }
            }
            return true
        }

        while (System.currentTimeMillis() <= deadline) {
            val wifi = findWifiNetworkForHost(cm, hostAddr, host, controlPort)
            if (wifi != null) {
                if (boundNetwork != wifi) {
                    if (bindProcessNetwork(cm, wifi)) {
                        boundNetwork = wifi
                        StreamState.log("Network bound to Wi-Fi: $wifi")
                    } else {
                        StreamState.log("Failed to bind process to Wi-Fi network")
                        // continue retry
                    }
                }
                return true
            }

            // no suitable Wi-Fi network yet (Wi-Fi connecting / hotspot switching)
            try { Thread.sleep(200) } catch (_: Throwable) {}
        }

        StreamState.log("No suitable Wi-Fi network found for host=$host within ${timeoutMs}ms")
        return false
    }

    private fun findWifiNetworkForHost(
        cm: ConnectivityManager,
        hostAddr: InetAddress?,
        host: String,
        port: Int
    ): Network? {
        val nets = try { cm.allNetworks } catch (_: Throwable) { emptyArray<Network>() }
        if (nets.isEmpty()) return null

        // Prefer Wi-Fi networks that are on the same subnet as the host (best for hotspot 192.168.137.x)
        val preferred = nets.firstOrNull { n ->
            val caps = cm.getNetworkCapabilities(n) ?: return@firstOrNull false
            if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return@firstOrNull false
            val lp = cm.getLinkProperties(n) ?: return@firstOrNull false
            hasIpv4(lp) && (isHostOnSameSubnet(hostAddr, lp) || hasRouteToHost(hostAddr, lp))
        }
        if (preferred != null) return preferred

        // Fallback: any Wi-Fi network that is not suspended
        val anyWifi = nets.firstOrNull { n ->
            val caps = cm.getNetworkCapabilities(n) ?: return@firstOrNull false
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) &&
                    caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED)
        }
        if (anyWifi != null) {
            val lp = cm.getLinkProperties(anyWifi)
            if (lp != null && hasIpv4(lp)) {
                val probe = quickProbe(anyWifi, host, port)
                StreamState.log("Probe hint: $host:$port result=$probe")
                return anyWifi
            }
        }

        // Last resort: active network if it is Wi-Fi
        val active = cm.activeNetwork ?: return null
        val caps = cm.getNetworkCapabilities(active) ?: return null
        return if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) active else null
    }

    private fun isHostOnSameSubnet(hostAddr: InetAddress?, lp: LinkProperties): Boolean {
        val host4 = hostAddr as? Inet4Address ?: return false
        val hostInt = ipv4ToInt(host4)

        for (la: LinkAddress in lp.linkAddresses) {
            val addr4 = la.address as? Inet4Address ?: continue
            val prefix = la.prefixLength
            if (prefix <= 0 || prefix > 32) continue
            val mask = prefixToMask(prefix)
            val netA = ipv4ToInt(addr4) and mask
            val netH = hostInt and mask
            if (netA == netH) return true
        }
        return false
    }

    private fun hasIpv4(lp: LinkProperties): Boolean {
        return lp.linkAddresses.any { it.address is Inet4Address }
    }

    private fun hasRouteToHost(hostAddr: InetAddress?, lp: LinkProperties): Boolean {
        if (hostAddr == null) return false
        return lp.routes.any { route ->
            route.destination?.contains(hostAddr) == true
        }
    }

    private enum class ProbeResult { Success, Refused, Timeout, Error }

    private fun quickProbe(network: Network, host: String, port: Int): ProbeResult {
        return try {
            val socket = network.socketFactory.createSocket()
            socket.use { it.connect(InetSocketAddress(host, port), 1500) }
            StreamState.log("Probe: $host:$port success via $network")
            ProbeResult.Success
        } catch (e: java.net.ConnectException) {
            StreamState.log("Probe: $host:$port refused via $network")
            ProbeResult.Refused
        } catch (e: java.net.SocketTimeoutException) {
            StreamState.log("Probe: $host:$port timeout via $network")
            ProbeResult.Timeout
        } catch (e: Throwable) {
            StreamState.log("Probe: $host:$port error ${e.message}")
            ProbeResult.Error
        }
    }

    private fun logWifiDiagnostics(cm: ConnectivityManager, hostAddr: InetAddress?, host: String, controlPort: Int) {
        try {
            val active = cm.activeNetwork
            val caps = active?.let { cm.getNetworkCapabilities(it) }
            val lp = active?.let { cm.getLinkProperties(it) }
            val transport = when {
                caps == null -> "none"
                caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "WIFI"
                caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "CELLULAR"
                caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ETHERNET"
                else -> "OTHER"
            }

            val wifiManager = applicationContext.getSystemService(WifiManager::class.java)
            val ssid = try { wifiManager?.connectionInfo?.ssid ?: "-" } catch (_: Throwable) { "-" }

            val localIp = lp?.linkAddresses?.firstOrNull { it.address is Inet4Address }?.address?.hostAddress ?: "-"
            val onSubnet = lp?.let { isHostOnSameSubnet(hostAddr, it) } == true
            StreamState.log("Wi-Fi diag: active=$transport ssid=$ssid localIp=$localIp host=$host sameSubnet=$onSubnet")
            if (active != null && caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true) {
                quickProbe(active, host, controlPort)
            }
        } catch (t: Throwable) {
            StreamState.log("Wi-Fi diag failed: ${t.message}")
        }
    }

    private fun ipv4ToInt(a: Inet4Address): Int {
        val b = a.address
        return ((b[0].toInt() and 0xFF) shl 24) or
                ((b[1].toInt() and 0xFF) shl 16) or
                ((b[2].toInt() and 0xFF) shl 8) or
                (b[3].toInt() and 0xFF)
    }

    private fun prefixToMask(prefix: Int): Int {
        return if (prefix == 0) 0 else (-1 shl (32 - prefix))
    }

    private fun bindProcessNetwork(cm: ConnectivityManager, network: Network): Boolean {
        return try {
            when {
                Build.VERSION.SDK_INT >= 23 -> cm.bindProcessToNetwork(network)
                Build.VERSION.SDK_INT >= 21 -> ConnectivityManager.setProcessDefaultNetwork(network)
                else -> true
            }
        } catch (t: Throwable) {
            StreamState.log("bindProcessNetwork failed: ${t.message}")
            false
        }
    }

    private fun unbindProcessNetwork() {
        val cm = try { getSystemService(ConnectivityManager::class.java) } catch (_: Throwable) { null }
        try {
            if (cm != null) {
                when {
                    Build.VERSION.SDK_INT >= 23 -> cm.bindProcessToNetwork(null)
                    Build.VERSION.SDK_INT >= 21 -> ConnectivityManager.setProcessDefaultNetwork(null)
                }
            }
        } catch (_: Throwable) {
        }
        boundNetwork = null
    }

    companion object {
        const val ACTION_START = "com.example.phonecamandroid.action.START"
        const val ACTION_STOP = "com.example.phonecamandroid.action.STOP"

        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_TCP_PORT = "tcpPort"
        const val EXTRA_WIDTH = "w"
        const val EXTRA_HEIGHT = "h"
        const val EXTRA_FPS = "fps"
        const val EXTRA_BITRATE = "bitrate"

        private const val CHANNEL_ID = "phonecam_stream"
        private const val NOTIF_ID = 101
    }
}
