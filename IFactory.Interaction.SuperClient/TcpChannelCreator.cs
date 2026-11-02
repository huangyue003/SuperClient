using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IFactory.Interaction.SuperClient;

public class TcpChannelCreator<TReceicePackage> : IChannelCreator<TReceicePackage>
{
    private readonly ILogger? _logger;
    private readonly ChannelOptions? _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private Socket? _listener;
    private Socket? _socket;
    private bool _disposed;
    private bool _started;
    public EndPoint? LocalEndPoint { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public TcpChannelCreator(ILogger? logger) : this(logger, null) { }
    public TcpChannelCreator(ILogger? logger, ChannelOptions? options) { _logger = logger ?? options?.Logger; _options = options?.Copy(); }
    public Action<IChannel<TReceicePackage>>? ChannelCreated { get; set; }

    public void Dispose()
    {
        Socket? listener; Socket? socket;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; listener = _listener; socket = _socket;
        }
        _stop.Cancel(); listener?.Dispose(); socket?.Dispose();
    }
    public Task RunAsync(string uri, IPipelineFilter<TReceicePackage> pipelineFilter)
    {
        var address = new Uri(uri);
        var query = ChannelUri.ParseQuery(address);
        var options = ChannelUri.Options(query, _logger, _options);
        var role = query.GetValueOrDefault("Role", "client");
        if (!role.Equals("client", StringComparison.OrdinalIgnoreCase) && !role.Equals("server", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TCP Role must be client or server.");
        if (address.Port < 0) throw new ArgumentException("TCP port is required.");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Creator has already started.");
            if (role.Equals("server", StringComparison.OrdinalIgnoreCase))
            {
                var endpoint = ChannelUri.LocalEndPoint(address);
                var listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { listener.Bind(endpoint); listener.Listen(16); }
                catch { listener.Dispose(); throw; }
                _listener = listener; LocalEndPoint = listener.LocalEndPoint;
            }
            _started = true;
        }
        Completion = RunConnectionsAsync(address, pipelineFilter, options);
        return Task.CompletedTask;
    }
    protected virtual ValueTask ConnectSocketAsync(Socket socket, string host, int port, CancellationToken token)
        => socket.ConnectAsync(host, port, token);
    protected virtual Task WaitForRetryAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);

    private async Task RunConnectionsAsync(Uri address, IPipelineFilter<TReceicePackage> filter, ChannelOptions options)
    {
        var token = _stop.Token;
        int failures = 0;
        long nextWarning = 1;
        var lastWarning = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            Socket? socket = null;
            bool connected = false;
            try
            {
                if (_listener is not null) socket = await _listener.AcceptAsync(token).ConfigureAwait(false);
                else
                {
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    lock (_sync) { if (_disposed) { socket.Dispose(); return; } _socket = socket; }
                    using var timeout = options.ConnectTimeout == 0 ? null : CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout?.CancelAfter(options.ConnectTimeout);
                    try { await ConnectSocketAsync(socket, address.DnsSafeHost, address.Port, timeout?.Token ?? token).ConfigureAwait(false); }
                    catch (OperationCanceledException ex) when (!token.IsCancellationRequested && timeout?.IsCancellationRequested == true)
                    { throw new TimeoutException($"TCP connect timed out for {address}.", ex); }
                }
                lock (_sync) { if (_disposed) { socket.Dispose(); return; } _socket = socket; }
                if (failures > 0) _logger?.LogInformation("TCP {Uri} recovered after {Failures} failures.", address, failures);
                failures = 0; nextWarning = 1; connected = true;
                filter.Reset();
                var channel = new TcpPipeChannel<TReceicePackage>(socket, filter, options.Copy()) { Identity = $"{socket.RemoteEndPoint}" };
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.Closed += (_, _) => closed.TrySetResult();
                try { ChannelCreated?.Invoke(channel); }
                catch { await channel.CloseAsync(CloseReason.ApplicationError).ConfigureAwait(false); throw; }
                await closed.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) break;
                failures++;
                if (failures >= nextWarning || Stopwatch.GetElapsedTime(lastWarning).TotalSeconds >= 30)
                {
                    _logger?.LogWarning(ex, "TCP {Uri}: {Failures} consecutive failures; latest: {Error}.", address, failures, ex.Message);
                    if (failures >= nextWarning) nextWarning = Math.Min(int.MaxValue, nextWarning * 10);
                    lastWarning = Stopwatch.GetTimestamp();
                }
            }
            finally
            {
                socket?.Dispose();
                lock (_sync) { if (ReferenceEquals(_socket, socket)) _socket = null; }
            }
            if (connected && _listener is not null) continue;
            double delay = Math.Min(options.ReconnectMaxDelay, options.ReconnectMinDelay * Math.Pow(2, Math.Min(Math.Max(failures - 1, 0), 30)));
            if (options.ReconnectJitter) delay = Math.Min(options.ReconnectMaxDelay, delay * (0.8 + Random.Shared.NextDouble() * 0.4));
            try { await WaitForRetryAsync(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}

