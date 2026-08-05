package com.clipbridge

import kotlinx.coroutines.flow.MutableStateFlow

object SyncRuntime {
    val running = MutableStateFlow(false)
    val status = MutableStateFlow("尚未启动。请与 Windows 填入相同配对码。")

    fun report(message: String) {
        status.value = message
    }
}
