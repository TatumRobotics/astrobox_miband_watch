using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace XiaomiAstroBoxCSharp.Protocol;

public static class CryptographyHelper
{
    /// <summary>
    /// AES-128-CCM encryption with 4-byte tag
    /// Matches Rust: type Aes128Ccm = Ccm<Aes128, U4, U12>
    /// </summary>
    public static byte[] Aes128CcmEncrypt(byte[] key, byte[] nonce, byte[] aad, byte[] plaintext)
    {
        if (key.Length != 16)
            throw new ArgumentException("Key must be 16 bytes", nameof(key));
        if (nonce.Length != 12)
            throw new ArgumentException("Nonce must be 12 bytes", nameof(nonce));

        // .NET AesCcm with 4-byte tag (32 bits)
        using var ccm = new AesCcm(key);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[4]; // 4-byte tag to match Rust U4

        ccm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        // Concatenate ciphertext + tag (matches Rust return format)
        var result = new byte[ciphertext.Length + tag.Length];
        Array.Copy(ciphertext, 0, result, 0, ciphertext.Length);
        Array.Copy(tag, 0, result, ciphertext.Length, tag.Length);

        return result;
    }

    /// <summary>
    /// AES-128-CCM decryption with 4-byte tag
    /// </summary>
    public static byte[] Aes128CcmDecrypt(byte[] key, byte[] nonce, byte[] aad, byte[] ciphertextAndTag)
    {
        if (key.Length != 16)
            throw new ArgumentException("Key must be 16 bytes", nameof(key));
        if (nonce.Length != 12)
            throw new ArgumentException("Nonce must be 12 bytes", nameof(nonce));
        if (ciphertextAndTag.Length < 4)
            throw new ArgumentException("Ciphertext must include 4-byte tag", nameof(ciphertextAndTag));

        using var ccm = new AesCcm(key);

        // Split ciphertext and tag
        var tagLength = 4;
        var ciphertext = new byte[ciphertextAndTag.Length - tagLength];
        var tag = new byte[tagLength];

        Array.Copy(ciphertextAndTag, 0, ciphertext, 0, ciphertext.Length);
        Array.Copy(ciphertextAndTag, ciphertext.Length, tag, 0, tagLength);

        var plaintext = new byte[ciphertext.Length];
        ccm.Decrypt(nonce, ciphertext, tag, plaintext, aad);

        return plaintext;
    }

    /// <summary>
    /// AES-128-CTR encryption/decryption (symmetric operation)
    /// Matches Rust: Ctr128BE<Aes128>
    /// </summary>
    public static byte[] Aes128CtrCrypt(byte[] key, byte[] iv, byte[] data)
    {
        if (key.Length != 16)
            throw new ArgumentException("Key must be 16 bytes", nameof(key));
        if (iv.Length != 16)
            throw new ArgumentException("IV must be 16 bytes", nameof(iv));

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var output = new byte[data.Length];
        var counter = new byte[16];
        Array.Copy(iv, counter, 16);

        using var encryptor = aes.CreateEncryptor();

        for (int i = 0; i < data.Length; i += 16)
        {
            var keystream = new byte[16];
            encryptor.TransformBlock(counter, 0, 16, keystream, 0);

            var blockSize = Math.Min(16, data.Length - i);
            for (int j = 0; j < blockSize; j++)
            {
                output[i + j] = (byte)(data[i + j] ^ keystream[j]);
            }

            // Increment counter (big-endian to match Ctr128BE)
            IncrementCounterBE(counter);
        }

        return output;
    }

