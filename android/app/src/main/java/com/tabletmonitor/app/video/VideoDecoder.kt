package com.tabletmonitor.app.video

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import java.util.ArrayDeque

/**
 * Low-latency H.264 decoder rendering straight to a Surface. Frames are queued as soon as the codec offers
 * an input buffer and every decoded frame is shown immediately (no A/V sync, no buffering).
 */
class VideoDecoder(
    private val surface: Surface,
    private val width: Int,
    private val height: Int,
    private val onNeedKeyFrame: () -> Unit,
) {
    private class Frame(val data: ByteArray, val offset: Int, val length: Int, val keyFrame: Boolean, val ptsUs: Long)

    private val thread = HandlerThread("VideoDecoder").apply { start() }
    private val handler = Handler(thread.looper)
    private val lock = Any()
    private val pendingFrames = ArrayDeque<Frame>()
    private val freeInputs = ArrayDeque<Int>()
    private var codec: MediaCodec? = null
    private var waitingForKeyFrame = true
    @Volatile private var released = false

    var onFrameRendered: (() -> Unit)? = null

    // Diagnostics shown while waiting for the first image.
    @Volatile var codecName: String = "–"; private set
    @Volatile var lastError: String? = null; private set
    @Volatile var framesReceived = 0; private set
    @Volatile var keyFramesReceived = 0; private set
    @Volatile var framesRendered = 0; private set

    /**
     * Starts the decoder. Some vendor decoders (e.g. Samsung Exynos) reject the aggressive low-latency
     * options, so progressively simpler configurations are tried, ending with the software decoder.
     */
    fun start() {
        val hardware = pickDecoder()
        val attempts = listOfNotNull(
            hardware?.let { it to lowLatencyFormat() },
            hardware?.let { it to basicFormat() },
            null to basicFormat(),
            "c2.android.avc.decoder" to basicFormat(),
        )
        for ((name, format) in attempts) {
            val c = try {
                if (name != null) MediaCodec.createByCodecName(name) else MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            } catch (e: Exception) {
                lastError = "create $name: ${e.message}"
                continue
            }
            try {
                setCallback(c)
                c.configure(format, surface, null, 0)
                c.start()
                codecName = c.name
                codec = c
                Log.i(TAG, "Decoder ${c.name} ${width}x$height started with $format")
                onNeedKeyFrame()
                return
            } catch (e: Exception) {
                Log.w(TAG, "Decoder ${c.name} rejected $format", e)
                lastError = "${c.name}: ${e.javaClass.simpleName} ${e.message ?: ""}".trim()
                runCatching { c.release() }
            }
        }
        throw IllegalStateException("No H.264 decoder accepted ${width}x$height ($lastError)")
    }

    private fun basicFormat(): MediaFormat =
        MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)

    private fun lowLatencyFormat(): MediaFormat = basicFormat().apply {
        setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
        setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
        setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED)
        setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
    }

    private fun setCallback(c: MediaCodec) {
        c.setCallback(object : MediaCodec.Callback() {
            override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
                synchronized(lock) { freeInputs.add(index) }
                feed()
            }

            override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
                if (released) return
                try {
                    codec.releaseOutputBuffer(index, true)
                    framesRendered++
                    onFrameRendered?.invoke()
                } catch (e: IllegalStateException) {
                    // codec stopped
                }
            }

            override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
                Log.e(TAG, "Decoder error", e)
                lastError = "${codec.name}: ${e.diagnosticInfo}"
                synchronized(lock) {
                    waitingForKeyFrame = true
                    pendingFrames.clear()
                }
                if (!e.isTransient && !e.isRecoverable) {
                    // The codec is dead – start a fresh one.
                    handler.post {
                        if (released) return@post
                        runCatching { this@VideoDecoder.codec?.release() }
                        this@VideoDecoder.codec = null
                        synchronized(lock) { freeInputs.clear() }
                        runCatching { start() }.onFailure { lastError = it.message }
                    }
                } else {
                    onNeedKeyFrame()
                }
            }

            override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
                Log.i(TAG, "Output format: $format")
            }
        }, handler)
    }

    /** Called from the network thread. */
    fun submit(data: ByteArray, offset: Int, length: Int, flaggedKeyFrame: Boolean, ptsUs: Long) {
        // Trust the bitstream, not only the flag: some encoders do not mark forced IDR frames.
        val keyFrame = flaggedKeyFrame || containsIdr(data, offset, length)
        framesReceived++
        if (keyFrame) keyFramesReceived++
        synchronized(lock) {
            if (waitingForKeyFrame) {
                if (!keyFrame) return
                waitingForKeyFrame = false
            }
            // If the decoder falls behind, drop to the newest keyframe-free tail: keep at most a few frames.
            if (pendingFrames.size > 6) {
                pendingFrames.clear()
                waitingForKeyFrame = !keyFrame
                if (waitingForKeyFrame) {
                    onNeedKeyFrame()
                    return
                }
            }
            pendingFrames.add(Frame(data, offset, length, keyFrame, ptsUs))
        }
        handler.post { feed() }
    }

    private fun feed() {
        val c = codec ?: return
        while (!released) {
            val frame: Frame
            val index: Int
            synchronized(lock) {
                if (pendingFrames.isEmpty() || freeInputs.isEmpty()) return
                frame = pendingFrames.poll()!!
                index = freeInputs.poll()!!
            }
            try {
                val buffer = c.getInputBuffer(index) ?: continue
                buffer.clear()
                buffer.put(frame.data, frame.offset, frame.length)
                c.queueInputBuffer(index, 0, frame.length, frame.ptsUs, if (frame.keyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            } catch (e: Exception) {
                Log.w(TAG, "queueInputBuffer failed", e)
                return
            }
        }
    }

    fun release() {
        released = true
        handler.post {
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
            codec = null
            thread.quitSafely()
        }
    }

    /** Prefers a hardware decoder that advertises the low-latency feature. */
    private fun pickDecoder(): String? {
        val infos = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            !info.isEncoder && info.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_VIDEO_AVC, ignoreCase = true) }
        }
        fun isHardware(info: MediaCodecInfo) =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) info.isHardwareAccelerated
            else !info.name.startsWith("OMX.google.") && !info.name.startsWith("c2.android.")
        fun lowLatency(info: MediaCodecInfo) = Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
            info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC)
                .isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)

        return (infos.firstOrNull { isHardware(it) && lowLatency(it) } ?: infos.firstOrNull { isHardware(it) })?.name
    }

    private companion object {
        const val TAG = "VideoDecoder"

        /** True when the Annex-B data contains an IDR slice (NAL type 5). */
        fun containsIdr(data: ByteArray, offset: Int, length: Int): Boolean {
            val end = offset + length - 3
            var i = offset
            while (i < end) {
                if (data[i].toInt() == 0 && data[i + 1].toInt() == 0 && data[i + 2].toInt() == 1) {
                    val type = data[i + 3].toInt() and 0x1F
                    if (type == 5) return true
                    if (type == 1) return false // a regular slice – no IDR in this access unit
                    i += 3
                } else {
                    i++
                }
            }
            return false
        }
    }
}
