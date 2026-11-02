using System.Reflection;
using IFactory.Interaction.SuperClient.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ObserverTests
{
    private sealed class LockProbe(object state) : ControlledChannel
    {
        public bool StartedUnderStateLock { get; private set; }
        public override void Start() { StartedUnderStateLock = Monitor.IsEntered(state); base.Start(); }
    }
    [Fact]
    public async Task StartAndSynchronousCallbacksRunOutsideClientStateLock()
    {
        await using var client = new SimplifiedClient("invalid://localhost", new FixedSizePipelineFilter(1));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Initialization);
        var type = typeof(SuperClient<byte[], byte[]>);
        var state = type.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var channel = new LockProbe(state);
        // Inject a synchronous transport into the same private callback used by creators.
        type.GetMethod("OnChannelCreated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, [channel, 0]);
        Assert.False(channel.StartedUnderStateLock);
        await client.DisposeChannelAsync();
    }
    [Fact]
    public async Task ObserverFaultIsLoggedAndCannotSuppressOtherObservers()
    {
        var logger = new RecordingLogger();
        var channel = new ControlledChannel(new ChannelOptions { Logger = logger }) { Identity = "observer-probe" };
        channel.Start(); int calls = 0;
        channel.Closed += (_, _) => throw new Exception("injected observer fault");
        channel.Closed += (_, _) => calls++;
        // Public setter remains for compatibility, but it must not skip real cleanup.
        channel.IsClosed = true;
        await channel.CloseAsync(CloseReason.LocalClosing);
        Assert.Equal(1, calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("observer-probe") && e.Message.Contains("observer"));
    }
}
