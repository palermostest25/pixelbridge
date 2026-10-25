using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PixelBridge.Core.Adb;

/// <summary>Validated key pair, kept in memory until the user confirms replacement.</summary>
public sealed class AdbKeyImport
{
    private readonly byte[] _private, _public;
    public string Fingerprint { get; }
    private AdbKeyImport(byte[] secret, byte[] pub, byte[] identity) { _private = secret; _public = pub; Fingerprint = Convert.ToHexString(SHA256.HashData(identity)); }
    public static AdbKeyImport Prepare(string selected)
    {
        var key = selected.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? selected[..^4] : selected;
        byte[] Read(string path) { var info = new FileInfo(path); if (!info.Exists || info.Length > 65536) throw new InvalidDataException("Choose adbkey with its matching adbkey.pub beside it."); return File.ReadAllBytes(path); }
        var secret = Read(key); var pub = Read(key + ".pub");
        try {
            using var rsa = RSA.Create(); rsa.ImportFromPem(Encoding.ASCII.GetString(secret));
            if (rsa.KeySize != 2048) throw new CryptographicException();
            var blob = Convert.FromBase64String(Encoding.ASCII.GetString(pub).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]);
            var parameters = rsa.ExportParameters(false);
            if (blob.Length != 524 || BinaryPrimitives.ReadUInt32LittleEndian(blob) != 64 || !blob.AsSpan(8, 256).SequenceEqual(parameters.Modulus!.Reverse().ToArray()) || BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(520)) != parameters.Exponent!.Aggregate(0u, (n, b) => (n << 8) | b)) throw new CryptographicException();
            return new AdbKeyImport(Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem() + "\n"), pub, rsa.ExportRSAPublicKey());
        } catch (Exception e) when (e is CryptographicException or FormatException or IndexOutOfRangeException or ArgumentException) { throw new InvalidDataException("These files are not a matching, unencrypted RSA-2048 ADB key pair."); }
    }
    public string Install(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android");
        if (!Directory.Exists(directory)) SecureDirectory(directory);
        string[] names = ["adbkey", "adbkey.pub"];
        foreach (var name in names) if (new FileInfo(Path.Combine(directory, name)).LinkTarget != null) throw new IOException("Existing ADB keys are symbolic links. Manage those keys at their original location.");
        var backup = Path.Combine(directory, "pixelbridge-key-backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
        SecureDirectory(Path.GetDirectoryName(backup)!); SecureDirectory(backup);
        byte[][] incoming = [_private, _public];
        var originals = names.Select(name => File.Exists(Path.Combine(directory, name)) ? File.ReadAllBytes(Path.Combine(directory, name)) : null).ToArray();
        for (int i = 0; i < 2; i++) {
            if (originals[i] is { } data) File.WriteAllBytes(Path.Combine(backup, names[i]), data);
            File.WriteAllBytes(Path.Combine(backup, "new-" + names[i]), incoming[i]);
        }
        if (!OperatingSystem.IsWindows()) foreach (var file in Directory.GetFiles(backup)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        int installed = 0;
        try { for (; installed < 2; installed++) File.Move(Path.Combine(backup, "new-" + names[installed]), Path.Combine(directory, names[installed]), true); }
        catch {
            for (int i = 0; i < installed; i++) { var destination = Path.Combine(directory, names[i]); if (originals[i] != null) File.Copy(Path.Combine(backup, names[i]), destination, true); else File.Delete(destination); }
            throw;
        }
        if (!OperatingSystem.IsWindows()) foreach (var name in names) File.SetUnixFileMode(Path.Combine(directory, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return backup;
    }
    private static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) {
            var access = new DirectorySecurity(); var user = WindowsIdentity.GetCurrent().User!;
            access.SetOwner(user); access.SetAccessRuleProtection(true, false);
            access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(access);
        } else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
