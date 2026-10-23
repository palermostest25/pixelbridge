using System.Security.Cryptography;
using System.Text;

namespace PixelBridge.Core.Protocol;

/// <summary>HMAC-SHA256 handshake primitives (protocol section 3); must match android AuthCrypto.kt.</summary>
public static class AuthCrypto
{
    public static string Nonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static byte[] ClientMac(byte[] key, string channel, string serverNonce, string clientNonce) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"PB-AUTH-v1|{channel}|{serverNonce}|{clientNonce}"));

    public static byte[] ServerMac(byte[] key, string channel, string serverNonce, string clientNonce) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"PB-AUTH-OK-v1|{channel}|{serverNonce}|{clientNonce}"));

    public static bool FixedTimeEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
}
