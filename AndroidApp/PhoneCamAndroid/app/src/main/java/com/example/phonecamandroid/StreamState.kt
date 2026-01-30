package com.example.phonecamandroid

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

data class StreamStats(
    val connectionState: String = "Idle",
    val fps: Double = 0.0,
    val bitrateKbps: Double = 0.0,
    val droppedFrames: Long = 0,
    val queueDepth: Int = 0,
    val localIp: String = "-",
    val remote: String = "-"
)

object StreamState {
    private val _stats = MutableStateFlow(StreamStats())
    val stats = _stats.asStateFlow()

    private val _logs = MutableSharedFlow<String>(replay = 200, extraBufferCapacity = 200)
    val logs = _logs.asSharedFlow()

    private val timeFormat = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    fun updateStats(stats: StreamStats) {
        _stats.value = stats
    }

    fun log(msg: String) {
        val ts = timeFormat.format(Date())
        _logs.tryEmit("$ts $msg")
    }
}
