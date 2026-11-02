using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Ports;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient
{
    public abstract class ChannelBase<TPackageInfo> : IChannel<TPackageInfo>, IChannel
    {
        public abstract void Start();

        public abstract IAsyncEnumerable<TPackageInfo> RunAsync();

        public abstract ValueTask SendAsync(ReadOnlyMemory<byte> buffer);

        public abstract ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> packageEncoder, TPackage package);

        public abstract ValueTask SendAsync(Action<PipeWriter> write);

        public bool IsClosed { get; set; }
        public string Identity { get; set; } = string.Empty;

        public EndPoint? RemoteEndPoint { get; protected set; }

        public EndPoint? LocalEndPoint { get; protected set; }

        public CloseReason? CloseReason { get; protected set; }

        public DateTimeOffset? LastSendTime { get; protected set; }
        public DateTimeOffset? LastReceiveTime { get; protected set; }
        public DateTimeOffset LastActiveTime { get; protected set; } = DateTimeOffset.Now;

        protected virtual void OnClosed()
        {
            IsClosed = true;
            if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
            var closed = Interlocked.Exchange(ref Closed, null);

            var closeReason = CloseReason.HasValue ? CloseReason.Value : SuperClient.CloseReason.Unknown;

            if (closed is null) return;
            foreach (EventHandler<CloseEventArgs> handler in closed.GetInvocationList())
            {
                try { handler(this, new CloseEventArgs(closeReason)); }
                catch (Exception ex) { try { OnObserverFaulted(ex); } catch { } }
            }
        }
        protected virtual void OnObserverFaulted(Exception error) { }
        private int _closedRaised;

        public event EventHandler<CloseEventArgs>? Closed;

        public abstract ValueTask CloseAsync(CloseReason closeReason);

        public abstract ValueTask DetachAsync();

    }
}


