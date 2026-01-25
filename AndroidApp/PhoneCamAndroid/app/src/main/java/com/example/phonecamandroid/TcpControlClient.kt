package com.example.phonecamandroid

import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.PrintWriter
import java.net.InetSocketAddress
import java.net.Socket
import java.util.UUID

data class ControlNegotiation(
    val udpPort: Int,
    val nonce: String
)

object TcpControlClient {
    fun negotiate(
        host: String,
        port: Int,
        width: Int,
        height: Int,
        fps: Int,
        bitrate: Int
    ): ControlNegotiation? {
        Socket().use { socket ->
            socket.soTimeout = 4000
            socket.connect(InetSocketAddress(host, port), 4000)

            socket.getInputStream().use { input ->
                socket.getOutputStream().use { output ->
                    val reader = BufferedReader(InputStreamReader(input))
                    val writer = PrintWriter(output, true)

                    val challenge = reader.readLine() ?: return null
                    val parts = challenge.trim().split(" ")
                    if (parts.size < 2 || parts[0] != "CHALLENGE") return null
                    val nonce = parts[1]

                    val clientNonce = UUID.randomUUID().toString().take(8)
                    writer.println("HELLO $nonce $clientNonce $width $height $fps $bitrate")

                    val ack = reader.readLine() ?: return null
                    val ackParts = ack.trim().split(" ")
                    if (ackParts.isEmpty() || ackParts[0] != "ACK") return null

                    val udpToken = ackParts.firstOrNull { it.startsWith("UDP=") } ?: return null
                    val udpPort = udpToken.removePrefix("UDP=").toIntOrNull() ?: return null
                    return ControlNegotiation(udpPort, nonce)
                }
            }
        }
    }
}
