using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using PixelBridge.Core;

namespace PixelBridge.Media;

/// <summary>
/// Writes decoded NV12 frames into the shared section read by the PixelBridge Camera media source.
/// Layout mirrors PixelBridge.VirtualCam/SharedFrame.h. The section is created by the media source
/// (inside the Frame Server service); this writer only opens it.
/// </summary>
public sealed unsafe class VirtualCamFeed : IDisposable
{
    public const string GlobalName = @"Global\PixelBridgeVirtualCamera.v1";
    public const string LocalName = @"Local\PixelBridgeVirtualCamera.v1";
    private const uint Magic = 0x43564250;
    private const int HeaderBytes = 4096;
    public const int MaxW = 1920, MaxH = 1080;
    private const long TotalBytes = HeaderBytes + MaxW * MaxH * 3L / 2;

    // Header field offsets (pack 4).
    private const int OffMagic = 0, OffSeq = 8, OffWidth = 16, OffHeight = 20, OffState = 24, OffWriterTick = 32,
        OffFrameTime = 40, OffFramesWritten = 48, OffReaderTick = 56;

    public enum FeedState : uint { Offline = 0, Live = 1, Paused = 2 }

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private byte* _base;
    private long _lastOpenAttempt;
    private readonly Lock _lock = new();

    public string? OpenedName { get; private set; }
    public bool IsOpen => _base != null;
    public long FramesWritten { get; private set; }

    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();

    /// <summary>Open the section if the media source has created it (i.e. some app is using the camera).</summary>
    public bool TryOpen()
    {
        if (_base != null) return true;
        var now = Environment.TickCount64;
        if (now - _lastOpenAttempt < 1000) return false;
        _lastOpenAttempt = now;
        foreach (var name in new[] { GlobalName, LocalName })
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
                _view = _mmf.CreateViewAccessor(0, TotalBytes, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _base = p + _view.PointerOffset;
                if (*(uint*)(_base + OffMagic) != Magic) { Close(); continue; }
                OpenedName = name;
                Log.Info($"virtual camera feed attached ({name})");
                return true;
            }
            catch (FileNotFoundException) { Close(); }
            catch (UnauthorizedAccessException e) { Log.Warn($"virtual camera section access denied: {e.Message}"); Close(); }
        }
        return false;
    }

    /// <summary>True while a camera consumer has pulled a frame within the last 2 s.</summary>
    public bool ConsumerActive => _base != null && GetTickCount64() - *(ulong*)(_base + OffReaderTick) < 2000;

    public void Write(Nv12Frame f)
    {
        lock (_lock)
        {
            if (!TryOpen()) return;
            if (f.Width > MaxW || f.Height > MaxH || f.Data.Length < f.Width * f.Height * 3 / 2) return;
            long* seq = (long*)(_base + OffSeq);
            Interlocked.Increment(ref *seq); // odd: writing
            *(uint*)(_base + OffWidth) = (uint)f.Width;
            *(uint*)(_base + OffHeight) = (uint)f.Height;
            *(uint*)(_base + OffState) = (uint)FeedState.Live;
            *(long*)(_base + OffFrameTime) = f.TimestampNs / 100;
            fixed (byte* src = f.Data)
                Buffer.MemoryCopy(src, _base + HeaderBytes, TotalBytes - HeaderBytes, f.Width * f.Height * 3 / 2);
            *(ulong*)(_base + OffWriterTick) = GetTickCount64();
            *(ulong*)(_base + OffFramesWritten) = (ulong)++FramesWritten;
            Interlocked.Increment(ref *seq); // even: stable
        }
    }

    /// <summary>Mark the feed offline/paused so the camera shows its placeholder instead of the last frame.</summary>
    public void SetState(FeedState state)
    {
        lock (_lock)
        {
            if (_base == null) return;
            long* seq = (long*)(_base + OffSeq);
            Interlocked.Increment(ref *seq);
            *(uint*)(_base + OffState) = (uint)state;
            *(ulong*)(_base + OffWriterTick) = GetTickCount64();
            Interlocked.Increment(ref *seq);
        }
    }

    private void Close()
    {
        if (_view != null)
        {
            if (_base != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _mmf?.Dispose();
        _view = null; _mmf = null; _base = null; OpenedName = null;
    }

    public void Dispose()
    {
        SetState(FeedState.Offline);
        lock (_lock) Close();
    }
}

/// <summary>Registers the session-lifetime "PixelBridge Camera" with Windows (MFCreateVirtualCamera).</summary>
public static partial class VirtualCamera
{
    public const string Clsid = "{8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}";
    public const string FriendlyName = "PixelBridge Camera";

    [LibraryImport("PixelBridge.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int pb_vcam_start(string friendlyName, string clsid);
    [LibraryImport("PixelBridge.Native.dll")] private static partial int pb_vcam_stop();
    [LibraryImport("PixelBridge.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int pb_vcam_is_registered(string clsid);

    /// <summary>Media source COM class registered machine-wide (requires the elevated installer step).</summary>
    public static bool IsSourceRegistered => pb_vcam_is_registered(Clsid) != 0;

    /// <summary>Returns the HRESULT (0 = created, 1 = already running).</summary>
    public static int Start() => pb_vcam_start(FriendlyName, Clsid);

    public static int Stop() => pb_vcam_stop();

    public static bool IsSupportedOs => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
}
