using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using PixelBridge.Core.Adb;

public sealed class AdbKeyImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pixelbridge-key-test-" + Guid.NewGuid().ToString("N"));
    public AdbKeyImportTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Pair(string name)
    {
        using var rsa = RSA.Create(2048); var p = rsa.ExportParameters(false);
        var n = new BigInteger(p.Modulus!, isUnsigned: true, isBigEndian: true);
        var inverse = BigInteger.ModPow(n & uint.MaxValue, (BigInteger.One << 31) - 1, BigInteger.One << 32);
        var blob = new byte[524]; BinaryPrimitives.WriteUInt32LittleEndian(blob, 64);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), unchecked(0u - (uint)inverse));
        p.Modulus!.Reverse().ToArray().CopyTo(blob, 8);
        BigInteger.ModPow(2, 4096, n).ToByteArray(isUnsigned: true).CopyTo(blob, 264);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(520), 65537);
        var path = Path.Combine(_root, name); File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem()); File.WriteAllText(path + ".pub", Convert.ToBase64String(blob) + " test@fixture\n"); return path;
    }
    [Fact] public void PrepareAcceptsBothFileSelectionsWithoutChangingKeys()
    {
        var path = Pair("adbkey");
        Assert.Equal(AdbKeyImport.Prepare(path).Fingerprint, AdbKeyImport.Prepare(path + ".pub").Fingerprint);
        Assert.False(Directory.Exists(Path.Combine(_root, "pixelbridge-key-backups")));
    }
    [Fact] public void MismatchedOrMissingPublicKeysAreRejected()
    {
        var first = Pair("first"); var second = Pair("second");
        File.Copy(second + ".pub", first + ".pub", true);
        Assert.Throws<InvalidDataException>(() => AdbKeyImport.Prepare(first));
        File.Delete(first + ".pub"); Assert.Throws<InvalidDataException>(() => AdbKeyImport.Prepare(first));
    }
    [Fact] public void ImportBacksUpBothKeysAndRestrictsPrivateFilePermissions()
    {
        var pair = Pair("source"); var destination = Path.Combine(_root, "destination"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "adbkey"), "old-private"); File.WriteAllText(Path.Combine(destination, "adbkey.pub"), "old-public");
        var plan = AdbKeyImport.Prepare(pair); var backup = plan.Install(destination);
        Assert.Equal("old-private", File.ReadAllText(Path.Combine(backup, "adbkey")));
        Assert.Equal("old-public", File.ReadAllText(Path.Combine(backup, "adbkey.pub")));
        Assert.Equal(plan.Fingerprint, AdbKeyImport.Prepare(Path.Combine(destination, "adbkey")).Fingerprint);
        if (!OperatingSystem.IsWindows()) {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(destination, "adbkey")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(backup));
        }
    }
    [Fact] public void ImportNeverFollowsAnExistingKeySymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        var pair = Pair("source"); var destination = Path.Combine(_root, "destination"); Directory.CreateDirectory(destination);
        var external = Path.Combine(_root, "unrelated"); File.WriteAllText(external, "unchanged");
        File.CreateSymbolicLink(Path.Combine(destination, "adbkey"), external);
        Assert.Throws<IOException>(() => AdbKeyImport.Prepare(pair).Install(destination)); Assert.Equal("unchanged", File.ReadAllText(external));
    }
}
