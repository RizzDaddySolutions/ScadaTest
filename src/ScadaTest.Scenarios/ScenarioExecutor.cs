using System.Diagnostics;
using ScadaTest.Core;

namespace ScadaTest.Scenarios;

/// <summary>
/// Runs one scenario: setup, steps, teardown. Teardown and tag restore always run, even after a failure.
/// The first failing setup or step stops the remaining ones in that phase.
/// </summary>
public sealed class ScenarioExecutor(StepRegistry registry)
{
    public async Task<ScenarioResult> RunAsync(
        ScenarioDefinition scenario,
        Func<Action<WriteRecord>, ScadaSession> sessionFactory,
        string evidenceDirectory,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var writes = new List<string>();
        var session = sessionFactory(w => { lock (writes) writes.Add($"{w.Timestamp:O} {w.Tag}: {ValueComparer.Format(w.OldValue)} -> {ValueComparer.Format(w.NewValue)}"); });
        var results = new List<StepResult>();

        var missing = session.MissingFrom(registry.RequiredBy(scenario));
        if (missing != Capability.None)
        {
            return new ScenarioResult(scenario, Outcome.Skipped, started, sw.Elapsed, results,
                $"Environment does not provide: {missing}.", writes);
        }

        var context = new StepContext(session, evidenceDirectory, scenario.Id);
        var outcome = await RunPhaseAsync("setup", scenario.Setup, context, results, ct).ConfigureAwait(false);
        if (outcome == Outcome.Passed)
            outcome = await RunPhaseAsync("steps", scenario.Steps, context, results, ct).ConfigureAwait(false);

        // Teardown runs regardless and does not hide an earlier failure.
        var teardown = await RunPhaseAsync("teardown", scenario.Teardown, context, results, CancellationToken.None, stopOnFailure: false).ConfigureAwait(false);
        string? message = results.FirstOrDefault(r => r.Outcome is Outcome.Failed or Outcome.Error)?.Message;

        if (session.TrackedTags is { } tracked)
        {
            var restoreErrors = await tracked.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (restoreErrors.Count > 0)
            {
                teardown = Outcome.Error;
                message ??= string.Join("; ", restoreErrors.Select(e => e.Message));
            }
        }

        if (outcome == Outcome.Passed && teardown != Outcome.Passed)
            outcome = Outcome.Error;

        return new ScenarioResult(scenario, outcome, started, sw.Elapsed, results, message, writes);
    }

    private async Task<Outcome> RunPhaseAsync(
        string phase, IReadOnlyList<StepDefinition> steps, StepContext context, List<StepResult> results,
        CancellationToken ct, bool stopOnFailure = true)
    {
        var phaseOutcome = Outcome.Passed;
        foreach (var step in steps)
        {
            var sw = Stopwatch.StartNew();
            Outcome outcome;
            string? message = null;
            try
            {
                await registry.Get(step.Kind).ExecuteAsync(context, step.Args, ct).ConfigureAwait(false);
                outcome = Outcome.Passed;
            }
            catch (ExpectationFailedException ex)
            {
                outcome = Outcome.Failed;
                message = ex.Message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = Outcome.Error;
                message = $"{ex.GetType().Name}: {ex.Message}";
            }

            results.Add(new StepResult(phase, step, outcome, sw.Elapsed,
                message is null ? null : $"line {step.Line}, {step.Kind}: {message}", context.TakeEvidence()));

            if (outcome != Outcome.Passed)
            {
                if (phaseOutcome == Outcome.Passed)
                    phaseOutcome = outcome;
                if (stopOnFailure)
                    break;
            }
        }
        return phaseOutcome;
    }
}
