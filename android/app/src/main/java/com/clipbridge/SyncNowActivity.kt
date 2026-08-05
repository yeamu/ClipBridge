package com.clipbridge

import android.app.Activity
import android.content.ClipboardManager
import android.os.Bundle
import android.widget.Toast
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class SyncNowActivity : Activity() {
    private val scope = CoroutineScope(Job() + Dispatchers.Main)
    private var started = false

    override fun onResume() {
        super.onResume()
        if (started) return
        started = true
        window.decorView.post {
            scope.launch {
                // Give Android one frame to register this no-UI activity as focused.
                delay(80)
                val result = when {
                    ClipboardSyncService.requestManualSync() -> "正在同步当前剪贴板"
                    currentClipboardText().isNullOrBlank() -> "没有可同步的文本或图片"
                    else -> withContext(Dispatchers.IO) { OneShotClipboardSync.send(this@SyncNowActivity, currentClipboardText()!!) }
                }
                Toast.makeText(this@SyncNowActivity, result, Toast.LENGTH_SHORT).show()
                finishAndRemoveTask()
            }
        }
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
