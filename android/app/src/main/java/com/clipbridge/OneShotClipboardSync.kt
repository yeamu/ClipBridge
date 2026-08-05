package com.clipbridge

import android.content.Context
import org.json.JSONObject
import java.io.BufferedWriter
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Connects only when the long-lived app connection no longer exists. */
object OneShotClipboardSync {
    fun send(context: Context, text: String): String {
        val settings = context.getSharedPreferences("clipbridge-settings", Context.MODE_PRIVATE)
        val host = settings.getString("windowsIp", "") ?: ""
        val code = settings.getString("pairingCode", "") ?: ""
        if (host.isBlank() || code.length < 4) return "请先在 ClipBridge 配置 Windows IP 和至少 4 位配对码"

        return try {
            Socket().use { socket ->
                socket.soTimeout = 8_000
                socket.connect(InetSocketAddress(host, 45837), 5_000)
                val writer = socket.getOutputStream().bufferedWriter(StandardCharsets.UTF_8)
                val deviceId = UUID.randomUUID().toString()
                writeLine(writer, hello(deviceId, code))

                val reply = socket.getInputStream().bufferedReader(StandardCharsets.UTF_8).readLine()
                    ?: return "Windows 没有响应配对验证"
                val hello = JSONObject(reply)
                val remoteId = hello.optString("DeviceId")
                if (hello.optString("Type") != "hello" ||
                    hello.optString("Proof") != proof(code, "hello|$remoteId|1"))
                    return "配对码验证失败"

                val id = UUID.randomUUID().toString()
                val sentAt = System.currentTimeMillis()
                val packet = JSONObject().apply {
                    put("Type", "clip")
                    put("Message", JSONObject().apply {
                        put("Id", id)
                        put("OriginDeviceId", deviceId)
                        put("Text", text)
                        put("SentAt", sentAt)
                    })
                    put("Mac", proof(code, "clip|$id|$deviceId|$text|$sentAt"))
                }
                writeLine(writer, packet.toString())
                "已发送到 Windows"
            }
        } catch (_: Exception) {
            "同步失败，请确认 Windows 正在运行"
        }
    }

    private fun hello(deviceId: String, code: String) = JSONObject().apply {
        put("Type", "hello")
        put("DeviceId", deviceId)
        put("DeviceName", android.os.Build.MODEL)
        put("Version", 1)
        put("Proof", proof(code, "hello|$deviceId|1"))
    }.toString()

    private fun writeLine(writer: BufferedWriter, value: String) {
        writer.write(value)
        writer.newLine()
        writer.flush()
    }

    private fun proof(code: String, input: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(code.toByteArray(StandardCharsets.UTF_8), "HmacSHA256"))
        return android.util.Base64.encodeToString(
            mac.doFinal(input.toByteArray(StandardCharsets.UTF_8)),
            android.util.Base64.NO_WRAP,
        )
    }
}
