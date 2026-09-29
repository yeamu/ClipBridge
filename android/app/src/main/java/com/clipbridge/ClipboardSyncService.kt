package com.clipbridge

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.net.Uri
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import android.util.Log
import android.util.JsonWriter
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject
import java.io.BufferedWriter
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.ServerSocket
import java.nio.charset.StandardCharsets
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

class ClipboardSyncService(
    private val context: Context,
    private val code: String,
    private val status: (String) -> Unit,
) {
    companion object {
        @Volatile
        private var activeService: ClipboardSyncService? = null

        // Called from the notification's transient foreground activity.
        // A running listener queues the clipboard until Windows reconnects.
        fun requestManualSync(): Boolean {
            val service = activeService ?: return false
            service.syncCurrentClipboard(force = true)
            return true
        }

        fun syncWhenAppFocused(): Boolean {
            val service = activeService ?: return false
            service.syncCurrentClipboard(force = SyncRuntime.clipboardNeedsFocus.value)
            return true
        }

        private const val PORT = 45837
        private const val AUTH_TIMEOUT_MS = 8_000L
        private const val RECONNECT_DELAY_MS = 2_500L
        private const val PING_INTERVAL_MS = 2_000L
        private const val IMAGE_PREFIX = "clipbridge:png:"
        private const val JPEG_PREFIX = "clipbridge:jpeg:"
        private const val GIF_PREFIX = "clipbridge:gif:"
        private const val RAW_IMAGE_PREFIX = "clipbridge:image:"
        private const val MAX_IMAGE_BYTES = 100 * 1024 * 1024
    }

    private data class PendingClip(
        val id: String,
        val text: String,
        val sentAt: Long,
        val mac: String,
    )

    private enum class HandleResult {
        NONE,
        VERIFIED,
        AUTH_FAILED,
    }

    private val tag = "ClipBridge"
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val deviceId = UUID.randomUUID().toString()
    private val seen = RecentMessageIds()
    private val connectionLock = Any()
    private val outboundSignal = Channel<Unit>(Channel.CONFLATED)

    private var clipboard: ClipboardManager? = null
    private var listener: ClipboardManager.OnPrimaryClipChangedListener? = null
    private val pendingClips = BoundedMemoryQueue<PendingClip>({ it.text.length * 2L + 1024 })
    private data class ClipboardRequest(val item: ClipData.Item, val force: Boolean)
    private val clipboardRequests = BoundedMemoryQueue<ClipboardRequest>({ (it.item.text?.length ?: 0) * 2L + 1024 })
    private val clipboardSignal = Channel<Unit>(Channel.CONFLATED)
    private val controls = Channel<String>(32)
    private val fileCache = ClipboardFileCache(File(context.cacheDir, "clipboard"))
    @Volatile private var lastSentId: String? = null
    @Volatile private var retryAfter = 0L
    private var activeSocket: Socket? = null
    private var serverSocket: ServerSocket? = null
    private var wifiEndpoint: WifiEndpoint? = null
    private var hasSeenWifi = false
    private val networkChanges = Channel<WifiEndpoint?>(Channel.CONFLATED)
    private val wifiMonitor = WifiNetworkMonitor(context, ::onWifiChanged)

    @Volatile
    private var lastHash: String? = null

    @Volatile
    private var connectionVerified = false

    fun start() {
        activeService = this
        clipboard = context.getSystemService(ClipboardManager::class.java)
        listener = ClipboardManager.OnPrimaryClipChangedListener {
            onClipboardChanged()
        }.also {
            clipboard!!.addPrimaryClipChangedListener(it)
        }
        report("已启动，等待 Wi-Fi IPv4 地址…")
        scope.launch { listenLoop() }
        scope.launch {
            for (signal in clipboardSignal) {
                while (scope.isActive) {
                    val request = clipboardRequests.peek() ?: break
                    onClipboardPayloadRead(request.item, request.force)
                    clipboardRequests.complete(request)
                }
            }
        }
        wifiMonitor.start()
    }

    fun stop() {
        wifiMonitor.stop()
        scope.cancel()
        listener?.let { clipboard?.removePrimaryClipChangedListener(it) }
        listener = null
        connectionVerified = false
        synchronized(connectionLock) {
            serverSocket?.close()
            serverSocket = null
            wifiEndpoint = null
            try {
                activeSocket?.close()
            } catch (_: Exception) {
            }
            activeSocket = null
        }
        outboundSignal.close()
        clipboardSignal.close()
        controls.close()
        pendingClips.clear()
        clipboardRequests.clear()
        while (controls.tryReceive().isSuccess) Unit
        lastHash = null
        networkChanges.close()
        if (activeService === this) activeService = null
    }

    fun syncCurrentClipboard(force: Boolean = false) {
        onClipboardChanged(force)
    }

    private fun onClipboardChanged(force: Boolean = false) {
        if (!scope.isActive) return
        if (!pendingClips.canAccept(1024)) {
            report("发送队列已满，请等待对端确认后再点按通知。")
            return
        }
        try {
            // Capture the item while the app has input focus; encoding and sending
            // run on IO workers so large images do not block the visible activity.
            val item = clipboard?.primaryClip?.getItemAt(0)
            if (item == null) {
                if (force) {
                    SyncRuntime.clipboardNeedsFocus.value = true
                    report("剪贴板为空或系统禁止后台读取，请点按通知完成同步。")
                }
                return
            }
            if (!clipboardRequests.add(ClipboardRequest(item, force))) {
                report("读取队列已满，请等待处理后再点按通知。")
                return
            }
            clipboardSignal.trySend(Unit)
            report("正在读取当前剪贴板…")
        } catch (_: SecurityException) {
            SyncRuntime.clipboardNeedsFocus.value = true
            report("系统暂未允许读取剪贴板，请点按通知重试。")
        }
    }

    private fun onClipboardPayloadRead(item: ClipData.Item, force: Boolean) {
        try {
            if (!pendingClips.canAccept(1024)) { report("发送队列已满，请等待发送后重试。"); return }
            val text = readClipboardPayload(item) ?: return
            if (!scope.isActive) return
            val hash = PayloadIdentity.hash(text)
            SyncRuntime.clipboardNeedsFocus.value = false
            if (!force && hash == lastHash) return
            val id = UUID.randomUUID().toString()
            val sentAt = System.currentTimeMillis()
            val mac = PayloadIdentity.mac(code, "clip|$id|$deviceId|", text, "|$sentAt")
            if (!pendingClips.add(PendingClip(id, text, sentAt, mac))) {
                report("发送队列已满，请等待发送后再点按通知；大图片单独发送，避免积压内存。")
                return
            }
            lastHash = hash
            outboundSignal.trySend(Unit)
            report(if (connectionVerified) "已读取手机剪贴板，等待对端确认…" else "内容保留在内存中，连接后按顺序发送。")
        } catch (_: SecurityException) {
            SyncRuntime.clipboardNeedsFocus.value = true
            report("系统暂未允许读取剪贴板，请保持 ClipBridge 在前台后重试。")
        } catch (exception: Exception) {
            Log.w(tag, "Clipboard read failed", exception)
            report("读取剪贴板失败：${exception.message ?: exception.javaClass.simpleName}")
        }
    }

    private fun onWifiChanged(endpoint: WifiEndpoint?) {
        val force = synchronized(connectionLock) {
            if (!scope.isActive || wifiEndpoint == endpoint) return
            val hadWifi = hasSeenWifi
            if (endpoint != null) hasSeenWifi = true
            wifiEndpoint = endpoint
            connectionVerified = false
            serverSocket?.close()
            serverSocket = null
            activeSocket?.let(::closeSocket)
            activeSocket = null
            hadWifi
        }
        networkChanges.trySend(endpoint)
        if (endpoint == null) {
            report("Wi-Fi 已断开，等待重新连接…")
        } else {
            // One request per network/address change; repeated link callbacks are
            // deduplicated by the monitor. Queue it until a verified PC connects.
            scope.launch(Dispatchers.Main) {
                if (synchronized(connectionLock) { wifiEndpoint == endpoint }) {
                    syncCurrentClipboard(force = force)
                }
            }
        }
    }

    private suspend fun listenLoop() {
        for (endpoint in networkChanges) {
            if (endpoint == null) continue
            while (scope.isActive && synchronized(connectionLock) { wifiEndpoint == endpoint }) {
                val server = ServerSocket()
                try {
                    synchronized(connectionLock) {
                        if (!scope.isActive || wifiEndpoint != endpoint) return@synchronized
                        server.reuseAddress = true
                        server.bind(InetSocketAddress(endpoint.address, PORT))
                        serverSocket = server
                    }
                    if (!server.isBound) break
                    report("手机 IP：${endpoint.address.hostAddress}，等待 Windows 连接…")
                    while (scope.isActive && !server.isClosed) {
                        val socket = server.accept()
                        synchronized(connectionLock) {
                            if (wifiEndpoint != endpoint || !scope.isActive) closeSocket(socket)
                            else activeSocket = socket
                        }
                        if (socket.isClosed) continue
                        try {
                            socket.tcpNoDelay = true
                            socket.soTimeout = 120_000
                            runConnection(socket)
                        } catch (exception: Exception) {
                            if (scope.isActive && !server.isClosed) Log.w(tag, "Connection ended", exception)
                        } finally {
                            connectionVerified = false
                            synchronized(connectionLock) {
                                if (activeSocket === socket) activeSocket = null
                            }
                            closeSocket(socket)
                        }
                    }
                } catch (exception: Exception) {
                    if (scope.isActive && synchronized(connectionLock) { wifiEndpoint == endpoint }) {
                        Log.w(tag, "Listener failed", exception)
                        report("手机监听失败，将自动重试：${exception.message}")
                    }
                } finally {
                    server.close()
                    synchronized(connectionLock) {
                        if (serverSocket === server) serverSocket = null
                    }
                }
                if (scope.isActive && synchronized(connectionLock) { wifiEndpoint == endpoint })
                    delay(RECONNECT_DELAY_MS)
            }
        }
    }

    private suspend fun runConnection(socket: Socket) = coroutineScope {
        lastSentId = null
        val writer = socket.getOutputStream().bufferedWriter(StandardCharsets.UTF_8)
        if (!writeLine(writer, hello())) throw IOException("握手发送失败")
        report("网络已连接，正在验证配对码…")

        val verified = CompletableDeferred<Unit>()
        val sender: Job = launch(Dispatchers.IO) {
            try {
                withTimeout(AUTH_TIMEOUT_MS) { verified.await() }
                connectionVerified = true
                outboundSignal.trySend(Unit)
                senderLoop(socket, writer)
            } catch (exception: Exception) {
                if (currentCoroutineContext().isActive) {
                    Log.w(tag, "Sender ended", exception)
                }
                closeSocket(socket)
            }
        }

        try {
            socket.getInputStream()
                .bufferedReader(StandardCharsets.UTF_8)
                .useLines { lines ->
                    lines.forEach { line ->
                        when (handle(line)) {
                            HandleResult.VERIFIED -> {
                                connectionVerified = true
                                if (!verified.isCompleted) verified.complete(Unit)
                            }

                            HandleResult.AUTH_FAILED -> {
                                closeSocket(socket)
                                return@forEach
                            }

                            HandleResult.NONE -> Unit
                        }
                    }
                }
        } finally {
            connectionVerified = false
            closeSocket(socket)
            sender.cancelAndJoin()
            if (scope.isActive) report("连接已断开，等待 Windows 重新连接…")
        }
    }

    private suspend fun senderLoop(socket: Socket, writer: BufferedWriter) {
        while (currentCoroutineContext().isActive && !socket.isClosed) {
            val signalled = withTimeoutOrNull(PING_INTERVAL_MS) { outboundSignal.receiveCatching().getOrNull() }
            if (signalled == null && !writeLine(writer, JSONObject().put("Type", "ping").toString()))
                throw IOException("心跳发送失败")
            while (true) {
                val control = controls.tryReceive().getOrNull() ?: break
                if (!writeLine(writer, control)) throw IOException("确认消息发送失败")
            }
            val candidate = pendingClips.peek() ?: continue
            if (candidate.id == lastSentId && System.currentTimeMillis() < retryAfter) continue
            lastSentId = candidate.id
            retryAfter = System.currentTimeMillis() + 120_000
            // Stream JSON encoding; never retain a second serialized image string.
            val json = JsonWriter(writer)
            json.beginObject().name("Type").value("clip")
                .name("Message").beginObject().name("Id").value(candidate.id)
                .name("OriginDeviceId").value(deviceId).name("Text").value(candidate.text)
                .name("SentAt").value(candidate.sentAt).endObject()
                .name("Mac").value(candidate.mac).endObject()
            writer.newLine()
            writer.flush()
            // The authenticated ACK removes the item; failures keep it in RAM.
        }
    }

    private fun acknowledge(id: String, success: Boolean) {
        val packet = JSONObject().put("Type", "ack").put("Id", id).put("Success", success)
            .put("Proof", proof("ack|$id|$success")).toString()
        if (!controls.trySend(packet).isSuccess) {
            synchronized(connectionLock) { activeSocket?.let(::closeSocket) }
        }
        outboundSignal.trySend(Unit)
    }

    private fun hello() = JSONObject().apply {
        put("Type", "hello")
        put("DeviceId", deviceId)
        put("DeviceName", android.os.Build.MODEL)
        put("Version", 1)
        put("Proof", proof("hello|$deviceId|1"))
    }.toString()

    private fun handle(line: String): HandleResult {
        try {
            val packet = JSONObject(line)
            if (packet.optString("Type") == "pong") return HandleResult.NONE

            if (packet.optString("Type") == "hello") {
                val remoteId = packet.getString("DeviceId")
                if (packet.getString("Proof") != proof("hello|$remoteId|1")) {
                    report("连接已建立，但配对码不一致。")
                    return HandleResult.AUTH_FAILED
                }
                report("已连接并通过配对验证：${packet.optString("DeviceName", "Windows")}。")
                return HandleResult.VERIFIED
            }

            if (!connectionVerified) return HandleResult.NONE
            if (packet.optString("Type") == "ack") {
                val id = packet.getString("Id")
                val success = packet.getBoolean("Success")
                if (packet.optString("Proof") != proof("ack|$id|$success")) return HandleResult.NONE
                val pending = pendingClips.peek() ?: return HandleResult.NONE
                if (pending.id != id) return HandleResult.NONE
                if (success) {
                    pendingClips.complete(pending)
                    report("Windows 已确认接收，已释放该项发送内存。")
                } else {
                    retryAfter = System.currentTimeMillis() + 30_000
                    report("Windows 暂时无法写入剪贴板，内容仍在内存中，稍后自动重试。")
                }
                outboundSignal.trySend(Unit)
                return HandleResult.NONE
            }
            if (packet.optString("Type") != "clip") return HandleResult.NONE
            val message = packet.getJSONObject("Message")
            val id = message.getString("Id")
            val origin = message.getString("OriginDeviceId")
            val text = message.getString("Text")
            val sentAt = message.getLong("SentAt")
            if (
                origin == deviceId ||
                packet.getString("Mac") != PayloadIdentity.mac(code, "clip|$id|$origin|", text, "|$sentAt")
            ) {
                return HandleResult.NONE
            }

            if (seen.contains(id)) { acknowledge(id, true); return HandleResult.NONE }
            val hash = PayloadIdentity.hash(text)
            val previous = lastHash
            try {
                if (hash != lastHash) {
                    lastHash = hash
                    if (!applyClipboardPayload(text)) throw IOException("不支持的图片格式或图片超过上限")
                }
                seen.add(id)
                acknowledge(id, true)
                report("已收到 Windows 剪贴板。")
            } catch (exception: Exception) {
                lastHash = previous
                acknowledge(id, false)
                report("写入剪贴板失败，对端将保留内容重试：${exception.message}")
            }
        } catch (exception: Exception) {
            Log.w(tag, "Packet handling failed", exception)
        }
        return HandleResult.NONE
    }

    private fun readClipboardPayload(item: ClipData.Item): String? {
        val uri = item.uri
        if (uri != null) {
            val knownBytes = runCatching { context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1 }.getOrDefault(-1)
            if (knownBytes > MAX_IMAGE_BYTES) { report("原始图片超过 100 MB，未同步。"); return null }
            if (knownBytes > 0 && !pendingClips.canAccept(((knownBytes + 2) / 3) * 8 + 1024)) {
                report("图片超出当前剩余队列容量，请等待发送后重试。")
                return null
            }
            var sourceTooLarge = false
            val source = context.contentResolver.openInputStream(uri)?.use { input ->
                val output = ByteArrayOutputStream(); val buffer = ByteArray(8192); var total = 0
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    total += count
                    if (total > MAX_IMAGE_BYTES) {
                        sourceTooLarge = true
                        return@use null
                    }
                    output.write(buffer, 0, count)
                }
                output.toByteArray()
            } ?: run {
                if (sourceTooLarge) report("原始图片超过 100 MB，未同步。")
                return null
            }
            val isGif = source.size >= 6 &&
                (source.copyOfRange(0, 6).toString(Charsets.US_ASCII) == "GIF87a" ||
                    source.copyOfRange(0, 6).toString(Charsets.US_ASCII) == "GIF89a")
            if (isGif) {
                return GIF_PREFIX + android.util.Base64.encodeToString(source, android.util.Base64.NO_WRAP)
            }
            val sourceExtension = detectImageExtension(uri, source)
            val requiresPng = sourceExtension == "heic" || sourceExtension == "heif"
            if (sourceExtension != null && !requiresPng) {
                val prefix = when (sourceExtension) {
                    "png" -> IMAGE_PREFIX
                    "jpg", "jpeg", "jfif" -> JPEG_PREFIX
                    else -> "$RAW_IMAGE_PREFIX$sourceExtension:"
                }
                return prefix + android.util.Base64.encodeToString(source, android.util.Base64.NO_WRAP)
            }
            val bitmap = try {
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.P) {
                    ImageDecoder.decodeBitmap(ImageDecoder.createSource(context.contentResolver, uri)) { decoder, _, _ ->
                        decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
                    }
                } else {
                    BitmapFactory.decodeByteArray(source, 0, source.size)
                }
            } catch (_: Exception) {
                BitmapFactory.decodeByteArray(source, 0, source.size)
            } ?: run { report("无法读取该图片格式，未同步。"); return null }
            val encoded = try {
                encodeStaticImage(bitmap, pngOnly = requiresPng)
            } finally {
                bitmap.recycle()
            }
            if (encoded == null) {
                report("图片转换后仍超过 100 MB，未同步。")
                return null
            }
            return encoded.first + android.util.Base64.encodeToString(encoded.second, android.util.Base64.NO_WRAP)
        }
        return item.coerceToText(context)?.toString()?.takeIf { it.isNotBlank() }
    }

    private fun encodeStaticImage(bitmap: Bitmap, pngOnly: Boolean = false): Pair<String, ByteArray>? {
        val output = ByteArrayOutputStream()
        if (bitmap.compress(Bitmap.CompressFormat.PNG, 100, output) && output.size() <= MAX_IMAGE_BYTES) {
            return IMAGE_PREFIX to output.toByteArray()
        }
        if (pngOnly) return null
        for (quality in intArrayOf(92, 85, 75, 65, 55, 45)) {
            output.reset()
            if (bitmap.compress(Bitmap.CompressFormat.JPEG, quality, output) && output.size() <= MAX_IMAGE_BYTES) {
                return JPEG_PREFIX to output.toByteArray()
            }
        }
        return null
    }

    private fun detectImageExtension(uri: Uri, bytes: ByteArray): String? {
        val mimeExtension = when (context.contentResolver.getType(uri)?.lowercase()) {
            "image/jpeg", "image/jpg" -> "jpg"
            "image/png" -> "png"
            "image/gif" -> "gif"
            "image/bmp", "image/x-ms-bmp" -> "bmp"
            "image/tiff" -> "tiff"
            "image/webp" -> "webp"
            "image/heic", "image/heic-sequence" -> "heic"
            "image/heif", "image/heif-sequence" -> "heif"
            "image/avif" -> "avif"
            "image/x-icon", "image/vnd.microsoft.icon" -> "ico"
            else -> null
        }
        if (mimeExtension != null) return mimeExtension

        val pathExtension = MimeTypeMap.getFileExtensionFromUrl(uri.toString()).lowercase()
        if (isSupportedImageExtension(pathExtension)) return pathExtension

        if (bytes.size >= 12) {
            if (
                bytes[0] == 0x89.toByte() && bytes[1] == 0x50.toByte() &&
                bytes[2] == 0x4E.toByte() && bytes[3] == 0x47.toByte()
            ) return "png"
            if (
                bytes[0] == 0xFF.toByte() && bytes[1] == 0xD8.toByte() &&
                bytes[2] == 0xFF.toByte()
            ) return "jpg"
            if (bytes[0] == 'B'.code.toByte() && bytes[1] == 'M'.code.toByte()) return "bmp"
            if (
                bytes[0] == 'R'.code.toByte() && bytes[1] == 'I'.code.toByte() &&
                bytes[2] == 'F'.code.toByte() && bytes[3] == 'F'.code.toByte() &&
                bytes[8] == 'W'.code.toByte() && bytes[9] == 'E'.code.toByte() &&
                bytes[10] == 'B'.code.toByte() && bytes[11] == 'P'.code.toByte()
            ) return "webp"
            if (
                (bytes[0] == 'I'.code.toByte() && bytes[1] == 'I'.code.toByte() &&
                    bytes[2] == 0x2A.toByte() && bytes[3] == 0.toByte()) ||
                (bytes[0] == 'M'.code.toByte() && bytes[1] == 'M'.code.toByte() &&
                    bytes[2] == 0.toByte() && bytes[3] == 0x2A.toByte())
            ) return "tiff"
            if (
                bytes[0] == 0.toByte() && bytes[1] == 0.toByte() &&
                bytes[2] == 1.toByte() && bytes[3] == 0.toByte()
            ) return "ico"
            if (
                bytes[4] == 'f'.code.toByte() && bytes[5] == 't'.code.toByte() &&
                bytes[6] == 'y'.code.toByte() && bytes[7] == 'p'.code.toByte()
            ) {
                val brands = bytes.copyOfRange(8, minOf(bytes.size, 40)).toString(Charsets.US_ASCII)
                if ("avif" in brands || "avis" in brands) return "avif"
                if (
                    "heic" in brands || "heix" in brands || "hevc" in brands ||
                    "hevx" in brands || "heim" in brands || "heis" in brands
                ) return "heic"
                if ("mif1" in brands || "msf1" in brands) return "heif"
            }
        }
        return null
    }

    private fun isSupportedImageExtension(extension: String): Boolean =
        extension in setOf(
            "png", "jpg", "jpeg", "jfif", "bmp", "gif", "tif", "tiff",
            "webp", "heic", "heif", "avif", "ico",
        )

    private fun applyClipboardPayload(payload: String): Boolean {
        if (
            !payload.startsWith(IMAGE_PREFIX) &&
            !payload.startsWith(JPEG_PREFIX) &&
            !payload.startsWith(GIF_PREFIX) &&
            !payload.startsWith(RAW_IMAGE_PREFIX)
        ) {
            clipboard?.setPrimaryClip(ClipData.newPlainText("ClipBridge", payload)); return true
        }
        val isGif = payload.startsWith(GIF_PREFIX)
        val isJpeg = payload.startsWith(JPEG_PREFIX)
        val isRawImage = payload.startsWith(RAW_IMAGE_PREFIX)
        val rawSeparator = if (isRawImage) {
            payload.indexOf(':', RAW_IMAGE_PREFIX.length)
        } else {
            -1
        }
        if (isRawImage && rawSeparator <= RAW_IMAGE_PREFIX.length) return false
        val rawExtension = if (rawSeparator > RAW_IMAGE_PREFIX.length) {
            payload.substring(RAW_IMAGE_PREFIX.length, rawSeparator).lowercase()
        } else null
        if (rawExtension != null && !isSupportedImageExtension(rawExtension)) return false
        val prefix = when {
            isGif -> GIF_PREFIX
            isJpeg -> JPEG_PREFIX
            rawExtension != null -> payload.substring(0, rawSeparator + 1)
            else -> IMAGE_PREFIX
        }
        if (payload.length - prefix.length > ((MAX_IMAGE_BYTES + 2L) / 3) * 4) return false
        var bytes = android.util.Base64.decode(payload.removePrefix(prefix), android.util.Base64.NO_WRAP)
        if (bytes.size > MAX_IMAGE_BYTES) return false
        val requiresPng = rawExtension == "heic" || rawExtension == "heif"
        if (requiresPng) {
            val bitmap = ImageDecoder.decodeBitmap(ImageDecoder.createSource(java.nio.ByteBuffer.wrap(bytes))) { decoder, _, _ ->
                decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
            }
            val encoded = try {
                encodeStaticImage(bitmap, pngOnly = true)
            } finally {
                bitmap.recycle()
            } ?: run { report("HEIC/HEIF 转换为 PNG 后超过 100 MB，未同步。"); return false }
            bytes = encoded.second
        }
        val folder = File(context.cacheDir, "clipboard").apply { mkdirs() }
        val extension = when {
            requiresPng -> "png"
            isGif -> "gif"
            isJpeg -> "jpg"
            rawExtension != null -> rawExtension
            else -> "png"
        }
        val image = File(folder, "remote-${UUID.randomUUID()}.$extension")
        try {
            image.writeBytes(bytes)
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", image)
            clipboard?.setPrimaryClip(ClipData.newUri(context.contentResolver, "ClipBridge image", uri))
        } catch (exception: Exception) {
            image.delete()
            throw exception
        }
        runCatching { fileCache.cleanup(setOf(image.name)) }
        return true
    }

    private fun writeLine(writer: BufferedWriter, text: String): Boolean = try {
        writer.write(text)
        writer.newLine()
        writer.flush()
        true
    } catch (_: Exception) {
        false
    }

    private fun closeSocket(socket: Socket) {
        try {
            socket.close()
        } catch (_: Exception) {
        }
    }

    private fun report(message: String) {
        if (!scope.isActive) return
        Log.i(tag, message)
        status(message)
    }

    private fun proof(input: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(
            SecretKeySpec(
                code.toByteArray(StandardCharsets.UTF_8),
                "HmacSHA256",
            ),
        )
        return android.util.Base64.encodeToString(
            mac.doFinal(input.toByteArray(StandardCharsets.UTF_8)),
            android.util.Base64.NO_WRAP,
        )
    }

}
