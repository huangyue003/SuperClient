using IFactory.Interaction;
using SuperSocket.ProtoBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace IFactory.Interaction.SuperClient
{
    public interface ISuperClient<TSendPackage, TReceivePackage>
        where TReceivePackage : class
        where TSendPackage : class

    {
        string? Uri { get; }
        DateTimeOffset? LastActiveTime { get; }
        IChannel<TReceivePackage>? Channel { get; }
        IAsyncEnumerator<TReceivePackage> _packageStream { get; }

        Task DisposeChannelAsync();
        Task ResetChannelAsync(string uri);
        Task SendAsync(ReadOnlyMemory<byte> data);
        Task SendAsync(TSendPackage package);
        Task<TReceivePackage?> SendReceiveAsync(TSendPackage package, int millisecondsTimeout);
        Task<TReceivePackage?> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout);
    }
}

