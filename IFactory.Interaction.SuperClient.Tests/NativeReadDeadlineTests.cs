using SuperSocket.ProtoBase;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using System.Buffers;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class NativeReadDeadlineTests
{
    private sealed class NonCancellableReadChannel() : PipeChannel<byte[]>(new FixedSizePipelineFilter(1),
        new ChannelOptions { ReceiveTimeout = 100 })
    {
        private readonly TaskCompletionSource<int> _nativeRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool AbortOnReceiveTimeout => true;
        protected override ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
            => new(_nativeRead.Task); // Deliberately ignores cancellation, as the virtual serial driver does.
        protected override ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> bytes, CancellationToken token)
            => ValueTask.FromResult((int)bytes.Length);
        protected override void Close() => _nativeRead.TrySetResult(0);
    }
    [Fact]
    public async Task DeadlineClosesATransportWhoseNativeReadIgnoresCancellation()
    {
        var channel = new NonCancellableReadChannel();
        var closed = new TaskCompletionSource<CloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Closed += (_, e) => closed.TrySetResult(e.Reason); channel.Start();
        try { Assert.Equal(CloseReason.TimeOut, await closed.Task.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { await channel.CloseAsync(CloseReason.LocalClosing); }
        Assert.True(channel.IsClosed);
    }
    private sealed class OpenGraceCreator(int budget, int deniedAttempts, bool block = false)
        : SerialIOChannelCreator<byte[]>(null, new ChannelOptions { SerialOpenRetryTimeout = budget })
    {
        public int Attempts { get; private set; }
        public int Delays { get; private set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override IChannel<byte[]> OpenChannel(SerialIOEndPoint endpoint, IPipelineFilter<byte[]> filter, ChannelOptions options)
        {
            if (++Attempts <= deniedAttempts) throw new UnauthorizedAccessException("exclusive native port is still busy");
            return new ControlledChannel();
        }
        protected override async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
        {
            Delays++; Waiting.TrySetResult();
            await Task.Delay(block ? Timeout.InfiniteTimeSpan : delay, token);
        }
    }
    [Fact]
    public async Task TemporaryNativeOpenDenialRetriesOpeningWithoutSending()
    {
        var creator = new OpenGraceCreator(500, 1); IChannel<byte[]>? channel = null;
        creator.ChannelCreated = c => { channel = c; c.Start(); };
        try
        {
            await creator.RunAsync("sio://COM1/?BaudRate=115200", new FixedSizePipelineFilter(1));
            Assert.Equal(2, creator.Attempts); Assert.Equal(1, creator.Delays);
            Assert.Empty(Assert.IsType<ControlledChannel>(channel).Accepted);
        }
        finally { if (channel is not null) await channel.CloseAsync(CloseReason.LocalClosing); creator.Dispose(); }
    }
    [Fact]
    public async Task DisabledOpenGracePreservesTheOriginalAccessError()
    {
        var creator = new OpenGraceCreator(0, 1);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => creator.RunAsync("sio://COM1/?BaudRate=115200", new FixedSizePipelineFilter(1)));
            Assert.Equal(1, creator.Attempts); Assert.Equal(0, creator.Delays);
        }
        finally { creator.Dispose(); }
    }
    [Fact]
    public async Task PermanentlyBusyPortFailsAfterABoundedGracePeriod()
    {
        var creator = new OpenGraceCreator(50, int.MaxValue);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => creator.RunAsync("sio://COM1/?BaudRate=115200", new FixedSizePipelineFilter(1)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(creator.Delays > 0);
        }
        finally { creator.Dispose(); }
    }
    [Fact]
    public async Task DisposingDuringOpenGraceCancelsTheRetryWait()
    {
        var creator = new OpenGraceCreator(1000, int.MaxValue, block: true);
        var starting = creator.RunAsync("sio://COM1/?BaudRate=115200", new FixedSizePipelineFilter(1));
        await creator.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); creator.Dispose();
        await Assert.ThrowsAsync<TaskCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, creator.Attempts);
    }}
