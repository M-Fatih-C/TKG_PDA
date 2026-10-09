package com.logirobotic.tkg_pda

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Keystore-backed storage for the notification credential. The raw value is
 * never written as plaintext.
 */
internal object TkgCredentialStore {
    private const val alias = "tkg.notification.aes"
    private const val fileName = "tkg-notification-credential.bin"
    private const val seenFile = "tkg-seen-events.txt"

    data class Stored(val credential: String, val baseUrl: String, val registrationId: String)

    fun save(context: Context, credential: String, baseUrl: String, registrationId: String) {
        val json = JSONObject()
            .put("credential", credential)
            .put("baseUrl", baseUrl)
            .put("registrationId", registrationId)
            .toString()
            .toByteArray(Charsets.UTF_8)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val encrypted = cipher.doFinal(json)
        val payload = ByteArray(cipher.iv.size + encrypted.size)
        System.arraycopy(cipher.iv, 0, payload, 0, cipher.iv.size)
        System.arraycopy(encrypted, 0, payload, cipher.iv.size, encrypted.size)
        context.filesDir.resolve(fileName).writeBytes(payload)
    }

    fun load(context: Context): Stored? {
        val file = context.filesDir.resolve(fileName)
        if (!file.exists()) return null
        return try {
            val payload = file.readBytes()
            if (payload.size <= 12) return null
            val iv = payload.copyOfRange(0, 12)
            val encrypted = payload.copyOfRange(12, payload.size)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
            val json = JSONObject(String(cipher.doFinal(encrypted), Charsets.UTF_8))
            val credential = json.optString("credential")
            val baseUrl = json.optString("baseUrl")
            if (credential.isBlank() || baseUrl.isBlank()) null
            else Stored(credential, baseUrl, json.optString("registrationId"))
        } catch (_: Exception) {
            null
        }
    }

    fun clear(context: Context) {
        context.filesDir.resolve(fileName).delete()
        context.filesDir.resolve(seenFile).delete()
    }

    fun hasSeen(context: Context, eventKey: String): Boolean =
        readSeen(context).contains(eventKey)

    fun remember(context: Context, eventKey: String) {
        val keys = readSeen(context).toMutableList()
        if (keys.contains(eventKey)) return
        keys.add(eventKey)
        val trimmed = if (keys.size > 200) keys.takeLast(200) else keys
        context.filesDir.resolve(seenFile).writeText(trimmed.joinToString("\n"))
    }

    private fun readSeen(context: Context): List<String> {
        val file = context.filesDir.resolve(seenFile)
        if (!file.exists()) return emptyList()
        return file.readText().lineSequence().map { it.trim() }.filter { it.isNotEmpty() }.toList()
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val existing = store.getKey(alias, null) as? SecretKey
        if (existing != null) return existing
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build()
        )
        return generator.generateKey()
    }
}
