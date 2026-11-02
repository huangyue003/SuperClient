using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using SuperSocket.ProtoBase;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ProtocolAndCreatorReviewTests
{
    private sealed class InputChannel(IPipelineFilter<byte[]> filter, ChannelOptions? options = null)
        : PipeChannel<byte[]>(filter, options ?? new ChannelOptions())
    {
        public Channel<byte[]> Input { get; } = Channel.CreateUnbounded<byte[]>();
        public TaskCompletionSource<byte[]> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Close() => Input.Writer.TryComplete();
        protected override async ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
        {
            try { var bytes = await Input.Reader.ReadAsync(token); bytes.CopyTo(memory); return bytes.Length; }
            catch (ChannelClosedException) { return 0; }
        }
        protected override ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> bytes, CancellationToken token)
        { Sent.TrySetResult(bytes.ToArray()); return ValueTask.FromResult((int)bytes.Length); }
    }
    private sealed class SwitchingFilter(IPipelineFilter<byte[]> next) : PipelineFilterBase<byte[]>
    {
        public int Resets { get; private set; }
        public override byte[] Filter(ref SequenceReader<byte> reader)
        { reader.Advance(1); NextFilter = next; return null!; }
        public override void Reset() { Resets++; base.Reset(); }
    }
    private sealed class CyclingFilter : PipelineFilterBase<byte[]>
    {
        public IPipelineFilter<byte[]> Target { get; set; } = null!;
        public override byte[] Filter(ref SequenceReader<byte> reader) { NextFilter = Target; return null!; }
    }
    [Fact]
    public async Task FilterSwitchPreservesContextAcrossFragmentedReadsAndResetsOldFilter()
    {
        var target = new FixedSizePipelineFilter(2); var context = new object();
        var first = new SwitchingFilter(target) { Context = context };
        var channel = new InputChannel(first, new ChannelOptions { ReadAsDemand = true, MaxPackageLength = 3 }); channel.Start();
        await using var stream = channel.GetPackageStream(); var read = stream.MoveNextAsync().AsTask();
        channel.Input.Writer.TryWrite(new byte[] { 0xaa }); channel.Input.Writer.TryWrite(new byte[] { 10, 20 });
        Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 10, 20 }, stream.Current); Assert.Same(context, target.Context); Assert.Equal(1, first.Resets);
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task NoProgressFilterCycleClosesInsteadOfSpinning()
    {
        var a = new CyclingFilter(); var b = new CyclingFilter(); a.Target = b; b.Target = a;
        var channel = new InputChannel(a); var closed = new TaskCompletionSource<CloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Closed += (_, e) => closed.TrySetResult(e.Reason); channel.Start(); channel.Input.Writer.TryWrite(new byte[] { 1 });
        Assert.Equal(CloseReason.ProtocolError, await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact]
    public async Task IncompleteEofClosesWithProtocolErrorAndCompletesTheConsumer()
    {
        var channel = new InputChannel(new FixedSizePipelineFilter(4)); channel.Start();
        await using var stream = channel.GetPackageStream(); var read = stream.MoveNextAsync().AsTask();
        channel.Input.Writer.TryWrite(new byte[] { 1, 2 }); channel.Input.Writer.TryComplete();
        Assert.False(await read.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(CloseReason.ProtocolError, channel.CloseReason);
    }
    [Fact]
    public async Task LegacyOutputBatchCanContainSeveralValidFrames()
    {
        var output = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        output.Writer.Write(new byte[] { 1, 2, 3, 4 }); output.Writer.Write(new byte[] { 5, 6, 7, 8 });
        await output.Writer.FlushAsync();
        var channel = new InputChannel(new FixedSizePipelineFilter(1), new ChannelOptions { Out = output, MaxPackageLength = 4 }); channel.Start();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(channel.IsClosed);
        await Assert.ThrowsAsync<ArgumentException>(async () => await channel.SendAsync(new byte[5]));
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task UdpCallbackRunsOutsideCreatorLockAndFailureClosesItsChannel()
    {
        var creator = new UdpChannelCreator<byte[]>(null);
        var sync = typeof(UdpChannelCreator<byte[]>).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(creator)!;
        IChannel<byte[]>? published = null;
        creator.ChannelCreated = channel => { published = channel; Assert.False(Monitor.IsEntered(sync)); throw new InvalidOperationException("observer"); };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => creator.RunAsync(
            "udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9", new FixedSizePipelineFilter(1)));
        Assert.Equal("observer", error.Message); Assert.True(published!.IsClosed); Assert.Equal(CloseReason.ApplicationError, published.CloseReason);
        creator.Dispose();
    }
    [Theory]
    [InlineData("udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9&Multicast=true", typeof(ArgumentException))]
    [InlineData("udp://[::1]:0/?RemoteEndPoint=[ff02::1]:9&Multicast=true", typeof(NotSupportedException))]
    public async Task InvalidMulticastIsRejectedBeforeAllocatingASocket(string uri, Type errorType)
    {
        var creator = new UdpChannelCreator<byte[]>(null);
        var error = await Record.ExceptionAsync(() => creator.RunAsync(uri, new FixedSizePipelineFilter(1)));
        Assert.IsType(errorType, error); Assert.Null(creator.LocalEndPoint); creator.Dispose();
    }
}
