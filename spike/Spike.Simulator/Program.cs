using System.Diagnostics;
using Opc.Ua;
using Opc.Ua.Client;
using ScadaTest.Adapters.OpcUa;
using ScadaTest.Core;
using ScadaTest.Spike;

// Spike 4: the custom OPC UA simulator's scenario/reset methods.
SpikeArgs a;
try { a = new SpikeArgs(args); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

if (a.Command is "help" or "-h")
{
    Console.WriteLine("""
        spike-sim <command> --endpoint opc.tcp://host:4841 [options]

          browse --object NODEID             List the simulator object's methods (with argument names and types) and variables
          call   --object NODEID --method NAME|NODEID [--arg V ...]
                                             Call one method directly and print its output arguments
          check  --scenario NAME [--param k=v ...] [--fault-device P-101 --fault trip] [--watch NODEID]
                                             Reset, load a scenario, inject a fault and reset again through OpcUaAdapter,
                                             reading the scenario state (and an optional watched tag) after each step

        check uses the NodeIds from environments.yaml unless you override them:
          --object ns=2;s=Simulator  --load ns=2;s=Simulator.LoadScenario  --reset ns=2;s=Simulator.Reset
          --fault ns=2;s=Simulator.InjectFault  --state ns=2;s=Simulator.State   (use --fault-node for the method)
        Connection: [--secure] [--auto-accept] [--username U --password P]
        """);
    return 0;
}

using var report = new Report($"sim-{a.Command}", args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

try
{
    switch (a.Command)
    {
        case "browse": await BrowseAsync(); break;
        case "call": await CallAsync(); break;
        case "check": await CheckAsync(); break;
        default: report.Fail($"Unknown command '{a.Command}'. Run with no arguments for help."); break;
    }
}
catch (Exception ex)
{
    report.Exception(ex);
}
return report.Finish();

// ---------------------------------------------------------------------------------------------------------------

Task<OpcUaRaw> RawAsync() => OpcUaRaw.ConnectAsync(a.Require("endpoint"), a.Flag("secure"), a.Flag("auto-accept"),
    a.Get("username"), a.Get("password"), a.Get("pki", OpcUaRaw.DefaultPkiPath), ct);

async Task BrowseAsync()
{
    await using var raw = await RawAsync();
    report.Pass($"Connected: {raw.Describe()}");
    var obj = NodeId.Parse(a.Get("object", "ns=2;s=Simulator"));
    var refs = await raw.BrowseAsync(obj, ct);
    var methods = 0;
    foreach (var r in refs)
    {
        var id = raw.ToNodeId(r.NodeId);
        if (r.NodeClass == NodeClass.Method)
        {
            methods++;
            var inputs = await raw.GetMethodArgumentsAsync(id, BrowseNames.InputArguments, ct);
            var outputs = await raw.GetMethodArgumentsAsync(id, BrowseNames.OutputArguments, ct);
            report.Info($"method   {r.BrowseName.Name}({string.Join(", ", inputs.Select(OpcUaRaw.DescribeArgument))})" +
                        $"{(outputs.Count > 0 ? " -> " + string.Join(", ", outputs.Select(OpcUaRaw.DescribeArgument)) : string.Empty)}  [{id}]");
        }
        else if (r.NodeClass == NodeClass.Variable)
        {
            var v = await raw.Session.ReadValueAsync(id, ct);
            report.Info($"variable {r.BrowseName.Name} = {OpcUaRaw.Format(v.Value)}  [{id}]");
        }
        else
        {
            report.Info($"{r.NodeClass,-8} {r.BrowseName.Name}  [{id}]");
        }
    }
    report.Check(methods > 0, $"{obj} has {methods} methods. Put their NodeIds in environments.yaml under the sim adapter.");
}

async Task CallAsync()
{
    await using var raw = await RawAsync();
    report.Info($"Connected: {raw.Describe()}");
    var obj = NodeId.Parse(a.Get("object", "ns=2;s=Simulator"));
    var method = await ResolveMethodAsync(raw, obj, a.Require("method"));
    var inputs = await raw.GetMethodArgumentsAsync(method, BrowseNames.InputArguments, ct);
    var texts = a.GetAll("arg");
    if (texts.Count != inputs.Count)
        report.Warn($"The method declares {inputs.Count} input arguments ({string.Join(", ", inputs.Select(OpcUaRaw.DescribeArgument))}) but {texts.Count} --arg were given.");
    var values = texts.Select((t, i) => i < inputs.Count ? OpcUaRaw.ConvertArgument(t, inputs[i]) : t).ToArray();

    var sw = Stopwatch.StartNew();
    var outputs = await raw.Session.CallAsync(obj, method, ct, values);
    report.Pass($"Called {method} with ({string.Join(", ", values.Select(OpcUaRaw.Format))}) in {sw.ElapsedMilliseconds} ms");
    for (var i = 0; i < outputs.Count; i++)
        report.Info($"  output {i}: {OpcUaRaw.Format(outputs[i])}");
}

async Task<NodeId> ResolveMethodAsync(OpcUaRaw raw, NodeId obj, string nameOrId)
{
    if (nameOrId.Contains('=', StringComparison.Ordinal))
        return NodeId.Parse(nameOrId);
    var match = (await raw.BrowseAsync(obj, ct))
        .FirstOrDefault(r => r.NodeClass == NodeClass.Method && string.Equals(r.BrowseName.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"{obj} has no method named '{nameOrId}'. Run 'browse' to list them.");
    return raw.ToNodeId(match.NodeId);
}

async Task CheckAsync()
{
    var settings = new Dictionary<string, string>
    {
        ["endpoint"] = a.Require("endpoint"),
        ["useSecurity"] = a.Flag("secure").ToString(),
        ["autoAcceptCertificates"] = a.Flag("auto-accept").ToString(),
        ["pkiPath"] = a.Get("pki", OpcUaRaw.DefaultPkiPath),
        ["simulator.object"] = a.Get("object", "ns=2;s=Simulator"),
        ["simulator.loadScenario"] = a.Get("load", "ns=2;s=Simulator.LoadScenario"),
        ["simulator.reset"] = a.Get("reset", "ns=2;s=Simulator.Reset"),
        ["simulator.injectFault"] = a.Get("fault-node", "ns=2;s=Simulator.InjectFault"),
        ["simulator.state"] = a.Get("state", "ns=2;s=Simulator.State"),
    };
    if (a.Get("username") is { } u) { settings["username"] = u; settings["password"] = a.Get("password", ""); }

    await using var adapter = new OpcUaAdapter();
    await adapter.ConnectAsync(new AdapterConfig("sim", settings, TagMap.Empty), ct);
    report.Check(adapter.Capabilities.HasFlag(Capability.Simulator), $"OpcUaAdapter connected to {settings["endpoint"]} with capabilities {adapter.Capabilities}");
    ISimulatorControl sim = adapter;
    var watch = a.Get("watch");

    async Task ShowAsync(string after)
    {
        var state = await report.StepAsync("Read scenario state", () => sim.GetScenarioStateAsync(ct));
        var line = $"After {after}: state '{state}'";
        if (watch is not null)
        {
            var v = await adapter.ReadAsync(watch, ct);
            line += $", {watch} = {OpcUaRaw.Format(v.Value)}";
        }
        report.Info(line);
    }

    async Task TimedAsync(string what, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        if (await report.StepAsync(what, action))
            report.Pass($"{what} returned in {sw.ElapsedMilliseconds} ms");
        await Task.Delay(a.Time("settle", "1s"), ct);
        await ShowAsync(what);
    }

    await ShowAsync("connect");
    await TimedAsync("Reset", () => sim.ResetAsync(ct));

    var parameters = a.GetAll("param").Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => (object?)(p.Length > 1 ? p[1] : null));
    var scenario = a.Require("scenario");
    await TimedAsync($"LoadScenario({scenario}{(parameters.Count > 0 ? ", " + string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}")) : string.Empty)})",
        () => sim.LoadScenarioAsync(scenario, parameters, ct));

    if (a.Get("fault-device") is { } device)
    {
        var fault = a.Get("fault", "trip");
        await TimedAsync($"InjectFault({device}, {fault})", () => sim.InjectFaultAsync(device, fault, ct));
    }

    await TimedAsync("Reset", () => sim.ResetAsync(ct));
    report.Info("Check above that the state (and watched tag) changed after LoadScenario/InjectFault and went back after the final Reset.");
}
