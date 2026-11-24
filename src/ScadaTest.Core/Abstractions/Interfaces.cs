namespace ScadaTest.Core;

/// <summary>Configuration handed to an adapter when it connects.</summary>
public sealed record AdapterConfig(
    string Name,
    IReadOnlyDictionary<string, string> Settings,
    TagMap TagMap)
{
    public string Require(string key) =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new InvalidOperationException($"Adapter '{Name}' requires setting '{key}'.");

    public string? Get(string key) => Settings.TryGetValue(key, out var v) ? v : null;
}

/// <summary>An adapter for one vendor or protocol. It implements any subset of the capability interfaces.</summary>
public interface IScadaAdapter : IAsyncDisposable
{
    /// <summary>The adapter type id, e.g. <c>opcua</c>, <c>aveva-historian</c>.</summary>
    string Id { get; }

    Capability Capabilities { get; }

    Task ConnectAsync(AdapterConfig config, CancellationToken ct);
}

public interface ITagClient
{
    Task<TagValue> ReadAsync(TagRef tag, CancellationToken ct);

    Task WriteAsync(TagRef tag, object? value, CancellationToken ct);

    IAsyncEnumerable<TagValue> SubscribeAsync(TagRef tag, CancellationToken ct);
}

public interface IAlarmClient
{
    Task<IReadOnlyList<AlarmState>> GetActiveAsync(AlarmFilter filter, CancellationToken ct);

    Task AcknowledgeAsync(AlarmRef alarm, string comment, CancellationToken ct);

    IAsyncEnumerable<AlarmEvent> SubscribeAsync(AlarmFilter filter, CancellationToken ct);
}

public interface IHistoryClient
{
    Task<IReadOnlyList<HistorySample>> QueryAsync(
        TagRef tag, DateTimeOffset from, DateTimeOffset to,
        RetrievalMode mode, TimeSpan? interval, CancellationToken ct);

    /// <summary>Stored alarm events (raise, ack, return to normal) for the window.</summary>
    Task<IReadOnlyList<AlarmEvent>> QueryAlarmEventsAsync(
        AlarmRef alarm, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public interface IHmiDriver
{
    Task OpenScreenAsync(string screen, CancellationToken ct);

    Task<SymbolState> GetSymbolStateAsync(SymbolRef symbol, CancellationToken ct);

    /// <summary>Captures the screen or a region as PNG bytes.</summary>
    Task<byte[]> CaptureAsync(Region? region, CancellationToken ct);

    Task ClickAsync(SymbolRef symbol, CancellationToken ct);
}

/// <summary>Scripts the process simulator (for example a custom OPC UA simulator) that stands in for the PLCs.</summary>
public interface ISimulatorControl
{
    Task LoadScenarioAsync(string scenario, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);

    /// <summary>Returns the simulator to a known state.</summary>
    Task ResetAsync(CancellationToken ct);

    /// <summary>Drives a field input such as <c>TT-201.PV</c>.</summary>
    Task SetInputAsync(TagRef input, object? value, CancellationToken ct);

    /// <summary>Injects a fault on a device, e.g. <c>trip</c> or <c>fail_to_start</c>.</summary>
    Task InjectFaultAsync(TagRef device, string fault, CancellationToken ct);

    Task<string> GetScenarioStateAsync(CancellationToken ct);
}

/// <summary>Marks an <see cref="IScadaAdapter"/> implementation for discovery by type id.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ScadaAdapterAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
