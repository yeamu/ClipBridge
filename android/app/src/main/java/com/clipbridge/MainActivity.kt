package com.clipbridge

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            val preferences = remember { getSharedPreferences("clipbridge-settings", MODE_PRIVATE) }
            var phoneIp by remember { mutableStateOf<String?>(null) }
            DisposableEffect(Unit) {
                val monitor = WifiNetworkMonitor(applicationContext) { endpoint ->
                    runOnUiThread { phoneIp = endpoint?.address?.hostAddress }
                }
                monitor.start()
                onDispose { monitor.stop() }
            }
            var code by remember { mutableStateOf(preferences.getString("pairingCode", "") ?: "") }
            val status by SyncRuntime.status.collectAsState()
            val running by SyncRuntime.running.collectAsState()
            var waitingForNotificationPermission by remember { mutableStateOf(false) }

            fun startSync() {
                ClipBridgeForegroundService.start(applicationContext)
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                    checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
                    SyncRuntime.report("正在启动同步；通知栏一键同步已启用。")
                } else {
                    SyncRuntime.report("正在启动同步；未授予通知权限，通知栏一键同步不可用。")
                }
            }

            val notificationPermission = rememberLauncherForActivityResult(
                ActivityResultContracts.RequestPermission(),
            ) { _ ->
                if (waitingForNotificationPermission) {
                    waitingForNotificationPermission = false
                    startSync()
                }
            }
            MaterialTheme { Surface(modifier = Modifier.fillMaxSize()) { Column(Modifier.verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text("ClipBridge", style = MaterialTheme.typography.headlineMedium)
                Text("本机 Wi-Fi IP（自动获取）")
                OutlinedTextField(value = phoneIp ?: "尚未连接 Wi-Fi / 未获取 IPv4", onValueChange = {}, readOnly = true, modifier = Modifier.fillMaxWidth(), singleLine = true)
                Text("在 Windows 端填写此 IP。切换 Wi-Fi 后自动尝试同步一次；IP 改变时请更新 Windows 设置。", style = MaterialTheme.typography.bodySmall)
                Text("配对码（至少 4 位）")
                OutlinedTextField(value = code, onValueChange = { code = it; preferences.edit().putString("pairingCode", it).apply() }, enabled = !running, modifier = Modifier.fillMaxWidth(), singleLine = true, visualTransformation = PasswordVisualTransformation())
                Button(onClick = {
                    if (running) {
                        ClipBridgeForegroundService.stop(applicationContext)
                        SyncRuntime.report("已停止。")
                    }
                    else if (code.length < 4) SyncRuntime.report("请输入至少 4 位配对码。")
                    else {
                        preferences.edit().putString("pairingCode", code).remove("windowsIp").apply()
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                            waitingForNotificationPermission = true
                            SyncRuntime.report("请允许通知权限，以启用通知栏一键同步。")
                        notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        } else {
                            startSync()
                        }
                    }
                }) { Text(if (running) "停止同步" else "开始同步") }
                HorizontalDivider(); Text(status, style = MaterialTheme.typography.bodyMedium)
                Text("复制后直接点按 ClipBridge 通知即可同步，无需展开；展开通知可打开设置。Android 系统限制普通后台应用直接读取剪贴板。", style = MaterialTheme.typography.bodySmall)
            } } }
        }
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) window.decorView.post { ClipboardSyncService.syncWhenAppFocused() }
    }
}
