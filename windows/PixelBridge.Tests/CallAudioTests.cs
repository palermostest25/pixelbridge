using PixelBridge.Core;
using System.Buffers.Binary;

public class CallAudioTests
{
    [Fact] public async Task ValidAudioAndMutePreserveFrameBoundaries()
    {
        byte[] pcm = [1, 2, 3, 4];
        await using var stream = new MemoryStream(CallAudioProtocol.PcmFrame(pcm, false));
        Assert.Equal(pcm, await CallAudioProtocol.ReadPcmAsync(stream, default));
        await using var muted = new MemoryStream(CallAudioProtocol.PcmFrame(pcm, true));
        Assert.Equal(new byte[4], await CallAudioProtocol.ReadPcmAsync(muted, default));
    }
    [Fact] public async Task CountersDoNotInterruptAudioFrames()
    {
        byte[] counters = new byte[29]; counters[0] = 4; counters[4] = 24;
        byte[] pcm = [1, 2, 3, 4];
        var bytes = counters.Concat(CallAudioProtocol.PcmFrame(pcm, false)).ToArray();
        Assert.Equal(pcm, await CallAudioProtocol.ReadPcmAsync(new MemoryStream(bytes), default));
    }
    [Fact] public async Task OversizedAndTruncatedAudioAreRejected()
    {
        byte[] header = [1, 0, 0, 0, 0]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), 6401);
        await Assert.ThrowsAsync<IOException>(() => CallAudioProtocol.ReadPcmAsync(new MemoryStream(header), default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => CallAudioProtocol.ReadPcmAsync(new MemoryStream([1, 0, 0, 0, 4, 0, 0]), default));
        Assert.Throws<ArgumentException>(() => CallAudioProtocol.PcmFrame([1], false));
    }
    [Fact] public async Task HelperErrorIsReportedAndWrongFormatIsRejected()
    {
        await Assert.ThrowsAsync<IOException>(() => CallAudioProtocol.ReadPcmAsync(new MemoryStream([3, 0, 0, 0, 4, 102, 97, 105, 108]), default));
        await Assert.ThrowsAsync<IOException>(() => CallAudioProtocol.ReadHeaderAsync(new MemoryStream(new byte[12]), default));
        await CallAudioProtocol.ReadHeaderAsync(new MemoryStream([80, 66, 65, 49, 0, 0, 62, 128, 0, 0, 0, 1]), default);
    }
}
