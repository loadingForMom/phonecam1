package com.example.phonecamandroid

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

data class StreamStats(
    val connectionState: String = "Idle",
    val fps: Float = 0f,
    val bitrateKbps: Float = 0f,
    val droppedFrames: Int = 0,
    val queueDepth: Int = 0,
    val localIp: String? = null,
    val remote: String? = null,
)

object StreamState {
    private val _stats = MutableStateFlow(StreamStats())
    val stats = _stats.asStateFlow()

    private val _logs = MutableSharedFlow<String>(replay = 100)
    val logs = _logs.asSharedFlow()

    fun log(s: String) {
        _logs.tryEmit(s)
    }

    fun update(stats: StreamStats) {
        _stats.value = stats
    }
}