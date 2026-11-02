using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class DetachTests
{
    [Fact]
    public async Task TcpRejectsDetachWithoutChangingStateAndRemainsUsable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var peer = new TcpClient(); var accept = listener.AcceptSocketAsync();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var channel = new TcpPipeChannel<byte[]>(await accept, new FixedSizePipelineFilter(1), new ChannelOptions());
        channel.Start(); int events = 0; channel.Closed += (_, _) => events++;
        await Assert.ThrowsAsync<NotSupportedException>(async () => await channel.DetachAsync());
        Assert.False(channel.IsClosed); Assert.Null(channel.CloseReason); Assert.Equal(0, events);
        await channel.SendAsync(new byte[] { 7 });
        byte[] read = new byte[1]; await peer.GetStream().ReadExactlyAsync(read).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, read[0]); await channel.CloseAsync(CloseReason.LocalClosing);
        Assert.Equal(1, events);
    }
    [Fact]
    public async Task UdpAndSerialRejectDetachBeforeChangingState()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var udp = new UdpPipeChannel<byte[]>(socket, new FixedSizePipelineFilter(1), new ChannelOptions(), new IPEndPoint(IPAddress.Loopback, 9));
        using var port = new SerialPort();
        var serial = new SerialIOPipeChannel<byte[]>(port, new FixedSizePipelineFilter(1), new ChannelOptions());
        foreach (var channel in new IChannel[] { udp, serial })
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () => await channel.DetachAsync());
            Assert.False(channel.IsClosed); Assert.Null(channel.CloseReason);
        }
        await udp.CloseAsync(CloseReason.LocalClosing);
    }
}
