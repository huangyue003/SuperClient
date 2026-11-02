using System.Buffers;
using System.IO.Pipelines;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class SendContractTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(1048576)]
    public async Task SendWaitsForTransportAcceptanceAndTimesOut(int size)
    {
        var logger = new RecordingLogger();
        var channel = new ControlledChannel(new ChannelOptions { SendTimeout = 250, Logger = logger }) { Identity = "probe-42" };
        channel.Start();
        var send = channel.SendAsync(new byte[size]).AsTask();
        await channel.NextSendAsync();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => send);
        await channel.CloseAsync(CloseReason.LocalClosing);
        Assert.Equal(CloseReason.TimeOut, channel.CloseReason);
        Assert.Equal((long)size, error.Data["BytesPending"]);
        Assert.Empty(channel.Accepted);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("probe-42")
            && e.Message.Contains("send") && e.Message.Contains("TimeOut") && e.Message.Contains($"pending={size}"));
    }
    [Fact]
    public async Task PartialSendFailsWithoutDuplication()
    {
        var channel = new ControlledChannel(); channel.Start();
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var send = channel.SendAsync(bytes).AsTask();
        (await channel.NextSendAsync()).Accept(3);
        var next = await channel.NextSendAsync();
        Assert.Equal(new byte[] { 4, 5, 6 }, next.Bytes);
        var cause = new IOException("injected transport fault"); next.Fail(cause);
        var error = await Assert.ThrowsAsync<ChannelTransportException>(() => send);
        Assert.Same(cause, error.InnerException);
        Assert.Equal(3, error.BytesAccepted); Assert.Equal(3, error.BytesPending);
        Assert.Equal(new byte[] { 1, 2, 3 }, channel.Accepted);
        Assert.Equal(CloseReason.SocketError, channel.CloseReason);
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task MultipleCallsHaveIndependentCompletionAndOrderedBytes()
    {
        var channel = new ControlledChannel(); channel.Start();
        var first = channel.SendAsync(new byte[] { 1 }).AsTask();
        var call = await channel.NextSendAsync();
        var second = channel.SendAsync(new byte[] { 2 }).AsTask();
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        call.Accept(1); await first;
        var next = await channel.NextSendAsync();
        Assert.Equal(new byte[] { 2 }, next.Bytes); Assert.False(second.IsCompleted);
        next.Accept(1); await second;
        Assert.Equal(new byte[] { 1, 2 }, channel.Accepted);
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task CloseDistinguishesActiveAndQueuedSends()
    {
        var channel = new ControlledChannel(); channel.Start();
        var active = channel.SendAsync(new byte[] { 1 }).AsTask(); await channel.NextSendAsync();
        var queued = channel.SendAsync(new byte[] { 2 }).AsTask();
        await channel.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ChannelTransportException>(() => active);
        Assert.Equal(CloseReason.LocalClosing, error.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.SendAsync(new byte[] { 3 }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.SendAsync(new ArrayPackageEncoder(), new byte[] { 3 }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.SendAsync(_ => { }));
    }
    [Fact]
    public async Task ActionBufferHasAHardLimitAndRejectedFrameDoesNotLeak()
    {
        var channel = new ControlledChannel(new ChannelOptions { MaxPackageLength = 4 }); channel.Start();
        await Assert.ThrowsAsync<ArgumentException>(async () => await channel.SendAsync(w => w.GetMemory(5)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await channel.SendAsync(w => { w.Write(new byte[4]); w.Write(new byte[1]); }));
        Assert.False(channel.IsClosed);
        var send = channel.SendAsync(new byte[] { 9 }).AsTask();
        var call = await channel.NextSendAsync(); Assert.Equal(new byte[] { 9 }, call.Bytes);
        call.Accept(1); await send;
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task UnexpectedCancellationIsATransportFaultNotATimeout()
    {
        var channel = new ControlledChannel(); channel.Start();
        var send = channel.SendAsync(new byte[] { 1 }).AsTask();
        var cause = new OperationCanceledException("transport rejected independently of our tokens");
        (await channel.NextSendAsync()).Fail(cause);
        var error = await Assert.ThrowsAsync<ChannelTransportException>(() => send);
        Assert.Equal(CloseReason.SocketError, error.Reason); Assert.Same(cause, error.InnerException);
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
    [Fact]
    public async Task QueueDeadlineDoesNotCloseAnotherSendOrTransmitTimedOutBytes()
    {
        var options = new ChannelOptions();
        var channel = new ControlledChannel(options); channel.Start();
        var first = channel.SendAsync(new byte[] { 1 }).AsTask(); var call = await channel.NextSendAsync();
        options.SendTimeout = 100;
        var second = channel.SendAsync(new byte[] { 2 }).AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => second);
        Assert.False(channel.IsClosed); Assert.Null(channel.CloseReason);
        call.Accept(1); await first;
        Assert.Equal(new byte[] { 1 }, channel.Accepted);
        await channel.CloseAsync(CloseReason.LocalClosing);
    }    [Fact]
    public async Task SmallPackageCannotCompleteBeforeItsTransportSignal()
    {
        var channel = new ControlledChannel(); channel.Start();
        var send = channel.SendAsync(new byte[16]).AsTask();
        var call = await channel.NextSendAsync(); Assert.False(send.IsCompleted);
        call.Accept(16); await send;
        await channel.CloseAsync(CloseReason.LocalClosing);
    }}



