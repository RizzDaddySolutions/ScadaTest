namespace ScadaTest.Core;

/// <summary>A logical tag name such as <c>P-101.Running</c>. Adapters translate it through the <see cref="TagMap"/>.</summary>
public readonly record struct TagRef(string Name)
{
    public static implicit operator TagRef(string name) => new(name);
    public override string ToString() => Name;
}

public enum Quality { Good, Uncertain, Bad }

public sealed record TagValue(TagRef Tag, object? Value, Quality Quality, DateTimeOffset Timestamp);

/// <summary>The capability areas an adapter can provide. A test declares which ones it requires.</summary>
[Flags]
public enum Capability
{
    None = 0,
    Tags = 1,
    Alarms = 2,
    History = 4,
    Hmi = 8,
    Simulator = 16,
}

public readonly record struct AlarmRef(string Source, string Condition)
{
    /// <summary>Parses <c>LT-301.Hi</c> into source <c>LT-301</c> and condition <c>Hi</c>.</summary>
    public static AlarmRef Parse(string text)
    {
        var dot = text.LastIndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
            throw new FormatException($"Alarm reference '{text}' must look like 'Source.Condition'.");
        return new AlarmRef(text[..dot], text[(dot + 1)..]);
    }

    public override string ToString() => $"{Source}.{Condition}";
}

public enum AlarmStatus { Inactive, ActiveUnacked, ActiveAcked, InactiveUnacked }

public sealed record AlarmState(AlarmRef Alarm, AlarmStatus Status, int Priority, string? Message, DateTimeOffset Timestamp);

public enum AlarmEventKind { Raise, Ack, ReturnToNormal }

public sealed record AlarmEvent(AlarmRef Alarm, AlarmEventKind Kind, DateTimeOffset Timestamp, string? Comment = null);

public sealed record AlarmFilter(string? Source = null, string? Condition = null)
{
    public static AlarmFilter All { get; } = new();

    public bool Matches(AlarmRef alarm) =>
        (Source is null || string.Equals(Source, alarm.Source, StringComparison.OrdinalIgnoreCase)) &&
        (Condition is null || string.Equals(Condition, alarm.Condition, StringComparison.OrdinalIgnoreCase));
}

public enum RetrievalMode { Delta, Cyclic }

public sealed record HistorySample(DateTimeOffset Timestamp, object? Value, Quality Quality);

public readonly record struct SymbolRef(string Name)
{
    public static implicit operator SymbolRef(string name) => new(name);
    public override string ToString() => Name;
}

/// <summary>The visual state of an HMI symbol, e.g. <c>running</c>, <c>stopped</c>, <c>fault</c>.</summary>
public sealed record SymbolState(string State, string? Detail = null);

public readonly record struct Region(int X, int Y, int Width, int Height);
