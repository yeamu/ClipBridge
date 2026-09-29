package com.clipbridge

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon

object QuickSyncNotification {
    private const val channelId = "clipbridge-quick-sync"
    const val notificationId = 45837

    private fun ensureChannel(context: Context): NotificationManager {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                channelId,
                "ClipBridge 一键同步",
                NotificationManager.IMPORTANCE_LOW,
            ).apply { description = "点按即可同步手机当前剪贴板" },
        )
        return manager
    }

    fun build(context: Context): android.app.Notification {
        ensureChannel(context)
        val actionIntent = Intent(context, SyncNowActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_MULTIPLE_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
        }
        val action = PendingIntent.getActivity(
            context,
            1,
            actionIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val openApp = PendingIntent.getActivity(
            context,
            2,
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return android.app.Notification.Builder(context, channelId)
            .setSmallIcon(R.drawable.ic_stat_clipbridge)
            .setContentTitle("点击同步到电脑")
            .setOnlyAlertOnce(true)
            .setContentIntent(action)
            .setOngoing(true)
            .addAction(
                android.app.Notification.Action.Builder(
                    Icon.createWithResource(context, R.drawable.ic_stat_clipbridge),
                    "打开设置",
                    openApp,
                ).build(),
            )
            .build()
    }

    fun cancel(context: Context) {
        context.getSystemService(NotificationManager::class.java).cancel(notificationId)
    }
}
