package com.clipbridge

import android.app.Activity
import android.content.ClipboardManager
import android.os.Bundle
import android.widget.Toast
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

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
                val text = if (ClipboardSyncService.requestManualSync()) {
                    Toast.makeText(this@SyncNowActivity, "正在同步当前剪贴板", Toast.LENGTH_SHORT).show()
                    finishAndRemoveTask()
                    return@launch
                } else currentClipboardText()
                val result = when {
                    text.isNullOrBlank() -> "没有可同步的文本或图片"
                    else -> withContext(Dispatchers.IO) { OneShotClipboardSync.send(this@SyncNowActivity, text) }
                }
                Toast.makeText(this@SyncNowActivity, result, Toast.LENGTH_SHORT).show()
                finishAndRemoveTask()
            }
        }
    }

    override fun finishAndRemoveTask() {
        super.finishAndRemoveTask()
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }

    private fun currentClipboardText(): String? = try {
        getSystemService(ClipboardManager::class.java)
            .primaryClip
            ?.getItemAt(0)
            ?.coerceToText(this)
            ?.toString()
            ?.takeIf { it.isNotBlank() }
    } catch (_: SecurityException) {
        null
    }

    override fun onDestroy() {
        scope.coroutineContext[Job]?.cancel()
        super.onDestroy()
    }
}
