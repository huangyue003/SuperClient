using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class TcpStartupFailureTests
{
    private sealed class ResetFilter(bool fail) : FixedSizePipelineFilter(1)
    {
        public bool Fail { get; set; } = fail;
        public override void Reset() { if (Fail) throw new InvalidOperationException("reset failed"); base.Reset(); }
    }
    private sealed class Server(RecordingLogger logger) : TcpChannelCreator<byte[]>(logger, new ChannelOptions { ReconnectJitter = false })
    {
        public Channel<(TimeSpan Delay, TaskCompletionSource Resume)> Retries { get; } = Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource)>();
        protected override async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
        {
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Retries.Writer.TryWrite((delay, resume)); await resume.Task.WaitAsync(token);
        }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupFailuresBackOffAndDoNotAnnounceRecoveryUntilStartupSucceeds(bool failInFilter)
    {
        var logger = new RecordingLogger(); var creator = new Server(logger); var filter = new ResetFilter(failInFilter);
        bool callbackFails = !failInFilter;
        var ready = new TaskCompletionSource<IChannel<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        creator.ChannelCreated = channel =>
        {
            if (callbackFails) throw new InvalidOperationException("startup callback failed");
            channel.Start(); ready.TrySetResult(channel);
        };
        using var first = new TcpClient(); using var second = new TcpClient(); using var third = new TcpClient();
        try
        {
            await creator.RunAsync("tcp://127.0.0.1:0/?Role=server", filter);
            var address = Assert.IsType<IPEndPoint>(creator.LocalEndPoint);
            await first.ConnectAsync(address);
            var one = await creator.Retries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(200, one.Delay.TotalMilliseconds);
            await second.ConnectAsync(address); one.Resume.TrySetResult();
            var two = await creator.Retries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(400, two.Delay.TotalMilliseconds);
            Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("recovered"));
            filter.Fail = false; callbackFails = false;
            await third.ConnectAsync(address); two.Resume.TrySetResult();
            var channel = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // WaitForRetry is gated and logging occurs before waiting for close;
            // closing the channel synchronizes the remainder of startup.
            await channel.CloseAsync(CloseReason.LocalClosing);
            creator.Dispose(); await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("recovered after 2 failures"));
        }
        finally { creator.Dispose(); await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
