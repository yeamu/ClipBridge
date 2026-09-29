package com.clipbridge

import java.io.File

class ClipboardFileCache(private val directory: File, private val maxBytes: Long = 256L * 1024 * 1024,
    private val maxFiles: Int = 50) {
    companion object {
        const val MAX_BYTES = 256L * 1024 * 1024
        const val RETENTION_MS = 7L * 24 * 60 * 60 * 1000
    }
    fun cleanup(protectedNames: Set<String>) {
        val files = directory.listFiles()?.filter { it.name.startsWith("remote-") }
            ?.sortedByDescending { it.lastModified() } ?: return
        var count = 0
        var bytes = 0L
        files.filter { it.name in protectedNames }.forEach { count++; bytes += it.length() }
        files.filter { it.name !in protectedNames }.forEach { file ->
            if (System.currentTimeMillis() - file.lastModified() > RETENTION_MS
                || count >= maxFiles || bytes + file.length() > maxBytes) file.delete()
            else { count++; bytes += file.length() }
        }
    }
}
