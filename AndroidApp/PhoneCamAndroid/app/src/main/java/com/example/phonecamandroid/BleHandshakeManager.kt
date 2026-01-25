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
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiConfiguration
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.os.Build
import android.os.ParcelUuid
import java.nio.charset.StandardCharsets
import java.util.UUID

class BleHandshakeManager(private val context: Context) {
    companion object {
        val SERVICE_UUID: UUID = UUID.fromString("0000feed-0000-1000-8000-00805f9b34fb")
        val HANDSHAKE_CHAR_UUID: UUID = UUID.fromString("0000feed-0001-1000-8000-00805f9b34fb")
        private const val CREDENTIALS_SEPARATOR = "|"
    }

    private val bluetoothManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val adapter: BluetoothAdapter? = bluetoothManager.adapter
    private val advertiser: BluetoothLeAdvertiser? = adapter?.bluetoothLeAdvertiser

    private var gattServer: BluetoothGattServer? = null
    private var advertising = false
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

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

            val parts = payload.split(CREDENTIALS_SEPARATOR)
            if (parts.size >= 2) {
                val ssid = parts[0]
                val password = parts[1]
                Thread { connectToWifi(ssid, password) }.start()
            } else {
                StreamState.log("BLE: invalid credential payload")
            }
        }
    }

    private fun connectToWifi(ssid: String, password: String) {
        val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager

        if (!wifiManager.isWifiEnabled) {
            val enabled = wifiManager.setWifiEnabled(true)
            StreamState.log("Wi-Fi: enable requested result=$enabled")
        }

        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            val config = WifiConfiguration().apply {
                SSID = "\"$ssid\""
                preSharedKey = "\"$password\""
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
            return
        }

        networkCallback?.let { cm.unregisterNetworkCallback(it) }

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
                cm.bindProcessToNetwork(network)
                StreamState.log("Wi-Fi: connected to $ssid")
            }

            override fun onUnavailable() {
                StreamState.log("Wi-Fi: failed to connect to $ssid")
            }
        }
        networkCallback = callback

        cm.requestNetwork(request, callback)
        StreamState.log("Wi-Fi: request sent for $ssid")
    }
}