    /// <summary>
    /// Increment counter in big-endian format (matches Ctr128BE)
    /// </summary>
    private static void IncrementCounterBE(byte[] counter)
    {
        // Increment from the right (big-endian)
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    /// <summary>
    /// HMAC-SHA256
    /// </summary>
    public static byte[] HmacSha256(byte[] key, params byte[][] data)
    {
        using var hmac = new HMACSHA256(key);

        if (data.Length == 1)
        {
            return hmac.ComputeHash(data[0]);
        }

        // Multiple inputs - concatenate and hash
        var combined = data.SelectMany(d => d).ToArray();
        return hmac.ComputeHash(combined);
    }

    /// <summary>
    /// KDF for MiWear authentication
    /// Direct port from Rust kdf_miwear function
    /// </summary>
    public static byte[] KdfMiWear(byte[] secretKey, byte[] phoneNonce, byte[] watchNonce)
    {
        if (secretKey.Length != 16)
            throw new ArgumentException("Secret key must be 16 bytes", nameof(secretKey));
        if (phoneNonce.Length != 16)
            throw new ArgumentException("Phone nonce must be 16 bytes", nameof(phoneNonce));
        if (watchNonce.Length != 16)
            throw new ArgumentException("Watch nonce must be 16 bytes", nameof(watchNonce));

        // 1) hmac_key = HMAC(init_key, secret_key)
        var initKey = new byte[32];
        Array.Copy(phoneNonce, 0, initKey, 0, 16);
        Array.Copy(watchNonce, 0, initKey, 16, 16);

        var hmacKey = HmacSha256(initKey, secretKey);

        // 2) Expand to 64 bytes using HKDF-like expansion with tag "miwear-auth"
        var okm = new byte[64];
        var tag = System.Text.Encoding.UTF8.GetBytes("miwear-auth");
        var offset = 0;
        var prev = Array.Empty<byte>();

        for (byte counter = 1; counter <= 3; counter++)
        {
            // Build: prev || tag || counter
            var data = new List<byte>();
            data.AddRange(prev);
            data.AddRange(tag);
            data.Add(counter);

            prev = HmacSha256(hmacKey, data.ToArray());

            var end = Math.Min(offset + 32, 64);
            var copyLen = end - offset;
            Array.Copy(prev, 0, okm, offset, copyLen);
            offset = end;
        }

        return okm;
    }

    /// <summary>
    /// Convert hex string to 16-byte array
    /// Matches Rust string_to_u8_16
    /// </summary>
    public static byte[] StringToBytes16(string hex)
    {
        if (hex.Length != 32)
            return null;

        var result = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            var byteStr = hex.Substring(i * 2, 2);
            if (!byte.TryParse(byteStr, System.Globalization.NumberStyles.HexNumber, null, out result[i]))
                return null;
        }
        return result;
    }

    /// <summary>
    /// Generate random bytes
    /// Matches Rust generate_random_bytes
    /// </summary>
    public static byte[] GenerateRandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// <summary>
    /// Convert byte array to hex string
    /// Matches Rust to_hex_string
    /// </summary>
    public static string ToHexString(byte[] data)
    {
        return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Convert hex string to bytes
    /// Matches Rust hex_stream_to_bytes
    /// </summary>
    public static byte[] HexStreamToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
            return null;

        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out result[i]))
                return null;
        }
        return result;
    }
}

/// <summary>
/// L2 cipher implementation for encrypting/decrypting L2 packets
/// Uses AES-128-CTR mode for symmetric encryption
/// </summary>
public class L2Cipher : IL2Cipher
{
    private readonly byte[] _encKey;
    private readonly byte[] _decKey;
    private readonly ILogger _logger;

    public L2Cipher(byte[] encKey, byte[] decKey, ILogger logger = null)
    {
        _encKey = encKey;
        _decKey = decKey;
        _logger = logger;
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        _logger?.LogTrace("Encrypting {Length} bytes", plaintext.Length);
        var result = CryptographyHelper.Aes128CtrCrypt(_encKey, _encKey, plaintext);
        _logger?.LogTrace("Encrypted to {Length} bytes", result.Length);
        return result;
    }

    public byte[] Decrypt(byte[] ciphertext)
    {
        _logger?.LogTrace("Decrypting {Length} bytes", ciphertext.Length);
        var result = CryptographyHelper.Aes128CtrCrypt(_decKey, _decKey, ciphertext);
        _logger?.LogTrace("Decrypted to {Length} bytes", result.Length);
        return result;
    }
}