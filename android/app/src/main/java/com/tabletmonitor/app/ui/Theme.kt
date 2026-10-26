package com.tabletmonitor.app.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

val BrandStart = Color(0xFF4F6BFF)
val BrandEnd = Color(0xFF8E4DF6)
val BrandGradient = Brush.linearGradient(listOf(BrandStart, BrandEnd))

private val LightColors = lightColorScheme(
    primary = Color(0xFF4A5BE8),
    secondary = Color(0xFF7A4FE0),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFB9C3FF),
    secondary = Color(0xFFD3BBFF),
)

@Composable
fun TabletMonitorTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val context = LocalContext.current
    val colors = when {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        dark -> DarkColors
        else -> LightColors
    }
    MaterialTheme(colorScheme = colors, content = content)
}
