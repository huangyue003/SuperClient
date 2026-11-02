using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class DisconnectNotificationTests
{
    [Fact]
    public async Task ServerPortIsVisibleBeforeAcceptAndDisconnectCarriesClosedChannel()
    {
        await using var client = new SimplifiedClient("tcp://127.0.0.1:0/?Role=server", new FixedSizePipelineFilter(1));
        await client.Initialization;
        var endpoint = Assert.IsType<IPEndPoint>(client.LocalEndPoint);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<ChannelDisconnectedEventArgs<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChannelCreated = _ => { connected.TrySetResult(); throw new Exception("user callback"); };
        client.Disconnected += (_, _) => throw new Exception("first observer");
        client.Disconnected += (_, args) => disconnected.TrySetResult(args);
        using var remote = new TcpClient(); await remote.ConnectAsync(endpoint);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)); var old = client.Channel;
        await client.DisposeChannelAsync();
        var args = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(old, args.Channel); Assert.Equal(CloseReason.LocalClosing, args.Reason);
        Assert.Equal(CloseReason.LocalClosing, client.LastCloseReason);
    }
    [Fact]
    public async Task ResetReleasesPendingResponseAndPublishesANewChannel()
    {
        await using var client = new SimplifiedClient("udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9", new FixedSizePipelineFilter(1));
        await client.Initialization;
        var old = client.Channel;
        var response = client.SendReceiveAsync(new byte[] { 1 }, Timeout.Infinite);
        await client.ResetChannelAsync();
        Assert.Null(await response.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotSame(old, client.Channel);
    }
}
