package com.clipbridge

import java.io.OutputStream
import java.io.OutputStreamWriter
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object PayloadIdentity {
    fun hash(text: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
        encode(object : OutputStream() {
            override fun write(value: Int) { digest.update(value.toByte()) }
            override fun write(bytes: ByteArray, offset: Int, length: Int) { digest.update(bytes, offset, length) }
        }, text)
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    fun mac(code: String, vararg parts: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(code.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        val sink = object : OutputStream() {
            override fun write(value: Int) { mac.update(value.toByte()) }
            override fun write(bytes: ByteArray, offset: Int, length: Int) { mac.update(bytes, offset, length) }
        }
        parts.forEach { encode(sink, it) }
        return android.util.Base64.encodeToString(mac.doFinal(), android.util.Base64.NO_WRAP)
    }

    private fun encode(sink: OutputStream, text: String) {
        // Writer.write(String) uses a small encoder buffer; no full UTF-8 copy.
        OutputStreamWriter(sink, Charsets.UTF_8).use { it.write(text) }
    }
}
