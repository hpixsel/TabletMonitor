package com.tabletmonitor.app.net

import android.content.Context
import android.graphics.Bitmap
import android.util.Log
import com.tabletmonitor.app.R
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.EOFException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.util.concurrent.Executors

interface SessionListener {
    fun onConnected(pcName: String)
    fun onVideoConfig(width: Int, height: Int)
    fun onVideoFrame(data: ByteArray, offset: Int, length: Int, keyFrame: Boolean, ptsUs: Long)
    fun onCursorShape(bitmap: Bitmap, hotX: Int, hotY: Int)
    fun onCursorPosition(x: Int, y: Int, visible: Boolean)
    fun onDisconnected(reason: String)
}

/**
 * Listens for the PC on TCP 47800. Only one session is active; a new connection from the PC replaces the
 * old one (e.g. after a Wi-Fi hiccup the old socket may not have noticed it is dead yet).
 */
class SessionServer(
    private val context: Context,
    private val listener: SessionListener,
    private val info: () -> DeviceInfo,
) {
    private var server: ServerSocket? = null
    @Volatile private var session: Session? = null

    val isConnected: Boolean get() = session != null

    fun start() {
        if (server != null) return
        val s = try {
            ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(Protocol.TCP_PORT))
            }
        } catch (e: Exception) {
            Log.e(TAG, "Cannot listen on ${Protocol.TCP_PORT}", e)
            return
        }
        server = s
        Thread({
            while (!s.isClosed) {
                val socket = try { s.accept() } catch (e: Exception) { break }
                session?.close("", notify = false) // replaced by the new connection
                val newSession = Session(socket)
                session = newSession
                Thread(newSession::run, "Session").start()
            }
        }, "SessionServer").start()
    }

    fun stop() {
        session?.close(context.getString(R.string.reason_app_closed), notify = true, sendBye = true)
        session = null
        server?.close()
        server = null
    }

    fun disconnect() {
        session?.close(context.getString(R.string.reason_disconnected), notify = true, sendBye = true)
    }

    fun sendPointers(payload: ByteArray) = session?.send(Protocol.POINTERS, payload)
    fun requestKeyFrame() = session?.send(Protocol.REQUEST_KEYFRAME, ByteArray(0))
    fun sendViewSize(width: Int, height: Int) =
        session?.send(Protocol.VIEW_SIZE, ByteBuffer.allocate(4).putShort(width.toShort()).putShort(height.toShort()).array())

    private inner class Session(private val socket: Socket) {
        private val sender = Executors.newSingleThreadExecutor()
        private val out = BufferedOutputStream(socket.getOutputStream(), 64 * 1024)
        @Volatile private var closed = false

        fun run() {
            try {
                socket.tcpNoDelay = true
                socket.receiveBufferSize = 1 shl 20
                val input = DataInputStream(BufferedInputStream(socket.getInputStream(), 256 * 1024))

                // Handshake
                socket.soTimeout = 5000
                val (type, hello) = read(input)
                if (type != Protocol.HELLO) throw IllegalStateException("Expected HELLO")
                val pcName = runCatching { JSONObject(String(hello, Charsets.UTF_8)).optString("name", "PC") }.getOrDefault("PC")
                send(Protocol.HELLO, info().toJson().toString().toByteArray(Charsets.UTF_8))
                listener.onConnected(pcName)

                socket.soTimeout = 6000 // the PC pings every second
                while (!closed) {
                    val (t, payload) = read(input)
                    handle(t, payload)
                }
            } catch (e: SocketTimeoutException) {
                close(context.getString(R.string.reason_pc_not_responding), notify = true)
            } catch (e: EOFException) {
                close(context.getString(R.string.reason_pc_ended), notify = true)
            } catch (e: Exception) {
                if (!closed) Log.w(TAG, "Session ended", e)
                close(context.getString(R.string.reason_connection_lost), notify = true)
            }
        }

        private fun handle(type: Byte, payload: ByteArray) {
            when (type) {
                Protocol.VIDEO_CONFIG -> {
                    val json = JSONObject(String(payload, Charsets.UTF_8))
                    listener.onVideoConfig(json.getInt("width"), json.getInt("height"))
                }
                Protocol.VIDEO_FRAME -> {
                    val buf = ByteBuffer.wrap(payload)
                    val flags = buf.get().toInt()
                    val pts = buf.getLong()
                    listener.onVideoFrame(payload, 9, payload.size - 9, (flags and 1) != 0, pts)
                }
                Protocol.CURSOR_SHAPE -> {
                    val buf = ByteBuffer.wrap(payload)
                    val w = buf.getShort().toInt() and 0xFFFF
                    val h = buf.getShort().toInt() and 0xFFFF
                    val hotX = buf.getShort().toInt() and 0xFFFF
                    val hotY = buf.getShort().toInt() and 0xFFFF
                    if (w > 0 && h > 0 && payload.size >= 8 + w * h * 4) {
                        val pixels = IntArray(w * h) { buf.getInt() }
                        listener.onCursorShape(Bitmap.createBitmap(pixels, w, h, Bitmap.Config.ARGB_8888), hotX, hotY)
                    }
                }
                Protocol.CURSOR_POS -> {
                    val buf = ByteBuffer.wrap(payload)
                    listener.onCursorPosition(buf.getShort().toInt(), buf.getShort().toInt(), buf.get().toInt() != 0)
                }
                Protocol.PING -> send(Protocol.PONG, payload)
                Protocol.BYE -> close(context.getString(R.string.reason_screen_off_on_pc), notify = true)
            }
        }

        private fun read(input: DataInputStream): Pair<Byte, ByteArray> {
            val type = input.readByte()
            val length = input.readInt()
            if (length < 0 || length > 16 * 1024 * 1024) throw IllegalStateException("Bad length $length")
            val payload = ByteArray(length)
            input.readFully(payload)
            return type to payload
        }

        fun send(type: Byte, payload: ByteArray) {
            if (closed) return
            sender.execute {
                try {
                    out.write(Protocol.frame(type, payload))
                    out.flush()
                } catch (e: Exception) {
                    close(context.getString(R.string.reason_connection_lost), notify = true)
                }
            }
        }

        fun close(reason: String, notify: Boolean, sendBye: Boolean = false) {
            if (closed) return
            if (sendBye) {
                send(Protocol.BYE, ByteArray(0))
                sender.shutdown()
                runCatching { sender.awaitTermination(300, java.util.concurrent.TimeUnit.MILLISECONDS) }
            }
            closed = true
            sender.shutdownNow()
            runCatching { socket.close() }
            if (session === this) session = null
            if (notify) listener.onDisconnected(reason)
        }
    }

    private companion object {
        const val TAG = "SessionServer"
    }
}
