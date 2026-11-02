using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class TcpServerTest
{
    [Fact]
    public async Task ServerReceivesPacketsAndAcceptsNextConnection()
    {
        var creator = new TcpChannelCreator<byte[]>(null);
        var connected = new TaskCompletionSource<IChannel<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        creator.ChannelCreated = channel => { channel.Start(); connected.TrySetResult(channel); };
        // Reserve an ephemeral port briefly; retry is unnecessary on an isolated loopback test host.
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        try
        {
            await creator.RunAsync($"tcp://127.0.0.1:{port}/?Role=server&ReceiveBufferSize=128&MaxPackageLength=1024", new FixedSizePipelineFilter(1));
            using var remote = new TcpClient();
            await remote.ConnectAsync(IPAddress.Loopback, port);
            var channel = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var stream = channel.GetPackageStream();
            await remote.GetStream().WriteAsync(new byte[] { 9 });
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new byte[] { 9 }, stream.Current);
            await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            connected = new TaskCompletionSource<IChannel<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var next = new TcpClient();
            await next.ConnectAsync(IPAddress.Loopback, port);
            var nextChannel = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(channel, nextChannel);
            await nextChannel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { creator.Dispose(); creator.Dispose(); }
    }
    [Fact]
    public async Task DisposeBeforeStartIsSafe()
    {
        var creator = new TcpChannelCreator<byte[]>(null);
        creator.Dispose();
        creator.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => creator.RunAsync("tcp://127.0.0.1:1", new FixedSizePipelineFilter(1)));
    }
}

