package com.charlen2024.nearlink

import android.app.Activity
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.Manifest
import android.content.Intent
import android.graphics.BitmapFactory
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.Typeface
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Bundle
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.OpenableColumns
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.*
import android.animation.ValueAnimator
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatDelegate
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.setPadding
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.google.android.material.button.MaterialButton
import com.google.android.material.navigation.NavigationBarView
import com.google.android.material.navigationrail.NavigationRailView
import com.google.android.material.textfield.TextInputEditText
import com.journeyapps.barcodescanner.CaptureActivity
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.io.InputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.net.URI
import java.net.InetAddress
import java.net.MulticastSocket
import java.net.DatagramPacket
import java.net.URLDecoder
import java.net.URLEncoder
import java.util.UUID
import java.security.SecureRandom
import android.util.Base64
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.CountDownLatch

private data class Desktop(val url: String, val code: String, val name: String, val session: String, val deviceId: String = "")
private data class NearbyPhone(val id: String, val name: String, val address: String, val port: Int, val lastSeen: Long)
private data class NearbyDesktop(val id: String, val name: String, val url: String, val lastSeen: Long)

class PortraitCaptureActivity : CaptureActivity()

class MainActivity : AppCompatActivity() {
    private val http = OkHttpClient.Builder().connectTimeout(8, TimeUnit.SECONDS).readTimeout(0, TimeUnit.MILLISECONDS).build()
    private val handler = Handler(Looper.getMainLooper())
    private val sockets = ConcurrentHashMap<String, WebSocket>()
    private val histories = ConcurrentHashMap<String, MutableList<JSONObject>>()
    private val devices = mutableListOf<Desktop>()
    private val nearbyPhones = ConcurrentHashMap<String, NearbyPhone>()
    private val nearbyDesktops = ConcurrentHashMap<String, NearbyDesktop>()
    private val pairingDesktopIds = ConcurrentHashMap.newKeySet<String>()
    private val pairingPhoneIds = ConcurrentHashMap.newKeySet<String>()
    private val phoneHistories = ConcurrentHashMap<String, MutableList<JSONObject>>()
    private var selectedPhoneId: String? = null
    private var deviceEditMode = false
    private val selectedPairedComputerIds = mutableSetOf<String>()
    private val selectedPairedPhoneIds = mutableSetOf<String>()
    private var phoneServer: ServerSocket? = null
    private var phoneServerThread: Thread? = null
    private var phoneId: String = ""
    private val phoneTransferExecutor = java.util.concurrent.Executors.newCachedThreadPool()
    private val pendingPhoneApprovals = ConcurrentHashMap<String, CountDownLatch>()
    private var selected = 0
    private var page = "连接"
    private var root: LinearLayout? = null
    private var content: LinearLayout? = null
    private var homeHeader: View? = null
    private var navRail: NavigationRailView? = null
    private var navCollapseButton: TextView? = null
    private var navCollapsed = false
    private val connectDestinationId = View.generateViewId()
    private val devicesDestinationId = View.generateViewId()
    private val transferDestinationId = View.generateViewId()
    private val settingsDestinationId = View.generateViewId()
    private val aboutDestinationId = View.generateViewId()
    private var statusText: TextView? = null
    private var pendingDownload: JSONObject? = null
    private var pendingAnnotatedBitmap: Bitmap? = null
    private var externalUris: List<Uri> = emptyList()
    private val messageNotificationPermission = registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestPermission()) { }
    private var destroyed = false
    @Volatile private var updateCheckInProgress = false
    private val updateCheckIntervalMs = 15 * 60 * 1000L
    private val updateCheckRunnable = object : Runnable {
        override fun run() {
            if (!destroyed && page != "传输") checkForAndroidUpdate(manual = false)
            if (!destroyed) handler.postDelayed(this, updateCheckIntervalMs)
        }
    }
    private var discoveryThread: Thread? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private val darkMode get() = (resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES
    private val pageBackground get() = if (darkMode) 0xff11151b.toInt() else 0xfff7f8fa.toInt()
    private val cardBackground get() = if (darkMode) 0xff1b222b.toInt() else Color.WHITE
    private val primaryText get() = if (darkMode) 0xfff1f4f8.toInt() else 0xff1f2937.toInt()
    private val secondaryText get() = if (darkMode) 0xffaab5c3.toInt() else 0xff6f7a8b.toInt()
    private val outline get() = if (darkMode) 0xff343e4a.toInt() else 0xffe7ebf0.toInt()
    private val subtleFill get() = if (darkMode) 0xff252e39.toInt() else 0xffe9edf2.toInt()

    private val scanner = registerForActivityResult(ScanContract()) { result ->
        if (result.contents != null) addDesktopFromLink(result.contents)
    }
    private val uploadPicker = registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        uris?.forEach { if (selectedPhoneId != null) uploadToPhone(it) else upload(it) }
    }
    private val savePicker = registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val item = pendingDownload
        if (uri != null && item != null) download(item, uri)
        pendingDownload = null
    }
    private val annotatedImagePicker = registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.CreateDocument("image/png")) { uri ->
        val bitmap = pendingAnnotatedBitmap
        pendingAnnotatedBitmap = null
        if (uri != null && bitmap != null) try {
            contentResolver.openOutputStream(uri)?.use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            toast("标注图片已保存")
        } catch (e: Exception) { toast("保存失败：${e.message}") }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        page = savedInstanceState?.getString("page") ?: "连接"
        val themeMode = when (getSharedPreferences("nearlink", MODE_PRIVATE).getString("theme", "system")) {
            "light" -> AppCompatDelegate.MODE_NIGHT_NO
            "dark" -> AppCompatDelegate.MODE_NIGHT_YES
            else -> AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM
        }
        AppCompatDelegate.setDefaultNightMode(themeMode)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.statusBarColor = Color.TRANSPARENT
        window.navigationBarColor = Color.TRANSPARENT
        WindowInsetsControllerCompat(window, window.decorView).apply { isAppearanceLightStatusBars = !darkMode; isAppearanceLightNavigationBars = !darkMode }
        loadDevices()
        phoneId = getSharedPreferences("nearlink", MODE_PRIVATE).getString("phoneDeviceId", null)
            ?: UUID.randomUUID().toString().also { getSharedPreferences("nearlink", MODE_PRIVATE).edit().putString("phoneDeviceId", it).apply() }
        loadPhoneHistories()
        startPhoneServer()
        startDiscovery()
        buildShell()
        createMessageNotificationChannel()
        connectAll()
        handleExternalFileIntent(intent)
        handleMessageNotificationIntent(intent)
        requestMessageNotificationPermission()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleExternalFileIntent(intent)
        handleMessageNotificationIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        val lastCheck = getSharedPreferences("nearlink", MODE_PRIVATE).getLong("lastUpdateCheck", 0L)
        if (page != "传输" && System.currentTimeMillis() - lastCheck > updateCheckIntervalMs)
            checkForAndroidUpdate(manual = false)
        handler.removeCallbacks(updateCheckRunnable)
        handler.postDelayed(updateCheckRunnable, updateCheckIntervalMs)
    }

    override fun onPause() {
        handler.removeCallbacks(updateCheckRunnable)
        super.onPause()
    }

    private fun checkForAndroidUpdate(manual: Boolean) {
        if (updateCheckInProgress) return
        updateCheckInProgress = true
        phoneTransferExecutor.execute {
            try {
                val request = Request.Builder().url("https://charlen.top/api/v1/releases/android").get().build()
                val release = http.newBuilder().readTimeout(12, TimeUnit.SECONDS).build().newCall(request).execute().use { response ->
                    if (!response.isSuccessful) throw java.io.IOException("更新服务暂不可用（${response.code}）")
                    JSONObject(response.body?.string().orEmpty())
                }
                getSharedPreferences("nearlink", MODE_PRIVATE).edit().putLong("lastUpdateCheck", System.currentTimeMillis()).apply()
                val latestName = release.optString("version", "").removePrefix("v")
                val latestCode = release.optLong("versionCode", 0L)
                val packageInfo = packageManager.getPackageInfo(packageName, 0)
                val currentName = packageInfo.versionName ?: "1.0.0"
                val currentCode = if (Build.VERSION.SDK_INT >= 28) packageInfo.longVersionCode else packageInfo.versionCode.toLong()
                val hasUpdate = latestName.isNotBlank() && latestCode > currentCode
                main {
                    if (hasUpdate) showAndroidUpdateDialog(release)
                    else if (manual) AlertDialog.Builder(this).setTitle("已是最新版本")
                        .setMessage("Nearlink $currentName 已是最新版本。")
                        .setPositiveButton("确定", null).show()
                }
            } catch (error: Exception) {
                if (manual) main { toast("检查更新失败：${error.message ?: "网络暂不可用"}") }
            } finally { updateCheckInProgress = false }
        }
    }

    private fun showAndroidUpdateDialog(release: JSONObject) {
        if (isFinishing || isDestroyed) return
        val notes = release.optString("notes").ifBlank { "现在下载并安装更新吗？" }
        AlertDialog.Builder(this)
            .setTitle("Nearlink 有新版本 ${release.optString("version")}")
            .setMessage(notes)
            .setNegativeButton("稍后", null)
            .setPositiveButton("下载并更新") { _, _ -> downloadAndroidUpdate(release) }
            .show()
    }

    private fun downloadAndroidUpdate(release: JSONObject) {
        if (Build.VERSION.SDK_INT >= 26 && !packageManager.canRequestPackageInstalls()) {
            AlertDialog.Builder(this)
                .setTitle("需要允许安装更新")
                .setMessage("请允许 Nearlink 安装应用。返回后再次点击“检查更新”即可继续。")
                .setNegativeButton("取消", null)
                .setPositiveButton("打开设置") { _, _ ->
                    startActivity(Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
                }.show()
            return
        }
        val url = release.optString("downloadUrl")
        val uri = try { Uri.parse("https://charlen.top").buildUpon().encodedPath(url).build() } catch (_: Exception) { null }
        if (uri == null || uri.scheme != "https" || uri.host != "charlen.top") { toast("更新下载地址无效"); return }
        toast("正在下载 Nearlink 更新…")
        phoneTransferExecutor.execute {
            try {
                val request = Request.Builder().url(uri.toString()).get().build()
                val apk = File(cacheDir, "updates/Nearlink-Android.apk")
                apk.parentFile?.mkdirs()
                http.newBuilder().readTimeout(5, TimeUnit.MINUTES).build().newCall(request).execute().use { response ->
                    if (!response.isSuccessful) throw java.io.IOException("下载失败（${response.code}）")
                    val body = response.body ?: throw java.io.IOException("更新文件为空")
                    body.byteStream().use { input -> apk.outputStream().use { input.copyTo(it) } }
                }
                val expected = release.optString("sha256").lowercase()
                val actual = java.security.MessageDigest.getInstance("SHA-256").digest(apk.readBytes()).joinToString("") { "%02x".format(it) }
                if (expected.isBlank() || !java.security.MessageDigest.isEqual(expected.toByteArray(), actual.toByteArray()))
                    throw java.io.IOException("更新文件校验失败")
                main {
                    val contentUri = androidx.core.content.FileProvider.getUriForFile(this, "$packageName.updates", apk)
                    val install = Intent(Intent.ACTION_VIEW).apply {
                        setDataAndType(contentUri, "application/vnd.android.package-archive")
                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    startActivity(install)
                }
            } catch (error: Exception) { main { toast("更新下载失败：${error.message ?: "请稍后重试"}") } }
        }
    }

    private fun requestMessageNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            messageNotificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    private fun createMessageNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel("nearlink_messages_v2", "新消息提醒", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Nearlink 收到新消息时播放提示音并振动"
                enableVibration(true)
                vibrationPattern = longArrayOf(0, 250, 150, 250)
                setSound(android.provider.Settings.System.DEFAULT_NOTIFICATION_URI,
                    android.media.AudioAttributes.Builder()
                        .setUsage(android.media.AudioAttributes.USAGE_NOTIFICATION)
                        .setContentType(android.media.AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build())
            }
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    private fun notifyReceivedMessage(sender: String, text: String, messageId: String, phonePeerId: String? = null, desktopUrl: String? = null) {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) return
        val open = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
            data = Uri.parse("nearlink://message/${Uri.encode(messageId)}")
            putExtra("nearlinkMessageNotification", true)
            phonePeerId?.let { putExtra("nearlinkPhonePeerId", it) }
            desktopUrl?.let { putExtra("nearlinkDesktopUrl", it) }
        }
        val requestCode = messageId.hashCode()
        val pending = PendingIntent.getActivity(this, requestCode, open, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val notification = NotificationCompat.Builder(this, "nearlink_messages_v2")
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Nearlink · $sender")
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(pending)
            .setAutoCancel(true)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .build()
        try { NotificationManagerCompat.from(this).notify("nearlink-message:$messageId", 0, notification) } catch (_: SecurityException) { }
    }

    private fun handleMessageNotificationIntent(intent: Intent?) {
        if (intent?.getBooleanExtra("nearlinkMessageNotification", false) != true) return
        val peerId = intent.getStringExtra("nearlinkPhonePeerId")
        val desktopUrl = intent.getStringExtra("nearlinkDesktopUrl")
        if (!peerId.isNullOrBlank()) {
            selectedPhoneId = peerId
        } else if (!desktopUrl.isNullOrBlank()) {
            val index = devices.indexOfFirst { it.url == desktopUrl }
            if (index >= 0) { selected = index; selectedPhoneId = null }
        }
        page = "传输"
        if (content != null) render()
        intent.removeExtra("nearlinkMessageNotification")
    }

    private fun handleExternalFileIntent(intent: Intent?) {
        if (intent == null) return
        val incoming = mutableListOf<Uri>()
        when (intent.action) {
            Intent.ACTION_VIEW -> intent.data?.let(incoming::add)
            Intent.ACTION_SEND -> {
                @Suppress("DEPRECATION")
                (intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))?.let(incoming::add)
                if (incoming.isEmpty()) intent.clipData?.let { clip -> for (i in 0 until clip.itemCount) clip.getItemAt(i).uri?.let(incoming::add) }
            }
            Intent.ACTION_SEND_MULTIPLE -> {
                @Suppress("DEPRECATION")
                intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.let(incoming::addAll)
                if (incoming.isEmpty()) intent.clipData?.let { clip -> for (i in 0 until clip.itemCount) clip.getItemAt(i).uri?.let(incoming::add) }
            }
        }
        val files = incoming.distinct()
        if (files.isEmpty()) return
        externalUris = files
        selectedPhoneId = null
        page = "连接"
        if (content != null) render()
    }

    private fun sendExternalFilesToDesktop(index: Int) {
        if (externalUris.isEmpty()) return
        selectedPhoneId = null
        selected = index
        val files = externalUris
        externalUris = emptyList()
        page = "传输"
        render()
        files.forEach { upload(it) }
    }

    private fun sendExternalFilesToPhone(peer: NearbyPhone) {
        if (externalUris.isEmpty()) return
        selectedPhoneId = peer.id
        val files = externalUris
        externalUris = emptyList()
        page = "传输"
        render()
        files.forEach { uploadToPhone(it) }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putString("page", page)
        super.onSaveInstanceState(outState)
    }

    override fun onDestroy() {
        destroyed = true
        handler.removeCallbacks(updateCheckRunnable)
        discoveryThread?.interrupt()
        phoneServerThread?.interrupt()
        try { phoneServer?.close() } catch (_: Exception) { }
        phoneTransferExecutor.shutdownNow()
        multicastLock?.let { if (it.isHeld) it.release() }
        sockets.values.forEach { it.close(1000, "app closed") }
        sockets.clear()
        super.onDestroy()
    }

    private fun buildShell() {
        val outer = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setBackgroundColor(pageBackground) }
        ViewCompat.setOnApplyWindowInsetsListener(outer) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            val ime = insets.getInsets(WindowInsetsCompat.Type.ime())
            view.setPadding(0, bars.top, 0, maxOf(bars.bottom, ime.bottom))
            insets
        }
        root = outer
        val rail = NavigationRailView(this).apply {
            setBackgroundColor(cardBackground)
            labelVisibilityMode = NavigationBarView.LABEL_VISIBILITY_LABELED
            val activeColors = android.content.res.ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                intArrayOf(if (darkMode) 0xffa9c7ff.toInt() else 0xff245fb8.toInt(), secondaryText)
            )
            itemIconTintList = activeColors
            itemTextColor = activeColors
            setItemActiveIndicatorColor(android.content.res.ColorStateList.valueOf(if (darkMode) 0xff283b5c.toInt() else 0xffe8f0fe.toInt()))
            menu.add(0, connectDestinationId, 0, "连接").setIcon(R.drawable.ic_connect).isCheckable = true
            menu.add(0, devicesDestinationId, 1, "设备").setIcon(R.drawable.ic_devices).isCheckable = true
            menu.add(0, transferDestinationId, 2, "传输").setIcon(R.drawable.ic_transfer).isCheckable = true
            menu.add(0, settingsDestinationId, 3, "设置").setIcon(R.drawable.ic_settings).isCheckable = true
            menu.add(0, aboutDestinationId, 4, "关于").setIcon(R.drawable.ic_info).isCheckable = true
            setOnItemSelectedListener { item ->
                val nextPage = when (item.itemId) {
                    connectDestinationId -> "连接"
                    devicesDestinationId -> "设备"
                    transferDestinationId -> "传输"
                    settingsDestinationId -> "设置"
                    else -> "关于"
                }
                if (page != nextPage) {
                if (page == "设备" && nextPage != "设备") {
                    deviceEditMode = false
                    selectedPairedComputerIds.clear()
                    selectedPairedPhoneIds.clear()
                }
                    page = nextPage
                    render()
                }
                true
            }
        }
        navRail = rail
        val collapse = TextView(this).apply {
            text = "‹"
            textSize = 28f
            gravity = Gravity.CENTER
            setTextColor(secondaryText)
            contentDescription = "收起侧边栏"
            setOnClickListener { toggleNavigationRail() }
        }
        navCollapseButton = collapse
        collapse.layoutParams = LinearLayout.LayoutParams(-1, dp(52))
        rail.addHeaderView(collapse)
        outer.addView(rail, LinearLayout.LayoutParams(dp(80), -1))
        val mainPane = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        outer.addView(mainPane, LinearLayout.LayoutParams(0, -1, 1f))
        val top = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(18), dp(12), dp(16), dp(8)) }
        top.addView(label("Nearlink", 22, primaryText, true))
        top.addView(label("手机与 Windows 之间的局域网互传", 12, secondaryText).apply { setPadding(0, dp(2), 0, dp(4)); maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END })
        statusText = label("正在连接设备…", 13, secondaryText)
        top.addView(statusText)
        homeHeader = top
        mainPane.addView(top)
        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), dp(4), dp(12), dp(8))
        }
        mainPane.addView(content, LinearLayout.LayoutParams(-1, 0, 1f))
        setContentView(outer)
        render()
    }

    private fun render() {
        val host = content ?: return
        val widthDp = resources.configuration.screenWidthDp
        val availableWidth = widthDp - if (navCollapsed) 64 else 80
        val sideInsetDp = if (availableWidth >= 1200) ((availableWidth - 1040) / 2).coerceAtLeast(12) else 12
        host.setPadding(dp(sideInsetDp), dp(4), dp(sideInsetDp), dp(8))
        homeHeader?.visibility = if (page == "连接") View.VISIBLE else View.GONE
        val destinationId = when (page) {
            "连接" -> connectDestinationId
            "设备" -> devicesDestinationId
            "传输" -> transferDestinationId
            "设置" -> settingsDestinationId
            else -> aboutDestinationId
        }
        navRail?.menu?.findItem(destinationId)?.isChecked = true
        host.removeAllViews()
        when (page) {
            "连接" -> renderDevices(host)
            "设备" -> renderPairedDevices(host)
            "传输" -> renderTransfer(host)
            "设置" -> renderSettings(host)
            else -> renderAbout(host)
        }
    }

    private fun toggleNavigationRail() {
        val rail = navRail ?: return
        navCollapsed = !navCollapsed
        val targetWidth = dp(if (navCollapsed) 64 else 80)
        val startWidth = rail.layoutParams.width
        rail.labelVisibilityMode = if (navCollapsed) NavigationBarView.LABEL_VISIBILITY_UNLABELED else NavigationBarView.LABEL_VISIBILITY_LABELED
        navCollapseButton?.apply {
            contentDescription = if (navCollapsed) "展开侧边栏" else "收起侧边栏"
            animate().rotation(if (navCollapsed) 180f else 0f).setDuration(180).start()
        }
        ValueAnimator.ofInt(startWidth, targetWidth).apply {
            duration = 180
            addUpdateListener { animator ->
                rail.layoutParams = rail.layoutParams.apply { width = animator.animatedValue as Int }
            }
            start()
        }
    }

    private fun renderSettings(host: LinearLayout) {
        val scroll = ScrollView(this).apply { clipToPadding = false }
        val pageContent = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        scroll.addView(pageContent)
        host.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        sectionHeader(pageContent, "设置", "管理 Nearlink 的外观与使用方式")
        val identityCard = card()
        identityCard.addView(label("设备标识", 17, primaryText, true))
        identityCard.addView(label("连接的电脑和其他手机会用这个名称识别你。", 13, secondaryText).apply { setPadding(0, dp(4), 0, dp(10)) })
        val identityInput = com.google.android.material.textfield.TextInputEditText(this).apply {
            setText(currentDeviceName())
            hint = "例如：我的手机"
            setSingleLine(true)
            maxLines = 1
            filters = arrayOf(android.text.InputFilter.LengthFilter(32))
        }
        identityCard.addView(identityInput, marginBottom(8))
        val identityStatus = label("", 12, secondaryText)
        val saveIdentity = MaterialButton(this).apply {
            text = "保存设备标识"
            isAllCaps = false
            setOnClickListener {
                val updatedName = identityInput.text?.toString()?.trim().orEmpty()
                if (updatedName.isBlank()) {
                    identityStatus.text = "请填写设备标识"
                } else {
                    getSharedPreferences("nearlink", MODE_PRIVATE).edit().putString("deviceName", updatedName.take(32)).apply()
                    val rename = JSONObject().put("type", "rename").put("name", currentDeviceName()).toString()
                    sockets.values.forEach { it.send(rename) }
                    identityStatus.text = "已保存，连接设备名称已更新"
                }
            }
        }
        identityCard.addView(saveIdentity)
        identityCard.addView(identityStatus.apply { setPadding(0, dp(6), 0, 0) })
        pageContent.addView(identityCard, marginBottom(12))
        val themeCard = card()
        themeCard.addView(label("外观", 17, primaryText, true))
        themeCard.addView(label("选择主题，也可以跟随系统设置。", 13, secondaryText).apply { setPadding(0, dp(4), 0, dp(10)) })
        val themeOptions = mutableMapOf<String, com.google.android.material.radiobutton.MaterialRadioButton>()
        val savedTheme = getSharedPreferences("nearlink", MODE_PRIVATE).getString("theme", "system") ?: "system"
        listOf(Triple("system", "跟随系统", "自动使用手机当前的浅色或深色主题"), Triple("light", "浅色", "使用浅色背景"), Triple("dark", "深色", "使用深色背景")).forEach { (key, title, description) ->
            val option = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(0, dp(5), 0, dp(5)) }
            val radio = com.google.android.material.radiobutton.MaterialRadioButton(this).apply {
                text = title
                textSize = 15f
                isChecked = savedTheme == key
                setOnClickListener {
                    themeOptions.forEach { (optionKey, button) -> button.isChecked = optionKey == key }
                    setThemePreference(key)
                }
            }
            themeOptions[key] = radio
            option.addView(radio)
            option.addView(label(description, 12, secondaryText).apply { setPadding(dp(36), 0, 0, 0) })
            themeCard.addView(option)
        }
        pageContent.addView(themeCard, marginBottom(12))
    }

    private fun setThemePreference(theme: String) {
        getSharedPreferences("nearlink", MODE_PRIVATE).edit().putString("theme", theme).apply()
        val mode = when (theme) {
            "light" -> AppCompatDelegate.MODE_NIGHT_NO
            "dark" -> AppCompatDelegate.MODE_NIGHT_YES
            else -> AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM
        }
        AppCompatDelegate.setDefaultNightMode(mode)
    }

    private fun renderAbout(host: LinearLayout) {
        val scroll = ScrollView(this).apply { clipToPadding = false }
        val pageContent = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        scroll.addView(pageContent)
        host.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        sectionHeader(pageContent, "关于 Nearlink", "手机与 Windows 之间的局域网互传")
        val about = card()
        about.addView(label("Nearlink", 22, primaryText, true))
        about.addView(label("在同一局域网内实时发送消息与文件。", 14, secondaryText).apply { setPadding(0, dp(8), 0, dp(16)) })
        val version = try { packageManager.getPackageInfo(packageName, 0).versionName ?: "1.0.0" } catch (_: Exception) { "1.0.0" }
        about.addView(label("版本 $version", 14, secondaryText).apply { setPadding(0, 0, 0, dp(10)) })
        about.addView(label("Designed by chao", 14, primaryText, true))
        about.addView(label("GitHub · charlen2024", 14, secondaryText).apply { setPadding(0, dp(5), 0, 0) })
        about.addView(MaterialButton(this).apply {
            text = "检查更新"; isAllCaps = false
            setOnClickListener { checkForAndroidUpdate(manual = true) }
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(14) })
        pageContent.addView(about, marginBottom(12))
    }

    private fun renderDevices(host: LinearLayout) {
        val scroll = ScrollView(this).apply { clipToPadding = false }
        val pageContent = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        scroll.addView(pageContent)
        host.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        sectionHeader(pageContent, "正在搜索附近的设备", "同一局域网中的电脑和手机会显示在这里")
        if (externalUris.isNotEmpty()) {
            val pending = card()
            pending.addView(label("从其他应用打开了 ${externalUris.size} 个文件", 16, primaryText, true))
            pending.addView(label(externalUris.firstOrNull()?.let(::queryName) ?: "文件", 13, secondaryText).apply { maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END; setPadding(0, dp(5), 0, 0) })
            pending.addView(label("点选下方任一在线设备即可发送", 13, if (darkMode) 0xff8ab4ff.toInt() else 0xff245fb8.toInt()).apply { setPadding(0, dp(8), 0, 0) })
            pageContent.addView(pending, marginBottom(12))
        }
        val discovered = nearbyDesktops.values.sortedBy { it.name.lowercase() }
        val peers = nearbyPhones.values.filter { it.id != phoneId }.sortedBy { it.name.lowercase() }
        val radar = card().apply {
            gravity = Gravity.CENTER
            setPadding(dp(12), dp(16), dp(12), dp(14))
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(if (darkMode) 0xff171d27.toInt() else 0xfff7f9fc.toInt())
                cornerRadius = dp(22).toFloat()
                setStroke(dp(1), outline)
            }
            addView(NearbyRadarView(this@MainActivity, darkMode, discovered.size + peers.size), LinearLayout.LayoutParams(-1, dp(216)))
            addView(label(
                if (discovered.isEmpty() && peers.isEmpty()) "正在搜索附近的设备" else "发现 ${discovered.size + peers.size} 台附近设备",
                15, primaryText, true
            ).apply { gravity = Gravity.CENTER; setPadding(0, dp(4), 0, dp(3)) })
            addView(label("同一局域网内，电脑和手机都可以直接连接", 12, secondaryText).apply { gravity = Gravity.CENTER })
        }
        pageContent.addView(radar, marginBottom(14))
        if (discovered.isEmpty() && peers.isEmpty()) {
            pageContent.addView(label("请确认设备已打开 Nearlink，并连接同一 Wi-Fi。", 13, secondaryText).apply {
                gravity = Gravity.CENTER
                setPadding(dp(12), dp(0), dp(12), dp(14))
            })
        } else discovered.forEach { peer ->
            val pairedIndex = devices.indexOfFirst { it.deviceId == peer.id }
            val box = card()
            val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
            row.addView(label(if (pairedIndex >= 0 && sockets.containsKey(devices[pairedIndex].url)) "●" else "○", 18,
                if (pairedIndex >= 0 && sockets.containsKey(devices[pairedIndex].url)) 0xff46a874.toInt() else 0xff7b8794.toInt()).apply { setPadding(0, 0, dp(12), 0) })
            row.addView(label("${peer.name} · 电脑", 16, primaryText, true), LinearLayout.LayoutParams(0, -2, 1f))
            box.addView(row)
            val action = MaterialButton(this).apply {
                isAllCaps = false
                val pending = pairingDesktopIds.contains(peer.id)
                text = when {
                    pending -> "等待电脑确认…"
                    pairedIndex >= 0 -> "打开传输"
                    else -> "申请配对连接"
                }
                isEnabled = !pending
                setOnClickListener {
                    if (pairedIndex >= 0) {
                        if (externalUris.isNotEmpty()) sendExternalFilesToDesktop(pairedIndex)
                        else { selectedPhoneId = null; selected = pairedIndex; page = "传输"; render() }
                    } else requestDesktopPair(peer)
                }
            }
            box.addView(action, LinearLayout.LayoutParams(-1, dp(48)).apply { topMargin = dp(10) })
            pageContent.addView(box, marginBottom(10))
        }
        peers.forEach { peer ->
            val box = card()
            val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
            row.addView(label("●", 18, 0xff46a874.toInt()).apply { setPadding(0, 0, dp(12), 0) })
            val names = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            names.addView(label("${peer.name} · 手机", 16, primaryText, true))
            names.addView(label(if (isPhoneTrusted(peer.id)) "已配对 · 手机直连" else "手机直连 · 可申请配对", 13, secondaryText).apply { setPadding(0, dp(4), 0, 0) })
            row.addView(names, LinearLayout.LayoutParams(0, -2, 1f))
            box.addView(row)
            val pending = pairingPhoneIds.contains(peer.id)
            box.addView(MaterialButton(this).apply {
                isAllCaps = false
                text = when {
                    pending -> "等待对方确认…"
                    isPhoneTrusted(peer.id) -> "打开传输"
                    else -> "申请配对连接"
                }
                isEnabled = !pending
                setOnClickListener {
                    if (isPhoneTrusted(peer.id)) {
                        if (externalUris.isNotEmpty()) sendExternalFilesToPhone(peer)
                        else { selectedPhoneId = peer.id; page = "传输"; render() }
                    } else requestPhonePair(peer)
                }
            }, LinearLayout.LayoutParams(-1, dp(46)).apply { topMargin = dp(9) })
            pageContent.addView(box, marginBottom(10))
        }
        pageContent.addView(MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply {
            text = "手动添加（扫码连接）"; isAllCaps = false
            setOnClickListener {
                scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                    .setPrompt("扫描电脑上 Nearlink 连接页中的二维码")
                    .setBeepEnabled(false)
                    .setCaptureActivity(PortraitCaptureActivity::class.java)
                    .setOrientationLocked(true))
            }
        }, marginBottom(16))
    }

    private fun renderPairedDevices(host: LinearLayout) {
        val scroll = ScrollView(this).apply { clipToPadding = false }
        val pageContent = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        scroll.addView(pageContent)
        host.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        pageContent.addView(MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply {
            text = "‹  返回附近设备"; isAllCaps = false
            setOnClickListener {
                deviceEditMode = false
                selectedPairedComputerIds.clear()
                selectedPairedPhoneIds.clear()
                page = "连接"
                render()
            }
        }, marginBottom(8))
        val trustedPhoneIds = getSharedPreferences("nearlink", MODE_PRIVATE)
            .getStringSet("trustedPhoneIds", emptySet()).orEmpty().toList().sortedBy { trustedPhoneName(it).lowercase() }
        val titleRow = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        titleRow.addView(label("已配对设备", 21, primaryText, true), LinearLayout.LayoutParams(0, -2, 1f))
        titleRow.addView(MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply {
            text = if (deviceEditMode) "完成" else "编辑"; isAllCaps = false
            setOnClickListener {
                deviceEditMode = !deviceEditMode
                selectedPairedComputerIds.clear()
                selectedPairedPhoneIds.clear()
                render()
            }
        })
        pageContent.addView(titleRow)
        pageContent.addView(label(
            if (deviceEditMode) "勾选要移除的设备，可一次选择多台。"
            else "管理手机和电脑的配对记录。",
            13, secondaryText
        ).apply { setPadding(0, 0, 0, dp(14)) })
        val bulkRemoveButton = MaterialButton(this).apply {
            text = "移除所选（0）"
            isAllCaps = false
            isEnabled = false
            setOnClickListener { confirmRemoveSelectedDevices() }
        }
        fun refreshBulkRemoveButton() {
            val selectedCount = selectedPairedComputerIds.size + selectedPairedPhoneIds.size
            bulkRemoveButton.text = "移除所选（$selectedCount）"
            bulkRemoveButton.isEnabled = selectedCount > 0
        }
        pageContent.addView(label("电脑（${devices.size}）", 17, primaryText, true).apply { setPadding(0, dp(6), 0, dp(8)) })
        if (devices.isEmpty()) pageContent.addView(label("还没有配对电脑。", 14, secondaryText).apply { setPadding(dp(4), 0, 0, dp(12)) })
        devices.toList().forEach { device ->
            val box = card()
            val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
            val selectionKey = computerSelectionKey(device)
            if (deviceEditMode) row.addView(CheckBox(this).apply {
                isChecked = selectionKey in selectedPairedComputerIds
                setOnCheckedChangeListener { _, checked ->
                    if (checked) selectedPairedComputerIds.add(selectionKey) else selectedPairedComputerIds.remove(selectionKey)
                    refreshBulkRemoveButton()
                }
            })
            row.addView(label(if (sockets.containsKey(device.url)) "●" else "○", 18,
                if (sockets.containsKey(device.url)) 0xff46a874.toInt() else secondaryText).apply { setPadding(0, 0, dp(12), 0) })
            val names = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            names.addView(label(device.name, 16, primaryText, true))
            names.addView(label(if (sockets.containsKey(device.url)) "在线" else "当前离线", 13, secondaryText).apply { setPadding(0, dp(4), 0, 0) })
            row.addView(names, LinearLayout.LayoutParams(0, -2, 1f))
            box.addView(row)
            pageContent.addView(box, marginBottom(10))
        }
        pageContent.addView(label("手机（${trustedPhoneIds.size}）", 17, primaryText, true).apply { setPadding(0, dp(18), 0, dp(8)) })
        if (trustedPhoneIds.isEmpty()) pageContent.addView(label("还没有配对手机。", 14, secondaryText).apply { setPadding(dp(4), 0, 0, dp(12)) })
        trustedPhoneIds.forEach { id ->
            val peer = nearbyPhones[id]
            val name = peer?.name ?: trustedPhoneName(id)
            val box = card()
            val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
            if (deviceEditMode) row.addView(CheckBox(this).apply {
                isChecked = id in selectedPairedPhoneIds
                setOnCheckedChangeListener { _, checked ->
                    if (checked) selectedPairedPhoneIds.add(id) else selectedPairedPhoneIds.remove(id)
                    refreshBulkRemoveButton()
                }
            })
            row.addView(label(if (peer == null) "○" else "●", 18, if (peer == null) secondaryText else 0xff46a874.toInt()).apply { setPadding(0, 0, dp(12), 0) })
            val names = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            names.addView(label(name, 16, primaryText, true))
            names.addView(label(if (peer == null) "当前离线" else "在线", 13, secondaryText).apply { setPadding(0, dp(4), 0, 0) })
            row.addView(names, LinearLayout.LayoutParams(0, -2, 1f))
            box.addView(row)
            pageContent.addView(box, marginBottom(10))
        }
        if (deviceEditMode) pageContent.addView(bulkRemoveButton, marginBottom(14))
    }

    private fun requestDesktopPair(peer: NearbyDesktop) {
        if (!pairingDesktopIds.add(peer.id)) return
        render()
        phoneTransferExecutor.execute {
            try {
                val localAddress = localWifiAddress() ?: throw java.io.IOException("无法获取手机 Wi-Fi 地址，请确认已连接局域网")
                val prefs = getSharedPreferences("nearlink", MODE_PRIVATE)
                val tokenKey = "peerToken:${peer.id}"
                val localToken = prefs.getString(tokenKey, null) ?: newPeerToken().also {
                    prefs.edit().putString(tokenKey, it).apply()
                }
                val body = JSONObject()
                    .put("deviceId", phoneId)
                    .put("name", currentDeviceName())
                    .put("token", localToken)
                    .put("baseUrl", "http://$localAddress:51463")
                    .toString().toRequestBody("application/json; charset=utf-8".toMediaType())
                val request = Request.Builder().url("${peer.url.trimEnd('/')}/api/pair/request").post(body).build()
                val paired = http.newCall(request).execute().use { response ->
                    if (!response.isSuccessful) throw java.io.IOException(when (response.code) {
                        403 -> "对方尚未接受配对请求"
                        429 -> "请求太频繁，请稍后再试"
                        else -> "配对请求失败（${response.code}）"
                    })
                    val result = JSONObject(response.body?.string().orEmpty())
                    val deviceId = result.optString("deviceId")
                    val token = result.optString("token")
                    if (deviceId != peer.id || token.isBlank()) throw java.io.IOException("电脑返回的配对信息无效")
                    Desktop(peer.url, token, result.optString("name", peer.name), phoneId, peer.id)
                }
                main {
                    pairingDesktopIds.remove(peer.id)
                    val existing = devices.indexOfFirst { it.deviceId == peer.id }
                    if (existing >= 0) devices[existing] = paired else devices.add(paired)
                    selected = devices.indexOfFirst { it.deviceId == peer.id }.coerceAtLeast(0)
                    saveDevices()
                    selectedPhoneId = null
                    page = "传输"
                    connectOne(paired)
                    render()
                    toast("已配对 ${paired.name}")
                }
            } catch (error: Exception) {
                main { pairingDesktopIds.remove(peer.id); render(); toast(error.message ?: "配对失败") }
            }
        }
    }

    private fun requestPhonePair(peer: NearbyPhone) {
        if (!pairingPhoneIds.add(peer.id)) return
        render()
        phoneTransferExecutor.execute {
            try {
                val request = JSONObject().put("type", "pair").put("id", UUID.randomUUID().toString())
                    .put("fromId", phoneId).put("fromName", currentDeviceName()).put("toId", peer.id)
                sendPhonePacket(peer, request, null, null)
                val trusted = getSharedPreferences("nearlink", MODE_PRIVATE)
                    .getStringSet("trustedPhoneIds", emptySet()).orEmpty().toMutableSet()
                trusted.add(peer.id)
                getSharedPreferences("nearlink", MODE_PRIVATE).edit().putStringSet("trustedPhoneIds", trusted).apply()
                saveTrustedPhoneName(peer.id, peer.name)
                main {
                    pairingPhoneIds.remove(peer.id)
                    selectedPhoneId = peer.id
                    page = "传输"
                    render()
                    toast("已与 ${peer.name} 配对")
                }
            } catch (error: Exception) {
                main { pairingPhoneIds.remove(peer.id); render(); toast(error.message ?: "配对请求失败") }
            }
        }
    }

    private fun isPhoneTrusted(id: String): Boolean = getSharedPreferences("nearlink", MODE_PRIVATE)
        .getStringSet("trustedPhoneIds", emptySet()).orEmpty().contains(id)

    private fun localWifiAddress(): String? {
        val address = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager).connectionInfo.ipAddress
        if (address == 0) return null
        return (0..3).joinToString(".") { shift -> ((address shr (shift * 8)) and 0xff).toString() }
    }

    private fun newPeerToken(): String = Base64.encodeToString(ByteArray(32).also(SecureRandom()::nextBytes), Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)

    private fun renderTransfer(host: LinearLayout) {
        selectedPhoneId?.let { id ->
            val peer = nearbyPhones[id]
            if (peer != null) renderPhoneTransfer(host, peer) else {
                val back = MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply { text = "‹  附近手机"; isAllCaps = false; setOnClickListener { selectedPhoneId = null; page = "连接"; render() } }
                host.addView(back, marginBottom(8))
                val offline = card(); offline.addView(label("手机暂时离线", 18, primaryText, true)); offline.addView(label("对方重新打开 Nearlink 并连接到同一局域网后，就可以继续传输。", 14, secondaryText).apply { setPadding(0, dp(8), 0, 0) }); host.addView(offline)
            }
            return
        }
        if (devices.isEmpty()) { renderDevices(host); return }
        val current = devices.getOrNull(selected) ?: devices.first()
        val switch = Spinner(this).apply {
            adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, devices.map { it.name + if (sockets.containsKey(it.url)) " · 在线" else "" })
            setSelection(selected.coerceIn(devices.indices))
            onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
                override fun onNothingSelected(parent: android.widget.AdapterView<*>?) {}
                override fun onItemSelected(parent: android.widget.AdapterView<*>?, view: View?, position: Int, id: Long) { if (selected != position) { selected = position; render() } }
            }
        }
        host.addView(switch, marginBottom(8))
        sectionHeader(host, "传输记录", "与 ${current.name} 实时同步")
        val historyScroll = ScrollView(this).apply { clipToPadding = false }
        val history = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        historyScroll.addView(history)
        host.addView(historyScroll, LinearLayout.LayoutParams(-1, 0, 1f))
        val items = histories[current.url] ?: mutableListOf()
        if (items.isEmpty()) history.addView(label("连接成功后，消息和文件会显示在这里。", 14, secondaryText).apply { setPadding(dp(4), dp(18), 0, dp(18)) })
        items.takeLast(120).forEach { item ->
            if (item.optString("kind") == "message") {
                val from = item.optString("from", "设备")
                val box = card()
                box.addView(label(from, 12, secondaryText, true))
                box.addView(copyableMessage(item.optString("text")))
                history.addView(box, marginBottom(9))
            } else if (item.optString("kind") == "file") {
                val box = card()
                val from = item.optString("from", "设备")
                box.addView(label("$from · ${formatSize(item.optLong("size"))}", 12, secondaryText, true))
                val fileName = item.optString("name", "文件")
                val isImage = item.optString("contentType").startsWith("image/") || fileName.matches(Regex("(?i).*\\.(png|jpe?g|gif|webp|bmp|heic)$"))
                if (isImage) {
                    val image = ImageView(this).apply { scaleType = ImageView.ScaleType.CENTER_CROP; setBackgroundColor(subtleFill); contentDescription = fileName }
                    image.setOnClickListener { showImagePreview(image, fileName) }
                    box.addView(image, LinearLayout.LayoutParams(-1, dp(210)).apply { topMargin = dp(10) })
                    loadPreview(current, item, image)
                }
                val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, dp(10), 0, 0) }
                row.addView(fileIcon(fileName), LinearLayout.LayoutParams(dp(30), dp(34)).apply { rightMargin = dp(10) })
                val name = label(fileName, 15, primaryText, true).apply { maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END }
                row.addView(name, LinearLayout.LayoutParams(0, -2, 1f))
                val save = MaterialButton(this).apply { text = "保存"; isAllCaps = false; setOnClickListener { pendingDownload = item; savePicker.launch(fileName) } }
                row.addView(save)
                box.addView(row)
                history.addView(box, marginBottom(9))
            }
        }
        historyScroll.post { historyScroll.fullScroll(View.FOCUS_DOWN) }
        val compose = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(10), dp(8), dp(8), dp(8))
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(cardBackground)
                cornerRadius = dp(18).toFloat()
                setStroke(dp(1), outline)
            }
        }
        val input = TextInputEditText(this).apply {
            hint = "发消息…"
            minLines = 1
            maxLines = 3
            textSize = 14f
            gravity = Gravity.CENTER_VERTICAL or Gravity.START
            imeOptions = EditorInfo.IME_ACTION_SEND
            setTextColor(primaryText)
            setPadding(dp(12), dp(10), dp(8), dp(10))
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(subtleFill)
                cornerRadius = dp(12).toFloat()
            }
        }
        compose.addView(input, LinearLayout.LayoutParams(0, dp(48), 1f).apply { rightMargin = dp(8) })
        val file = TextView(this).apply {
            text = "＋"
            contentDescription = "添加文件"
            gravity = Gravity.CENTER
            textSize = 20f
            setTextColor(if (darkMode) 0xff8ab4ff.toInt() else 0xff245fb8.toInt())
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(cardBackground)
                cornerRadius = dp(14).toFloat()
                setStroke(dp(1), outline)
            }
            setOnClickListener { uploadPicker.launch(arrayOf("*/*")) }
        }
        val send = TextView(this).apply {
            text = "发送"
            gravity = Gravity.CENTER
            textSize = 14f
            setTextColor(Color.WHITE)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setSingleLine(true)
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(if (darkMode) 0xff3478f6.toInt() else 0xff2563eb.toInt())
                cornerRadius = dp(14).toFloat()
            }
            setOnClickListener { sendMessage(input.text?.toString().orEmpty()) }
        }
        input.setOnEditorActionListener { _, action, _ -> if (action == EditorInfo.IME_ACTION_SEND) { sendMessage(input.text?.toString().orEmpty()); true } else false }
        compose.addView(file, LinearLayout.LayoutParams(dp(44), dp(44)).apply { rightMargin = dp(6) })
        compose.addView(send, LinearLayout.LayoutParams(dp(76), dp(44)))
        host.addView(compose, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8); bottomMargin = dp(4) })
    }

    private fun renderPhoneTransfer(host: LinearLayout, peer: NearbyPhone) {
        val back = MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply {
            text = "‹  附近手机"; isAllCaps = false
            setOnClickListener { selectedPhoneId = null; page = "连接"; render() }
        }
        host.addView(back, marginBottom(6))
        sectionHeader(host, peer.name, "手机直连 · 消息和文件不会经过电脑")
        val historyScroll = ScrollView(this).apply { clipToPadding = false; isFillViewport = true }
        val history = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        historyScroll.addView(history)
        host.addView(historyScroll, LinearLayout.LayoutParams(-1, 0, 1f))
        val items = phoneHistories.getOrPut(peer.id) { mutableListOf() }
        if (items.isEmpty()) history.addView(label("向 ${peer.name} 发送消息或文件。", 14, secondaryText).apply { setPadding(dp(4), dp(18), 0, dp(18)) })
        synchronized(items) { items.toList() }.takeLast(120).forEach { item ->
            val box = card()
            box.addView(label(item.optString("from", "设备") + if (item.optBoolean("outgoing")) " · 已发送" else " · 收到", 12, secondaryText, true))
            if (item.optString("kind") == "message") {
                box.addView(copyableMessage(item.optString("text")))
            } else {
                val fileName = item.optString("name", "文件")
                val isImage = item.optString("contentType").startsWith("image/") || fileName.matches(Regex("(?i).*\\.(png|jpe?g|gif|webp|bmp|heic)$"))
                val local = File(item.optString("path"))
                if (isImage && local.isFile) {
                    val image = ImageView(this).apply { scaleType = ImageView.ScaleType.CENTER_CROP; setBackgroundColor(subtleFill); contentDescription = fileName }
                    BitmapFactory.decodeFile(local.absolutePath)?.let(image::setImageBitmap)
                    image.setOnClickListener { showImagePreview(image, fileName) }
                    box.addView(image, LinearLayout.LayoutParams(-1, dp(220)).apply { topMargin = dp(10) })
                }
                val line = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, dp(10), 0, 0) }
                line.addView(fileIcon(fileName), LinearLayout.LayoutParams(dp(30), dp(34)).apply { rightMargin = dp(10) })
                line.addView(label("$fileName\n${formatSize(item.optLong("size"))}", 14, primaryText, true), LinearLayout.LayoutParams(0, -2, 1f))
                if (!item.optBoolean("outgoing")) line.addView(MaterialButton(this).apply { text = "保存"; isAllCaps = false; setOnClickListener { pendingDownload = item; savePicker.launch(fileName) } })
                box.addView(line)
            }
            history.addView(box, marginBottom(9))
        }
        val compose = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(10), dp(8), dp(8), dp(8))
            background = android.graphics.drawable.GradientDrawable().apply { setColor(cardBackground); cornerRadius = dp(18).toFloat(); setStroke(dp(1), outline) }
        }
        val input = TextInputEditText(this).apply {
            hint = "发消息…"; minLines = 1; maxLines = 3; textSize = 14f; gravity = Gravity.CENTER_VERTICAL or Gravity.START
            imeOptions = EditorInfo.IME_ACTION_SEND; setTextColor(primaryText); setPadding(dp(12), dp(10), dp(8), dp(10))
            background = android.graphics.drawable.GradientDrawable().apply { setColor(subtleFill); cornerRadius = dp(12).toFloat() }
        }
        compose.addView(input, LinearLayout.LayoutParams(0, dp(48), 1f).apply { rightMargin = dp(8) })
        val file = TextView(this).apply {
            text = "＋"; contentDescription = "添加文件"; gravity = Gravity.CENTER; textSize = 20f
            setTextColor(if (darkMode) 0xff8ab4ff.toInt() else 0xff245fb8.toInt())
            background = android.graphics.drawable.GradientDrawable().apply { setColor(cardBackground); cornerRadius = dp(14).toFloat(); setStroke(dp(1), outline) }
            setOnClickListener { uploadPicker.launch(arrayOf("*/*")) }
        }
        val send = TextView(this).apply {
            text = "发送"; gravity = Gravity.CENTER; textSize = 14f; setTextColor(Color.WHITE); typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            background = android.graphics.drawable.GradientDrawable().apply { setColor(if (darkMode) 0xff3478f6.toInt() else 0xff2563eb.toInt()); cornerRadius = dp(14).toFloat() }
            setOnClickListener { sendPhoneMessage(peer, input) }
        }
        input.setOnEditorActionListener { _, action, _ -> if (action == EditorInfo.IME_ACTION_SEND) { sendPhoneMessage(peer, input); true } else false }
        compose.addView(file, LinearLayout.LayoutParams(dp(44), dp(44)).apply { rightMargin = dp(6) })
        compose.addView(send, LinearLayout.LayoutParams(dp(76), dp(44)))
        host.addView(compose, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8); bottomMargin = dp(4) })
    }

    private fun showImagePreview(source: ImageView, fileName: String) {
        if (source.drawable == null) return
        val dialog = android.app.Dialog(this)
        val page = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(0xff111318.toInt())
        }
        val titleBar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(16), dp(8), dp(10), dp(8)) }
        titleBar.addView(label(fileName, 14, Color.WHITE, true), LinearLayout.LayoutParams(0, -2, 1f))
        titleBar.addView(TextView(this).apply {
            text = "完成"; gravity = Gravity.CENTER; textSize = 14f; setTextColor(Color.WHITE); setPadding(dp(12), dp(8), dp(12), dp(8))
            setOnClickListener { dialog.dismiss() }
        })
        page.addView(titleBar)
        val stage = FrameLayout(this).apply { setBackgroundColor(0xff111318.toInt()) }
        val enlarged = ImageView(this).apply { setImageDrawable(source.drawable); scaleType = ImageView.ScaleType.FIT_CENTER; contentDescription = fileName }
        stage.addView(enlarged, FrameLayout.LayoutParams(-1, -1))
        val ink = DrawingOverlayView(this) { dialog.dismiss() }
        stage.addView(ink, FrameLayout.LayoutParams(-1, -1))
        page.addView(stage, LinearLayout.LayoutParams(-1, 0, 1f))
        val actions = LinearLayout(this).apply { gravity = Gravity.END or Gravity.CENTER_VERTICAL; setPadding(dp(12), dp(8), dp(12), dp(12)) }
        actions.addView(TextView(this).apply {
            text = "清除涂鸦"; textSize = 13f; gravity = Gravity.CENTER; setTextColor(0xffc5cad2.toInt()); setPadding(dp(12), dp(10), dp(12), dp(10))
            setOnClickListener { ink.clear() }
        })
        actions.addView(MaterialButton(this).apply {
            text = "保存标注图"; isAllCaps = false
            setOnClickListener {
                if (stage.width <= 0 || stage.height <= 0) return@setOnClickListener
                val bitmap = Bitmap.createBitmap(stage.width, stage.height, Bitmap.Config.ARGB_8888)
                stage.draw(Canvas(bitmap))
                pendingAnnotatedBitmap = bitmap
                annotatedImagePicker.launch(File(fileName).nameWithoutExtension + "-标注.png")
            }
        })
        page.addView(actions)
        dialog.setContentView(page)
        dialog.setCancelable(true)
        dialog.window?.apply {
            setBackgroundDrawableResource(android.R.color.transparent)
            setDimAmount(0.72f)
            setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) setBackgroundBlurRadius(dp(24))
        }
        dialog.show()
        dialog.window?.setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
    }

    private fun sendPhoneMessage(peer: NearbyPhone, input: TextInputEditText) {
        val text = input.text?.toString()?.trim().orEmpty()
        if (text.isBlank()) return
        val item = JSONObject().put("type", "message").put("id", UUID.randomUUID().toString()).put("fromId", phoneId).put("fromName", currentDeviceName()).put("toId", peer.id).put("text", text).put("time", System.currentTimeMillis())
        phoneTransferExecutor.execute {
            try { sendPhonePacket(peer, item, null, null); appendPhoneHistory(peer.id, JSONObject(item.toString()).put("kind", "message").put("from", currentDeviceName()).put("outgoing", true)); main { if (selectedPhoneId == peer.id) render() } }
            catch (e: Exception) { toast("发送失败：${e.message ?: "无法连接设备"}") }
        }
    }

    private fun copyableMessage(text: String): TextView = label(text, 16, primaryText).apply {
        setPadding(0, dp(6), 0, 0)
        setTextIsSelectable(true)
        setOnLongClickListener {
            val clipboard = getSystemService(android.content.ClipboardManager::class.java)
            clipboard.setPrimaryClip(android.content.ClipData.newPlainText("Nearlink 消息", text))
            toast("已复制消息")
            true
        }
    }

    private fun confirmRemovePhone(peer: NearbyPhone) = confirmRemovePhone(peer.id, peer.name)

    private fun confirmRemovePhone(peerId: String, peerName: String) {
        AlertDialog.Builder(this)
            .setTitle("移除 $peerName？")
            .setMessage("从本机已配对列表移除，并清除本机聊天记录。之后可重新申请配对。")
            .setNegativeButton("取消", null)
            .setPositiveButton("移除") { _, _ ->
                val prefs = getSharedPreferences("nearlink", MODE_PRIVATE)
                val trusted = prefs.getStringSet("trustedPhoneIds", emptySet()).orEmpty().toMutableSet()
                trusted.remove(peerId)
                prefs.edit().putStringSet("trustedPhoneIds", trusted).apply()
                val names = try { JSONObject(prefs.getString("trustedPhoneNames", "{}") ?: "{}") } catch (_: Exception) { JSONObject() }
                names.remove(peerId)
                prefs.edit().putString("trustedPhoneNames", names.toString()).apply()
                phoneHistories.remove(peerId)
                savePhoneHistories()
                if (selectedPhoneId == peerId) { selectedPhoneId = null; page = "连接" }
                render()
            }
            .show()
    }

    private fun computerSelectionKey(device: Desktop): String = device.deviceId.ifBlank { device.url }

    private fun confirmRemoveSelectedDevices() {
        val computerKeys = selectedPairedComputerIds.toSet()
        val phoneIds = selectedPairedPhoneIds.toSet()
        val computers = devices.filter { computerSelectionKey(it) in computerKeys }
        val phones = phoneIds.map { id -> id to (nearbyPhones[id]?.name ?: trustedPhoneName(id)) }
        if (computers.isEmpty() && phones.isEmpty()) return
        val names = (computers.map { "${it.name} · 电脑" } + phones.map { "${it.second} · 手机" })
        AlertDialog.Builder(this)
            .setTitle("移除所选设备？")
            .setMessage("将从本机已配对列表移除以下设备，并清除本机对应的聊天记录：\n\n${names.joinToString("\n")}\n\n之后可以重新申请配对。")
            .setNegativeButton("取消", null)
            .setPositiveButton("移除 ${names.size} 台") { _, _ ->
                computers.forEach { device ->
                    sockets.remove(device.url)?.close(1000, "removed")
                    histories.remove(device.url)
                }
                val removedKeys = computers.map(::computerSelectionKey).toSet()
                devices.removeAll { computerSelectionKey(it) in removedKeys }
                selected = if (devices.isEmpty()) 0 else selected.coerceIn(0, devices.lastIndex)
                val prefs = getSharedPreferences("nearlink", MODE_PRIVATE)
                val trusted = prefs.getStringSet("trustedPhoneIds", emptySet()).orEmpty().toMutableSet()
                phoneIds.forEach { id ->
                    trusted.remove(id)
                    phoneHistories.remove(id)
                }
                prefs.edit().putStringSet("trustedPhoneIds", trusted).apply()
                val trustedNames = try { JSONObject(prefs.getString("trustedPhoneNames", "{}") ?: "{}") } catch (_: Exception) { JSONObject() }
                phoneIds.forEach { trustedNames.remove(it) }
                prefs.edit().putString("trustedPhoneNames", trustedNames.toString()).apply()
                savePhoneHistories()
                if (selectedPhoneId?.let(phoneIds::contains) == true) {
                    selectedPhoneId = null
                    page = "连接"
                }
                selectedPairedComputerIds.clear()
                selectedPairedPhoneIds.clear()
                saveDevices()
                render()
                updateStatus()
            }
            .show()
    }

    private fun trustedPhoneName(id: String): String {
        val names = try { JSONObject(getSharedPreferences("nearlink", MODE_PRIVATE).getString("trustedPhoneNames", "{}") ?: "{}") } catch (_: Exception) { JSONObject() }
        return names.optString(id).ifBlank { "手机设备 · ${id.take(8)}" }
    }

    private fun saveTrustedPhoneName(id: String, name: String) {
        val prefs = getSharedPreferences("nearlink", MODE_PRIVATE)
        val names = try { JSONObject(prefs.getString("trustedPhoneNames", "{}") ?: "{}") } catch (_: Exception) { JSONObject() }
        names.put(id, name)
        prefs.edit().putString("trustedPhoneNames", names.toString()).apply()
    }

    private fun approvePhonePeer(peer: NearbyPhone): Boolean {
        val prefs = getSharedPreferences("nearlink", MODE_PRIVATE)
        if (prefs.getStringSet("trustedPhoneIds", emptySet()).orEmpty().contains(peer.id)) return true
        val gate = CountDownLatch(1)
        val existing = pendingPhoneApprovals.putIfAbsent(peer.id, gate)
        if (existing != null) return existing.await(60, java.util.concurrent.TimeUnit.SECONDS) &&
            getSharedPreferences("nearlink", MODE_PRIVATE).getStringSet("trustedPhoneIds", emptySet()).orEmpty().contains(peer.id)
        handler.post {
            if (destroyed || isFinishing) { gate.countDown(); return@post }
            val dialog = AlertDialog.Builder(this)
                .setTitle("允许手机连接？")
                .setMessage("${peer.name} 想与你建立手机直连。允许后可互相发送消息和文件。")
                .setNegativeButton("拒绝") { _, _ -> gate.countDown() }
                .setPositiveButton("允许") { _, _ ->
                    val accepted = prefs.getStringSet("trustedPhoneIds", emptySet()).orEmpty().toMutableSet()
                    accepted.add(peer.id)
                    prefs.edit().putStringSet("trustedPhoneIds", accepted).apply()
                    saveTrustedPhoneName(peer.id, peer.name)
                    gate.countDown()
                }
                .create()
            dialog.setOnCancelListener { gate.countDown() }
            dialog.show()
        }
        val completed = gate.await(60, java.util.concurrent.TimeUnit.SECONDS)
        pendingPhoneApprovals.remove(peer.id, gate)
        return completed && prefs.getStringSet("trustedPhoneIds", emptySet()).orEmpty().contains(peer.id)
    }

    private fun uploadToPhone(uri: Uri) {
        val peer = selectedPhoneId?.let { nearbyPhones[it] } ?: return upload(uri)
        val name = queryName(uri) ?: "文件"
        val size = contentResolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1L
        if (size <= 0L || size > 1024L * 1024 * 1024) { toast("文件大小无效或超过 1 GB"); return }
        val type = contentResolver.getType(uri) ?: "application/octet-stream"
        phoneTransferExecutor.execute {
            try {
                contentResolver.openInputStream(uri)?.use { stream ->
                    val item = JSONObject().put("type", "file").put("id", UUID.randomUUID().toString()).put("fromId", phoneId).put("fromName", currentDeviceName()).put("toId", peer.id).put("name", name).put("contentType", type).put("size", size).put("time", System.currentTimeMillis())
                    sendPhonePacket(peer, item, stream, size)
                } ?: throw java.io.IOException("无法读取文件")
                val record = JSONObject().put("id", UUID.randomUUID().toString()).put("kind", "file").put("from", currentDeviceName()).put("name", name).put("contentType", type).put("size", size).put("outgoing", true)
                appendPhoneHistory(peer.id, record); main { if (selectedPhoneId == peer.id) render() }
            } catch (e: Exception) { toast("发送失败：${e.message ?: "无法连接设备"}") }
        }
    }

    private fun sendPhonePacket(peer: NearbyPhone, header: JSONObject, stream: InputStream?, size: Long?) {
        Socket().use { socket ->
            socket.connect(java.net.InetSocketAddress(peer.address, peer.port), 5000); socket.soTimeout = if (header.optString("type") == "pair") 65000 else 15000
            val output = DataOutputStream(socket.getOutputStream()); val bytes = header.toString().toByteArray(Charsets.UTF_8)
            output.writeInt(bytes.size); output.write(bytes)
            if (stream != null && size != null) { val buffer = ByteArray(64 * 1024); var remaining = size; while (remaining > 0) { val n = stream.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt()); if (n < 0) throw java.io.IOException("文件读取提前结束"); output.write(buffer, 0, n); remaining -= n }; output.flush() }
            val input = DataInputStream(socket.getInputStream()); val length = input.readInt(); if (length !in 1..8192) throw java.io.IOException("无效响应")
            val response = ByteArray(length); input.readFully(response); val ack = JSONObject(String(response, Charsets.UTF_8))
            if (!ack.optBoolean("ok")) throw java.io.IOException(ack.optString("error", "对方未接收"))
        }
    }

    private fun connectAll() {
        devices.forEach { device ->
            val uri = URI(device.url)
            val builder = HttpUrl.Builder().scheme(uri.scheme).host(uri.host).port(if (uri.port > 0) uri.port else if (uri.scheme == "https") 443 else 80).addPathSegments("ws")
                .addQueryParameter("code", device.code).addQueryParameter("session", device.session).addQueryParameter("name", currentDeviceName()).addQueryParameter("kind", "phone")
            http.newWebSocket(Request.Builder().url(builder.build()).build(), object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    sockets[device.url] = webSocket
                    device.deviceId.takeIf { it.isNotBlank() }?.let { nearbyDesktops[it] = NearbyDesktop(it, device.name, device.url, System.currentTimeMillis()) }
                    refreshIdentity(device)
                    main { updateStatus(); if (page == "连接" || (devices.getOrNull(selected)?.url == device.url)) render() }
                }
                override fun onMessage(webSocket: WebSocket, text: String) { receive(device, text) }
                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { sockets.remove(device.url, webSocket); main { updateStatus(); render() }; reconnect(device) }
                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) { sockets.remove(device.url, webSocket); main { updateStatus(); render() }; reconnect(device) }
            })
        }
        updateStatus()
    }

    private fun startDiscovery() {
        try {
            val wifi = applicationContext.getSystemService(WIFI_SERVICE) as WifiManager
            multicastLock = wifi.createMulticastLock("nearlink-discovery").apply { setReferenceCounted(false); acquire() }
            discoveryThread = Thread {
                try {
                    MulticastSocket(51462).use { socket ->
                        socket.reuseAddress = true
                        socket.soTimeout = 1500
                        val group = InetAddress.getByName("239.255.42.99")
                        socket.joinGroup(group)
                        val target = java.net.InetSocketAddress(group, 51462)
                        var lastBeacon = 0L
                        while (!destroyed && !Thread.currentThread().isInterrupted) {
                            if (System.currentTimeMillis() - lastBeacon >= 1800) {
                                try {
                                    val beacon = JSONObject().put("service", "nearlink-phone").put("deviceId", phoneId).put("name", currentDeviceName()).put("port", 51463).toString().toByteArray(Charsets.UTF_8)
                                    socket.send(DatagramPacket(beacon, beacon.size, target)); lastBeacon = System.currentTimeMillis()
                                } catch (_: Exception) { }
                            }
                            try {
                                val bytes = ByteArray(2048)
                                val packet = DatagramPacket(bytes, bytes.size)
                                socket.receive(packet)
                                val beacon = JSONObject(String(packet.data, 0, packet.length, Charsets.UTF_8))
                                if (beacon.optString("service") == "nearlink-phone") {
                                    val id = beacon.optString("deviceId")
                                    val name = beacon.optString("name", "Nearby phone")
                                    val port = beacon.optInt("port", 51463)
                                    if (id.isBlank() || id == phoneId || port !in 1..65535) continue
                                    val peer = NearbyPhone(id, name, packet.address.hostAddress.orEmpty(), port, System.currentTimeMillis())
                                    handler.post {
                                        val old = nearbyPhones[id]
                                        nearbyPhones[id] = peer
                                        if (old == null || old.name != name || old.address != peer.address) {
                                            if (page == "连接" || (page == "传输" && selectedPhoneId == id)) render()
                                        }
                                    }
                                    continue
                                }
                                if (beacon.optString("service") != "nearlink") continue
                                val id = beacon.optString("deviceId")
                                val url = beacon.optString("baseUrl")
                                val uri = try { URI(url) } catch (_: Exception) { null }
                                if (id.isBlank() || id == phoneId || uri == null || uri.scheme != "http" || uri.host.isNullOrBlank() || uri.port !in 1..65535) continue
                                handler.post {
                                    val nearby = NearbyDesktop(id, beacon.optString("name", "Nearlink 电脑"), url.trimEnd('/'), System.currentTimeMillis())
                                    val oldNearby = nearbyDesktops.put(id, nearby)
                                    val desktopChanged = oldNearby == null || oldNearby.name != nearby.name || oldNearby.url != nearby.url
                                    val index = devices.indexOfFirst { it.deviceId == id }
                                    if (index >= 0 && (devices[index].url != nearby.url || devices[index].name != nearby.name)) {
                                        val old = devices[index]
                                        if (old.url != nearby.url) sockets.remove(old.url)?.close(1000, "address changed")
                                        val updated = old.copy(url = nearby.url, name = nearby.name)
                                        devices[index] = updated
                                        saveDevices()
                                        if (old.url != nearby.url) connectOne(updated)
                                    }
                                    if (desktopChanged && page == "连接") render()
                                    updateStatus()
                                }
                            } catch (_: java.net.SocketTimeoutException) { }
                            handler.post {
                                val cutoff = System.currentTimeMillis() - 7000
                                val removedPhones = nearbyPhones.entries.removeIf { it.value.lastSeen < cutoff }
                                val removedDesktops = nearbyDesktops.entries.removeIf { (id, peer) ->
                                    peer.lastSeen < cutoff && devices.none { it.deviceId == id && sockets.containsKey(it.url) }
                                }
                                val removed = removedPhones || removedDesktops
                                if (removed && (page == "连接" || selectedPhoneId?.let { !nearbyPhones.containsKey(it) } == true)) render()
                                if (removed) updateStatus()
                            }
                        }
                        socket.leaveGroup(group)
                    }
                } catch (error: Exception) { Log.e("NearlinkDiscovery", "LAN discovery stopped", error) }
            }.apply { name = "Nearlink LAN discovery"; isDaemon = true; start() }
        } catch (_: Exception) { }
    }

    private fun startPhoneServer() {
        phoneServerThread = Thread({
            try {
                val server = ServerSocket(51463, 16, InetAddress.getByName("0.0.0.0"))
                phoneServer = server
                while (!destroyed && !Thread.currentThread().isInterrupted) {
                    val client = server.accept()
                    phoneTransferExecutor.execute { handlePhoneClient(client) }
                }
            } catch (_: Exception) { }
        }, "Nearlink phone direct server").apply { isDaemon = true; start() }
    }

    private fun handlePhoneClient(socket: Socket) {
        socket.use { client ->
            client.soTimeout = 30000
            val input = DataInputStream(client.getInputStream()); val output = DataOutputStream(client.getOutputStream())
            try {
                val headerLength = input.readInt()
                if (headerLength !in 1..16384) throw java.io.IOException("无效请求")
                val raw = ByteArray(headerLength); input.readFully(raw); val header = JSONObject(String(raw, Charsets.UTF_8))
                val fromId = header.optString("fromId"); val toId = header.optString("toId")
                val known = nearbyPhones[fromId]
                if (toId != phoneId || known == null || known.address != client.inetAddress.hostAddress) throw java.io.IOException("设备身份不匹配")
                if (!approvePhonePeer(known)) throw java.io.IOException("未获准连接")
                val kind = header.optString("type")
                if (kind == "pair") {
                    val ack = JSONObject().put("ok", true).toString().toByteArray(Charsets.UTF_8)
                    output.writeInt(ack.size); output.write(ack); output.flush()
                    main { if (page == "连接" || (page == "传输" && selectedPhoneId == fromId)) render() }
                    return@use
                }
                val record: JSONObject
                if (kind == "message") {
                    val text = header.optString("text").take(10000)
                    if (text.isBlank()) throw java.io.IOException("消息为空")
                    record = JSONObject().put("id", header.optString("id", UUID.randomUUID().toString())).put("kind", "message").put("from", known.name).put("fromId", fromId).put("text", text).put("time", header.optLong("time", System.currentTimeMillis())).put("outgoing", false)
                } else if (kind == "file") {
                    val size = header.optLong("size", -1)
                    if (size !in 1..(1024L * 1024 * 1024)) throw java.io.IOException("文件大小无效")
                    val displayName = File(header.optString("name", "文件")).name.take(180).ifBlank { "文件" }
                    val directory = File(filesDir, "phone-transfers").apply { mkdirs() }
                    val target = File(directory, "${System.currentTimeMillis()}-${UUID.randomUUID()}-$displayName")
                    FileOutputStream(target).use { out ->
                        val buffer = ByteArray(64 * 1024); var remaining = size
                        while (remaining > 0) { val n = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt()); if (n < 0) throw java.io.IOException("文件传输中断"); out.write(buffer, 0, n); remaining -= n }
                    }
                    record = JSONObject().put("id", header.optString("id", UUID.randomUUID().toString())).put("kind", "file").put("from", known.name).put("fromId", fromId).put("name", displayName).put("contentType", header.optString("contentType", "application/octet-stream")).put("size", size).put("path", target.absolutePath).put("time", header.optLong("time", System.currentTimeMillis())).put("outgoing", false)
                } else throw java.io.IOException("不支持的传输类型")
                appendPhoneHistory(fromId, record)
                if (kind == "message") notifyReceivedMessage(known.name, record.optString("text"), record.optString("id"), phonePeerId = fromId)
                val ack = JSONObject().put("ok", true).toString().toByteArray(Charsets.UTF_8); output.writeInt(ack.size); output.write(ack); output.flush()
                main { if (page == "连接" || (page == "传输" && selectedPhoneId == fromId)) render() }
            } catch (e: Exception) {
                try { val error = JSONObject().put("ok", false).put("error", e.message ?: "接收失败").toString().toByteArray(Charsets.UTF_8); output.writeInt(error.size); output.write(error); output.flush() } catch (_: Exception) { }
            }
        }
    }

    private fun appendPhoneHistory(peerId: String, item: JSONObject) {
        val list = phoneHistories.getOrPut(peerId) { mutableListOf() }
        synchronized(list) { if (list.none { it.optString("id") == item.optString("id") }) { list.add(item); while (list.size > 400) list.removeAt(0) } }
        savePhoneHistories()
    }

    private fun loadPhoneHistories() {
        try {
            val saved = JSONArray(getSharedPreferences("nearlink", MODE_PRIVATE).getString("phoneHistories", "{}") ?: "{}")
            for (i in 0 until saved.length()) { val o = saved.getJSONObject(i); val arr = o.optJSONArray("items") ?: continue; val list = mutableListOf<JSONObject>(); for (j in 0 until arr.length()) list.add(arr.getJSONObject(j)); phoneHistories[o.getString("peerId")] = list }
        } catch (_: Exception) { }
    }

    private fun savePhoneHistories() {
        val saved = JSONArray()
        phoneHistories.forEach { (peerId, items) -> val arr = JSONArray(); synchronized(items) { items.forEach { arr.put(it) } }; saved.put(JSONObject().put("peerId", peerId).put("items", arr)) }
        getSharedPreferences("nearlink", MODE_PRIVATE).edit().putString("phoneHistories", saved.toString()).apply()
    }

    private fun reconnect(device: Desktop) {
        handler.postDelayed({ if (!destroyed && !isFinishing && devices.any { it.url == device.url } && !sockets.containsKey(device.url)) connectOne(device) }, 2500)
    }

    private fun connectOne(device: Desktop) {
        try {
            val uri = URI(device.url)
            val builder = HttpUrl.Builder().scheme(uri.scheme).host(uri.host).port(if (uri.port > 0) uri.port else if (uri.scheme == "https") 443 else 80).addPathSegments("ws")
                .addQueryParameter("code", device.code).addQueryParameter("session", device.session).addQueryParameter("name", currentDeviceName()).addQueryParameter("kind", "phone")
            http.newWebSocket(Request.Builder().url(builder.build()).build(), socketListener(device))
        } catch (_: Exception) { reconnect(device) }
    }

    private fun socketListener(device: Desktop) = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            sockets[device.url] = webSocket
            device.deviceId.takeIf { it.isNotBlank() }?.let { nearbyDesktops[it] = NearbyDesktop(it, device.name, device.url, System.currentTimeMillis()) }
            refreshIdentity(device)
            main { updateStatus(); render() }
        }
        override fun onMessage(webSocket: WebSocket, text: String) { receive(device, text) }
        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { sockets.remove(device.url, webSocket); main { updateStatus(); render() }; reconnect(device) }
        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) { sockets.remove(device.url, webSocket); main { updateStatus(); render() }; reconnect(device) }
    }

    private fun refreshIdentity(device: Desktop) {
        val url = endpoint(device, "/api/target")?.newBuilder()?.addQueryParameter("code", device.code)?.build() ?: return
        http.newCall(Request.Builder().url(url).build()).enqueue(object : Callback {
            override fun onFailure(call: Call, e: java.io.IOException) { }
            override fun onResponse(call: Call, response: Response) { response.use {
                if (!it.isSuccessful) return
                try {
                    val target = JSONObject(it.body?.string().orEmpty())
                    val id = target.optString("deviceId")
                    if (id.isBlank()) return
                    main {
                        val index = devices.indexOfFirst { item -> item.url == device.url && item.code == device.code }
                        if (index >= 0) {
                            val updatedName = target.optString("name", devices[index].name)
                            devices[index] = devices[index].copy(deviceId = id, name = updatedName)
                            nearbyDesktops[id] = NearbyDesktop(id, updatedName, device.url, System.currentTimeMillis())
                            saveDevices()
                            if (page == "连接") render()
                        }
                    }
                } catch (_: Exception) { }
            } }
        })
    }

    private fun receive(device: Desktop, text: String) {
        try {
            val event = JSONObject(text)
            if (event.optString("type") == "presence") {
                val current = event.optJSONArray("devices")
                val remote = (0 until (current?.length() ?: 0)).asSequence()
                    .map { current!!.getJSONObject(it) }
                    .firstOrNull { it.optString("id") == device.deviceId }
                val updatedName = remote?.optString("name")
                if (!updatedName.isNullOrBlank()) main {
                    val index = devices.indexOfFirst { it.url == device.url && it.code == device.code }
                    if (index >= 0 && devices[index].name != updatedName) {
                        devices[index] = devices[index].copy(name = updatedName)
                        saveDevices()
                        if (page == "连接") render()
                    }
                }
            }
            val list = histories.getOrPut(device.url) { mutableListOf() }
            var incomingMessage: JSONObject? = null
            synchronized(list) {
                when (event.optString("type")) {
                    "history" -> event.optJSONArray("items")?.let { batch -> for (i in 0 until batch.length()) addUnique(list, batch.getJSONObject(i)) }
                    "message", "file" -> event.optJSONObject("item")?.let { item ->
                        val isNew = list.none { it.optString("id") == item.optString("id") }
                        addUnique(list, item)
                        val fromId = item.optString("fromId")
                        val isOwnMessage = fromId == phoneId
                        if (event.optString("type") == "message" && isNew && !isOwnMessage) incomingMessage = JSONObject(item.toString())
                    }
                }
            }
            incomingMessage?.let { notifyReceivedMessage(it.optString("from", device.name), it.optString("text"), it.optString("id"), desktopUrl = device.url) }
            main { if (devices.getOrNull(selected)?.url == device.url && page == "传输") render() }
        } catch (_: Exception) { }
    }

    private fun addUnique(list: MutableList<JSONObject>, item: JSONObject) { if (list.none { it.optString("id") == item.optString("id") }) list.add(item) }

    private fun sendMessage(text: String) {
        val clean = text.trim(); if (clean.isEmpty()) return
        val device = devices.getOrNull(selected) ?: return
        val edit = content?.let(::findEditText)
        val base = endpoint(device, "/api/message") ?: return
        val url = base.newBuilder().addQueryParameter("code", device.code).addQueryParameter("from", currentDeviceName()).addQueryParameter("fromId", phoneId).addQueryParameter("session", device.session).build()
        val body = JSONObject().put("text", clean).toString().toRequestBody("application/json; charset=utf-8".toMediaType())
        http.newCall(Request.Builder().url(url).post(body).build()).enqueue(object : Callback {
            override fun onFailure(call: Call, e: java.io.IOException) { main { toast("发送失败：${e.message}") } }
            override fun onResponse(call: Call, response: Response) { response.use { if (it.isSuccessful) { main { edit?.let { field -> if (field.text?.toString()?.trim() == clean) field.setText("") } } } else main { toast(if (it.code == 403) "消息发送失败（电脑未授权）" else "发送失败 (${it.code})") } } }
        })
    }

    private fun findEditText(view: View): TextInputEditText? {
        if (view is TextInputEditText) return view
        if (view is ViewGroup) for (i in 0 until view.childCount) findEditText(view.getChildAt(i))?.let { return it }
        return null
    }

    private fun upload(uri: Uri) {
        val device = devices.getOrNull(selected) ?: return
        val name = queryName(uri) ?: "文件"
        val length = contentResolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1L
        if (length <= 0L) { toast("无法读取文件大小，暂时不能发送"); return }
        if (length > 1024L * 1024 * 1024) { toast("单个文件不能超过 1 GB"); return }
        val transferId = UUID.randomUUID().toString().replace("-", "")
        val approvalBase = endpoint(device, "/api/transfer/request") ?: return
        val approvalUrl = approvalBase.newBuilder().addQueryParameter("code", device.code).addQueryParameter("session", device.session).build()
        val approvalBody = JSONObject().put("sessionId", device.session).put("from", currentDeviceName()).put("name", name).put("size", length).put("transferId", transferId)
            .toString().toRequestBody("application/json; charset=utf-8".toMediaType())
        http.newCall(Request.Builder().url(approvalUrl).post(approvalBody).build()).enqueue(object : Callback {
            override fun onFailure(call: Call, e: java.io.IOException) { main { toast("接收请求失败：${e.message}") } }
            override fun onResponse(call: Call, response: Response) {
                val grant = response.use {
                    if (!it.isSuccessful) { main { toast(if (it.code == 403) "电脑暂未接收 $name" else "接收请求失败 (${it.code})") }; return }
                    try {
                        JSONObject(it.body?.string().orEmpty()).let { body ->
                            val ticket = body.optString("ticket")
                            val grantedId = body.optString("transferId")
                            if (ticket.isNotBlank() && grantedId == transferId) ticket else null
                        }
                    }
                    catch (_: Exception) { null }
                } ?: run { main { toast("电脑返回了无效的接收许可") }; return }
                phoneTransferExecutor.execute {
                    try { uploadResumableFile(device, uri, name, length, transferId, grant) }
                    catch (error: Exception) { toast("发送失败：${error.message ?: "网络中断"}") }
                }
            }
        })
    }

    private fun uploadResumableFile(device: Desktop, uri: Uri, name: String, size: Long, transferId: String, ticket: String) {
        val digest = java.security.MessageDigest.getInstance("SHA-256")
        contentResolver.openInputStream(uri)?.use { input ->
            val hashBuffer = ByteArray(128 * 1024)
            while (true) { val read = input.read(hashBuffer); if (read < 0) break; digest.update(hashBuffer, 0, read) }
        } ?: throw java.io.IOException("无法读取文件")
        val sha256 = digest.digest().joinToString("") { "%02X".format(it) }

        fun uploadUrl(action: String, offset: Long? = null): okhttp3.HttpUrl {
            val path = "/api/upload/$transferId/$action"
            val base = endpoint(device, path) ?: throw java.io.IOException("电脑地址无效")
            return base.newBuilder().addQueryParameter("code", device.code)
                .addQueryParameter("session", device.session).addQueryParameter("from", currentDeviceName())
                .addQueryParameter("ticket", ticket).addQueryParameter("name", name)
                .addQueryParameter("size", size.toString()).addQueryParameter("sha256", sha256)
                .apply { if (offset != null) addQueryParameter("offset", offset.toString()) }.build()
        }

        fun status(): Pair<Long, Boolean> {
            var lastError: Exception? = null
            for (attempt in 1..4) {
                try {
                    http.newCall(Request.Builder().url(uploadUrl("status")).get().build()).execute().use { response ->
                        if (response.code == 408 || response.code == 429 || response.code >= 500) {
                            if (attempt == 4) throw java.io.IOException("断点查询失败 (${response.code})")
                            Thread.sleep(250L shl (attempt - 1)); return@use
                        }
                        if (!response.isSuccessful) throw java.io.IOException("断点查询失败 (${response.code})")
                        val body = JSONObject(response.body?.string().orEmpty())
                        return body.optLong("offset", -1L) to body.optBoolean("completed", false)
                    }
                } catch (error: Exception) {
                    lastError = error
                    if (attempt == 4) break
                    Thread.sleep(250L shl (attempt - 1))
                }
            }
            throw java.io.IOException("无法读取断点状态", lastError)
        }

        var (offset, completed) = status()
        if (completed) return
        if (offset !in 0..size) throw java.io.IOException("电脑返回的断点位置无效")
        val input = contentResolver.openInputStream(uri) ?: throw java.io.IOException("无法读取文件")
        input.use { stream ->
            var skipped = 0L
            while (skipped < offset) {
                val n = stream.skip(offset - skipped)
                if (n > 0) skipped += n else { if (stream.read() < 0) throw java.io.IOException("文件读取位置无效"); skipped++ }
            }

            val buffer = ByteArray(4 * 1024 * 1024)
            while (offset < size) {
                val count = minOf(buffer.size.toLong(), size - offset).toInt()
                var filled = 0
                while (filled < count) {
                    val n = stream.read(buffer, filled, count - filled)
                    if (n < 0) throw java.io.IOException("文件读取不完整")
                    filled += n
                }

                var advanced = false
                var lastError: Exception? = null
                for (attempt in 1..4) {
                    try {
                        val chunk = buffer.copyOf(count).toRequestBody("application/octet-stream".toMediaType())
                        http.newCall(Request.Builder().url(uploadUrl("chunk", offset)).put(chunk).build()).execute().use { response ->
                            if (response.code == 408 || response.code == 429 || response.code >= 500) {
                                lastError = java.io.IOException("临时传输错误 (${response.code})")
                            } else {
                                if (!response.isSuccessful) throw java.io.IOException("发送失败 (${response.code})")
                                val next = JSONObject(response.body?.string().orEmpty()).optLong("offset", -1L)
                                if (next !in (offset + 1)..size) throw java.io.IOException("电脑返回的断点位置无效")
                                offset = next
                                advanced = true
                            }
                        }
                    } catch (error: Exception) { lastError = error }
                    if (!advanced && attempt < 4) {
                        Thread.sleep(250L shl (attempt - 1))
                        val state = status()
                        if (state.second) return
                        if (state.first > offset) { offset = state.first; advanced = true }
                    }
                    if (advanced) break
                }
                if (!advanced) throw java.io.IOException("数据块重试失败", lastError)
            }
        }

        var lastError: Exception? = null
        for (attempt in 1..4) {
            try {
                http.newCall(Request.Builder().url(uploadUrl("complete")).post(ByteArray(0).toRequestBody()).build()).execute().use { response ->
                    if (!response.isSuccessful) throw java.io.IOException("文件校验或保存失败 (${response.code})")
                }
                return
            } catch (error: Exception) {
                lastError = error
                if (attempt < 4) Thread.sleep(250L shl (attempt - 1))
            }
        }
        throw java.io.IOException("文件入库未确认", lastError)
    }

    private fun loadPreview(device: Desktop, item: JSONObject, view: ImageView) {
        val base = endpoint(device, "/api/preview/${encode(item.optString("id"))}") ?: return
        val url = base.newBuilder().addQueryParameter("code", device.code).addQueryParameter("session", device.session).build()
        http.newCall(Request.Builder().url(url).build()).enqueue(object : Callback {
            override fun onFailure(call: Call, e: java.io.IOException) {}
            override fun onResponse(call: Call, response: Response) { response.use { if (it.isSuccessful) { val bitmap = it.body?.byteStream()?.use { input -> BitmapFactory.decodeStream(input) }; if (bitmap != null) main { view.setImageBitmap(bitmap); view.alpha = 0f; view.animate().alpha(1f).setDuration(180).start() } } } }
        })
    }

    private fun download(item: JSONObject, destination: Uri) {
        if (item.has("path")) {
            val source = File(item.optString("path"))
            phoneTransferExecutor.execute {
                try {
                    if (!source.isFile) throw java.io.IOException("接收的文件已不存在")
                    FileInputStream(source).use { input -> contentResolver.openOutputStream(destination)?.use { output -> input.copyTo(output, 128 * 1024) } ?: throw java.io.IOException("无法写入目标位置") }
                } catch (e: Exception) { toast("保存失败：${e.message}") }
            }
            return
        }
        val device = devices.getOrNull(selected) ?: return
        val fileName = item.optString("name", "Nearlink 文件")
        val base = endpoint(device, "/api/file/${encode(item.optString("id"))}") ?: return
        val url = base.newBuilder().addQueryParameter("code", device.code).addQueryParameter("name", fileName).addQueryParameter("session", device.session).build()
        http.newCall(Request.Builder().url(url).build()).enqueue(object : Callback {
            override fun onFailure(call: Call, e: java.io.IOException) { main { toast("保存失败：${e.message}") } }
            override fun onResponse(call: Call, response: Response) { response.use { if (!it.isSuccessful) { main { toast("保存失败 (${it.code})") }; return }
                try { val input = it.body?.byteStream() ?: throw java.io.IOException("文件为空"); contentResolver.openOutputStream(destination)?.use { out -> input.use { stream -> stream.copyTo(out, 128 * 1024) } } ?: throw java.io.IOException("无法写入目标位置") }
                catch (e: Exception) { main { toast("保存失败：${e.message}") } }
            } }
        })
    }

    private fun addDesktopFromLink(link: String) {
        try {
            val uri = URI(link.trim())
            val query = uri.rawQuery.orEmpty().split('&').mapNotNull { part -> val p = part.split('=', limit = 2); if (p.size == 2) URLDecoder.decode(p[0], "UTF-8") to URLDecoder.decode(p[1], "UTF-8") else null }.toMap()
            val code = query["code"] ?: throw IllegalArgumentException("链接中没有配对码")
            if (uri.scheme !in listOf("http", "https") || uri.host.isNullOrBlank()) throw IllegalArgumentException("请输入有效的 Nearlink 连接链接")
            val baseUrl = "${uri.scheme}://${uri.authority}"
            val existing = devices.indexOfFirst { it.url == baseUrl && it.code == code }
            if (existing >= 0) {
                selected = existing
                page = "传输"
                if (!sockets.containsKey(devices[existing].url)) connectOne(devices[existing])
                saveDevices()
                render()
                toast("正在连接 ${devices[existing].name}")
                return
            }

            val desktop = Desktop(baseUrl, code, "Nearlink 电脑 ${uri.host}", UUID.randomUUID().toString(), query["deviceId"].orEmpty())
            devices.add(desktop)
            selected = devices.lastIndex
            saveDevices()
            page = "传输"
            render()
            connectOne(desktop)
            toast("已添加设备，正在连接")
        } catch (e: Exception) { toast(e.message ?: "二维码内容无效") }
    }

    private fun confirmRemove(index: Int) {
        AlertDialog.Builder(this).setTitle("移除电脑？").setMessage("${devices[index].name} 将从本机列表移除。").setNegativeButton("取消", null).setPositiveButton("移除") { _, _ ->
            val removed = devices.removeAt(index); sockets.remove(removed.url)?.close(1000, "removed"); histories.remove(removed.url); selected = if (devices.isEmpty()) 0 else selected.coerceAtMost(devices.lastIndex); saveDevices(); render(); updateStatus()
        }.show()
    }

    private fun endpoint(device: Desktop, path: String): HttpUrl? = try { val uri = URI(device.url); HttpUrl.Builder().scheme(uri.scheme).host(uri.host).port(if (uri.port > 0) uri.port else if (uri.scheme == "https") 443 else 80).encodedPath(path).build() } catch (_: Exception) { null }
    private fun queryName(uri: Uri): String? = contentResolver.query(uri, null, null, null, null)?.use { c -> if (c.moveToFirst()) c.getString(c.getColumnIndexOrThrow(OpenableColumns.DISPLAY_NAME)) else null }
    private fun loadDevices() { val raw = getSharedPreferences("nearlink", MODE_PRIVATE).getString("devices", "[]") ?: "[]"; try { val arr = JSONArray(raw); for (i in 0 until arr.length()) { val o = arr.getJSONObject(i); val oldUrl = o.getString("url"); val parsed = URI(oldUrl); val url = if (parsed.port > 0 && parsed.port != 51461) "${parsed.scheme}://${parsed.host}:51461" else oldUrl.trimEnd('/'); devices.add(Desktop(url, o.getString("code"), o.getString("name"), o.optString("session").ifBlank { UUID.randomUUID().toString() }, o.optString("deviceId"))) }; selected = getSharedPreferences("nearlink", MODE_PRIVATE).getInt("selected", 0).coerceIn(0, (devices.size - 1).coerceAtLeast(0)); saveDevices() } catch (_: Exception) { devices.clear() } }
    private fun currentDeviceName(): String = getSharedPreferences("nearlink", MODE_PRIVATE)
        .getString("deviceName", null)?.trim()?.takeIf(String::isNotBlank)
        ?: android.os.Build.MODEL.takeIf(String::isNotBlank)?.take(32)
        ?: "Android 手机"
    private fun saveDevices() { val arr = JSONArray(); devices.forEach { arr.put(JSONObject().put("url", it.url).put("code", it.code).put("name", it.name).put("session", it.session).put("deviceId", it.deviceId)) }; getSharedPreferences("nearlink", MODE_PRIVATE).edit().putString("devices", arr.toString()).putInt("selected", selected).apply() }
    private fun updateStatus() {
        val online = sockets.size
        val pairedIds = devices.mapNotNull { it.deviceId.takeIf(String::isNotBlank) }.toSet()
        val discovered = nearbyDesktops.keys.count { it !in pairedIds } + nearbyPhones.keys.count { it != phoneId }
        main { statusText?.text = when {
            online + discovered > 0 -> "${online + discovered} 台设备在线 · 局域网直连"
            devices.isEmpty() -> "正在扫描附近设备"
            else -> "设备离线 · 正在尝试重连"
        } }
    }
    private fun sectionHeader(parent: LinearLayout, title: String, subtitle: String) { parent.addView(label(title, 21, primaryText, true).apply { setPadding(0, dp(12), 0, dp(3)) }); parent.addView(label(subtitle, 13, secondaryText).apply { setPadding(0, 0, 0, dp(14)) }) }
    private fun card(): LinearLayout = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(16)); background = android.graphics.drawable.GradientDrawable().apply { setColor(cardBackground); cornerRadius = dp(18).toFloat(); setStroke(dp(1), outline) } }
    private fun fileIcon(fileName: String): ImageView = ImageView(this).apply {
        setImageResource(R.drawable.ic_file)
        imageTintList = android.content.res.ColorStateList.valueOf(if (darkMode) 0xff8ab4ff.toInt() else 0xff245fb8.toInt())
        contentDescription = "文件：$fileName"
        scaleType = ImageView.ScaleType.FIT_CENTER
    }
    private fun label(text: String, size: Int, color: Int, bold: Boolean = false) = TextView(this).apply { this.text = text; textSize = size.toFloat(); setTextColor(color); if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) }
    private fun marginBottom(value: Int) = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(value) }
    private fun dp(value: Int) = (value * resources.displayMetrics.density + 0.5f).toInt()
    private fun main(block: () -> Unit) { handler.post(block) }
    private fun toast(text: String) = main { Toast.makeText(this, text, Toast.LENGTH_SHORT).show() }
    private fun formatSize(size: Long): String = when { size < 1024 -> "$size B"; size < 1024 * 1024 -> "%.1f KB".format(size / 1024.0); size < 1024L * 1024 * 1024 -> "%.1f MB".format(size / (1024.0 * 1024)); else -> "%.2f GB".format(size / (1024.0 * 1024 * 1024)) }
    private fun encode(value: String) = URLEncoder.encode(value, "UTF-8").replace("+", "%20")
}

