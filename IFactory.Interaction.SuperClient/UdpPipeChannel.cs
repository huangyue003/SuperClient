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
    protected override int MaximumSendLength => Math.Min(base.MaximumSendLength, 65507);
    protected override bool SendEmptyBuffer => true;
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


