package com.clipbridge

import android.net.ConnectivityManager
import android.net.LinkAddress
import android.net.LinkProperties
import org.robolectric.shadows.ShadowNetwork
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], manifest = Config.NONE)
class WifiNetworkMonitorTest {
    @Test fun `switch with same IP emits once and old network loss does not clear new IP`() {
        val events = mutableListOf<WifiEndpoint?>()
        val monitor = WifiNetworkMonitor(RuntimeEnvironment.getApplication()) { events.add(it) }
        val callback = callback(monitor)
        monitor.start()
        try {
            val first = ShadowNetwork.newInstance(1)
            val second = ShadowNetwork.newInstance(2)
            callback.onLinkPropertiesChanged(first, properties("192.168.1.9/24"))
            callback.onLinkPropertiesChanged(first, properties("192.168.1.9/24"))
            assertEquals(1, events.size)
            callback.onLinkPropertiesChanged(second, properties("192.168.1.9/24"))
            callback.onLost(first)
            assertEquals(2, events.size)
            assertEquals(second, events.last()!!.network)
            callback.onLost(second)
            assertNull(events.last())
            callback.onLinkPropertiesChanged(second, properties("192.168.1.9/24"))
            assertEquals(4, events.size)
        } finally { monitor.stop() }
        callback.onLost(ShadowNetwork.newInstance(2))
        assertEquals(4, events.size)
    }

    @Test fun `waits for usable IPv4 and detects DHCP address changes`() {
        val events = mutableListOf<WifiEndpoint?>()
        val monitor = WifiNetworkMonitor(RuntimeEnvironment.getApplication()) { events.add(it) }
        val callback = callback(monitor)
        monitor.start()
        try {
            val network = ShadowNetwork.newInstance(3)
            callback.onLinkPropertiesChanged(network, properties("fe80::1/64", "169.254.1.1/16", "127.0.0.1/8"))
            assertTrue(events.isEmpty())
            callback.onLinkPropertiesChanged(network, properties("fe80::1/64", "192.168.2.5/24"))
            assertEquals("192.168.2.5", events.last()!!.address.hostAddress)
            callback.onLinkPropertiesChanged(network, properties("192.168.2.6/24"))
            assertEquals(2, events.size)
            assertEquals("192.168.2.6", events.last()!!.address.hostAddress)
            callback.onLinkPropertiesChanged(network, properties("fe80::1/64"))
            assertNull(events.last())
        } finally { monitor.stop() }
    }

    private fun properties(vararg addresses: String) = LinkProperties().apply {
        val constructor = LinkAddress::class.java.getDeclaredConstructor(String::class.java)
        constructor.isAccessible = true
        setLinkAddresses(addresses.map { constructor.newInstance(it) })
    }

    private fun callback(monitor: WifiNetworkMonitor): ConnectivityManager.NetworkCallback =
        WifiNetworkMonitor::class.java.getDeclaredField("callback").let {
            it.isAccessible = true
            it.get(monitor) as ConnectivityManager.NetworkCallback
        }
}
