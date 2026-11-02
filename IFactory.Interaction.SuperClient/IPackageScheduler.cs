using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using SuperSocket.ProtoBase;
namespace IFactory.Interaction.SuperClient
{
    public interface IPackageScheduler<TSendPackage,TReceivePackage>
        where TReceivePackage : class
        where TSendPackage : class
    {
        ValueTask HandlePackage(TReceivePackage package);
        Action<TReceivePackage>? MismatchingPackageReceiveHandler { get; set; }
        IPackageAwaiter<TReceivePackage> RegisterAwaiter(TSendPackage package);
    }

   
}
