using ScadaTest.Core;

namespace ScadaTest.Scenarios.Steps;

internal sealed class DelegateStep(string kind, Capability requires, Func<StepContext, StepArgs, CancellationToken, Task> run) : IStepHandler
{
    public string Kind { get; } = kind;
    public Capability Requires { get; } = requires;
    public Task ExecuteAsync(StepContext context, StepArgs args, CancellationToken ct) => run(context, args, ct);
}

/// <summary>The built-in step vocabulary. Each step is small on purpose; add custom steps through <see cref="StepRegistry"/>.</summary>
public static class BuiltInSteps
{
    public static IEnumerable<IStepHandler> All()
    {
        // ---- actions ----
        yield return new DelegateStep("write", Capability.Tags, (c, a, ct) =>
            c.Session.Tags.WriteAsync(a.String("tag"), a.Get("value"), ct));

        yield return new DelegateStep("wait", Capability.None, (c, a, ct) =>
            Task.Delay(Duration.Parse(a.ScalarOr("for")), ct));

        yield return new DelegateStep("open_screen", Capability.Hmi, (c, a, ct) =>
            c.Session.Hmi.OpenScreenAsync(a.ScalarOr("screen"), ct));

        yield return new DelegateStep("click", Capability.Hmi, (c, a, ct) =>
            c.Session.Hmi.ClickAsync(a.ScalarOr("symbol"), ct));

        yield return new DelegateStep("capture", Capability.Hmi, Capture);

        yield return new DelegateStep("ack", Capability.Alarms, (c, a, ct) =>
        {
            // 'via: hmi' is accepted for readability; the HMI driver performs the ack once HMI alarm grids are mapped.
            var alarm = AlarmRef.Parse(a.ScalarOr("alarm"));
            return c.Session.Alarms.AcknowledgeAsync(alarm, a.OptionalString("comment") ?? "automated test", ct);
        });

        // ---- simulator ----
        yield return new DelegateStep("sim_load", Capability.Simulator, (c, a, ct) =>
        {
            var parameters = a.Get("parameters") as IReadOnlyDictionary<string, object?>
                ?? (a.Get("parameters") as Dictionary<string, object?>);
            return c.Session.Simulator.LoadScenarioAsync(a.ScalarOr("scenario"), parameters, ct);
        });

        yield return new DelegateStep("sim_set", Capability.Simulator, (c, a, ct) =>
            c.Session.Simulator.SetInputAsync(a.String("input"), a.Get("value"), ct));

        yield return new DelegateStep("sim_fault", Capability.Simulator, (c, a, ct) =>
            c.Session.Simulator.InjectFaultAsync(a.String("device"), a.String("fault"), ct));

        yield return new DelegateStep("sim_reset", Capability.Simulator, (c, a, ct) =>
            c.Session.Simulator.ResetAsync(ct));

        // ---- expectations ----
        yield return new DelegateStep("expect_tag", Capability.Tags, ExpectTag);
        yield return new DelegateStep("expect_alarm", Capability.Alarms, ExpectAlarm);
        yield return new DelegateStep("expect_symbol", Capability.Hmi, ExpectSymbol);
        yield return new DelegateStep("expect_history", Capability.History, ExpectHistory);
    }

    private static async Task ExpectTag(StepContext c, StepArgs a, CancellationToken ct)
    {
        var e = c.Session.Expect.Tag(a.String("tag"));
        if (a.Has("equals"))
            e.ToEqual(a.Get("equals"), a.Double("tolerance") ?? 0);
        else if (a.Has("between") && a.Get("between") is List<object?> range && range.Count == 2
                 && ValueComparer.TryDouble(range[0], out var lo) && ValueComparer.TryDouble(range[1], out var hi))
            e.ToBeBetween(lo, hi);
        else
            throw new ScenarioFormatException("expect_tag needs 'equals' or 'between: [low, high]'.");

        var seen = await e.Within(a.Duration("within") ?? c.Session.Waits.Tags, ct).ConfigureAwait(false);
        c.AddEvidence(new Evidence("value", $"{a.String("tag")} = {seen}"));
    }

    private static async Task ExpectAlarm(StepContext c, StepArgs a, CancellationToken ct)
    {
        var alarm = a.Has("alarm")
            ? AlarmRef.Parse(a.String("alarm"))
            : new AlarmRef(a.String("source"), a.String("condition"));
        var status = ParseStatus(a.OptionalString("state") ?? "active_unacked");
        var e = c.Session.Expect.Alarm(alarm).ToBeIn(status);
        if (a.Double("priority") is { } p)
            e.WithPriority((int)p);
        var seen = await e.Within(a.Duration("within") ?? c.Session.Waits.Alarms, ct).ConfigureAwait(false);
        c.AddEvidence(new Evidence("alarm", $"{alarm}: {seen}"));
    }

