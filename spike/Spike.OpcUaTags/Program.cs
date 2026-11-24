using System.Diagnostics;
using Opc.Ua;
using ScadaTest.Adapters.OpcUa;
using ScadaTest.Core;
using ScadaTest.Spike;

// Spike 2a: Galaxy attribute read/write over OPC UA (OI Gateway or the System Platform OPC UA server).
SpikeArgs a;
try { a = new SpikeArgs(args); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

if (a.Command is "help" or "-h")
{
    Console.WriteLine("""
        spike-opcua <command> --endpoint opc.tcp://host:port [options]

          connect                       Connect and print the endpoint, security and namespaces
          browse    [--node NODEID]     Print the address space below a node (default Objects), to find Galaxy attributes
                    [--depth 3] [--filter P101]
          read      --node NODEID ...   Read one or more nodes through OpcUaAdapter (value, type, quality, timestamp)
          roundtrip --node NODEID --value V [--within 5s]
                                        Write V, wait until it reads back, then restore the original value

        Connection: [--secure] [--auto-accept] [--username U --password P] [--pki DIR]
        Repeat --node to read several. The client certificate lives in %LOCALAPPDATA%\ScadaTest\pki by default.
        """);
    return 0;
}

using var report = new Report($"opcua-{a.Command}", args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

try
{
    switch (a.Command)
    {
        case "connect": await ConnectAsync(); break;
        case "browse": await BrowseAsync(); break;
        case "read": await ReadAsync(); break;
        case "roundtrip": await RoundTripAsync(); break;
        default: report.Fail($"Unknown command '{a.Command}'. Run with no arguments for help."); break;
    }
}
catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadSecurityChecksFailed || ex.StatusCode == StatusCodes.BadCertificateUntrusted)
{
    report.Exception(ex);
    report.Info("The server rejected our certificate. Trust 'ScadaTest' in the server's certificate store (it is usually in a 'rejected' folder), then run again.");
}
catch (Exception ex)
{
    report.Exception(ex);
}
return report.Finish();

// ---------------------------------------------------------------------------------------------------------------

Task<OpcUaRaw> RawAsync() => OpcUaRaw.ConnectAsync(a.Require("endpoint"), a.Flag("secure"), a.Flag("auto-accept"),
    a.Get("username"), a.Get("password"), a.Get("pki", OpcUaRaw.DefaultPkiPath), ct);

async Task<OpcUaAdapter> AdapterAsync()
{
    var settings = new Dictionary<string, string>
    {
        ["endpoint"] = a.Require("endpoint"),
        ["useSecurity"] = a.Flag("secure").ToString(),
        ["autoAcceptCertificates"] = a.Flag("auto-accept").ToString(),
        ["pkiPath"] = a.Get("pki", OpcUaRaw.DefaultPkiPath),
    };
    if (a.Get("username") is { } u) { settings["username"] = u; settings["password"] = a.Get("password", ""); }
    var adapter = new OpcUaAdapter();
    var sw = Stopwatch.StartNew();
    await adapter.ConnectAsync(new AdapterConfig("galaxy", settings, TagMap.Empty), ct);
    report.Pass($"OpcUaAdapter connected to {settings["endpoint"]} in {sw.ElapsedMilliseconds} ms (security {(a.Flag("secure") ? "on" : "off")})");
    return adapter;
}

async Task ConnectAsync()
{
    await using var raw = await RawAsync();
    report.Pass($"Connected: {raw.Describe()}");
    var ns = raw.Session.NamespaceUris;
    for (var i = 0; i < ns.Count; i++)
        report.Info($"ns={i}  {ns.GetString((uint)i)}");
    if (!a.Flag("secure"))
        report.Warn("Connected without security. Check that the real target also allows this, or rerun with --secure.");
}

async Task BrowseAsync()
{
    await using var raw = await RawAsync();
    report.Info($"Connected: {raw.Describe()}");
    var start = a.Get("node") is { } n ? NodeId.Parse(n) : ObjectIds.ObjectsFolder;
    var maxDepth = a.Int("depth", 3);
    var filter = a.Get("filter");
    var count = 0;

    async Task WalkAsync(NodeId node, int depth)
    {
        if (depth > maxDepth || ct.IsCancellationRequested) return;
        IReadOnlyList<ReferenceDescription> refs;
        try { refs = await raw.BrowseAsync(node, ct); }
        catch (Exception ex) { report.Warn($"{new string(' ', depth * 2)}{node}: {ex.Message}"); return; }
        foreach (var r in refs)
        {
            var id = raw.ToNodeId(r.NodeId);
            var line = $"{new string(' ', depth * 2)}{r.NodeClass,-8} {r.BrowseName.Name}  [{id}]";
            if (filter is null || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                report.Info(line);
                count++;
            }
            if (r.NodeClass == NodeClass.Object || (r.NodeClass == NodeClass.Variable && depth < maxDepth))
                await WalkAsync(id, depth + 1);
        }
    }

    await WalkAsync(start, 0);
    report.Check(count > 0, $"Browsed {count} nodes below {start} (depth {maxDepth}{(filter is null ? string.Empty : $", filter '{filter}'")}). Copy the NodeIds into tagmap.yaml.");
}

async Task ReadAsync()
{
    var nodes = a.GetAll("node");
    if (nodes.Count == 0) throw new ArgumentException("Give at least one --node.");
    await using var adapter = await AdapterAsync();
    foreach (var node in nodes)
    {
        var v = await report.StepAsync($"Read {node}", () => adapter.ReadAsync(node, ct));
        if (v is null) continue;
        report.Check(v.Quality == Quality.Good,
            $"{node} = {OpcUaRaw.Format(v.Value)} ({v.Value?.GetType().Name ?? "null"}), quality {v.Quality}, timestamp {v.Timestamp:O} (age {(DateTimeOffset.UtcNow - v.Timestamp).TotalSeconds:0.0}s)");
    }
}

async Task RoundTripAsync()
{
    var node = a.Require("node");
    var text = a.Require("value");
    var within = a.Time("within", "5s");
    await using var adapter = await AdapterAsync();

    var before = await adapter.ReadAsync(node, ct);
    report.Info($"Before: {node} = {OpcUaRaw.Format(before.Value)} ({before.Value?.GetType().Name}), {before.Quality}");

    var ok = await report.StepAsync($"Write {text} to {node}", () => adapter.WriteAsync(node, text, ct));
    if (!ok) return;
    var sw = Stopwatch.StartNew();
    TagValue? seen = null;
    while (sw.Elapsed < within)
    {
        seen = await adapter.ReadAsync(node, ct);
        if (ValueComparer.AreEqual(seen.Value, text)) break;
        await Task.Delay(100, ct);
    }
    report.Check(seen is not null && ValueComparer.AreEqual(seen.Value, text),
        $"Read back {OpcUaRaw.Format(seen?.Value)} after {sw.ElapsedMilliseconds} ms (wanted {text} within {within.TotalSeconds:0.#}s)");

    await report.StepAsync($"Restore {node}", () => adapter.WriteAsync(node, before.Value, ct));
    var after = await adapter.ReadAsync(node, ct);
    report.Check(ValueComparer.AreEqual(after.Value, before.Value), $"Restored: {node} = {OpcUaRaw.Format(after.Value)}");
}
