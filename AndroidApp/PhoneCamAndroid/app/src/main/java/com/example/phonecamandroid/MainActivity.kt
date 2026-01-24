package com.example.phonecamandroid

import android.Manifest
import android.content.ComponentName
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.view.Surface
import android.view.TextureView
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat.startForegroundService
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var btnStart: Button
    private lateinit var btnStop: Button
    private lateinit var btnLogs: Button
    private lateinit var btnBle: Button
    private lateinit var txtStatus: TextView
    private lateinit var edtHost: EditText
    private lateinit var edtPort: EditText
    private lateinit var previewView: TextureView

    private var previewSurface: Surface? = null
    private var service: H264StreamService? = null
    private var bound = false

    private val bleManager by lazy { BleHandshakeManager(this) }

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            val ok = result.values.all { it }
            log(if (ok) "Permissions OK" else "Permissions denied: $result")
            btnStart.isEnabled = ok
        }

    private val serviceConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: android.os.IBinder?) {
            val local = binder as? H264StreamService.LocalBinder
            service = local?.service
            bound = true
            service?.setPreviewSurface(previewSurface)
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            bound = false
            service = null
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        btnStart = findViewById(R.id.btnStart)
        btnStop = findViewById(R.id.btnStop)
        btnLogs = findViewById(R.id.btnLogs)
        btnBle = findViewById(R.id.btnBle)
        txtStatus = findViewById(R.id.txtStatus)
        edtHost = findViewById(R.id.edtHost)
        edtPort = findViewById(R.id.edtPort)
        previewView = findViewById(R.id.previewView)

        previewView.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(surfaceTexture: android.graphics.SurfaceTexture, width: Int, height: Int) {
                previewSurface = Surface(surfaceTexture)
                service?.setPreviewSurface(previewSurface)
            }

            override fun onSurfaceTextureSizeChanged(surfaceTexture: android.graphics.SurfaceTexture, width: Int, height: Int) {}

            override fun onSurfaceTextureDestroyed(surfaceTexture: android.graphics.SurfaceTexture): Boolean {
                previewSurface?.release()
                previewSurface = null
                service?.setPreviewSurface(null)
                return true
            }

            override fun onSurfaceTextureUpdated(surfaceTexture: android.graphics.SurfaceTexture) {}
        }

        btnStart.setOnClickListener { startStreaming() }
        btnStop.setOnClickListener { stopStreaming() }
        btnLogs.setOnClickListener { startActivity(Intent(this, LogsActivity::class.java)) }
        btnBle.setOnClickListener { toggleBleScan() }

        ensurePermissions()

        lifecycleScope.launch {
            StreamState.stats.collectLatest { stats ->
                val idleStates = setOf("Idle", "Stopped", "Failed", "Control failed", "Stream init failed")
                val baseStatus = "State=${stats.connectionState} FPS=${"%.1f".format(stats.fps)} " +
                    "Bitrate=${"%.0f".format(stats.bitrateKbps)} kbps"
                txtStatus.text = if (stats.connectionState in idleStates) {
                    "$baseStatus (Preview starts when streaming)"
                } else {
                    baseStatus
                }
                if (stats.connectionState in idleStates) {
                    btnStart.isEnabled = true
                    btnStop.isEnabled = false
                } else if (stats.connectionState == "Streaming") {
                    btnStart.isEnabled = false
                    btnStop.isEnabled = true
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        val intent = Intent(this, H264StreamService::class.java)
        bindService(intent, serviceConnection, BIND_AUTO_CREATE)
    }

    override fun onStop() {
        super.onStop()
        if (bound) {
            unbindService(serviceConnection)
            bound = false
        }
    }

    private fun ensurePermissions() {
        val need = mutableListOf(
            Manifest.permission.CAMERA,
            Manifest.permission.RECORD_AUDIO
        )

        if (Build.VERSION.SDK_INT >= 33) {
            need.add(Manifest.permission.NEARBY_WIFI_DEVICES)
            need.add(Manifest.permission.POST_NOTIFICATIONS)
        }
        if (Build.VERSION.SDK_INT >= 31) {
            need.add(Manifest.permission.BLUETOOTH_SCAN)
            need.add(Manifest.permission.BLUETOOTH_CONNECT)
        }

        val missing = need.filter { perm ->
            ContextCompat.checkSelfPermission(this, perm) != PackageManager.PERMISSION_GRANTED
        }

        if (missing.isNotEmpty()) {
            log("Requesting: $missing")
            permissionLauncher.launch(missing.toTypedArray())
        } else {
            log("Permissions OK")
            btnStart.isEnabled = true
        }
    }

    private fun startStreaming() {
        btnStart.isEnabled = false
        btnStop.isEnabled = true

        val host = edtHost.text.toString().ifBlank { "192.168.137.1" }
        val port = edtPort.text.toString().toIntOrNull() ?: 39010

        log("Starting service → $host:$port")
        val i = Intent(this, H264StreamService::class.java).apply {
            action = H264StreamService.ACTION_START
            putExtra(H264StreamService.EXTRA_HOST, host)
            putExtra(H264StreamService.EXTRA_PORT, port)
            putExtra(H264StreamService.EXTRA_WIDTH, 1280)
            putExtra(H264StreamService.EXTRA_HEIGHT, 720)
            putExtra(H264StreamService.EXTRA_FPS, 30)
            putExtra(H264StreamService.EXTRA_BITRATE, 2_000_000)
        }
        startForegroundService(this, i)
    }

    private fun stopStreaming() {
        btnStop.isEnabled = false
        btnStart.isEnabled = true

        log("Stopping…")

        val i = Intent(this, H264StreamService::class.java).apply {
            action = H264StreamService.ACTION_STOP
        }
        startService(i)
    }

    private fun log(s: String) {
        StreamState.log(s)
    }

    private fun toggleBleScan() {
        val serviceUuid = "0000feed-0000-1000-8000-00805f9b34fb"
        if (bleManager.isScanning()) {
            bleManager.stopScan()
            btnBle.text = "BLE Scan"
        } else {
            bleManager.startScan(serviceUuid)
            btnBle.text = "Stop BLE"
        }
    }
}
