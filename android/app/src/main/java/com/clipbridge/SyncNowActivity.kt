package com.clipbridge

import android.app.Activity
import android.os.Bundle
import android.widget.Toast
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay

class SyncNowActivity : Activity() {
    private val scope = CoroutineScope(Job() + Dispatchers.Main)
    private var started = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // A tiny transparent, focusable window reads the clipboard without showing the app UI.
        window.setLayout(1, 1)
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || started) return
        started = true
        window.decorView.post {
            scope.launch {
                // Read only after Android grants input focus, without a fixed launch delay.
                if (!ClipboardSyncService.requestManualSync()) {
                    // Restore the phone listener while this user-opened activity
                    // still has focus, so text AND images use the normal queue.
                    val code = getSharedPreferences("clipbridge-settings", MODE_PRIVATE)
                        .getString("pairingCode", "") ?: ""
                    if (code.length < 4) {
                        Toast.makeText(this@SyncNowActivity, "请先设置至少 4 位配对码", Toast.LENGTH_SHORT).show()
                        finishAndRemoveTask()
                        return@launch
                    }
                    ClipBridgeForegroundService.start(applicationContext)
                    for (attempt in 1..40) {
                        delay(50)
                        if (ClipboardSyncService.requestManualSync()) break
                    }
                }
                Toast.makeText(this@SyncNowActivity, SyncRuntime.status.value, Toast.LENGTH_SHORT).show()
                finishAndRemoveTask()
            }
        }
    }

    override fun finishAndRemoveTask() {
        super.finishAndRemoveTask()
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }

    override fun onDestroy() {
        scope.coroutineContext[Job]?.cancel()
        super.onDestroy()
    }
}
