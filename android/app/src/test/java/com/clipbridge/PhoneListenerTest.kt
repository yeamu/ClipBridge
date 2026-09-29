package com.clipbridge

import android.content.ClipData
import android.content.ClipboardManager
import org.robolectric.shadows.ShadowNetwork
import android.os.Looper
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], manifest = Config.NONE)
class PhoneListenerTest {
    @Test fun `phone accepts PC then closes old connection and syncs once after Wi-Fi change`() {
        val context = RuntimeEnvironment.getApplication()
        val clipboard = context.getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(ClipData.newPlainText("test", "phone clipboard"))
        val service = ClipboardSyncService(context, "test-code") { }
        service.start()
        try {
            change(service, 1, "127.0.0.1")
            shadowOf(Looper.getMainLooper()).idle()
            connect("127.0.0.1").use { old ->
                val firstReader = old.getInputStream().bufferedReader()
                assertEquals("hello", JSONObject(firstReader.readLine()).getString("Type"))
                authenticate(old)
                assertEquals("phone clipboard", JSONObject(firstReader.readLine()).getJSONObject("Message").getString("Text"))

                change(service, 2, "127.0.0.2")
                shadowOf(Looper.getMainLooper()).idle()
                assertNull(firstReader.readLine())
                connect("127.0.0.2").use { current ->
                    val reader = current.getInputStream().bufferedReader()
                    assertEquals("hello", JSONObject(reader.readLine()).getString("Type"))
                    authenticate(current)
                    val resent = JSONObject(reader.readLine())
                    assertEquals("phone clipboard", resent.getJSONObject("Message").getString("Text"))
                    acknowledge(current, resent.getJSONObject("Message").getString("Id"))
                    // The first unacknowledged item is released before the
                    // switch-triggered item can be sent.
                    val switched = JSONObject(reader.readLine())
                    assertEquals("phone clipboard", switched.getJSONObject("Message").getString("Text"))
                    acknowledge(current, switched.getJSONObject("Message").getString("Id"))
                    change(service, 2, "127.0.0.2")
                    shadowOf(Looper.getMainLooper()).idle()
                    current.soTimeout = 300
                    try {
                        reader.readLine()
                        fail("Repeated callback sent a duplicate clipboard")
                    } catch (_: SocketTimeoutException) { }
                    service.stop()
                    current.soTimeout = 2_000
                    assertNull(reader.readLine())
                }
            }
        } finally { service.stop() }
    }

    private fun change(service: ClipboardSyncService, id: Int, address: String) {
        val method = ClipboardSyncService::class.java.getDeclaredMethod("onWifiChanged", WifiEndpoint::class.java)
        method.isAccessible = true
        method.invoke(service, WifiEndpoint(ShadowNetwork.newInstance(id), InetAddress.getByName(address) as Inet4Address))
    }

    private fun connect(address: String): Socket {
        val deadline = System.nanoTime() + 5_000_000_000L
        while (true) {
            val socket = Socket()
            try {
                socket.connect(InetSocketAddress(address, 45837), 500)
                socket.soTimeout = 5_000
                return socket
            } catch (exception: Exception) {
                socket.close()
                if (System.nanoTime() >= deadline) throw exception
                Thread.sleep(25)
            }
        }
    }

    private fun acknowledge(socket: Socket, id: String) {
        val packet = JSONObject().put("Type", "ack").put("Id", id).put("Success", true)
            .put("Proof", PayloadIdentity.mac("test-code", "ack|$id|true"))
        socket.getOutputStream().write((packet.toString() + "\n").toByteArray())
        socket.getOutputStream().flush()
    }

    private fun authenticate(socket: Socket) {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec("test-code".toByteArray(), "HmacSHA256"))
        val proof = Base64.getEncoder().encodeToString(mac.doFinal("hello|pc|1".toByteArray()))
        val hello = JSONObject().put("Type", "hello").put("DeviceId", "pc")
            .put("DeviceName", "Test PC").put("Version", 1).put("Proof", proof)
        socket.getOutputStream().write((hello.toString() + "\n").toByteArray())
        socket.getOutputStream().flush()
    }
}
