using ScadaTest.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ScadaTest.Runner;

/// <summary>The contents of <c>environments.yaml</c>.</summary>
public sealed class EnvironmentsFile
{
    /// <summary>Hosts the runner must never connect to. Any adapter setting containing one stops the run.</summary>
    public List<string> ProductionHosts { get; set; } = [];

    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static EnvironmentsFile Load(string path) =>
        Yaml.Deserialize<EnvironmentsFile>(File.ReadAllText(path)) ?? new EnvironmentsFile();

    internal static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
}

public sealed class EnvironmentConfig
{
    public bool AllowWrites { get; set; }
    public List<string> WritablePatterns { get; set; } = ["*"];
    public string? TagMap { get; set; }
    public string EvidenceDir { get; set; } = "evidence";
    public WaitsConfig Waits { get; set; } = new();
    public Dictionary<string, AdapterEntry> Adapters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Capability name (tags, alarms, history, hmi, simulator) to adapter key.</summary>
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdapterEntry
{
    public string Type { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WaitsConfig
{
    public string? Tags { get; set; }
    public string? Alarms { get; set; }
    public string? Hmi { get; set; }
    public string? History { get; set; }
    public string? Poll { get; set; }

    public WaitDefaults ToDefaults()
    {
        var d = new WaitDefaults();
        return d with
        {
            Tags = Tags is null ? d.Tags : Duration.Parse(Tags),
            Alarms = Alarms is null ? d.Alarms : Duration.Parse(Alarms),
            Hmi = Hmi is null ? d.Hmi : Duration.Parse(Hmi),
            History = History is null ? d.History : Duration.Parse(History),
            PollInterval = Poll is null ? d.PollInterval : Duration.Parse(Poll),
        };
    }
}

/// <summary>The contents of a tag map file.</summary>
public sealed class TagMapFile
{
    /// <summary>Adapter key to address template, e.g. <c>opcua: "ns=2;s=Galaxy.{name:nodash}"</c>.</summary>
    public Dictionary<string, string> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logical tag to (adapter key to address).</summary>
    public Dictionary<string, Dictionary<string, string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static TagMap Load(string path)
    {
        var file = EnvironmentsFile.Yaml.Deserialize<TagMapFile>(File.ReadAllText(path)) ?? new TagMapFile();
        var map = new TagMap();
        foreach (var (adapter, template) in file.Templates)
            map.SetTemplate(adapter, template);
        foreach (var (tag, perAdapter) in file.Tags)
            foreach (var (adapter, address) in perAdapter)
                map.Add(tag, adapter, address);
        return map;
    }
}
