package com.example.phonecamandroid

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattServer
import android.bluetooth.BluetoothGattServerCallback
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.BluetoothLeAdvertiser
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.LinkAddress
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiConfiguration
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.os.Build
import android.os.ParcelUuid
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.util.UUID

class BleHandshakeManager(private val context: Context) {
    companion object {
        val SERVICE_UUID: UUID = UUID.fromString("0000feed-0000-1000-8000-00805f9b34fb")
        val HANDSHAKE_CHAR_UUID: UUID = UUID.fromString("0000feed-0001-1000-8000-00805f9b34fb")
        private const val CREDENTIALS_SEPARATOR = "|"
        private const val DEFAULT_HOST = "192.168.137.1"
        private const val DEFAULT_TCP = 39000
        private const val DEFAULT_UDP = 39010
    }

    private val bluetoothManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val adapter: BluetoothAdapter? = bluetoothManager.adapter
    private val advertiser: BluetoothLeAdvertiser? = adapter?.bluetoothLeAdvertiser

    private var gattServer: BluetoothGattServer? = null
    private var advertising = false
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var lastAutoStartNonce: String? = null

    fun isAdvertising(): Boolean = advertising

    @SuppressLint("MissingPermission")
    fun startHandshake() {
        if (advertising) return
        if (adapter == null || !adapter.isEnabled) {
            StreamState.log("BLE: adapter unavailable or disabled")
            return
        }
        if (advertiser == null) {
            StreamState.log("BLE: advertiser unavailable")
            return
        }

        val service = BluetoothGattService(SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)
        val credentialChar = BluetoothGattCharacteristic(
            HANDSHAKE_CHAR_UUID,
            BluetoothGattCharacteristic.PROPERTY_WRITE,
            BluetoothGattCharacteristic.PERMISSION_WRITE
        )
        service.addCharacteristic(credentialChar)

        gattServer = bluetoothManager.openGattServer(context, gattServerCallback)
        if (gattServer == null) {
            StreamState.log("BLE: failed to open GATT server")
            return
        }
        gattServer?.addService(service)

        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setConnectable(true)
            .setTimeout(0)
            .build()

        val data = AdvertiseData.Builder()
            .addServiceUuid(ParcelUuid(SERVICE_UUID))
            .setIncludeDeviceName(false)
            .build()

        advertiser.startAdvertising(settings, data, advertiseCallback)
        advertising = true
        StreamState.log("BLE: advertising started")
    }

    @SuppressLint("MissingPermission")
    fun stopHandshake() {
        if (!advertising) return
        try {
            advertiser?.stopAdvertising(advertiseCallback)
        } catch (_: Throwable) {
        }

        // Безопасно снимаем коллбек сети
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        networkCallback?.let { cb ->
            try {
                cm.unregisterNetworkCallback(cb)
            } catch (_: Throwable) {
            }
        }
        networkCallback = null

        try {
            gattServer?.close()
        } catch (_: Throwable) {
        } finally {
            gattServer = null
        }

        advertising = false
        StreamState.log("BLE: advertising stopped")
    }

    private val advertiseCallback = object : AdvertiseCallback() {}

    private val gattServerCallback = object : BluetoothGattServerCallback() {
        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            val state = when (newState) {
                BluetoothProfile.STATE_CONNECTED -> "connected"
                BluetoothProfile.STATE_DISCONNECTED -> "disconnected"
                else -> "state=$newState"
            }
            StreamState.log("BLE: device ${device.address} $state")
        }

        override fun onCharacteristicWriteRequest(
            device: BluetoothDevice,
            requestId: Int,
            characteristic: BluetoothGattCharacteristic,
            preparedWrite: Boolean,
            responseNeeded: Boolean,
            offset: Int,
            value: ByteArray
        ) {
            if (characteristic.uuid != HANDSHAKE_CHAR_UUID) return

            val payload = String(value, StandardCharsets.UTF_8)
            StreamState.log("BLE: credentials received: $payload")
            if (responseNeeded) {
                gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, offset, null)
            }

            val parsed = parsePayload(payload)
            if (parsed == null) {
                StreamState.log("BLE: invalid credential payload")
                return
            }

            StreamState.log(
                "BLE: parsed ssid=${parsed.ssid} host=${parsed.host} tcp=${parsed.tcpPort} udp=${parsed.udpPort} " +
                        "autostart=${parsed.autostart} nonce=${parsed.nonce ?: "-"}"
            )

