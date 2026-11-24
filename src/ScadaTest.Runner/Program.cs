using ScadaTest.Reporting;
using ScadaTest.Runner;
using ScadaTest.Scenarios;

return await Cli.RunAsync(args, Console.Out, Console.Error);

namespace ScadaTest.Runner
{
    public static class Cli
    {
        private const string Usage = """
            Usage:
              scadatest run <path> --env <name> [--config environments.yaml] [--filter <tag>]...
                                   [--junit results.xml] [--json results.json]
              scadatest validate <path>
              scadatest list <path> [--filter <tag>]...

            <path> is a *.scada.yaml file or a folder searched recursively.
            Exit codes: 0 all passed, 1 failures or errors, 2 usage or configuration error.
            """;

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
        {
            if (args.Length < 2 || args[0] is "-h" or "--help")
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            var command = args[0];
            var path = Path.GetFullPath(args[1]);
            var options = ParseOptions(args.Skip(2).ToArray());
            var registry = StepRegistry.CreateDefault();
            var loader = new ScenarioLoader(registry);

            IReadOnlyList<ScenarioDefinition> scenarios;
            try
            {
                scenarios = File.Exists(path) ? [loader.LoadFile(path)] : loader.LoadDirectory(path);
            }
            catch (Exception ex) when (ex is ScenarioFormatException or IOException or YamlDotNet.Core.YamlException)
            {
                error.WriteLine($"error: {ex.Message}");
                return 2;
            }

            var filters = options.GetValueOrDefault("filter") ?? [];
            scenarios = scenarios.Where(s => filters.Count == 0 || filters.Any(f => s.Tags.Contains(f, StringComparer.OrdinalIgnoreCase))).ToArray();

            switch (command)
            {
                case "validate":
                    output.WriteLine($"{scenarios.Count} scenarios are valid.");
                    return 0;
                case "list":
                    foreach (var s in scenarios)
                        output.WriteLine($"{s.Id,-28} needs {registry.RequiredBy(s),-32} [{string.Join(", ", s.Tags)}]");
                    return 0;
                case "run":
                    return await RunScenariosAsync(scenarios, registry, options, path, output, error);
                default:
                    error.WriteLine($"Unknown command '{command}'.");
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        private static async Task<int> RunScenariosAsync(
            IReadOnlyList<ScenarioDefinition> scenarios, StepRegistry registry,
            Dictionary<string, List<string>> options, string path, TextWriter output, TextWriter error)
        {
            var envName = options.GetValueOrDefault("env")?.LastOrDefault();
            if (envName is null)
            {
                error.WriteLine("error: --env is required for 'run'.");
                return 2;
            }

            var configPath = Path.GetFullPath(options.GetValueOrDefault("config")?.LastOrDefault()
                ?? Path.Combine(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, "environments.yaml"));
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            ConnectedEnvironment environment;
            try
            {
                var file = EnvironmentsFile.Load(configPath);
                environment = await ConnectedEnvironment.ConnectAsync(file, envName, Path.GetDirectoryName(configPath)!, AdapterCatalog.Discover(), cts.Token);
            }
            catch (Exception ex)
            {
                error.WriteLine($"error: {ex.Message}");
                return 2;
            }

            var results = new List<ScenarioResult>();
            await using (environment)
            {
                var executor = new ScenarioExecutor(registry);
                var evidenceDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, environment.Config.EvidenceDir));
                output.WriteLine($"Running {scenarios.Count} scenarios against '{envName}'.");
                foreach (var scenario in scenarios)
                {
                    if (cts.IsCancellationRequested)
                        break;
                    var result = await executor.RunAsync(scenario, environment.CreateSession, evidenceDir, cts.Token);
                    results.Add(result);
                    ConsoleReport.WriteScenario(output, result);
                }
            }

            ConsoleReport.WriteSummary(output, results);
            if (options.GetValueOrDefault("junit")?.LastOrDefault() is { } junit)
                JUnitReport.Build(results, "ScadaTest", envName).Save(junit);
            if (options.GetValueOrDefault("json")?.LastOrDefault() is { } json)
                await File.WriteAllTextAsync(json, JsonReport.Build(results, envName));

            return results.All(r => r.Outcome is Outcome.Passed or Outcome.Skipped) ? 0 : 1;
        }

        internal static Dictionary<string, List<string>> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    continue;
                var key = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                if (!options.TryGetValue(key, out var list))
                    options[key] = list = [];
                list.Add(value);
            }
            return options;
        }
    }
}
