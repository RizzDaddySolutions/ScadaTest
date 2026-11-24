using ScadaTest.Adapters.InMemory;
using ScadaTest.Core;

namespace ScadaTest.Tests;

public class CoreTests
{
    [TestCase("500ms", 500)]
    [TestCase("10s", 10_000)]
    [TestCase("1.5m", 90_000)]
    [TestCase("1h", 3_600_000)]
    public void Duration_parses_yaml_forms(string text, double ms) =>
        Assert.That(Duration.Parse(text).TotalMilliseconds, Is.EqualTo(ms));

    [Test]
    public void Duration_rejects_bare_numbers() =>
        Assert.Throws<FormatException>(() => Duration.Parse("10"));

    [TestCase(1, true, true)]
    [TestCase(0, false, true)]
    [TestCase("True", true, true)]
    [TestCase(59.8, 60.0, false)]
    [TestCase(60L, 60.0, true)]
    [TestCase("Auto", "auto", true)]
    public void ValueComparer_compares_loosely(object actual, object expected, bool equal) =>
        Assert.That(ValueComparer.AreEqual(actual, expected), Is.EqualTo(equal));

    [Test]
    public void ValueComparer_honours_tolerance() =>
        Assert.That(ValueComparer.AreEqual(59.8, 60.0, tolerance: 0.5), Is.True);

    [Test]
    public void TagMap_prefers_explicit_entries_then_templates()
    {
        var map = new TagMap()
            .SetTemplate("galaxy", "ns=2;s=Galaxy.{name:nodash}")
            .Add("LT-301.PV", "galaxy", "ns=2;s=Special.LT301");

        Assert.Multiple(() =>
        {
            Assert.That(map.Resolve("P-101.Running", "galaxy"), Is.EqualTo("ns=2;s=Galaxy.P101.Running"));
            Assert.That(map.Resolve("LT-301.PV", "galaxy"), Is.EqualTo("ns=2;s=Special.LT301"));
            Assert.That(map.Resolve("P-101.Running", "other"), Is.EqualTo("P-101.Running"));
        });
    }

    [Test]
    public void WriteGuard_blocks_disabled_writes_and_unlisted_tags()
    {
        Assert.Throws<WriteNotAllowedException>(() => WriteGuard.ReadOnly.EnsureWritable("P-101.StartCmd"));
        var guard = new WriteGuard(true, ["P-*"]);
        Assert.DoesNotThrow(() => guard.EnsureWritable("P-101.StartCmd"));
        Assert.Throws<WriteNotAllowedException>(() => guard.EnsureWritable("LT-301.PV"));
    }

    [Test]
    public async Task Tracking_client_restores_original_values_in_reverse_order()
    {
        var plant = new InMemoryPlant().WithTag("A", 1).WithTag("B", "x");
        var tracked = new TrackingTagClient(plant, WriteGuard.AllowAll);

        await tracked.WriteAsync("A", 2, default);
        await tracked.WriteAsync("A", 3, default);
        await tracked.WriteAsync("B", "y", default);
        var errors = await tracked.RestoreAsync(default);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(plant.Get("A"), Is.EqualTo(1));
            Assert.That(plant.Get("B"), Is.EqualTo("x"));
        });
    }

    [Test]
    public async Task Fluent_expectation_waits_for_a_delayed_value()
    {
        var plant = new InMemoryPlant()
            .WithTag("P-101.Running", false)
            .When("P-101.StartCmd", true, "P-101.Running", true, delay: TimeSpan.FromMilliseconds(200));
        var s = new ScadaSession(tags: plant, guard: WriteGuard.AllowAll, waits: new WaitDefaults { PollInterval = 20.Milliseconds() });

        await s.Tags.WriteAsync("P-101.StartCmd", true, default);
        await s.Expect.Tag("P-101.Running").ToEqual(true).Within(2.Seconds());
    }

    [Test]
    public async Task Fluent_expectation_reports_last_value_on_timeout()
    {
        var plant = new InMemoryPlant().WithTag("P-101.Running", false);
        var s = new ScadaSession(tags: plant, waits: new WaitDefaults { PollInterval = 20.Milliseconds() });

        var ex = await Assert.ThrowsAsync<ExpectationFailedException>(() =>
            s.Expect.Tag("P-101.Running").ToEqual(true).Within(150.Milliseconds()));
        Assert.That(ex!.Message, Does.Contain("last saw False"));
    }

    [Test]
    public void Session_throws_a_clear_error_for_missing_capabilities()
    {
        var s = new ScadaSession(tags: new InMemoryPlant());
        Assert.That(s.MissingFrom(Capability.Tags | Capability.Hmi), Is.EqualTo(Capability.Hmi));
        Assert.Throws<CapabilityNotAvailableException>(() => _ = s.Hmi);
    }
}
