namespace IFactory.Interaction.SuperClient;

public class ImmediatePackageScheduler<TSendPackage, TReceivePackage> : IPackageScheduler<TSendPackage, TReceivePackage>, ISchedulerLifetime, ISerializedRequestScheduler
    where TReceivePackage : class where TSendPackage : class
{
    private IPackageAwaiter<TReceivePackage>? _current;
    private long _unmatched;
    public long UnmatchedPackages => Interlocked.Read(ref _unmatched);
    void ISchedulerLifetime.CancelPending() => Interlocked.Exchange(ref _current, null)?.Write(null!);
    public Action<TReceivePackage>? MismatchingPackageReceiveHandler { get; set; }

    public ValueTask HandlePackage(TReceivePackage package)
    {
        var awaiter = Interlocked.Exchange(ref _current, null);
        if (awaiter is null) { Interlocked.Increment(ref _unmatched); MismatchingPackageReceiveHandler?.Invoke(package); }
        else awaiter.Write(package);
        return ValueTask.CompletedTask;
    }

    public IPackageAwaiter<TReceivePackage> RegisterAwaiter(TSendPackage package)
    {
        var awaiter = new ImmediatePackageAwaiter<TReceivePackage>();
        awaiter.Completed = () => Interlocked.CompareExchange(ref _current, null, awaiter);
        if (Interlocked.CompareExchange(ref _current, awaiter, null) is not null)
            throw new InvalidOperationException("An unkeyed request is already pending.");
        return awaiter;
    }
}

