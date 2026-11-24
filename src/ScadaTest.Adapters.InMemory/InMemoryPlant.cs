using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ScadaTest.Core;

namespace ScadaTest.Adapters.InMemory;

/// <summary>
/// A small in-memory plant: tags, limit alarms, recorded history and a scriptable simulator.
/// Rules react to writes, so a test can model "StartCmd true makes Running true".
/// Use it to develop tests and step handlers without a SCADA system.
/// </summary>
[ScadaAdapter("inmemory")]
public sealed class InMemoryPlant : IScadaAdapter, ITagClient, IAlarmClient, IHistoryClient, ISimulatorControl, IHmiDriver
{
    private readonly ConcurrentDictionary<string, TagValue> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AlarmState> _alarms = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Tag, HistorySample Sample)> _history = [];
    private readonly List<AlarmEvent> _alarmEvents = [];
    private readonly List<LimitAlarm> _limits = [];
    private readonly List<Func<InMemoryPlant, TagRef, object?, Task>> _rules = [];
    private readonly Dictionary<string, Func<InMemoryPlant, IReadOnlyDictionary<string, object?>?, Task>> _scenarios = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<InMemoryPlant, string, Task>> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<InMemoryPlant, SymbolState>> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _initial = new(StringComparer.OrdinalIgnoreCase);
    private string _scenarioState = "idle";

    public string Id => "inmemory";

    public Capability Capabilities => Capability.Tags | Capability.Alarms | Capability.History | Capability.Simulator | Capability.Hmi;

    public string? CurrentScreen { get; private set; }

    public Task ConnectAsync(AdapterConfig config, CancellationToken ct)
    {
        if (string.Equals(config.Get("model"), "demo", StringComparison.OrdinalIgnoreCase))
            DemoPlant.Configure(this);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- model setup -------------------------------------------------------

    public InMemoryPlant WithTag(string name, object? value)
    {
        _initial[name] = value;
        Set(name, value);
        return this;
    }

    public InMemoryPlant WithHiAlarm(string source, string tag, double limit, int priority, string? message = null)
    {
        _limits.Add(new LimitAlarm(new AlarmRef(source, "Hi"), tag, limit, priority, message ?? $"{source} high"));
        return this;
    }

    /// <summary>Adds a rule that runs after every write.</summary>
    public InMemoryPlant OnWrite(Func<InMemoryPlant, TagRef, object?, Task> rule)
    {
        _rules.Add(rule);
        return this;
    }

    /// <summary>Shortcut: writing <paramref name="trigger"/> with <paramref name="value"/> sets <paramref name="target"/>.</summary>
    public InMemoryPlant When(string trigger, object? value, string target, object? result, TimeSpan? delay = null) =>
        OnWrite(async (plant, tag, written) =>
        {
            if (!string.Equals(tag.Name, trigger, StringComparison.OrdinalIgnoreCase) || !ValueComparer.AreEqual(written, value))
                return;
            if (delay is { } d)
                _ = Task.Delay(d).ContinueWith(_ => plant.Set(target, result), TaskScheduler.Default);
            else
                plant.Set(target, result);
            await Task.CompletedTask;
        });

    public InMemoryPlant WithScenario(string name, Func<InMemoryPlant, IReadOnlyDictionary<string, object?>?, Task> load)
    {
        _scenarios[name] = load;
        return this;
    }

    public InMemoryPlant WithFault(string fault, Func<InMemoryPlant, string, Task> apply)
    {
        _faults[fault] = apply;
        return this;
    }

    public InMemoryPlant WithSymbol(string symbol, Func<InMemoryPlant, SymbolState> state)
    {
        _symbols[symbol] = state;
        return this;
    }

    public object? Get(string tag) => _tags.TryGetValue(tag, out var v) ? v.Value : null;

    /// <summary>Sets a tag directly (as the process would), records history and evaluates alarms.</summary>
    public void Set(string tag, object? value)
    {
        var now = DateTimeOffset.UtcNow;
        _tags[tag] = new TagValue(tag, value, Quality.Good, now);
        lock (_history) _history.Add((tag, new HistorySample(now, value, Quality.Good)));
        EvaluateAlarms(tag, value, now);
    }

    // ---- ITagClient ----------------------------------------------------------

    public Task<TagValue> ReadAsync(TagRef tag, CancellationToken ct) =>
        Task.FromResult(_tags.TryGetValue(tag.Name, out var v) ? v : new TagValue(tag, null, Quality.Bad, DateTimeOffset.UtcNow));

    public async Task WriteAsync(TagRef tag, object? value, CancellationToken ct)
    {
        Set(tag.Name, value);
        foreach (var rule in _rules.ToArray())
            await rule(this, tag, value).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TagValue> SubscribeAsync(TagRef tag, [EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset last = default;
        while (!ct.IsCancellationRequested)
        {
            var v = await ReadAsync(tag, ct).ConfigureAwait(false);
            if (v.Timestamp != last)
            {
                last = v.Timestamp;
                yield return v;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // ---- IAlarmClient --------------------------------------------------------

    public Task<IReadOnlyList<AlarmState>> GetActiveAsync(AlarmFilter filter, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AlarmState>>(_alarms.Values
            .Where(a => a.Status != AlarmStatus.Inactive && filter.Matches(a.Alarm))
            .ToArray());

    public Task AcknowledgeAsync(AlarmRef alarm, string comment, CancellationToken ct)
    {
        if (_alarms.TryGetValue(alarm.ToString(), out var state))
        {
            var now = DateTimeOffset.UtcNow;
            var next = state.Status switch
            {
                AlarmStatus.ActiveUnacked => AlarmStatus.ActiveAcked,
                AlarmStatus.InactiveUnacked => AlarmStatus.Inactive,
                _ => state.Status,
            };
            _alarms[alarm.ToString()] = state with { Status = next, Timestamp = now };
            lock (_alarmEvents) _alarmEvents.Add(new AlarmEvent(alarm, AlarmEventKind.Ack, now, comment));
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AlarmEvent> SubscribeAsync(AlarmFilter filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = 0;
        while (!ct.IsCancellationRequested)
        {
            AlarmEvent[] fresh;
            lock (_alarmEvents)
            {
                fresh = _alarmEvents.Skip(seen).ToArray();
                seen = _alarmEvents.Count;
            }
            foreach (var e in fresh.Where(e => filter.Matches(e.Alarm)))
                yield return e;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // ---- IHistoryClient ------------------------------------------------------

    public Task<IReadOnlyList<HistorySample>> QueryAsync(TagRef tag, DateTimeOffset from, DateTimeOffset to, RetrievalMode mode, TimeSpan? interval, CancellationToken ct)
    {
        lock (_history)
        {
            return Task.FromResult<IReadOnlyList<HistorySample>>(_history
                .Where(h => string.Equals(h.Tag, tag.Name, StringComparison.OrdinalIgnoreCase) && h.Sample.Timestamp >= from && h.Sample.Timestamp <= to)
                .Select(h => h.Sample)
                .ToArray());
        }
    }

    public Task<IReadOnlyList<AlarmEvent>> QueryAlarmEventsAsync(AlarmRef alarm, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        lock (_alarmEvents)
        {
            return Task.FromResult<IReadOnlyList<AlarmEvent>>(_alarmEvents
                .Where(e => e.Alarm == alarm && e.Timestamp >= from && e.Timestamp <= to)
                .ToArray());
        }
    }

    // ---- ISimulatorControl ---------------------------------------------------

    public async Task LoadScenarioAsync(string scenario, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        if (!_scenarios.TryGetValue(scenario, out var load))
            throw new InvalidOperationException($"Unknown simulator scenario '{scenario}'.");
        await load(this, parameters).ConfigureAwait(false);
        _scenarioState = $"running:{scenario}";
    }

    public Task ResetAsync(CancellationToken ct)
    {
        foreach (var (tag, value) in _initial)
            Set(tag, value);
        foreach (var key in _alarms.Keys)
            _alarms[key] = _alarms[key] with { Status = AlarmStatus.Inactive };
        _scenarioState = "idle";
        return Task.CompletedTask;
    }

    public Task SetInputAsync(TagRef input, object? value, CancellationToken ct)
    {
        Set(input.Name, value);
        return Task.CompletedTask;
    }

    public Task InjectFaultAsync(TagRef device, string fault, CancellationToken ct) =>
        _faults.TryGetValue(fault, out var apply)
            ? apply(this, device.Name)
            : throw new InvalidOperationException($"Unknown fault '{fault}'.");

    public Task<string> GetScenarioStateAsync(CancellationToken ct) => Task.FromResult(_scenarioState);

    // ---- IHmiDriver (state derived from the model, for runner tests) ---------

    public Task OpenScreenAsync(string screen, CancellationToken ct)
    {
        CurrentScreen = screen;
        return Task.CompletedTask;
    }

    public Task<SymbolState> GetSymbolStateAsync(SymbolRef symbol, CancellationToken ct) =>
        Task.FromResult(_symbols.TryGetValue(symbol.Name, out var f) ? f(this) : new SymbolState("unknown"));

    public Task<byte[]> CaptureAsync(Region? region, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());

    public Task ClickAsync(SymbolRef symbol, CancellationToken ct) => Task.CompletedTask;

    // ---- internals -----------------------------------------------------------

    private void EvaluateAlarms(string tag, object? value, DateTimeOffset now)
    {
        foreach (var limit in _limits.Where(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase)))
        {
            if (!ValueComparer.TryDouble(value, out var v))
                continue;
            var key = limit.Alarm.ToString();
            var current = _alarms.TryGetValue(key, out var s) ? s.Status : AlarmStatus.Inactive;
            var inAlarm = v > limit.Limit;
            AlarmStatus next = (inAlarm, current) switch
            {
                (true, AlarmStatus.Inactive) => AlarmStatus.ActiveUnacked,
                (true, AlarmStatus.InactiveUnacked) => AlarmStatus.ActiveUnacked,
                (false, AlarmStatus.ActiveUnacked) => AlarmStatus.InactiveUnacked,
                (false, AlarmStatus.ActiveAcked) => AlarmStatus.Inactive,
                _ => current,
            };
            if (next == current)
                continue;
            _alarms[key] = new AlarmState(limit.Alarm, next, limit.Priority, limit.Message, now);
            var kind = inAlarm ? AlarmEventKind.Raise : AlarmEventKind.ReturnToNormal;
            lock (_alarmEvents) _alarmEvents.Add(new AlarmEvent(limit.Alarm, kind, now));
        }
    }

    private sealed record LimitAlarm(AlarmRef Alarm, string Tag, double Limit, int Priority, string Message);
}
