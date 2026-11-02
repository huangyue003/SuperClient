using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IFactory.Interaction.SuperClient
{
    public interface IPackageAwaiter<TPackage> where TPackage : class
    {
        void  Write(TPackage? package);
        ValueTask<TPackage?> ReadAsync(int millisecondsTimeout);
    }
    public interface IPackageAwaiter<TKey, TPackage> : IPackageAwaiter<TPackage> where TPackage : class
    {
        TKey Key { get; }
    }
}

