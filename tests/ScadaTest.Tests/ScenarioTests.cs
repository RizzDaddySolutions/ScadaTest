using System.Xml.Linq;
using ScadaTest.Adapters.InMemory;
using ScadaTest.Core;
using ScadaTest.Reporting;
using ScadaTest.Scenarios;

namespace ScadaTest.Tests;

public class ScenarioTests
{
    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "samples");
    private readonly StepRegistry _registry = StepRegistry.CreateDefault();

    private static Func<Action<WriteRecord>, ScadaSession> DemoSession(InMemoryPlant? plant = null)
    {
        plant ??= DemoPlant.Configure(new InMemoryPlant());
        return onWrite => new ScadaSession(plant, plant, plant, plant, plant, WriteGuard.AllowAll,
            new WaitDefaults { PollInterval = 20.Milliseconds() }, onWrite);
    }

    [Test]
    public void Loads_every_sample_scenario()
    {
        var scenarios = new ScenarioLoader(_registry).LoadDirectory(Samples);
        Assert.That(scenarios.Select(s => s.Id), Is.EquivalentTo(new[]
        {
            "ALM-LT301-HI", "PUMP-P101-START", "PUMP-P101-TRIP", "TREND-TT201-STEP",
        }));
    }

    [Test]
    public void Parses_step_kinds_arguments_and_scalar_steps()
    {
        var s = new ScenarioLoader(_registry).LoadFile(Path.Combine(Samples, "pump_p101_start.scada.yaml"));
        Assert.Multiple(() =>
        {
            Assert.That(s.Requires, Is.EqualTo(Capability.Tags | Capability.Hmi));
            Assert.That(s.Tags, Does.Contain("smoke"));
            Assert.That(s.Steps[0].Kind, Is.EqualTo("open_screen"));
            Assert.That(s.Steps[0].Args.ScalarOr("screen"), Is.EqualTo("Area200_Overview"));
            Assert.That(s.Steps[1].Args.Get("value"), Is.EqualTo(true));
            Assert.That(s.Steps[2].Args.Duration("within"), Is.EqualTo(TimeSpan.FromSeconds(10)));
        });
    }

    [Test]
    public void Rejects_unknown_steps_with_the_line_number()
    {
        var ex = Assert.Throws<ScenarioFormatException>(() => new ScenarioLoader(_registry).Parse("""
            id: X
            steps:
              - write: { tag: A, value: 1 }
              - frobnicate: {}
            """));
        Assert.That(ex!.Message, Does.Contain("Line 4").And.Contain("frobnicate"));
    }

    [TestCase("pump_p101_start.scada.yaml")]
    [TestCase("pump_p101_trip.scada.yaml")]
    [TestCase("alarm_lt301_hi.scada.yaml")]
    [TestCase("trend_tt201.scada.yaml")]
    public async Task Sample_scenarios_pass_against_the_demo_plant(string file)
    {
        var scenario = new ScenarioLoader(_registry).LoadFile(Path.Combine(Samples, file));
        var result = await new ScenarioExecutor(_registry).RunAsync(scenario, DemoSession(), Path.GetTempPath(), default);
        Assert.That(result.Outcome, Is.EqualTo(Outcome.Passed), result.Message);
    }

    [Test]
    public async Task A_failed_expectation_still_runs_teardown_and_restores_tags()
    {
        var plant = DemoPlant.Configure(new InMemoryPlant()).WithTag("P-101.Mode", "Manual");
        var scenario = new ScenarioLoader(_registry).Parse("""
            id: FAILS
            steps:
              - write: { tag: P-101.Mode, value: Auto }
              - expect_tag: { tag: P-101.Running, equals: true, within: 100ms }
              - write: { tag: P-101.Mode, value: Never }
            teardown:
              - write: { tag: TT-201.PV, value: 99 }
            """);

        var result = await new ScenarioExecutor(_registry).RunAsync(scenario, DemoSession(plant), Path.GetTempPath(), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(Outcome.Failed));
            Assert.That(result.Message, Does.Contain("line 4").And.Contain("P-101.Running = True"));
            Assert.That(result.Steps.Select(s => s.Phase), Is.EqualTo(new[] { "steps", "steps", "teardown" }));
            Assert.That(plant.Get("P-101.Mode"), Is.EqualTo("Manual"), "written tag restored");
            Assert.That(plant.Get("TT-201.PV"), Is.EqualTo(20.0), "teardown write restored too");
            Assert.That(result.Writes, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task Skips_when_the_environment_lacks_a_capability()
    {
        var scenario = new ScenarioLoader(_registry).LoadFile(Path.Combine(Samples, "pump_p101_start.scada.yaml"));
        var plant = new InMemoryPlant();
        var result = await new ScenarioExecutor(_registry).RunAsync(scenario, w => new ScadaSession(tags: plant, onWrite: w), Path.GetTempPath(), default);
        Assert.That(result.Outcome, Is.EqualTo(Outcome.Skipped));
        Assert.That(result.Message, Does.Contain("Hmi"));
    }

    [Test]
    public async Task JUnit_report_counts_outcomes()
    {
        var registry = _registry;
        var pass = new ScenarioLoader(registry).LoadFile(Path.Combine(Samples, "trend_tt201.scada.yaml"));
        var fail = new ScenarioLoader(registry).Parse("""
            id: F
            steps:
              - expect_tag: { tag: P-101.Running, equals: true, within: 50ms }
            """);
        var executor = new ScenarioExecutor(registry);
        var results = new[]
        {
            await executor.RunAsync(pass, DemoSession(), Path.GetTempPath(), default),
            await executor.RunAsync(fail, DemoSession(), Path.GetTempPath(), default),
        };

        var suite = JUnitReport.Build(results, "ScadaTest", "demo").Root!.Element("testsuite")!;
        Assert.Multiple(() =>
        {
            Assert.That((int)suite.Attribute("tests")!, Is.EqualTo(2));
            Assert.That((int)suite.Attribute("failures")!, Is.EqualTo(1));
            Assert.That(suite.Elements("testcase").Last().Element("failure"), Is.Not.Null);
        });
        Assert.That(JsonReport.Build(results, "demo"), Does.Contain("\"outcome\": \"failed\""));
    }
}
