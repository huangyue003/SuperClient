using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ConfigurationAndActivityTests
{
    [Fact]
    public async Task SendTimestampRequiresActualTransportAcceptance()
    {
        var channel = new ControlledChannel(); channel.Start();
        Assert.Null(channel.LastSendTime); Assert.Null(channel.LastReceiveTime);
        var send = channel.SendAsync(new byte[] { 1 }).AsTask();
        var call = await channel.NextSendAsync(); Assert.Null(channel.LastSendTime);
        call.Accept(1); await send;
        Assert.NotNull(channel.LastSendTime); Assert.Null(channel.LastReceiveTime);
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task SocketFlagsAreAppliedAndSerialDefaultsAreUsableSettings()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var channel = new TcpPipeChannel<byte[]>(socket, new FixedSizePipelineFilter(1), new ChannelOptions { NoDelay = true, KeepAlive = true });
        Assert.True(socket.NoDelay);
        Assert.Equal(1, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        await channel.CloseAsync(CloseReason.LocalClosing);
        var serial = new SerialIOEndPoint();
        Assert.Equal(9600, serial.BaudRate); Assert.Equal(8, serial.Databits); Assert.Equal(StopBits.One, serial.StopBits);
    }
    [Theory]
    [InlineData("SendTimeout=-1")]
    [InlineData("ConnectTimeout=-1")]
    [InlineData("MaxPendingPackages=0")]
    [InlineData("ReadAsDemand=maybe")]
    [InlineData("ReconnectMinDelay=500&ReconnectMaxDelay=100")]
    public async Task InvalidAddedOptionsFailDuringInitialization(string query)
    {
        await using var client = new SimplifiedClient("udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9&" + query, new FixedSizePipelineFilter(1));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Initialization);
    }
}
