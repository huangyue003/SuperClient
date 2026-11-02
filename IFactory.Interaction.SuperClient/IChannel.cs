using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Ports;
using System.Net;
using System.Threading.Tasks;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient
{
    public interface IChannel
    {
        void Start();
        
        ValueTask SendAsync(ReadOnlyMemory<byte> data);
        ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> packageEncoder, TPackage package);

        ValueTask SendAsync(Action<PipeWriter> write);

        ValueTask CloseAsync(CloseReason closeReason);

        event EventHandler<CloseEventArgs>? Closed;

        bool IsClosed { get; }

        EndPoint? RemoteEndPoint { get; }

        EndPoint? LocalEndPoint { get; }

        DateTimeOffset LastActiveTime { get; }

        /// <summary>Built-in transports reject detachment before changing state.</summary>
        /// <remarks>Use CloseAsync. This API does not transfer ownership of a socket or serial port.</remarks>
        ValueTask DetachAsync();
        string Identity { get; }

        CloseReason? CloseReason { get; }
    }

    public interface IChannel<TPackageInfo> : IChannel
    {
        IAsyncEnumerable<TPackageInfo> RunAsync();
    }
}


