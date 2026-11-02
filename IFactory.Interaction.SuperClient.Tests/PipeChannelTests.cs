using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using SuperSocket.ProtoBase;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class PipeChannelTests
{
    private sealed class TestChannel(IPipelineFilter<byte[]> filter, ChannelOptions? options = null)
        : PipeChannel<byte[]>(filter, options ?? new ChannelOptions())
    {
        public readonly Channel<byte[]> Input = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public readonly List<byte> Sent = new();
        public bool ReturnZero { get; set; }
        protected override void Close() => Input.Writer.TryComplete();
        protected override async ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
        {
            try
            {
                var data = await Input.Reader.ReadAsync(token);
                data.CopyTo(memory);
                return data.Length;
            }
            catch (ChannelClosedException) { return 0; }
        }
        protected override ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> buffer, CancellationToken token)
        {
            if (ReturnZero) return ValueTask.FromResult(0);
            int count = (int)Math.Min(3, buffer.Length);
            lock (Sent) Sent.AddRange(buffer.Slice(0, count).ToArray());
            return ValueTask.FromResult(count);
        }
    }
    private sealed class NoProgressFilter : PipelineFilterBase<byte[]>
    {
        public override byte[] Filter(ref SequenceReader<byte> reader) => new byte[] { 1 };
    }
    private sealed class ConsumingFilter : PipelineFilterBase<byte[]>
    {
        public override byte[] Filter(ref SequenceReader<byte> reader) { reader.AdvanceToEnd(); return null!; }
    }
    [Fact]
    public async Task RetriesEveryPartialSend()
    {
        var channel = new TestChannel(new FixedSizePipelineFilter(1));
        channel.Start();
        byte[] payload = Enumerable.Range(0, 20000).Select(i => (byte)i).ToArray();
        await channel.SendAsync(payload);
        await TcpClientTest.WaitUntilAsync(() => { lock (channel.Sent) return channel.Sent.Count == payload.Length; });
        Assert.Equal(payload, channel.Sent);
        await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task ReadOnDemandHandlesFragmentedPacketAndClose()
    {
        var channel = new TestChannel(new FixedSizePipelineFilter(4), new ChannelOptions { ReadAsDemand = true });
        channel.Start();
        await using var stream = channel.GetPackageStream();
        var pending = stream.MoveNextAsync().AsTask();
        channel.Input.Writer.TryWrite(new byte[] { 1, 2 });
        channel.Input.Writer.TryWrite(new byte[] { 3, 4 });
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, stream.Current);
        await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await stream.MoveNextAsync());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOrOversizedFramesClosePromptly(bool noProgress)
    {
        var channel = new TestChannel(noProgress ? new NoProgressFilter() : new FixedSizePipelineFilter(10),
            new ChannelOptions { MaxPackageLength = 4 });
        channel.Start();
        channel.Input.Writer.TryWrite(new byte[5]);
        await TcpClientTest.WaitUntilAsync(() => channel.IsClosed);
        Assert.Equal(CloseReason.ProtocolError, channel.CloseReason);
    }
    [Fact]
    public async Task LengthLimitIncludesBytesConsumedByStatefulFilter()
    {
        var channel = new TestChannel(new ConsumingFilter(), new ChannelOptions { MaxPackageLength = 4 });
        channel.Start();
        channel.Input.Writer.TryWrite(new byte[3]);
        channel.Input.Writer.TryWrite(new byte[3]);
        await TcpClientTest.WaitUntilAsync(() => channel.IsClosed);
        Assert.Equal(CloseReason.ProtocolError, channel.CloseReason);
    }
    [Fact]
    public async Task ReceiveTimeoutClosesChannel()
    {
        var channel = new TestChannel(new FixedSizePipelineFilter(1), new ChannelOptions { ReceiveTimeout = 30 });
        channel.Start();
        await TcpClientTest.WaitUntilAsync(() => channel.IsClosed);
        Assert.Equal(CloseReason.TimeOut, channel.CloseReason);
    }
    [Fact]
    public async Task ZeroSendCountClosesInsteadOfSpinning()
    {
        var channel = new TestChannel(new FixedSizePipelineFilter(1)) { ReturnZero = true };
        channel.Start();
        await Assert.ThrowsAsync<ChannelTransportException>(async () => await channel.SendAsync(new byte[] { 1 }));
        await TcpClientTest.WaitUntilAsync(() => channel.IsClosed);
        Assert.Equal(CloseReason.SocketError, channel.CloseReason);
    }
    [Fact]
    public async Task CloseUnblocksPipeUnderBackpressure()
    {
        var channel = new TestChannel(new FixedSizePipelineFilter(1000), new ChannelOptions
        { In = new Pipe(new PipeOptions(pauseWriterThreshold: 16, resumeWriterThreshold: 8)) });
        channel.Start();
        channel.Input.Writer.TryWrite(new byte[32]);
        await Task.Delay(30);
        await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task QueueOverflowClosesChannel()
    {
        var channel = new TestChannel(new FixedSizePipelineFilter(1), new ChannelOptions { MaxPendingPackages = 2 });
        channel.Start();
        channel.Input.Writer.TryWrite(new byte[] { 1, 2, 3 });
        await TcpClientTest.WaitUntilAsync(() => channel.IsClosed);
        Assert.Equal(CloseReason.InternalError, channel.CloseReason);
    }
    [Fact]
    public async Task ObjectPipeDrainsAndCompletesPendingConsumer()
    {
        using var pipe = new DefaultObjectPipe<string>(2);
        Assert.Equal(1, pipe.Write("one"));
        Assert.Equal(2, pipe.Write("two"));
        Assert.Throws<ChannelQueueOverflowException>(() => pipe.Write("overflow"));
        pipe.Complete();
        Assert.Equal((true, "one"), await pipe.ReadAsync());
        Assert.Equal((true, "two"), await pipe.ReadAsync());
        Assert.False((await pipe.ReadAsync()).HasValue);
        using var waiting = new DefaultObjectPipe<string>();
        var read = waiting.ReadAsync().AsTask();
        waiting.Dispose();
        Assert.False((await read.WaitAsync(TimeSpan.FromSeconds(5))).HasValue);
    }
}
