using System.Globalization;
using ScadaTest.Core;
using YamlDotNet.RepresentationModel;

namespace ScadaTest.Scenarios;

/// <summary>Loads scenarios from YAML and validates step kinds against the registered handlers.</summary>
public sealed class ScenarioLoader(StepRegistry registry)
{
    public const string FilePattern = "*.scada.yaml";

    public IReadOnlyList<ScenarioDefinition> LoadDirectory(string path) =>
        Directory.EnumerateFiles(path, FilePattern, SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(LoadFile)
            .ToArray();

    public ScenarioDefinition LoadFile(string file)
    {
        try
        {
            return Parse(File.ReadAllText(file)) with { SourceFile = file };
        }
        catch (ScenarioFormatException ex)
        {
            throw new ScenarioFormatException($"{file}: {ex.Message}");
        }
    }

    public ScenarioDefinition Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ScenarioFormatException("A scenario must be a YAML mapping with 'id' and 'steps'.");

        var id = Scalar(root, "id") ?? throw new ScenarioFormatException("Missing 'id'.");
        var scenario = new ScenarioDefinition
        {
            Id = id,
            Title = Scalar(root, "title"),
            Requires = ParseCapabilities(Sequence(root, "requires")),
            Tags = Sequence(root, "tags"),
            Setup = Steps(root, "setup"),
            Steps = Steps(root, "steps"),
            Teardown = Steps(root, "teardown"),
        };
        if (scenario.Steps.Count == 0)
            throw new ScenarioFormatException($"Scenario '{id}' has no steps.");
        return scenario;
    }

    public static Capability ParseCapabilities(IEnumerable<string> names)
    {
        var result = Capability.None;
        foreach (var name in names)
        {
            if (!Enum.TryParse<Capability>(name, ignoreCase: true, out var c))
                throw new ScenarioFormatException($"Unknown capability '{name}'. Use tags, alarms, history, hmi or simulator.");
            result |= c;
        }
        return result;
    }

    private IReadOnlyList<StepDefinition> Steps(YamlMappingNode root, string key)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return [];
        if (node is not YamlSequenceNode seq)
            throw new ScenarioFormatException($"'{key}' must be a list of steps.");

        var steps = new List<StepDefinition>();
        foreach (var item in seq)
        {
            var line = (int)item.Start.Line;
            if (item is not YamlMappingNode map || map.Children.Count != 1)
                throw new ScenarioFormatException($"Line {line}: each step must be a single 'kind: arguments' entry.");
            var (k, v) = map.Children.First();
            var kind = ((YamlScalarNode)k).Value!;
            if (!registry.IsKnown(kind))
                throw new ScenarioFormatException($"Line {line}: unknown step '{kind}'. Known steps: {string.Join(", ", registry.Kinds)}.");
            steps.Add(new StepDefinition(kind, new StepArgs(ToArgs(v)), line));
        }
        return steps;
    }

    private static IReadOnlyDictionary<string, object?> ToArgs(YamlNode node) => node switch
    {
        YamlMappingNode m => m.Children.ToDictionary(
            kv => ((YamlScalarNode)kv.Key).Value!,
            kv => ToValue(kv.Value),
            StringComparer.OrdinalIgnoreCase),
        _ => new Dictionary<string, object?> { [StepArgs.ScalarKey] = ToValue(node) },
    };

    private static object? ToValue(YamlNode node) => node switch
    {
        YamlScalarNode s => ConvertScalar(s),
        YamlSequenceNode q => q.Select(ToValue).ToList(),
        YamlMappingNode m => m.Children.ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => ToValue(kv.Value), StringComparer.OrdinalIgnoreCase),
        _ => null,
    };

    /// <summary>Plain scalars become bool, long or double when they look like one; quoted scalars stay strings.</summary>
    private static object? ConvertScalar(YamlScalarNode s)
    {
        var text = s.Value;
        if (text is null)
            return null;
        if (s.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            return text;
        if (text is "~" or "null")
            return null;
        if (bool.TryParse(text, out var b))
            return b;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return text;
    }

    private static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlScalarNode s ? s.Value : null;

    private static IReadOnlyList<string> Sequence(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlSequenceNode seq
            ? seq.OfType<YamlScalarNode>().Select(s => s.Value!).ToArray()
            : [];
}
