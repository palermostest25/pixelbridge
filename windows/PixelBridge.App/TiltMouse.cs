using System.Runtime.InteropServices;
using PixelBridge.Core;

namespace PixelBridge.App;

/// <summary>
/// Opt-in gyro-to-mouse. Off by default, never persisted as on, disabled on disconnect/privacy, and
/// toggled off by the global safety hotkey Ctrl+Alt+P. Only relative mouse moves; no keyboard input.
/// </summary>
public sealed class TiltMouse
{
    private double _accX, _accY;
    private long _lastTs;
    public bool Enabled { get; set; }

    public void OnSensor(SensorSample s, double sensitivity)
    {
        if (!Enabled || s.Type != SensorTypes.Gyroscope || s.Values.Length < 3) { _lastTs = 0; return; }
        if (_lastTs == 0) { _lastTs = s.TimestampNs; return; }
        double dt = (s.TimestampNs - _lastTs) / 1e9;
        _lastTs = s.TimestampNs;
        if (dt <= 0 || dt > 0.2) return;
        const double dead = 0.03; // rad/s
        double yaw = Math.Abs(s.Values[2]) > dead ? s.Values[2] : 0;
        double pitch = Math.Abs(s.Values[0]) > dead ? s.Values[0] : 0;
        _accX += -yaw * dt * sensitivity;
        _accY += -pitch * dt * sensitivity;
        int dx = (int)_accX, dy = (int)_accY;
        if (dx == 0 && dy == 0) return;
        _accX -= dx; _accY -= dy;
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = 0x0001 /* MOVE */ } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; } // MOUSEINPUT is the largest union member: 40 bytes on x64

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
