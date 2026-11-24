namespace ScadaTest.Core;

/// <summary>
/// Fluent expectations for C# tests:
/// <code>await s.Expect.Tag("P-101.Running").ToEqual(true).Within(10.Seconds());</code>
/// </summary>
public sealed class Expect(IScadaSession session)
{
    public TagExpectation Tag(TagRef tag) => new(session, tag);

    public AlarmExpectation Alarm(string alarm) => new(session, AlarmRef.Parse(alarm));

    public AlarmExpectation Alarm(AlarmRef alarm) => new(session, alarm);

    public SymbolExpectation Symbol(SymbolRef symbol) => new(session, symbol);
}

public sealed class TagExpectation(IScadaSession session, TagRef tag)
{
    private Func<TagValue, bool> _predicate = _ => true;
    private string _description = $"{tag} to be readable";

    public TagExpectation ToEqual(object? expected, double tolerance = 0)
    {
        _predicate = v => v.Quality == Quality.Good && ValueComparer.AreEqual(v.Value, expected, tolerance);
        _description = tolerance > 0
            ? $"{tag} = {ValueComparer.Format(expected)} ±{tolerance}"
            : $"{tag} = {ValueComparer.Format(expected)}";
        return this;
    }

    public TagExpectation ToBeBetween(double low, double high)
    {
        _predicate = v => v.Quality == Quality.Good && ValueComparer.TryDouble(v.Value, out var d) && d >= low && d <= high;
        _description = $"{tag} between {low} and {high}";
        return this;
    }

    public TagExpectation ToSatisfy(Func<TagValue, bool> predicate, string description)
    {
        _predicate = predicate;
        _description = $"{tag} {description}";
        return this;
    }

    public Task<string> Within(TimeSpan timeout, CancellationToken ct = default) =>
        Eventually.UntilAsync(_description, async c =>
        {
            var v = await session.Tags.ReadAsync(tag, c).ConfigureAwait(false);
            return (_predicate(v), $"{ValueComparer.Format(v.Value)} ({v.Quality})");
        }, timeout, session.Waits.PollInterval, ct);
}

public sealed class AlarmExpectation(IScadaSession session, AlarmRef alarm)
{
    private AlarmStatus _status = AlarmStatus.ActiveUnacked;
    private int? _priority;

    public AlarmExpectation ToBeIn(AlarmStatus status)
    {
        _status = status;
        return this;
    }

    public AlarmExpectation WithPriority(int priority)
    {
        _priority = priority;
        return this;
    }

    public Task<string> Within(TimeSpan timeout, CancellationToken ct = default) =>
        Eventually.UntilAsync($"alarm {alarm} {_status}" + (_priority is null ? "" : $" priority {_priority}"), async c =>
        {
            var active = await session.Alarms.GetActiveAsync(new AlarmFilter(alarm.Source, alarm.Condition), c).ConfigureAwait(false);
            var match = active.FirstOrDefault();
            var status = match?.Status ?? AlarmStatus.Inactive;
            var passed = status == _status && (_priority is null || match?.Priority == _priority);
            return (passed, match is null ? "no active alarm" : $"{match.Status} priority {match.Priority}");
        }, timeout, session.Waits.PollInterval, ct);
}

public sealed class SymbolExpectation(IScadaSession session, SymbolRef symbol)
{
    private string _state = "";

    public SymbolExpectation ToBeIn(string state)
    {
        _state = state;
        return this;
    }

    public Task<string> Within(TimeSpan timeout, CancellationToken ct = default) =>
        Eventually.UntilAsync($"symbol {symbol} in state '{_state}'", async c =>
        {
            var s = await session.Hmi.GetSymbolStateAsync(symbol, c).ConfigureAwait(false);
            return (string.Equals(s.State, _state, StringComparison.OrdinalIgnoreCase),
                    s.Detail is null ? s.State : $"{s.State} ({s.Detail})");
        }, timeout, session.Waits.PollInterval, ct);
}
