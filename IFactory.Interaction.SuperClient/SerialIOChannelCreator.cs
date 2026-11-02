using System.IO.Ports;
using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

internal class SerialIOChannelCreator<TReceivePackage> : IChannelCreator<TReceivePackage>
{
    private readonly ILogger? _logger;
    private readonly object _sync = new();
    private SerialPort? _port;
    private bool _disposed;
    public SerialIOChannelCreator(ILogger? logger) => _logger = logger;
    public Action<IChannel<TReceivePackage>>? ChannelCreated { get; set; }
    public void Dispose() { lock (_sync) { _disposed = true; _port?.Dispose(); } }
    public Task RunAsync(string uri, IPipelineFilter<TReceivePackage> pipelineFilter)
    {
        var endpoint = new SerialIOEndPoint(uri);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_port is not null) throw new InvalidOperationException("Creator has already started.");
            var port = new SerialPort(endpoint.PortName, endpoint.BaudRate, endpoint.Parity, endpoint.Databits, endpoint.StopBits);
            try
            {
                port.Open();
                _port = port;
                ChannelCreated?.Invoke(new SerialIOPipeChannel<TReceivePackage>(port, pipelineFilter,
                    ChannelUri.Options(ChannelUri.ParseQuery(new Uri(uri)), _logger)) { Identity = endpoint.PortName });
            }
            catch { port.Dispose(); _port = null; throw; }
        }
        return Task.CompletedTask;
    }
}
