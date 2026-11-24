using System.Globalization;
using ScadaTest.Core;

namespace ScadaTest.Spike;

/// <summary>
/// Minimal parser for <c>command --key value --flag</c>. A key given more than once keeps every value.
/// </summary>
public sealed class SpikeArgs
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    public SpikeArgs(string[] args)
    {
        var hasCommand = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal);
        Command = hasCommand ? args[0].ToLowerInvariant() : "help";
        for (var i = hasCommand ? 1 : 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{args[i]}'. Options look like --name value.");
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            if (!_options.TryGetValue(key, out var list))
                _options[key] = list = [];
            list.Add(value);
        }
    }

    public string? Get(string key) => _options.TryGetValue(key, out var v) ? v[^1] : null;

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public IReadOnlyList<string> GetAll(string key) => _options.TryGetValue(key, out var v) ? v : [];

    public string Require(string key) =>
        Get(key) ?? throw new ArgumentException($"Missing required option --{key}.");

    public bool Flag(string key) => Get(key) is { } v && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    public int Int(string key, int fallback) =>
        Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public double Double(string key, double fallback) =>
        Get(key) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public TimeSpan Time(string key, string fallback) => Duration.Parse(Get(key, fallback));
}
