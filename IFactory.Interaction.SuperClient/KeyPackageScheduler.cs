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
                if (Awaiters.TryRemove(pair.Key, out var awaiter)) awaiter.Write(null!);
        }

        public Action<TReceivePackage>? MismatchingPackageReceiveHandler { get; set; }

        public ValueTask HandlePackage(TReceivePackage package)
        {
            if (Awaiters.TryRemove(package.Key, out var awaiter))
                awaiter.Write(package);
            else
                MismatchingPackageReceiveHandler?.Invoke(package);

            return ValueTask.CompletedTask;
        }

        public IPackageAwaiter<TReceivePackage> RegisterAwaiter(TSendPackage package)
        {
            ArgumentNullException.ThrowIfNull(package, nameof(package));
            var key = package.Key; 
            var awaiter = new KeyPackageAwaiter<TKey, TReceivePackage>() { Key = package.Key };
            awaiter.Completed = () => ((ICollection<KeyValuePair<TKey, IPackageAwaiter<TReceivePackage>>>)Awaiters)
                .Remove(new(key, awaiter));
            if (!Awaiters.TryAdd(key, awaiter))
                throw new InvalidOperationException("A request with this key is already pending.");
            return awaiter;
        }
    }
}
