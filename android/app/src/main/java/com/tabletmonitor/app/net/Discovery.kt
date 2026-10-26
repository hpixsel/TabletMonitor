package com.tabletmonitor.app.net

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface

/**
 * UDP discovery: answers PC probes, announces this tablet and keeps the list of computers in the network
 * (every probe is also an announcement of the PC that sent it).
 */
class Discovery(private val scope: CoroutineScope, private val info: () -> DeviceInfo) {
    private val _computers = MutableStateFlow<List<Computer>>(emptyList())
    val computers: StateFlow<List<Computer>> = _computers

    private var socket: DatagramSocket? = null
    private var jobs = emptyList<Job>()

    fun start() {
        if (socket != null) return
        val s = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(Protocol.DISCOVERY_PORT))
            }
        } catch (e: Exception) {
            Log.e(TAG, "Discovery socket failed", e)
            return
        }
        socket = s
        jobs = listOf(
            scope.launch(Dispatchers.IO) { receiveLoop(s) },
            scope.launch(Dispatchers.IO) { announceLoop(s) },
        )
    }

    fun stop() {
        jobs.forEach { it.cancel() }
        socket?.close()
        socket = null
        _computers.value = emptyList()
    }

    /** Asks the PC to connect to us (the PC may first ask its user for approval). */
    fun requestConnect(computer: Computer) {
        val s = socket ?: return
        scope.launch(Dispatchers.IO) {
            val data = Protocol.discoveryPacket(Protocol.DISCOVERY_CONNECT_REQUEST, info().toJson())
            repeat(3) {
                runCatching { s.send(DatagramPacket(data, data.size, computer.address, Protocol.DISCOVERY_PORT)) }
                delay(150)
            }
        }
    }

    private fun receiveLoop(s: DatagramSocket) {
        val buffer = ByteArray(2048)
        while (!s.isClosed) {
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                s.receive(packet)
            } catch (e: Exception) {
                if (s.isClosed) return
                continue
            }
            val len = packet.length
            if (len < 6 || !buffer.copyOfRange(0, 4).contentEquals(Protocol.DISCOVERY_MAGIC)) continue
            if (buffer[4] != Protocol.DISCOVERY_PROBE) continue
            val json = runCatching { JSONObject(String(buffer, 5, len - 5, Charsets.UTF_8)) }.getOrNull() ?: continue

            val computer = Computer(
                id = json.optString("id"),
                name = json.optString("name", packet.address.hostAddress ?: "PC"),
                address = packet.address,
                lastSeen = System.currentTimeMillis(),
            )
            _computers.value = (_computers.value.filter { it.id != computer.id } + computer).sortedBy { it.name }

            val reply = Protocol.discoveryPacket(Protocol.DISCOVERY_INFO, info().toJson())
            runCatching { s.send(DatagramPacket(reply, reply.size, packet.address, packet.port)) }
        }
    }

    private suspend fun announceLoop(s: DatagramSocket) {
        while (scope.isActive && !s.isClosed) {
            val current = info()
            if (!current.busy) {
                val data = Protocol.discoveryPacket(Protocol.DISCOVERY_INFO, current.toJson())
                for (target in broadcastAddresses()) {
                    runCatching { s.send(DatagramPacket(data, data.size, target, Protocol.DISCOVERY_PORT)) }
                }
            }
            val now = System.currentTimeMillis()
            _computers.value = _computers.value.filter { now - it.lastSeen < 7000 }
            delay(2000)
        }
    }

    private fun broadcastAddresses(): Set<InetAddress> {
        val result = mutableSetOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        runCatching {
            for (nic in NetworkInterface.getNetworkInterfaces()) {
                if (!nic.isUp || nic.isLoopback) continue
                for (addr in nic.interfaceAddresses) {
                    if (addr.address is Inet4Address) addr.broadcast?.let { result += it }
                }
            }
        }
        return result
    }

    /** IPv4 addresses of this device, shown on the home screen. */
    fun localAddresses(): List<String> = runCatching {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .mapNotNull { it.hostAddress }
    }.getOrDefault(emptyList())

    private companion object {
        const val TAG = "Discovery"
    }
}
