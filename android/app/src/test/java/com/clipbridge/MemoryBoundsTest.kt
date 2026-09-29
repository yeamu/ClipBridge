package com.clipbridge

import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], manifest = Config.NONE)
class MemoryBoundsTest {
    @Test fun `queues bound bytes and items and release only confirmed head`() {
        val queue = BoundedMemoryQueue<ByteArray>({ it.size.toLong() }, 3, 10, 30)
        val first = ByteArray(6)
        assertTrue(queue.add(first)); assertFalse(queue.add(ByteArray(6)))
        assertSame(first, queue.peek()); assertFalse(queue.complete(ByteArray(6)))
        assertTrue(queue.complete(first))
        assertTrue(queue.add(ByteArray(25))); assertFalse(queue.add(ByteArray(1)))
        queue.clear(); assertNull(queue.peek()); assertFalse(queue.add(ByteArray(31)))
        repeat(3) { assertTrue(queue.add(ByteArray(1))) }
        assertFalse(queue.add(ByteArray(1)))
    }

    @Test fun `seen IDs are bounded and UTF-8 hashes preserve protocol`() {
        val seen = RecentMessageIds(3)
        assertTrue(seen.add("1")); assertFalse(seen.add("1"))
        listOf("2", "3", "4").forEach { seen.add(it) }
        assertFalse(seen.contains("1")); assertTrue(seen.contains("4"))
        val text = "中文 📋\n".repeat(2000)
        val hash = MessageDigest.getInstance("SHA-256").digest(text.toByteArray()).joinToString("") { "%02x".format(it) }
        assertEquals(hash, PayloadIdentity.hash(text))
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec("code".toByteArray(), "HmacSHA256"))
        val expected = java.util.Base64.getEncoder().encodeToString(mac.doFinal(("prefix|" + text + "|suffix").toByteArray()))
        assertEquals(expected, PayloadIdentity.mac("code", "prefix|", text, "|suffix"))
    }

    @Test fun `cache expiration and quota keep current referenced file`() {
        val folder = Files.createTempDirectory("clipbridge-cache-test").toFile()
        val current = File(folder, "remote-current.gif").apply { writeBytes(ByteArray(16)); setLastModified(1) }
        val expired = File(folder, "remote-expired.gif").apply { writeBytes(ByteArray(16)); setLastModified(1) }
        repeat(70) { File(folder, "remote-$it.gif").writeBytes(ByteArray(16)) }
        ClipboardFileCache(folder).cleanup(setOf(current.name))
        assertTrue(current.exists()); assertFalse(expired.exists())
        assertTrue(folder.listFiles()!!.size <= 50)
        ClipboardFileCache(folder, maxBytes = 32).cleanup(setOf(current.name))
        assertTrue(current.exists()); assertTrue(folder.listFiles()!!.sumOf { it.length() } <= 32)
    }
}
