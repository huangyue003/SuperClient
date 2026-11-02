using System.Net;
using IFactory.Interaction.SuperClient;

var server = new TcpChannelCreator<byte[]>(null);
var sessionReady = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
server.ChannelCreated = channel =>
{
    channel.Start();
    sessionReady.TrySetResult(EchoAsync(channel));
};
try
{
    await server.RunAsync("tcp://127.0.0.1:0/?Role=server", new FixedSizePipelineFilter(4));
    var address = (IPEndPoint)server.LocalEndPoint!;
    await using var client = new SimplifiedClient($"tcp://127.0.0.1:{address.Port}",
        new FixedSizePipelineFilter(4), new ChannelOptions { SendTimeout = 2000 });
    await WaitForConnectionAsync(client, TimeSpan.FromSeconds(5));
    var session = await sessionReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
    byte[] sent = [1, 2, 3, 4];
    var received = await client.SendReceiveAsync(sent, 3000);
    if (received is null || !sent.SequenceEqual(received))
        throw new InvalidOperationException("Loopback response did not match the request.");
    Console.WriteLine($"Sent: {Convert.ToHexString(sent)}; received: {Convert.ToHexString(received)}");
    await client.DisposeChannelAsync();
    await session.WaitAsync(TimeSpan.FromSeconds(5));
}
finally
{
    server.Dispose();
    await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
}

static async Task EchoAsync(IChannel<byte[]> channel)
{
    await foreach (var frame in channel.RunAsync())
        await channel.SendAsync(frame);
}

static async Task WaitForConnectionAsync(SimplifiedClient client, TimeSpan timeout)
{
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Connected(SuperClient<byte[], byte[]> _) => ready.TrySetResult();
    client.ChannelCreated += Connected;
    try
    {
        await client.Initialization;
        // Subscribe first, then inspect: this handles connections completed before subscription.
        if (client.Channel is { IsClosed: false }) return;
        await ready.Task.WaitAsync(timeout);
    }
    finally { client.ChannelCreated -= Connected; }
}
