using ScadaTest.Core;

namespace ScadaTest.Scenarios;

/// <summary>Everything a step needs while it runs.</summary>
public sealed class StepContext(IScadaSession session, string evidenceDirectory, string scenarioId)
{
    private readonly List<Evidence> _evidence = [];

    public IScadaSession Session { get; } = session;
    public string EvidenceDirectory { get; } = evidenceDirectory;
    public string ScenarioId { get; } = scenarioId;
    public DateTimeOffset ScenarioStartedAt { get; } = DateTimeOffset.UtcNow;

    public void AddEvidence(Evidence evidence) => _evidence.Add(evidence);

    internal IReadOnlyList<Evidence> TakeEvidence()
    {
        var list = _evidence.ToArray();
        _evidence.Clear();
        return list;
    }
}

public interface IStepHandler
{
    /// <summary>The YAML key, e.g. <c>expect_tag</c>.</summary>
    string Kind { get; }

    /// <summary>The capability the step needs, used to check an environment before running.</summary>
    Capability Requires { get; }

    Task ExecuteAsync(StepContext context, StepArgs args, CancellationToken ct);
}

/// <summary>Maps step kinds to handlers. Add custom steps with <see cref="Register"/>.</summary>
public sealed class StepRegistry
{
    private readonly Dictionary<string, IStepHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public static StepRegistry CreateDefault()
    {
        var r = new StepRegistry();
        foreach (var h in Steps.BuiltInSteps.All())
            r.Register(h);
        return r;
    }

    public StepRegistry Register(IStepHandler handler)
    {
        _handlers[handler.Kind] = handler;
        return this;
    }

    public bool IsKnown(string kind) => _handlers.ContainsKey(kind);

    public IEnumerable<string> Kinds => _handlers.Keys.Order(StringComparer.Ordinal);

    public IStepHandler Get(string kind) =>
        _handlers.TryGetValue(kind, out var h) ? h : throw new ScenarioFormatException($"Unknown step '{kind}'.");

    /// <summary>The capabilities a scenario's steps need, in addition to what it declares.</summary>
    public Capability RequiredBy(ScenarioDefinition scenario) =>
        scenario.Setup.Concat(scenario.Steps).Concat(scenario.Teardown)
            .Aggregate(scenario.Requires, (acc, s) => acc | Get(s.Kind).Requires);
}
