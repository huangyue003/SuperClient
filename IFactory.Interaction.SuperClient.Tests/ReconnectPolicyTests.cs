using System.Net.Sockets;
using System.Threading.Channels;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ReconnectPolicyTests
{
    private sealed class RefusingCreator(RecordingLogger logger) : TcpChannelCreator<byte[]>(logger,
        new ChannelOptions { ReconnectJitter = false })
    {
        public Channel<(TimeSpan Delay, TaskCompletionSource Resume)> Retries { get; } = Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource)>();
        protected override ValueTask ConnectSocketAsync(Socket socket, string host, int port, CancellationToken token)
            => ValueTask.FromException(new SocketException((int)SocketError.ConnectionRefused));
        protected override async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
        {
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Retries.Writer.TryWrite((delay, resume)); await resume.Task.WaitAsync(token);
        }
    }
    private sealed class ConnectingCreator(RecordingLogger logger) : TcpChannelCreator<byte[]>(logger,
        new ChannelOptions { ConnectTimeout = 250 })
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Retrying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async ValueTask ConnectSocketAsync(Socket socket, string host, int port, CancellationToken token)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        protected override async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token)
        { Retrying.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
    }
    [Fact]
    public async Task BackoffIsBoundedAndRepeatedFailuresDoNotSpamWarnings()
    {
        var logger = new RecordingLogger(); var creator = new RefusingCreator(logger);
        try
        {
            await creator.RunAsync("tcp://127.0.0.1:9", new FixedSizePipelineFilter(1));
            int[] expected = [200, 400, 800, 1600, 3200, 5000];
            for (int i = 0; i < expected.Length; i++)
            {
                var retry = await creator.Retries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(expected[i], retry.Delay.TotalMilliseconds);
                if (i + 1 < expected.Length) retry.Resume.TrySetResult();
            }
            Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Warning));
        }
        finally { creator.Dispose(); await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task ConnectDeadlineIsReportedAsTimeoutAndDisposeStopsRetries()
    {
        var logger = new RecordingLogger(); var creator = new ConnectingCreator(logger);
        await creator.RunAsync("tcp://127.0.0.1:9", new FixedSizePipelineFilter(1));
        await creator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await creator.Retrying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("timed out"));
        creator.Dispose(); await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task UriOverridesCopiedOptionsWithoutMutatingTheCaller()
    {
        var options = new ChannelOptions { MaxPackageLength = 8, SendTimeout = 10 };
        await using var client = new SimplifiedClient(
            "udp://127.0.0.1:0/?RemoteEndPoint=127.0.0.1:9&MaxPackageLength=4&SendTimeout=0&MaxPendingPackages=2&ReadAsDemand=true",
            new FixedSizePipelineFilter(1), options);
        await client.Initialization;
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(new byte[5]));
        Assert.Equal(8, options.MaxPackageLength); Assert.Equal(10, options.SendTimeout);
    }
}
