using System.Runtime.InteropServices;

namespace PixelBridge.Media;

internal static partial class Native
{
    private const string Dll = "PixelBridge.Native.dll";

    [LibraryImport(Dll)] internal static partial int pb_dec_create(out nint handle);
    [LibraryImport(Dll)] internal static unsafe partial int pb_dec_decode(nint h, byte* data, uint len, long ts100ns);
    [LibraryImport(Dll)] internal static unsafe partial int pb_dec_latest(nint h, byte* dst, uint capacity, out uint width, out uint height, out long ts100ns);
    [LibraryImport(Dll)] internal static partial int pb_dec_flush(nint h);
    [LibraryImport(Dll)] internal static partial void pb_dec_destroy(nint h);
    [LibraryImport(Dll)] internal static unsafe partial void pb_nv12_to_bgra(byte* nv12, uint w, uint h, byte* bgra, uint dstStride, [MarshalAs(UnmanagedType.Bool)] bool mirror);
}

/// <summary>A decoded, tightly packed NV12 frame.</summary>
public sealed class Nv12Frame(int width, int height, byte[] data, long timestampNs)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Data { get; } = data;
    public long TimestampNs { get; } = timestampNs;

    public unsafe void ToBgra(byte[] dst, int stride, bool mirror = false)
    {
        fixed (byte* s = Data) fixed (byte* d = dst)
            Native.pb_nv12_to_bgra(s, (uint)Width, (uint)Height, d, (uint)stride, mirror);
    }
}

/// <summary>Media Foundation H.264 decoder (Microsoft H264 decoder MFT) via PixelBridge.Native.dll.</summary>
public sealed class H264Decoder : IDisposable
{
    private nint _h;
    private byte[] _scratch = new byte[1920 * 1080 * 3 / 2];
    public int DecodeErrors { get; private set; }
    public long FramesOut { get; private set; }

    public H264Decoder()
    {
        int hr = Native.pb_dec_create(out _h);
        if (hr < 0) throw new COMException("H.264 decoder creation failed", hr);
    }

    public unsafe void Decode(ReadOnlySpan<byte> accessUnit, long timestampNs)
    {
        fixed (byte* p = accessUnit)
        {
            int hr = Native.pb_dec_decode(_h, p, (uint)accessUnit.Length, timestampNs / 100);
            if (hr < 0) DecodeErrors++;
        }
    }

    /// <summary>Returns the newest decoded frame, or null if nothing new since the last call.</summary>
    public unsafe Nv12Frame? TakeLatest()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int hr;
            uint w, h; long ts;
            fixed (byte* d = _scratch) hr = Native.pb_dec_latest(_h, d, (uint)_scratch.Length, out w, out h, out ts);
            if (hr == 1) return null; // S_FALSE
            if (hr == unchecked((int)0x8007007A)) { _scratch = new byte[w * h * 3 / 2]; continue; } // insufficient buffer
            if (hr < 0) return null;
            var size = (int)(w * h * 3 / 2);
            FramesOut++;
            return new Nv12Frame((int)w, (int)h, _scratch.AsSpan(0, size).ToArray(), ts * 100);
        }
        return null;
    }

    public void Flush() => Native.pb_dec_flush(_h);

    public void Dispose()
    {
        if (_h != 0) { Native.pb_dec_destroy(_h); _h = 0; }
    }
}

public static class Bmp
{
    /// <summary>Write a 32-bit top-down BMP (dependency-free snapshot for tests/diagnostics).</summary>
    public static void Write(string path, byte[] bgra, int w, int h)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int size = w * h * 4;
        bw.Write((ushort)0x4D42); bw.Write(54 + size); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(0); bw.Write(size);
        bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(bgra, 0, size);
    }
}
