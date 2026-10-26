package com.tabletmonitor.app

import android.app.Application
import android.content.Context
import android.graphics.Bitmap
import android.hardware.display.DisplayManager
import android.net.wifi.WifiManager
import android.os.Build
import android.provider.Settings
import android.view.Display
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.tabletmonitor.app.net.Computer
import com.tabletmonitor.app.net.DeviceInfo
import com.tabletmonitor.app.net.Discovery
import com.tabletmonitor.app.net.SessionListener
import com.tabletmonitor.app.net.SessionServer
import com.tabletmonitor.app.update.Release
import com.tabletmonitor.app.update.Updater
import com.tabletmonitor.app.video.StreamView
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import java.util.UUID

sealed interface UpdateState {
    data object None : UpdateState
    data class Available(val release: Release) : UpdateState
    data class Downloading(val release: Release, val progress: Float) : UpdateState
    data class Failed(val release: Release, val message: String) : UpdateState
}

sealed interface ConnectionState {
    data object Waiting : ConnectionState
    data class Connected(val pcName: String) : ConnectionState
}

class MainViewModel(app: Application) : AndroidViewModel(app), SessionListener {
    private val prefs = app.getSharedPreferences("tabletmonitor", Context.MODE_PRIVATE)
    private val deviceId: String = prefs.getString("id", null) ?: UUID.randomUUID().toString().take(12).also {
        prefs.edit().putString("id", it).apply()
    }

    val deviceName: String = Settings.Global.getString(app.contentResolver, Settings.Global.DEVICE_NAME)
        ?.takeIf { it.isNotBlank() } ?: Build.MODEL

    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Waiting)
    val state: StateFlow<ConnectionState> = _state

    private val _message = MutableStateFlow<String?>(null)
    val message: StateFlow<String?> = _message

    private val _requestedPc = MutableStateFlow<String?>(null)
    /** Id of the PC we asked to connect, while waiting for it. */
    val requestedPc: StateFlow<String?> = _requestedPc

    private val _addresses = MutableStateFlow<List<String>>(emptyList())
    val addresses: StateFlow<List<String>> = _addresses

    private val _update = MutableStateFlow<UpdateState>(UpdateState.None)
    val update: StateFlow<UpdateState> = _update
    private var lastUpdateCheck = 0L

    private val discovery = Discovery(viewModelScope) { deviceInfo() }
    val computers: StateFlow<List<Computer>> = discovery.computers

    private val server = SessionServer(app, this) { deviceInfo() }
    private var multicastLock: WifiManager.MulticastLock? = null
    private var requestTimeout: Job? = null

    /** The view currently showing the stream (set by the UI). */
    @Volatile var streamView: StreamView? = null
        set(value) {
            field = value
            val config = videoConfig
            if (value != null && config != null) value.post { value.setVideoSize(config.first, config.second) }
        }

    @Volatile private var videoConfig: Pair<Int, Int>? = null

    fun start() {
        val wifi = getApplication<Application>().applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        multicastLock = wifi.createMulticastLock("TabletMonitor").apply {
            setReferenceCounted(false)
            acquire()
        }
        server.start()
        discovery.start()
        checkForUpdate()
        _addresses.value = discovery.localAddresses()
    }

    fun stop() {
        discovery.stop()
        server.stop()
        multicastLock?.release()
        multicastLock = null
    }

    fun connectTo(computer: Computer) {
        _requestedPc.value = computer.id
        discovery.requestConnect(computer)
        requestTimeout?.cancel()
        requestTimeout = viewModelScope.launch {
            delay(30_000)
            if (_requestedPc.value == computer.id) {
                _requestedPc.value = null
                _message.value = getApplication<Application>().getString(R.string.pc_not_responding, computer.name)
            }
        }
    }

    fun disconnect() = server.disconnect()

    // ---------------- Updates (GitHub Releases) ----------------

    private fun checkForUpdate() {
        val now = System.currentTimeMillis()
        if (now - lastUpdateCheck < 6 * 60 * 60 * 1000L || _update.value is UpdateState.Downloading) return
        lastUpdateCheck = now
        viewModelScope.launch {
            runCatching { Updater.check() }
                .onSuccess { release -> _update.value = release?.let { UpdateState.Available(it) } ?: UpdateState.None }
                .onFailure { android.util.Log.w("Updater", "Update check failed", it) } // offline – try later
        }
    }

    /** Downloads, verifies and opens the system installer. Asks for the install permission first if needed. */
    fun installUpdate() {
        val release = when (val s = _update.value) {
            is UpdateState.Available -> s.release
            is UpdateState.Failed -> s.release
            else -> return
        }
        val app = getApplication<Application>()
        if (Updater.needsInstallPermission(app)) {
            Updater.openInstallPermissionSettings(app)
            return
        }
        viewModelScope.launch {
            _update.value = UpdateState.Downloading(release, 0f)
            runCatching { Updater.download(app, release) { p -> _update.value = UpdateState.Downloading(release, p) } }
                .onSuccess { apk ->
                    _update.value = UpdateState.Available(release)
                    Updater.install(app, apk)
                }
                .onFailure { _update.value = UpdateState.Failed(release, it.message ?: it.javaClass.simpleName) }
        }
    }

    fun consumeMessage() {
        _message.value = null
    }

    // ---------------- Input from the stream view ----------------

    fun sendPointers(payload: ByteArray) = server.sendPointers(payload)
    fun sendViewSize(width: Int, height: Int) = server.sendViewSize(width, height)
    fun requestKeyFrame() = server.requestKeyFrame()

    // ---------------- SessionListener (network thread) ----------------

    override fun onConnected(pcName: String) {
        requestTimeout?.cancel()
        _requestedPc.value = null
        videoConfig = null
        _state.value = ConnectionState.Connected(pcName)
    }

    override fun onVideoConfig(width: Int, height: Int) {
        videoConfig = width to height
        streamView?.let { v -> v.post { v.setVideoSize(width, height) } }
    }

    override fun onVideoFrame(data: ByteArray, offset: Int, length: Int, keyFrame: Boolean, ptsUs: Long) {
        streamView?.submitFrame(data, offset, length, keyFrame, ptsUs)
    }

    override fun onCursorShape(bitmap: Bitmap, hotX: Int, hotY: Int) {
        streamView?.setCursorShape(bitmap, hotX, hotY)
    }

    override fun onCursorPosition(x: Int, y: Int, visible: Boolean) {
        streamView?.setCursorPosition(x, y, visible)
    }

    override fun onDisconnected(reason: String) {
        videoConfig = null
        _state.value = ConnectionState.Waiting
        _message.value = reason
        _addresses.value = discovery.localAddresses()
    }

    override fun onCleared() {
        stop()
    }

    // ---------------- Device description ----------------

    private fun deviceInfo(): DeviceInfo {
        val app = getApplication<Application>()
        val display = (app.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).getDisplay(Display.DEFAULT_DISPLAY)
        val mode = display.mode
        val w = mode.physicalWidth
        val h = mode.physicalHeight
        val refresh = display.supportedModes
            .filter { it.physicalWidth == w && it.physicalHeight == h }
            .maxOfOrNull { it.refreshRate } ?: display.refreshRate
        return DeviceInfo(
            id = deviceId,
            name = deviceName,
            model = "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}",
            width = maxOf(w, h),
            height = minOf(w, h),
            refresh = Math.round(refresh),
            dpi = app.resources.displayMetrics.densityDpi,
            busy = server.isConnected,
        )
    }
}
