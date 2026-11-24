using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using ScadaTest.Scenarios;

namespace ScadaTest.Reporting;

public static class ConsoleReport
{
    public static void WriteScenario(TextWriter output, ScenarioResult r)
    {
        var mark = r.Outcome switch
        {
            Outcome.Passed => "PASS",
            Outcome.Failed => "FAIL",
            Outcome.Skipped => "SKIP",
            _ => "ERR ",
        };
        output.WriteLine($"[{mark}] {r.Scenario.Id} ({r.Duration.TotalSeconds:0.0} s){(r.Scenario.Title is null ? "" : " " + r.Scenario.Title)}");
        if (r.Message is not null)
            output.WriteLine($"       {r.Message}");
    }

    public static void WriteSummary(TextWriter output, IReadOnlyList<ScenarioResult> results)
    {
        int Count(Outcome o) => results.Count(r => r.Outcome == o);
        output.WriteLine();
        output.WriteLine($"{results.Count} scenarios: {Count(Outcome.Passed)} passed, {Count(Outcome.Failed)} failed, " +
                         $"{Count(Outcome.Error)} errors, {Count(Outcome.Skipped)} skipped.");
    }
}

/// <summary>JUnit XML, understood by Azure DevOps, Jenkins and GitHub Actions test reporters.</summary>
public static class JUnitReport
{
    public static XDocument Build(IReadOnlyList<ScenarioResult> results, string suiteName, string environment)
    {
        static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

        var suite = new XElement("testsuite",
            new XAttribute("name", suiteName),
            new XAttribute("tests", results.Count),
            new XAttribute("failures", results.Count(r => r.Outcome == Outcome.Failed)),
            new XAttribute("errors", results.Count(r => r.Outcome == Outcome.Error)),
            new XAttribute("skipped", results.Count(r => r.Outcome == Outcome.Skipped)),
            new XAttribute("time", Seconds(TimeSpan.FromTicks(results.Sum(r => r.Duration.Ticks)))),
            new XAttribute("timestamp", (results.FirstOrDefault()?.StartedAt ?? DateTimeOffset.UtcNow).ToString("s", CultureInfo.InvariantCulture)),
            new XElement("properties", new XElement("property", new XAttribute("name", "environment"), new XAttribute("value", environment))));

        foreach (var r in results)
        {
            var testcase = new XElement("testcase",
                new XAttribute("classname", r.Scenario.SourceFile is null ? suiteName : Path.GetFileName(r.Scenario.SourceFile)),
                new XAttribute("name", r.Scenario.Title is null ? r.Scenario.Id : $"{r.Scenario.Id}: {r.Scenario.Title}"),
                new XAttribute("time", Seconds(r.Duration)));

            switch (r.Outcome)
            {
                case Outcome.Failed:
                    testcase.Add(new XElement("failure", new XAttribute("message", r.Message ?? "failed"), StepLog(r)));
                    break;
                case Outcome.Error:
                    testcase.Add(new XElement("error", new XAttribute("message", r.Message ?? "error"), StepLog(r)));
                    break;
                case Outcome.Skipped:
                    testcase.Add(new XElement("skipped", new XAttribute("message", r.Message ?? "skipped")));
                    break;
            }
            testcase.Add(new XElement("system-out", StepLog(r)));
            suite.Add(testcase);
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("testsuites", suite));
    }

    private static string StepLog(ScenarioResult r) =>
        string.Join(Environment.NewLine, r.Steps.Select(s =>
            $"[{s.Outcome}] {s.Phase} line {s.Step.Line}: {s.Step} ({s.Duration.TotalMilliseconds:0} ms)" +
            (s.Message is null ? "" : $" - {s.Message}") +
            string.Concat(s.Evidence.Select(e => $"{Environment.NewLine}    {e.Kind}: {e.Description}{(e.FilePath is null ? "" : $" ({e.FilePath})")}")))
            .Concat(r.Writes.Select(w => $"write {w}")));
}

/// <summary>A JSON results file for custom tooling and the future HTML report.</summary>
public static class JsonReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Build(IReadOnlyList<ScenarioResult> results, string environment) =>
        JsonSerializer.Serialize(new
        {
            environment,
            generatedAt = DateTimeOffset.UtcNow,
            scenarios = results.Select(r => new
            {
                id = r.Scenario.Id,
                title = r.Scenario.Title,
                file = r.Scenario.SourceFile,
                outcome = r.Outcome,
                startedAt = r.StartedAt,
                durationMs = (long)r.Duration.TotalMilliseconds,
                message = r.Message,
                writes = r.Writes,
                steps = r.Steps.Select(s => new
                {
                    phase = s.Phase,
                    line = s.Step.Line,
                    kind = s.Step.Kind,
                    args = s.Step.Args.ToString(),
                    outcome = s.Outcome,
                    durationMs = (long)s.Duration.TotalMilliseconds,
                    message = s.Message,
                    evidence = s.Evidence,
                }),
            }),
        }, Options);
}
