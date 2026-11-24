namespace ScadaTest.Core;

/// <summary>
/// Translates logical names (used by tests) into the address each adapter needs, for example
/// <c>P-101.Running</c> to <c>ns=2;s=Galaxy.P101.Running</c> for OPC UA.
/// The same test runs against another site by swapping the map.
/// </summary>
public sealed class TagMap
{
    private readonly Dictionary<string, Dictionary<string, string>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);

    public static TagMap Empty => new();

    /// <summary>Maps one logical tag for one adapter.</summary>
    public TagMap Add(string logicalName, string adapterName, string address)
    {
        if (!_entries.TryGetValue(logicalName, out var perAdapter))
            _entries[logicalName] = perAdapter = new(StringComparer.OrdinalIgnoreCase);
        perAdapter[adapterName] = address;
        return this;
    }

    /// <summary>
    /// Sets a fallback template for an adapter, e.g. <c>ns=2;s=Galaxy.{name}</c>.
    /// <c>{name}</c> is the logical name and <c>{name:nodash}</c> the name without dashes.
    /// </summary>
    public TagMap SetTemplate(string adapterName, string template)
    {
        _templates[adapterName] = template;
        return this;
    }

    public string Resolve(TagRef tag, string adapterName)
    {
        if (_entries.TryGetValue(tag.Name, out var perAdapter) && perAdapter.TryGetValue(adapterName, out var address))
            return address;
        if (_templates.TryGetValue(adapterName, out var template))
            return template
                .Replace("{name:nodash}", tag.Name.Replace("-", string.Empty), StringComparison.Ordinal)
                .Replace("{name}", tag.Name, StringComparison.Ordinal);
        return tag.Name;
    }
}
