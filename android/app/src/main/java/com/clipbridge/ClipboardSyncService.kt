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
import java.nio.charset.StandardCharsets
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

class ClipboardSyncService(
    private val context: Context,
    private val code: String,
    private val host: String,
    private val status: (String) -> Unit,
) {
    companion object {
        @Volatile
        private var activeService: ClipboardSyncService? = null

        // Called from the notification's transient foreground activity.
        // It returns false only when the app process no longer has a live connection.
        fun requestManualSync(): Boolean {
            val service = activeService ?: return false
            service.syncCurrentClipboard(force = true)
            return true
        }

        fun syncWhenAppFocused(): Boolean {
            val service = activeService ?: return false
            service.syncCurrentClipboard()
            return true
        }

        private const val PORT = 45837
        private const val CONNECT_TIMEOUT_MS = 5_000
        private const val AUTH_TIMEOUT_MS = 8_000L
        private const val RECONNECT_DELAY_MS = 2_500L
        private const val PING_INTERVAL_MS = 2_000L
        private const val CLIP_DEBOUNCE_MS = 25L
        private const val MAX_PENDING_CLIPS = 20
        private const val IMAGE_PREFIX = "clipbridge:png:"
        private const val JPEG_PREFIX = "clipbridge:jpeg:"
        private const val GIF_PREFIX = "clipbridge:gif:"
        private const val RAW_IMAGE_PREFIX = "clipbridge:image:"
        private const val MAX_IMAGE_BYTES = 20 * 1024 * 1024
    }

    private data class PendingClip(
        val id: String,
        val text: String,
        val packet: String,
    )

    private enum class HandleResult {
        NONE,
        VERIFIED,
        AUTH_FAILED,
    }

    private val tag = "ClipBridge"
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val deviceId = UUID.randomUUID().toString()
    private val seen = mutableSetOf<String>()
    private val pendingLock = Any()
    private val connectionLock = Any()
    private val outboundSignal = Channel<Unit>(Channel.CONFLATED)

    private var clipboard: ClipboardManager? = null
    private var listener: ClipboardManager.OnPrimaryClipChangedListener? = null
    private val pendingClips = ArrayDeque<PendingClip>()
    private var activeSocket: Socket? = null

    @Volatile
    private var lastText: String? = null

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
        scope.launch { connectLoop() }
        report("已启动，正在连接 Windows…")
    }

    fun stop() {
        listener?.let { clipboard?.removePrimaryClipChangedListener(it) }
        listener = null
        connectionVerified = false
        synchronized(connectionLock) {
            try {
                activeSocket?.close()
            } catch (_: Exception) {
            }
            activeSocket = null
        }
        outboundSignal.close()
        scope.cancel()
        if (activeService === this) activeService = null
    }

    fun syncCurrentClipboard(force: Boolean = false) {
        onClipboardChanged(force)
    }

    private fun onClipboardChanged(force: Boolean = false) {
        try {
            val text = readClipboardPayload() ?: return

            if (!force && text == lastText) return
            lastText = text

            val id = UUID.randomUUID().toString()
            val sentAt = System.currentTimeMillis()
            val packet = JSONObject().apply {
                put("Type", "clip")
                put(
                    "Message",
                    JSONObject().apply {
                        put("Id", id)
                        put("OriginDeviceId", deviceId)
                        put("Text", text)
                        put("SentAt", sentAt)
                    },
                )
                put("Mac", proof("clip|$id|$deviceId|$text|$sentAt"))
            }
            val pending = PendingClip(id, text, packet.toString())
            synchronized(seen) { seen.add(id) }
            synchronized(pendingLock) {
                if (pendingClips.size == MAX_PENDING_CLIPS) pendingClips.removeFirst()
                pendingClips.addLast(pending)
            }
            outboundSignal.trySend(Unit)

            if (connectionVerified) {
                report("已读取手机剪贴板，已加入发送队列…")
            } else {
                report("已保存剪贴板，连接后按顺序发送。")
            }
        } catch (_: SecurityException) {
            report("系统暂未允许读取剪贴板，请保持 ClipBridge 在前台后重试。")
        } catch (exception: Exception) {
            Log.w(tag, "Clipboard read failed", exception)
            report("读取剪贴板失败：${exception.message ?: exception.javaClass.simpleName}")
        }
    }

    private suspend fun connectLoop() {
        while (scope.isActive) {
            try {
                val socket = Socket()
                synchronized(connectionLock) { activeSocket = socket }
                try {
                    socket.connect(InetSocketAddress(host, PORT), CONNECT_TIMEOUT_MS)
                    runConnection(socket)
                } finally {
                    connectionVerified = false
                    synchronized(connectionLock) {
                        if (activeSocket === socket) activeSocket = null
                    }
                    try {
                        socket.close()
                    } catch (_: Exception) {
                    }
                }
            } catch (exception: Exception) {
                if (scope.isActive) {
                    Log.w(tag, "Connect attempt failed", exception)
                    report("等待 Windows（确认 IP、同一 Wi-Fi 和防火墙）…")
                }
            }
            if (scope.isActive) delay(RECONNECT_DELAY_MS)
        }
    }

    private suspend fun runConnection(socket: Socket) = coroutineScope {
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
            sender.cancelAndJoin()
            closeSocket(socket)
            if (scope.isActive) report("连接已断开，正在重连…")
        }
    }

    private suspend fun senderLoop(socket: Socket, writer: BufferedWriter) {
        while (currentCoroutineContext().isActive && !socket.isClosed) {
            val signalled = withTimeoutOrNull(PING_INTERVAL_MS) {
                outboundSignal.receiveCatching().getOrNull()
            }

            if (signalled == null) {
                if (!writeLine(writer, JSONObject().put("Type", "ping").toString())) {
                    throw IOException("心跳发送失败")
                }
                continue
            }

            // Briefly gather callbacks from one multi-selection copy, then retain
            // each resulting clip in FIFO order.
            delay(CLIP_DEBOUNCE_MS)
            while (outboundSignal.tryReceive().isSuccess) Unit

            while (currentCoroutineContext().isActive && !socket.isClosed) {
                val candidate = synchronized(pendingLock) { pendingClips.firstOrNull() } ?: break
                if (!writeLine(writer, candidate.packet)) {
                    // Keep every queued clip for the next verified connection.
                    throw IOException("剪贴板发送失败")
                }
                synchronized(pendingLock) {
                    if (pendingClips.firstOrNull() === candidate) pendingClips.removeFirst()
                }
                report("已发送到 Windows（${candidate.text.length} 个字符）。")
            }
        }
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

            if (packet.optString("Type") != "clip") return HandleResult.NONE
            val message = packet.getJSONObject("Message")
            val id = message.getString("Id")
            val origin = message.getString("OriginDeviceId")
            val text = message.getString("Text")
            val sentAt = message.getLong("SentAt")
            if (
                origin == deviceId ||
                packet.getString("Mac") != proof("clip|$id|$origin|$text|$sentAt") ||
                !synchronized(seen) { seen.add(id) }
            ) {
                return HandleResult.NONE
            }

            if (lastText != text) {
                lastText = text
                applyClipboardPayload(text)
            }
            report("已收到 Windows 剪贴板。")
        } catch (exception: Exception) {
            Log.w(tag, "Packet handling failed", exception)
        }
        return HandleResult.NONE
    }

    private fun readClipboardPayload(): String? {
        val item = clipboard?.primaryClip?.getItemAt(0) ?: return null
        val uri = item.uri
        if (uri != null) {
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
                if (sourceTooLarge) report("原始图片超过 20 MB，未同步。")
                return null
            }
            val isGif = source.size >= 6 &&
                (source.copyOfRange(0, 6).toString(Charsets.US_ASCII) == "GIF87a" ||
                    source.copyOfRange(0, 6).toString(Charsets.US_ASCII) == "GIF89a")
            if (isGif) {
                return GIF_PREFIX + android.util.Base64.encodeToString(source, android.util.Base64.NO_WRAP)
            }
            val sourceExtension = detectImageExtension(uri, source)
            if (sourceExtension != null) {
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
                encodeStaticImage(bitmap)
            } finally {
                bitmap.recycle()
            }
            if (encoded == null) {
                report("图片转换后仍超过 20 MB，未同步。")
                return null
            }
            return encoded.first + android.util.Base64.encodeToString(encoded.second, android.util.Base64.NO_WRAP)
        }
        return item.coerceToText(context)?.toString()?.takeIf { it.isNotBlank() }
    }

    private fun encodeStaticImage(bitmap: Bitmap): Pair<String, ByteArray>? {
        val output = ByteArrayOutputStream()
        if (bitmap.compress(Bitmap.CompressFormat.PNG, 100, output) && output.size() <= MAX_IMAGE_BYTES) {
            return IMAGE_PREFIX to output.toByteArray()
        }
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

    private fun applyClipboardPayload(payload: String) {
        if (
            !payload.startsWith(IMAGE_PREFIX) &&
            !payload.startsWith(JPEG_PREFIX) &&
            !payload.startsWith(GIF_PREFIX) &&
            !payload.startsWith(RAW_IMAGE_PREFIX)
        ) {
            clipboard?.setPrimaryClip(ClipData.newPlainText("ClipBridge", payload)); return
        }
        val isGif = payload.startsWith(GIF_PREFIX)
        val isJpeg = payload.startsWith(JPEG_PREFIX)
        val isRawImage = payload.startsWith(RAW_IMAGE_PREFIX)
        val rawSeparator = if (isRawImage) {
            payload.indexOf(':', RAW_IMAGE_PREFIX.length)
        } else {
            -1
        }
        if (isRawImage && rawSeparator <= RAW_IMAGE_PREFIX.length) return
        val rawExtension = if (rawSeparator > RAW_IMAGE_PREFIX.length) {
            payload.substring(RAW_IMAGE_PREFIX.length, rawSeparator).lowercase()
        } else null
        if (rawExtension != null && !isSupportedImageExtension(rawExtension)) return
        val prefix = when {
            isGif -> GIF_PREFIX
            isJpeg -> JPEG_PREFIX
            rawExtension != null -> payload.substring(0, rawSeparator + 1)
            else -> IMAGE_PREFIX
        }
        val bytes = android.util.Base64.decode(payload.removePrefix(prefix), android.util.Base64.NO_WRAP)
        if (bytes.size > MAX_IMAGE_BYTES) return
        val folder = File(context.cacheDir, "clipboard").apply { mkdirs() }
        val extension = when {
            isGif -> "gif"
            isJpeg -> "jpg"
            rawExtension != null -> rawExtension
            else -> "png"
        }
        val image = File(folder, "remote-${System.currentTimeMillis()}.$extension").apply { writeBytes(bytes) }
        val uri = FileProvider.getUriForFile(context, "com.clipbridge.fileprovider", image)
        clipboard?.setPrimaryClip(ClipData.newUri(context.contentResolver, "ClipBridge image", uri))
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
