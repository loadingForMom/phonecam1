package com.example.phonecamandroid

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat.startForegroundService
import androidx.core.content.ContextCompat

class MainActivity : AppCompatActivity() {

    private lateinit var btnStart: Button
    private lateinit var btnStop: Button
    private lateinit var txtLog: TextView

    private var streaming = false

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            val ok = result.values.all { it }
            log(if (ok) "Permissions OK" else "Permissions denied: $result")
            btnStart.isEnabled = ok
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        btnStart = findViewById(R.id.btnStart)
        btnStop = findViewById(R.id.btnStop)
        txtLog = findViewById(R.id.txtLog)

        btnStart.setOnClickListener { startStreaming() }
        btnStop.setOnClickListener { stopStreaming() }

        ensurePermissions()
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
        streaming = true

        val host = "192.168.137.1" // TODO: сделать ввод в UI
        val port = 39010

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
        streaming = false

        log("Stopping…")

        val i = Intent(this, H264StreamService::class.java).apply {
            action = H264StreamService.ACTION_STOP
        }
        startService(i)
    }

    private fun log(s: String) {
        runOnUiThread {
            txtLog.append("\n$s")
        }
    }
}