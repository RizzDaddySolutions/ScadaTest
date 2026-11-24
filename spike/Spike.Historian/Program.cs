using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using ScadaTest.Adapters.Historian;
using ScadaTest.Adapters.OpcUa;
using ScadaTest.Core;
using ScadaTest.Spike;

// Spike 3: AVEVA Historian over SQL.
SpikeArgs a;
try { a = new SpikeArgs(args); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

if (a.Command is "help" or "-h")
{
    Console.WriteLine("""
        spike-historian <command> [--connection "Server=...;Database=Runtime;Integrated Security=true;TrustServerCertificate=true"]

          check     --tag TAGNAME [--minutes 15]  Connect, find the tag, compare Live with History (Delta and Cyclic) and check time zones
          find      --like "P101%"               List Historian tags matching a pattern (find the historized attribute names)
          roundtrip --tag TAGNAME --endpoint opc.tcp://... --node NODEID --value V [--within 60s]
                                                 Write V through OPC UA and time how long until it appears in History
          alarms    [--source LT301 --condition Hi --query alarm.sql --minutes 60]
                                                 Find the alarm/event tables and, with --query, test the alarmEventsQuery

        Default connection: Server=localhost;Database=Runtime;Integrated Security=true;TrustServerCertificate=true
        OPC UA options for roundtrip: [--secure] [--auto-accept] [--username U --password P]
        """);
    return 0;
}

using var report = new Report($"historian-{a.Command}", args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;
var connectionString = a.Get("connection", "Server=localhost;Database=Runtime;Integrated Security=true;TrustServerCertificate=true");

try
{
    switch (a.Command)
    {
        case "check": await CheckAsync(); break;
        case "find": await FindAsync(); break;
        case "roundtrip": await RoundTripAsync(); break;
        case "alarms": await AlarmsAsync(); break;
        default: report.Fail($"Unknown command '{a.Command}'. Run with no arguments for help."); break;
    }
}
catch (Exception ex)
{
    report.Exception(ex);
}
return report.Finish();

// ---------------------------------------------------------------------------------------------------------------

async Task<HistorianAdapter> AdapterAsync()
{
    var adapter = new HistorianAdapter();
    var settings = new Dictionary<string, string> { ["connectionString"] = connectionString };
    if (a.Get("query") is { } q) settings["alarmEventsQuery"] = File.ReadAllText(q);
    await adapter.ConnectAsync(new AdapterConfig("historian", settings, TagMap.Empty), ct);
    report.Pass($"HistorianAdapter connected ({new SqlConnectionStringBuilder(connectionString).DataSource})");
    return adapter;
}

async Task<SqlConnection> OpenAsync()
{
    var c = new SqlConnection(connectionString);
    await c.OpenAsync(ct);
    return c;
}

async Task<List<object?[]>> QueryAsync(SqlConnection c, string sql, params (string Name, object Value)[] parameters)
{
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 };
    foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var rows = new List<object?[]>();
    while (await r.ReadAsync(ct))
    {
        var row = new object?[r.FieldCount];
        for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        rows.Add(row);
    }
    return rows;
}

async Task PrintTableAsync(SqlConnection c, string sql, int maxRows)
{
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 };
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
    report.Info("  columns: " + string.Join(", ", cols));
    var n = 0;
    while (n < maxRows && await r.ReadAsync(ct))
    {
        n++;
        report.Info("  " + string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "null" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
    }
    if (n == 0) report.Info("  (no rows)");
}

async Task CheckAsync()
{
    var tag = a.Require("tag");
    var minutes = a.Int("minutes", 15);
    await using var adapter = await AdapterAsync();
    await using var c = await OpenAsync();

    var version = await QueryAsync(c, "SELECT @@VERSION");
    report.Info("SQL Server: " + Convert.ToString(version[0][0], CultureInfo.InvariantCulture)?.Split('\n')[0].Trim());
    var serverClock = await QueryAsync(c, "SELECT SYSUTCDATETIME(), SYSDATETIMEOFFSET()");
    report.Info($"Server clock: UTC {serverClock[0][0]:O}, local {serverClock[0][1]}; this machine UTC {DateTime.UtcNow:O}");

    var tagRow = await QueryAsync(c, "SELECT TagName, Description FROM Runtime.dbo.Tag WHERE TagName = @t", ("@t", tag));
    if (!report.Check(tagRow.Count == 1, $"Tag '{tag}' exists in Runtime.dbo.Tag{(tagRow.Count == 1 ? $" ({tagRow[0][1]})" : ". Use 'find --like' to see the historized names.")}"))
        return;

    var live = await QueryAsync(c, "SELECT DateTime, Value, vValue, QualityDetail FROM Runtime.dbo.Live WHERE TagName = @t", ("@t", tag));
    if (live.Count == 1)
        report.Info($"Live: {live[0][1] ?? live[0][2]} at {live[0][0]:O} (server local time), QualityDetail {live[0][3]}");

    var to = DateTimeOffset.UtcNow;
    var from = to.AddMinutes(-minutes);
    var sw = Stopwatch.StartNew();
    var delta = await adapter.QueryAsync(tag, from, to, RetrievalMode.Delta, null, ct);
    report.Check(delta.Count > 0, $"Delta query returned {delta.Count} samples for the last {minutes} min in {sw.ElapsedMilliseconds} ms");
    foreach (var s in delta.TakeLast(5))
        report.Info($"  {s.Timestamp:O}  {OpcUaRaw.Format(s.Value)}  {s.Quality}");

    sw.Restart();
    var cyclic = await adapter.QueryAsync(tag, from, to, RetrievalMode.Cyclic, TimeSpan.FromMinutes(1), ct);
    report.Check(cyclic.Count >= minutes - 1, $"Cyclic 1-minute query returned {cyclic.Count} samples (expect about {minutes + 1}) in {sw.ElapsedMilliseconds} ms");

    if (delta.Count > 0)
    {
        var last = delta[^1].Timestamp;
        var ahead = last - DateTimeOffset.UtcNow;
        report.Check(ahead < TimeSpan.FromMinutes(1),
            $"Newest sample {last:O} is {(ahead > TimeSpan.Zero ? "ahead of" : "behind")} now by {Math.Abs(ahead.TotalMinutes):0.0} min. A whole-hour offset means the UTC handling is wrong.");
        var bad = delta.Count(s => s.Quality != Quality.Good);
        if (bad > 0) report.Warn($"{bad} of {delta.Count} samples are not Good quality (QualityDetail other than 192).");
    }
}

async Task FindAsync()
{
    await using var c = await OpenAsync();
    var rows = await QueryAsync(c, "SELECT TOP 200 TagName, Description FROM Runtime.dbo.Tag WHERE TagName LIKE @p ORDER BY TagName", ("@p", a.Require("like")));
    foreach (var r in rows) report.Info($"{r[0]}  {r[1]}");
    report.Check(rows.Count > 0, $"{rows.Count} tags match '{a.Get("like")}'");
}

async Task RoundTripAsync()
{
    var tag = a.Require("tag");
    var node = a.Require("node");
    var value = a.Require("value");
    var within = a.Time("within", "60s");
    await using var historian = await AdapterAsync();

    await using var opc = new OpcUaAdapter();
    var settings = new Dictionary<string, string>
    {
        ["endpoint"] = a.Require("endpoint"),
        ["useSecurity"] = a.Flag("secure").ToString(),
        ["autoAcceptCertificates"] = a.Flag("auto-accept").ToString(),
        ["pkiPath"] = a.Get("pki", OpcUaRaw.DefaultPkiPath),
    };
    if (a.Get("username") is { } u) { settings["username"] = u; settings["password"] = a.Get("password", ""); }
    await opc.ConnectAsync(new AdapterConfig("galaxy", settings, TagMap.Empty), ct);
    var original = await opc.ReadAsync(node, ct);
    report.Pass($"OPC UA connected; {node} is {OpcUaRaw.Format(original.Value)}");

    var writtenAt = DateTimeOffset.UtcNow;
    await opc.WriteAsync(node, value, ct);
    report.Info($"Wrote {value} to {node} at {writtenAt:O}");
    try
    {
        var sw = Stopwatch.StartNew();
        HistorySample? found = null;
        while (sw.Elapsed < within && found is null)
        {
            await Task.Delay(1000, ct);
            var samples = await historian.QueryAsync(tag, writtenAt.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5), RetrievalMode.Delta, null, ct);
            found = samples.FirstOrDefault(s => ValueComparer.AreEqual(s.Value, value, 0.0001));
        }
        report.Check(found is not null, found is null
            ? $"{value} did not appear in History for '{tag}' within {within.TotalSeconds:0}s. Is the attribute historized, with a deadband that lets this change through?"
            : $"{value} appeared in History after {sw.Elapsed.TotalSeconds:0.0}s, timestamped {found.Timestamp:O} ({(found.Timestamp - writtenAt).TotalMilliseconds:0} ms after the write). Use at least {Math.Ceiling(sw.Elapsed.TotalSeconds * 2):0}s as the history wait.");
    }
    finally
    {
        await opc.WriteAsync(node, original.Value, CancellationToken.None);
        report.Info($"Restored {node} to {OpcUaRaw.Format(original.Value)}");
    }
}

