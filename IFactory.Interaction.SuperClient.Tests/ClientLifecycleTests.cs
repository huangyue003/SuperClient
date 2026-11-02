using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ClientLifecycleTests
{
    [Fact]
    public async Task FinalResponseIsDeliveredBeforeRemoteEof()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new SimplifiedClient($"tcp://{listener.LocalEndpoint}", new FixedSizePipelineFilter(1));
        using var remote = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await TcpClientTest.WaitUntilAsync(() => client.Channel is not null);
        var response = client.SendReceiveAsync(new byte[] { 1 }, 5000);
        var buffer = new byte[1];
        await remote.GetStream().ReadExactlyAsync(buffer);
        await remote.GetStream().WriteAsync(new byte[] { 9 });
        remote.Dispose();
        Assert.Equal(new byte[] { 9 }, await response);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectReleasesInfiniteResponseWait(bool local)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new SimplifiedClient($"tcp://{listener.LocalEndpoint}", new FixedSizePipelineFilter(1));
        using var remote = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await TcpClientTest.WaitUntilAsync(() => client.Channel is not null);
        var response = client.SendReceiveAsync(new byte[] { 1 }, Timeout.Infinite);
        await remote.GetStream().ReadExactlyAsync(new byte[1]);
        if (local) await client.DisposeChannelAsync();
        else remote.Dispose();
        Assert.Null(await response.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Theory]
    [InlineData("tcp://127.0.0.1:1/?Role=unknown")]
    [InlineData("tcp://127.0.0.1:1/?bad")]
    [InlineData("tcp://127.0.0.1:1/?ReceiveBufferSize=0")]
    [InlineData("udp://127.0.0.1:0")]
    [InlineData("udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9&Multicast=bad")]
    public async Task InvalidConfigurationIsObservable(string uri)
    {
        await using var client = new SimplifiedClient(uri, new FixedSizePipelineFilter(1));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Initialization);
        Assert.Null(client.Channel);
    }
}
