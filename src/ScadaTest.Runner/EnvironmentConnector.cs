using ScadaTest.Core;

namespace ScadaTest.Runner;

/// <summary>Connects the adapters an environment names and binds each capability to one of them.</summary>
public sealed class ConnectedEnvironment : IAsyncDisposable
{
    private readonly List<IScadaAdapter> _adapters;

    private ConnectedEnvironment(EnvironmentConfig config, List<IScadaAdapter> adapters, Dictionary<Capability, IScadaAdapter> bound)
    {
        Config = config;
        _adapters = adapters;
        Bound = bound;
        Guard = new WriteGuard(config.AllowWrites, config.WritablePatterns);
        Waits = config.Waits.ToDefaults();
    }

    public EnvironmentConfig Config { get; }
    public IReadOnlyDictionary<Capability, IScadaAdapter> Bound { get; }
    public WriteGuard Guard { get; }
    public WaitDefaults Waits { get; }

    public static async Task<ConnectedEnvironment> ConnectAsync(
        EnvironmentsFile file, string name, string baseDirectory, AdapterCatalog catalog, CancellationToken ct)
    {
        if (!file.Environments.TryGetValue(name, out var config))
            throw new InvalidOperationException($"Environment '{name}' is not defined. Known: {string.Join(", ", file.Environments.Keys)}.");

        EnsureNotProduction(file, name, config);

        var tagMap = config.TagMap is null ? TagMap.Empty : TagMapFile.Load(Path.Combine(baseDirectory, config.TagMap));
        var byKey = new Dictionary<string, IScadaAdapter>(StringComparer.OrdinalIgnoreCase);
        var all = new List<IScadaAdapter>();
        try
        {
            foreach (var (key, entry) in config.Adapters)
            {
                var adapter = catalog.Create(entry.Type);
                var settings = entry.Settings.ToDictionary(kv => kv.Key, kv => ResolvePath(kv.Key, kv.Value, baseDirectory), StringComparer.OrdinalIgnoreCase);
                await adapter.ConnectAsync(new AdapterConfig(key, settings, tagMap), ct).ConfigureAwait(false);
                byKey[key] = adapter;
                all.Add(adapter);
            }

            var bound = new Dictionary<Capability, IScadaAdapter>();
            foreach (var (capabilityName, key) in config.Bindings)
            {
                if (!Enum.TryParse<Capability>(capabilityName, true, out var capability) || capability == Capability.None)
                    throw new InvalidOperationException($"Unknown capability '{capabilityName}' in bindings.");
                if (!byKey.TryGetValue(key, out var adapter))
                    throw new InvalidOperationException($"Binding '{capabilityName}' points at unknown adapter '{key}'.");
                if (!adapter.Capabilities.HasFlag(capability))
                    throw new InvalidOperationException($"Adapter '{key}' ({adapter.Id}) does not provide '{capability}'.");
                bound[capability] = adapter;
            }
            return new ConnectedEnvironment(config, all, bound);
        }
        catch
        {
            foreach (var a in all)
                await a.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a fresh session per scenario so write tracking and restore are per test.</summary>
    public ScadaSession CreateSession(Action<WriteRecord> onWrite) => new(
        tags: Bound.GetValueOrDefault(Capability.Tags) as ITagClient,
        alarms: Bound.GetValueOrDefault(Capability.Alarms) as IAlarmClient,
        history: Bound.GetValueOrDefault(Capability.History) as IHistoryClient,
        hmi: Bound.GetValueOrDefault(Capability.Hmi) as IHmiDriver,
        simulator: Bound.GetValueOrDefault(Capability.Simulator) as ISimulatorControl,
        guard: Guard,
        waits: Waits,
        onWrite: onWrite);

    public async ValueTask DisposeAsync()
    {
        foreach (var a in _adapters)
            await a.DisposeAsync().ConfigureAwait(false);
    }

    internal static void EnsureNotProduction(EnvironmentsFile file, string name, EnvironmentConfig config)
    {
        foreach (var (key, entry) in config.Adapters)
        foreach (var (setting, value) in entry.Settings)
        foreach (var host in file.ProductionHosts)
            if (value.Contains(host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Refusing to run: environment '{name}', adapter '{key}', setting '{setting}' points at production host '{host}'.");
    }

    private static string ResolvePath(string key, string value, string baseDirectory) =>
        key.EndsWith("Map", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(baseDirectory, value))
            : value;
}
