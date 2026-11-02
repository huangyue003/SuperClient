using System.Buffers;
using System.Net.Sockets;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

public class TcpPipeChannel<TReceivePackage> : PipeChannel<TReceivePackage>
{
    private readonly Socket _socket;
    public TcpPipeChannel(Socket socket, IPipelineFilter<TReceivePackage> pipelineFilter, ChannelOptions options)
        : base(pipelineFilter, options)
    {
        _socket = socket;
        socket.NoDelay = options.NoDelay;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
        LocalEndPoint = socket.LocalEndPoint!;
        RemoteEndPoint = socket.RemoteEndPoint!;
    }
    protected override void Close() => _socket.Dispose();
    protected override ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
        => _socket.ReceiveAsync(memory, SocketFlags.None, token);
    protected override ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> buffer, CancellationToken token)
        => _socket.SendAsync(buffer.First, SocketFlags.None, token);
    protected override bool IsIgnorableException(Exception e) => base.IsIgnorableException(e)
        || e is SocketException se && se.IsIgnorableSocketException();
}
