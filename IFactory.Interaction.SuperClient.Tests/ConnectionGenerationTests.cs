using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using SuperSocket.ProtoBase;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class ConnectionGenerationTests
{
    [Fact]
    public async Task NewConnectionCompletesOldRequestsEvenWhenOldReceiveCallbackIsStillBlocked()
    {
        var scheduler = new KeyPackageScheduler<string, StringPackageInfo, StringPackageInfo>();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.MismatchingPackageReceiveHandler = _ => { entered.TrySetResult(); release.Wait(); };
        var filter = new CommandLinePipelineFilter { Decoder = new DefaultStringPackageDecoder() };
        await using var client = new SuperClient<StringPackageInfo, StringPackageInfo>(
            "tcp://127.0.0.1:0/?Role=server", filter, new StringPackageInfoEncoder(), scheduler, null);
        var connections = Channel.CreateUnbounded<IChannel<StringPackageInfo>>();
        await client.Initialization; client.ChannelCreated = c => connections.Writer.TryWrite(c.Channel!);
        using var first = new TcpClient(); using var second = new TcpClient();
        Task<StringPackageInfo?>? pending = null;
        try
        {
            var endpoint = Assert.IsType<IPEndPoint>(client.LocalEndPoint);
            await first.ConnectAsync(endpoint);
            await connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            pending = client.SendReceiveAsync(new StringPackageInfo { Key = "old", Body = "request" }, Timeout.Infinite);
            using var reader = new StreamReader(first.GetStream(), leaveOpen: true);
            Assert.StartsWith("old ", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await first.GetStream().WriteAsync(Encoding.UTF8.GetBytes("notice unsolicited\r\n"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.Dispose();
            await second.ConnectAsync(endpoint);
            await connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            // A new request must survive completion of the old callback.
            var current = client.SendReceiveAsync(new StringPackageInfo { Key = "new", Body = "request" }, 5000);
            using var nextReader = new StreamReader(second.GetStream(), leaveOpen: true);
            Assert.StartsWith("new ", await nextReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            release.Set();
            await second.GetStream().WriteAsync(Encoding.UTF8.GetBytes("new response\r\n"));
            Assert.Equal("response", (await current)!.Body);
        }
        finally { release.Set(); }
    }
}
