namespace ScadaTest.Core;

/// <summary>What a test sees: one client per capability, each bound to whichever adapter provides it.</summary>
public interface IScadaSession
{
    ITagClient Tags { get; }
    IAlarmClient Alarms { get; }
    IHistoryClient History { get; }
    IHmiDriver Hmi { get; }
    ISimulatorControl Simulator { get; }
    Capability Available { get; }
    Expect Expect { get; }
    WaitDefaults Waits { get; }
}

public sealed record WaitDefaults
{
    public TimeSpan Tags { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Alarms { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Hmi { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan History { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

public sealed class ScadaSession : IScadaSession
{
    private readonly ITagClient? _tags;
    private readonly IAlarmClient? _alarms;
    private readonly IHistoryClient? _history;
    private readonly IHmiDriver? _hmi;
    private readonly ISimulatorControl? _simulator;

    public ScadaSession(
        ITagClient? tags = null,
        IAlarmClient? alarms = null,
        IHistoryClient? history = null,
        IHmiDriver? hmi = null,
        ISimulatorControl? simulator = null,
        WriteGuard? guard = null,
        WaitDefaults? waits = null,
        Action<WriteRecord>? onWrite = null)
    {
        TrackedTags = tags is null ? null : new TrackingTagClient(tags, guard ?? WriteGuard.ReadOnly, onWrite);
        _tags = TrackedTags;
        _alarms = alarms;
        _history = history;
        _hmi = hmi;
        _simulator = simulator;
        Waits = waits ?? new WaitDefaults();
        Expect = new Expect(this);
        Available =
            (tags is null ? 0 : Capability.Tags) |
            (alarms is null ? 0 : Capability.Alarms) |
            (history is null ? 0 : Capability.History) |
            (hmi is null ? 0 : Capability.Hmi) |
            (simulator is null ? 0 : Capability.Simulator);
    }

    /// <summary>The tag client with write tracking, used by the runner to restore state in teardown.</summary>
    public TrackingTagClient? TrackedTags { get; }

    public ITagClient Tags => _tags ?? throw Missing(Capability.Tags);
    public IAlarmClient Alarms => _alarms ?? throw Missing(Capability.Alarms);
    public IHistoryClient History => _history ?? throw Missing(Capability.History);
    public IHmiDriver Hmi => _hmi ?? throw Missing(Capability.Hmi);
    public ISimulatorControl Simulator => _simulator ?? throw Missing(Capability.Simulator);
    public Capability Available { get; }
    public Expect Expect { get; }
    public WaitDefaults Waits { get; }

    public Capability MissingFrom(Capability required) => required & ~Available;

    private static CapabilityNotAvailableException Missing(Capability c) =>
        new($"No adapter in this environment provides the '{c}' capability.");
}

public sealed class CapabilityNotAvailableException(string message) : InvalidOperationException(message);
