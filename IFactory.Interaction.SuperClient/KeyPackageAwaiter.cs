using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace IFactory.Interaction.SuperClient
{
    public class KeyPackageAwaiter<TKey, TPackage> : ImmediatePackageAwaiter<TPackage>, IPackageAwaiter<TKey, TPackage>
        where TPackage : class
    {
        public TKey Key { get; set; } = default!;
    }
}

