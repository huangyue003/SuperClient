using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class CoverageGapTests
{
    private sealed class BrokenEncoder : IPackageEncoder<byte[]>
    {
        public int Encode(IBufferWriter<byte> writer, byte[] package)
        { writer.Write(new byte[] { 0xee, 0xff }); throw new FormatException("bad frame"); }
    }
    private sealed class FailingSerial(RecordingLogger logger) : SerialIOChannelCreator<byte[]>(logger,
        new ChannelOptions { SerialReconnect = true })
    {
        private int _attempts;
        public Channel<ControlledChannel> Opened { get; } = Channel.CreateUnbounded<ControlledChannel>();
        public TaskCompletionSource<TimeSpan> Retrying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override IChannel<byte[]> OpenChannel(SerialIOEndPoint endpoint, IPipelineFilter<byte[]> filter, ChannelOptions options)
        {
            if (++_attempts == 1) throw new IOException("temporary open failure");
            var channel = new ControlledChannel(); Opened.Writer.TryWrite(channel); return channel;
        }
        protected override async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
        { Retrying.TrySetResult(delay); await Resume.Task.WaitAsync(token); }
    }
    [Fact]
    public async Task FailedEncoderDoesNotLeakItsPrefixIntoTheNextSend()
    {
        var channel = new ControlledChannel(); channel.Start();
        var error = await Assert.ThrowsAsync<FormatException>(async () => await channel.SendAsync(new BrokenEncoder(), new byte[] { 1 }));
        Assert.Equal("bad frame", error.Message); Assert.False(channel.IsClosed); Assert.Empty(channel.Accepted);
        var send = channel.SendAsync(new byte[] { 7 }).AsTask(); var call = await channel.NextSendAsync();
        Assert.Equal(new byte[] { 7 }, call.Bytes); call.Accept(1); await send;
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task LimitedWriterPreservesPipeFlushAndCompletionSemantics()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0)); var writer = new LimitedPipeWriter(pipe.Writer, 8);
        Assert.True(writer.CanGetUnflushedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetMemory(-1));
        writer.GetSpan(4)[..4].Fill(7); writer.Advance(4); Assert.Equal(4, writer.UnflushedBytes);
        await writer.FlushAsync(); var read = await pipe.Reader.ReadAsync(); Assert.Equal(new byte[] { 7, 7, 7, 7 }, read.Buffer.ToArray()); pipe.Reader.AdvanceTo(read.Buffer.End);
        writer.CancelPendingFlush(); Assert.True((await writer.FlushAsync()).IsCanceled);
        writer.Complete(); var end = await pipe.Reader.ReadAsync(); Assert.True(end.IsCompleted); pipe.Reader.AdvanceTo(end.Buffer.End);
        await writer.CompleteAsync(); await pipe.Reader.CompleteAsync();
    }
    [Fact]
    public async Task OldKeyedAwaiterCleanupCannotRemoveANewRegistrationWithTheSameKey()
    {
        var scheduler = new KeyPackageScheduler<string, StringPackageInfo, StringPackageInfo>();
        var first = new StringPackageInfo { Key = "key", Body = "first" };
        var old = scheduler.RegisterAwaiter(first); await scheduler.HandlePackage(first);
        var second = new StringPackageInfo { Key = "key", Body = "second" }; var current = scheduler.RegisterAwaiter(second);
        Assert.Equal("key", Assert.IsType<KeyPackageAwaiter<string, StringPackageInfo>>(current).Key);
        Assert.Same(first, await old.ReadAsync(0)); // Invokes old cleanup after the key was reused.
        await scheduler.HandlePackage(second); Assert.Same(second, await current.ReadAsync(100));
    }
    [Fact]
    public async Task KeyedDisconnectCompletesAllPendingWaitersAndCountsUnsolicitedPackages()
    {
        var scheduler = new KeyPackageScheduler<string, StringPackageInfo, StringPackageInfo>();
        var a = scheduler.RegisterAwaiter(new StringPackageInfo { Key = "a" });
        var b = scheduler.RegisterAwaiter(new StringPackageInfo { Key = "b" });
        ((ISchedulerLifetime)scheduler).CancelPending(); Assert.Null(await a.ReadAsync(-1)); Assert.Null(await b.ReadAsync(-1));
        StringPackageInfo? unmatched = null; scheduler.MismatchingPackageReceiveHandler = p => unmatched = p;
        var unsolicited = new StringPackageInfo { Key = "notice" }; await scheduler.HandlePackage(unsolicited);
        Assert.Same(unsolicited, unmatched); Assert.Equal(1, scheduler.UnmatchedPackages);
    }
    [Fact]
    public async Task SerialOpenFailureIsLoggedAndOptInRecoversWithoutReplayingData()
    {
        var logger = new RecordingLogger(); var creator = new FailingSerial(logger); creator.ChannelCreated = c => c.Start();
        try
        {
            await creator.RunAsync("sio://COM2/?BaudRate=9600", new FixedSizePipelineFilter(1));
            var delay = await creator.Retrying.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.InRange(delay.TotalMilliseconds, 160, 240);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("com2"));
            creator.Resume.TrySetResult(); var recovered = await creator.Opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(recovered.Accepted); await recovered.CloseAsync(CloseReason.LocalClosing);
            await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { creator.Dispose(); }
    }
    [Fact]
    public async Task EmptyUdpSendProducesOneEmptyDatagram()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var client = new SimplifiedClient($"udp://127.0.0.1:0/?RemoteEndPoint={peer.Client.LocalEndPoint}", new FixedSizePipelineFilter(1));
        await client.Initialization; await client.SendAsync(ReadOnlyMemory<byte>.Empty);
        Assert.Empty((await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
    }
    [Fact]
    public async Task CreatorsRejectNullFiltersBeforeBindingOrStartingRetryLoops()
    {
        var tcp = new TcpChannelCreator<byte[]>(null); var udp = new UdpChannelCreator<byte[]>(null); var serial = new SerialIOChannelCreator<byte[]>(null);
        try
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => tcp.RunAsync("tcp://127.0.0.1:0/?Role=server", null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => udp.RunAsync("udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9", null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => serial.RunAsync("sio://COM2/?BaudRate=9600", null!));
            Assert.Null(tcp.LocalEndPoint); Assert.Null(udp.LocalEndPoint);
        }
        finally { tcp.Dispose(); udp.Dispose(); serial.Dispose(); }
    }
}
