package com.clipbridge

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import java.net.Inet4Address

data class WifiEndpoint(val network: Network, val address: Inet4Address)

/** Observes Wi-Fi link addresses, including LANs without Internet access. */
class WifiNetworkMonitor(context: Context, private val changed: (WifiEndpoint?) -> Unit) {
    private val manager = context.getSystemService(ConnectivityManager::class.java)
    private val lock = Any()
    private val endpoints = linkedMapOf<Network, WifiEndpoint>()
    private var selected: WifiEndpoint? = null
    private var started = false
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) {
            synchronized(lock) {
                if (!started) return
                val address = properties.linkAddresses.map { it.address }
                    .filterIsInstance<Inet4Address>()
                    .firstOrNull { !it.isLoopbackAddress && !it.isLinkLocalAddress && !it.isAnyLocalAddress }
                if (address == null) endpoints.remove(network)
                else endpoints[network] = WifiEndpoint(network, address)
                publish()
            }
        }

        override fun onLost(network: Network) {
            synchronized(lock) {
                if (!started) return
                endpoints.remove(network)
                publish()
            }
        }
    }

    private fun publish() {
        val next = endpoints.values.lastOrNull()
        if (next != selected) {
            selected = next
            changed(next)
        }
    }

    fun start() = synchronized(lock) {
        if (started) return
        started = true
        try {
            manager.registerNetworkCallback(
                NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(), callback,
            )
        } catch (exception: Exception) {
            started = false
            throw exception
        }
    }

    fun stop() = synchronized(lock) {
        if (!started) return
        started = false
        manager.unregisterNetworkCallback(callback)
        endpoints.clear()
        selected = null
    }
}
