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
            var windowsIp by remember { mutableStateOf(preferences.getString("windowsIp", "") ?: "") }
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
            MaterialTheme { Surface(modifier = Modifier.fillMaxSize()) { Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text("ClipBridge", style = MaterialTheme.typography.headlineMedium)
                Text("Windows 电脑的局域网 IP")
                OutlinedTextField(value = windowsIp, onValueChange = { windowsIp = it; preferences.edit().putString("windowsIp", it).apply() }, modifier = Modifier.fillMaxWidth(), singleLine = true, placeholder = { Text("例如 192.168.1.20") })
                Text("配对码（至少 4 位）")
                OutlinedTextField(value = code, onValueChange = { code = it; preferences.edit().putString("pairingCode", it).apply() }, modifier = Modifier.fillMaxWidth(), singleLine = true, visualTransformation = PasswordVisualTransformation())
                Button(onClick = {
                    if (running) {
                        ClipBridgeForegroundService.stop(applicationContext)
                        SyncRuntime.report("已停止。")
                    }
                    else if (code.length < 4 || windowsIp.isBlank()) SyncRuntime.report("请输入 Windows IP 和至少 4 位配对码。")
                    else {
                        preferences.edit().putString("windowsIp", windowsIp).putString("pairingCode", code).apply()
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
                Text("开始同步后会默认启用通知栏“同步当前剪贴板”。Android 系统限制普通后台应用直接读取剪贴板。", style = MaterialTheme.typography.bodySmall)
            } } }
        }
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) window.decorView.post { ClipboardSyncService.syncWhenAppFocused() }
    }
}
