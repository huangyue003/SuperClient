using System.Threading.Channels;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using SuperSocket.ProtoBase;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class SerialRecoveryPolicyTests
{
    private sealed class Creator(ChannelOptions options, int deniedOpen = 0, bool alwaysDenied = false, bool letGraceExpire = false)
        : SerialIOChannelCreator<byte[]>(null, options)
    {
        private int _opens;
        private int _fastDelays;
        public int OpenCount => Volatile.Read(ref _opens);
        public int FastDelays => Volatile.Read(ref _fastDelays);
        public Channel<IChannel<byte[]>> Opened { get; } = Channel.CreateUnbounded<IChannel<byte[]>>();
        public Channel<(TimeSpan Delay, TaskCompletionSource Resume)> Delays { get; } = Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource)>();
        public void Observe() => ChannelCreated = channel => { channel.Start(); Opened.Writer.TryWrite(channel); };
        protected override IChannel<byte[]> OpenChannel(SerialIOEndPoint endpoint, IPipelineFilter<byte[]> filter, ChannelOptions configured)
        {
            int attempt = Interlocked.Increment(ref _opens);
            if (alwaysDenied || attempt == deniedOpen) throw new UnauthorizedAccessException("native handle not released yet");
            return new ControlledChannel();
        }
        protected override async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
        {
            if (delay.TotalMilliseconds <= 25)
            {
                Interlocked.Increment(ref _fastDelays);
                if (letGraceExpire) { await Task.Delay(delay, token); return; }
            }
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Delays.Writer.TryWrite((delay, resume)); await resume.Task.WaitAsync(token);
        }
        public Task<IChannel<byte[]>> NextAsync() => Opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task<(TimeSpan Delay, TaskCompletionSource Resume)> DelayAsync() => Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task StopAsync()
        { Dispose(); await Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    private const string Uri = "sio://COM1/?BaudRate=115200";
    [Theory]
    [InlineData(CloseReason.LocalClosing)]
    [InlineData(CloseReason.ApplicationError)]
    [InlineData(CloseReason.ProtocolError)]
    [InlineData(CloseReason.InternalError)]
    public async Task TerminalReasonsRemainTerminalByDefault(CloseReason reason)
    {
        var creator = new Creator(new ChannelOptions { SerialReconnect = true }); creator.Observe();
        try
        {
            await creator.RunAsync(Uri, new FixedSizePipelineFilter(1));
            await (await creator.NextAsync()).CloseAsync(reason);
            await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, creator.OpenCount); Assert.False(creator.Delays.Reader.TryRead(out _));
        }
        finally { await creator.StopAsync(); }
    }
    [Fact]
    public async Task RepeatedInternalClosuresBackOffAcrossSuccessfulOpensAndNeverReplayCommands()
    {
        var creator = new Creator(new ChannelOptions { SerialReconnect = true, SerialReconnectOnInternalError = true,
            ReconnectMinDelay = 100, ReconnectMaxDelay = 400, ReconnectJitter = false }); creator.Observe();
        try
        {
            await creator.RunAsync(Uri, new FixedSizePipelineFilter(1));
            foreach (int expected in new[] { 100, 200, 400, 400 })
            {
                var channel = await creator.NextAsync(); Assert.Empty(Assert.IsType<ControlledChannel>(channel).Accepted);
                await channel.CloseAsync(CloseReason.InternalError);
                var wait = await creator.DelayAsync(); Assert.Equal(expected, wait.Delay.TotalMilliseconds); wait.Resume.TrySetResult();
            }
            await (await creator.NextAsync()).CloseAsync(CloseReason.SocketError);
            var normal = await creator.DelayAsync(); Assert.Equal(100, normal.Delay.TotalMilliseconds); normal.Resume.TrySetResult();
            await (await creator.NextAsync()).CloseAsync(CloseReason.LocalClosing);
            await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(6, creator.OpenCount);
        }
        finally { await creator.StopAsync(); }
    }
    [Fact]
    public async Task ReconnectUsesOpenGraceAfterATransientNativeAccessDenial()
    {
        var creator = new Creator(new ChannelOptions { SerialReconnect = true, SerialOpenRetryTimeout = 500,
            ReconnectJitter = false }, deniedOpen: 2); creator.Observe();
        try
        {
            await creator.RunAsync(Uri, new FixedSizePipelineFilter(1)); await (await creator.NextAsync()).CloseAsync(CloseReason.SocketError);
            var recovery = await creator.DelayAsync(); Assert.Equal(200, recovery.Delay.TotalMilliseconds); recovery.Resume.TrySetResult();
            var grace = await creator.DelayAsync(); Assert.InRange(grace.Delay.TotalMilliseconds, 0.01, 25); grace.Resume.TrySetResult();
            var channel = await creator.NextAsync(); Assert.Equal(3, creator.OpenCount); Assert.Equal(1, creator.FastDelays);
            Assert.False(creator.Delays.Reader.TryRead(out _)); await channel.CloseAsync(CloseReason.LocalClosing);
        }
        finally { await creator.StopAsync(); }
    }
    [Fact]
    public async Task ExhaustedOpenGraceFallsBackToConnectionBackoff()
    {
        var creator = new Creator(new ChannelOptions { SerialReconnect = true, SerialOpenRetryTimeout = 150,
            ReconnectJitter = false }, alwaysDenied: true, letGraceExpire: true); creator.Observe();
        try
        {
            await creator.RunAsync(Uri, new FixedSizePipelineFilter(1));
            var backoff = await creator.DelayAsync(); Assert.Equal(200, backoff.Delay.TotalMilliseconds);
            Assert.True(creator.FastDelays > 0); Assert.True(creator.OpenCount > 1); Assert.False(creator.Opened.Reader.TryRead(out _));
        }
        finally { await creator.StopAsync(); }
    }
    [Fact]
    public async Task DisposeCancelsOpenGraceInsideReconnectMode()
    {
        var creator = new Creator(new ChannelOptions { SerialReconnect = true, SerialOpenRetryTimeout = 500 }, alwaysDenied: true); creator.Observe();
        await creator.RunAsync(Uri, new FixedSizePipelineFilter(1));
        var grace = await creator.DelayAsync(); Assert.InRange(grace.Delay.TotalMilliseconds, 0.01, 25);
        await creator.StopAsync(); Assert.Equal(1, creator.OpenCount);
    }
    [Fact]
    public void UriOptInIsExplicitAndDoesNotEnableSerialReconnectItself()
    {
        var configured = ChannelUri.Options(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["SerialReconnectOnInternalError"] = "true" }, null);
        Assert.True(configured.SerialReconnectOnInternalError); Assert.False(configured.SerialReconnect);
        Assert.False(new ChannelOptions().SerialReconnectOnInternalError);
        Assert.Throws<ArgumentException>(() => ChannelUri.Options(new Dictionary<string, string> { ["SerialReconnectOnInternalError"] = "invalid" }, null));
    }
}
