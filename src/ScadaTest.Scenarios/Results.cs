namespace ScadaTest.Scenarios;

public enum Outcome { Passed, Failed, Error, Skipped }

/// <summary>Evidence a step leaves behind: values seen, screenshots, notes.</summary>
public sealed record Evidence(string Kind, string Description, string? FilePath = null);

public sealed record StepResult(
    string Phase,
    StepDefinition Step,
    Outcome Outcome,
    TimeSpan Duration,
    string? Message,
    IReadOnlyList<Evidence> Evidence);

public sealed record ScenarioResult(
    ScenarioDefinition Scenario,
    Outcome Outcome,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<StepResult> Steps,
    string? Message,
    IReadOnlyList<string> Writes)
{
    public bool Passed => Outcome == Outcome.Passed;
}
