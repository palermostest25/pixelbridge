using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using PixelBridge.Core.Api;

public class SensorDemandTests
{
    [Fact] public async Task SensorApiDemandEndsWhenIdleClientDisconnects()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await using var server = new SensorApiServer(port,"test-token",()=>new{});
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.DemandChanged += on => { if(on) started.TrySetResult(); else stopped.TrySetResult(); };
        server.Start(); Assert.Equal(0,server.ClientCount);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sensors?token=test-token"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1,server.ClientCount);
        client.Abort();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0,server.ClientCount);
    }
}
