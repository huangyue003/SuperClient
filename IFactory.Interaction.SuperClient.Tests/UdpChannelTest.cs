using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class UdpChannelTest
{
    [Fact]
    public async Task SendsSeparateDatagramsAndReceivesLargePayload()
    {
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = remote.Client.LocalEndPoint;
        await using var client = new SimplifiedClient($"udp://127.0.0.1:0/?RemoteEndPoint={endpoint}&ReceiveBufferSize=128&MaxPackageLength=65507", new FixedSizePipelineFilter(60000));
        await client.Initialization;
        var first = new byte[] { 1, 2, 3 };
        await Task.WhenAll(client.SendAsync(first), client.SendAsync(new byte[] { 4, 5 }));
        var a = await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var b = await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first, a.Buffer);
        Assert.Equal(new byte[] { 4, 5 }, b.Buffer);
        byte[] payload = Enumerable.Repeat((byte)7, 60000).ToArray();
        var response = client.SendReceiveAsync(new byte[] { 0 }, 5000);
        var request = await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await remote.SendAsync(Array.Empty<byte>(), request.RemoteEndPoint);
        await remote.SendAsync(payload, request.RemoteEndPoint);
        Assert.Equal(payload, await response);
        await client.Channel!.SendAsync(writer => { writer.Write(new byte[5000]); writer.Write(new byte[5000]); });
        Assert.Equal(10000, (await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer.Length);
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.Channel!.SendAsync(new byte[65508]));
    }
}

