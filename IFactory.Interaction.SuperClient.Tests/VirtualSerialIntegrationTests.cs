using System.Diagnostics;
using System.Buffers;
using System.IO.Ports;
using System.Threading.Channels;
using SuperSocket.ProtoBase;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

// Opt-in native-driver integration tests. The collection exclusively owns the
// configured virtual pair; ordinary CI never opens a machine's serial ports.
[CollectionDefinition("VirtualSerial", DisableParallelization = true)]
public sealed class VirtualSerialCollection { }

public sealed class VirtualSerialFactAttribute : FactAttribute
{
    public VirtualSerialFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUPERCLIENT_SERIAL_PORTS")))
            Skip = "Set SUPERCLIENT_SERIAL_PORTS=COM1,COM2 to test an explicitly selected Windows virtual serial pair.";
    }
}

[Collection("VirtualSerial")]
public class VirtualSerialIntegrationTests
{
    private static (string Client, string Peer) Ports()
    {
        var values = Environment.GetEnvironmentVariable("SUPERCLIENT_SERIAL_PORTS")!.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 2 || values.Any(string.IsNullOrWhiteSpace) || values[0].Equals(values[1], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SUPERCLIENT_SERIAL_PORTS must contain two different port names, e.g. COM1,COM2.");
        return (values[0], values[1]);
    }
    private static string Address(string port) => $"sio://{port}/?BaudRate=115200&Parity=None&StopBits=1&DataBits=8";
    private static SerialPort Open(string port)
    {
        var serial = new SerialPort(port, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 3000, WriteTimeout = 3000 };
        try { serial.Open(); serial.DiscardInBuffer(); serial.DiscardOutBuffer(); return serial; }
        catch { serial.Dispose(); throw; }
    }
    private static async Task<SerialPort> OpenEventuallyAsync(string port)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return Open(port); }
            catch (UnauthorizedAccessException) when (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 500)
            { await Task.Delay(10); }
        }
    }
    private static Task<byte[]> ReadAsync(SerialPort port, int size) => ReadCoreAsync(port, size).WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task<byte[]> ReadCoreAsync(SerialPort port, int size)
    { var bytes = new byte[size]; await port.BaseStream.ReadExactlyAsync(bytes); return bytes; }
    private static async Task ExchangeAsync(SimplifiedClient client, SerialPort peer, byte value)
    {
        byte[] request = [value, 2, 3, 4]; byte[] response = [value, 6, 7, 8];
        var pending = client.SendReceiveAsync(request, 3000);
        Assert.Equal(request, await ReadAsync(peer, request.Length));
        await peer.BaseStream.WriteAsync(response);
        Assert.Equal(response, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    private sealed class ObservedFilter : FixedSizePipelineFilter
    {
        public TaskCompletionSource Partial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ObservedFilter() : base(4) { }
        public override byte[] Filter(ref SequenceReader<byte> reader)
        { if (reader.Remaining is > 0 and < 4) Partial.TrySetResult(); return base.Filter(ref reader); }
    }
    [VirtualSerialFact]
    public async Task NativeRequestResponsePreservesAResponseSplitAcrossDriverReads()
    {
        var ports = Ports(); using var peer = await OpenEventuallyAsync(ports.Peer); var filter = new ObservedFilter();
        await using var client = new SimplifiedClient(Address(ports.Client), filter, new ChannelOptions { SendTimeout = 2000, ReadAsDemand = true });
        await client.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = client.SendReceiveAsync(new byte[] { 1, 2, 3, 4 }, 3000);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await ReadAsync(peer, 4));
        await peer.BaseStream.WriteAsync(new byte[] { 9, 8 });
        await filter.Partial.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await peer.BaseStream.WriteAsync(new byte[] { 7, 6 });
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(client.Channel!.LastReceiveTime); Assert.NotNull(client.Channel.LastSendTime);
    }
    [VirtualSerialFact]
    public async Task TwoLibraryClientsCommunicateInBothDirections()
    {
        var ports = Ports();
        await using var left = new SimplifiedClient(Address(ports.Client), new FixedSizePipelineFilter(4));
        await using var right = new SimplifiedClient(Address(ports.Peer), new FixedSizePipelineFilter(4));
        await Task.WhenAll(left.Initialization, right.Initialization).WaitAsync(TimeSpan.FromSeconds(5));
        var atLeft = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atRight = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        left.Scheduler.MismatchingPackageReceiveHandler = bytes => atLeft.TrySetResult(bytes);
        right.Scheduler.MismatchingPackageReceiveHandler = bytes => atRight.TrySetResult(bytes);
        await left.SendAsync(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await atRight.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await right.SendAsync(new byte[] { 5, 6, 7, 8 });
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, await atLeft.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [VirtualSerialFact]
    public async Task NativeReadDecodesMultipleFramesWithoutDroppingTheTail()
    {
        var ports = Ports(); using var peer = await OpenEventuallyAsync(ports.Peer);
        await using var client = new SimplifiedClient(Address(ports.Client), new FixedSizePipelineFilter(4));
        await client.Initialization; var frames = Channel.CreateUnbounded<byte[]>();
        client.Scheduler.MismatchingPackageReceiveHandler = bytes => frames.Writer.TryWrite(bytes);
        await peer.BaseStream.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, await frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [VirtualSerialFact]
    public async Task ClosingAnIdleNativeReadReleasesThePortAndAllowsReset()
    {
        var ports = Ports(); using var peer = await OpenEventuallyAsync(ports.Peer);
        await using var client = new SimplifiedClient(Address(ports.Client), new FixedSizePipelineFilter(4));
        await client.Initialization; var original = client.Channel!; int closed = 0;
        original.Closed += (_, _) => Interlocked.Increment(ref closed);
        await client.DisposeChannelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(original.IsClosed); Assert.Equal(CloseReason.LocalClosing, original.CloseReason); Assert.Equal(1, closed);
        using (var reopened = await OpenEventuallyAsync(ports.Client))
        {
            reopened.Write(new byte[] { 10, 11, 12, 13 }, 0, 4);
            Assert.Equal(new byte[] { 10, 11, 12, 13 }, await ReadAsync(peer, 4));
        }
        await client.ResetChannelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(original, client.Channel); await ExchangeAsync(client, peer, 21);
    }
    [VirtualSerialFact]
    public async Task TenNativeResetCyclesContinueToExchangeExactFrames()
    {
        var ports = Ports(); using var peer = await OpenEventuallyAsync(ports.Peer);
        await using var client = new SimplifiedClient(Address(ports.Client), new FixedSizePipelineFilter(4));
        await client.Initialization;
        for (byte cycle = 0; cycle < 10; cycle++)
        {
            await ExchangeAsync(client, peer, cycle);
            await client.ResetChannelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        await ExchangeAsync(client, peer, 10);
    }
    [VirtualSerialFact]
    public async Task NativeReadTimeoutClosesTheChannelWithoutPeerTraffic()
    {
        var ports = Ports(); using var peer = await OpenEventuallyAsync(ports.Peer);
        var creator = new SerialIOChannelCreator<byte[]>(null, new ChannelOptions { ReceiveTimeout = 250 });
        var closed = new TaskCompletionSource<CloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        IChannel<byte[]>? current = null;
        creator.ChannelCreated = channel => { current = channel; channel.Closed += (_, e) => closed.TrySetResult(e.Reason); channel.Start(); };
        try
        {
            await creator.RunAsync(Address(ports.Client), new FixedSizePipelineFilter(4));
            Assert.Equal(CloseReason.TimeOut, await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(current!.IsClosed);
        }
        finally
        {
            if (current is not null) await current.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            creator.Dispose();
        }
    }
    [VirtualSerialFact]
    public async Task OptInRecoveryReopensTheNativePortAfterAnExplicitFaultClose()
    {
        var ports = Ports(); using var peer = await OpenEventuallyAsync(ports.Peer);
        var creator = new SerialIOChannelCreator<byte[]>(null, new ChannelOptions
        { SerialReconnect = true, ReconnectJitter = false, ReconnectMinDelay = 25, ReconnectMaxDelay = 100 });
        var opened = Channel.CreateUnbounded<IChannel<byte[]>>(); IChannel<byte[]>? latest = null;
        creator.ChannelCreated = c => { latest = c; c.Start(); opened.Writer.TryWrite(c); };
        try
        {
            await creator.RunAsync(Address(ports.Client), new FixedSizePipelineFilter(4));
            var first = await opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await first.SendAsync(new byte[] { 1, 2, 3, 4 }); Assert.Equal(new byte[] { 1, 2, 3, 4 }, await ReadAsync(peer, 4));
            await first.CloseAsync(CloseReason.SocketError);
            var second = await opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(first, second); Assert.Equal(0, peer.BytesToRead);
            await second.SendAsync(new byte[] { 5, 6, 7, 8 }); Assert.Equal(new byte[] { 5, 6, 7, 8 }, await ReadAsync(peer, 4));
        }
        finally
        {
            if (latest is not null) await latest.CloseAsync(CloseReason.LocalClosing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            creator.Dispose(); await creator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }}
