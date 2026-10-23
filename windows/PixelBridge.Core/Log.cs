using System.Collections.Concurrent;

namespace PixelBridge.Core;

/// <summary>Event/metric log. Never pass media, message text, coordinates or keys to it.</summary>
public static class Log
{
    private static readonly ConcurrentQueue<string> Recent = new();
    private static readonly Lock FileLock = new();
    public static event Action<string>? Line;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static IReadOnlyList<string> Snapshot() => Recent.ToArray();

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}";
        Recent.Enqueue(line);
        while (Recent.Count > 2000) Recent.TryDequeue(out _);
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(Path.Combine(AppPaths.LogDir, $"pixelbridge-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch { }
        Line?.Invoke(line);
    }
}
