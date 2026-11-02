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
    public UdpChannelCreator(ILogger? logger, ChannelOptions? options) { _logger = logger ?? options?.Logger; _options = options?.Copy(); }
    public EndPoint? LocalEndPoint { get; private set; }
    public Action<IChannel<TReceicePackage>>? ChannelCreated { get; set; }
    public void Dispose()
    {
        Socket? socket;
        lock (_sync) { _disposed = true; socket = _socket; }
        socket?.Dispose();
    }
    public async Task RunAsync(string uri, IPipelineFilter<TReceicePackage> pipelineFilter)
    {
        ArgumentNullException.ThrowIfNull(pipelineFilter);
        var address = ChannelUri.Parse(uri);
        if (address.Scheme != "udp") throw new ArgumentException("UDP creator requires a udp URI.", nameof(uri));
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
        if (multicast)
        {
            if (remote.AddressFamily != AddressFamily.InterNetwork)
                throw new NotSupportedException("IPv6 multicast is not supported.");
            byte first = remote.Address.GetAddressBytes()[0];
            if (first < 224 || first > 239)
                throw new ArgumentException($"Multicast requires an IPv4 group address in URI '{uri}'.", nameof(uri));
        }
        IChannel<TReceicePackage> channel;
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

                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(remote.Address, local.Address));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 10);
                }
                _socket = socket;
                LocalEndPoint = socket.LocalEndPoint;
                channel = new UdpPipeChannel<TReceicePackage>(socket, pipelineFilter, options, remote)
                { Identity = $"{socket.LocalEndPoint}" };
            }
            catch { socket.Dispose(); _socket = null; throw; }
        }
        // External code can reenter Dispose or coordinate with another thread.
        // Never invoke it while the creator mutex owns the transport state.
        try { ChannelCreated?.Invoke(channel); }
        catch
        {
            await channel.CloseAsync(CloseReason.ApplicationError).ConfigureAwait(false);
            lock (_sync) { _socket = null; }
            throw;
        }
    }
}
