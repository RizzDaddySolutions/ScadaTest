using System.Globalization;
using System.Text.RegularExpressions;

namespace ScadaTest.Core;

public static partial class TimeExtensions
{
    public static TimeSpan Seconds(this int value) => TimeSpan.FromSeconds(value);
    public static TimeSpan Seconds(this double value) => TimeSpan.FromSeconds(value);
    public static TimeSpan Milliseconds(this int value) => TimeSpan.FromMilliseconds(value);
    public static TimeSpan Minutes(this int value) => TimeSpan.FromMinutes(value);
}

/// <summary>Parses durations as written in YAML: <c>500ms</c>, <c>10s</c>, <c>1.5m</c>, <c>1h</c>.</summary>
public static partial class Duration
{
    [GeneratedRegex(@"^\s*(?<n>\d+(\.\d+)?)\s*(?<u>ms|s|m|h)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static TimeSpan Parse(string text)
    {
        var m = Pattern().Match(text);
        if (!m.Success)
            throw new FormatException($"'{text}' is not a duration. Use e.g. 500ms, 10s, 2m or 1h.");
        var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        return m.Groups["u"].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            _ => TimeSpan.FromHours(n),
        };
    }
}