private class NearbyRadarView(context: android.content.Context, darkMode: Boolean, private val foundCount: Int) : View(context) {
    private val density = resources.displayMetrics.density
    private var scanPhase = 0f
    private var scanAnimator: ValueAnimator? = null
    private val ringPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = if (darkMode) 0xff354153.toInt() else 0xffd9e0e9.toInt()
        style = Paint.Style.STROKE
        strokeWidth = 1.2f * density
    }
    private val accentPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = if (darkMode) 0xff82aaff.toInt() else 0xff4778ce.toInt()
        style = Paint.Style.STROKE
        strokeWidth = 2.3f * density
        strokeCap = Paint.Cap.ROUND
    }
    private val dotPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = if (darkMode) 0xffa9c5ff.toInt() else 0xff4778ce.toInt()
        style = Paint.Style.FILL
    }
    private val centerFill = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = if (darkMode) 0xff263b60.toInt() else 0xffe7efff.toInt()
        style = Paint.Style.FILL
    }
    private val pulsePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = if (darkMode) 0xff82aaff.toInt() else 0xff4778ce.toInt()
        style = Paint.Style.STROKE
        strokeWidth = 2f * density
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        if (Build.VERSION.SDK_INT < 26 || ValueAnimator.areAnimatorsEnabled()) {
            scanAnimator = ValueAnimator.ofFloat(0f, 1f).apply {
                duration = 1900L
                repeatCount = ValueAnimator.INFINITE
                interpolator = android.view.animation.LinearInterpolator()
                addUpdateListener { scanPhase = it.animatedValue as Float; invalidate() }
                start()
            }
        }
    }

    override fun onDetachedFromWindow() {
        scanAnimator?.cancel()
        scanAnimator = null
        super.onDetachedFromWindow()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val cx = width / 2f
        val cy = height * 0.53f
        val maxRadius = minOf(width * 0.39f, height * 0.47f)
        pulsePaint.alpha = ((1f - scanPhase) * 150f).toInt().coerceIn(0, 150)
        canvas.drawCircle(cx, cy, maxRadius * (.12f + .88f * scanPhase), pulsePaint)
        pulsePaint.alpha = 255
        listOf(1f, .73f, .46f).forEach { scale ->
            canvas.drawCircle(cx, cy, maxRadius * scale, ringPaint)
        }
        canvas.drawCircle(cx, cy, 24f * density, centerFill)
        // Three open radio waves and a center point form a simple discovery beacon.
        listOf(13f, 20f, 27f).forEach { radius ->
            val r = radius * density
            canvas.drawArc(cx - r, cy - r, cx + r, cy + r, 218f, 104f, false, accentPaint)
        }
        canvas.drawCircle(cx, cy, 3.2f * density, dotPaint)
        val nodeRadius = maxRadius * .73f
        if (foundCount > 0) canvas.drawCircle(cx - nodeRadius * .72f, cy - nodeRadius * .45f, 4f * density, dotPaint)
        if (foundCount > 1) canvas.drawCircle(cx + nodeRadius * .72f, cy - nodeRadius * .45f, 4f * density, dotPaint)
    }
}

private class DrawingOverlayView(context: android.content.Context, private val onTap: () -> Unit) : View(context) {
    private val strokes = mutableListOf<Path>()
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.RED
        style = Paint.Style.STROKE
        strokeWidth = 6f * resources.displayMetrics.density
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }
    private var active: Path? = null
    private var downX = 0f
    private var downY = 0f
    private var moved = false

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        strokes.forEach { canvas.drawPath(it, paint) }
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = event.x; downY = event.y; moved = false
                active = Path().also { it.moveTo(event.x, event.y); strokes.add(it) }
                invalidate(); return true
            }
            MotionEvent.ACTION_MOVE -> {
                if (kotlin.math.abs(event.x - downX) + kotlin.math.abs(event.y - downY) > 18f * resources.displayMetrics.density) moved = true
                active?.lineTo(event.x, event.y); invalidate(); return true
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                active?.lineTo(event.x, event.y); active = null; invalidate()
                if (event.actionMasked == MotionEvent.ACTION_UP && !moved) onTap()
                return true
            }
        }
        return true
    }

    fun clear() { strokes.clear(); active = null; invalidate() }
}