            Thread {
                try {
                    connectToWifi(parsed)
                } catch (t: Throwable) {
                    // Чтобы никакой фоновой поток не убивал процесс
                    StreamState.log("BLE: connectToWifi crashed: ${t.javaClass.simpleName}: ${t.message}")
                }
            }.start()
        }
    }

    private fun connectToWifi(payload: HandshakePayload) {
        val ssid = payload.ssid
        val password = payload.psk
        val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager

        @Suppress("DEPRECATION")
        if (!wifiManager.isWifiEnabled) {
            val enabled = try {
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
                    wifiManager.isWifiEnabled = true
                    true
                } else {
                    false // Need to use settings panel
                }
            } catch (t: Throwable) {
                StreamState.log("Wi-Fi: setWifiEnabled failed: ${t.javaClass.simpleName}: ${t.message}")
                false
            }
            StreamState.log("Wi-Fi: enable requested result=$enabled")
        }

        // Если уже на подходящем Wi-Fi — биндимся и автозапускаем без requestNetwork
        if (payload.autostart && isWifiAlreadySuitable(cm, payload.host, payload.tcpPort)) {
            StreamState.log("Wi-Fi: already suitable; ensuring bind + autostart")
            bindToActiveWifiIfPossible(cm)
            startStreamingIfNeeded(payload)
            return
        }

        // Legacy (до Android 10): можно через WifiConfiguration
        @Suppress("DEPRECATION")
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            val config = WifiConfiguration().apply {
                this.SSID = "\"$ssid\""
                this.preSharedKey = "\"$password\""
            }

            wifiManager.configuredNetworks?.firstOrNull { it.SSID == "\"$ssid\"" }?.let {
                wifiManager.removeNetwork(it.networkId)
            }

            val netId = wifiManager.addNetwork(config)
            if (netId == -1) {
                StreamState.log("Wi-Fi: failed to add network for $ssid")
                return
            }

            wifiManager.disconnect()
            wifiManager.enableNetwork(netId, true)
            wifiManager.reconnect()
            StreamState.log("Wi-Fi: connecting (legacy) to $ssid")
            scheduleLegacyAutostart(payload)
            return
        }

        // Android 10+ : WifiNetworkSpecifier + requestNetwork
        // Но requestNetwork может требовать прав/политик -> ловим SecurityException и НЕ падаем
        networkCallback?.let { cb ->
            try {
                cm.unregisterNetworkCallback(cb)
            } catch (_: Throwable) {
            }
        }
        networkCallback = null

        val specifier = WifiNetworkSpecifier.Builder()
            .setSsid(ssid)
            .setWpa2Passphrase(password)
            .build()

        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .setNetworkSpecifier(specifier)
            .build()

        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                try {
                    cm.bindProcessToNetwork(network)
                    StreamState.log("Wi-Fi: connected/bound to $ssid")
                } catch (t: Throwable) {
                    StreamState.log("Wi-Fi: bindProcessToNetwork failed: ${t.javaClass.simpleName}: ${t.message}")
                }

                if (payload.autostart) {
                    startStreamingIfNeeded(payload)
                }
            }

            override fun onUnavailable() {
                StreamState.log("Wi-Fi: requestNetwork unavailable for $ssid (no UI approval / policy)")
                // fallback: пробуем работать с текущим Wi-Fi если он уже есть
                if (payload.autostart) {
                    bindToActiveWifiIfPossible(cm)
                    if (isWifiAlreadySuitable(cm, payload.host, payload.tcpPort)) {
                        startStreamingIfNeeded(payload)
                    }
                }
            }
        }
        networkCallback = callback

        try {
            cm.requestNetwork(request, callback)
            StreamState.log("Wi-Fi: request sent for $ssid")
        } catch (se: SecurityException) {
            // ✅ вот тут и был твой краш
            StreamState.log("Wi-Fi: requestNetwork SecurityException: ${se.message}")
            // fallback: не умираем, а пытаемся работать с текущим Wi-Fi
            if (payload.autostart) {
                bindToActiveWifiIfPossible(cm)
                if (isWifiAlreadySuitable(cm, payload.host, payload.tcpPort)) {
                    startStreamingIfNeeded(payload)
                } else {
                    StreamState.log("Wi-Fi: fallback skipped (not suitable yet)")
                }
            }
        } catch (t: Throwable) {
            StreamState.log("Wi-Fi: requestNetwork failed: ${t.javaClass.simpleName}: ${t.message}")
        }
    }

    /**
     * Мягкий bind к текущему активному Wi-Fi без requestNetwork.
     * Это безопасно и не требует WRITE_SETTINGS.
     */
    private fun bindToActiveWifiIfPossible(cm: ConnectivityManager) {
        try {
            val active = cm.activeNetwork ?: return
            val caps = cm.getNetworkCapabilities(active) ?: return
            if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return
            cm.bindProcessToNetwork(active)
            StreamState.log("Wi-Fi: bound to active Wi-Fi")
        } catch (t: Throwable) {
            StreamState.log("Wi-Fi: bindToActiveWifi failed: ${t.javaClass.simpleName}: ${t.message}")
        }
    }

    private fun scheduleLegacyAutostart(payload: HandshakePayload) {
        if (!payload.autostart) return
        Thread {
            try {
                Thread.sleep(1200)
            } catch (_: Throwable) { }
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            if (isWifiAlreadySuitable(cm, payload.host, payload.tcpPort)) {
                bindToActiveWifiIfPossible(cm)
                startStreamingIfNeeded(payload)
            } else {
                StreamState.log("Wi-Fi: legacy autostart skipped (not yet connected)")
            }
        }.start()
    }

    private fun startStreamingIfNeeded(payload: HandshakePayload) {
        val nonce = payload.nonce
        if (nonce != null && nonce == lastAutoStartNonce) {
            StreamState.log("Autostart: already handled nonce=$nonce")
            return
        }

        val state = StreamState.stats.value.connectionState
        val idleStates = setOf("Idle", "Stopped", "Failed", "Control failed", "Stream init failed")
        if (state !in idleStates) {
            StreamState.log("Autostart: skipped, state=$state")
            return
        }

        lastAutoStartNonce = nonce
        val intent = Intent(context, H264StreamService::class.java).apply {
            action = H264StreamService.ACTION_START
            putExtra(H264StreamService.EXTRA_HOST, payload.host)
            putExtra(H264StreamService.EXTRA_PORT, payload.udpPort)
            putExtra(H264StreamService.EXTRA_TCP_PORT, payload.tcpPort)
            // 4:3 "full sensor" look (most phones are native 4:3) while keeping ~720p height.
            // 960x720 is 4:3 and encoder-friendly (multiples of 16).
            putExtra(H264StreamService.EXTRA_WIDTH, 960)
            putExtra(H264StreamService.EXTRA_HEIGHT, 720)
            putExtra(H264StreamService.EXTRA_FPS, 30)
            putExtra(H264StreamService.EXTRA_BITRATE, 2_000_000)
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
            StreamState.log("Autostart: requested streaming → ${payload.host}:${payload.udpPort}")
        } catch (t: Throwable) {
            StreamState.log("Autostart: start service failed: ${t.javaClass.simpleName}: ${t.message}")
        }
    }

    private fun isWifiAlreadySuitable(cm: ConnectivityManager, host: String, port: Int): Boolean {
        val active = cm.activeNetwork ?: return false
        val caps = try { cm.getNetworkCapabilities(active) } catch (_: Throwable) { null } ?: return false
        if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return false
        val lp = try { cm.getLinkProperties(active) } catch (_: Throwable) { null } ?: return false
        val hostAddr = try { InetAddress.getByName(host) } catch (_: Throwable) { null }

        if (!hasIpv4(lp)) return false
        val onSubnet = isHostOnSameSubnet(hostAddr, lp)
        val hasRoute = hasRouteToHost(hostAddr, lp)
        val probe = quickProbe(host, port, timeoutMs = 1500)
        StreamState.log("Wi-Fi: suitability wifi ipv4=${hasIpv4(lp)} subnet=$onSubnet route=$hasRoute probe=$probe")
        return onSubnet || hasRoute
    }

    private enum class ProbeResult { Success, Refused, Timeout, Error }

    private fun quickProbe(host: String, port: Int, timeoutMs: Int): ProbeResult {
        return try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, port), timeoutMs)
                ProbeResult.Success
            }
        } catch (e: java.net.ConnectException) {
            ProbeResult.Refused
        } catch (e: java.net.SocketTimeoutException) {
            ProbeResult.Timeout
        } catch (_: Throwable) {
            ProbeResult.Error
        }
    }

    private fun hasIpv4(lp: android.net.LinkProperties): Boolean {
        return lp.linkAddresses.any { it.address is Inet4Address }
    }

    private fun isHostOnSameSubnet(hostAddr: InetAddress?, lp: android.net.LinkProperties): Boolean {
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

    private fun hasRouteToHost(hostAddr: InetAddress?, lp: android.net.LinkProperties): Boolean {
        if (hostAddr == null) return false
        return lp.routes.any { route ->
            route.destination?.contains(hostAddr) == true
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

    private data class HandshakePayload(
        val ssid: String,
        val psk: String,
        val host: String,
        val tcpPort: Int,
        val udpPort: Int,
        val autostart: Boolean,
        val nonce: String?
    )

    private fun parsePayload(payload: String): HandshakePayload? {
        val trimmed = payload.trim()
        if (trimmed.contains("=")) {
            val parts = trimmed.split(';')
            val map = mutableMapOf<String, String>()
            for (part in parts) {
                val token = part.trim()
                if (token.isEmpty() || token.equals("pcam", ignoreCase = true)) continue
                val idx = token.indexOf('=')
                if (idx <= 0) continue
                val key = token.substring(0, idx).lowercase()
                val value = token.substring(idx + 1)
                map[key] = value
            }

            val ssid = map["ssid"] ?: return null
            val psk = map["psk"] ?: return null
            val host = map["host"] ?: DEFAULT_HOST
            val tcp = map["tcp"]?.toIntOrNull() ?: DEFAULT_TCP
            val udp = map["udp"]?.toIntOrNull() ?: DEFAULT_UDP
            val autostart = map["autostart"]?.trim() == "1"
            val nonce = map["nonce"]

            return HandshakePayload(ssid, psk, host, tcp, udp, autostart, nonce)
        }

        val legacy = trimmed.split(CREDENTIALS_SEPARATOR)
        if (legacy.size >= 2) {
            return HandshakePayload(
                ssid = legacy[0],
                psk = legacy[1],
                host = DEFAULT_HOST,
                tcpPort = DEFAULT_TCP,
                udpPort = DEFAULT_UDP,
                autostart = true,
                nonce = null
            )
        }

        return null
    }
}