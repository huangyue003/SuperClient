using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SuperSocket.ProtoBase;
namespace IFactory.Interaction.SuperClient
{
    public class KeyPackageScheduler<TKey, TSendPackage, TReceivePackage> : IPackageScheduler<TSendPackage, TReceivePackage>, ISchedulerLifetime
        where TKey : notnull
        where TReceivePackage : class, IKeyedPackageInfo<TKey>
          where TSendPackage : class, IKeyedPackageInfo<TKey>
    {
        ConcurrentDictionary<TKey, IPackageAwaiter<TReceivePackage>> Awaiters { get; } = new ConcurrentDictionary<TKey, IPackageAwaiter<TReceivePackage>>();
        void ISchedulerLifetime.CancelPending()
        {
            foreach (var pair in Awaiters)
                if (Awaiters.TryRemove(pair)) pair.Value.Write(null!);
        }

        private long _unmatched;
        public long UnmatchedPackages => Interlocked.Read(ref _unmatched);
        public Action<TReceivePackage>? MismatchingPackageReceiveHandler { get; set; }

        public ValueTask HandlePackage(TReceivePackage package)
        {
            if (Awaiters.TryRemove(package.Key, out var awaiter))
                awaiter.Write(package);
            else
            { Interlocked.Increment(ref _unmatched); MismatchingPackageReceiveHandler?.Invoke(package); }

            return ValueTask.CompletedTask;
        }

        public IPackageAwaiter<TReceivePackage> RegisterAwaiter(TSendPackage package)
        {
            ArgumentNullException.ThrowIfNull(package, nameof(package));
            var key = package.Key; 
            var awaiter = new KeyPackageAwaiter<TKey, TReceivePackage>() { Key = package.Key };
            awaiter.Completed = () => Awaiters.TryRemove(new KeyValuePair<TKey, IPackageAwaiter<TReceivePackage>>(key, awaiter));
            if (!Awaiters.TryAdd(key, awaiter))
                throw new InvalidOperationException("A request with this key is already pending.");
            return awaiter;
        }
    }
}
