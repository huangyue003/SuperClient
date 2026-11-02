using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ShutdownTests
{
    private sealed class HeldLockChannel : ControlledChannel
    {
        public void Hold() => SendLock.Wait();
        public async Task ReleaseAsync() { SendLock.Release(); await Out.Writer.CompleteAsync(); }
    }
    [Fact]
    public async Task CloseCanSkipABusyOutputWriterLock()
    {
        var channel = new HeldLockChannel(); channel.Start(); channel.Hold();
        try
        {
            await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(channel.IsClosed);
        }
        finally { await channel.ReleaseAsync(); }
    }
    [Fact]
    public async Task CloseDoesNotWaitForBlockedUserWriter()
    {
        var channel = new ControlledChannel(); channel.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var send = Task.Run(async () => await channel.SendAsync(_ => { entered.TrySetResult(); release.Wait(); }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(channel.IsClosed); Assert.False(send.IsCompleted);
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => send);
    }
    [Fact]
    public async Task ShutdownPreservesTheOriginalSendFault()
    {
        var channel = new ControlledChannel(); channel.Start();
        var send = channel.SendAsync(new byte[] { 1 }).AsTask();
        var call = await channel.NextSendAsync(); var cause = new IOException("original failure");
        call.Fail(cause);
        var error = await Assert.ThrowsAsync<ChannelTransportException>(() => send);
        await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(cause, error.InnerException); Assert.Equal(CloseReason.SocketError, channel.CloseReason);
    }
}

