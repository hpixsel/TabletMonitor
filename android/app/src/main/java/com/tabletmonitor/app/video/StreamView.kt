package com.tabletmonitor.app.video

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.widget.FrameLayout
import java.nio.ByteBuffer

/**
 * Full-screen video of the PC display: decoder surface, cursor overlay and touch/stylus capture.
 * The video is letter-boxed to keep its aspect ratio; touch coordinates are normalised to the video.
 */
@SuppressLint("ViewConstructor")
class StreamView(context: Context, private val callbacks: Callbacks) : FrameLayout(context), SurfaceHolder.Callback {

    interface Callbacks {
        fun onPointers(payload: ByteArray)
        fun onViewSize(width: Int, height: Int)
        fun onNeedKeyFrame()
        fun onFrameRendered()
    }

    private val surfaceView = SurfaceView(context)
    private val cursorView = CursorView(context)
    private var decoder: VideoDecoder? = null
    private var surfaceReady = false
    private var videoWidth = 0
    private var videoHeight = 0

    init {
        setBackgroundColor(Color.BLACK)
        surfaceView.holder.addCallback(this)
        addView(surfaceView)
        addView(cursorView)
        isFocusable = true
        isFocusableInTouchMode = true
    }

    // ---------------- Video ----------------

    /** Main thread. */
    fun setVideoSize(width: Int, height: Int) {
        if (width == videoWidth && height == videoHeight && decoder != null) {
            callbacks.onNeedKeyFrame()
            return
        }
        videoWidth = width
        videoHeight = height
        cursorView.videoWidth = width
        surfaceView.holder.setFixedSize(width, height)
        requestLayout()
        restartDecoder()
    }

    @Volatile private var framesWithoutDecoder = 0
    @Volatile private var startError: String? = null

    /** Network thread. */
    fun submitFrame(data: ByteArray, offset: Int, length: Int, keyFrame: Boolean, ptsUs: Long) {
        val d = decoder
        if (d == null) framesWithoutDecoder++ else d.submit(data, offset, length, keyFrame, ptsUs)
    }

    /** One line of diagnostics shown while waiting for the first image. */
    fun diagnostics(): String {
        val d = decoder
        return buildString {
            append("Video ${videoWidth}×$videoHeight")
            append(" · surface ${if (surfaceReady) "ready" else "missing"}")
            if (d != null) {
                append(" · frames ${d.framesReceived} (key ${d.keyFramesReceived}), shown ${d.framesRendered}")
                append(" · decoder ${d.codecName}")
            } else {
                append(" · no decoder, dropped frames $framesWithoutDecoder")
            }
            (startError ?: d?.lastError)?.let { append("\nError: $it") }
        }
    }

    fun setCursorShape(bitmap: Bitmap, hotX: Int, hotY: Int) = cursorView.setShape(bitmap, hotX, hotY)

    fun setCursorPosition(x: Int, y: Int, visible: Boolean) = cursorView.setPosition(x, y, visible)

    private fun restartDecoder() {
        decoder?.release()
        decoder = null
        if (!surfaceReady || videoWidth == 0) return
        decoder = VideoDecoder(surfaceView.holder.surface, videoWidth, videoHeight, callbacks::onNeedKeyFrame).apply {
            onFrameRendered = callbacks::onFrameRendered
            try {
                start()
                startError = null
            } catch (e: Exception) {
                android.util.Log.e("StreamView", "Decoder start failed", e)
                startError = e.message
            }
        }
    }

    fun release() {
        decoder?.release()
        decoder = null
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        surfaceReady = true
        restartDecoder()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        release()
    }

    // ---------------- Layout ----------------

    private var videoLeft = 0
    private var videoTop = 0
    private var videoW = 0
    private var videoH = 0

    override fun onLayout(changed: Boolean, left: Int, top: Int, right: Int, bottom: Int) {
        val w = right - left
        val h = bottom - top
        if (videoWidth > 0 && videoHeight > 0) {
            val scale = minOf(w.toFloat() / videoWidth, h.toFloat() / videoHeight)
            videoW = (videoWidth * scale).toInt()
            videoH = (videoHeight * scale).toInt()
        } else {
            videoW = w
            videoH = h
        }
        videoLeft = (w - videoW) / 2
        videoTop = (h - videoH) / 2
        surfaceView.layout(videoLeft, videoTop, videoLeft + videoW, videoTop + videoH)
        cursorView.layout(videoLeft, videoTop, videoLeft + videoW, videoTop + videoH)
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val w = MeasureSpec.getSize(widthMeasureSpec)
        val h = MeasureSpec.getSize(heightMeasureSpec)
        setMeasuredDimension(w, h)
        surfaceView.measure(MeasureSpec.makeMeasureSpec(w, MeasureSpec.AT_MOST), MeasureSpec.makeMeasureSpec(h, MeasureSpec.AT_MOST))
        cursorView.measure(MeasureSpec.makeMeasureSpec(w, MeasureSpec.AT_MOST), MeasureSpec.makeMeasureSpec(h, MeasureSpec.AT_MOST))
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        if (w > 0 && h > 0) callbacks.onViewSize(w, h)
    }

