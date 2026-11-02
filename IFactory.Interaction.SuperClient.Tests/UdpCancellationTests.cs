using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class UdpCancellationTests
{
    private sealed class StalledUdp(Socket socket) : UdpPipeChannel<byte[]>(socket, new FixedSizePipelineFilter(1),
        new ChannelOptions(), new IPEndPoint(IPAddress.Loopback, 9))
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> bytes, CancellationToken token)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return (int)bytes.Length; }
    }
    [Fact]
    public async Task CloseCancelsActiveUdpSendEvenWithTimeoutDisabled()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var channel = new StalledUdp(socket); channel.Start();
        var send = channel.SendAsync(new byte[] { 1 }).AsTask();
        await channel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ChannelTransportException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(CloseReason.LocalClosing, error.Reason);
    }
}
