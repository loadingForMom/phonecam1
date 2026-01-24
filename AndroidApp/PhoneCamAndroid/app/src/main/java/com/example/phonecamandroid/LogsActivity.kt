package com.example.phonecamandroid

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class LogsActivity : AppCompatActivity() {

    private lateinit var txtStats: TextView
    private lateinit var txtLogs: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_logs)

        txtStats = findViewById(R.id.txtStats)
        txtLogs = findViewById(R.id.txtLogs)

        lifecycleScope.launch {
            StreamState.stats.collectLatest { stats ->
                txtStats.text = buildString {
                    appendLine("State: ${stats.connectionState}")
                    appendLine("FPS: ${"%.1f".format(stats.fps)}")
                    appendLine("Bitrate: ${"%.0f".format(stats.bitrateKbps)} kbps")
                    appendLine("Dropped frames: ${stats.droppedFrames}")
                    appendLine("Queue depth: ${stats.queueDepth}")
                    appendLine("Local IP: ${stats.localIp}")
                    appendLine("Remote: ${stats.remote}")
                }
            }
        }

        lifecycleScope.launch {
            StreamState.logs.collectLatest { line ->
                txtLogs.append("\n$line")
            }
        }
    }
}
