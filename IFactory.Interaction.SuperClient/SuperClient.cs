using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

public class SuperClient<TSendPackage, TReceivePackage> : ISuperClient<TSendPackage, TReceivePackage>, IAsyncDisposable
    where TReceivePackage : class where TSendPackage : class
{
    private readonly ILogger? _logger;
    private readonly ChannelOptions? _options;
    private readonly IPackageEncoder<TSendPackage> _encoder;
    private readonly IPipelineFilter<TReceivePackage> _filter;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _state = new();
    private IChannelCreator<TReceivePackage>? _creator;
    private Task _receiveTask = Task.CompletedTask;
    private int _generation;
    private int _connectionVersion;
    private DateTimeOffset? _lastActiveTime;

    public SuperClient(string uri, IPipelineFilter<TReceivePackage> pipelineFilter,
        IPackageEncoder<TSendPackage> packageEncoder, IPackageScheduler<TSendPackage, TReceivePackage> scheduler, ILogger? logger)
        : this(uri, pipelineFilter, packageEncoder, scheduler, logger, null) { }

    public SuperClient(string uri, IPipelineFilter<TReceivePackage> pipelineFilter,
        IPackageEncoder<TSendPackage> packageEncoder, IPackageScheduler<TSendPackage, TReceivePackage> scheduler,
        ILogger? logger, ChannelOptions? options)
    {
        _options = options?.Copy();
        ArgumentNullException.ThrowIfNull(pipelineFilter);
        ArgumentNullException.ThrowIfNull(packageEncoder);
        ArgumentNullException.ThrowIfNull(scheduler);
        _logger = logger ?? options?.Logger;
        _filter = pipelineFilter;
        _encoder = packageEncoder;
        Scheduler = scheduler;
        Uri = uri;
        Initialization = InitializeAsync(uri);
    }

    // Initialization failures are observable; TCP reconnect attempts continue in the creator.
    public Task Initialization { get; private set; }
    public string? Uri { get; set; }
    public IChannel<TReceivePackage>? Channel { get; private set; }
    public DateTimeOffset? LastActiveTime => Channel?.LastActiveTime ?? _lastActiveTime;
    public IPackageScheduler<TSendPackage, TReceivePackage> Scheduler { get; }
    public IAsyncEnumerator<TReceivePackage> _packageStream { get; set; } = null!;
    public System.Net.EndPoint? LocalEndPoint => Channel?.LocalEndPoint ?? _creator?.LocalEndPoint;
    public CloseReason? LastCloseReason { get; private set; }
    public event EventHandler<ChannelDisconnectedEventArgs<TReceivePackage>>? Disconnected;
    public int Idetify { get; set; }
    public Action<SuperClient<TSendPackage, TReceivePackage>>? ChannelCreated { get; set; }
    public Action<SuperClient<TSendPackage, TReceivePackage>>? ChannelDisconnected { get; set; }

    private async Task InitializeAsync(string uri)
    {
        var address = ChannelUri.Parse(uri);
        IChannelCreator<TReceivePackage> creator = address.Scheme switch
        {
            "sio" => new SerialIOChannelCreator<TReceivePackage>(_logger, _options),
            "udp" => new UdpChannelCreator<TReceivePackage>(_logger, _options),
            "tcp" => new TcpChannelCreator<TReceivePackage>(_logger, _options),
            _ => throw new ArgumentException($"Unsupported URI scheme: {address.Scheme}", nameof(uri))
        };
        int generation;
        lock (_state) { _creator = creator; generation = ++_generation; }
        creator.ChannelCreated = channel => OnChannelCreated(channel, generation);
        try { await creator.RunAsync(uri, _filter).ConfigureAwait(false); }
        catch { creator.Dispose(); throw; }
    }

    private void OnChannelCreated(IChannel<TReceivePackage> channel, int generation)
    {
        int version;
        lock (_state)
        {
            if (generation != _generation) { _ = channel.CloseAsync(CloseReason.LocalClosing); return; }
            Channel = channel;
            channel.Closed += ChannelClosed;
            version = ++_connectionVersion;
        }
        // Start and the synchronous prefix of ReceiveAsync can call user code.
        // Never execute them while holding the lifecycle state lock.
        channel.Start();
        var stream = channel.GetPackageStream();
        lock (_state)
        {
            if (version != _connectionVersion) { _ = channel.CloseAsync(CloseReason.LocalClosing); return; }
            _packageStream = stream;
        }
        _receiveTask = ReceiveAsync(channel, stream, version);
        try { ChannelCreated?.Invoke(this); }
        catch (Exception ex) { _logger?.LogError(ex, "ChannelCreated callback failed."); }
    }
    private IChannel<TReceivePackage> GetChannel()
    {
        var channel = Channel;
        if (channel is null || channel.IsClosed) throw new InvalidOperationException("No active channel is available.");
        return channel;
    }
    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        await Initialization.ConfigureAwait(false);
        await GetChannel().SendAsync(data).ConfigureAwait(false);
    }
    public async Task SendAsync(TSendPackage package)
    {
        await Initialization.ConfigureAwait(false);
        await GetChannel().SendAsync(_encoder, package).ConfigureAwait(false);
    }
    /// <summary>Wait for a matching response after transport acceptance.</summary>
    /// <remarks>millisecondsTimeout excludes serialized-request queueing and sending; -1 waits indefinitely.</remarks>
    public Task<TReceivePackage?> SendReceiveAsync(TSendPackage package, int millisecondsTimeout)
        => SendReceiveCoreAsync(package, channel => channel.SendAsync(_encoder, package).AsTask(), millisecondsTimeout);
    public Task<TReceivePackage?> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout)
        => SendReceiveCoreAsync(null!, channel => channel.SendAsync(data).AsTask(), millisecondsTimeout);

    private async Task<TReceivePackage?> SendReceiveCoreAsync(TSendPackage package, Func<IChannel<TReceivePackage>, Task> send, int timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, Timeout.Infinite);
        var serialize = Scheduler is ISerializedRequestScheduler;
        if (serialize) await _requests.WaitAsync().ConfigureAwait(false);
        IPackageAwaiter<TReceivePackage>? awaiter = null;
        try
        {
            await Initialization.ConfigureAwait(false);
            IChannel<TReceivePackage> channel;
            lock (_state)
            {
                channel = GetChannel();
                awaiter = Scheduler.RegisterAwaiter(package);
            }
            await send(channel).ConfigureAwait(false);
            return await awaiter.ReadAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            if (awaiter is ImmediatePackageAwaiter<TReceivePackage> pending) pending.Completed?.Invoke();
            if (serialize) _requests.Release();
        }
    }

    public async Task ResetChannelAsync(string uri = "")
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await Initialization.ConfigureAwait(false); } catch { /* A reset may recover failed initialization. */ }
            await DisposeCoreAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(uri)) Uri = uri;
            _filter.Reset();
            Initialization = InitializeAsync(Uri ?? throw new InvalidOperationException("URI is required."));
            await Initialization.ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
    public async Task DisposeChannelAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await Initialization.ConfigureAwait(false); } catch { }
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
    private async Task DisposeCoreAsync()
    {
        IChannel<TReceivePackage>? channel;
        IChannelCreator<TReceivePackage>? creator;
        lock (_state)
        {
            ++_generation;
            ++_connectionVersion;
            creator = _creator;
            _creator = null;
            channel = Channel;
            _lastActiveTime = channel?.LastActiveTime ?? _lastActiveTime;
            Channel = null;
            _packageStream = null!;
            (Scheduler as ISchedulerLifetime)?.CancelPending();
        }
        // Mark local closing before disposing the creator's socket.
        if (channel is not null)
        {
            try { await channel.CloseAsync(CloseReason.LocalClosing).ConfigureAwait(false); }
            finally { channel.Closed -= ChannelClosed; creator?.Dispose(); }
        }
        else creator?.Dispose();
    }
    public ValueTask DisposeAsync() => new(DisposeChannelAsync());

    // Kept for source compatibility. The receive loop is started automatically exactly once.
    public void StartRecive() { }

    private async Task ReceiveAsync(IChannel<TReceivePackage> channel, IAsyncEnumerator<TReceivePackage> stream, int version)
    {
        try
        {
            while (await stream.MoveNextAsync().ConfigureAwait(false))
            {
                if (version != Volatile.Read(ref _connectionVersion)) break;
                await Scheduler.HandlePackage(stream.Current).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Receive loop failed for {Uri}.", Uri);
            if (!channel.IsClosed) await channel.CloseAsync(CloseReason.ApplicationError).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            lock (_state)
            {
                if (version == _connectionVersion) (Scheduler as ISchedulerLifetime)?.CancelPending();
            }
        }
    }
    private void ChannelClosed(object? sender, CloseEventArgs args)
    {
        lock (_state)
        {
            if (ReferenceEquals(Channel, sender))
            {
                _lastActiveTime = Channel?.LastActiveTime ?? _lastActiveTime;
                Channel = null;
            }
        }
        LastCloseReason = args.Reason;
        if (sender is IChannel<TReceivePackage> closed && Disconnected is { } handlers)
        {
            var snapshot = new ChannelDisconnectedEventArgs<TReceivePackage>(closed, args.Reason);
            foreach (EventHandler<ChannelDisconnectedEventArgs<TReceivePackage>> handler in handlers.GetInvocationList())
            {
                try { handler(this, snapshot); }
                catch (Exception ex) { _logger?.LogError(ex, "Disconnected observer failed."); }
            }
        }
        try { ChannelDisconnected?.Invoke(this); }
        catch (Exception ex) { _logger?.LogError(ex, "ChannelDisconnected callback failed."); }
        _logger?.LogInformation("Channel {Uri} closed: {Reason}.", Uri, args.Reason);
    }
}






