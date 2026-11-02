using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;
using System.Net;
using System.Net.Sockets;

namespace IFactory.Interaction.SuperClient;

public class TcpChannelCreator<TReceicePackage> : IChannelCreator<TReceicePackage>
{
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private Socket? _listener;
    private Socket? _socket;
    private bool _disposed;
    public TcpChannelCreator(ILogger? logger) => _logger = logger;
    public Action<IChannel<TReceicePackage>>? ChannelCreated { get; set; }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _listener?.Dispose();
            _socket?.Dispose();
        }
    }

    public Task RunAsync(string uri, IPipelineFilter<TReceicePackage> pipelineFilter)
    {
        var address = new Uri(uri);
        var query = ChannelUri.ParseQuery(address);
        var options = ChannelUri.Options(query, _logger);
        var role = query.GetValueOrDefault("Role", "client");
        if (!role.Equals("client", StringComparison.OrdinalIgnoreCase) && !role.Equals("server", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TCP Role must be client or server.");
        if (address.Port < 0) throw new ArgumentException("TCP port is required.");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null || _socket is not null) throw new InvalidOperationException("Creator has already started.");
            if (role.Equals("server", StringComparison.OrdinalIgnoreCase))
            {
                var endpoint = IPEndPoint.Parse(address.Authority);
                _listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { _listener.Bind(endpoint); _listener.Listen(16); }
                catch { _listener.Dispose(); _listener = null; throw; }
            }
        }
        _ = RunConnectionsAsync(address, pipelineFilter, options);
        return Task.CompletedTask;
    }

    private async Task RunConnectionsAsync(Uri address, IPipelineFilter<TReceicePackage> filter, ChannelOptions options)
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            Socket? socket = null;
            try
            {
                if (_listener is not null) socket = await _listener.AcceptAsync(token).ConfigureAwait(false);
                else
                {
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    lock (_sync) { if (_disposed) { socket.Dispose(); return; } _socket = socket; }
                    await socket.ConnectAsync(address.DnsSafeHost, address.Port, token).ConfigureAwait(false);
                }
                lock (_sync) { if (_disposed) { socket.Dispose(); return; } _socket = socket; }
                filter.Reset();
                var channel = new TcpPipeChannel<TReceicePackage>(socket, filter, options)
                { Identity = $"{socket.RemoteEndPoint}" };
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.Closed += (_, _) => closed.TrySetResult();
                ChannelCreated?.Invoke(channel);
                await closed.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) break;
                _logger?.LogWarning(ex, "TCP connection failed for {Uri}", address);
            }
            finally { socket?.Dispose(); }
            try { await Task.Delay(200, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