    // ---------------- Input ----------------

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) requestUnbufferedDispatch(event)
        send(event, hover = false)
        return true
    }

    override fun onGenericMotionEvent(event: MotionEvent): Boolean {
        val isStylus = (0 until event.pointerCount).any {
            event.getToolType(it) == MotionEvent.TOOL_TYPE_STYLUS || event.getToolType(it) == MotionEvent.TOOL_TYPE_ERASER
        }
        if (!isStylus) return super.onGenericMotionEvent(event)
        when (event.actionMasked) {
            MotionEvent.ACTION_HOVER_ENTER, MotionEvent.ACTION_HOVER_MOVE, MotionEvent.ACTION_HOVER_EXIT -> {
                send(event, hover = true)
                return true
            }
        }
        return super.onGenericMotionEvent(event)
    }

    private fun send(event: MotionEvent, hover: Boolean) {
        if (videoW == 0 || videoH == 0) return
        val count = event.pointerCount.coerceAtMost(10)
        val buf = ByteBuffer.allocate(1 + count * 16)
        buf.put(count.toByte())
        val action = event.actionMasked
        for (i in 0 until count) {
            val state: Int = when {
                hover && action == MotionEvent.ACTION_HOVER_EXIT -> STATE_HOVER_EXIT
                hover -> STATE_HOVER
                action == MotionEvent.ACTION_CANCEL -> STATE_CANCEL
                (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN) && i == event.actionIndex -> STATE_DOWN
                (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_POINTER_UP) && i == event.actionIndex -> STATE_UP
                else -> STATE_MOVE
            }
            val tool = when (event.getToolType(i)) {
                MotionEvent.TOOL_TYPE_STYLUS -> TOOL_STYLUS
                MotionEvent.TOOL_TYPE_ERASER -> TOOL_ERASER
                else -> TOOL_FINGER
            }
            val barrel = (event.buttonState and (MotionEvent.BUTTON_STYLUS_PRIMARY or MotionEvent.BUTTON_SECONDARY)) != 0
            val x = ((event.getX(i) - videoLeft) / videoW).coerceIn(0f, 1f)
            val y = ((event.getY(i) - videoTop) / videoH).coerceIn(0f, 1f)
            val pressure = if (tool == TOOL_FINGER) 1f else event.getPressure(i).coerceIn(0f, 1f)

            buf.put((event.getPointerId(i) and 0xFF).toByte())
            buf.put(state.toByte())
            buf.put(tool.toByte())
            buf.put(if (barrel) 1 else 0)
            buf.putFloat(x)
            buf.putFloat(y)
            buf.putFloat(pressure)
        }
        callbacks.onPointers(buf.array())
    }

    private companion object {
        const val STATE_DOWN = 0
        const val STATE_MOVE = 1
        const val STATE_UP = 2
        const val STATE_CANCEL = 3
        const val STATE_HOVER = 4
        const val STATE_HOVER_EXIT = 5
        const val TOOL_FINGER = 0
        const val TOOL_STYLUS = 1
        const val TOOL_ERASER = 2
    }
}

/** Draws the PC mouse cursor on top of the video (sent separately so it moves without re-encoding). */
private class CursorView(context: Context) : View(context) {
    private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
    private var bitmap: Bitmap? = null
    private var hotX = 0
    private var hotY = 0
    private var x = 0
    private var y = 0
    private var visible = false
    var videoWidth = 0

    fun setShape(b: Bitmap, hx: Int, hy: Int) {
        bitmap = b
        hotX = hx
        hotY = hy
        postInvalidateOnAnimation()
    }

    fun setPosition(px: Int, py: Int, v: Boolean) {
        x = px
        y = py
        visible = v
        postInvalidateOnAnimation()
    }

    override fun onDraw(canvas: Canvas) {
        val b = bitmap ?: return
        if (!visible || videoWidth == 0) return
        val scale = width.toFloat() / videoWidth
        canvas.save()
        canvas.scale(scale, scale)
        canvas.drawBitmap(b, (x - hotX).toFloat(), (y - hotY).toFloat(), paint)
        canvas.restore()
    }
}
