package dev.pixelbridge.app.transport

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** HMAC handshake primitives shared with the Windows host (protocol section 3). Pure JVM; unit-tested. */
object AuthCrypto {
    private val random = SecureRandom()

    fun nonce(): String = Base64.getEncoder().encodeToString(ByteArray(32).also(random::nextBytes))

    fun clientMac(key: ByteArray, channel: String, serverNonce: String, clientNonce: String) =
        hmac(key, "PB-AUTH-v1|$channel|$serverNonce|$clientNonce")

    fun serverMac(key: ByteArray, channel: String, serverNonce: String, clientNonce: String) =
        hmac(key, "PB-AUTH-OK-v1|$channel|$serverNonce|$clientNonce")

    fun constantTimeEquals(a: ByteArray, b: ByteArray) = MessageDigest.isEqual(a, b)

    private fun hmac(key: ByteArray, message: String): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(message.toByteArray(Charsets.UTF_8))
    }
}

/** Pairing key storage: the 32-byte key is AES-GCM encrypted with a non-exportable Android Keystore key. */
class PairingStore(context: Context) {
    private val prefs = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    fun isPaired() = prefs.contains(KEY_BLOB)

    fun hostName(): String? = prefs.getString(KEY_HOST, null)

    fun save(key: ByteArray, host: String) {
        require(key.size == 32) { "pairing key must be 32 bytes" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, keystoreKey())
        val blob = cipher.iv + cipher.doFinal(key)
        prefs.edit()
            .putString(KEY_BLOB, Base64.getEncoder().encodeToString(blob))
            .putString(KEY_HOST, host.take(64))
            .putLong(KEY_TIME, System.currentTimeMillis())
            .apply()
    }

    fun load(): ByteArray? {
        val encoded = prefs.getString(KEY_BLOB, null) ?: return null
        return try {
            val blob = Base64.getDecoder().decode(encoded)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, keystoreKey(), GCMParameterSpec(128, blob, 0, 12))
            cipher.doFinal(blob, 12, blob.size - 12)
        } catch (e: Exception) {
            null
        }
    }

    fun clear() {
        prefs.edit().clear().apply()
        KeyStore.getInstance("AndroidKeyStore").apply { load(null) }.deleteEntry(ALIAS)
    }

    private fun keystoreKey(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    private companion object {
        const val ALIAS = "pixelbridge_pairing"
        const val KEY_BLOB = "blob"
        const val KEY_HOST = "host"
        const val KEY_TIME = "time"
    }
}
