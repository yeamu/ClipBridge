package com.clipbridge

import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import androidx.core.content.ContextCompat

class ClipBridgeForegroundService : Service() {
    private var sync: ClipboardSyncService? = null

    override fun onCreate() {
        super.onCreate()
        // Android requires a foreground notification immediately after startup.
        startForeground(QuickSyncNotification.notificationId, QuickSyncNotification.build(this))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val settings = getSharedPreferences("clipbridge-settings", Context.MODE_PRIVATE)
        val host = settings.getString("windowsIp", "") ?: ""
        val code = settings.getString("pairingCode", "") ?: ""
        if (host.isBlank() || code.length < 4) {
            SyncRuntime.report("请先配置 Windows IP 和至少 4 位配对码。")
            stopSelf()
            return START_NOT_STICKY
        }

        sync?.stop()
        sync = ClipboardSyncService(applicationContext, code, host) { SyncRuntime.report(it) }
        sync!!.start()
        SyncRuntime.running.value = true
        return START_STICKY
    }

    override fun onDestroy() {
        sync?.stop()
        sync = null
        SyncRuntime.running.value = false
        QuickSyncNotification.cancel(this)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, ClipBridgeForegroundService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, ClipBridgeForegroundService::class.java))
        }
    }
}
