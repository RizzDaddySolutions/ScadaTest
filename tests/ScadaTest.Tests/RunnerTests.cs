using ScadaTest.Runner;

namespace ScadaTest.Tests;

public class RunnerTests
{
    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "samples");

    [Test]
    public async Task Cli_runs_the_samples_against_the_demo_environment()
    {
        var junit = Path.Combine(Path.GetTempPath(), $"scadatest-{Guid.NewGuid():N}.xml");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Cli.RunAsync(["run", Samples, "--env", "demo", "--junit", junit], output, error);

        Assert.That(exit, Is.EqualTo(0), output + error.ToString());
        Assert.That(output.ToString(), Does.Contain("4 passed"));
        Assert.That(File.Exists(junit), Is.True);
    }

    [Test]
    public async Task Cli_filters_by_tag()
    {
        var output = new StringWriter();
        var exit = await Cli.RunAsync(["list", Samples, "--filter", "smoke"], output, TextWriter.Null);
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(output.ToString(), Does.Contain("PUMP-P101-START").And.Contain("ALM-LT301-HI").And.Not.Contain("TRIP"));
    }

    [Test]
    public async Task Refuses_environments_that_point_at_production()
    {
        var file = EnvironmentsFile.Load(Path.Combine(Samples, "environments.yaml"));
        file.Environments["test-vm"].Adapters["historian"].Settings["connectionString"] = "Server=PROD-HIST01;Database=Runtime";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectedEnvironment.ConnectAsync(file, "test-vm", Samples, AdapterCatalog.Discover(), default));
        Assert.That(ex!.Message, Does.Contain("production host"));
    }

    [Test]
    public void Catalog_discovers_built_in_adapters() =>
        Assert.That(AdapterCatalog.Discover().Ids, Is.SupersetOf(new[] { "inmemory", "opcua", "aveva-historian" }));
}
