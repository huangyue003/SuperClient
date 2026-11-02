using System.Threading.Channels;
using SuperSocket.ProtoBase;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class SerialReconnectTests
{
    private sealed class FakeSerialCreator(bool reconnect) : SerialIOChannelCreator<byte[]>(null,
        new ChannelOptions { SerialReconnect = reconnect, ReconnectJitter = false })
    {
        public Channel<ControlledChannel> Opened { get; } = Channel.CreateUnbounded<ControlledChannel>();
        public TaskCompletionSource Retry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override IChannel<byte[]> OpenChannel(SerialIOEndPoint endpoint, IPipelineFilter<byte[]> filter, ChannelOptions options)
        { var channel = new ControlledChannel(); Opened.Writer.TryWrite(channel); return channel; }
        protected override async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
        { Retry.TrySetResult(); await Resume.Task.WaitAsync(token); }
    }
    [Fact]
    public async Task OptInRecoversConnectionsButNeverReplaysCommands()
    {
        var creator = new FakeSerialCreator(true); creator.ChannelCreated = c => c.Start();
        await creator.RunAsync("sio://COM2/?BaudRate=9600", new FixedSizePipelineFilter(1));
        var first = await creator.Opened.Reader.ReadAsync();
        await first.CloseAsync(CloseReason.SocketError);
        await creator.Retry.Task.WaitAsync(TimeSpan.FromSeconds(5)); creator.Resume.TrySetResult();
        var second = await creator.Opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(first, second); Assert.Empty(second.Accepted);
        creator.Dispose(); await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task DefaultRemainsOneShotAndLocalCloseStopsOptInLoop()
    {
        foreach (bool reconnect in new[] { false, true })
        {
            var creator = new FakeSerialCreator(reconnect); creator.ChannelCreated = c => c.Start();
            await creator.RunAsync("sio://COM2/?BaudRate=9600", new FixedSizePipelineFilter(1));
            var first = await creator.Opened.Reader.ReadAsync();
            await first.CloseAsync(CloseReason.LocalClosing);
            await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(creator.Opened.Reader.TryRead(out _)); creator.Dispose();
        }
    }
}
