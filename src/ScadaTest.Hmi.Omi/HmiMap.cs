using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ScadaTest.Hmi.Omi;

/// <summary>
/// Describes how to reach screens and read symbols in one ViewApp. Example:
/// <code>
/// screens:
///   Area200_Overview: { automationId: NavArea200 }
/// symbols:
///   P-101:
///     region: [412, 220, 48, 48]      # x, y, width, height relative to the ViewApp window
///     tolerance: 40                   # max RGB distance
///     states: { running: "#00B050", stopped: "#808080", fault: "#FF0000" }
/// </code>
/// </summary>
public sealed class HmiMap
{
    public Dictionary<string, ScreenEntry> Screens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SymbolEntry> Symbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static HmiMap Load(string path) =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<HmiMap>(File.ReadAllText(path)) ?? new HmiMap();
}

public sealed class ScreenEntry
{
    /// <summary>UIA AutomationId of the element that opens the screen (a nav button).</summary>
    public string? AutomationId { get; set; }

    /// <summary>UIA Name of that element, used when no AutomationId is set.</summary>
    public string? Name { get; set; }

    /// <summary>Time to let the screen render after navigating, e.g. <c>1s</c>.</summary>
    public string? Settle { get; set; }
}

public sealed class SymbolEntry
{
    public List<int> Region { get; set; } = [];
    public int Tolerance { get; set; } = 40;
    public Dictionary<string, string> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
