using System.Net;
using System.Net.Sockets;
using SuperSocket.ProtoBase;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class SchedulerIntegrationTests
{
    private sealed class CustomScheduler : IPackageScheduler<byte[], byte[]>, ISerializedRequestScheduler
    {
        private readonly ImmediatePackageScheduler<byte[], byte[]> _inner = new();
        public Action<byte[]>? MismatchingPackageReceiveHandler { get => _inner.MismatchingPackageReceiveHandler; set => _inner.MismatchingPackageReceiveHandler = value; }
        public ValueTask HandlePackage(byte[] package) => _inner.HandlePackage(package);
        public IPackageAwaiter<byte[]> RegisterAwaiter(byte[] package) => _inner.RegisterAwaiter(package);
    }
    [Fact]
    public async Task IndependentSchedulerCanOptIntoSerializedRequests()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new SuperClient<byte[], byte[]>("tcp://127.0.0.1:0/?Role=server", new FixedSizePipelineFilter(1),
            new ArrayPackageEncoder(), new CustomScheduler(), null);
        await client.Initialization; client.ChannelCreated = _ => connected.TrySetResult();
        using var peer = new TcpClient(); await peer.ConnectAsync((IPEndPoint)client.LocalEndPoint!);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = client.SendReceiveAsync(new byte[] { 1 }, 5000);
        var second = client.SendReceiveAsync(new byte[] { 2 }, 5000);
        byte[] bytes = new byte[1];
        await peer.GetStream().ReadExactlyAsync(bytes); Assert.Equal(1, bytes[0]);
        await peer.GetStream().WriteAsync(new byte[] { 10 }); Assert.Equal(new byte[] { 10 }, await first);
        await peer.GetStream().ReadExactlyAsync(bytes); Assert.Equal(2, bytes[0]);
        await peer.GetStream().WriteAsync(new byte[] { 20 }); Assert.Equal(new byte[] { 20 }, await second);
    }
    [Fact]
    public async Task KeyedRequestsReceiveOutOfOrderResponsesEndToEnd()
    {
        var filter = new CommandLinePipelineFilter { Decoder = new DefaultStringPackageDecoder() };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new SuperClient<StringPackageInfo, StringPackageInfo>("tcp://127.0.0.1:0/?Role=server", filter,
            new StringPackageInfoEncoder(), new KeyPackageScheduler<string, StringPackageInfo, StringPackageInfo>(), null);
        await client.Initialization; client.ChannelCreated = _ => connected.TrySetResult();
        using var peer = new TcpClient(); await peer.ConnectAsync((IPEndPoint)client.LocalEndPoint!);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var a = client.SendReceiveAsync(new StringPackageInfo { Key = "a", Body = "1" }, 5000);
        var b = client.SendReceiveAsync(new StringPackageInfo { Key = "b", Body = "2" }, 5000);
        var reader = new StreamReader(peer.GetStream(), leaveOpen: true);
        Assert.StartsWith("a ", await reader.ReadLineAsync()); Assert.StartsWith("b ", await reader.ReadLineAsync());
        await peer.GetStream().WriteAsync(System.Text.Encoding.UTF8.GetBytes("b two\r\na one\r\n"));
        Assert.Equal("one", (await a)!.Body); Assert.Equal("two", (await b)!.Body);
    }
    [Fact]
    public async Task UnmatchedCounterIncludesPackagesHandledAsUnsolicited()
    {
        var scheduler = new ImmediatePackageScheduler<byte[], byte[]>();
        int handled = 0; scheduler.MismatchingPackageReceiveHandler = _ => handled++;
        await scheduler.HandlePackage(new byte[] { 1 });
        Assert.Equal(1, handled); Assert.Equal(1, scheduler.UnmatchedPackages);
    }
}
