using System.Reflection;
using ScadaTest.Core;

namespace ScadaTest.Runner;

/// <summary>
/// Finds adapter types by their <see cref="ScadaAdapterAttribute"/> id. Built-in adapters are referenced
/// directly; others (such as the Windows-only OMI driver) are loaded from <c>ScadaTest.*.dll</c> files in an
/// <c>adapters</c> folder next to the runner.
/// </summary>
public sealed class AdapterCatalog
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.OrdinalIgnoreCase);

    public static AdapterCatalog Discover(string? pluginDirectory = null)
    {
        var catalog = new AdapterCatalog();
        Assembly[] builtIn =
        [
            typeof(Adapters.InMemory.InMemoryPlant).Assembly,
            typeof(Adapters.OpcUa.OpcUaAdapter).Assembly,
            typeof(Adapters.Historian.HistorianAdapter).Assembly,
        ];
        foreach (var a in builtIn)
            catalog.Scan(a);

        var dir = pluginDirectory ?? Path.Combine(AppContext.BaseDirectory, "adapters");
        if (Directory.Exists(dir))
        {
            foreach (var dll in Directory.EnumerateFiles(dir, "ScadaTest.*.dll"))
            {
                try { catalog.Scan(Assembly.LoadFrom(dll)); }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
                {
                    Console.Error.WriteLine($"warning: could not load adapter assembly {Path.GetFileName(dll)}: {ex.Message}");
                }
            }
        }
        return catalog;
    }

    public IEnumerable<string> Ids => _types.Keys.Order(StringComparer.Ordinal);

    public IScadaAdapter Create(string id) =>
        _types.TryGetValue(id, out var t)
            ? (IScadaAdapter)Activator.CreateInstance(t)!
            : throw new InvalidOperationException($"No adapter with type '{id}'. Available: {string.Join(", ", Ids)}.");

    private void Scan(Assembly assembly)
    {
        foreach (var t in assembly.GetExportedTypes())
            if (t.GetCustomAttribute<ScadaAdapterAttribute>() is { } attr && typeof(IScadaAdapter).IsAssignableFrom(t) && !t.IsAbstract)
                _types[attr.Id] = t;
    }
}
