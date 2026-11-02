using System.Threading.Channels;

namespace IFactory.Interaction.SuperClient;

// A single consumer reads decoded packages. Framework channels handle wakeups and
// continuation scheduling without pooled-array ownership or reusable task tokens.
internal class DefaultObjectPipe<T> : IObjectPipe<T>, IDisposable
{
    private int _length;
    private readonly int _capacity;
    public DefaultObjectPipe(int capacity = 1024) => _capacity = capacity;
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });

    public int Write(T target)
    {
        if (Interlocked.Increment(ref _length) > _capacity)
        {
            Interlocked.Decrement(ref _length);
            throw new ChannelQueueOverflowException(Volatile.Read(ref _length), _capacity);
        }
        if (!_channel.Writer.TryWrite(target))
        {
            Interlocked.Decrement(ref _length);
            throw new ObjectDisposedException(GetType().Name);
        }
        return Volatile.Read(ref _length);
    }

    public async ValueTask<(bool HasValue, T Value)> ReadAsync()
    {
        OnWaitTaskStart();
        try
        {
            var value = await _channel.Reader.ReadAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _length);
            return (true, value);
        }
        catch (ChannelClosedException) { return (false, default!); }
    }

    protected virtual void OnWaitTaskStart() { }
    public void Complete() => _channel.Writer.TryComplete();
    public void Dispose() => Complete();
}

internal class DefaultObjectPipeWithSupplyControl<T> : DefaultObjectPipe<T>, ISupplyController
{
    public DefaultObjectPipeWithSupplyControl(int capacity = 1024) : base(capacity) { }
    private readonly SemaphoreSlim _demand = new(0, 1);
    public async ValueTask SupplyRequired() => await _demand.WaitAsync().ConfigureAwait(false);
    protected override void OnWaitTaskStart() => Signal();
    public void SupplyEnd() => Signal();
    private void Signal()
    {
        try { _demand.Release(); }
        catch (SemaphoreFullException) { }
    }
}

