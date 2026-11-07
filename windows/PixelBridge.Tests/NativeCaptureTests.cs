using PixelBridge.Core;

namespace PixelBridge.Tests;

public class NativeCaptureTests
{
    [Fact]
    public void OnlySafeNonemptyCameraOriginals()
    {
        var files = NativeCapture.Files("""
            Row: 0 _id=17, _display_name=PXL_20261107_120000.jpg, _size=123
            Row: 1 _id=18, _display_name=PXL_20261107_120001.mp4, _size=456
            Row: 2 _id=19, _display_name=PXL_../../other.jpg, _size=789
            Row: 3 _id=20, _display_name=PXL_empty.jpg, _size=0
            Row: 4 _id=21, _display_name=PXL_hidden.exe, _size=1
            """).ToArray();
        Assert.Equal(new long[] { 17, 18 }, files.Select(file => file.Id));
        Assert.Equal(new long[] { 123, 456 }, files.Select(file => file.Bytes));
    }

    [Fact]
    public void SavingOriginalDoesNotReplaceExistingFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var existing = Path.Combine(dir.FullName, "PXL_test.jpg");
            var temp = Path.Combine(dir.FullName, "pending.part");
            File.WriteAllBytes(existing, [0]);
            File.WriteAllBytes(temp, [1, 2, 3]);
            var saved = NativeCapture.Save(temp, "PXL_test.jpg", dir.FullName);
            Assert.Equal("PXL_test (1).jpg", Path.GetFileName(saved));
            Assert.Equal(new byte[] { 0 }, File.ReadAllBytes(existing));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(saved));
            Assert.False(File.Exists(temp));
        }
        finally { dir.Delete(recursive: true); }
    }
}