    private static async Task ExpectSymbol(StepContext c, StepArgs a, CancellationToken ct)
    {
        var symbol = a.String("symbol");
        var seen = await c.Session.Expect.Symbol(symbol).ToBeIn(a.String("state"))
            .Within(a.Duration("within") ?? c.Session.Waits.Hmi, ct).ConfigureAwait(false);
        c.AddEvidence(new Evidence("symbol", $"{symbol}: {seen}"));
        await Capture(c, new StepArgs(new Dictionary<string, object?> { [StepArgs.ScalarKey] = $"{symbol}_{a.String("state")}" }), ct)
            .ConfigureAwait(false);
    }

    private static Task ExpectHistory(StepContext c, StepArgs a, CancellationToken ct)
    {
        var within = a.Duration("within") ?? c.Session.Waits.History;
        var poll = TimeSpan.FromSeconds(Math.Max(1, c.Session.Waits.PollInterval.TotalSeconds));

        if (a.Has("alarm"))
        {
            var alarm = AlarmRef.Parse(a.String("alarm"));
            var wanted = (a.Get("events") as List<object?> ?? []).Select(x => ParseEventKind(Convert.ToString(x)!)).ToArray();
            return Eventually.UntilAsync($"history for {alarm} to contain [{string.Join(", ", wanted)}]", async t =>
            {
                var events = await c.Session.History.QueryAlarmEventsAsync(alarm, c.ScenarioStartedAt, DateTimeOffset.UtcNow, t).ConfigureAwait(false);
                var kinds = events.Select(e => e.Kind).ToArray();
                return (wanted.All(kinds.Contains), $"[{string.Join(", ", kinds)}]");
            }, within, poll, ct);
        }

        // Tag history: every expected value appears, in order, within tolerance.
        var tag = a.String("tag");
        var expected = a.Get("contains") as List<object?> ?? throw new ScenarioFormatException("expect_history needs 'contains: [values]' or 'alarm' + 'events'.");
        var tolerance = a.Double("tolerance") ?? 0;
        return Eventually.UntilAsync($"history for {tag} to contain {ValueComparer.Format(string.Join(", ", expected))}", async t =>
        {
            var samples = await c.Session.History.QueryAsync(tag, c.ScenarioStartedAt, DateTimeOffset.UtcNow, RetrievalMode.Delta, null, t).ConfigureAwait(false);
            var i = 0;
            foreach (var s in samples)
                if (i < expected.Count && ValueComparer.AreEqual(s.Value, expected[i], tolerance))
                    i++;
            return (i == expected.Count, $"{samples.Count} samples, matched {i} of {expected.Count}");
        }, within, poll, ct);
    }

    private static async Task Capture(StepContext c, StepArgs a, CancellationToken ct)
    {
        var name = a.ScalarOr("name");
        var png = await c.Session.Hmi.CaptureAsync(null, ct).ConfigureAwait(false);
        if (png.Length == 0)
            return;
        Directory.CreateDirectory(c.EvidenceDirectory);
        var safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var path = Path.Combine(c.EvidenceDirectory, $"{c.ScenarioId}_{safe}_{DateTime.UtcNow:HHmmssfff}.png");
        await File.WriteAllBytesAsync(path, png, ct).ConfigureAwait(false);
        c.AddEvidence(new Evidence("screenshot", name, path));
    }

    public static AlarmStatus ParseStatus(string text) => text.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "inactive" or "normal" => AlarmStatus.Inactive,
        "activeunacked" or "active" => AlarmStatus.ActiveUnacked,
        "activeacked" or "acked" => AlarmStatus.ActiveAcked,
        "inactiveunacked" => AlarmStatus.InactiveUnacked,
        _ => throw new ScenarioFormatException($"Unknown alarm state '{text}'. Use inactive, active_unacked, active_acked or inactive_unacked."),
    };

    public static AlarmEventKind ParseEventKind(string text) => text.ToLowerInvariant() switch
    {
        "raise" or "raised" => AlarmEventKind.Raise,
        "ack" or "acked" => AlarmEventKind.Ack,
        "rtn" or "return" or "returntonormal" => AlarmEventKind.ReturnToNormal,
        _ => throw new ScenarioFormatException($"Unknown alarm event '{text}'. Use raise, ack or rtn."),
    };
}
