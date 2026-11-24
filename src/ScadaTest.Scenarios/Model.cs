using ScadaTest.Core;

namespace ScadaTest.Scenarios;

/// <summary>One test scenario, as loaded from a <c>*.scada.yaml</c> file.</summary>
public sealed record ScenarioDefinition
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public Capability Requires { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<StepDefinition> Setup { get; init; } = [];
    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];
    public IReadOnlyList<StepDefinition> Teardown { get; init; } = [];
    public string? SourceFile { get; init; }
}

/// <summary>A single step: its kind (e.g. <c>write</c>) and its arguments.</summary>
public sealed record StepDefinition(string Kind, StepArgs Args, int Line)
{
    public override string ToString() => $"{Kind} {Args}";
}

/// <summary>Step arguments. A scalar step such as <c>open_screen: Overview</c> stores its value under <see cref="ScalarKey"/>.</summary>
public sealed class StepArgs(IReadOnlyDictionary<string, object?> values)
{
    public const string ScalarKey = "_";

    public IReadOnlyDictionary<string, object?> Values { get; } = values;

    public bool Has(string key) => Values.ContainsKey(key);

    public object? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public string String(string key) =>
        Values.TryGetValue(key, out var v) && v is not null
            ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!
            : throw new ScenarioFormatException($"Missing required argument '{key}'.");

    public string? OptionalString(string key) =>
        Values.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The scalar value, or the named argument when the step uses mapping form.</summary>
    public string ScalarOr(string key) => Has(ScalarKey) ? String(ScalarKey) : String(key);

    public TimeSpan? Duration(string key) => OptionalString(key) is { } s ? Core.Duration.Parse(s) : null;

    public double? Double(string key) =>
        OptionalString(key) is { } s
            ? double.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public override string ToString() =>
        Has(ScalarKey) ? ValueComparer.Format(Get(ScalarKey)) : "{ " + string.Join(", ", Values.Select(kv => $"{kv.Key}: {ValueComparer.Format(kv.Value)}")) + " }";
}

public sealed class ScenarioFormatException(string message) : Exception(message);
