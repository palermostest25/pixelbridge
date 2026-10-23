using System.Buffers.Binary;
using System.Text;

namespace PixelBridge.Core.Protocol;

/// <summary>Message type codes. See protocol/PROTOCOL.md section 2.</summary>
public static class MsgType
{
    public const byte Hello = 0x01, Capabilities = 0x02, AuthResponse = 0x04, AuthOk = 0x05, AuthFail = 0x06;
    public const byte StartStream = 0x10, StopStream = 0x11, StreamStarted = 0x12, StreamStopped = 0x13, StreamError = 0x14, Status = 0x15;
    public const byte VideoConfig = 0x20, VideoFrame = 0x21, RequestIdr = 0x22;
    public const byte AudioConfig = 0x30, AudioFrame = 0x31;
    public const byte SensorSample = 0x40, BatteryStatus = 0x41, NetworkStatus = 0x42, NotificationEvent = 0x43, LocationSample = 0x44;
    public const byte Heartbeat = 0x50, Pong = 0x51, Error = 0x7F;
    public const byte RadioRequest = 0x60, RadioResponse = 0x61, RadioEvent = 0x62;
}

[Flags]
public enum FrameFlags : ushort { None = 0, Keyframe = 1, CodecConfig = 2, EndOfStream = 4 }

public sealed record Frame(byte Type, byte[] Payload, FrameFlags Flags = FrameFlags.None, long TimestampNs = 0)
{
    public const byte Version = 1;
    public const int HeaderSize = 24;
    public const FrameFlags KnownFlags = FrameFlags.Keyframe | FrameFlags.CodecConfig | FrameFlags.EndOfStream;

    public string Text => Encoding.UTF8.GetString(Payload);

    public static Frame Json(byte type, string json, long timestampNs = 0) => new(type, Encoding.UTF8.GetBytes(json), FrameFlags.None, timestampNs);
}

public sealed class ProtocolException(string message) : IOException(message);

/// <summary>Per-channel payload limits (protocol section 0 table).</summary>
public static class ChannelLimits
{
    public static int MaxPayload(string channel) => channel switch
    {
        "video" => 2 * 1024 * 1024,
        "audio" => 16384,
        _ => 65536,
    };
}

/// <summary>Writes frames with per-direction sequence numbers. Callers must serialise access.</summary>
public sealed class FrameWriter(Stream stream)
{
    private ulong _sequence;
    private readonly byte[] _header = new byte[Frame.HeaderSize];

    public void Write(Frame f)
    {
        EncodeHeader(_header, f, _sequence++);
        stream.Write(_header);
        stream.Write(f.Payload);
        stream.Flush();
    }

    public static void EncodeHeader(Span<byte> h, Frame f, ulong sequence)
    {
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)f.Payload.Length);
        h[4] = Frame.Version;
        h[5] = f.Type;
        BinaryPrimitives.WriteUInt16BigEndian(h[6..], (ushort)f.Flags);
        BinaryPrimitives.WriteUInt64BigEndian(h[8..], sequence);
        BinaryPrimitives.WriteInt64BigEndian(h[16..], f.TimestampNs);
    }

    public static byte[] Encode(Frame f, ulong sequence)
    {
        var buf = new byte[Frame.HeaderSize + f.Payload.Length];
        EncodeHeader(buf, f, sequence);
        f.Payload.CopyTo(buf, Frame.HeaderSize);
        return buf;
    }
}

/// <summary>Reads and validates frames; any violation throws <see cref="ProtocolException"/> and the socket must be closed.</summary>
public sealed class FrameReader(Stream stream, int maxPayload)
{
    private ulong _expected;
    private readonly byte[] _header = new byte[Frame.HeaderSize];

    public async Task<Frame> ReadAsync(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_header, ct).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(_header);
        byte version = _header[4];
        byte type = _header[5];
        var flags = (FrameFlags)BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(6));
        ulong seq = BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(8));
        long ts = BinaryPrimitives.ReadInt64BigEndian(_header.AsSpan(16));
        if (version != Frame.Version) throw new ProtocolException($"unsupported version {version}");
        if (length > (uint)maxPayload) throw new ProtocolException($"payload {length} exceeds {maxPayload}");
        if ((flags & ~Frame.KnownFlags) != 0) throw new ProtocolException($"unknown flags {(ushort)flags}");
        if (seq != _expected) throw new ProtocolException($"sequence {seq} != {_expected}");
        _expected++;
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new Frame(type, payload, flags, ts);
    }
}