async Task AlarmsAsync()
{
    await using var c = await OpenAsync();
    var dbs = await QueryAsync(c, "SELECT name FROM sys.databases WHERE name NOT IN ('master','tempdb','model','msdb') ORDER BY name");
    report.Info("Databases: " + string.Join(", ", dbs.Select(r => r[0])));

    var candidates = new List<string>();
    foreach (var db in dbs.Select(r => (string)r[0]!))
    {
        List<object?[]> objects;
        try
        {
            objects = await QueryAsync(c, $"""
                SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM [{db.Replace("]", "]]", StringComparison.Ordinal)}].INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME LIKE '%Alarm%' OR TABLE_NAME LIKE '%Event%'
                ORDER BY TABLE_NAME
                """);
        }
        catch (SqlException ex) { report.Warn($"{db}: {ex.Message}"); continue; }
        foreach (var o in objects)
        {
            var name = $"[{db}].[{o[0]}].[{o[1]}]";
            candidates.Add(name);
            report.Info($"{((string)o[2]! == "VIEW" ? "view " : "table")} {name}");
        }
    }
    report.Check(candidates.Count > 0, $"Found {candidates.Count} alarm/event tables or views");

    foreach (var name in candidates.Where(n => n.Contains("Runtime", StringComparison.OrdinalIgnoreCase) || n.Contains("ALM", StringComparison.OrdinalIgnoreCase)).Take(a.Int("peek", 6)))
    {
        report.Info($"Sample of {name}:");
        try { await PrintTableAsync(c, $"SELECT TOP 3 * FROM {name}", 3); }
        catch (SqlException ex) { report.Warn($"  {ex.Message}"); }
    }

    if (a.Get("query") is not null)
    {
        await using var adapter = await AdapterAsync();
        var alarm = new AlarmRef(a.Require("source"), a.Require("condition"));
        var to = DateTimeOffset.UtcNow;
        var events = await adapter.QueryAlarmEventsAsync(alarm, to.AddMinutes(-a.Int("minutes", 60)), to, ct);
        foreach (var e in events) report.Info($"  {e.Timestamp:O}  {e.Kind}");
        var kinds = events.Select(e => e.Kind).Distinct().ToList();
        report.Check(kinds.Count == 3, $"alarmEventsQuery returned {events.Count} events for {alarm} with kinds {string.Join(", ", kinds)} (want Raise, Ack and ReturnToNormal)");
    }
    else
    {
        report.Info("Next: write a query returning EventTime (UTC) and EventKind (raise/ack/rtn) for @source, @condition, @from, @to, save it as alarm.sql and rerun with --query alarm.sql --source LT301 --condition Hi.");
    }
}
