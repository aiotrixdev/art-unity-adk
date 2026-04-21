using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ART.ADK
{
    // ─────────────────────────────────────────
    // Error types
    // ─────────────────────────────────────────
    public enum EncryptionError
    {
        InvalidBase64,
        InvalidKeyLength,
        DataTooShort,
        Utf8DecodeFailed,
        AuthenticationFailed
    }

    public class EncryptionHelperException : Exception
    {
        public EncryptionError ErrorType { get; }
        public EncryptionHelperException(EncryptionError errorType) : base(GetMessage(errorType))
            => ErrorType = errorType;

        static string GetMessage(EncryptionError e) => e switch
        {
            EncryptionError.InvalidBase64        => "Failed to decode base64 string.",
            EncryptionError.InvalidKeyLength     => "Invalid key length.",
            EncryptionError.DataTooShort         => "Encrypted data is too short.",
            EncryptionError.Utf8DecodeFailed     => "Failed to decode decrypted data as UTF-8.",
            EncryptionError.AuthenticationFailed => "Authentication tag mismatch.",
            _                                    => "Unknown encryption error."
        };
    }

    // ─────────────────────────────────────────
    // EncryptionHelper — public API
    // Uses NaclBox (Curve25519 + XSalsa20-Poly1305) from NaclImpl.cs
    // ─────────────────────────────────────────
    public static class EncryptionHelper
    {
        public const int PublicKeyLength = 32;
        public const int SecretKeyLength = 32;
        public const int NonceLength     = 24;

        // ── Generate Key Pair ────────────────────────────────────────────────
        public static KeyPairType GenerateKeyPair()
        {
            NaclBox.KeyPair(out byte[] pk, out byte[] sk);
            return new KeyPairType(Convert.ToBase64String(pk), Convert.ToBase64String(sk));
        }

        // ── Encrypt ──────────────────────────────────────────────────────────
        /// <summary>
        /// Encrypt a UTF-8 message for recipientPublicKey from senderPrivateKey.
        /// Output format: Base64( nonce(24) + tag(16) + ciphertext )
        /// </summary>
        public static string Encrypt(string message, string recipientPublicKey, string senderPrivateKey)
        {
            byte[] pub, priv;
            try { pub  = Convert.FromBase64String(recipientPublicKey); }
            catch { throw new EncryptionHelperException(EncryptionError.InvalidBase64); }
            try { priv = Convert.FromBase64String(senderPrivateKey); }
            catch { throw new EncryptionHelperException(EncryptionError.InvalidBase64); }

            if (pub.Length  != PublicKeyLength) throw new EncryptionHelperException(EncryptionError.InvalidKeyLength);
            if (priv.Length != SecretKeyLength) throw new EncryptionHelperException(EncryptionError.InvalidKeyLength);

            byte[] msgBytes = Encoding.UTF8.GetBytes(message);

            Debug.Log($"[ENCRYPT] Plaintext ({msgBytes.Length} bytes): {message}");
            Debug.Log($"[ENCRYPT] RecipientPublicKey: {recipientPublicKey}");

            // Random 24-byte nonce
            var nonce = new byte[NonceLength];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(nonce);
            Debug.Log($"[ENCRYPT] Nonce (base64): {Convert.ToBase64String(nonce)}");

            // NaCl box: tag(16) + ciphertext
            byte[] boxed = NaclBox.Box(msgBytes, nonce, pub, priv);

            // Prepend nonce: full = nonce(24) + boxed
            var full = new byte[NonceLength + boxed.Length];
            Array.Copy(nonce,  0, full, 0,          NonceLength);
            Array.Copy(boxed,  0, full, NonceLength, boxed.Length);

            var result = Convert.ToBase64String(full);
            Debug.Log($"[ENCRYPT] Ciphertext ({full.Length} bytes total, base64 len={result.Length}): {result.Substring(0, Mathf.Min(60, result.Length))}...");
            return result;
        }

        // ── Decrypt ──────────────────────────────────────────────────────────
        /// <summary>
        /// Decrypt a message from senderPublicKey to recipientPrivateKey.
        /// Input format: Base64( nonce(24) + tag(16) + ciphertext )
        /// </summary>
        public static string Decrypt(string encryptedData, string senderPublicKey, string recipientPrivateKey)
        {
            byte[] full, pub, priv;
            try { full = Convert.FromBase64String(encryptedData); }
            catch { throw new EncryptionHelperException(EncryptionError.InvalidBase64); }
            try { pub  = Convert.FromBase64String(senderPublicKey); }
            catch { throw new EncryptionHelperException(EncryptionError.InvalidBase64); }
            try { priv = Convert.FromBase64String(recipientPrivateKey); }
            catch { throw new EncryptionHelperException(EncryptionError.InvalidBase64); }

            if (pub.Length  != PublicKeyLength) throw new EncryptionHelperException(EncryptionError.InvalidKeyLength);
            if (priv.Length != SecretKeyLength) throw new EncryptionHelperException(EncryptionError.InvalidKeyLength);
            if (full.Length < NonceLength + NaclBox.OverheadBytes)
                throw new EncryptionHelperException(EncryptionError.DataTooShort);

            Debug.Log($"[DECRYPT] Ciphertext total size: {full.Length} bytes");
            Debug.Log($"[DECRYPT] SenderPublicKey: {senderPublicKey}");

            // Split nonce and box
            var nonce = new byte[NonceLength];
            var box   = new byte[full.Length - NonceLength];
            Array.Copy(full, 0,          nonce, 0, NonceLength);
            Array.Copy(full, NonceLength, box,  0, box.Length);
            Debug.Log($"[DECRYPT] Nonce (base64): {Convert.ToBase64String(nonce)}");
            Debug.Log($"[DECRYPT] Box (tag+cipher) size: {box.Length} bytes");

            // NaclBox.Open throws AuthenticationFailed on tag mismatch
            byte[] plainBytes = NaclBox.Open(box, nonce, pub, priv);

            try
            {
                var plaintext = Encoding.UTF8.GetString(plainBytes);
                Debug.Log($"[DECRYPT] ✅ Decrypted plaintext ({plainBytes.Length} bytes): {plaintext}");
                return plaintext;
            }
            catch { throw new EncryptionHelperException(EncryptionError.Utf8DecodeFailed); }
        }
    }
}