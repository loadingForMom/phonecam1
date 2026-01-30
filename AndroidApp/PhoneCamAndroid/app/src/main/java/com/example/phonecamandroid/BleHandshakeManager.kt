package com.example.phonecamandroid

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.os.ParcelUuid

class BleHandshakeManager(private val context: Context) {
    private val bluetoothManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val adapter: BluetoothAdapter? = bluetoothManager.adapter
    private val scanner = adapter?.bluetoothLeScanner

    private var scanning = false

    fun isScanning(): Boolean = scanning

    @SuppressLint("MissingPermission")
    fun startScan(serviceUuid: String) {
        if (scanning) return
        if (scanner == null) {
            StreamState.log("BLE: scanner unavailable")
            return
        }

        val filters = listOf(
            ScanFilter.Builder()
                .setServiceUuid(ParcelUuid.fromString(serviceUuid))
                .build()
        )

        val settings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .build()

        StreamState.log("BLE: scan started")
        scanning = true
        scanner.startScan(filters, settings, scanCb)
    }

    @SuppressLint("MissingPermission")
    fun stopScan() {
        if (!scanning) return
        scanning = false
        try {
            scanner?.stopScan(scanCb)
        } catch (_: Throwable) {
        }
        StreamState.log("BLE: scan stopped")
    }

    private val scanCb = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            val name = result.device.name ?: "unknown"
            StreamState.log("BLE: device=$name rssi=${result.rssi} addr=${result.device.address}")
        }

        override fun onScanFailed(errorCode: Int) {
            StreamState.log("BLE: scan failed code=$errorCode")
            scanning = false
        }
    }
}
