package com.tabletmonitor.app.net

import org.json.JSONObject
import java.nio.ByteBuffer

/** Wire protocol shared with the Windows app – see docs/PROTOCOL.md. */
object Protocol {
    const val VERSION = 1
    const val TCP_PORT = 47800
    const val DISCOVERY_PORT = 47801
    val DISCOVERY_MAGIC = "TMD1".toByteArray(Charsets.US_ASCII)

    const val DISCOVERY_PROBE: Byte = 1
    const val DISCOVERY_INFO: Byte = 2
    const val DISCOVERY_CONNECT_REQUEST: Byte = 3

    // PC → tablet
    const val HELLO: Byte = 1
    const val VIDEO_CONFIG: Byte = 2
    const val VIDEO_FRAME: Byte = 3
    const val CURSOR_SHAPE: Byte = 4
    const val CURSOR_POS: Byte = 5
    const val PING: Byte = 6
    const val BYE: Byte = 7

    // tablet → PC
    const val POINTERS: Byte = 10
    const val REQUEST_KEYFRAME: Byte = 11
    const val PONG: Byte = 12
    const val VIEW_SIZE: Byte = 13

    fun frame(type: Byte, payload: ByteArray): ByteArray =
        ByteBuffer.allocate(5 + payload.size).put(type).putInt(payload.size).put(payload).array()

    fun discoveryPacket(type: Byte, json: JSONObject): ByteArray =
        DISCOVERY_MAGIC + byteArrayOf(type) + json.toString().toByteArray(Charsets.UTF_8)
}

/** What this tablet tells the PC about itself. */
data class DeviceInfo(
    val id: String,
    val name: String,
    val model: String,
    val width: Int,
    val height: Int,
    val refresh: Int,
    val dpi: Int,
    val busy: Boolean,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("id", id)
        .put("name", name)
        .put("model", model)
        .put("width", width)
        .put("height", height)
        .put("refresh", refresh)
        .put("dpi", dpi)
        .put("port", Protocol.TCP_PORT)
        .put("version", Protocol.VERSION)
        .put("busy", busy)
}

/** A computer running TabletMonitor, heard through its discovery probes. */
data class Computer(val id: String, val name: String, val address: java.net.InetAddress, val lastSeen: Long)
