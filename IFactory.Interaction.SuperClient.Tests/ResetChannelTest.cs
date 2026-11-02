using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ResetChannelTest
{
    [Fact]
    public async Task ResetReleasesPortAndCanRecoverInitializationFailure()
    {
        await using var client = new SimplifiedClient("invalid://localhost", new FixedSizePipelineFilter(1));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Initialization);
        await client.ResetChannelAsync("udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9");
        Assert.NotNull(client.Channel);
        var old = client.Channel;
        await client.ResetChannelAsync("udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(old!.IsClosed);
        Assert.NotSame(old, client.Channel);
        await client.SendAsync(new byte[] { 1 });
        await Task.WhenAll(client.DisposeChannelAsync(), client.DisposeChannelAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(client.Channel);
    }
}
