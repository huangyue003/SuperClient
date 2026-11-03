using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

internal class SerialIOChannelCreator<TReceivePackage> : IChannelCreator<TReceivePackage>
{
    private readonly ILogger? _logger;
    private readonly ChannelOptions? _options;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private SerialPort? _port;
    private IChannel<TReceivePackage>? _current;
    private bool _disposed;
    private bool _started;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public SerialIOChannelCreator(ILogger? logger) : this(logger, null) { }
    public SerialIOChannelCreator(ILogger? logger, ChannelOptions? options) { _logger = logger ?? options?.Logger; _options = options?.Copy(); }
    public Action<IChannel<TReceivePackage>>? ChannelCreated { get; set; }
    public void Dispose()
    {
        IChannel<TReceivePackage>? current;
        lock (_sync) { if (_disposed) return; _disposed = true; current = _current; }
        _stop.Cancel();
        if (current is not null) _ = CloseSafelyAsync(current);
        else _port?.Dispose();
    }
    private async Task CloseSafelyAsync(IChannel<TReceivePackage> channel)
    {
        try { await channel.CloseAsync(CloseReason.LocalClosing).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogError(ex, "Serial channel cleanup failed."); }
    }
    public async Task RunAsync(string uri, IPipelineFilter<TReceivePackage> pipelineFilter)
    {
        ArgumentNullException.ThrowIfNull(pipelineFilter);
        var endpoint = new SerialIOEndPoint(uri);
        var options = ChannelUri.Options(ChannelUri.ParseQuery(ChannelUri.Parse(uri)), _logger, _options);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Creator has already started.");
            _started = true;
        }
        if (options.SerialReconnect) Completion = RunReconnectAsync(endpoint, pipelineFilter, options);
        else Publish(await OpenWithGraceAsync(endpoint, pipelineFilter, options).ConfigureAwait(false));
    }
    private async Task<IChannel<TReceivePackage>> OpenWithGraceAsync(SerialIOEndPoint endpoint,
        IPipelineFilter<TReceivePackage> filter, ChannelOptions options)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            _stop.Token.ThrowIfCancellationRequested();
            try { return OpenChannel(endpoint, filter, options); }
            catch (UnauthorizedAccessException) when (options.SerialOpenRetryTimeout > 0
                && Stopwatch.GetElapsedTime(started).TotalMilliseconds < options.SerialOpenRetryTimeout)
            {
                // Windows drivers may release the exclusive port handle shortly
                // after Close returns. Retry only opening, never any command.
                double remaining = options.SerialOpenRetryTimeout - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining <= 0) throw;
                await WaitForRetryAsync(TimeSpan.FromMilliseconds(Math.Min(25, remaining)), _stop.Token).ConfigureAwait(false);
            }
        }
    }
    protected virtual IChannel<TReceivePackage> OpenChannel(SerialIOEndPoint endpoint,
        IPipelineFilter<TReceivePackage> filter, ChannelOptions options)
    {
        var port = new SerialPort(endpoint.PortName, endpoint.BaudRate, endpoint.Parity, endpoint.Databits, endpoint.StopBits);
        try
        {
            port.Open();
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                _port = port;
            }
            return new SerialIOPipeChannel<TReceivePackage>(port, filter, options.Copy()) { Identity = endpoint.PortName };
        }
        catch { port.Dispose(); throw; }
    }
    protected virtual Task WaitForRetryAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    private void Publish(IChannel<TReceivePackage> channel)
    {
        lock (_sync)
        {
            if (_disposed) { _ = CloseSafelyAsync(channel); return; }
            _current = channel;
        }
        try { ChannelCreated?.Invoke(channel); }
        catch { _ = CloseSafelyAsync(channel); throw; }
    }
    private async Task RunReconnectAsync(SerialIOEndPoint endpoint, IPipelineFilter<TReceivePackage> filter, ChannelOptions options)
    {
        var token = _stop.Token;
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            IChannel<TReceivePackage>? channel = null;
            try
            {
                filter.Reset();
                channel = OpenChannel(endpoint, filter, options);
                var closed = new TaskCompletionSource<CloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.Closed += (_, args) => closed.TrySetResult(args.Reason);
                Publish(channel); failures = 0;
                var reason = await closed.Task.WaitAsync(token).ConfigureAwait(false);
                if (reason is CloseReason.LocalClosing or CloseReason.ApplicationError or CloseReason.ProtocolError or CloseReason.InternalError) return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                failures++;
                if (failures == 1 || failures % 10 == 0)
                    _logger?.LogWarning(ex, "Serial {Port} open failed {Failures} times.", endpoint.PortName, failures);
            }
            finally
            {
                if (channel is not null) await CloseSafelyAsync(channel).ConfigureAwait(false);
                lock (_sync) { if (ReferenceEquals(channel, _current)) _current = null; }
            }
            double delay = Math.Min(options.ReconnectMaxDelay,
                options.ReconnectMinDelay * Math.Pow(2, Math.Min(Math.Max(failures - 1, 0), 30)));
            if (options.ReconnectJitter) delay = Math.Min(options.ReconnectMaxDelay, delay * (0.8 + Random.Shared.NextDouble() * 0.4));
            try { await WaitForRetryAsync(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
