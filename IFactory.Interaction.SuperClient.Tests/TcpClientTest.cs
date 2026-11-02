using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class TcpClientTest
{
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, stop.Token);
    }
    [Fact]
    public async Task ConnectsWithoutQueryAndExchangesLargePacket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        await using var client = new SimplifiedClient($"tcp://localhost:{endpoint.Port}", new FixedSizePipelineFilter(100000));
        using var remote = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => client.Channel is not null);
        var payload = Enumerable.Range(0, 100000).Select(i => (byte)i).ToArray();
        var echo = Task.Run(async () =>
        {
            var buffer = new byte[payload.Length];
            await remote.GetStream().ReadExactlyAsync(buffer);
            Assert.Equal(payload, buffer);
            await remote.GetStream().WriteAsync(buffer);
        });
        Assert.Equal(payload, await client.SendReceiveAsync(payload, 5000));
        await echo.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(client.LastActiveTime);
        Assert.NotNull(client.Channel!.LocalEndPoint);
        await client.DisposeChannelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(payload));
    }
    [Fact]
    public async Task ReconnectsAfterRemoteClose()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new SimplifiedClient($"tcp://{listener.LocalEndpoint}", new FixedSizePipelineFilter(1));
        using var first = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => client.Channel is not null);
        var previous = client.Channel;
        first.Dispose();
        using var second = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => client.Channel is not null && !ReferenceEquals(previous, client.Channel));
        Assert.True(previous!.IsClosed);
    }
}
