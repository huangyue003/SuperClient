using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipelines;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

public class UdpPipeChannel<TReceivePackage> : PipeChannel<TReceivePackage>
{
    private readonly Socket _socket;
    public UdpPipeChannel(Socket socket, IPipelineFilter<TReceivePackage> pipelineFilter, ChannelOptions options, IPEndPoint remoteEndPoint)
        : base(pipelineFilter, options)
    {
        _socket = socket;
        options.ReceiveBufferSize = Math.Max(options.ReceiveBufferSize, 65536);
        LocalEndPoint = socket.LocalEndPoint!;
        RemoteEndPoint = remoteEndPoint;
    }
    protected override void Close() => _socket.Dispose();
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer)
    {
        await SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CheckChannelOpen();
            if (buffer.Length > 65507) throw new ArgumentException("UDP payload exceeds 65507 bytes.");
            using var timeout = CreateTimeout(CancellationToken.None, Options.SendTimeout);
            await _socket.SendToAsync(buffer, SocketFlags.None, RemoteEndPoint!, timeout?.Token ?? CancellationToken.None).ConfigureAwait(false);
            LastActiveTime = DateTimeOffset.Now;
        }
        finally { SendLock.Release(); }
    }
    public override ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> encoder, TPackage package)
    {
        CheckChannelOpen();
        var buffer = new ArrayBufferWriter<byte>();
        encoder.Encode(buffer, package);
        return SendAsync(buffer.WrittenMemory);
    }
    public override async ValueTask SendAsync(Action<PipeWriter> write)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        try
        {
            write(pipe.Writer);
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            var result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
            await SendAsync(result.Buffer.ToArray()).ConfigureAwait(false);
            pipe.Reader.AdvanceTo(result.Buffer.End);
        }
        finally
        {
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
        }
    }
    protected override async ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
    {
        // UDP may legally contain empty datagrams; those do not indicate EOF.
        while (true)
        {
            var result = await _socket.ReceiveFromAsync(memory, SocketFlags.None,
                new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0), token).ConfigureAwait(false);
            if (result.ReceivedBytes > 0) return result.ReceivedBytes;
        }
    }
    protected override ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> buffer, CancellationToken token)
    {
        if (buffer.Length > 65507) throw new ArgumentException("UDP payload exceeds 65507 bytes.");
        // Preserve one datagram even when the pipe buffer spans several segments.
        return _socket.SendToAsync(buffer.IsSingleSegment ? buffer.First : buffer.ToArray(), SocketFlags.None, RemoteEndPoint!, token);
    }
}

