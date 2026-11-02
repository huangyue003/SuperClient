using System.Net;
using Microsoft.Extensions.Logging;

namespace IFactory.Interaction.SuperClient;

internal static class ChannelUri
{
    internal static IPEndPoint LocalEndPoint(Uri uri)
    {
        if (uri.Port < 0 || !IPEndPoint.TryParse(uri.Authority, out var endpoint))
            throw new ArgumentException($"Local endpoint must contain an IP literal and port in URI '{uri}'.", nameof(uri));
        return endpoint;
    }
    internal static IPEndPoint RemoteEndPoint(string value, Uri uri)
    {
        if (!IPEndPoint.TryParse(value, out var endpoint) || endpoint.Port == 0)
            throw new ArgumentException($"RemoteEndPoint must contain an IP literal and nonzero port in URI '{uri}'.", nameof(uri));
        return endpoint;
    }
    internal static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || pair[0].Length == 0)
                throw new ArgumentException($"Invalid URI parameter: {part}");
            var key = Uri.UnescapeDataString(pair[0]);
            if (!result.TryAdd(key, Uri.UnescapeDataString(pair[1])))
                throw new ArgumentException($"Duplicate URI parameter: {key}");
        }
        return result;
    }

    internal static ChannelOptions Options(Dictionary<string, string> query, ILogger? logger, ChannelOptions? defaults = null)
    {
        var options = defaults?.Copy() ?? new ChannelOptions();
        options.Logger = logger ?? options.Logger;
        if (query.TryGetValue("ReceiveBufferSize", out var receive))
            options.ReceiveBufferSize = PositiveInt(receive, "ReceiveBufferSize");
        if (query.TryGetValue("MaxPackageLength", out var max))
            options.MaxPackageLength = PositiveInt(max, "MaxPackageLength");
        foreach (var (key, value) in query)
        {
            switch (key.ToUpperInvariant())
            {
                case "SENDTIMEOUT": options.SendTimeout = Nonnegative(value, key); break;
                case "RECEIVETIMEOUT": options.ReceiveTimeout = Nonnegative(value, key); break;
                case "CONNECTTIMEOUT": options.ConnectTimeout = Nonnegative(value, key); break;
                case "MAXPENDINGPACKAGES": options.MaxPendingPackages = PositiveInt(value, key); break;
                case "SENDBUFFERSIZE": options.SendBufferSize = PositiveInt(value, key); break;
                case "READASDEMAND": options.ReadAsDemand = Boolean(value, key); break;
                case "NODELAY": options.NoDelay = Boolean(value, key); break;
                case "KEEPALIVE": options.KeepAlive = Boolean(value, key); break;
                case "SERIALRECONNECT": options.SerialReconnect = Boolean(value, key); break;
                case "RECONNECTMINDELAY": options.ReconnectMinDelay = PositiveInt(value, key); break;
                case "RECONNECTMAXDELAY": options.ReconnectMaxDelay = PositiveInt(value, key); break;
                case "RECONNECTJITTER": options.ReconnectJitter = Boolean(value, key); break;
            }
        }
        options.Validate();
        return options;
    }

    private static int Nonnegative(string value, string name) => int.TryParse(value, out var n) && n >= 0
        ? n : throw new ArgumentException($"{name} must be a nonnegative integer.");
    private static bool Boolean(string value, string name) => bool.TryParse(value, out var b)
        ? b : throw new ArgumentException($"{name} must be true or false.");
    private static int PositiveInt(string value, string name) => int.TryParse(value, out var n) && n > 0
        ? n : throw new ArgumentException($"{name} must be a positive integer.");
}


