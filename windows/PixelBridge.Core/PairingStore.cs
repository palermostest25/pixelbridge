using System.Security.Cryptography;
using System.Text.Json;

namespace PixelBridge.Core;

/// <summary>Per-device 32-byte pairing keys protected with DPAPI (CurrentUser) under %APPDATA%\PixelBridge.</summary>
public sealed class PairingStore(string? directory = null)
{
    private readonly string _dir = directory ?? AppPaths.DataDir;
    private static readonly byte[] Entropy = "PixelBridge.pairing.v1"u8.ToArray();

    private string PathFor(string serial) => Path.Combine(_dir, $"pairing-{Sanitize(serial)}.bin");

    public byte[]? Load(string serial)
    {
        var p = PathFor(serial);
        if (!File.Exists(p)) return null;
        try { return Unprotect(File.ReadAllBytes(p)); }
        catch (CryptographicException) { return null; }
    }

    public byte[] CreateAndSave(string serial)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(_dir);
        var path = PathFor(serial);
        if (!OperatingSystem.IsWindows())
        {
            // Create the file owner-only before writing the secret.
            File.WriteAllBytes(path, []);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.WriteAllBytes(path, Protect(key));
        File.WriteAllText(Path.Combine(_dir, $"pairing-{Sanitize(serial)}.json"),
            JsonSerializer.Serialize(new { serial, pairedUtc = DateTime.UtcNow }));
        return key;
    }

    public DateTime? PairedUtc(string serial)
    {
        var p = Path.Combine(_dir, $"pairing-{Sanitize(serial)}.json");
        if (!File.Exists(p)) return null;
        try { return JsonDocument.Parse(File.ReadAllText(p)).RootElement.GetProperty("pairedUtc").GetDateTime(); }
        catch { return null; }
    }

    public void Delete(string serial)
    {
        File.Delete(PathFor(serial));
        File.Delete(Path.Combine(_dir, $"pairing-{Sanitize(serial)}.json"));
    }

    private static string Sanitize(string s) => string.Concat(s.Where(char.IsLetterOrDigit));

    // Windows: DPAPI (CurrentUser). Linux/macOS: owner-only file (0600) in the user's config directory.
    private static byte[] Protect(byte[] key) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser) : [.. "PB1"u8, .. key];

    private static byte[] Unprotect(byte[] blob)
    {
        if (OperatingSystem.IsWindows()) return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        if (blob.Length != 35 || blob[0] != 'P' || blob[1] != 'B' || blob[2] != '1') throw new CryptographicException("bad pairing file");
        return blob[3..];
    }
}

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PixelBridge");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string RecordingsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PixelBridge");
}
