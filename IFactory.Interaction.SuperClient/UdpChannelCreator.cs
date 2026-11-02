using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;
using System.Net;
using System.Net.Sockets;

namespace IFactory.Interaction.SuperClient;

public class UdpChannelCreator<TReceicePackage> : IChannelCreator<TReceicePackage>
{
    private readonly ILogger? _logger;
    private readonly ChannelOptions? _options;
    private readonly object _sync = new();
    private Socket? _socket;
    private bool _disposed;
    public UdpChannelCreator(ILogger? logger) : this(logger, null) { }
    public UdpChannelCreator(ILogger? logger, ChannelOptions? options) { _logger = logger; _options = options?.Copy(); }
    public EndPoint? LocalEndPoint { get; private set; }
    public Action<IChannel<TReceicePackage>>? ChannelCreated { get; set; }
    public void Dispose()
    {
        lock (_sync) { _disposed = true; _socket?.Dispose(); }
    }
    public Task RunAsync(string uri, IPipelineFilter<TReceicePackage> pipelineFilter)
    {
        var address = new Uri(uri);
        var local = ChannelUri.LocalEndPoint(address);
        var query = ChannelUri.ParseQuery(address);
        if (!query.TryGetValue("RemoteEndPoint", out var remoteText))
            throw new ArgumentException("RemoteEndPoint is required in UDP URI.");
        var remote = ChannelUri.RemoteEndPoint(remoteText, address);
        if (local.AddressFamily != remote.AddressFamily) throw new ArgumentException("Endpoint address families must match.");
        var options = ChannelUri.Options(query, _logger, _options);
        // A UDP datagram must be received in full, otherwise the OS discards its tail.
        options.ReceiveBufferSize = Math.Max(options.ReceiveBufferSize, 65536);
        var multicast = false;
        if (query.TryGetValue("Multicast", out var value) && !bool.TryParse(value, out multicast))
            throw new ArgumentException("Multicast must be true or false.");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null) throw new InvalidOperationException("Creator has already started.");
            var socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(local);
                if (OperatingSystem.IsWindows()) socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null);
                if (multicast)
                {
                    if (local.AddressFamily != AddressFamily.InterNetwork) throw new NotSupportedException("IPv6 multicast is not supported.");
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(remote.Address, local.Address));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 10);
                }
                _socket = socket;
                LocalEndPoint = socket.LocalEndPoint;
                ChannelCreated?.Invoke(new UdpPipeChannel<TReceicePackage>(socket, pipelineFilter, options, remote)
                { Identity = $"{socket.LocalEndPoint}" });
            }
            catch { socket.Dispose(); _socket = null; throw; }
        }
        return Task.CompletedTask;
    }
}



