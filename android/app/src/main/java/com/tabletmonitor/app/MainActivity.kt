package com.tabletmonitor.app

import android.os.Bundle
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.Crossfade
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.tabletmonitor.app.ui.HomeScreen
import com.tabletmonitor.app.ui.TabletMonitorTheme
import com.tabletmonitor.app.video.StreamView
import kotlinx.coroutines.delay

class MainActivity : ComponentActivity() {
    private val viewModel: MainViewModel by viewModels()
    private var lastBackPress = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (viewModel.state.value is ConnectionState.Connected) {
                    val now = System.currentTimeMillis()
                    if (now - lastBackPress < 2000) {
                        viewModel.disconnect()
                    } else {
                        lastBackPress = now
                        Toast.makeText(this@MainActivity, R.string.press_back_again, Toast.LENGTH_SHORT).show()
                    }
                } else {
                    finish()
                }
            }
        })

        setContent {
            TabletMonitorTheme {
                val state by viewModel.state.collectAsStateWithLifecycle()
                val computers by viewModel.computers.collectAsStateWithLifecycle()
                val requested by viewModel.requestedPc.collectAsStateWithLifecycle()
                val addresses by viewModel.addresses.collectAsStateWithLifecycle()
                val message by viewModel.message.collectAsStateWithLifecycle()
                val snackbar = remember { SnackbarHostState() }

                LaunchedEffect(message) {
                    message?.let {
                        viewModel.consumeMessage()
                        snackbar.showSnackbar(it)
                    }
                }
                LaunchedEffect(state) { applyImmersive(state is ConnectionState.Connected) }

                Box(Modifier.fillMaxSize()) {
                    Crossfade(state, label = "screen") { s ->
                        when (s) {
                            is ConnectionState.Connected -> StreamScreen(s.pcName)
                            ConnectionState.Waiting -> HomeScreen(
                                deviceName = viewModel.deviceName,
                                addresses = addresses,
                                computers = computers,
                                requestedPc = requested,
                                onConnect = viewModel::connectTo,
                            )
                        }
                    }
                    SnackbarHost(snackbar, Modifier.align(Alignment.BottomCenter).padding(24.dp))
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        viewModel.start()
    }

    override fun onStop() {
        super.onStop()
        // Leaving the app ends the session – the PC removes the extra screen.
        viewModel.stop()
    }

    @Composable
    private fun StreamScreen(pcName: String) {
        var firstFrame by remember { mutableStateOf(false) }
        var showBanner by remember { mutableStateOf(true) }
        var diagnostics by remember { mutableStateOf("") }
        LaunchedEffect(firstFrame) {
            while (!firstFrame) {
                delay(500)
                diagnostics = viewModel.streamView?.diagnostics() ?: "Waiting for the view…"
            }
        }
        LaunchedEffect(Unit) {
            delay(2500)
            showBanner = false
        }

        Box(Modifier.fillMaxSize().background(Color.Black)) {
            AndroidView(
                modifier = Modifier.fillMaxSize(),
                factory = { context ->
                    StreamView(context, object : StreamView.Callbacks {
                        override fun onPointers(payload: ByteArray) {
                            viewModel.sendPointers(payload)
                        }

                        override fun onViewSize(width: Int, height: Int) {
                            viewModel.sendViewSize(width, height)
                        }

                        override fun onNeedKeyFrame() {
                            viewModel.requestKeyFrame()
                        }

                        override fun onFrameRendered() {
                            if (!firstFrame) runOnUiThread { firstFrame = true }
                        }
                    }).also { viewModel.streamView = it }
                },
                onRelease = { view ->
                    if (viewModel.streamView === view) viewModel.streamView = null
                    view.release()
                },
            )

            if (!firstFrame) {
                Column(Modifier.align(Alignment.Center), horizontalAlignment = Alignment.CenterHorizontally) {
                    CircularProgressIndicator(Modifier.size(40.dp), color = Color.White)
                    Spacer(Modifier.height(16.dp))
                    Text(stringResource(R.string.starting_screen), color = Color.White)
                    Spacer(Modifier.height(24.dp))
                    Text(
                        diagnostics,
                        color = Color.White.copy(alpha = 0.6f),
                        style = MaterialTheme.typography.bodySmall,
                        textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                        modifier = Modifier.padding(horizontal = 32.dp),
                    )
                }
            }

            AnimatedVisibility(
                visible = showBanner,
                enter = fadeIn(),
                exit = fadeOut(),
                modifier = Modifier.align(Alignment.TopCenter).padding(top = 24.dp),
            ) {
                Text(
                    stringResource(R.string.connected_to, pcName),
                    color = Color.White,
                    style = MaterialTheme.typography.labelLarge,
                    modifier = Modifier
                        .clip(RoundedCornerShape(50))
                        .background(Color(0xCC202124))
                        .padding(horizontal = 20.dp, vertical = 10.dp),
                )
            }
        }
    }

    private fun applyImmersive(enabled: Boolean) {
        val controller = WindowCompat.getInsetsController(window, window.decorView)
        if (enabled) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            controller.hide(WindowInsetsCompat.Type.systemBars())
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.P) {
                window.attributes = window.attributes.apply {
                    layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
                }
            }
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            controller.show(WindowInsetsCompat.Type.systemBars())
        }
    }
}
