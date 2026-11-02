using System.Buffers;
using System.IO.Ports;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

public class SerialIOPipeChannel<TPackageInfo> : PipeChannel<TPackageInfo>
{
    private readonly SerialPort _serialPort;
    public SerialIOPipeChannel(SerialPort serialPort, IPipelineFilter<TPackageInfo> pipelineFilter, ChannelOptions options)
        : base(pipelineFilter, options) => _serialPort = serialPort;
    protected override void Close() => _serialPort.Dispose();
    public override ValueTask DetachAsync() => ValueTask.FromException(new NotSupportedException(
        "Serial streams cannot reliably cancel an active read without closing the port. Use CloseAsync instead."));
    protected override ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
        => _serialPort.BaseStream.ReadAsync(memory, token);
    protected override async ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> buffer, CancellationToken token)
    {
        await _serialPort.BaseStream.WriteAsync(buffer.First, token).ConfigureAwait(false);
        return buffer.First.Length;
    }
}
