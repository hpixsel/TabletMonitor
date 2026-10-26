package com.tabletmonitor.app.update

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import com.tabletmonitor.app.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

data class Release(val version: String, val apkName: String, val apkUrl: String, val checksumsUrl: String)

/**
 * In-app updates from GitHub Releases: the release carries "TabletMonitor-<version>.apk" and "SHA256SUMS.txt";
 * the APK is only handed to the system installer when its checksum matches.
 */
object Updater {
    private const val LATEST_RELEASE = "https://api.github.com/repos/hpixsel/TabletMonitor/releases/latest"

    /** The latest release when it is newer than this app, otherwise null. */
    suspend fun check(): Release? = withContext(Dispatchers.IO) {
        val json = JSONObject(get(LATEST_RELEASE).decodeToString())
        val version = json.getString("tag_name").trimStart('v', 'V')
        if (compare(version, BuildConfig.VERSION_NAME) <= 0) return@withContext null

        var apk: Pair<String, String>? = null
        var sums: String? = null
        val assets = json.getJSONArray("assets")
        for (i in 0 until assets.length()) {
            val asset = assets.getJSONObject(i)
            val name = asset.getString("name")
            val url = asset.getString("browser_download_url")
            when {
                name.endsWith(".apk", ignoreCase = true) -> apk = name to url
                name.equals("SHA256SUMS.txt", ignoreCase = true) -> sums = url
            }
        }
        val (apkName, apkUrl) = apk ?: return@withContext null
        Release(version, apkName, apkUrl, sums ?: return@withContext null)
    }

    /** Downloads and verifies the APK; returns the file in the app cache. */
    suspend fun download(context: Context, release: Release, progress: (Float) -> Unit): File = withContext(Dispatchers.IO) {
        val expected = get(release.checksumsUrl).decodeToString().lineSequence()
            .map { it.trim().split(Regex("\\s+"), limit = 2) }
            .firstOrNull { it.size == 2 && it[1].trimStart('*') == release.apkName }?.get(0)
            ?: error("No checksum for ${release.apkName}")

        val dir = File(context.cacheDir, "updates").apply { mkdirs() }
        dir.listFiles()?.forEach { it.delete() }
        val file = File(dir, release.apkName)
        val digest = MessageDigest.getInstance("SHA-256")

        val connection = open(release.apkUrl)
        try {
            val total = connection.contentLengthLong
            connection.inputStream.use { input ->
                file.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        digest.update(buffer, 0, read)
                        done += read
                        if (total > 0) progress(done.toFloat() / total)
                    }
                }
            }
        } finally {
            connection.disconnect()
        }

        val actual = digest.digest().joinToString("") { "%02x".format(it) }
        if (!actual.equals(expected, ignoreCase = true)) {
            file.delete()
            error("Checksum mismatch")
        }
        file
    }

    /** True when the user still has to allow this app to install packages. */
    fun needsInstallPermission(context: Context): Boolean = !context.packageManager.canRequestPackageInstalls()

    fun openInstallPermissionSettings(context: Context) {
        context.startActivity(
            Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }

    /** Hands the verified APK to the system installer (the user confirms there). */
    fun install(context: Context, apk: File) {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.updates", apk)
        context.startActivity(
            Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }

    private fun open(url: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000
            readTimeout = 30_000
            instanceFollowRedirects = true
            setRequestProperty("User-Agent", "TabletMonitor-Android/${BuildConfig.VERSION_NAME}")
            setRequestProperty("Accept", "application/vnd.github+json")
        }

    private fun get(url: String): ByteArray {
        val connection = open(url)
        try {
            if (connection.responseCode !in 200..299) error("HTTP ${connection.responseCode}")
            return connection.inputStream.use { it.readBytes() }
        } finally {
            connection.disconnect()
        }
    }

    /** Compares dotted versions numerically ("0.1.10" > "0.1.9"). */
    private fun compare(a: String, b: String): Int {
        val x = a.split('.').map { it.toIntOrNull() ?: 0 }
        val y = b.split('.').map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(x.size, y.size)) {
            val d = (x.getOrElse(i) { 0 }).compareTo(y.getOrElse(i) { 0 })
            if (d != 0) return d
        }
        return 0
    }
}
