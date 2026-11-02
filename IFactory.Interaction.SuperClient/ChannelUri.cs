using Microsoft.Extensions.Logging;

namespace IFactory.Interaction.SuperClient;

internal static class ChannelUri
{
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

    internal static ChannelOptions Options(Dictionary<string, string> query, ILogger? logger)
    {
        var options = new ChannelOptions { Logger = logger! };
        if (query.TryGetValue("ReceiveBufferSize", out var receive))
            options.ReceiveBufferSize = PositiveInt(receive, "ReceiveBufferSize");
        if (query.TryGetValue("MaxPackageLength", out var max))
            options.MaxPackageLength = PositiveInt(max, "MaxPackageLength");
        return options;
    }

    private static int PositiveInt(string value, string name) => int.TryParse(value, out var n) && n > 0
        ? n : throw new ArgumentException($"{name} must be a positive integer.");
}
